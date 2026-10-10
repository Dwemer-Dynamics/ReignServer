"""Provider-free index and failure contracts; model quality is evaluated separately."""
import copy
import io
import json
import sqlite3
import tempfile
import threading
import unittest
from unittest.mock import patch
from contextlib import contextmanager, closing
from pathlib import Path

import numpy as np
from late_interaction import LateInteractionIndex, LocalInferenceGate, SCHEMA, MODEL_HASH, text_hash, source_key, maxsim_score


class FixtureEncoder:
    def __init__(self):
        self.calls = 0

    def windows(self, text, query=False):
        # Multiple windows exercise atomic publication and priority release.
        return [(text[i:i+12], i, min(i+12, len(text))) for i in range(0, len(text), 12)]

    def encode_window(self, tokens, query=False):
        self.calls += 1
        vector = np.zeros((1, 128), dtype=np.float32)
        vector[0, 0 if "rare" in tokens else 1] = 1
        return vector


class LateInteractionContracts(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.encoder = FixtureEncoder()
        self.gates = []
        @contextmanager
        def gate(foreground):
            self.gates.append(foreground)
            yield
        self.index = LateInteractionIndex(Path(self.temp.name), Path(self.temp.name)/"no-model",
                                         operation_gate=gate, encoder_factory=lambda: self.encoder)

    def tearDown(self):
        self.temp.cleanup()

    def request(self, **values):
        return {"schema": SCHEMA, "modelHash": MODEL_HASH, "campaignId": "fixture", **values}

    def source(self, sid, text="ordinary", view="public", revision="a"):
        return {"sourceId": sid, "text": text, "sourceHash": revision*64, "textHash": text_hash(text), "view": view}

    def search(self, sources, **values):
        eligible = [{k: s[k] for k in ("sourceId", "sourceHash", "textHash", "view")} for s in sources]
        return self.index.search(self.request(query="rare", eligible=eligible, **values))

    def test_worker_colbert_search_does_not_wait_for_unrelated_archive_maintenance(self):
        from worker import WorkerState
        state = WorkerState(Path(self.temp.name)/"independent-worker")
        state.late_interaction.factory = lambda: FixtureEncoder()
        source = self.source("original", "rare")
        completed = threading.Event()
        outputs = []
        def run():
            try:
                outputs.append(state.late_interaction.index(self.request(sources=[source])))
                outputs.append(state.late_interaction.search(self.request(query="rare", eligible=[source], limit=1)))
            finally:
                completed.set()
        with state.operation_gate(False):
            thread = threading.Thread(target=run, daemon=True)
            thread.start()
            independent = completed.wait(3)
        thread.join(5)
        self.assertTrue(independent, "ColBERT was blocked by unrelated vector-store maintenance")
        self.assertEqual(len(outputs), 2)
        self.assertTrue(all(item["ok"] for item in outputs), outputs)
        self.assertEqual(outputs[1]["results"][0]["sourceId"], "original")

    def test_local_inference_foreground_precedes_queued_background(self):
        gate = LocalInferenceGate(foreground_timeout=3)
        order = []
        def run(name, foreground):
            with gate(foreground): order.append(name)
        with gate(False):
            background = threading.Thread(target=run, args=("background", False), daemon=True)
            foreground = threading.Thread(target=run, args=("foreground", True), daemon=True)
            background.start(); foreground.start()
            with gate.condition:
                queued = gate.condition.wait_for(lambda: gate.foreground_waiters == 1, timeout=2)
        foreground.join(5); background.join(5)
        self.assertTrue(queued)
        self.assertEqual(order, ["foreground", "background"])

    def test_local_inference_timeout_releases_waiter_without_releasing_active_owner(self):
        gate = LocalInferenceGate(foreground_timeout=0)
        with gate(False):
            with self.assertRaisesRegex(TimeoutError, "colbert_inference_busy"):
                with gate(True): self.fail("busy inference must not enter")
            self.assertTrue(gate.status()["active"])
            self.assertEqual(gate.status()["foregroundWaiters"], 0)
        with gate(True): self.assertTrue(gate.status()["active"])
        self.assertFalse(gate.status()["active"])

    def test_eligible_collection_discovery_not_lexical_candidates(self):
        sources = [self.source("lexical", "ordinary"), self.source("old-original", "rare")]
        self.assertTrue(self.index.index(self.request(sources=sources))["ok"])
        result = self.search(sources, limit=1)
        self.assertEqual(result["results"][0]["sourceId"], "old-original")
        self.assertEqual(result["indexedCount"], 2)
        self.assertEqual(result["status"], "ready")

    def test_private_views_campaigns_and_missing_allowlist_cannot_influence_rank(self):
        own, public = self.source("same", "rare secret", "self"), self.source("same", "ordinary", "public")
        other = self.source("outsider", "rare")
        self.assertTrue(self.index.index(self.request(sources=[own, public, other]))["ok"])
        result = self.search([public])
        self.assertEqual([(r["sourceId"],r["view"]) for r in result["results"]], [("same","public")])
        self.assertEqual(result["results"][0]["score"], 0)
        denied = self.index.search({**self.request(query="rare", eligible=[own]), "campaignId": "other"})
        self.assertEqual(denied["indexedCount"], 0)
        self.assertEqual(self.search([])["results"], [])

    def test_stale_source_and_text_hashes_excluded_before_top_k(self):
        source = self.source("source", "rare")
        self.index.index(self.request(sources=[source]))
        for key in ("sourceHash", "textHash"):
            with self.subTest(key=key):
                stale = dict(source, **{key:"b"*64})
                self.assertEqual(self.search([stale])["indexedCount"], 0)

    def test_canonical_uppercase_source_fence_round_trips_unchanged(self):
        source = self.source("canonical-source", "rare", revision="A")
        indexed = self.index.index(self.request(sources=[source]))
        self.assertTrue(indexed["ok"])
        result = self.search([source])
        self.assertTrue(result["ok"])
        self.assertEqual(result["results"][0]["sourceHash"], "A"*64)
        # The worker must not invent a replacement fence or accept a changed one.
        self.assertEqual(self.search([dict(source, sourceHash="B"*64)])["indexedCount"], 0)

    def test_cache_persists_restart_and_generation_identity(self):
        source = self.source("source", "rare"*8)
        first = self.index.index(self.request(sources=[source]))
        calls = self.encoder.calls
        second = self.index.index(self.request(sources=[source]))
        self.assertEqual(self.encoder.calls, calls)
        self.assertTrue(second["sources"][0]["cached"])
        again = LateInteractionIndex(Path(self.temp.name), Path(self.temp.name)/"no-model", encoder_factory=lambda:self.encoder)
        self.assertEqual(first["indexGeneration"], again.status()["indexGeneration"])
        self.index = again
        self.assertEqual(self.search([source])["status"], "ready")

    def test_replacing_source_replaces_all_windows_atomically(self):
        old = self.source("source", "ordinary"*9)
        new = self.source("source", "rare", revision="b")
        self.index.index(self.request(sources=[old]))
        self.index.index(self.request(sources=[new]))
        self.assertEqual(self.search([old])["indexedCount"], 0)
        self.assertEqual(self.search([new])["results"][0]["score"], 1)
        with closing(sqlite3.connect(self.index.path)) as db:
            self.assertEqual(db.execute("SELECT count(*) FROM chunks").fetchone()[0], 1)

    def test_failed_update_retains_prior_complete_source(self):
        source = self.source("source", "rare")
        self.index.index(self.request(sources=[source]))
        previous = self.encoder.encode_window
        def broken(*args, **kwargs):
            raise RuntimeError("inference failed")
        self.encoder.encode_window = broken
        result = self.index.index(self.request(sources=[self.source("source", "ordinary"*9, revision="b")]))
        self.encoder.encode_window = previous
        self.assertFalse(result["ok"])
        self.assertEqual(self.search([source])["results"][0]["score"], 1)

    def test_late_window_and_priority_boundaries(self):
        source = self.source("source", "ordinary...."*12 + "rare")
        indexed = self.index.index(self.request(sources=[source]))
        self.assertGreater(indexed["sources"][0]["chunks"], 1)
        self.assertGreater(self.gates.count(False), 12)
        self.assertEqual(self.search([source])["results"][0]["score"], 1)

    def test_invalid_batch_changes_nothing(self):
        source = self.source("source", "rare")
        malformed = self.source("bad", "rare")
        malformed["textHash"] = "b"*64
        self.assertFalse(self.index.index(self.request(sources=[source, malformed]))["ok"])
        self.assertFalse(self.index.path.exists())
        for invalid in [dict(source, view="other-person"), dict(source, sourceId=""), dict(source, sourceHash="bad")]:
            with self.assertRaises(ValueError):
                source_key(invalid)

    def test_missing_index_and_model_do_not_trigger_download_or_pass(self):
        source = self.source("source")
        self.assertEqual(self.search([source])["status"], "index_not_ready")
        self.assertEqual(self.encoder.calls, 0)
        self.index.factory = lambda: (_ for _ in ()).throw(FileNotFoundError("absent"))
        result = self.index.index(self.request(sources=[source]))
        self.assertFalse(result["ok"])
        self.assertEqual(result["errorType"], "FileNotFoundError")

    def test_partial_coverage_is_explicit(self):
        source, absent = self.source("source", "rare"), self.source("absent", "rare")
        self.index.index(self.request(sources=[source]))
        result = self.search([source, absent])
        self.assertEqual((result["status"], result["eligibleCount"], result["indexedCount"]), ("partial", 2, 1))

    def test_corrupt_vectors_and_model_revision_do_not_return_results(self):
        source = self.source("source", "rare")
        self.index.index(self.request(sources=[source]))
        with closing(sqlite3.connect(self.index.path)) as db:
            db.execute("UPDATE chunks SET vectors=?", (b"short",))
            db.commit()
        self.assertFalse(self.search([source])["ok"])
        self.assertEqual(self.search([source])["results"], [])
        result = self.index.search({**self.request(query="rare", eligible=[source]), "modelHash":"b"*64})
        self.assertFalse(result["ok"])

    def test_bounds_never_silently_clip_eligibility(self):
        source = self.source("source", "rare")
        self.assertFalse(self.search([source, source])["ok"])
        self.assertFalse(self.index.search(self.request(query="rare",eligible=[source]*50001))["ok"])
        self.index.index(self.request(sources=[source]))
        self.assertFalse(self.search([source], limit=0)["ok"])

    def test_tiled_maxsim_matches_complete_reference_and_keeps_query_tail(self):
        rng = np.random.default_rng(927)
        document = rng.normal(size=(180, 128)).astype(np.float32)
        document /= np.linalg.norm(document, axis=1, keepdims=True)
        for length in (1, 31, 32, 33, 64, 127, 256, 500):
            query = rng.normal(size=(length, 128)).astype(np.float32)
            query /= np.linalg.norm(query, axis=1, keepdims=True)
            reference = float((document @ query.T).max(axis=0).sum())
            self.assertAlmostEqual(maxsim_score(document, query), reference, delta=0.00005)
        query = np.zeros((500, 128), dtype=np.float32)
        query[-1, 127] = 1
        document = np.zeros((180, 128), dtype=np.float32)
        document[-1, 127] = 1
        self.assertEqual(maxsim_score(document, query), 1)

    def test_long_query_preserves_source_ranking_and_deterministic_ties(self):
        sources = [self.source(sid, "rare") for sid in ("z", "a")] + [self.source("unrelated")]
        self.index.index(self.request(sources=sources))
        query = np.zeros((500, 128), dtype=np.float32)
        query[-1, 0] = 1
        self.index.query_cache["rare"] = query
        result = self.search(sources)
        self.assertTrue(result["ok"], result)
        self.assertEqual([row["sourceId"] for row in result["results"]], ["a", "z", "unrelated"])
        self.assertEqual(result["timing"]["queryTokens"], 500)
        self.assertEqual(result["timing"]["scoredSources"], 3)

    def test_missing_later_chunk_cannot_claim_complete_source_coverage(self):
        source = self.source("source", "ordinary...." * 4 + "rare")
        self.assertTrue(self.index.index(self.request(sources=[source]))["ok"])
        with closing(sqlite3.connect(self.index.path)) as db:
            db.execute("DELETE FROM chunks WHERE ordinal=(SELECT MAX(ordinal) FROM chunks)")
            db.commit()
        result = self.search([source])
        self.assertEqual((result["ok"], result["results"], result["reason"]), (False, [], "incomplete_index_chunks"))

    def test_timeout_has_work_receipt_and_never_publishes_partial_ranks(self):
        sources = [self.source("first", "rare"), self.source("second", "rare")]
        self.index.index(self.request(sources=sources))
        clock = [0.0]
        def score(document, query):
            value = maxsim_score(document, query)
            clock[0] += 2.1
            return value
        with patch("late_interaction.time.perf_counter", side_effect=lambda: clock[0]), patch("late_interaction.maxsim_score", side_effect=score):
            result = self.search(sources)
        self.assertEqual((result["ok"], result["status"], result["results"]), (False, "search_budget_exceeded", []))
        self.assertEqual(result["timing"]["scoreBudgetMs"], 2000)
        self.assertEqual(result["timing"]["scoredChunks"], 1)
        self.assertGreaterEqual(result["timing"]["scoreMs"], 2100)
        self.assertEqual(self.index.status()["timing"], result["timing"])

    def test_transient_index_busy_and_timeout_do_not_become_content_failures(self):
        with self.index.index_lock:
            result = self.index.index(self.request(sources=[self.source("source")]))
        self.assertEqual((result["status"], result["retryable"]), ("index_busy", True))
        self.index.factory = lambda: (_ for _ in ()).throw(TimeoutError("colbert_inference_busy"))
        result = self.index.index(self.request(sources=[self.source("source")]))
        self.assertEqual((result["status"], result["retryable"]), ("index_unavailable", True))
        self.index.factory = lambda: (_ for _ in ()).throw(ValueError("colbert_model_hash_mismatch"))
        result = self.index.index(self.request(sources=[self.source("source")]))
        self.assertFalse(result["retryable"])

    def test_invalid_query_vectors_are_rejected_without_ranks(self):
        source = self.source("source", "rare")
        self.index.index(self.request(sources=[source]))
        for query in (np.zeros((501,128), dtype=np.float32), np.zeros((32,127), dtype=np.float32),
                      np.full((32,128), np.nan, dtype=np.float32)):
            self.index.query_cache["rare"] = query
            result = self.search([source])
            self.assertEqual((result["ok"], result["reason"], result["results"]), (False, "invalid_query_vectors", []))

    def test_eligible_identity_lookup_scales_without_quadratic_sql_work(self):
        work = []
        for count in (20, 200):
            index = LateInteractionIndex(Path(self.temp.name)/str(count), Path(self.temp.name)/"no-model",
                                         encoder_factory=lambda: self.encoder)
            sources = [self.source("source-"+str(i), "rare flower") for i in range(count)]
            for offset in range(0, count, 8):
                self.assertTrue(index.index(self.request(sources=sources[offset:offset+8]))["ok"])
            operations = [0]
            original_connection = index._connection
            def tracked_connection(create=False):
                db = original_connection(create)
                def progress():
                    operations[0] += 100
                    return 0
                db.set_progress_handler(progress, 100)
                return db
            index._connection = tracked_connection
            eligible = [{k:s[k] for k in ("sourceId", "sourceHash", "textHash", "view")} for s in sources]
            found = index.search(self.request(query="rare", eligible=eligible, limit=1))
            self.assertTrue(found["ok"])
            self.assertEqual(found["indexedCount"], count)
            work.append(operations[0])
        # Deterministic SQLite VM work, not a machine-dependent timing assertion.
        self.assertLess(work[1], work[0]*25+100)


def run_contracts():
    stream = io.StringIO()
    result = unittest.TextTestRunner(stream=stream).run(unittest.defaultTestLoader.loadTestsFromTestCase(LateInteractionContracts))
    if not result.wasSuccessful():
        raise ValueError(stream.getvalue())
    return {"schema": SCHEMA, "passed": result.testsRun, "networkCalls": 0}


if __name__ == "__main__":
    unittest.main()
