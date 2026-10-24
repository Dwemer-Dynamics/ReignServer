"""Provider-free contracts for the isolated experiment's evidence boundaries."""
import copy
import json
from pathlib import Path
import tempfile
import unittest
from compare import Budget, Codex, Corpus, dump, stats, summarize


class FakeEmbedder:
    metadata = {"model": "test_only", "dimensions": 2}

    def encode(self, texts):
        return [[1.0, 0.0] if "horse" in text else [0.0, 1.0] for text in texts]


class EvidenceContracts(unittest.TestCase):
    def fixture(self):
        return {"scope": {"npcId": "npc", "peerId": "player", "asOfSequence": 5}, "records": [
            {"sourceId": "known", "speakerName": "Player", "text": "The horse watched the stream.", "audience": ["npc", "player"], "sequence": 2},
            {"sourceId": "private", "speakerName": "Other", "text": "The secret witness was Jorin.", "audience": ["other", "player"], "sequence": 3},
            {"sourceId": "future", "speakerName": "Player", "text": "A future horse race.", "audience": ["npc", "player"], "sequence": 6}]}

    def corpus(self, root, fixture=None):
        return Corpus(fixture or self.fixture(), FakeEmbedder(), Path(root) / "index.json")

    def test_search_and_read_cannot_leak_foreign_or_future_records(self):
        with tempfile.TemporaryDirectory() as root:
            corpus = self.corpus(root)
            results, _ = corpus.search("secret horse witness", 8)
            self.assertEqual([r["sourceId"] for r in results], ["known"])
            for inaccessible in ("private", "future", "missing"):
                with self.assertRaises(ValueError):
                    corpus.read([inaccessible])

    def test_scope_cannot_be_overridden_by_model(self):
        with tempfile.TemporaryDirectory() as root:
            corpus = self.corpus(root)
            with self.assertRaises(ValueError):
                corpus.tool("search_memories", {"query": "horse", "npcId": "other"})
            with self.assertRaises(ValueError):
                corpus.tool("shell", {"query": "horse"})

    def test_reads_preserve_whole_source_and_attribution(self):
        with tempfile.TemporaryDirectory() as root:
            fixture = self.fixture()
            fixture["records"][0]["text"] = "horse " + "long historical evidence " * 200
            corpus = self.corpus(root, fixture)
            result = corpus.search("horse")[0][0]
            self.assertEqual(result["text"], fixture["records"][0]["text"])
            self.assertEqual(result["speakerName"], "Player")
            self.assertGreater(len(corpus.index["parents"]), 1)

    def test_modified_corpus_cannot_reuse_index(self):
        with tempfile.TemporaryDirectory() as root:
            self.corpus(root)
            fixture = self.fixture()
            fixture["records"][0]["text"] = "tampered"
            with self.assertRaises(ValueError):
                self.corpus(root, fixture)

    def test_duplicate_ids_fail_before_embedding(self):
        fixture = self.fixture()
        fixture["records"].append(copy.deepcopy(fixture["records"][0]))
        with tempfile.TemporaryDirectory() as root, self.assertRaises(ValueError):
            self.corpus(root, fixture)

    def test_lookup_argument_bounds(self):
        with tempfile.TemporaryDirectory() as root:
            corpus = self.corpus(root)
            for query, limit in (("", 6), ("x" * 1201, 6), ("horse", 9), ("horse", True)):
                with self.assertRaises(ValueError):
                    corpus.search(query, limit)
            with self.assertRaises(ValueError):
                corpus.read([])

    def test_budget_reserves_before_dispatch(self):
        budget = Budget(2)
        budget.reserve()
        budget.reserve()
        with self.assertRaises(RuntimeError):
            budget.reserve()
        self.assertEqual(budget.used, 2)

    def test_durable_budget_cannot_reset_or_expand_after_restart(self):
        with tempfile.TemporaryDirectory() as root:
            ledger = Path(root) / "budget.json"
            Budget(2, ledger).reserve()
            recovered = Budget(2, ledger)
            self.assertEqual(recovered.used, 1)
            recovered.reserve()
            with self.assertRaises(RuntimeError):
                Budget(2, ledger).reserve()
            with self.assertRaises(ValueError):
                Budget(3, ledger)

    def test_invalid_tool_argument_can_recover_with_valid_lookup(self):
        with tempfile.TemporaryDirectory() as root:
            codex = object.__new__(Codex)
            codex.thread_ids, codex.budget, codex.backlog = {"owned"}, Budget(1), []
            codex.model, codex.effort = "offline-test", "medium"
            codex.corpus = self.corpus(root)
            sent = []
            messages = iter([
                {"id": 1, "method": "item/tool/call", "params": {"threadId": "owned", "tool": "search_memories", "arguments": {"query": "horse", "limit": 10}}},
                {"id": 2, "method": "item/tool/call", "params": {"threadId": "owned", "tool": "search_memories", "arguments": {"query": "horse", "limit": 8}}},
                {"method": "item/completed", "params": {"threadId": "owned", "item": {"type": "agentMessage", "id": "reply", "text": "The horse watched the stream."}}},
                {"method": "turn/completed", "params": {"threadId": "owned", "turn": {"status": "completed"}}}])
            codex.rpc = lambda *args, **kwargs: {}
            codex.next = lambda deadline: next(messages)
            codex.send = sent.append
            codex._trace = lambda event: None
            result = codex.turn("owned", "What did the horse do?", "on_demand")
            self.assertFalse(sent[0]["result"]["success"])
            self.assertIn("retry", sent[0]["result"]["contentItems"][0]["text"])
            self.assertTrue(sent[1]["result"]["success"])
            self.assertEqual(result["toolCalls"][1]["result"]["records"][0]["sourceId"], "known")
            self.assertEqual(result["status"], "completed")

    def test_missing_timings_are_unknown_and_retrieval_is_not_quality(self):
        self.assertEqual(stats([None])["median"], None)
        row = {"arm": "automatic", "status": "failed", "text": "", "endToEndMs": 4,
               "firstTextEndToEndMs": None, "retrievalMs": 2, "toolCalls": []}
        result = summarize([row])["automatic"]
        self.assertEqual(result["completed"], 0)
        self.assertIn("Requires source-backed review", result["quality"])


if __name__ == "__main__":
    unittest.main()
