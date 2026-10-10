"""Local ColBERT discovery over source-bound, observer-projected originals.

The caller supplies the exact eligible source/view hashes for each search. This
index is disposable and has no authority to grant knowledge or update memories.
No downloads, provider calls, listeners or eager model loads occur here.
"""
from __future__ import annotations

import hashlib
import json
import math
import sqlite3
import string
import threading
import time
import uuid
from collections import OrderedDict
from contextlib import contextmanager, closing
from pathlib import Path

import numpy as np

SCHEMA = "reign-colbert-source-v1"
MODEL = "colbert-ir/colbertv2.0"
REVISION = "c1e84128e85ef755c096a95bdb06b47793b13acf"
MODEL_HASH = "bfe81fa313c4c2a12319ec9ef0cdf1c995daad051e9c5a1fcabcb90feb3c8286"
FILES = {
    "colbert-model.onnx": MODEL_HASH,
    "colbert-tokenizer.json": "5fd1c882abbd30517dced455a2c9768945ec726b96727927e4959348d9de550b",
    "colbert-config.json": "cbdcc01dc7772fd0e5e1d85d5de695faf8c4935ae3c7ea3fdb6c24397b284f3c",
    "colbert-artifact.metadata": "0ddc5a54234cff6d13bc9411250a5479d9d96f3ffbace76d8a1884144377e434",
}
MAX_SOURCES = 50000
MAX_TEXT_CHARS = 32000
MAX_CHUNKS = 128
MAXSIM_QUERY_TILE = 32
SEARCH_SCORE_BUDGET_SECONDS = 2.0


def maxsim_score(document, query):
    # Large narrow GEMMs can cross a BLAS parallelism threshold and cost far
    # more than small ones. Bound each matrix without changing process-wide
    # thread settings or omitting any query/document token. Sum only once, in
    # original query order; this remains the same complete-window MaxSim.
    maxima = np.empty(len(query), dtype=np.float32)
    for start in range(0, len(query), MAXSIM_QUERY_TILE):
        maxima[start:start + MAXSIM_QUERY_TILE] = (
            document @ query[start:start + MAXSIM_QUERY_TILE].T).max(axis=0)
    return float(maxima.sum())


def text_hash(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def _hash(value):
    # Reign's canonical source fence uses uppercase SHA-256; text digests use
    # lowercase. Preserve the exact supplied fence while accepting either form.
    return isinstance(value, str) and len(value) == 64 and all(c in "0123456789abcdefABCDEF" for c in value)


def source_key(source):
    if not isinstance(source, dict):
        raise ValueError("invalid_source")
    sid, view = source.get("sourceId"), source.get("view")
    if not isinstance(sid, str) or not 0 < len(sid) <= 512 or view not in ("self", "public"):
        raise ValueError("invalid_source_identity")
    if not _hash(source.get("sourceHash")) or not _hash(source.get("textHash")):
        raise ValueError("invalid_source_hash")
    return sid, view, source["sourceHash"], source["textHash"]


class ColbertEncoder:
    """Published CPU ONNX graph, official marker/mask/MaxSim conventions."""
    def __init__(self, directory):
        from tokenizers import Tokenizer
        import onnxruntime as ort
        directory = Path(directory)
        for name, expected in FILES.items():
            path = directory / name
            if not path.is_file():
                raise FileNotFoundError("colbert_model_not_installed")
            digest = hashlib.sha256()
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
            if digest.hexdigest() != expected:
                raise ValueError("colbert_model_hash_mismatch")
        self.tokenizer = Tokenizer.from_file(str(directory / "colbert-tokenizer.json"))
        self.tokenizer.no_truncation()
        self.tokenizer.no_padding()
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        self.session = ort.InferenceSession(str(directory / "colbert-model.onnx"),
                                           sess_options=options, providers=["CPUExecutionProvider"])
        self.punctuation = {self.tokenizer.encode(p, add_special_tokens=False).ids[0] for p in string.punctuation}

    def windows(self, text, query=False):
        encoded = self.tokenizer.encode(text, add_special_tokens=False)
        if not encoded.ids:
            raise ValueError("empty_tokens")
        if query:
            if len(encoded.ids) > 497:
                raise ValueError("query_exceeds_token_limit")
            return [(encoded.ids, 0, len(text))]
        windows = [(encoded.ids[i:i + 177], encoded.offsets[i][0], encoded.offsets[min(i + 176, len(encoded.ids) - 1)][1])
                   for i in range(0, len(encoded.ids), 153)]
        if len(windows) > MAX_CHUNKS:
            raise ValueError("source_exceeds_chunk_limit")
        return windows

    def encode_window(self, tokens, query=False):
        ids = [101, 1 if query else 2] + tokens + [102]
        mask = [1] * len(ids)
        if query:
            padding = max(0, 32 - len(ids))
            ids += [103] * padding
            mask += [0] * padding
        raw = self.session.run(None, {"input_ids": np.asarray([ids], dtype=np.int64),
                                     "attention_mask": np.asarray([mask], dtype=np.int64)})[0][0]
        normalized = raw / np.maximum(np.linalg.norm(raw, axis=1, keepdims=True), 1e-12)
        if not query:
            normalized = normalized[[token not in self.punctuation and token != 0 for token in ids]]
        if normalized.ndim != 2 or normalized.shape[1] != 128 or not np.isfinite(normalized).all():
            raise ValueError("invalid_model_vectors")
        return normalized.astype(np.float32)


class LocalInferenceGate:
    """Serialize this model, independently of unrelated vector-store I/O."""
    def __init__(self, foreground_timeout=1.0):
        self.condition = threading.Condition()
        self.active = False
        self.foreground_waiters = 0
        self.foreground_timeout = foreground_timeout

    @contextmanager
    def __call__(self, foreground):
        deadline = time.monotonic() + self.foreground_timeout
        with self.condition:
            if foreground:
                self.foreground_waiters += 1
                self.condition.notify_all()
            try:
                while self.active or (not foreground and self.foreground_waiters):
                    remaining = deadline - time.monotonic() if foreground else None
                    if foreground and remaining <= 0:
                        raise TimeoutError("colbert_inference_busy")
                    self.condition.wait(remaining)
                self.active = True
            finally:
                if foreground:
                    self.foreground_waiters -= 1
                    self.condition.notify_all()
        try:
            yield
        finally:
            with self.condition:
                self.active = False
                self.condition.notify_all()

    def status(self):
        with self.condition:
            return {"scope": "colbert_only", "active": self.active,
                    "foregroundWaiters": self.foreground_waiters,
                    "foregroundWaitLimitMs": int(self.foreground_timeout * 1000)}


class LateInteractionIndex:
    def __init__(self, data_dir, model_dir, operation_gate=None, encoder_factory=None):
        self.path = Path(data_dir) / "vectors" / "late_interaction.sqlite"
        self.model_dir = Path(model_dir)
        self.gate = operation_gate if operation_gate is not None else LocalInferenceGate()
        self.factory = encoder_factory or (lambda: ColbertEncoder(self.model_dir))
        self.encoder = None
        self.load_lock = threading.Lock()
        self.index_lock = threading.Lock()
        self.query_cache = OrderedDict()
        self.last_status = "not_loaded"
        self.last_timing = {}

    def status(self):
        generation = ""
        if self.path.is_file():
            try:
                with closing(self._connection()) as db:
                    generation = db.execute("SELECT value FROM metadata WHERE key='generation'").fetchone()[0]
            except sqlite3.Error:
                pass
        return {"schema": SCHEMA, "model": MODEL, "revision": REVISION, "modelHash": MODEL_HASH,
                "indexGeneration": generation,
                "modelLoaded": self.encoder is not None, "indexExists": self.path.is_file(),
                "modelInstalled": all((self.model_dir / name).is_file() for name in FILES),
                "inferenceGate": self.gate.status() if isinstance(self.gate, LocalInferenceGate) else None,
                "status": self.last_status, "timing": dict(self.last_timing), "authority": "retrieval_candidates_only"}

    def _encoder(self):
        with self.load_lock:
            if self.encoder is None:
                self.encoder = self.factory()
        return self.encoder

    def _connection(self, create=False):
        if create:
            self.path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self.path if create else self.path.as_uri() + "?mode=ro", uri=not create, timeout=2)
        if create:
            connection.execute("PRAGMA journal_mode=WAL")
            connection.execute("CREATE TABLE IF NOT EXISTS metadata(key TEXT PRIMARY KEY,value TEXT)")
            connection.execute("INSERT OR IGNORE INTO metadata VALUES('generation',?)", (uuid.uuid4().hex,))
            connection.execute("""CREATE TABLE IF NOT EXISTS sources(
                campaign TEXT, source_id TEXT, view TEXT, source_hash TEXT, text_hash TEXT,
                model TEXT, chunk_count INTEGER, PRIMARY KEY(campaign,source_id,view))""")
            connection.execute("""CREATE TABLE IF NOT EXISTS chunks(
                campaign TEXT, source_id TEXT, view TEXT, ordinal INTEGER, start INTEGER, end INTEGER,
                count INTEGER, vectors BLOB, PRIMARY KEY(campaign,source_id,view,ordinal))""")
            connection.commit()
        return connection

    @staticmethod
    def _campaign(body):
        campaign = body.get("campaignId")
        if not isinstance(campaign, str) or not 0 < len(campaign) <= 256:
            raise ValueError("invalid_campaign")
        if body.get("schema") != SCHEMA or body.get("modelHash") != MODEL_HASH:
            raise ValueError("model_contract_mismatch")
        return campaign

    def _result(self, ok, status, **values):
        self.last_status = status
        return {"ok": ok, "status": status, "schema": SCHEMA, "modelHash": MODEL_HASH, **values}

    def index(self, body):
        started = time.perf_counter()
        if not self.index_lock.acquire(blocking=False):
            return self._result(False, "index_busy", retryable=True)
        try:
            campaign = self._campaign(body)
            sources = body.get("sources")
            if not isinstance(sources, list) or not 1 <= len(sources) <= 8:
                raise ValueError("invalid_source_batch")
            # Validate the entire batch before creating or altering any cache.
            keys = [source_key(s) for s in sources]
            if len({key[:2] for key in keys}) != len(keys):
                raise ValueError("duplicate_source_view")
            for source in sources:
                text = source.get("text")
                if not isinstance(text, str) or not 0 < len(text) <= MAX_TEXT_CHARS or text_hash(text) != source["textHash"]:
                    raise ValueError("source_text_hash_mismatch")
            published = []
            with closing(self._connection(create=True)) as db:
                for source, key in zip(sources, keys):
                    sid, view, source_hash, digest = key
                    cached = db.execute("SELECT source_hash,text_hash,model,chunk_count FROM sources WHERE campaign=? AND source_id=? AND view=?",
                                        (campaign, sid, view)).fetchone()
                    model_key = SCHEMA + ":" + MODEL_HASH
                    if cached and cached[:3] == (source_hash, digest, model_key):
                        published.append({**source, "text": None, "chunks": cached[3], "cached": True})
                        continue
                    with self.gate(False):
                        encoder = self._encoder()
                        windows = encoder.windows(source["text"])
                    chunks = []
                    for tokens, start, end in windows:
                        # Release this model's priority gate after each <=180 token chunk.
                        with self.gate(False):
                            vectors = encoder.encode_window(tokens)
                        chunks.append((campaign, sid, view, len(chunks), start, end, len(vectors),
                                       vectors.astype("<f2").tobytes()))
                    if not chunks or windows[-1][2] < len(source["text"].rstrip()):
                        raise ValueError("incomplete_source_windows")
                    # Publish all windows atomically; a crash cannot leave a prefix.
                    with db:
                        db.execute("DELETE FROM chunks WHERE campaign=? AND source_id=? AND view=?", (campaign, sid, view))
                        db.executemany("INSERT INTO chunks VALUES(?,?,?,?,?,?,?,?)", chunks)
                        db.execute("INSERT OR REPLACE INTO sources VALUES(?,?,?,?,?,?,?)",
                                   (campaign, sid, view, source_hash, digest, model_key, len(chunks)))
                    published.append({"sourceId": sid, "view": view, "sourceHash": source_hash,
                                      "textHash": digest, "chunks": len(chunks), "cached": False})
            self.last_timing = {"indexMs": round((time.perf_counter() - started) * 1000)}
            return self._result(True, "indexed", sources=published, timing=dict(self.last_timing), indexGeneration=self.status()["indexGeneration"])
        except Exception as exc:
            retryable = isinstance(exc, TimeoutError) or (
                isinstance(exc, sqlite3.OperationalError) and getattr(exc, "sqlite_errorcode", None) in
                (sqlite3.SQLITE_BUSY, sqlite3.SQLITE_LOCKED))
            return self._result(False, "index_unavailable", errorType=type(exc).__name__,
                                reason=str(exc)[:160], retryable=retryable)
        finally:
            self.index_lock.release()

    def search(self, body):
        started = time.perf_counter()
        score_started = None
        timing = {"eligibleLookupMs": 0, "queryMs": 0, "scoreMs": 0,
                  "queryTokens": 0, "scoredChunks": 0, "scoredSources": 0,
                  "scoreBudgetMs": int(SEARCH_SCORE_BUDGET_SECONDS * 1000), "maxSimQueryTile": MAXSIM_QUERY_TILE}
        try:
            campaign = self._campaign(body)
            query, eligible = body.get("query"), body.get("eligible")
            if not isinstance(query, str) or not 0 < len(query) <= 12000:
                raise ValueError("invalid_query")
            if not isinstance(eligible, list) or len(eligible) > MAX_SOURCES:
                raise ValueError("eligibility_limit_exceeded")
            keys = [source_key(s) for s in eligible]
            if len({key[:2] for key in keys}) != len(keys):
                raise ValueError("duplicate_eligible_view")
            if not eligible:
                return self._result(True, "empty_eligibility", results=[], eligibleCount=0, indexedCount=0)
            if not self.path.is_file():
                return self._result(False, "index_not_ready", results=[], eligibleCount=len(eligible), indexedCount=0)
            limit = body.get("limit", 24)
            if isinstance(limit, bool) or not isinstance(limit, int) or not 1 <= limit <= 128:
                raise ValueError("invalid_result_limit")
            model_key = SCHEMA + ":" + MODEL_HASH
            with closing(self._connection()) as db:
                # SQL joins exact caller-approved identities/hashes BEFORE loading
                # any vectors. No global top-k followed by privacy filtering.
                # SQLite may reorder an ordinary JOIN into a campaign-wide scan
                # that reparses the allowlist once per source. Keep eligible IDs
                # outermost so the full (campaign, source, view) index is used.
                join = """FROM json_each(?) e CROSS JOIN sources s ON s.campaign=?
                    AND s.source_id=json_extract(e.value,'$.sourceId') AND s.view=json_extract(e.value,'$.view')
                    AND s.source_hash=json_extract(e.value,'$.sourceHash') AND s.text_hash=json_extract(e.value,'$.textHash')
                    AND s.model=?"""
                args = (json.dumps(eligible, separators=(",", ":")), campaign, model_key)
                lookup_started = time.perf_counter()
                indexed = db.execute("SELECT COUNT(*) " + join, args).fetchone()[0]
                timing["eligibleLookupMs"] = round((time.perf_counter() - lookup_started) * 1000, 3)
                if indexed == 0:
                    return self._result(False, "index_not_ready", results=[], eligibleCount=len(eligible), indexedCount=0)
                query_started = time.perf_counter()
                with self.gate(True):
                    q = self.query_cache.get(query)
                    if q is None:
                        encoder = self._encoder()
                        q = encoder.encode_window(encoder.windows(query, query=True)[0][0], query=True)
                        self.query_cache[query] = q
                        while len(self.query_cache) > 128:
                            self.query_cache.popitem(last=False)
                    else:
                        self.query_cache.move_to_end(query)
                query_ms = (time.perf_counter() - query_started) * 1000
                if q.ndim != 2 or not 1 <= len(q) <= 500 or q.shape[1] != 128 or not np.isfinite(q).all():
                    raise ValueError("invalid_query_vectors")
                timing.update(queryMs=round(query_ms, 3), queryTokens=len(q))
                score_started = time.perf_counter()
                ranked = {}
                coverage = {}
                stream = db.execute("SELECT s.source_id,s.view,s.source_hash,s.text_hash,s.chunk_count,c.ordinal,c.count,c.vectors " + join
                                    + " JOIN chunks c ON c.campaign=s.campaign AND c.source_id=s.source_id AND c.view=s.view", args)
                for sid, view, source_hash, digest, expected_chunks, ordinal, count, blob in stream:
                    if not 1 <= expected_chunks <= MAX_CHUNKS or not 0 <= ordinal < expected_chunks:
                        raise ValueError("corrupt_index_chunk_coverage")
                    if count < 1 or count > 180 or len(blob) != count * 128 * 2:
                        raise ValueError("corrupt_index_chunk")
                    d = np.frombuffer(blob, dtype="<f2").astype(np.float32).reshape(count, 128)
                    if not np.isfinite(d).all():
                        raise ValueError("nonfinite_index_vector")
                    score = maxsim_score(d, q)
                    if not math.isfinite(score):
                        raise ValueError("nonfinite_score")
                    identity = (sid, view)
                    coverage.setdefault(identity, [expected_chunks, 0])[1] += 1
                    timing["scoredChunks"] += 1
                    if identity not in ranked or score > ranked[identity]["score"]:
                        ranked[identity] = {"sourceId": sid, "view": view, "sourceHash": source_hash, "textHash": digest, "score": score}
                    if (time.perf_counter() - score_started) > SEARCH_SCORE_BUDGET_SECONDS:
                        timing.update(scoreMs=round((time.perf_counter() - score_started) * 1000, 3),
                                      totalMs=round((time.perf_counter() - started) * 1000, 3), scoredSources=len(ranked))
                        self.last_timing = timing
                        return self._result(False, "search_budget_exceeded", results=[], eligibleCount=len(eligible), indexedCount=indexed,
                                            timing=dict(timing))
                # Corrupt/missing chunks cannot be counted as successful coverage.
                if len(ranked) != indexed or any(expected != actual for expected, actual in coverage.values()):
                    raise ValueError("incomplete_index_chunks")
            timing.update(scoreMs=round((time.perf_counter() - score_started) * 1000, 3),
                          totalMs=round((time.perf_counter() - started) * 1000, 3), scoredSources=len(ranked))
            self.last_timing = timing
            return self._result(True, "ready" if indexed == len(eligible) else "partial", eligibleCount=len(eligible), indexedCount=indexed,
                                results=sorted(ranked.values(), key=lambda r: (-r["score"], r["sourceId"], r["view"]))[:limit],
                                timing=dict(self.last_timing), authority="retrieval_candidates_only")
        except Exception as exc:
            if score_started is not None:
                timing["scoreMs"] = round((time.perf_counter() - score_started) * 1000, 3)
            timing["totalMs"] = round((time.perf_counter() - started) * 1000, 3)
            self.last_timing = timing
            return self._result(False, "search_unavailable", results=[], errorType=type(exc).__name__, reason=str(exc)[:160], timing=dict(timing))
