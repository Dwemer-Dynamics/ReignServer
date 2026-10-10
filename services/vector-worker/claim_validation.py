"""Local, bounded NLI comparisons. Scores are hints, never actor/action authority."""
from __future__ import annotations

import copy
import hashlib
import json
import math
import os
import threading
import time
from collections import OrderedDict
from pathlib import Path

import numpy as np

SCHEMA = "reign-dialogue-claim-validation-v1"
MODEL = "cross-encoder/nli-deberta-v3-xsmall"
REVISION = "a150876415327c80daeff35ca6f68f5ed8cf5c24"
MODEL_HASH = "7105da41f625c42eca24e9465ec99150d02a80e644659d7a1daa93a6357155d4"
TOKENIZER_HASH = "5124ef2ead1a10a717703bc436de7f353da76d6340e4587719b42b1693707964"
LABELS = ("contradiction", "entailment", "neutral")
GROUNDING_MODEL = "lytang/MiniCheck-DeBERTa-v3-Large"
GROUNDING_REVISION = "2f2d01a54fa022a7ffadb76260e1ea8bc88c82bb"
GROUNDING_HASH = "0697ebdc2fab6c085b02f6ad97af8652160f751b3882dc530719c8a7bc58b775"
GROUNDING_WEIGHTS_HASH = "b9480694aa5f5a902833a99f6a91fe3f68e75ce6a27a80ac5ea48c3aa0c42cb2"
GROUNDING_TOKENIZER_HASH = "8bfb874b90f3b3ea9481289bf351eef77462f0935f526ea107f53789708cc5d4"
GROUNDING_SOURCE_HASH = "32ced784f4831d5f324a072e43b96d4072cac4bf41042bc42243605ffbbccbdf"
GROUNDING_LABELS = ("unsupported", "supported")


def text_hash(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def file_hash(path):
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate_pairs(body):
    if not isinstance(body, dict) or body.get("schema") != SCHEMA:
        raise ValueError("invalid_schema")
    budget = body.get("workBudgetMs", 8000)
    if isinstance(budget, bool) or not isinstance(budget, int) or not 500 <= budget <= 15000:
        raise ValueError("invalid_work_budget")
    pairs = body.get("pairs")
    if not isinstance(pairs, list) or not 1 <= len(pairs) <= 8:
        raise ValueError("pair_limit")
    seen, total = set(), 0
    for pair in pairs:
        if not isinstance(pair, dict):
            raise ValueError("invalid_pair")
        ident = pair.get("id")
        if not isinstance(ident, str) or not 1 <= len(ident) <= 256 or ident in seen:
            raise ValueError("invalid_pair_identity")
        seen.add(ident)
        if not isinstance(pair.get("sourceId"), str) or not 1 <= len(pair["sourceId"]) <= 256:
            raise ValueError("invalid_source_identity")
        for field in ("sourceHash", "claimHash"):
            value = pair.get(field)
            if not isinstance(value, str) or len(value) != 64 or any(c not in "0123456789abcdef" for c in value):
                raise ValueError("invalid_source_hash")
        for field in ("premise", "hypothesis"):
            value = pair.get(field)
            if not isinstance(value, str) or not value.strip() or len(value) > 2400:
                raise ValueError("pair_text_limit")
            total += len(value)
            if pair.get(field + "Hash") != text_hash(value):
                raise ValueError("pair_text_hash_mismatch")
    if total > 16000:
        raise ValueError("request_text_limit")
    return pairs


class ClaimValidator:
    def __init__(self, model_dir, loader=None, cache_limit=256, model=None):
        self.selection = model if model is not None else os.environ.get("REIGN_DIALOGUE_CLAIM_MODEL", "nli")
        self.grounding = self.selection == "minicheck"
        # Explicit optional candidate assets. Never silently substitute NLI if
        # a selected MiniCheck model is absent, invalid or unavailable.
        override = os.environ.get("REIGN_DIALOGUE_GROUNDING_DIR", "") if self.grounding else ""
        self.model_dir = Path(override or model_dir)
        self.model_name = GROUNDING_MODEL if self.grounding else MODEL
        self.model_hash = GROUNDING_HASH if self.grounding else MODEL_HASH
        self.tokenizer_hash = GROUNDING_TOKENIZER_HASH if self.grounding else TOKENIZER_HASH
        self.labels = GROUNDING_LABELS if self.grounding else LABELS
        self.token_limit = 256 if self.grounding else 512
        self.loader = loader
        self.session = self.tokenizer = None
        self.lock = threading.Lock()
        self.cache = OrderedDict()
        self.cache_limit = max(1, min(512, cache_limit))
        self.last_status = "not_loaded"

    def status(self):
        return {"schema": SCHEMA, "model": self.model_name, "modelHash": self.model_hash,
                "tokenizerHash": self.tokenizer_hash, "modelLoaded": self.session is not None,
                "lastStatus": self.last_status, "backend": "cpu", "remoteCalls": 0,
                "modelSelection": self.selection, "scoringKind": "grounding_binary" if self.grounding else "nli_three_way",
                "weightsHash": GROUNDING_WEIGHTS_HASH if self.grounding else "",
                "semanticAccuracyCertified": False, "inputTokenLimit": self.token_limit,
                "authority": "comparison_hint_only", "cachedPairs": len(self.cache)}

    def warmup(self):
        if not self.lock.acquire(blocking=False):
            return
        try:
            self._load()
            self.last_status = "ready"
        except Exception:
            self.last_status = "unavailable"
        finally:
            self.lock.release()

    def _load(self):
        if self.session is not None:
            return
        if self.selection not in ("nli", "minicheck"):
            raise ValueError("disabled" if self.selection == "off" else "unknown_model_selection")
        if self.loader:
            self.session, self.tokenizer = self.loader()
            return
        graph = self.model_dir / ("dialogue-minicheck.onnx" if self.grounding else "dialogue-nli.onnx")
        tokens = self.model_dir / ("dialogue-minicheck-tokenizer.json" if self.grounding else "dialogue-nli-tokenizer.json")
        if not graph.is_file() or not tokens.is_file():
            raise ValueError("local_model_not_available")
        if graph.is_symlink() or tokens.is_symlink() or file_hash(graph) != self.model_hash or file_hash(tokens) != self.tokenizer_hash:
            raise ValueError("unqualified_model_files")
        if self.grounding:
            weights = self.model_dir / "dialogue-minicheck.weights"
            if weights.is_symlink() or not weights.is_file() or file_hash(weights) != GROUNDING_WEIGHTS_HASH:
                raise ValueError("unqualified_model_weights")
        import onnxruntime as ort
        from tokenizers import Tokenizer
        options = ort.SessionOptions()
        options.intra_op_num_threads = 2
        options.inter_op_num_threads = 1
        if self.grounding:
            # Keep the tested external FP32 weights; no implicit quantization.
            options.graph_optimization_level = ort.GraphOptimizationLevel.ORT_ENABLE_BASIC
        self.session = ort.InferenceSession(str(graph), sess_options=options, providers=["CPUExecutionProvider"])
        self.tokenizer = Tokenizer.from_file(str(tokens))
        self.tokenizer.no_truncation()
        self.tokenizer.no_padding()
        if self.session.get_providers() != ["CPUExecutionProvider"]:
            self.session = self.tokenizer = None
            raise ValueError("unexpected_backend")

    def evaluate(self, body):
        started = time.perf_counter()
        try:
            pairs = validate_pairs(body)
        except (ValueError, TypeError) as exc:
            return {"ok": False, "schema": SCHEMA, "status": str(exc), "remoteCalls": 0}
        if not self.lock.acquire(blocking=False):
            return {"ok": False, "schema": SCHEMA, "status": "busy", "remoteCalls": 0}
        try:
            was_loaded = self.session is not None
            self._load()
            loaded = time.perf_counter()
            # Warm-up is reported separately. Callers still enforce a total wall
            # deadline; an excessive cold load cannot authorize a late effect.
            budget = body.get("workBudgetMs", 8000) / 1000
            rows = []
            for pair in pairs:
                identity = self.model_hash + "|" + self.tokenizer_hash + "|" + pair["premiseHash"] + "|" + pair["hypothesisHash"]
                key = text_hash(identity)
                cached = key in self.cache
                if cached:
                    row = copy.deepcopy(self.cache[key])
                    self.cache.move_to_end(key)
                else:
                    # MiniCheck's document + EOS + claim format differs from a
                    # generic NLI pair. Tokenization must preserve that format.
                    encoded = self.tokenizer.encode(pair["premise"] + "[SEP]" + pair["hypothesis"]) if self.grounding else self.tokenizer.encode(pair["premise"], pair["hypothesis"])
                    if len(encoded.ids) > self.token_limit:
                        row = {"status": "token_limit", "outcome": "unresolved", "coverage": "not_scored"}
                    elif time.perf_counter() - loaded > budget:
                        row = {"status": "time_budget", "outcome": "unresolved", "coverage": "not_scored"}
                    else:
                        logits = self._infer_logits(encoded)
                        if not isinstance(logits, list) or len(logits) != len(self.labels) or not all(
                                isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) for value in logits):
                            raise ValueError("invalid_model_scores")
                        maximum = max(logits)
                        exponentials = [math.exp(value - maximum) for value in logits]
                        total = sum(exponentials)
                        probabilities = [value / total for value in exponentials]
                        scores = {label: float(probabilities[i]) for i, label in enumerate(self.labels)}
                        label = self.labels[max(range(len(probabilities)), key=probabilities.__getitem__)]
                        outcome = ("supported" if scores["supported"] >= .9 else "unsupported" if scores["unsupported"] >= .9 else "unresolved") if self.grounding else ("supported" if scores["entailment"] >= .9 else "contradicted" if scores["contradiction"] >= .9 else "unresolved")
                        row = {"status": "scored", "coverage": "complete_pair", "label": label,
                               "outcome": outcome, "scores": scores, "inputTokens": len(encoded.ids)}
                        # A slow inference is not a successful timely check. The
                        # caller deadline cannot cancel an ONNX kernel mid-call.
                        if time.perf_counter() - loaded > budget:
                            row = {"status": "late_scored", "outcome": "unresolved", "coverage": "not_scored",
                                   "diagnosticScores": scores, "diagnosticLabel": label, "inputTokens": len(encoded.ids)}
                        else:
                            self.cache[key] = copy.deepcopy(row)
                            while len(self.cache) > self.cache_limit:
                                self.cache.popitem(last=False)
                row.update({k: pair[k] for k in ("id", "sourceId", "sourceHash", "claimHash", "premiseHash", "hypothesisHash")})
                row["cacheHit"] = cached
                row["authority"] = "comparison_hint_only"
                rows.append(row)
            self.last_status = "ready"
            return {"ok": True, "schema": SCHEMA, "modelHash": self.model_hash, "tokenizerHash": self.tokenizer_hash,
                    "scoringKind": "grounding_binary" if self.grounding else "nli_three_way",
                    "weightsHash": GROUNDING_WEIGHTS_HASH if self.grounding else "",
                    "backend": "cpu", "rows": rows, "remoteCalls": 0,
                    "durationMs": round((time.perf_counter() - started) * 1000),
                    "loadDurationMs": round((loaded - started) * 1000), "coldLoad": not was_loaded,
                    "workBudgetMs": round(budget * 1000),
                    "complete": all(row["status"] == "scored" for row in rows)}
        except Exception as exc:
            self.last_status = "unavailable"
            return {"ok": False, "schema": SCHEMA, "status": "unavailable", "errorType": type(exc).__name__, "remoteCalls": 0}
        finally:
            self.lock.release()

    def _infer_logits(self, encoded):
        inputs = {"input_ids": np.asarray([encoded.ids], dtype=np.int64),
                  "attention_mask": np.asarray([encoded.attention_mask], dtype=np.int64),
                  "token_type_ids": np.asarray([encoded.type_ids], dtype=np.int64)}
        names = {entry.name for entry in self.session.get_inputs()}
        return np.asarray(self.session.run(None, {k: v for k, v in inputs.items() if k in names})[0])[0].tolist()
