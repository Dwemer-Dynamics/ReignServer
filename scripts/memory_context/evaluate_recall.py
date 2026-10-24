"""Compare source delivery against separate labels in an offline frozen corpus.

The labels are read only after candidate prompts are built. No provider calls,
answer grading, production ranking parity or independent holdout is implied.
"""
import argparse
import collections
import copy
import json
from pathlib import Path

from experiment import FrozenRequest, SCHEMA, digest, read_json, write_json


def make_assembly(fixture):
    scope = fixture["scope"]
    if not isinstance(scope.get("asOfSequence"), int) or isinstance(scope["asOfSequence"], bool):
        raise ValueError("A frozen accepted-sequence cutoff is required")
    if not scope.get("npcId") or not scope.get("peerId"):
        raise ValueError("Exact observer and peer are required")
    if not 1 <= len(fixture["records"]) <= 512:
        raise ValueError("Expected 1..512 frozen sources")
    seen = set()
    eligible = []
    for row in fixture["records"]:
        identity = row.get("sourceId")
        if not isinstance(identity, str) or not identity or identity in seen:
            raise ValueError("Missing or duplicate source identity")
        seen.add(identity)
        if not isinstance(row.get("sequence"), int) or isinstance(row["sequence"], bool):
            raise ValueError("Invalid source sequence")
        if not isinstance(row.get("text"), str) or row.get("role") not in {"player", "npc"}:
            raise ValueError("Invalid original source content/role")
        audience = row.get("audience")
        if not isinstance(audience, list):
            raise ValueError("Missing source audience")
        if row["sequence"] <= scope["asOfSequence"] and all(x in audience for x in (scope["npcId"], scope["peerId"])):
            eligible.append(row)
    groups = collections.OrderedDict()
    for row in sorted(eligible, key=lambda r: r["sequence"]):
        groups.setdefault((row["sessionId"], row.get("exchangeId") or row["sourceId"]), []).append(row)
    records = []
    for (session, exchange), rows in groups.items():
        text = "\n".join("- [Day " + str(r["worldDay"]) + "] [source=" + r["sourceId"] + " role=" + r["role"] + "] "
                         + r["speakerName"] + ": " + r["text"] for r in rows)
        body = ("[[continuity source=" + exchange + " kind=targeted_historical_exchange observer=" + scope["npcId"]
                + " session=" + session + " day=0 priority=95 required=true]]\n" + text + "\n[[/continuity]]")
        records.append({"id": exchange, "kind": "targeted_historical_exchange", "text": body})
    assembly = {"messages": [
        {"role": "system", "content": "Frozen retrieval-only development fixture. Preserve original speaker attribution and uncertainty."},
        {"role": "user", "content": "CHARACTER REFERENCE CONTEXT\n" + json.dumps(fixture["foundation"], ensure_ascii=False)},
        {"role": "user", "content": "REFERENCE CONTEXT\n" + "\n".join(r["text"] for r in records)},
        {"role": "user", "content": 'LATEST PLAYER TURN {"speaker":"Player"}\nHello.'}],
        "promptEnvelope": {"continuityPreflight": {"continuityEvidence": {"requiredContent": records},
                              "memoryPrecisionEvidence": {"observer": scope["npcId"], "timeline": "frozen-development-corpus", "records": []}}}}
    return assembly, {"eligibleSources": len(eligible), "excludedSources": len(seen) - len(eligible), "exchanges": len(records)}


def capture_candidates(fixture):
    assembly, inventory = make_assembly(fixture)
    cases = fixture.get("cases", [])
    if not 1 <= len(cases) <= 1000 or len({c["caseId"] for c in cases}) != len(cases):
        raise ValueError("Expected 1..1000 unique cases")
    results = []
    for case in cases:
        query = case["query"]
        if not isinstance(query, str) or not query or len(query) > 12000:
            raise ValueError("Invalid query")
        previous = case.get("previousQueries", [])
        if (not isinstance(previous, list) or len(previous) > 2
                or any(not isinstance(p, str) or len(p) > 12000 for p in previous)):
            raise ValueError("Expected at most two bounded explicit previous queries")
        case_assembly = copy.deepcopy(assembly)
        case_assembly["messages"][-1]["content"] = 'LATEST PLAYER TURN {"speaker":"Player"}\n' + query
        frozen = FrozenRequest(case_assembly, "frozen-source-corpus")
        for arm in ("baseline", "dedup", "selective", "guarded"):
            result = frozen.run(arm, previous=previous)
            results.append({"caseId": case["caseId"], "query": query, "arm": arm,
                            "selectedSourceIds": sorted({s["id"] for s in result["retainedSourceViews"]}),
                            "characters": result["characters"], "baselineCharacters": result["baselineCharacters"],
                            "selection": result["selection"], "checks": result["checks"]})
    return results, inventory


def evaluate(results, labels):
    label_map = {c["caseId"]: c for c in labels["cases"]}
    baseline = {r["caseId"]: set(r["selectedSourceIds"]) for r in results if r["arm"] == "baseline"}
    for row in results:
        required = label_map.get(row["caseId"], {}).get("supports", [])
        unavailable = set(required) - baseline[row["caseId"]]
        if unavailable:
            raise ValueError("Label source unavailable in frozen baseline: " + row["caseId"])
        missing = sorted(set(required) - set(row["selectedSourceIds"]))
        row.update(expectedSourceIds=required, missingSourceIds=missing,
                   requiredSourceCoverage=(len(required) - len(missing)) / len(required) if required else None,
                   sourceDeliveryVerdict="not_scored" if not required else "pass" if not missing else "fail")
    totals = {}
    for arm in ("baseline", "dedup", "selective", "guarded"):
        rows = [r for r in results if r["arm"] == arm]
        totals[arm] = {"cases": len(rows), "graded": sum(r["sourceDeliveryVerdict"] != "not_scored" for r in rows),
                       "passed": sum(r["sourceDeliveryVerdict"] == "pass" for r in rows),
                       "failed": sum(r["sourceDeliveryVerdict"] == "fail" for r in rows),
                       "ungraded": sum(r["sourceDeliveryVerdict"] == "not_scored" for r in rows),
                       "fallbacks": sum(bool((r["selection"] or {}).get("fallback")) for r in rows)}
    return totals


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--fixture", required=True)
    parser.add_argument("--labels", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    output = Path(args.output).resolve()
    if output.exists():
        raise ValueError("Use a fresh output file; earlier evidence is retained")
    results, inventory = capture_candidates(read_json(args.fixture))
    # No answer/source labels were available to construction or selection above.
    totals = evaluate(results, read_json(args.labels))
    report = {"schema": SCHEMA, "classification": "development_source_delivery_only_not_prose_or_holdout",
              "fixtureSha256": digest(Path(args.fixture).read_bytes()), "labelsSha256": digest(Path(args.labels).read_bytes()),
              "inventory": inventory, "totals": totals, "cases": results, "providerCalls": 0}
    output.parent.mkdir(parents=True, exist_ok=True)
    write_json(output, report)
    print(json.dumps({"output": str(output), "inventory": inventory, "totals": totals}))


if __name__ == "__main__":
    main()
