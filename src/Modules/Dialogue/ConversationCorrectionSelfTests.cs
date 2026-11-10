using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationCorrectionSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => rows.Add(TestDict("caseId", "correction_" + id, "passed", pass, "summary", id, "data", data));
            const string correction = "I left for Ostican in a different direction. The letter had nothing to do with my lodging. It has nothing to do with this.";
            const string lodging = "I didnt stay at an inn, although it doesnt really matter as it has nothing to do with this. I stayed as a guest of the governor in the keep.";
            foreach (string text in new[] { correction, lodging, "That is not relevant.", "Stop asking; we can deal with that later." })
                Check("recognizes_correction_" + rows.Count, IsConversationCorrection(text));
            var history = new List<Dictionary<string, object>> {
                TestDict("role", "player", "turn_id", "first", "text", correction),
                TestDict("role", "npc", "speaker_id", "npc", "text", "The road-map waits until you name an inn."),
                TestDict("role", "player", "turn_id", "current", "text", lodging) };
            const string bad = "It does matter, because it is the one answer with a witness made of ink. Name the governor whose keep sheltered you; I need the guest records.";
            Check("captured_lodging_demand_is_rejected", FindConversationCorrectionViolations(TestDict("reply", bad), lodging, history, "npc").Count > 0);
            var payload = TestDict();
            var originalState = TestDict("currentPlan", "Demand his inns and guest records before questioning the courier.", "currentCrisis", "The letter has not arrived.");
            string before = Json.Serialize(originalState);
            var projected = ReconcileConversationPlanForPrompt(originalState, lodging, payload);
            Check("plan_projection_preserves_source_and_underlying_concern", ReadString(projected, "currentPlan", "x") == ""
                && ReadString(projected, "currentCrisis", "") == "The letter has not arrived." && Json.Serialize(originalState) == before
                && ReadString(payload, "contestedCurrentPlan", "").Length > 0);
            Check("hidden_plan_must_be_reconciled", FindConversationCorrectionViolations(TestDict("reply", "You are right; I will ask my courier instead."), lodging, history, "npc", payload)
                .Any(v => ReadString(v, "field", "") == "unreconciled_currentPlan"));
            var good = TestDict("reply", "Your lodging is unrelated. I will question my own courier; the missing letter remains unresolved.",
                "stateUpdates", TestDict("currentPlan", "Question my courier about the missing letter."));
            Check("reconciled_step_does_not_resolve_parent", FindConversationCorrectionViolations(good, lodging, history, "npc", payload).Count == 0);
            foreach (string reply in new[] { "I will not reveal the courier's name.", "I heard you. I still doubt your explanation, but I have no proof.",
                "You need not name the governor; I will investigate the letter myself.", "There are 50 denars in the purse." })
                Check("refusal_doubt_and_literal_facts_preserved_" + rows.Count, FindConversationCorrectionViolations(TestDict("reply", reply), lodging, history, "npc").Count == 0);
            var newEvidence = TestDict("role", "player", "turn_id", "new_evidence", "text", "The courier handed the governor my letter at that keep.");
            history.Insert(0, newEvidence);
            string supportedReply = "Name the governor whose keep sheltered you because you said the courier handed the governor the letter at that keep.";
            var supported = TestDict("reply", supportedReply,
                "correctionEvidence", new[] { TestDict("turnId", "new_evidence", "quote", ReadString(newEvidence, "text", ""), "replyQuote", supportedReply) });
            Check("source_linked_new_evidence_can_reopen_request", FindConversationCorrectionViolations(supported, lodging, history, "npc").Count == 0);
            ReadDictionaryList(supported, "correctionEvidence")[0]["quote"] = "A fabricated independent fact.";
            Check("fabricated_support_cannot_excuse_demand", FindConversationCorrectionViolations(supported, lodging, history, "npc").Count > 0);
            foreach (string text in new[] { "That’s irrelevant.", "That isn’t relevant.", "It doesn’t matter.", "This is unrelated.", "Quit asking.", "We can discuss that later." })
                Check("paraphrase_correction_" + rows.Count, IsConversationCorrection(text));
            Check("ordinary_denial_is_testimony_not_correction", !IsConversationCorrection("I wasn't at the inn.") && !IsConversationCorrection("I didn't see the courier."));
            Check("clause_negation_does_not_excuse_new_demand", FindConversationCorrectionViolations(TestDict("reply", "You say the governor's keep is unrelated, but name the governor whose keep sheltered you."), lodging, history, "npc").Count > 0);
            Check("deictic_correction_binds_last_demand", FindConversationCorrectionViolations(TestDict("reply", "Name the inn where you stayed."), "Stop asking.", history, "npc").Count > 0);
            var doubtful = TestDict("reply", "I still doubt that explanation.", "beliefWrites", new[] { TestDict("text", "His dates at the inn may not match the records.") });
            Check("uncertain_private_belief_survives", FindConversationCorrectionViolations(doubtful, lodging, history, "npc").Count == 0);
            Check("real_boundary_survives_without_external_quote", FindConversationCorrectionViolations(TestDict("reply", "I will not help until you name the governor whose keep sheltered you."), lodging, history, "npc").Count == 0);
            var unrelatedEvidence = TestDict("reply", bad + " I ask because I must.", "correctionEvidence", new[] {
                TestDict("turnId", "new_evidence", "quote", ReadString(newEvidence, "text", ""), "replyQuote", "I ask because I must.") });
            Check("unrelated_valid_quote_cannot_disable_all_guards", FindConversationCorrectionViolations(unrelatedEvidence, lodging, history, "npc").Count > 0);
            var unchangedPlan = TestDict("reply", "I understand.", "stateUpdates", TestDict("currentPlan", ReadString(payload, "contestedCurrentPlan", "")));
            Check("unchanged_plan_cannot_fake_reconciliation", FindConversationCorrectionViolations(unchangedPlan, lodging, history, "npc", payload).Count > 0);
            const string water = "I pour each of you a cup of water.";
            var group = new List<Dictionary<string, object>> { TestDict("role", "player", "text", water),
                TestDict("role", "npc", "speaker_id", "other", "speaker", "Ira", "text", "*She takes the cup in both hands — the grip of a woman who no longer trusts one — and drinks half of it before she rules.*") };
            string mirrored = "*She accepts the cup with both hands — the morning's honesty about her own grip — and drinks half before she speaks.*";
            Check("captured_same_beat_cup_gesture_is_detected", FindGroupExpressionEchoes(TestDict("reply", mirrored), water, group, "npc", "Rhagaea")
                .Any(v => ReadString(v, "type", "") == "group_conversation_gesture_echo"));
            Check("independent_cup_reaction_is_allowed", FindGroupExpressionEchoes(TestDict("reply", "*She accepts the water.* Thank you."), water, group, "npc", "Rhagaea").Count == 0);
            Check("older_other_speaker_gesture_is_not_same_beat", FindGroupExpressionEchoes(TestDict("reply", mirrored), "Something else.", group, "npc", "Rhagaea").Count == 0);
            string record = RenderContinuityRecord("source", "letter", "npc", "session", 1d, 119, true, "An exact supplied letter.");
            Check("exact_source_dedup_retains_original_once", OmitExactSuppliedContinuityRecords("RULES\n" + record + "\nOTHER", record) == "RULES\n\nOTHER");
            Check("same_id_different_revision_is_not_dropped", OmitExactSuppliedContinuityRecords(record.Replace("supplied", "revised"), record).Contains("revised"));
            Check("observer_and_session_dedup_boundaries", OmitExactSuppliedContinuityRecords(record.Replace("observer=npc", "observer=other"), record).Contains("observer=other")
                && OmitExactSuppliedContinuityRecords(record.Replace("session=session", "session=other"), record).Contains("session=other"));

            string campaign = "correction_fixture_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> Reply(string text) { var p = AgencyTestReply(null); p["reply"] = text; p["body"] = text;
                    p["actionGate"] = TestDict("needed", false, "commitment", "roleplay_only", "intent", "", "confidence", 1d);
                    return CompleteConversationStructuredResponse(p, "dialogue", out _); }
                int calls = 0;
                var corrected = Reply(ReadString(good, "reply", "")); corrected["stateUpdates"] = ReadDictionary(good, "stateUpdates");
                var input = Reply(bad);
                var result = RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(input)), TestDict("requestType", "dialogue"),
                    TestDict("playerText", lodging, "contestedCurrentPlan", ReadString(payload, "contestedCurrentPlan", "")), TestDict("knowsIdentity", true), history,
                    campaign, "correction_repair", "dialogue", "npc", "Rhagaea", "Player", "", req => { calls++; return TestDict("ok", true, "content", Json.Serialize(corrected)); });
                Check("production_repair_changes_dependent_state", calls == 1 && ReadBool(ReadDictionary(result, "roleplayContinuityRepair"), "accepted", false)
                    && ReadString(ReadDictionary(TryParseJsonObject(ReadString(result, "content", "")), "stateUpdates"), "currentPlan", "") == "Question my courier about the missing letter.", result);
                RunNarrativeClockCorrectionTests(campaign, Check);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return rows;
        }
    }
}
