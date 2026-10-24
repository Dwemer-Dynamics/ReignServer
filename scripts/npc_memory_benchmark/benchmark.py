#!/usr/bin/env python3
"""Offline embedding experiment. Reads frozen files; never contacts Reign or downloads models."""
from __future__ import annotations

import argparse
import ctypes
import hashlib
import importlib.metadata
import json
import math
import os
import platform
import sys
import time
from collections import Counter
from pathlib import Path

SCHEMA = "reign-npc-retrieval-benchmark-v1"


def digest(path):
    h = hashlib.sha256()
    with Path(path).open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            h.update(block)
    return h.hexdigest()


def read_json(path):
    if Path(path).stat().st_size > 32 * 1024 * 1024:
        raise ValueError("JSON exceeds 32 MiB")
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def eligible(memory, query):
    return (memory["campaign"] == query["campaign"]
            and memory["timeline"] == query["timeline"]
            and memory["generation"] == query["generation"]
            and memory["day"] <= query["day"]
            and query["observer"] in memory["observers"]
            and memory.get("active", True))


def validate_fixture(data):
    if data.get("schema") != SCHEMA or not data.get("memories") or not data.get("queries"):
        raise ValueError("Invalid or empty fixture")
    memories, queries = data["memories"], data["queries"]
    if len(memories) > 100000 or len(queries) > 1000:
        raise ValueError("Fixture exceeds bounds")
    for rows in (memories, queries):
        if len({row["id"] for row in rows}) != len(rows):
            raise ValueError("Duplicate identity")
        for row in rows:
            if not isinstance(row["text"], str) or not row["text"].strip() or len(row["text"]) > 24000:
                raise ValueError("Empty or oversized text")
            if not all(isinstance(row[key], str) and row[key] for key in ("id", "campaign", "timeline", "generation")):
                raise ValueError("Missing scope identity")
            if not isinstance(row["day"], (int, float)) or not math.isfinite(row["day"]):
                raise ValueError("Invalid day")
    by_id = {row["id"]: row for row in memories}
    for memory in memories:
        if not isinstance(memory["observers"], list) or not memory["observers"]:
            raise ValueError("Missing explicit audience")
    for query in queries:
        if query["split"] not in ("dev", "test") or not query["category"] or not query["language"] or not query["observer"]:
            raise ValueError("Missing query strata")
        for mid, grade in query["judgments"].items():
            if mid not in by_id or type(grade) is not int or grade not in (1, 2, 3) or not eligible(by_id[mid], query):
                raise ValueError("Invalid or ineligible relevance label")
    for split in ("dev", "test"):
        group = [q for q in queries if q["split"] == split]
        if not any(q["judgments"] for q in group) or not any(not q["judgments"] for q in group):
            raise ValueError("Both splits need positive and no-answer queries")


def metric(ranked, judgments, k=5):
    selected = ranked[:k]
    matches = [i + 1 for i, mid in enumerate(selected) if mid in judgments]
    if not judgments:
        return {"falseRetrieval": float(bool(selected)), "correctAbstention": float(not selected)}
    gains = [(2 ** judgments.get(mid, 0) - 1) / math.log2(i + 2) for i, mid in enumerate(selected)]
    ideal = sum((2 ** grade - 1) / math.log2(i + 2)
                for i, grade in enumerate(sorted(judgments.values(), reverse=True)[:k]))
    return {"hitAt5": float(bool(matches)), "recallAt5": len(matches) / len(judgments),
            "precisionAt5": len(matches) / k, "mrrAt5": 1 / matches[0] if matches else 0,
            "ndcgAt5": sum(gains) / ideal, "returnedPrecision": len(matches) / len(selected) if selected else 0}


def aggregate(rows, threshold=None):
    values = {}
    for row in rows:
        ids = [hit["id"] for hit in row["ranking"] if threshold is None or hit["score"] >= threshold]
        for key, value in metric(ids, row["judgments"]).items():
            values.setdefault(key, []).append(value)
    return {"queries": len(rows), "answerable": sum(bool(r["judgments"]) for r in rows),
            **{key: sum(v) / len(v) for key, v in values.items()}}


def calibrate(rows, max_false_retrieval):
    # Only development labels enter threshold selection. Keep test labels untouched.
    candidates = sorted({hit["score"] for row in rows for hit in row["ranking"]} | {1.000001})
    choices = [(t, aggregate(rows, t)) for t in candidates]
    allowed = [(t, m) for t, m in choices if m["falseRetrieval"] <= max_false_retrieval]
    threshold, scores = max(allowed, key=lambda x: (x[1]["recallAt5"], x[1]["returnedPrecision"], -x[0]))
    return {"threshold": threshold, "maxDevFalseRetrieval": max_false_retrieval, "dev": scores}


def rss():
    """Current and process high-water resident bytes, including native allocations."""
    if os.name == "nt":
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb", wintypes.DWORD), ("PageFaultCount", wintypes.DWORD)] + [
                (name, ctypes.c_size_t) for name in ("PeakWorkingSetSize", "WorkingSetSize", "QuotaPeakPagedPoolUsage",
                "QuotaPagedPoolUsage", "QuotaPeakNonPagedPoolUsage", "QuotaNonPagedPoolUsage", "PagefileUsage", "PeakPagefileUsage")]
        counters = Counters(); counters.cb = ctypes.sizeof(counters)
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.GetCurrentProcess.restype = wintypes.HANDLE
        get_memory = ctypes.WinDLL("psapi").GetProcessMemoryInfo
        get_memory.argtypes = [wintypes.HANDLE, ctypes.POINTER(Counters), wintypes.DWORD]
        get_memory.restype = wintypes.BOOL
        if not get_memory(kernel.GetCurrentProcess(), ctypes.byref(counters), counters.cb):
            raise OSError("GetProcessMemoryInfo failed")
        return {"residentBytes": counters.WorkingSetSize, "peakResidentBytes": counters.PeakWorkingSetSize}
    import resource
    resident = None
    if Path("/proc/self/statm").exists():
        resident = int(Path("/proc/self/statm").read_text().split()[1]) * os.sysconf("SC_PAGE_SIZE")
    return {"residentBytes": resident, "peakResidentBytes": resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * (1 if sys.platform == "darwin" else 1024)}


def distribution(values):
    ordered = sorted(values)
    return {"n": len(values), **{f"p{p}Ms": ordered[max(0, math.ceil(p / 100 * len(ordered)) - 1)] for p in (50, 95, 99)}}


class Encoder:
    def __init__(self, args):
        import numpy as np
        self.np = np; self.args = args; self.truncated = Counter()
        root = Path(args.model_dir).resolve()
        if not root.is_dir():
            raise ValueError("A complete pre-staged local model directory is required")
        files = sorted(p for p in root.rglob("*") if p.is_file())
        self.inventory = [{"path": p.relative_to(root).as_posix(), "bytes": p.stat().st_size, "sha256": digest(p)} for p in files]
        if not self.inventory:
            raise ValueError("Empty model directory")
        self.tree_hash = hashlib.sha256(json.dumps(self.inventory, sort_keys=True).encode()).hexdigest()
        os.environ["HF_HUB_OFFLINE"] = "1"; os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["TOKENIZERS_PARALLELISM"] = "false"
        from tokenizers import Tokenizer
        self.tokenizer = Tokenizer.from_file(str(root / "tokenizer.json"))
        self.tokenizer.no_truncation(); self.tokenizer.no_padding()
        if args.backend == "fastembed-bge":
            # Deliberately mirrors worker.py: embed() for both sides, no query prefix.
            from fastembed import TextEmbedding
            self.model = TextEmbedding(model_name="BAAI/bge-small-en-v1.5", threads=args.threads,
                providers=["CPUExecutionProvider"], specific_model_path=str(root), local_files_only=True)
        else:
            import onnxruntime as ort
            options = ort.SessionOptions(); options.intra_op_num_threads = args.threads; options.inter_op_num_threads = 1
            model_path = (root / args.model_file).resolve()
            if not model_path.is_relative_to(root):
                raise ValueError("Model file must stay inside the local directory")
            self.model = ort.InferenceSession(str(model_path), sess_options=options, providers=["CPUExecutionProvider"])
            config = read_json(root / "config.json")
            self.pad_id = config.get("pad_token_id", 0)

    def encode(self, texts, kind):
        np = self.np
        prefix = self.args.query_prefix if kind == "query" else self.args.document_prefix
        texts = [prefix + text for text in texts]
        encodings = self.tokenizer.encode_batch(texts)
        self.truncated[kind] += sum(len(e.ids) > self.args.max_tokens for e in encodings)
        if self.args.backend == "fastembed-bge":
            vectors = np.asarray(list(self.model.embed(texts, batch_size=4)), dtype=np.float32)
        else:
            batches = []
            for start in range(0, len(texts), 4):
                batch = encodings[start:start + 4]
                if any(len(e.ids) > self.args.max_tokens for e in batch):
                    # Tokenizer handles model-specific trailing separators correctly.
                    self.tokenizer.enable_truncation(max_length=self.args.max_tokens)
                    batch = self.tokenizer.encode_batch(texts[start:start + 4])
                    self.tokenizer.no_truncation()
                width = max(len(e.ids) for e in batch)
                ids = np.full((len(batch), width), self.pad_id, dtype=np.int64)
                mask = np.zeros_like(ids); types = np.zeros_like(ids)
                for i, e in enumerate(batch):
                    ids[i, :len(e.ids)] = e.ids; mask[i, :len(e.ids)] = 1; types[i, :len(e.ids)] = e.type_ids
                values = {"input_ids": ids, "attention_mask": mask, "token_type_ids": types}
                feed = {}
                for item in self.model.get_inputs():
                    if item.name not in values:
                        raise ValueError("Unsupported ONNX input: " + item.name)
                    feed[item.name] = values[item.name].astype(np.int32 if item.type == "tensor(int32)" else np.int64)
                hidden = self.model.run(None, feed)[0]
                if hidden.ndim != 3:
                    raise ValueError("Expected token-level output; do not guess pooling for a different graph")
                pooled = hidden[:, 0] if self.args.pooling == "cls" else (hidden * mask[:, :, None]).sum(1) / mask.sum(1)[:, None]
                batches.append(pooled)
            vectors = np.asarray(np.concatenate(batches), dtype=np.float32)
        if vectors.ndim != 2 or not np.isfinite(vectors).all() or np.any(np.linalg.norm(vectors, axis=1) == 0):
            raise ValueError("Invalid embeddings")
        return vectors / np.linalg.norm(vectors, axis=1, keepdims=True)


def run(args):
    # Thread limits must precede numpy import. Fresh process per model is required.
    for key in ("OMP_NUM_THREADS", "OPENBLAS_NUM_THREADS", "MKL_NUM_THREADS"):
        os.environ[key] = str(args.threads)
    import numpy as np
    fixture = read_json(args.fixture); validate_fixture(fixture)
    memories = list(fixture["memories"])
    for npc in range(args.extra_npcs):
        for index in range(args.memories_per_npc):
            memories.append({"id": f"scale-{npc}-{index}", "text": f"Noble {npc} bought grain and visited the market on day {index % 90}.",
                "campaign": "synthetic", "timeline": "main", "generation": "g1", "day": index % 90,
                "observers": [f"scale-npc-{npc}"] + (["corein"] if index % 10 == 0 else [])})
    if len(memories) > 100000:
        raise ValueError("Scale limit is 100000 memories")
    before = rss(); started = time.perf_counter(); encoder = Encoder(args)
    model_load_ms = (time.perf_counter() - started) * 1000; after_model = rss()
    started = time.perf_counter(); matrix = encoder.encode([m["text"] for m in memories], "document")
    index_ms = (time.perf_counter() - started) * 1000; after_index = rss()
    rows = []; timings = {k: [] for k in ("queryEmbedding", "eligibility", "search", "total")}
    # One independent uncached warm-up; measured repeats still perform inference.
    first_query_start = time.perf_counter()
    encoder.encode(["An unrelated warm-up sentence."], "warmup")
    first_query_ms = (time.perf_counter() - first_query_start) * 1000
    for query in fixture["queries"]:
        ranking = None
        for repeat in range(args.repeats):
            start = time.perf_counter(); vector = encoder.encode([query["text"]], "query")[0]; encoded = time.perf_counter()
            indexes = np.asarray([i for i, m in enumerate(memories) if eligible(m, query)], dtype=np.int64); filtered = time.perf_counter()
            scores = matrix[indexes] @ vector
            # Stable source ID tie-break; complete documents are the ranking unit.
            best = sorted(range(len(indexes)), key=lambda j: (-float(scores[j]), memories[int(indexes[j])]["id"]))[:5]
            result = [{"id": memories[int(indexes[j])]["id"], "score": float(scores[j])} for j in best]; done = time.perf_counter()
            if ranking is not None and [h["id"] for h in ranking] != [h["id"] for h in result]:
                raise ValueError("Non-reproducible ranking across repeats")
            ranking = result
            for key, elapsed in (("queryEmbedding", encoded-start), ("eligibility", filtered-encoded), ("search", done-filtered), ("total", done-start)):
                timings[key].append(elapsed * 1000)
        rows.append({"id": query["id"], "split": query["split"], "category": query["category"], "language": query["language"],
            "judgments": query["judgments"], "eligibleCount": len(indexes), "ranking": ranking})
    calibration = calibrate([r for r in rows if r["split"] == "dev"], args.max_dev_false_retrieval)
    test = [r for r in rows if r["split"] == "test"]
    versions = {}
    for package in ("numpy", "onnxruntime", "tokenizers", "fastembed"):
        try: versions[package] = importlib.metadata.version(package)
        except importlib.metadata.PackageNotFoundError: versions[package] = None
    report = {"schema": SCHEMA, "certificateEligible": False, "scope": "synthetic offline dense retrieval; not production pipeline parity",
        "fixtureSha256": digest(args.fixture), "runnerSha256": digest(__file__), "model": args.model_id,
        "modelRevision": args.revision, "modelTreeSha256": encoder.tree_hash, "modelFiles": encoder.inventory,
        "configuration": vars(args), "environment": {"python": sys.version, "platform": platform.platform(), "cpu": platform.processor(), "logicalCpus": os.cpu_count(), "packages": versions},
        "corpus": {"memories": len(memories), "dimensions": matrix.shape[1], "float32VectorBytes": matrix.nbytes,
            "scalePolicy": "20 default memories per extra NPC; every tenth synthetic memory visible to corein",
            "modelDirectoryBytes": sum(f["bytes"] for f in encoder.inventory), "truncatedInputs": dict(encoder.truncated)},
        "latency": {"modelLoadAndHashMs": model_load_ms, "firstQueryAfterIndexMs": first_query_ms, "indexEmbeddingMs": index_ms, "documentsPerSecond": len(memories) / (index_ms / 1000),
            "warmUncached": {k: distribution(v) for k, v in timings.items()}},
        "memory": {"beforeModel": before, "afterModel": after_model, "afterIndex": after_index, "afterQueries": rss()},
        "calibration": calibration, "testRaw": aggregate(test), "testThresholded": aggregate(test, calibration["threshold"]),
        "testByLanguage": {lang: {"raw": aggregate([r for r in test if r["language"] == lang]), "thresholded": aggregate([r for r in test if r["language"] == lang], calibration["threshold"])} for lang in sorted({r["language"] for r in test})},
        "testByCategory": {cat: {"raw": aggregate([r for r in test if r["category"] == cat]), "thresholded": aggregate([r for r in test if r["category"] == cat], calibration["threshold"])} for cat in sorted({r["category"] for r in test})},
        "cases": rows, "coverageLimits": ["Synthetic labels are development evidence, not representative Reign accuracy.",
            "Eligibility is a frozen simplified contract, not production authorization proof.", "No SQL, chunk grouping, hybrid fusion, reranker, prompt packing or NPC answer generation.",
            "Serial CPU exact search; no live contention, cold OS-cache claim, GPU VRAM or re-embedding rollback proof.",
            "Peak RSS is process lifetime; model hashing and imports are included. Index bytes exclude payloads, caches and Qdrant."]}
    # Exclusive creation: no accidental replacement of retained evidence.
    output = Path(args.output); output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8") as stream:
        json.dump(report, stream, ensure_ascii=False, indent=2); stream.write("\n")
    print(json.dumps({"report": str(output), "model": args.model_id, "testRaw": report["testRaw"], "testThresholded": report["testThresholded"], "latency": report["latency"], "memory": report["memory"]}, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", default=str(Path(__file__).with_name("fixtures.json")))
    parser.add_argument("--backend", choices=("fastembed-bge", "onnx"), required=True)
    parser.add_argument("--model-dir", required=True); parser.add_argument("--model-id", required=True)
    parser.add_argument("--revision", required=True, help="Resolved upstream commit or local artifact identity")
    parser.add_argument("--model-file", default="model.onnx"); parser.add_argument("--pooling", choices=("cls", "mean"), default="mean")
    parser.add_argument("--max-tokens", type=int, default=512); parser.add_argument("--threads", type=int, default=2)
    parser.add_argument("--query-prefix", default=""); parser.add_argument("--document-prefix", default="")
    parser.add_argument("--extra-npcs", type=int, default=0); parser.add_argument("--memories-per-npc", type=int, default=20)
    parser.add_argument("--repeats", type=int, default=3); parser.add_argument("--max-dev-false-retrieval", type=float, default=0.05)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    if not (1 <= args.threads <= 4 and 1 <= args.repeats <= 100 and 8 <= args.max_tokens <= 8192
            and 0 <= args.extra_npcs <= 5000 and 1 <= args.memories_per_npc <= 1000
            and args.extra_npcs * args.memories_per_npc <= 99900 and 0 <= args.max_dev_false_retrieval <= 1):
        parser.error("Argument outside benchmark bounds")
    if args.backend == "fastembed-bge" and (args.max_tokens != 512 or args.query_prefix or args.document_prefix):
        parser.error("Production BGE adapter requires 512 tokens and no prefixes")
    if Path(args.output).exists():
        parser.error("Output already exists")
    run(args)


if __name__ == "__main__":
    main()
