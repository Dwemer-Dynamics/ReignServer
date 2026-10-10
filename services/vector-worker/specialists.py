"""Bounded, local-only extraction. Results are search hints, never campaign facts."""
from __future__ import annotations

import copy
import hashlib
import math
import os
import json
import threading
import time
from collections import OrderedDict
from pathlib import Path
from typing import Any

SCHEMA = "reign-memory-specialist-v1"
ENTITY_TYPES = {
    "person": "A named person mentioned in this passage",
    "place": "A named town, settlement, region or other location",
    "object": "A specific physical object, gift, keepsake or possession",
    "goal": "An explicitly described intended outcome or shared plan",
    "commitment": "The exact language of a promise, obligation or accepted commitment",
    "life_event": "A described major change in a person's circumstances or relationships",
}
TOPICS = ["family", "marriage", "bereavement", "friendship", "romance", "leadership",
          "independence", "commerce", "land", "military", "tournaments", "correspondence",
          "gifts", "botany", "travel", "captivity", "identity", "commitments", "boundaries"]


def text_hash(text: str) -> str:
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def utf16_offset(text: str, offset: int) -> int:
    return len(text[:offset].encode("utf-16-le")) // 2


def validate_extraction(text: str, result: Any) -> dict[str, Any]:
    """Keep exact input spans. Confidence does not establish truth or identity."""
    result = result if isinstance(result, dict) else {}
    entities, topics, rejected = [], [], 0
    raw_entities = result.get("entities")
    if not isinstance(raw_entities, dict):
        raw_entities = {}
        rejected += 1
    for kind, items in raw_entities.items():
        if kind not in ENTITY_TYPES or not isinstance(items, list):
            rejected += 1
            continue
        for item in items[:128]:
            if not isinstance(item, dict):
                rejected += 1
                continue
            start, end, value = item.get("start"), item.get("end"), item.get("text")
            score = item.get("confidence", 0)
            if (type(start) is not int or type(end) is not int or not isinstance(value, str)
                    or not 0 <= start < end <= len(text) or text[start:end] != value
                    or isinstance(score, bool) or not isinstance(score, (int, float))
                    or not math.isfinite(score) or not 0.5 <= score <= 1):
                rejected += 1
                continue
            entry = {"kind": kind, "text": value, "start": utf16_offset(text, start),
                     "end": utf16_offset(text, end), "confidence": score,
                     "authority": "candidate_mention"}
            if entry not in entities:
                entities.append(entry)
    classifications = result.get("classifications")
    raw_topics = result.get("topics") or (classifications.get("topics", []) if isinstance(classifications, dict) else [])
    if isinstance(raw_topics, dict):
        raw_topics = [raw_topics]
    if isinstance(raw_topics, list):
        for item in raw_topics[:len(TOPICS)]:
            if not isinstance(item, dict):
                continue
            label, score = item.get("label"), item.get("confidence", 0)
            if (label in TOPICS and not isinstance(score, bool) and isinstance(score, (int, float))
                    and math.isfinite(score) and 0.5 <= score <= 1 and label not in topics):
                topics.append(label)
    return {"entities": entities[:128], "topics": topics, "rejectedSpans": rejected,
            "authority": "retrieval_candidates_only", "offsetEncoding": "utf16"}


def source_chunks(text: str, tokenizer, token_limit: int = 160, overlap: int = 24):
    """Cover every character, retaining overlap without decoding/re-writing text."""
    offsets = tokenizer(text, add_special_tokens=False, return_offsets_mapping=True)["offset_mapping"]
    if not offsets:
        return [(0, len(text))]
    chunks = []
    for start in range(0, len(offsets), token_limit - overlap):
        end = min(len(offsets), start + token_limit)
        lo = 0 if start == 0 else offsets[start][0]
        hi = len(text) if end == len(offsets) else offsets[end][0]
        chunks.append((lo, hi))
        if end == len(offsets):
            break
    if len(chunks) > 48:
        raise ValueError("source_chunk_limit")
    return chunks


class MemoryExtractor:
    def __init__(self, model_dir: str = "", loader=None, cache_limit: int = 256):
        self.model_dir = model_dir or os.environ.get("REIGN_MEMORY_EXTRACTOR_MODEL_DIR", "")
        self.loader = loader
        self.model = None
        self.lock = threading.Lock()
        self.cache: OrderedDict[str, dict[str, Any]] = OrderedDict()
        self.cache_limit = max(1, min(1024, cache_limit))
        self.model_hash = ""
        self.last_status = "not_loaded" if self.model_dir or loader else "not_configured"

    def status(self) -> dict[str, Any]:
        return {"schema": SCHEMA, "configured": bool(self.model_dir or self.loader),
                "modelLoaded": self.model is not None, "modelHash": self.model_hash,
                "lastStatus": self.last_status, "cachedSources": len(self.cache),
                "remoteCalls": 0, "authority": "retrieval_candidates_only"}

    def _load(self):
        if self.model is not None:
            return
        if self.loader:
            self.model, self.model_hash = self.loader(), "injected_fixture"
            return
        directory = Path(self.model_dir)
        if not self.model_dir or not directory.is_dir() or not (directory / "config.json").is_file():
            raise ValueError("local_model_not_available")
        required = ["config.json", "encoder_config/config.json", "tokenizer_config.json", "tokenizer.json"]
        for name in required:
            config = json.loads((directory / name).read_text(encoding="utf-8"))
            if config.get("auto_map"):
                raise ValueError("custom_model_code_not_allowed")
            if name == "encoder_config/config.json" and config.get("model_type") != "deberta-v2":
                raise ValueError("unqualified_encoder")
        # Pin cache identity to the actual weights as well as the schema/config.
        files = sorted(directory.glob("*.safetensors"))
        if not files:
            raise ValueError("local_weights_not_available")
        digest = hashlib.sha256(SCHEMA.encode("utf-8"))
        tokenizer_files = [directory / name for name in
                           ("added_tokens.json", "special_tokens_map.json", "spm.model", "vocab.json", "merges.txt")
                           if (directory / name).is_file()]
        for path in [directory / name for name in required] + tokenizer_files + files:
            digest.update(path.relative_to(directory).as_posix().encode("utf-8") + b"\0")
            digest.update(str(path.stat().st_size).encode("ascii") + b"\0")
            with path.open("rb") as stream:
                for block in iter(lambda: stream.read(1024 * 1024), b""):
                    digest.update(block)
        self.model_hash = digest.hexdigest()
        import torch
        from gliner2 import GLiNER2
        torch.set_num_threads(max(1, min(4, int(os.environ.get("REIGN_EXTRACTOR_THREADS", "2")))))
        # A runtime request never downloads models, executes custom Hub code, or
        # uses the hosted API. Missing local dependencies leave the worker usable.
        self.model = GLiNER2.from_pretrained(str(directory.resolve()), local_files_only=True)
        self.model.eval()

    def extract(self, body: dict[str, Any]) -> dict[str, Any]:
        started = time.perf_counter()
        if not isinstance(body, dict):
            return {"ok": False, "status": "invalid_input", "remoteCalls": 0}
        text, source = body.get("text"), body.get("sourceId")
        if not isinstance(text, str) or not text.strip() or len(text) > 12000:
            return {"ok": False, "status": "input_out_of_bounds", "remoteCalls": 0}
        if not isinstance(source, str) or not source or len(source) > 256:
            return {"ok": False, "status": "invalid_source_identity", "remoteCalls": 0}
        fingerprint = text_hash(text)
        if body.get("textHash") != fingerprint:
            return {"ok": False, "status": "source_hash_mismatch", "remoteCalls": 0}
        if not self.model_dir and not self.loader:
            return {"ok": False, "status": "not_configured", "remoteCalls": 0}
        if not self.lock.acquire(blocking=False):
            return {"ok": False, "status": "busy", "remoteCalls": 0}
        try:
            self._load()
            key = text_hash(SCHEMA + "|" + self.model_hash + "|" + fingerprint)
            cached = key in self.cache
            if cached:
                result = copy.deepcopy(self.cache[key])
                self.cache.move_to_end(key)
            else:
                tokenizer = getattr(getattr(self.model, "processor", None), "tokenizer", None)
                if tokenizer is None:
                    raise ValueError("tokenizer_unavailable")
                chunks = source_chunks(text, tokenizer)
                schema = self.model.create_schema().entities(ENTITY_TYPES).classification(
                    "topics", TOPICS, multi_label=True, cls_threshold=0.5)
                result = {"entities": [], "topics": [], "rejectedSpans": 0,
                          "authority": "retrieval_candidates_only", "offsetEncoding": "utf16",
                          "chunkCount": len(chunks), "coverage": "complete_source_windows"}
                for lo, hi in chunks:
                    raw = self.model.extract(text[lo:hi], schema, include_spans=True,
                                             include_confidence=True, max_len=None)
                    part = validate_extraction(text[lo:hi], raw)
                    shift = utf16_offset(text, lo)
                    for entity in part["entities"]:
                        entity["start"] += shift
                        entity["end"] += shift
                        if not any((e["kind"], e["start"], e["end"]) == (entity["kind"], entity["start"], entity["end"]) for e in result["entities"]):
                            result["entities"].append(entity)
                    result["topics"] = list(dict.fromkeys(result["topics"] + part["topics"]))
                    result["rejectedSpans"] += part["rejectedSpans"]
                if len(result["entities"]) > 128:
                    result["entities"] = result["entities"][:128]
                    result["coverage"] = "entity_limit_reached"
                self.cache[key] = copy.deepcopy(result)
                while len(self.cache) > self.cache_limit:
                    self.cache.popitem(last=False)
            result.update({"ok": True, "schema": SCHEMA, "sourceId": source, "textHash": fingerprint,
                           "modelHash": self.model_hash, "cacheHit": cached, "remoteCalls": 0,
                           "durationMs": round((time.perf_counter() - started) * 1000)})
            self.last_status = "ready"
            return result
        except Exception as exc:
            self.last_status = "unavailable"
            return {"ok": False, "status": "unavailable", "errorType": type(exc).__name__, "remoteCalls": 0}
        finally:
            self.lock.release()

    def extract_claims(self, body):
        """A separate schema; entities/topics are never repurposed as actors."""
        started = time.perf_counter()
        schema_id = "reign-dialogue-claim-extraction-v1"
        text = body.get("text") if isinstance(body, dict) else None
        source = body.get("sourceId") if isinstance(body, dict) else None
        if (not isinstance(text, str) or not text.strip() or len(text) > 2400
                or not isinstance(source, str) or not 1 <= len(source) <= 256
                or body.get("textHash") != text_hash(text)):
            return {"ok": False, "schema": schema_id, "status": "invalid_input", "remoteCalls": 0}
        if not self.model_dir and not self.loader:
            return {"ok": False, "schema": schema_id, "status": "not_configured", "remoteCalls": 0}
        if not self.lock.acquire(blocking=False):
            return {"ok": False, "schema": schema_id, "status": "busy", "remoteCalls": 0}
        try:
            self._load()
            tokenizer = getattr(getattr(self.model, "processor", None), "tokenizer", None)
            if tokenizer is None:
                raise ValueError("tokenizer_unavailable")
            # Reject instead of letting the extraction library clip a decisive clause.
            encoded = tokenizer(text, add_special_tokens=False)["input_ids"]
            if len(encoded) > 256:
                return {"ok": False, "schema": schema_id, "status": "token_limit", "remoteCalls": 0}
            key = text_hash(schema_id + "|" + self.model_hash + "|" + text_hash(text))
            cached = key in self.cache
            if cached:
                result = copy.deepcopy(self.cache[key])
                self.cache.move_to_end(key)
            else:
                raw = self.model.extract_json(text, {"recall_claim": [
                    "speaker::str::The exact person or pronoun credited with saying or asking something",
                    "statement::str::The exact recalled statement or question attributed to that speaker",
                    "addressee::str::The exact explicitly addressed person, if present"]},
                    include_spans=True, include_confidence=True)
                proposals, rejected = [], 0
                for item in (raw.get("recall_claim", []) if isinstance(raw, dict) else [])[:8]:
                    if not isinstance(item, dict):
                        rejected += 1
                        continue
                    fields = {}
                    for name in ("speaker", "statement", "addressee"):
                        value = item.get(name)
                        if not isinstance(value, dict):
                            continue
                        lo, hi, phrase, confidence = value.get("start"), value.get("end"), value.get("text"), value.get("confidence")
                        if (type(lo) is not int or type(hi) is not int or not 0 <= lo < hi <= len(text)
                                or not isinstance(phrase, str) or text[lo:hi] != phrase
                                or type(confidence) not in (int, float) or not math.isfinite(confidence) or not .5 <= confidence <= 1):
                            rejected += 1
                            continue
                        fields[name] = {"text": phrase, "start": utf16_offset(text, lo),
                                        "end": utf16_offset(text, hi), "confidence": confidence}
                    if "speaker" in fields and "statement" in fields:
                        proposals.append(fields)
                    else:
                        rejected += 1
                result = {"claims": proposals, "rejectedSpans": rejected, "offsetEncoding": "utf16",
                          "coverage": "bounded_input_only", "authority": "claim_candidates_only"}
                self.cache[key] = copy.deepcopy(result)
                while len(self.cache) > self.cache_limit:
                    self.cache.popitem(last=False)
            result.update({"ok": True, "schema": schema_id, "sourceId": source, "textHash": text_hash(text),
                           "modelHash": self.model_hash, "remoteCalls": 0, "cacheHit": cached,
                           "durationMs": round((time.perf_counter() - started) * 1000)})
            return result
        except Exception as exc:
            return {"ok": False, "schema": schema_id, "status": "unavailable", "errorType": type(exc).__name__, "remoteCalls": 0}
        finally:
            self.lock.release()
