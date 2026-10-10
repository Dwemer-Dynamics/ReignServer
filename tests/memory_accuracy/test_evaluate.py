import copy
import unittest

from evaluate import evaluate


class AcceptanceGuards(unittest.TestCase):
    def fixture(self):
        source = {"sourceId": "prior", "speakerId": "npc", "text": "Where would you like to go?", "sequence": 1}
        case = {"caseId": "case", "split": "development", "conversationId": "isolated", "asOfSequence": 1,
                "availableSources": [source], "labelStatus": "verified_from_source", "requiredEvidence": [
                    {"id": "answer", "sourceId": "prior", "speakerId": "npc", "quote": source["text"], "answerRequired": True}]}
        result = {"caseId": "case", "sourceFingerprint": "artifact", "selectedEvidence": [copy.deepcopy(source)],
                  "answerLabels": {}, "measurements": {"baselineTotalInputTokens": 100, "totalInputTokens": 50,
                    "retrievalMs": 1, "checkerMs": 0, "generationMs": 0, "retryCalls": 0, "totalCost": 0}}
        return {"cases": [case]}, {"sourceFingerprint": "artifact", "results": [result]}

    def test_no_execution_cannot_certify(self):
        manifest, _ = self.fixture()
        result = evaluate(manifest, None)
        self.assertFalse(result["certificateEligible"])
        self.assertIn("candidate_execution_incomplete", result["gates"])

    def test_retrieval_does_not_count_as_an_answer(self):
        manifest, execution = self.fixture()
        result = evaluate(manifest, execution)
        self.assertEqual(result["requiredEvidenceCoverage"], 1)
        self.assertEqual(result["answerCoverage"], 0)

    def test_future_source_inside_manifest_is_rejected(self):
        manifest, execution = self.fixture()
        manifest["cases"][0]["availableSources"][0]["sequence"] = 2
        self.assertIn("future_or_unsequenced_manifest_source:prior", evaluate(manifest, execution)["cases"][0]["gaps"])

    def test_future_timestamp_is_rejected(self):
        manifest, execution = self.fixture()
        manifest["cases"][0]["asOfUtc"] = "2026-09-27T01:00:00Z"
        manifest["cases"][0]["availableSources"][0]["availableUtc"] = "2026-09-27T01:00:01Z"
        self.assertIn("future_or_undated_manifest_source:prior", evaluate(manifest, execution)["cases"][0]["gaps"])

    def test_duplicate_results_do_not_hide_a_failure(self):
        manifest, execution = self.fixture()
        execution["results"].append(copy.deepcopy(execution["results"][0]))
        self.assertIn("duplicate_result_ids", evaluate(manifest, execution)["gates"])

    def test_clipped_content_wrong_speaker_and_extra_calls_fail(self):
        manifest, execution = self.fixture()
        row = execution["results"][0]
        row["selectedEvidence"][0]["text"] = "Where would you"
        row["checkerCalls"] = 1
        gaps = evaluate(manifest, execution)["cases"][0]["gaps"]
        self.assertIn("changed_or_clipped_source_content:prior", gaps)
        self.assertIn("extra_call_while_disabled", gaps)
        row["selectedEvidence"][0]["speakerId"] = "player"
        self.assertIn("wrong_source_speaker:prior", evaluate(manifest, execution)["cases"][0]["gaps"])

    def test_nonfinite_measurements_cannot_pass(self):
        manifest, execution = self.fixture()
        execution["results"][0]["measurements"]["checkerMs"] = float("nan")
        self.assertIn("missing_or_invalid_measurement:checkerMs", evaluate(manifest, execution)["cases"][0]["gaps"])

    def test_source_matching_without_semantic_verdict_cannot_certify(self):
        manifest, execution = self.fixture()
        execution["results"][0]["answerLabels"] = {"answer": "correct"}
        execution["humanReview"] = {"voice": True, "naturalness": True, "unnecessaryCorrections": True}
        result = evaluate(manifest, execution)
        self.assertEqual(result["requiredEvidenceCoverage"], 1)
        self.assertEqual(result["answerCoverage"], 1)
        self.assertIn("semantic_replay_protocol_missing", result["gates"])
        self.assertIn("semantic_result_review_missing_or_failed", result["cases"][0]["gaps"])
        execution["evaluationProtocol"] = "reign-dialogue-semantic-replay-v1"
        execution["results"][0]["semanticReview"] = {"status": "reviewed", "reviewer": "fixture-reviewer",
            "evidenceReference": "isolated-case-result", "expectationMatched": True, "sourceEvidenceVerified": True}
        reviewed = evaluate(manifest, execution)
        self.assertNotIn("semantic_replay_protocol_missing", reviewed["gates"])
        self.assertNotIn("semantic_result_review_missing_or_failed", reviewed["cases"][0]["gaps"])


if __name__ == "__main__":
    unittest.main()
