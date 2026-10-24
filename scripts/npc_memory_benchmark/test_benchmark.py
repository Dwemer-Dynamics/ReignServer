import copy
import unittest
from pathlib import Path

from benchmark import aggregate, calibrate, eligible, metric, read_json, validate_fixture


class BenchmarkContracts(unittest.TestCase):
    def setUp(self):
        self.fixture = read_json(Path(__file__).with_name("fixtures.json"))

    def test_fixture_and_all_privacy_traps(self):
        validate_fixture(self.fixture)
        query = self.fixture["queries"][0]
        source = next(m for m in self.fixture["memories"] if m["id"] == "rescue")
        self.assertTrue(eligible(source, query))
        traps = [m for m in self.fixture["memories"] if m["id"].startswith("rescue-forbidden")]
        self.assertEqual(len(traps), 6)
        self.assertTrue(all(not eligible(m, query) for m in traps))

    def test_graded_metrics_and_missing_relevant_documents(self):
        scores = metric(["noise", "a", "b"], {"a": 3, "b": 1, "c": 2})
        self.assertEqual(scores["hitAt5"], 1)
        self.assertAlmostEqual(scores["recallAt5"], 2/3)
        self.assertEqual(scores["precisionAt5"], 0.4)
        self.assertEqual(scores["mrrAt5"], 0.5)
        self.assertLess(scores["ndcgAt5"], 1)
        self.assertEqual(metric([], {"a": 3})["recallAt5"], 0)

    def test_negative_queries_are_not_positive_accuracy(self):
        self.assertEqual(metric([], {}), {"falseRetrieval": 0, "correctAbstention": 1})
        self.assertEqual(metric(["noise"], {})["falseRetrieval"], 1)

    def test_threshold_dev_only_and_no_answer_tradeoff(self):
        dev = [
            {"judgments": {"a": 3}, "ranking": [{"id": "a", "score": 0.8}, {"id": "noise", "score": 0.3}]},
            {"judgments": {}, "ranking": [{"id": "noise", "score": 0.7}]},
        ]
        selected = calibrate(dev, 0.05)
        self.assertEqual(selected["threshold"], 0.8)
        self.assertEqual(selected["dev"]["recallAt5"], 1)
        test = [{"judgments": {"b": 3}, "ranking": [{"id": "b", "score": 0.75}]}]
        self.assertEqual(aggregate(test, selected["threshold"])["recallAt5"], 0)

    def test_wrong_labels_and_duplicate_ids_fail_closed(self):
        broken = copy.deepcopy(self.fixture)
        broken["queries"][0]["judgments"] = {"rescue-forbidden-private": 3}
        with self.assertRaises(ValueError): validate_fixture(broken)
        broken = copy.deepcopy(self.fixture)
        broken["memories"].append(broken["memories"][0])
        with self.assertRaises(ValueError): validate_fixture(broken)

    def test_empty_split_and_nonfinite_time_rejected(self):
        broken = copy.deepcopy(self.fixture)
        broken["queries"] = [q for q in broken["queries"] if q["judgments"]]
        with self.assertRaises(ValueError): validate_fixture(broken)
        broken = copy.deepcopy(self.fixture)
        broken["memories"][0]["day"] = float("nan")
        with self.assertRaises(ValueError): validate_fixture(broken)


if __name__ == "__main__":
    unittest.main()
