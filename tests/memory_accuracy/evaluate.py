"""Provider-free evaluator for frozen dialogue evidence manifests.

This reads captured evidence/results only. It never calls a model, game, database,
or network. Missing execution, labels, or human review cannot issue a certificate.
"""
import argparse
import hashlib
import json
import math
import statistics
from datetime import datetime
from pathlib import Path


def evaluate(manifest, execution):
    cases = manifest.get("cases", [])
    result_rows = (execution or {}).get("results", [])
    results = {r["caseId"]: r for r in result_rows}
    development = [c for c in cases if c.get("split") == "development"]
    holdout = [c for c in cases if c.get("split") == "holdout"]
    issues, per_case = [], []
    if len(development) < 53:
        issues.append("fewer_than_53_development_exchanges")
    if len(holdout) < 100:
        issues.append("fewer_than_100_independently_labeled_holdout_cases")
    if len({c["caseId"] for c in cases}) != len(cases):
        issues.append("duplicate_case_ids")
    if len(results) != len(result_rows):
        issues.append("duplicate_result_ids")
    if set(results) - {c["caseId"] for c in cases}:
        issues.append("unknown_result_ids")
    semantic_categories = {"attribution", "answer_completion", "correction", "chronology", "hypothetical_vs_completed"}
    if not semantic_categories.issubset({label for c in holdout for label in c.get("semanticCategories", [])}):
        issues.append("semantic_holdout_coverage_incomplete")
    if (execution or {}).get("evaluationProtocol") != "reign-dialogue-semantic-replay-v1":
        issues.append("semantic_replay_protocol_missing")
    development_conversations = {c.get("conversationId") for c in development}
    if any(c.get("conversationId") in development_conversations for c in holdout):
        issues.append("development_conversation_leaked_into_holdout")
    covered = required = answered = answer_required = 0
    reduction, retrieval, checker, generation, retries, costs = [], [], [], [], [], []
    for case in cases:
        row = results.get(case["caseId"])
        gaps = []
        if not row:
            per_case.append({"caseId": case["caseId"], "status": "not_executed"})
            continue
        sources = {s["sourceId"]: s for s in case.get("availableSources", [])}
        selected = {s["sourceId"]: s for s in row.get("selectedEvidence", [])}
        if len(sources) != len(case.get("availableSources", [])):
            gaps.append("duplicate_source_ids")
        if len(selected) != len(row.get("selectedEvidence", [])):
            gaps.append("duplicate_selected_source_ids")
        for source_id, source in sources.items():
            if "asOfSequence" in case and ("sequence" not in source or source["sequence"] > case["asOfSequence"]):
                gaps.append("future_or_unsequenced_manifest_source:" + source_id)
            if "asOfUtc" in case:
                try:
                    cutoff = datetime.fromisoformat(case["asOfUtc"].replace("Z", "+00:00"))
                    available = datetime.fromisoformat(source["availableUtc"].replace("Z", "+00:00"))
                    if cutoff.tzinfo is None or available.tzinfo is None or available > cutoff:
                        gaps.append("future_or_undated_manifest_source:" + source_id)
                except (KeyError, TypeError, ValueError):
                    gaps.append("future_or_undated_manifest_source:" + source_id)
        for source_id, source in selected.items():
            original = sources.get(source_id)
            if not original:
                gaps.append("ineligible_or_future_source:" + source_id)
            elif source.get("speakerId") != original.get("speakerId"):
                gaps.append("wrong_source_speaker:" + source_id)
            elif source.get("text") != original.get("text"):
                gaps.append("changed_or_clipped_source_content:" + source_id)
        for fact in case.get("requiredEvidence", []):
            required += 1
            evidence = selected.get(fact["sourceId"], {})
            present = fact["quote"] in evidence.get("text", "") and evidence.get("speakerId") == fact.get("speakerId")
            covered += int(present)
            if not present:
                gaps.append("required_evidence_missing:" + fact["sourceId"])
            if fact.get("answerRequired"):
                answer_required += 1
                # Answer labels are independent evaluation input, never inferred
                # from retrieval presence or a model's self-reported confidence.
                answered += int(row.get("answerLabels", {}).get(fact["id"]) == "correct")
        if case.get("labelStatus") != "verified_from_source":
            gaps.append("labels_require_source_review")
        semantic_review = row.get("semanticReview", {})
        if not (semantic_review.get("status") == "reviewed" and semantic_review.get("reviewer")
                and semantic_review.get("evidenceReference") and semantic_review.get("expectationMatched") is True
                and semantic_review.get("sourceEvidenceVerified") is True):
            gaps.append("semantic_result_review_missing_or_failed")
        if row.get("criticalErrors", 0) or row.get("repairInducedRelationshipReversal", False) or row.get("unrelatedMetadataLoss", False):
            gaps.append("critical_behavior_error")
        if not row.get("highMemoryMode", False) and (row.get("checkerCalls", 0) or row.get("semanticRewriteCalls", 0)):
            gaps.append("extra_call_while_disabled")
        if row.get("checkerCalls", 0) > 1 or row.get("semanticRewriteCalls", 0) > 1:
            gaps.append("new_path_call_budget_exceeded")
        if row.get("sourceFingerprint") != (execution or {}).get("sourceFingerprint"):
            gaps.append("artifact_fingerprint_mismatch")
        metrics = row.get("measurements", {})
        valid_metric = lambda value: isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value) and value >= 0
        baseline, candidate = metrics.get("baselineTotalInputTokens"), metrics.get("totalInputTokens")
        if valid_metric(baseline) and baseline > 0 and valid_metric(candidate):
            reduction.append(1 - candidate / baseline)
        else:
            gaps.append("missing_or_invalid_input_token_measurement")
        for name, target in [("retrievalMs", retrieval), ("checkerMs", checker), ("generationMs", generation), ("retryCalls", retries), ("totalCost", costs)]:
            if valid_metric(metrics.get(name)):
                target.append(metrics[name])
            else:
                gaps.append("missing_or_invalid_measurement:" + name)
        per_case.append({"caseId": case["caseId"], "status": "failed" if gaps else "evidence_pass", "gaps": gaps})
    executed = sum(c["status"] != "not_executed" for c in per_case)
    coverage = covered / required if required else None
    answer_coverage = answered / answer_required if answer_required else None
    median_reduction = statistics.median(reduction) if reduction else None
    if executed != len(cases):
        issues.append("candidate_execution_incomplete")
    if coverage is None or coverage < .98:
        issues.append("required_evidence_below_98_percent_or_unmeasured")
    if answer_coverage is None or answer_coverage < .98:
        issues.append("answer_coverage_below_98_percent_or_unmeasured")
    if median_reduction is None or median_reduction < .40:
        issues.append("median_total_input_reduction_below_40_percent_or_unmeasured")
    if any(c["status"] == "failed" for c in per_case):
        issues.append("failed_cases")
    if not (execution or {}).get("sourceFingerprint"):
        issues.append("missing_validated_artifact_fingerprint")
    if not all((execution or {}).get("humanReview", {}).get(k) is True for k in ("voice", "naturalness", "unnecessaryCorrections")):
        issues.append("human_review_outstanding")
    return {"schema": "reign-dialogue-memory-acceptance-v1", "certificateEligible": not issues,
            "developmentCases": len(development), "holdoutCases": len(holdout), "executedCases": executed,
            "requiredEvidenceCoverage": coverage, "answerCoverage": answer_coverage,
            "medianTotalInputReduction": median_reduction, "gates": issues, "cases": per_case,
            "measurements": {name: {"count": len(data), "median": statistics.median(data) if data else None}
                             for name, data in [("retrievalMs", retrieval), ("generationMs", generation), ("checkerMs", checker), ("retryCalls", retries), ("totalCost", costs)]}}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", required=True, type=Path)
    parser.add_argument("--results", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    manifest_bytes = args.manifest.read_bytes()
    report = evaluate(json.loads(manifest_bytes.decode("utf-8-sig")), json.loads(args.results.read_text(encoding="utf-8-sig")) if args.results else None)
    report["manifestSha256"] = hashlib.sha256(manifest_bytes).hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: v for k, v in report.items() if k != "cases"}, indent=2))
    return 0 if report["certificateEligible"] else 2


if __name__ == "__main__":
    raise SystemExit(main())
