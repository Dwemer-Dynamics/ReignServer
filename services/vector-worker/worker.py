#!/usr/bin/env python3
"""Managed local embedding and vector-search worker for Bannerlord Reign."""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import re
import threading
import time
import sys
import uuid
from collections import OrderedDict, deque
from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlparse

import numpy as np


MODEL_NAME = "BAAI/bge-small-en-v1.5"
MODEL_DIMENSIONS = 384
MODEL_VERSION = "bge-small-en-v1.5-384-precision-v2"
CHUNK_VERSION = "token-window-v1"
STOP_WORDS = {
    "about", "after", "again", "against", "also", "among", "because", "been", "before", "being",
    "between", "could", "does", "from", "have", "into", "just", "more", "most", "other", "over",
    "same", "should", "some", "such", "than", "that", "their", "them", "then", "there", "these",
    "they", "this", "those", "through", "under", "very", "what", "when", "where", "which", "while",
    "with", "would", "your", "ours", "were", "will", "said", "says", "saying", "the", "and", "for",
    "but", "not", "you", "are", "was", "has", "had", "his", "her", "she", "him", "our", "who",
}


class WorkerState:
    def __init__(self, data_dir: Path) -> None:
        self.data_dir = data_dir
        configured_model = os.environ.get("REIGN_VECTOR_MODEL_DIR", "").strip()
        self.bundled_model = bool(configured_model)
        self.model_dir = Path(configured_model).resolve() if configured_model else data_dir / "models" / "embeddings"
        self.local_vector_dir = data_dir / "vectors" / "local"
        self.model = None
        self.tokenizer = None
        self.model_lock = threading.RLock()
        self.clients: dict[str, Any] = {}
        self.client_lock = threading.RLock()
        # Embedded Qdrant and FastEmbed both mutate process-local state. The
        # HTTP server is threaded, so serialize those operations to prevent
        # concurrent search/upsert calls from corrupting a local collection.
        self.vector_lock = threading.RLock()
        # The worker is deliberately threaded so health checks remain responsive,
        # but model inference and embedded Qdrant operations are finite shared
        # resources. Live retrieval must not queue behind a stream of indexing or
        # maintenance requests. A foreground waiter therefore takes precedence at
        # the next operation boundary.
        self.operation_condition = threading.Condition()
        self.operation_active = False
        self.foreground_waiters = 0
        self.foreground_active = 0
        self.background_active = 0
        self.recent_operations: deque[dict[str, Any]] = deque(maxlen=32)
        self.started = time.time()
        self.last_error = ""
        self.last_operation = ""
        self.last_duration_ms = 0
        self.request_count = 0
        self.embedding_cache: OrderedDict[str, list[float]] = OrderedDict()
        self.embedding_cache_limit = max(128, int(os.environ.get("REIGN_EMBEDDING_CACHE_SIZE", "2048")))
        # Embedded Qdrant performs filtered searches by scanning its SQLite-backed
        # local collection. Once campaign history becomes large that can add
        # several seconds to every live prompt. Keep an authoritative in-memory
        # cosine index for the local provider while continuing to persist every
        # point through Qdrant. External Qdrant retains its native indexed search.
        self.local_indexes: dict[str, dict[str, Any]] = {}
        self.local_index_building: set[str] = set()
        self.local_index_last_error = ""
        self.local_index_last_build_ms = 0

    @contextmanager
    def operation_gate(self, foreground: bool):
        queued = time.perf_counter()
        with self.operation_condition:
            if foreground:
                self.foreground_waiters += 1
                try:
                    while self.operation_active:
                        self.operation_condition.wait()
                finally:
                    self.foreground_waiters -= 1
                self.foreground_active += 1
            else:
                while self.operation_active or self.foreground_waiters > 0:
                    self.operation_condition.wait()
                self.background_active += 1
            self.operation_active = True
        timing = {"queueWaitMs": int((time.perf_counter() - queued) * 1000)}
        try:
            yield timing
        finally:
            with self.operation_condition:
                self.operation_active = False
                if foreground:
                    self.foreground_active = max(0, self.foreground_active - 1)
                else:
                    self.background_active = max(0, self.background_active - 1)
                self.operation_condition.notify_all()

    def record_operation(self, operation: str, foreground: bool, started: float, timing: dict[str, Any]) -> dict[str, Any]:
        result = dict(timing)
        result["operation"] = operation
        result["priority"] = "foreground" if foreground else "background"
        result["durationMs"] = int((time.perf_counter() - started) * 1000)
        result["completedUtc"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
        self.recent_operations.append(result)
        return result

    def get_model(self):
        with self.model_lock:
            if self.model is None:
                if self.bundled_model:
                    required = ("model_optimized.onnx", "tokenizer.json", "config.json", "tokenizer_config.json", "special_tokens_map.json")
                    if any(not (self.model_dir / name).is_file() for name in required):
                        raise FileNotFoundError("The bundled embedding model is incomplete. Run ReignServer setup to repair it.")
                else:
                    self.model_dir.mkdir(parents=True, exist_ok=True)
                from fastembed import TextEmbedding

                self.last_operation = "loading_model"
                self.model = TextEmbedding(
                    model_name=MODEL_NAME,
                    cache_dir=str(self.model_dir),
                    threads=max(1, min(int(os.environ.get("REIGN_VECTOR_THREADS", "4")), os.cpu_count() or 1)),
                    lazy_load=True,
                    specific_model_path=str(self.model_dir) if self.bundled_model else None,
                    local_files_only=self.bundled_model,
                )
                list(self.model.embed(["Bannerlord Reign semantic memory initialization."], batch_size=1))
                self.last_operation = "model_ready"
                self.last_error = ""
            return self.model

    def embed(self, texts: list[str]) -> list[list[float]]:
        clean = [str(text or "") for text in texts]
        started = time.perf_counter()
        try:
            with self.model_lock:
                keys = [hashlib.sha256(text.encode("utf-8")).hexdigest() for text in clean]
                vectors: list[list[float] | None] = [None] * len(clean)
                missing_texts: list[str] = []
                missing_keys: list[str] = []
                missing_indexes: list[int] = []
                for index, key in enumerate(keys):
                    cached = self.embedding_cache.get(key)
                    if cached is None:
                        missing_texts.append(clean[index]); missing_keys.append(key); missing_indexes.append(index)
                    else:
                        self.embedding_cache.move_to_end(key); vectors[index] = cached
                if missing_texts:
                    generated = [vector.tolist() for vector in self.get_model().embed(
                        missing_texts, batch_size=min(32, max(1, len(missing_texts))))]
                    for index, key, vector in zip(missing_indexes, missing_keys, generated):
                        vectors[index] = vector; self.embedding_cache[key] = vector
                    while len(self.embedding_cache) > self.embedding_cache_limit:
                        self.embedding_cache.popitem(last=False)
                vectors = [vector for vector in vectors if vector is not None]
            for vector in vectors:
                if len(vector) != MODEL_DIMENSIONS:
                    raise ValueError(f"Embedding dimension {len(vector)} did not match {MODEL_DIMENSIONS}.")
            self.last_duration_ms = int((time.perf_counter() - started) * 1000)
            self.last_operation = "embed"
            self.last_error = ""
            return vectors
        except Exception as exc:
            self.last_error = str(exc)
            self.last_operation = "embed_failed"
            raise

    def get_tokenizer(self):
        with self.model_lock:
            if self.tokenizer is None:
                self.get_model()
                from tokenizers import Tokenizer
                candidates = [self.model_dir / "tokenizer.json"] if self.bundled_model else [p for p in self.model_dir.rglob("tokenizer.json") if "bge-small-en-v1.5" in str(p)]
                if len(candidates) != 1:
                    raise RuntimeError("Embedding tokenizer is unavailable; refuse silent model-window truncation.")
                self.tokenizer = Tokenizer.from_file(str(candidates[0]))
                self.tokenizer.no_truncation()
                self.tokenizer.no_padding()
            return self.tokenizer

    def remove_documents(self, client, collection: str, ids: list[str], local: bool) -> None:
        from qdrant_client import models
        if not ids or not client.collection_exists(collection):
            return
        client.delete(collection_name=collection, points_selector=models.FilterSelector(filter=models.Filter(must=[
            models.FieldCondition(key="documentId", match=models.MatchAny(any=ids))])), wait=True)
        client.delete(collection_name=collection, points_selector=models.PointIdsList(points=ids), wait=True)
        if local:
            index = self.local_indexes.get(collection)
            child_ids = [] if index is None else [key for key, (_, payload) in index["points"].items() if payload.get("documentId") in ids]
            self.delete_local_index(collection, ids + child_ids, False)

    def get_client(self, provider: str, qdrant_url: str, api_key: str):
        from qdrant_client import QdrantClient

        provider = (provider or "local").strip().lower()
        key = provider + "|" + qdrant_url.strip() + "|" + hashlib.sha256(api_key.encode("utf-8")).hexdigest()[:12]
        with self.client_lock:
            if key not in self.clients:
                if provider == "qdrant":
                    if not qdrant_url.strip():
                        raise ValueError("qdrantUrl is required for the qdrant provider.")
                    self.clients[key] = QdrantClient(url=qdrant_url.strip(), api_key=api_key or None, timeout=10)
                else:
                    self.local_vector_dir.mkdir(parents=True, exist_ok=True)
                    self.clients[key] = QdrantClient(path=str(self.local_vector_dir))
            return self.clients[key]

    def ensure_collection(self, client, collection: str, external: bool) -> None:
        from qdrant_client import models

        if not client.collection_exists(collection):
            client.create_collection(
                collection_name=collection,
                vectors_config=models.VectorParams(size=MODEL_DIMENSIONS, distance=models.Distance.COSINE),
            )
        if external:
            for field in ("campaignId", "timelineId", "sourceType", "memoryLane", "status", "ownerId", "knownBy", "hiddenFrom", "documentId", "restoreGeneration"):
                try:
                    client.create_payload_index(collection, field, models.PayloadSchemaType.KEYWORD, wait=True)
                except Exception:
                    pass

    def load_local_index(self, client, collection: str) -> dict[str, Any]:
        cached = self.local_indexes.get(collection)
        if cached is not None:
            return cached

        started = time.perf_counter()
        points: OrderedDict[str, tuple[np.ndarray, dict[str, Any]]] = OrderedDict()
        if client.collection_exists(collection):
            offset = None
            while True:
                records, offset = client.scroll(
                    collection_name=collection,
                    limit=1024,
                    offset=offset,
                    with_payload=True,
                    with_vectors=True,
                )
                for record in records:
                    raw_vector = record.vector
                    if isinstance(raw_vector, dict):
                        raw_vector = next(iter(raw_vector.values()), [])
                    vector = np.asarray(raw_vector or [], dtype=np.float32)
                    if vector.size != MODEL_DIMENSIONS:
                        continue
                    points[str(record.id)] = (vector, dict(record.payload or {}))
                if offset is None:
                    break
        index = {
            "points": points,
            "ids": [],
            "payloads": [],
            "matrix": None,
            "loadedUtc": time.time(),
        }
        self.local_indexes[collection] = index
        self.local_index_last_build_ms = int((time.perf_counter() - started) * 1000)
        self.local_index_last_error = ""
        return index

    def rebuild_local_matrix(self, index: dict[str, Any]) -> None:
        points = index["points"]
        ids = list(points.keys())
        payloads = [points[point_id][1] for point_id in ids]
        if ids:
            matrix = np.vstack([points[point_id][0] for point_id in ids]).astype(np.float32, copy=False)
            norms = np.linalg.norm(matrix, axis=1, keepdims=True)
            matrix = matrix / np.maximum(norms, np.float32(1e-12))
        else:
            matrix = np.empty((0, MODEL_DIMENSIONS), dtype=np.float32)
        index["ids"] = ids
        index["payloads"] = payloads
        index["matrix"] = matrix

    def update_local_index(self, collection: str, documents: list[dict[str, Any]],
                           vectors: list[list[float]]) -> None:
        index = self.local_indexes.get(collection)
        if index is None:
            return
        for document, vector in zip(documents, vectors):
            point_id = str(document.get("id") or "")
            if not point_id:
                continue
            index["points"][point_id] = (
                np.asarray(vector, dtype=np.float32),
                dict(document.get("payload") or {}),
            )
        index["matrix"] = None

    def delete_local_index(self, collection: str, ids: list[str], delete_collection: bool) -> None:
        if delete_collection:
            self.local_indexes.pop(collection, None)
            return
        index = self.local_indexes.get(collection)
        if index is None:
            return
        for point_id in ids:
            index["points"].pop(str(point_id), None)
        index["matrix"] = None

    def warm_default_local_index(self) -> None:
        collection = "reign_memory_bge_small_en_v1_5_v1"
        try:
            with self.operation_gate(False):
                self.embed(["Bannerlord Reign foreground semantic query warmup."])
                with self.vector_lock:
                    client = self.get_client("local", "", "")
                    self.load_local_index(client, collection)
            self.last_operation = "local_index_ready"
        except Exception as exc:
            self.local_index_last_error = str(exc)
            self.last_error = str(exc)


STATE: WorkerState
SERVER: ThreadingHTTPServer | None = None


def limit_text(value: Any, maximum: int) -> str:
    text = str(value or "").strip()
    return text if len(text) <= maximum else text[:maximum]


def collection_name(body: dict[str, Any]) -> str:
    raw = str(body.get("collection") or "reign_memory_bge_small_en_v1_5_v1")
    return re.sub(r"[^a-zA-Z0-9_-]+", "_", raw)[:200]


def provider_settings(body: dict[str, Any]) -> tuple[str, str, str]:
    return (
        str(body.get("provider") or "local").lower(),
        str(body.get("qdrantUrl") or ""),
        str(body.get("qdrantApiKey") or ""),
    )


def build_filter(filters: dict[str, Any]):
    from qdrant_client import models

    conditions = []
    for key, value in (filters or {}).items():
        if value is None or value == "" or value == []:
            continue
        if key == "observerId":
            conditions.append(models.FieldCondition(key="eligibilityVersion", match=models.MatchValue(value=2)))
            conditions.append(models.Filter(must_not=[models.FieldCondition(key="hiddenFrom", match=models.MatchValue(value=value))]))
            conditions.append(models.Filter(should=[
                models.FieldCondition(key="ownerId", match=models.MatchValue(value=value)),
                models.Filter(must=[models.FieldCondition(key="privateMental", match=models.MatchValue(value=False))], should=[
                    models.FieldCondition(key="knownBy", match=models.MatchValue(value=value)),
                    models.FieldCondition(key="publicUnscoped", match=models.MatchValue(value=True))])]))
            continue
        if key == "asOfWorldDay":
            conditions.append(models.FieldCondition(key="worldDay", range=models.Range(lte=float(value))))
            continue
        if isinstance(value, list):
            conditions.append(models.FieldCondition(key=key, match=models.MatchAny(any=value)))
        else:
            conditions.append(models.FieldCondition(key=key, match=models.MatchValue(value=value)))
    return models.Filter(must=conditions) if conditions else None


def payload_matches_filters(payload: dict[str, Any], filters: dict[str, Any]) -> bool:
    for key, expected in (filters or {}).items():
        if expected is None or expected == "" or expected == []:
            continue
        if key == "observerId":
            if payload.get("eligibilityVersion") != 2 or expected in payload.get("hiddenFrom", []):
                return False
            own = payload.get("ownerId") == expected
            if not own and (payload.get("privateMental") is not False or not (
                    expected in payload.get("knownBy", []) or payload.get("publicUnscoped") is True)):
                return False
            continue
        if key == "asOfWorldDay":
            if float(payload.get("worldDay", 0)) > float(expected):
                return False
            continue
        actual = payload.get(key)
        if isinstance(expected, list):
            if isinstance(actual, list):
                if not any(value in expected for value in actual):
                    return False
            elif actual not in expected:
                return False
        elif isinstance(actual, list):
            if expected not in actual:
                return False
        elif actual != expected:
            return False
    return True


def contextual_chunks(document: dict[str, Any], tokenizer) -> list[dict[str, Any]]:
    """Versioned, overlapping token windows retain the original character span."""
    text = str(document.get("text") or "")
    encoded = tokenizer.encode(text, add_special_tokens=False)
    offsets = encoded.offsets
    if not offsets:
        return []
    parent = str(document.get("id") or "")
    chunks = []
    # 448 body tokens leave room for model special tokens. No character cap or
    # model-side truncation is permitted to discard a source's later outcome.
    for start in range(0, len(offsets), 416):
        end = min(start + 448, len(offsets))
        begin_char, end_char = offsets[start][0], offsets[end - 1][1]
        payload = dict(document.get("payload") or {})
        context = " | ".join(str(payload.get(k) or "") for k in ("sourceType", "ownerId", "timelineId", "worldDay"))
        # Context is metadata, never generated synopsis; keep it token bounded.
        context_tokens = tokenizer.encode(context, add_special_tokens=False)
        if len(context_tokens.ids) > 48:
            context = context[:context_tokens.offsets[47][1]]
        value = context + "\n" + text[begin_char:end_char]
        if len(tokenizer.encode(value, add_special_tokens=True).ids) > 512:
            raise ValueError("Embedding chunk exceeds the certified model window.")
        payload = dict(document.get("payload") or {})
        payload.update(documentId=parent, chunkVersion=CHUNK_VERSION, chunkOrdinal=len(chunks),
                       sourceStart=begin_char, sourceEnd=end_char, chunkHash=hashlib.sha256(value.encode("utf-8")).hexdigest())
        chunks.append({"id": str(uuid.uuid5(uuid.NAMESPACE_URL, parent + "|" + CHUNK_VERSION + "|" + str(start))),
                       "text": value, "payload": payload})
        if end == len(offsets):
            break
    return chunks


def rerank_certificate_valid(certificate: dict[str, Any], model_hash: str, tokenizer_hash: str) -> bool:
    try:
        return (certificate.get("schema") == "reign-memory-rerank-certificate-v1"
                and certificate.get("modelSha256") == model_hash
                and certificate.get("tokenizerSha256") == tokenizer_hash
                and len(certificate.get("heldOutCorpusSha256", "")) == 64
                and certificate.get("heldOut") is True
                and int(certificate.get("caseCount", 0)) >= 100
                and int(certificate.get("criticalErrors", 1)) == 0
                and float(certificate.get("requiredFactCoverage", 0)) >= .98
                and float(certificate.get("quality", 0)) > float(certificate.get("baselineQuality", 1))
                and 0 < float(certificate.get("rerankP95Ms", 0)) <= 1.2 * float(certificate.get("baselineP95Ms", 0)))
    except (TypeError, ValueError):
        return False


def local_cross_encoder(query: str, candidates: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Opt-in local ONNX inference; no implicit downloads or cloud requests."""
    directory = Path(os.environ.get("REIGN_MEMORY_RERANK_MODEL_DIR", ""))
    certificate_path = os.environ.get("REIGN_MEMORY_RERANK_CERTIFICATE", "")
    if not certificate_path or not str(directory) or not directory.is_dir():
        raise ValueError("Cross-encoder requires a local model and held-out quality/latency certificate.")
    model_path, tokenizer_path = directory / "model.onnx", directory / "tokenizer.json"
    certificate = json.loads(Path(certificate_path).read_text(encoding="utf-8"))
    model_hash = hashlib.sha256(model_path.read_bytes()).hexdigest()
    tokenizer_hash = hashlib.sha256(tokenizer_path.read_bytes()).hexdigest()
    if not rerank_certificate_valid(certificate, model_hash, tokenizer_hash):
        raise ValueError("Cross-encoder certificate does not satisfy precision and latency gates.")
    if len(candidates) > 32:
        raise ValueError("Cross-encoder candidate limit is 32.")
    import onnxruntime as ort
    from tokenizers import Tokenizer
    tokenizer = Tokenizer.from_file(str(tokenizer_path))
    tokenizer.no_truncation()
    tokenizer.no_padding()
    options = ort.SessionOptions()
    options.intra_op_num_threads = 2
    session = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
    results = []
    for candidate in candidates:
        tokens = tokenizer.encode(query, str(candidate.get("text") or ""))
        # Long sources keep their existing rank; never score only their prefix.
        if len(tokens.ids) > 512:
            continue
        values = {"input_ids": np.asarray([tokens.ids], dtype=np.int64),
                  "attention_mask": np.asarray([tokens.attention_mask], dtype=np.int64),
                  "token_type_ids": np.asarray([tokens.type_ids], dtype=np.int64)}
        score = float(np.asarray(session.run(None, {i.name: values[i.name] for i in session.get_inputs()})[0]).reshape(-1)[0])
        if math.isfinite(score):
            results.append({"id": str(candidate.get("id") or ""), "score": score})
    return sorted(results, key=lambda r: r["score"], reverse=True)


def unique_document_hits(results: list[dict[str, Any]], limit: int) -> list[dict[str, Any]]:
    seen = set()
    selected = []
    for hit in sorted(results, key=lambda h: float(h.get("score", 0)), reverse=True):
        payload = hit.get("payload") or {}
        identity = payload.get("documentId") or hit.get("id")
        if identity in seen:
            continue
        seen.add(identity)
        selected.append(hit)
        if len(selected) >= limit:
            break
    return selected


def extract_topics(text: str, maximum: int) -> list[str]:
    words = [word.lower() for word in re.findall(r"[A-Za-z][A-Za-z'-]{2,}", text)]
    counts: dict[str, int] = {}
    first: dict[str, int] = {}
    for index, word in enumerate(words):
        if word in STOP_WORDS:
            continue
        counts[word] = counts.get(word, 0) + 1
        first.setdefault(word, index)
    ranked = sorted(counts, key=lambda word: (-counts[word], first[word], word))
    return ranked[: max(1, min(12, maximum))]


def cosine(left: list[float], right: list[float]) -> float:
    dot = sum(a * b for a, b in zip(left, right))
    left_norm = math.sqrt(sum(a * a for a in left))
    right_norm = math.sqrt(sum(b * b for b in right))
    return dot / (left_norm * right_norm) if left_norm and right_norm else 0.0


class Handler(BaseHTTPRequestHandler):
    server_version = "ReignVectorWorker/1.0"

    def log_message(self, _format: str, *_args: Any) -> None:
        return

    def send_json(self, status: int, value: Any) -> None:
        raw = json.dumps(value, ensure_ascii=True, separators=(",", ":")).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.end_headers()
        self.wfile.write(raw)

    def read_json(self) -> dict[str, Any]:
        length = int(self.headers.get("Content-Length", "0") or "0")
        if length <= 0:
            return {}
        value = json.loads(self.rfile.read(min(length, 16 * 1024 * 1024)).decode("utf-8"))
        return value if isinstance(value, dict) else {}

    def do_GET(self) -> None:
        STATE.request_count += 1
        parsed = urlparse(self.path)
        try:
            if parsed.path == "/health":
                self.send_json(200, status_payload())
                return
            if parsed.path == "/topic":
                query = parse_qs(parsed.query)
                text = (query.get("text") or [""])[0]
                maximum = int((query.get("max_topics") or query.get("maxTopics") or ["5"])[0])
                topics = extract_topics(text, maximum)
                self.send_json(200, {"ok": True, "topics": topics, "generated_tags": topics})
                return
            if parsed.path == "/vectors/status":
                self.send_json(200, status_payload())
                return
            self.send_json(404, {"ok": False, "error": "not_found"})
        except Exception as exc:
            STATE.last_error = str(exc)
            self.send_json(500, {"ok": False, "error": str(exc)})

    def do_POST(self) -> None:
        STATE.request_count += 1
        parsed = urlparse(self.path)
        try:
            body = self.read_json()
            if parsed.path == "/rerank":
                self.handle_rerank(body)
            elif parsed.path == "/vectors/upsert":
                self.handle_upsert(body)
            elif parsed.path == "/vectors/search":
                self.handle_search(body)
            elif parsed.path == "/vectors/delete":
                self.handle_delete(body)
            elif parsed.path == "/vectors/status":
                self.send_json(200, status_payload())
            elif parsed.path == "/shutdown":
                self.send_json(200, {"ok": True, "status": "stopping"})
                threading.Thread(target=lambda: SERVER.shutdown() if SERVER else None, daemon=True).start()
            else:
                self.send_json(404, {"ok": False, "error": "not_found"})
        except Exception as exc:
            STATE.last_error = str(exc)
            self.send_json(500, {"ok": False, "error": str(exc)})

    def handle_rerank(self, body: dict[str, Any]) -> None:
        started = time.perf_counter()
        query = limit_text(body.get("query"), 4000)
        candidates = [item for item in body.get("candidates", []) if isinstance(item, dict)]
        if body.get("rankingModel") == "cross-encoder/ms-marco-MiniLM-L6-v2":
            with STATE.operation_gate(True):
                results = local_cross_encoder(query, candidates)
            self.send_json(200, {"ok": True, "results": results, "model": body["rankingModel"]})
            return
        foreground = str(body.get("priority") or "").strip().lower() in {"foreground", "interactive", "live"}
        aggregate_timing: dict[str, Any] = {"queueWaitMs": 0, "chunks": 0}
        if foreground:
            with STATE.operation_gate(True) as gate_timing:
                vectors = STATE.embed([query] + [limit_text(item.get("text"), 4000) for item in candidates])
                aggregate_timing["queueWaitMs"] += gate_timing["queueWaitMs"]
                aggregate_timing["chunks"] = 1
            results = [
                {"id": str(item.get("id") or ""), "score": cosine(vectors[0], vectors[index + 1])}
                for index, item in enumerate(candidates)
            ]
        else:
            # Background reranks can contain many candidates. Release the worker
            # between small chunks so an arriving live search takes the next slot.
            with STATE.operation_gate(False) as gate_timing:
                query_vector = STATE.embed([query])[0]
                aggregate_timing["queueWaitMs"] += gate_timing["queueWaitMs"]
            results = []
            chunk_size = max(1, min(8, int(os.environ.get("REIGN_BACKGROUND_RERANK_CHUNK_SIZE", "4"))))
            for offset in range(0, len(candidates), chunk_size):
                chunk = candidates[offset:offset + chunk_size]
                with STATE.operation_gate(False) as gate_timing:
                    vectors = STATE.embed([limit_text(item.get("text"), 4000) for item in chunk])
                    aggregate_timing["queueWaitMs"] += gate_timing["queueWaitMs"]
                    aggregate_timing["chunks"] += 1
                results.extend(
                    {"id": str(item.get("id") or ""), "score": cosine(query_vector, vector)}
                    for item, vector in zip(chunk, vectors)
                )
        results.sort(key=lambda item: item["score"], reverse=True)
        top_k = max(1, min(len(results), int(body.get("top_k") or len(results) or 1)))
        timing = STATE.record_operation("rerank", foreground, started, aggregate_timing)
        self.send_json(200, {"ok": True, "results": results[:top_k], "model": MODEL_NAME, "timing": timing})

    def handle_upsert(self, body: dict[str, Any]) -> None:
        from qdrant_client import models

        started = time.perf_counter()
        documents = [item for item in body.get("documents", []) if isinstance(item, dict)]
        if not documents:
            self.send_json(200, {"ok": True, "upserted": 0})
            return
        provider, url, key = provider_settings(body)
        aggregate_timing = {"queueWaitMs": 0, "chunks": 0}
        with STATE.operation_gate(False) as gate_timing:
            parents = [str(item.get("id") or "") for item in documents]
            tokenizer = STATE.get_tokenizer()
            documents = [chunk for item in documents for chunk in contextual_chunks(item, tokenizer)]
            aggregate_timing["queueWaitMs"] += gate_timing["queueWaitMs"]
        # Compute bounded batches before atomic index publication. A partial
        # inference failure leaves the previous complete document index intact.
        vectors = []
        for offset in range(0, len(documents), 4):
            with STATE.operation_gate(False) as gate_timing:
                vectors.extend(STATE.embed([item["text"] for item in documents[offset:offset + 4]]))
                aggregate_timing["queueWaitMs"] += gate_timing["queueWaitMs"]
                aggregate_timing["chunks"] += 1
        points = [models.PointStruct(id=item["id"], vector=vector, payload=item["payload"]) for item, vector in zip(documents, vectors)]
        collection = collection_name(body)
        with STATE.operation_gate(False) as gate_timing:
            with STATE.vector_lock:
                client = STATE.get_client(provider, url, key)
                STATE.ensure_collection(client, collection, provider == "qdrant")
                STATE.remove_documents(client, collection, parents, provider != "qdrant")
                if points:
                    client.upsert(collection_name=collection, points=points, wait=True)
                if provider != "qdrant":
                    STATE.update_local_index(collection, documents, vectors)
        gate_timing = aggregate_timing
        timing = STATE.record_operation("upsert", False, started, gate_timing)
        self.send_json(200, {"ok": True, "upserted": len(points), "collection": collection, "provider": provider, "timing": timing})

    def handle_search(self, body: dict[str, Any]) -> None:
        started = time.perf_counter()
        provider, url, key = provider_settings(body)
        collection = collection_name(body)
        limit = max(1, min(512, int(body.get("limit") or 96)))
        with STATE.operation_gate(True) as gate_timing:
            vector = STATE.embed([limit_text(body.get("query"), 4000)])[0]
            with STATE.vector_lock:
                client = STATE.get_client(provider, url, key)
                if not client.collection_exists(collection):
                    timing = STATE.record_operation("search", True, started, gate_timing)
                    self.send_json(200, {"ok": True, "results": [], "reason": "collection_missing", "timing": timing})
                    return
                if provider != "qdrant":
                    index = STATE.load_local_index(client, collection)
                    if index["matrix"] is None:
                        STATE.rebuild_local_matrix(index)
                    matching = [
                        position for position, payload in enumerate(index["payloads"])
                        if payload_matches_filters(payload, body.get("filters") or {})
                    ]
                    if matching:
                        query_vector = np.asarray(vector, dtype=np.float32)
                        query_vector = query_vector / max(float(np.linalg.norm(query_vector)), 1e-12)
                        candidate_scores = index["matrix"][matching] @ query_vector
                        take = len(matching)
                        if take < len(matching):
                            selected = np.argpartition(candidate_scores, -take)[-take:]
                        else:
                            selected = np.arange(len(matching))
                        selected = selected[np.argsort(candidate_scores[selected])[::-1]]
                        results = [
                            {
                                "id": index["ids"][matching[int(position)]],
                                "score": float(candidate_scores[int(position)]),
                                "payload": index["payloads"][matching[int(position)]],
                            }
                            for position in selected
                        ]
                    else:
                        results = []
                else:
                    response = client.query_points_groups(
                        collection_name=collection,
                        query=vector,
                        query_filter=build_filter(body.get("filters") or {}),
                        group_by="documentId",
                        group_size=1,
                        limit=limit,
                        with_payload=True,
                        with_vectors=False,
                    )
                    results = [
                        {"id": str(point.id), "score": float(point.score), "payload": point.payload or {}}
                        for group in response.groups for point in group.hits
                    ]
        results = unique_document_hits(results, limit)
        timing = STATE.record_operation("search", True, started, gate_timing)
        self.send_json(200, {"ok": True, "results": results, "collection": collection, "provider": provider,
                             "eligibilityApplied": bool((body.get("filters") or {}).get("observerId")), "timing": timing})

    def handle_delete(self, body: dict[str, Any]) -> None:
        from qdrant_client import models

        started = time.perf_counter()
        provider, url, key = provider_settings(body)
        collection = collection_name(body)
        ids = [str(value) for value in body.get("ids", []) if str(value).strip()]
        with STATE.operation_gate(False) as gate_timing:
            with STATE.vector_lock:
                client = STATE.get_client(provider, url, key)
                if bool(body.get("deleteCollection")):
                    deleted = False
                    if client.collection_exists(collection):
                        client.delete_collection(collection_name=collection)
                        deleted = True
                    if provider != "qdrant":
                        STATE.delete_local_index(collection, [], True)
                    timing = STATE.record_operation("delete_collection", False, started, gate_timing)
                    self.send_json(200, {"ok": True, "deletedCollection": deleted, "collection": collection, "timing": timing})
                    return
                if ids and client.collection_exists(collection):
                    STATE.remove_documents(client, collection, ids, provider != "qdrant")
        timing = STATE.record_operation("delete", False, started, gate_timing)
        self.send_json(200, {"ok": True, "deleted": len(ids), "collection": collection, "timing": timing})


def status_payload() -> dict[str, Any]:
    return {
        "ok": True,
        "service": "ReignVectorWorker",
        "model": MODEL_NAME,
        "modelVersion": MODEL_VERSION,
        "dimensions": MODEL_DIMENSIONS,
        "modelLoaded": STATE.model is not None,
        "modelDirectory": str(STATE.model_dir),
        "localVectorDirectory": str(STATE.local_vector_dir),
        "uptimeSeconds": int(time.time() - STATE.started),
        "requestCount": STATE.request_count,
        "lastOperation": STATE.last_operation,
        "lastDurationMs": STATE.last_duration_ms,
        "foregroundWaiters": STATE.foreground_waiters,
        "foregroundActive": STATE.foreground_active,
        "backgroundActive": STATE.background_active,
        "localIndexReady": bool(STATE.local_indexes),
        "localIndexCollections": {
            name: len(index.get("points", {}))
            for name, index in STATE.local_indexes.items()
        },
        "localIndexLastBuildMs": STATE.local_index_last_build_ms,
        "localIndexLastError": STATE.local_index_last_error,
        "recentOperations": list(STATE.recent_operations),
        "lastError": STATE.last_error,
    }


def main() -> None:
    global STATE, SERVER
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", default=8082, type=int)
    parser.add_argument("--data-dir", default=str(Path(__file__).resolve().parent / "data"))
    parser.add_argument("--self-test", action="store_true", help="Offline release proof in a fresh explicitly supplied data directory; never opens a listener.")
    args = parser.parse_args()
    if args.self_test:
        if "--data-dir" not in sys.argv:
            parser.error("--self-test requires an explicit isolated --data-dir")
        print(json.dumps(release_self_test(Path(args.data_dir).resolve())))
        return
    STATE = WorkerState(Path(args.data_dir).resolve())
    STATE.data_dir.mkdir(parents=True, exist_ok=True)
    SERVER = ThreadingHTTPServer((args.host, args.port), Handler)
    threading.Thread(target=STATE.warm_default_local_index, daemon=True,
                     name="reign-vector-index-warmup").start()
    SERVER.serve_forever(poll_interval=0.25)
    SERVER.server_close()


def release_self_test(data_dir: Path) -> dict[str, Any]:
    if data_dir.exists() and any(data_dir.iterdir()):
        raise ValueError("Release proof requires a fresh empty data directory.")
    if not os.environ.get("REIGN_VECTOR_MODEL_DIR", "").strip():
        raise ValueError("Release proof requires the bundled model directory.")
    # Fail any attempted network connection, including a model-download fallback.
    import socket
    def deny_connection(*args, **kwargs):
        raise RuntimeError("Network access is prohibited during offline release proof.")
    socket.socket.connect = deny_connection
    socket.create_connection = deny_connection
    os.environ["HF_HUB_OFFLINE"] = "1"
    data_dir.mkdir(parents=True, exist_ok=True)
    state = WorkerState(data_dir)
    vectors = state.embed(["The blacksmith forged a steel sword.", "The queen negotiated a peace treaty."])
    if len(vectors) != 2 or any(len(v) != 384 or not all(math.isfinite(x) for x in v) for v in vectors):
        raise ValueError("Bundled embedding inference returned invalid vectors.")
    from qdrant_client import models
    client = state.get_client("local", "", "")
    client.create_collection("reign_release_proof", vectors_config=models.VectorParams(size=384, distance=models.Distance.COSINE))
    client.upsert("reign_release_proof", points=[models.PointStruct(id=i + 1, vector=v, payload={"proof": True}) for i, v in enumerate(vectors)], wait=True)
    client.close()
    state.clients.clear()
    reopened = state.get_client("local", "", "")
    result = reopened.search("reign_release_proof", query_vector=vectors[1], limit=1)
    if len(result) != 1 or result[0].id != 2 or result[0].score < 0.99:
        raise ValueError("Persistent local semantic search failed after reopening storage.")
    reopened.close()
    proof = {"schema": "reign-vector-release-proof-v1", "ok": True, "model": MODEL_NAME,
             "dimensions": 384, "networkBlocked": True, "listenerStarted": False,
             "persistenceReopened": True, "bundledExecutable": bool(getattr(sys, "frozen", False))}
    (data_dir / "release-proof.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
    return proof


if __name__ == "__main__":
    import multiprocessing
    multiprocessing.freeze_support()
    main()
