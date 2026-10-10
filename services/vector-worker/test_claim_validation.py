"""Contracts for source binding/failure handling; semantic quality is measured separately."""
import copy
import tempfile
import unittest
from unittest.mock import patch
from types import SimpleNamespace
from claim_validation import (ClaimValidator, SCHEMA, GROUNDING_HASH,
                              GROUNDING_WEIGHTS_HASH, text_hash, validate_pairs)
from prepare_claim_grounding import check_source
from pathlib import Path
from specialists import MemoryExtractor


def pair(ident="one", source="source", premise="Mira said stop talking about the future.", hypothesis="Mira said stop talking about the future."):
    return {"id": ident, "sourceId": source, "sourceHash": text_hash(premise), "claimHash": text_hash(hypothesis),
            "premise": premise, "hypothesis": hypothesis, "premiseHash": text_hash(premise), "hypothesisHash": text_hash(hypothesis)}


class ClaimValidationContracts(unittest.TestCase):
    @staticmethod
    def fake_loader(logits=(5., -5.), tokens=25):
        class Session:
            calls = 0
            def get_inputs(self):
                return [SimpleNamespace(name="input_ids"), SimpleNamespace(name="attention_mask")]
            def run(self, *_):
                self.calls += 1
                return [[list(logits)]]
        class Tokenizer:
            arguments = None
            def encode(self, *args):
                self.arguments = args
                return SimpleNamespace(ids=[1] * tokens, attention_mask=[1] * tokens, type_ids=[0] * tokens)
        session, tokenizer = Session(), Tokenizer()
        return session, tokenizer, lambda: (session, tokenizer)

    @classmethod
    def fake_model(cls, logits=(5., -5.), tokens=25):
        session, tokenizer, loader = cls.fake_loader(logits, tokens)
        model = ClaimValidator("missing", loader=loader, model="minicheck")
        # Isolate ONNX array transport, not the real score validation, softmax,
        # deadlines, source bindings, cache or result publication. Lean canonical
        # contracts deliberately run without installed inference dependencies.
        model._infer_logits = lambda encoded: session.run()[0][0]
        return session, tokenizer, model

    def test_grounding_binary_not_contradiction_and_cache_rebinds_source(self):
        session, tokenizer, model = self.fake_model()
        one = model.evaluate({"schema": SCHEMA, "pairs": [pair()]})
        self.assertTrue(one["ok"])
        self.assertEqual(one["rows"][0]["outcome"], "unsupported")
        self.assertEqual(set(one["rows"][0]["scores"]), {"supported", "unsupported"})
        self.assertEqual(one["scoringKind"], "grounding_binary")
        self.assertEqual(one["modelHash"], GROUNDING_HASH)
        self.assertEqual(one["weightsHash"], GROUNDING_WEIGHTS_HASH)
        self.assertEqual(tokenizer.arguments, (pair()["premise"] + "[SEP]" + pair()["hypothesis"],))
        one["rows"].clear()
        two = model.evaluate({"schema": SCHEMA, "pairs": [pair("two", "another")]})
        self.assertTrue(two["rows"][0]["cacheHit"])
        self.assertEqual(two["rows"][0]["sourceId"], "another")
        self.assertEqual(session.calls, 1)
        self.assertFalse(model.status()["semanticAccuracyCertified"])
        self.assertEqual(two["rows"][0]["authority"], "comparison_hint_only")

    def test_grounding_uncertainty_and_bad_scores_never_pass(self):
        for logits, expected in (((0., 0.), "unresolved"), ((-5., 5.), "supported")):
            _, _, model = self.fake_model(logits)
            result = model.evaluate({"schema": SCHEMA, "pairs": [pair()]})
            self.assertEqual(result["rows"][0]["outcome"], expected)
        for logits in ((1., 2., 3.), (float("nan"), 0.), (float("inf"), 0.)):
            _, _, model = self.fake_model(logits)
            self.assertFalse(model.evaluate({"schema": SCHEMA, "pairs": [pair()]})["ok"])

    def test_grounding_complete_input_bound_and_late_result_abstention(self):
        session, _, model = self.fake_model(tokens=257)
        result = model.evaluate({"schema": SCHEMA, "pairs": [pair()]})
        self.assertEqual(result["rows"][0]["status"], "token_limit")
        self.assertEqual(result["rows"][0]["coverage"], "not_scored")
        self.assertFalse(result["complete"])
        self.assertEqual(session.calls, 0)
        _, _, model = self.fake_model()
        # Start, loaded, before inference, after inference, response duration.
        with patch("claim_validation.time.perf_counter", side_effect=[0., .1, .2, 8.2, 8.3]):
            late = model.evaluate({"schema": SCHEMA, "pairs": [pair()]})
        self.assertEqual(late["rows"][0]["outcome"], "unresolved")
        self.assertEqual(late["rows"][0]["status"], "late_scored")
        self.assertIn("diagnosticScores", late["rows"][0])
        self.assertEqual(len(model.cache), 0)

    def test_budget_validation_and_load_separated_from_work(self):
        for budget in (True, 0, 499, 15001, 8000., "8000"):
            with self.assertRaises(ValueError): validate_pairs({"schema": SCHEMA, "pairs": [pair()], "workBudgetMs": budget})
        session, _, model = self.fake_model()
        with patch("claim_validation.time.perf_counter", side_effect=[0., 4., 4.1, 4.4, 4.5]):
            result = model.evaluate({"schema": SCHEMA, "pairs": [pair()], "workBudgetMs": 500})
        self.assertEqual(result["rows"][0]["status"], "scored")
        self.assertEqual(result["loadDurationMs"], 4000)
        self.assertTrue(result["coldLoad"])
        self.assertEqual(session.calls, 1)
        _, _, model = self.fake_model()
        with patch("claim_validation.time.perf_counter", side_effect=[0., 0., .6, .7]):
            expired = model.evaluate({"schema": SCHEMA, "pairs": [pair()], "workBudgetMs": 500})
        self.assertEqual(expired["rows"][0]["status"], "time_budget")

    def test_disabled_unknown_and_missing_candidate_never_fallback(self):
        def forbidden(): raise AssertionError("disabled/unknown must not load")
        for selected in ("off", "typo"):
            model = ClaimValidator("missing", loader=forbidden, model=selected)
            self.assertFalse(model.evaluate({"schema": SCHEMA, "pairs": [pair()]})["ok"])
        with tempfile.TemporaryDirectory() as root:
            model = ClaimValidator(root, model="minicheck")
            self.assertFalse(model.evaluate({"schema": SCHEMA, "pairs": [pair()]})["ok"])
            self.assertEqual(model.status()["modelSelection"], "minicheck")
            self.assertEqual(model.status()["modelHash"], GROUNDING_HASH)
            with self.assertRaises(ValueError): check_source(Path(root))

    def test_identity_bounds_reject_before_model_loading(self):
        def forbidden(): raise AssertionError("invalid request must not load a model")
        model = ClaimValidator("missing", loader=forbidden)
        for body in (None, [], {}, {"schema": SCHEMA, "pairs": []}, {"schema": SCHEMA, "pairs": [pair()] * 9},
                     {"schema": SCHEMA, "pairs": [pair(), pair()]}, {"schema": SCHEMA, "pairs": [{**pair(), "sourceHash": "invalid"}]},
                     {"schema": SCHEMA, "pairs": [{**pair(), "hypothesis": "changed"}]},
                     {"schema": SCHEMA, "pairs": [pair(premise="x" * 2401)]}):
            self.assertFalse(model.evaluate(body)["ok"])

    def test_missing_and_busy_helpers_are_not_verification(self):
        with tempfile.TemporaryDirectory() as root:
            model = ClaimValidator(root)
            self.assertFalse(model.evaluate({"schema": SCHEMA, "pairs": [pair()]})["ok"])
            model.lock.acquire()
            try:
                self.assertEqual(model.evaluate({"schema": SCHEMA, "pairs": [pair()]})["status"], "busy")
            finally:
                model.lock.release()

    def test_exact_hashes_and_multiple_sources_are_required(self):
        original = pair()
        request = {"schema": SCHEMA, "pairs": [original, pair("two", "another")]}
        self.assertEqual(len(validate_pairs(request)), 2)
        for field in ("premiseHash", "hypothesisHash", "sourceId", "id", "claimHash"):
            bad = copy.deepcopy(request)
            bad["pairs"][0][field] = ""
            with self.assertRaises(ValueError): validate_pairs(bad)

    def test_scores_do_not_create_actor_or_action_authority(self):
        status = ClaimValidator("missing").status()
        self.assertEqual(status["authority"], "comparison_hint_only")
        self.assertEqual(status["backend"], "cpu")
        self.assertEqual(status["remoteCalls"], 0)
        self.assertFalse(status["modelLoaded"])

    def test_claim_extractor_unconfigured_and_mismatched_hash(self):
        model = MemoryExtractor()
        model.model_dir = ""
        body = {"sourceId": "draft", "text": "You said stop talking about the future.", "textHash": text_hash("You said stop talking about the future.")}
        self.assertEqual(model.extract_claims(body)["status"], "not_configured")
        self.assertEqual(model.extract_claims({**body, "textHash": "changed"})["status"], "invalid_input")

    def test_claim_extractor_span_identity_utf16_cache_and_busy(self):
        class Fake:
            processor = SimpleNamespace(tokenizer=lambda text, **_: {"input_ids": [1] * len(text.split())})
            def extract_json(self, text, schema, **kwargs):
                def span(value):
                    start = text.index(value)
                    return {"text": value, "start": start, "end": start + len(value), "confidence": .99}
                return {"recall_claim": [{"speaker": span("You"), "statement": span("stop talking about the future")},
                                         {"speaker": {"text": "Mira", "start": 0, "end": 4, "confidence": 1}, "statement": span("stop talking about the future")} ]}
        model = MemoryExtractor(loader=Fake)
        text = "🌿 You said stop talking about the future."
        body = {"sourceId": "one", "text": text, "textHash": text_hash(text)}
        first = model.extract_claims(body)
        self.assertEqual(first["claims"][0]["speaker"]["start"], 3)
        self.assertEqual(len(first["claims"]), 1)
        self.assertGreater(first["rejectedSpans"], 0)
        first["claims"].clear()
        second = model.extract_claims({**body, "sourceId": "two"})
        self.assertEqual(len(second["claims"]), 1)
        self.assertEqual(second["sourceId"], "two")
        self.assertTrue(second["cacheHit"])
        self.assertEqual(second["authority"], "claim_candidates_only")
        model.lock.acquire()
        try: self.assertEqual(model.extract_claims(body)["status"], "busy")
        finally: model.lock.release()
