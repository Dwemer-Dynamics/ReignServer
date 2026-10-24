"""Freeze an existing private Reign memory-evidence capture for comparison.

Does not read live campaigns or create character personalities. Evaluation labels
are deliberately separate from the model-visible fixture.
"""
import argparse
from pathlib import Path
from compare import SCHEMA, digest, dump, load


def prepare(capture, destination):
    original = load(capture)
    cases = {case["caseId"]: case for case in original["cases"]}
    seed = cases["fresh_session_horse_original"]
    writer = seed["writerReplay"]
    ordered = ["fresh_session_horse_original", "fresh_session_fence_invitation", "fresh_session_spring_hand",
               "fresh_session_coreining_word", "unknown_frog_witness_control",
               "frog_callback_before_failed_answer", "tournament_callback_before_failed_answer"]
    fixture = {"schema": SCHEMA, "classification": "adapted_real_history_development_cases_not_holdout",
               "provenance": {"captureSha256": digest(Path(capture).read_bytes()),
                              "note": "Captured historical character stack and sources, no live data mutation or personality authoring."},
               "scope": {"npcId": seed["observerId"], "peerId": seed["peerId"], "asOfSequence": seed["asOfSequence"]},
               "foundation": {key: writer[key] for key in ("profile", "characteristics", "relationships", "state", "sceneContext")},
               "records": seed["sources"],
               "cases": [{"caseId": key, "query": cases[key]["query"], "kind": "adapted_historical_query"} for key in ordered]}
    fixture["cases"].insert(1, {"caseId": "short_stream_followup", "query": "And what did he do after it jumped back into the stream?", "kind": "constructed_followup_to_real_evidence"})
    fixture["cases"].append({"caseId": "later_name_correction", "query": "I renamed Tempest to Comet yesterday. What do I call him now, and what was his old quarrel at the stream?", "kind": "constructed_current_player_correction_not_historical_fact"})
    destination = Path(destination)
    if destination.exists():
        raise ValueError("Frozen fixture already exists; do not overwrite")
    dump(destination, fixture)
    return {"fixtureSha256": digest(destination.read_bytes()), "records": len(fixture["records"]), "cases": len(fixture["cases"]), "character": writer["profile"].get("name")}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--capture", required=True)
    parser.add_argument("--output", required=True)
    args = parser.parse_args()
    print(prepare(args.capture, args.output))
