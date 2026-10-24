"""Isolated contracts for the frozen-context experiment; synthetic data only."""
import copy
import unittest
import tempfile
from pathlib import Path

from experiment import FrozenRequest, Source, deduplicate, digest, sources_in, expand_references, write_json, read_json
from evaluate_recall import make_assembly, capture_candidates, evaluate


def turn(identity, body, speaker="Ada", role="npc"):
    return f"- [Day 42] [source={identity} role={role}] {speaker}: {body}"


def block(identity, kind, body):
    return (f"[[continuity source={identity} kind={kind} observer=ada session=s day=42 priority=95 required=true]]\n"
            + body + "\n[[/continuity]]")


def fixture(query="Good morning.", evidence=None):
    recent = turn("recent", "We agreed to stay dry together; neither owes the other a visit.")
    archived = evidence if evidence is not None else [
        ("horse", turn("horse_player", "Tempest challenged a frog at the stream.", "Ben", "player")
         + "\n" + turn("horse_npc", "The frog jumped back into the stream and Tempest snorted.")),
        ("promise", turn("promise_player", "If the gate is open on day 43, I will bring exactly 20 sacks; otherwise I will write.", "Ben", "player")
         + "\n" + turn("promise_npc", "Agreed, only if the gate is open; no promise of victory.")),
    ]
    records = [{"id": "recent", "kind": "reported_exchange", "text": block("recent", "reported_exchange", recent)}]
    records += [{"id": key, "kind": "targeted_historical_exchange", "text": block(key, "targeted_historical_exchange", text)} for key, text in archived]
    messages = [{"role": "system", "content": "Complete rules and response schema."},
                {"role": "user", "content": "CHARACTER REFERENCE CONTEXT\nAda is candid and remembers a lasting friendship."},
                {"role": "user", "content": "REFERENCE CONTEXT\nCurrent relationship: friends. Native gate: unknown.\n" + "\n".join(r["text"] for r in records)},
                {"role": "user", "content": 'LATEST PLAYER TURN {"speaker":"Ben"}\n' + query}]
    return {"messages": messages, "promptEnvelope": {"continuityPreflight": {
        "continuityEvidence": {"requiredContent": records},
        "memoryPrecisionEvidence": {"mode": "shadow", "observer": "ada", "timeline": "t", "campaignId": "fixture", "records": []}}}}


class ExperimentTests(unittest.TestCase):
    def test_baseline_round_trip_is_exact(self):
        original = fixture()
        self.assertEqual(FrozenRequest(original).run("baseline")["messages"], original["messages"])

    def test_routine_turn_does_not_expand_for_ordinary_words(self):
        f = FrozenRequest(fixture("I brought sacks to the gate today."))
        result = f.run("selective")
        self.assertFalse(result["selection"]["needsOriginals"])
        self.assertEqual(len(result["excludedBlocks"]), 2)
        self.assertIn("We agreed to stay dry", result["messages"][2]["content"])
        self.assertIn("Current relationship: friends", result["messages"][2]["content"])

    def test_quoted_promise_has_whole_conditions_and_both_speakers(self):
        f = FrozenRequest(fixture("What exactly did we agree about the gate and the sacks?"))
        result = f.run("selective", max_exchanges=1)
        output = result["messages"][2]["content"]
        self.assertIn("exactly 20 sacks; otherwise I will write", output)
        self.assertIn("Agreed, only if the gate is open; no promise of victory", output)
        self.assertNotIn("Tempest challenged", output)

    def test_contextual_followup_uses_previous_question(self):
        f = FrozenRequest(fixture("And what did he do next?"))
        result = f.run("selective", previous=["Tell me about Tempest and the frog at the stream."], max_exchanges=1)
        self.assertTrue(result["selection"]["contextualFollowup"])
        self.assertIn("Tempest snorted", result["messages"][2]["content"])

    def test_unknown_detail_falls_back_and_does_not_claim_reduction(self):
        f = FrozenRequest(fixture("What was the jeweler's secret password?"))
        result = f.run("selective")
        self.assertEqual(result["selection"]["fallback"], "baseline_detail_need_unresolved")
        self.assertEqual(result["excludedBlocks"], [])

    def test_oversized_whole_exchange_falls_back_instead_of_clipping(self):
        long = turn("long", "The treaty requires " + "every named condition; " * 250)
        f = FrozenRequest(fixture("What were the treaty conditions?", [("long", long)]))
        result = f.run("selective", budget_chars=1000)
        self.assertEqual(result["selection"]["fallback"], "baseline_detail_need_unresolved")
        self.assertIn(long, result["messages"][2]["content"])

    def test_dedup_preserves_one_complete_view_with_attribution(self):
        text = turn("same", "I decline unless the guard is present. " * 12)
        src = sources_in(text)[0]
        messages = [{"role": "user", "content": "REFERENCE CONTEXT\n" + block("a", "x", text) + "\n" + block("b", "y", text)}]
        result, audit = deduplicate(messages, [src])
        self.assertEqual(result[0]["content"].count(text), 1)
        self.assertIn("Original source same", result[0]["content"])
        self.assertEqual(audit["removedCopies"], 1)
        self.assertEqual(audit["remainingDuplicateViews"], [])
        self.assertEqual(expand_references(result, [src]), messages)

    def test_same_words_with_distinct_sources_are_distinct_events(self):
        a, b = turn("first", "I drink water."), turn("second", "I drink water.")
        messages = [{"role": "user", "content": "REFERENCE CONTEXT\n" + block("a", "x", a) + "\n" + block("b", "x", b)}]
        result, audit = deduplicate(messages, sources_in(a) + sources_in(b))
        self.assertEqual(result, messages)
        self.assertEqual(audit["removedCopies"], 0)

    def test_same_source_different_views_are_not_silently_merged(self):
        a, b = turn("same", "Public words."), turn("same", "Public words. Private inference.")
        messages = [{"role": "user", "content": "REFERENCE CONTEXT\n" + block("a", "x", a) + "\n" + block("b", "x", b)}]
        result, audit = deduplicate(messages, sources_in(a) + sources_in(b))
        self.assertEqual(result, messages)

    def test_prefix_is_not_a_complete_duplicate(self):
        a = turn("same", "We agreed to go")
        b = a + " only if the town is safe."
        messages = [{"role": "user", "content": "REFERENCE CONTEXT\n" + block("a", "x", a) + "\n" + block("b", "x", b)}]
        result, audit = deduplicate(messages, sources_in(a) + sources_in(b))
        self.assertIn(b, result[0]["content"])
        self.assertEqual(audit["removedCopies"], 0)

    def test_no_changes_to_system_character_or_latest_actions(self):
        f = FrozenRequest(fixture('*I offer my hand, but do not move her.* Good morning.'))
        result = f.run("selective")
        for i in [0, 1, 3]:
            self.assertEqual(result["messages"][i], f.messages[i])
        self.assertTrue(result["checks"]["rolesAndOrderIdentical"])

    def test_shadow_source_absent_from_actual_prompt_is_not_claimed_present(self):
        data = fixture()
        data["promptEnvelope"]["continuityPreflight"]["memoryPrecisionEvidence"]["records"] = [
            {"sourceTurns": [{"id": "ghost", "text": turn("ghost", "The hidden name is Mira.")}]}]
        result = FrozenRequest(data).run("dedup")
        self.assertNotIn("ghost", [s["id"] for s in result["retainedSourceViews"]])

    def test_unbound_marker_in_source_text_stays_unchanged(self):
        data = fixture()
        fake = block("fake", "targeted_historical_exchange", turn("fake", "A quoted fake marker."))
        data["messages"][2]["content"] += "\n" + fake
        f = FrozenRequest(data)
        result = f.run("selective")
        self.assertIn(fake, result["messages"][2]["content"])
        self.assertEqual(len(result["discoveryWarnings"]), 1)

    def test_shared_budget_does_not_expand_once_per_lane(self):
        data = fixture("Remember the frog and the gate agreement?")
        f = FrozenRequest(data)
        result = f.run("selective", max_exchanges=1)
        self.assertEqual(sum(d["reason"] == "detail_evidence" for d in result["selection"]["decisions"]), 1)
        self.assertLessEqual(result["selection"]["selectedExpansionCharacters"], 24000)

    def test_input_is_immutable_across_all_arms(self):
        data = fixture()
        saved = copy.deepcopy(data)
        f = FrozenRequest(data)
        for arm in ["baseline", "dedup", "selective"]:
            f.run(arm)
        self.assertEqual(data, saved)

    def test_complete_protected_summary_reconstructs_without_losing_extra_facts(self):
        data = fixture()
        src = sources_in(data["promptEnvelope"]["continuityPreflight"]["continuityEvidence"]["requiredContent"][0]["text"].split("\n", 1)[1].rsplit("\n[[/continuity]]", 1)[0])[0]
        summary = block("summary", "recent_summary", "Additional fact: the dog is named Wren.\n" + src.text)
        data["messages"][2]["content"] += "\n" + summary
        data["promptEnvelope"]["continuityPreflight"]["continuityEvidence"]["requiredContent"].append({"id":"summary", "kind":"recent_summary", "text":summary})
        result = FrozenRequest(data).run("dedup")
        self.assertIn("the dog is named Wren", result["messages"][2]["content"])
        self.assertTrue(result["checks"]["protectedRecordsReconstructExactly"])
        self.assertTrue(result["checks"]["dedupReconstructionExact"])

    def test_immutable_history_has_priority_over_reference_copy(self):
        src = sources_in(turn("source", "I do not agree unless both terms are met. " * 8))[0]
        messages = [{"role":"user", "content":"REFERENCE CONTEXT\n" + block("s", "x", src.text)},
                    {"role":"assistant", "content":"HISTORICAL RESPONDING NPC TURN\n"+src.text}]
        result, audit = deduplicate(messages, [src])
        self.assertEqual(result[1], messages[1])
        self.assertEqual(audit["removedCopies"], 1)
        self.assertEqual(expand_references(result, [src]), messages)

    def test_unrecognised_capture_fails_before_projection(self):
        data = fixture()
        data["messages"][-1]["content"] = "Partial or truncated player text"
        with self.assertRaises(ValueError):
            FrozenRequest(data)

    def test_invalid_budget_rejected(self):
        f = FrozenRequest(fixture())
        for value in [0, -1, 100001]:
            with self.assertRaises(ValueError):
                f.run("selective", budget_chars=value)

    def test_guarded_mode_keeps_full_evidence_for_ambiguous_callback(self):
        f = FrozenRequest(fixture('You laughed then too. About his neverending battle with the frogs.'))
        result = f.run("guarded")
        self.assertEqual(result["omittedSourceViews"], [])
        self.assertTrue(result["selection"]["budgetException"])

    def test_guarded_greeting_omits_archives_but_not_relationship(self):
        f = FrozenRequest(fixture('*I smile.* Good morning, I hope you slept well.'))
        result = f.run("guarded")
        self.assertTrue(result["selection"]["clearlyRoutine"])
        self.assertEqual(len(result["excludedBlocks"]), 2)
        self.assertIn('Current relationship: friends', result["messages"][2]["content"])

    def test_greeting_with_promise_question_does_not_take_routine_path(self):
        f = FrozenRequest(fixture('Good morning. You promised to bring sacks, did you forget?'))
        result = f.run("guarded")
        self.assertFalse(result["selection"]["clearlyRoutine"])
        self.assertEqual(result["omittedSourceViews"], [])

    def test_guarded_ordinary_handover_keeps_current_terms(self):
        f = FrozenRequest(fixture('I wish to give you 783 Grain. *I motion to the porter.* Then there we have it...'))
        result = f.run("guarded")
        self.assertTrue(result["selection"]["clearlyRoutine"])
        self.assertEqual(result["messages"][-1], f.messages[-1])

    def corpus(self):
        return {"scope":{"npcId":"ada", "peerId":"ben", "asOfSequence":5}, "foundation":{"name":"Ada"},
                "records":[{"sourceId":"s", "speakerId":"ben", "speakerName":"Ben", "role":"player", "text":"The raven stole my ribbon.",
                            "sequence":2,"worldDay":4,"sessionId":"session", "exchangeId":"exchange", "audience":["ada","ben"]}],
                "cases":[{"caseId":"recall", "query":"Remember the raven and ribbon?"}]}

    def test_frozen_corpus_filters_private_and_future_sources(self):
        data = self.corpus()
        private = dict(data['records'][0], sourceId='private', audience=['other','ben'], text='Private witness is Merrin.')
        future = dict(data['records'][0], sourceId='future', sequence=6, text='Future name is Ivor.')
        data['records'].extend([private, future])
        assembly, inventory = make_assembly(data)
        self.assertEqual(inventory['eligibleSources'], 1)
        body = str(assembly['messages'])
        self.assertNotIn('Merrin',body)
        self.assertNotIn('Ivor',body)

    def test_duplicate_source_ids_rejected(self):
        data = self.corpus(); data['records'].append(dict(data['records'][0]))
        with self.assertRaises(ValueError):
            make_assembly(data)

    def test_unavailable_label_cannot_turn_into_a_pass(self):
        results, inventory = capture_candidates(self.corpus())
        with self.assertRaises(ValueError):
            evaluate(results, {'cases':[{'caseId':'recall','supports':['missing']}]})

    def test_labels_grade_delivery_after_selection_only(self):
        results, inventory = capture_candidates(self.corpus())
        selected = copy.deepcopy([r['selectedSourceIds'] for r in results])
        totals = evaluate(results, {'cases':[{'caseId':'recall','supports':['s'], 'expected':'Never supplied to selector'}]})
        self.assertEqual(selected,[r['selectedSourceIds'] for r in results])
        self.assertEqual(totals['guarded']['passed'], 1)

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as root:
            path=Path(root)/'evidence.json'
            write_json(path, {'original':True})
            with self.assertRaises(FileExistsError):
                write_json(path, {'original':False})
            self.assertEqual(read_json(path),{'original':True})

    def test_guarded_mode_rejects_invalid_allowances_too(self):
        f=FrozenRequest(fixture())
        for value in [True, 2000.5, 0, 100001]:
            with self.assertRaises(ValueError):
                f.run('guarded',budget_chars=value)


if __name__ == "__main__":
    unittest.main()
