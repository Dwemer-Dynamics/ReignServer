"""Bounded local relevance and extractive packing hints; never fact/identity authority.

Pinned ONNX conversions of Qwen3-Reranker-0.6B and LLMLingua-2. No downloads,
remote inference, database access, generated prose or eager model loading.
"""
from __future__ import annotations

import copy
import hashlib
import math
import threading
import time
from collections import OrderedDict
from pathlib import Path

import numpy as np

SCHEMA = "reign-memory-selection-v1"
RANKING_POLICY = "complete-source-window-max-v1"
QWEN_MODEL = "Qwen/Qwen3-Reranker-0.6B"
LINGUA_MODEL = "microsoft/llmlingua-2-bert-base-multilingual-cased-meetingbank"
QWEN_HASH = "91541a53f5faa6796553aa0d29fea3b24abceccfd45ccfeb78a267d4ea34908a"
RUNTIME_VERSION = "1.22.0"
QWEN_TOKENIZER_HASH = "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4"
LINGUA_HASH = "6745c7aff5cb5c2c3c96b8e72ab8a65ce42157058f9221f6c340f0c37e760f5d"
LINGUA_TOKENIZER_HASH = "bf1b59b7b11c95f194f51708d918eea378e09d05f84c0e1656dc5180e8117088"
FILES = {"qwen-reranker-binary.onnx": QWEN_HASH, "qwen-reranker-tokenizer.json": QWEN_TOKENIZER_HASH,
         "llmlingua2.onnx": LINGUA_HASH, "llmlingua2-tokenizer.json": LINGUA_TOKENIZER_HASH}
INSTRUCTION = ("Retrieve conversation evidence needed to respond accurately to the current contribution. "
               "Include prior explanations, answers, corrections, shared experiences and conditions. "
               "A similar topic alone is less relevant than what these speakers actually said or did.")
PREFIX = ('<|im_start|>system\nJudge whether the Document meets the requirements based on the Query and '
          'the Instruct provided. Note that the answer can only be "yes" or "no".<|im_end|>\n<|im_start|>user\n')
SUFFIX = '<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n'


def text_hash(value):
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def file_hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def utf16_offset(text, index):
    return len(text[:index].encode("utf-16-le")) // 2


def source_windows(text, offsets, budget, overlap=192):
    """Cover the entire original, including whitespace, with overlapping source slices."""
    if budget <= overlap or budget < 256:
        raise ValueError("ranking_query_leaves_no_document_window")
    if not offsets:
        return [(0, len(text))]
    if any(a < 0 or b < a or b > len(text) for a, b in offsets):
        raise ValueError("invalid_ranking_source_offsets")
    windows = []
    for start in range(0, len(offsets), budget - overlap):
        end = min(len(offsets), start + budget)
        lo = 0 if start == 0 else offsets[start][0]
        hi = len(text) if end == len(offsets) else offsets[end][0]
        if hi <= lo or windows and lo > windows[-1][1]:
            raise ValueError("incomplete_ranking_source_coverage")
        windows.append((lo, hi))
        if len(windows) > 16:
            raise ValueError("complete_bundle_exceeds_window_limit")
        if end == len(offsets):
            break
    if windows[-1][1] != len(text):
        raise ValueError("incomplete_ranking_source_coverage")
    return windows


def _valid_candidates(body):
    values = body.get("candidates")
    if not isinstance(values, list) or not 1 <= len(values) <= 32:
        raise ValueError("candidate_count_out_of_bounds")
    seen, total = set(), 0
    for item in values:
        if not isinstance(item, dict):
            raise ValueError("invalid_candidate")
        sid, text = item.get("id"), item.get("text")
        if not isinstance(sid, str) or not 0 < len(sid) <= 96 or sid in seen:
            raise ValueError("invalid_candidate_identity")
        if not isinstance(text, str) or not 0 < len(text) <= 24000:
            raise ValueError("candidate_text_out_of_bounds")
        if item.get("textHash", "").lower() != text_hash(text):
            raise ValueError("candidate_hash_mismatch")
        seen.add(sid); total += len(text)
    if total > 160000:
        raise ValueError("request_text_out_of_bounds")
    return values


def validate_segments(item):
    """Require an exact, gap-free partition in .NET UTF-16 coordinates."""
    segments = item.get("segments")
    if not isinstance(segments, list) or not 1 <= len(segments) <= 256:
        raise ValueError("invalid_segment_count")
    cursor, pieces = 0, []
    for index, segment in enumerate(segments):
        if not isinstance(segment, dict) or segment.get("index") != index:
            raise ValueError("invalid_segment_index")
        text = segment.get("text")
        if (not isinstance(text, str) or not text or type(segment.get("protected")) is not bool
                or type(segment.get("start")) is not int or segment["start"] != cursor):
            raise ValueError("invalid_segment_span")
        cursor += utf16_offset(text, len(text)); pieces.append(text)
    if "".join(pieces) != item["text"]:
        raise ValueError("segment_content_mismatch")
    return segments


def select_segments(segments, probabilities, rate):
    """LLMLingua supplies salience only. Whole protected spans always survive."""
    if len(probabilities) != len(segments) or any(not math.isfinite(v) or not 0 <= v <= 1 for v in probabilities):
        raise ValueError("invalid_compression_scores")
    chosen = {i for i, s in enumerate(segments) if s["protected"]}
    target = math.ceil(sum(len(s["text"]) for s in segments) * rate)
    used = sum(len(segments[i]["text"]) for i in chosen)
    for index in sorted((i for i in range(len(segments)) if i not in chosen), key=lambda i: (-probabilities[i], i)):
        if used >= target:
            break
        chosen.add(index); used += len(segments[index]["text"])
    return sorted(chosen)


class OnnxSelectionModels:
    def __init__(self, directory):
        self.directory = Path(directory)
        self.sessions, self.tokenizers = {}, {}
        self.last_rank_details = {}

    def load(self, operation, backend="cpu"):
        key = operation + ":" + backend
        if key in self.sessions:
            return
        import onnxruntime as ort
        from tokenizers import Tokenizer
        if ort.__version__ != RUNTIME_VERSION:
            raise ValueError("unqualified_selection_runtime_version")
        prefix = "qwen-reranker" if operation == "rank" else "llmlingua2"
        model_file = "qwen-reranker-binary.onnx" if operation == "rank" else "llmlingua2.onnx"
        for name in (model_file, prefix + "-tokenizer.json"):
            path = self.directory / name
            if not path.is_file() or path.is_symlink() or file_hash(path) != FILES[name]:
                raise ValueError("pinned_local_model_unavailable")
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        # Avoid keeping a high-water arena allocation after an unusually long source.
        options.enable_cpu_mem_arena = False
        options.enable_mem_pattern = False
        providers = ["CPUExecutionProvider"]
        if backend == "cuda":
            ort.preload_dlls(directory="")
            if "CUDAExecutionProvider" not in ort.get_available_providers():
                raise ValueError("selection_cuda_unavailable")
            # Reserve a bounded arena; never silently spill a requested CUDA run
            # into slow CPU ranking or claim it passed the CUDA qualification.
            providers.insert(0, ("CUDAExecutionProvider", {"gpu_mem_limit": 2 * 1024**3,
                "arena_extend_strategy": "kSameAsRequested", "use_tf32": False}))
        session = ort.InferenceSession(str(self.directory / model_file), sess_options=options, providers=providers)
        if backend == "cuda" and "CUDAExecutionProvider" not in session.get_providers():
            raise ValueError("selection_cuda_initialization_failed")
        # Backend changes do not keep a second ranker resident.
        for old in list(self.sessions):
            if old.startswith(operation + ":"):
                del self.sessions[old]
        self.sessions[key] = session
        tokenizer = Tokenizer.from_file(str(self.directory / (prefix + "-tokenizer.json")))
        tokenizer.no_truncation(); tokenizer.no_padding()
        self.tokenizers[operation] = tokenizer

    @staticmethod
    def run(session, inputs, deadline):
        import onnxruntime as ort
        remaining = deadline - time.perf_counter()
        if remaining <= 0:
            raise TimeoutError("selection_deadline")
        options = ort.RunOptions()
        if "CUDAExecutionProvider" in session.get_providers():
            # Variable-length records otherwise leave unusable arena fragments
            # behind over a long session despite the overall allocation cap.
            options.add_run_config_entry("memory.enable_memory_arena_shrinkage", "gpu:0")
        timer = threading.Timer(remaining, lambda: setattr(options, "terminate", True))
        timer.daemon = True; timer.start()
        try:
            result = session.run(["logits"], inputs, options)[0]
            if time.perf_counter() > deadline:
                raise TimeoutError("selection_deadline")
            return result
        finally:
            timer.cancel()

    def rank(self, query, text, deadline, backend="cpu"):
        self.load("rank", backend)
        tokenizer, session = self.tokenizers["rank"], self.sessions["rank:" + backend]
        before = PREFIX + "<Instruct>: " + INSTRUCTION + "\n<Query>: " + query + "\n<Document>: "
        ids = tokenizer.encode(before + text + SUFFIX, add_special_tokens=False).ids
        self.last_rank_details = {}
        if len(ids) <= 2048:
            score = self.rank_tokens(session, ids, deadline)
            self.last_rank_details = {"sourceCoverage": "complete", "windowCount": 1,
                "maxWindowTokens": len(ids), "sourceSpansUtf16": [[0, utf16_offset(text, len(text))]]}
            return score
        # Relevance is the strongest covered passage, not a factual verdict.
        # All windows must finish; the writer still receives the original linked
        # record, never a favorable prefix or a model-written summary.
        fragment = "SCORING FRAGMENT OF COMPLETE EVIDENCE (whole record retained):\n"
        overhead = len(tokenizer.encode(before + fragment + SUFFIX, add_special_tokens=False).ids)
        encoded = tokenizer.encode(text, add_special_tokens=False)
        windows = source_windows(text, encoded.offsets, 2048 - overhead - 16)
        scores, lengths = [], []
        for lo, hi in windows:
            ids = tokenizer.encode(before + fragment + text[lo:hi] + SUFFIX, add_special_tokens=False).ids
            if len(ids) > 2048:
                raise ValueError("ranking_window_exceeds_token_limit")
            scores.append(self.rank_tokens(session, ids, deadline)); lengths.append(len(ids))
        self.last_rank_details = {"sourceCoverage": "complete_overlapping_source_windows", "windowCount": len(windows),
            "maxWindowTokens": max(lengths), "sourceSpansUtf16": [[utf16_offset(text, lo), utf16_offset(text, hi)] for lo, hi in windows]}
        return max(scores)

    def rank_tokens(self, session, ids, deadline):
        inputs = {"input_ids": np.asarray([ids], dtype=np.int64),
                  "attention_mask": np.ones((1, len(ids)), dtype=np.int64),
                  "position_ids": np.arange(len(ids), dtype=np.int64)[None, :]}
        for item in session.get_inputs():
            if item.name.startswith("past_key_values."):
                inputs[item.name] = np.zeros((1, 8, 0, 128),
                    dtype=np.float16 if item.type == "tensor(float16)" else np.float32)
        # The Q4/F16 graph retains the last position's no/yes output columns.
        pair = self.run(session, inputs, deadline)[0].astype(np.float64)
        if pair.shape != (2,):
            raise ValueError("binary_score_shape_mismatch")
        probabilities = np.exp(pair - pair.max())
        return float(probabilities[1] / probabilities.sum())

    def compress(self, item, deadline):
        self.load("compress")
        tokenizer, session = self.tokenizers["compress"], self.sessions["compress:cpu"]
        segments = validate_segments(item)
        encoded = tokenizer.encode(item["text"], add_special_tokens=False)
        if len(encoded.ids) > 4096:
            raise ValueError("compression_text_exceeds_token_limit")
        probabilities = np.zeros(len(encoded.ids), dtype=np.float64)
        # Full source coverage with exact offsets; never decode text from model tokens.
        for start in range(0, len(encoded.ids), 480):
            ids = [tokenizer.token_to_id("[CLS]")] + encoded.ids[start:start + 480] + [tokenizer.token_to_id("[SEP]")]
            inputs = {"input_ids": np.asarray([ids], dtype=np.int64),
                      "attention_mask": np.ones((1, len(ids)), dtype=np.int64),
                      "token_type_ids": np.zeros((1, len(ids)), dtype=np.int64)}
            logits = self.run(session, inputs, deadline)[0, 1:-1].astype(np.float64)
            values = np.exp(logits - logits.max(axis=1, keepdims=True))
            probabilities[start:start + len(values)] = values[:, 1] / values.sum(axis=1)
        spans, cursor = [], 0
        for segment in segments:
            end = cursor + len(segment["text"])
            token_scores = [probabilities[i] for i, (a, b) in enumerate(encoded.offsets) if b > cursor and a < end]
            spans.append(float(np.mean(token_scores)) if token_scores else 1.0)
            cursor = end
        return select_segments(segments, spans, float(item.get("rate", .65)))


class MemorySelection:
    def __init__(self, directory, models=None):
        self.directory = Path(directory)
        self.models = models or OnnxSelectionModels(directory)
        self.lock = threading.Lock()
        self.cache = OrderedDict()
        self.last_status = "not_loaded"

    def status(self):
        return {"schema": SCHEMA, "rankingPolicy": RANKING_POLICY, "modelsInstalled": all((self.directory / name).is_file() for name in FILES),
                "ranker": QWEN_MODEL, "rankerHash": QWEN_HASH, "compressor": LINGUA_MODEL,
                 "compressorHash": LINGUA_HASH, "loadedOperations": list(getattr(self.models, "sessions", {})),
                "runtimeVersion": RUNTIME_VERSION, "cudaArenaLimitBytes": 2 * 1024**3,
                "cachedResults": len(self.cache), "lastStatus": self.last_status, "remoteCalls": 0}

    def evaluate(self, body):
        started = time.perf_counter()
        result = {"schema": SCHEMA, "ok": False, "remoteCalls": 0, "results": []}
        acquired = False
        try:
            if not isinstance(body, dict) or body.get("schema") != SCHEMA:
                raise ValueError("invalid_request_schema")
            if body.get("rankingPolicy", RANKING_POLICY) != RANKING_POLICY:
                raise ValueError("ranking_policy_mismatch")
            operation = body.get("operation")
            if operation not in ("rank", "compress"):
                raise ValueError("invalid_operation")
            backend = body.get("backend", "cpu")
            if backend not in ("cpu", "cuda") or operation == "compress" and backend != "cpu":
                raise ValueError("invalid_selection_backend")
            result.update({"operation": operation, "modelHash": QWEN_HASH if operation == "rank" else LINGUA_HASH,
                           "tokenizerHash": QWEN_TOKENIZER_HASH if operation == "rank" else LINGUA_TOKENIZER_HASH,
                            "backend": backend, "runtimeVersion": RUNTIME_VERSION})
            result["rankingPolicy"] = RANKING_POLICY
            candidates = _valid_candidates(body)
            query = body.get("query", "")
            if not isinstance(query, str) or len(query) > 6000 or operation == "rank" and not query.strip():
                raise ValueError("query_out_of_bounds")
            timeout = body.get("timeoutMs", 15000)
            if type(timeout) is not int or not 100 <= timeout <= 30000:
                raise ValueError("timeout_out_of_bounds")
            deadline = started + timeout / 1000
            if operation == "compress":
                for item in candidates:
                    validate_segments(item)
                    rate = item.get("rate", .65)
                    if isinstance(rate, bool) or not isinstance(rate, (int, float)) or not .5 <= rate <= 1:
                        raise ValueError("rate_out_of_bounds")
            acquired = self.lock.acquire(timeout=min(.1, timeout / 1000))
            if not acquired:
                raise TimeoutError("selection_busy")
            outputs = []
            for item in candidates:
                if time.perf_counter() >= deadline:
                    raise TimeoutError("selection_deadline")
                # Protected partitions and rate are part of the compression cache identity.
                import json
                key = text_hash(SCHEMA + RANKING_POLICY + operation + backend + RUNTIME_VERSION + query + json.dumps(item, sort_keys=True, ensure_ascii=True))
                if key in self.cache:
                    value = copy.deepcopy(self.cache[key]); self.cache.move_to_end(key)
                else:
                    value = {"id": item["id"], "textHash": item["textHash"]}
                    if operation == "rank":
                        score = self.models.rank(query, item["text"], deadline, backend)
                        if not math.isfinite(score) or not 0 <= score <= 1:
                            raise ValueError("invalid_relevance_score")
                        value["score"] = score
                        value["scoringCoverage"] = copy.deepcopy(getattr(self.models, "last_rank_details", {}))
                    else:
                        value["selectedSegments"] = self.models.compress(item, deadline)
                    if time.perf_counter() >= deadline:
                        raise TimeoutError("selection_deadline")
                    self.cache[key] = copy.deepcopy(value)
                    while len(self.cache) > 256:
                        self.cache.popitem(last=False)
                outputs.append(value)
            result.update({"ok": True, "status": "complete", "results": outputs})
        except Exception as exc:
            # Partial ranking must not mix an incomparable score scale with untouched records.
            message = str(exc)
            status = ("selection_memory_pressure" if "allocate memory" in message.lower() or "out of memory" in message.lower()
                      else "selection_deadline" if "terminate flag" in message.lower() else message[:180])
            result.update({"status": status, "results": []})
        finally:
            if acquired:
                self.lock.release()
        self.last_status = result["status"]
        result["elapsedMs"] = round((time.perf_counter() - started) * 1000, 3)
        return result
