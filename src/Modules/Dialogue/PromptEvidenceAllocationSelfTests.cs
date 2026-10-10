using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunPromptEvidenceAllocationAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "prompt_evidence_" + id,
                "passed", pass, "summary", id, "data", data));
            const string answer = "Player: Tournaments fund neutral caravans, intelligence and elite units; then we buy land from indebted lords.";
            const string question = "NPC: How will we build an independent nation?";
            const string acknowledgement = "NPC: I heard that plan; I doubt whether the first seller will agree.";
            string answerBundle = RenderContinuityRecord("plan_bundle", "resolved_sequence", "npc", "old_session", 1d, 105, true,
                question + "\n" + answer + "\n" + acknowledgement);
            string optional = RenderContinuityRecord("optional_related", "reported_exchange", "npc", "old_session", 1d, 95, false,
                "A relevant earlier account. " + new string('r', 850));
            string transcript = string.Join("\n\n", Enumerable.Range(0, 8).Select(i => RenderContinuityRecord("t" + i,
                "accepted_exchange", "npc", "now", 2d, 80, false, "Earlier small talk. " + new string('x', 450))));
            var ranked = SelectSharedPromptRecords(optional, transcript, optional.Length + 800, new List<object>());
            Check("relevance_competes_across_sections", ranked.Context.Contains(optional)
                && ranked.Transcript.Length > 0 && ranked.Transcript.Length < transcript.Length);

            var payload = TestDict("mode", "party_chat", "conversationSessionId", "family", "sceneTurnId", "beat3", "worldDay", 2d);
            string group = ProtectSharedGroupPrompt("NPC B asked Which town? Player opportunity: none yet.", "npc", payload);
            var mandatory = SelectSharedPromptRecords(answerBundle + "\n\n" + group, transcript, 0, new List<object>());
            Check("whole_answer_and_group_opportunity_survive_full_budget", mandatory.Context.Contains(question)
                && mandatory.Context.Contains(answer) && mandatory.Context.Contains(acknowledgement) && mandatory.Context.Contains("none yet"));
            CaptureRequiredPromptEvidence(payload, "selectedContextAndMemory", mandatory.Context);
            var evidence = TestDict("requiredContent", CapturedRequiredPromptEvidence(payload));
            Check("proof_contains_supporting_content_not_only_ids", MissingContinuityContent("plan_bundle group_state", evidence).Count == 2);
            Check("missing_answer_rejected_even_with_correct_ids", MissingContinuityContent(mandatory.Context.Replace(answer, ""), evidence).Contains("plan_bundle"));
            payload["requiredPromptEvidence"] = new[] { TestDict("id", "forged", "text", "invented") };
            Check("client_diagnostics_cannot_replace_expected_sources", CapturedRequiredPromptEvidence(payload).Count == 2
                && CapturedRequiredPromptEvidence(payload).All(r => ReadString(r, "id", "") != "forged"));

            var values = new Dictionary<string, string> { ["playerText"] = "You have heard our plan.", ["contextPullText"] = answerBundle + "\n\n" + group,
                ["priorDialogueText"] = transcript };
            var budget = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", values,
                "Family conversation.", "Attributed speakers.", "", "Rules.", "Profile.", targetOverride: 9000,
                capacity: TestDict("inputAllowance", 32000, "outputFits", true));
            var envelope = CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", budget.LiveTurn);
            payload["continuityEvidence"] = evidence;
            FinalizeContinuityPromptBudget(envelope, payload, TestDict("inputAllowance", 32000, "outputFits", true));
            Check("actual_compacted_messages_preserve_protected_content", ReadBool(ReadDictionary(envelope.Diagnostics, "continuityPreflight"), "requiredSourceCoverageComplete", false));
            bool rejected = false;
            foreach (var message in envelope.Messages) message["content"] = ReadString(message, "content", "").Replace(answer, "");
            try { FinalizeContinuityPromptBudget(envelope, payload, TestDict("inputAllowance", 32000, "outputFits", true)); }
            catch (InvalidOperationException) { rejected = true; }
            Check("adapter_losing_answer_fails_actual_prompt_preflight", rejected);
            ResetRequiredPromptEvidence(payload);
            Check("reused_request_does_not_keep_stale_expectations", CapturedRequiredPromptEvidence(payload).Count == 0);

            string packedAnswer = RenderContinuityRecord("packed_answer", "reported_exchange", "npc", "older", 1d, 84, false,
                "Player: Will you accept 300 denars after the caravan arrives?\n"
                + "[background narration omitted; original retained]\n" + new string('b', 5800)
                + "\nNPC: I accept 300 denars, payable only after the caravan arrives. Nothing has been paid.");
            var reserveValues = new Dictionary<string, string> { ["playerText"] = "Remind me of the caravan price.",
                ["contextPullText"] = packedAnswer, ["priorDialogueText"] = "" };
            string reservedLive = AssembleConversationLiveTurn("dialogue_live_turn_template.txt", reserveValues, "", "", "");
            int Serialized(string prefix) => EstimateContinuityTokens(Json.Serialize(new[] {
                TestDict("role", "system", "content", prefix), TestDict("role", "system", "content", "Profile."),
                TestDict("role", "user", "content", reservedLive) }));
            string fixedPrefix = new string('x', Math.Max(0, (31000 - Serialized("")) * 3));
            Check("metadata_reserve_fixture_crosses_only_reserved_boundary", Serialized(fixedPrefix) > 32000 - 2048
                && Serialized(fixedPrefix) <= 32000 && fixedPrefix.Length + reservedLive.Length + "Profile.".Length <= 96000);
            PromptLiveTurnBudgetResult ReserveCase(int allowance) => BuildBudgetedConversationLiveTurn(
                "dialogue_live_turn_template.txt", "priorDialogueText", new Dictionary<string, string>(reserveValues),
                "", "", "", fixedPrefix, "Profile.", capacity: TestDict("inputAllowance", allowance, "outputFits", true, "capacityVerified", true));
            var expanded = ReserveCase(96000);
            Check("metadata_reserve_expands_before_dropping_selected_answer", expanded.ContextPullText.Contains(packedAnswer)
                && ReadInt(expanded.Diagnostics, "targetInputTokens", 0) == 48000
                && ReadInt(expanded.Diagnostics, "protectedInputTokenEstimate", 0) + 2048 <= 32000
                && ReadBool(expanded.Diagnostics, "targetMet", false));
            var bounded = ReserveCase(32000);
            Check("metadata_reserve_never_exceeds_verified_capacity", !bounded.ContextPullText.Contains(packedAnswer)
                && ReadInt(bounded.Diagnostics, "targetInputTokens", 0) <= 32000
                && ReadBool(bounded.Diagnostics, "targetMet", false));

            // A newer lexical hit must not displace the reranker's better answer
            // when its original score is larger. Exercise both allocation passes.
            var bestEvidence = new MemoryEvidence { SourceId = "ranked_answer", Kind = "reported_exchange", Owner = "npc", Session = "older",
                Score = 82, SelectionRank = 0, SelectionRelevance = .9d,
                Text = "Player: When will you decide?\nNPC: After the caravan returns. " + new string('a', 5700) };
            var weakerEvidence = new MemoryEvidence { SourceId = "weaker_match", Kind = "reported_exchange", Owner = "npc", Session = "newer",
                Score = 84, SelectionRank = 7, SelectionRelevance = .1d,
                Text = "A different caravan was discussed. " + new string('b', 5700) };
            string bestRecord = RenderMemoryEvidence(bestEvidence, "ranked_answer");
            string weakerRecord = RenderMemoryEvidence(weakerEvidence, "weaker_match");
            Check("renderer_preserves_validated_selection_rank", bestRecord.Contains(" selectionRank=0")
                && weakerRecord.Contains(" selectionRank=7"));
            Check("rank_header_roundtrips_without_splitting_source", ContinuityPromptRecords(bestRecord, false).Single().SelectionRank == 0
                && ContinuityPromptRecords(bestRecord, false).Single().Text == bestRecord);
            Check("legacy_header_remains_rank_free_and_readable", !packedAnswer.Contains(" selectionRank=")
                && ContinuityPromptRecords(packedAnswer, false).Single().SelectionRank == null);
            string overflowRank = bestRecord.Replace(" selectionRank=0", " selectionRank=999999999999999999999");
            Check("invalid_rank_cannot_overflow_or_gain_priority", ContinuityPromptRecords(overflowRank, false).Single().SelectionRank == null);
            string quotedRank = RenderContinuityRecord("quoted", "reported_exchange", "npc", "older", 1d, 80, false, bestRecord);
            Check("quoted_record_cannot_inject_selection_rank", ContinuityPromptRecords(quotedRank, false).Count == 1
                && ContinuityPromptRecords(quotedRank, false).Single().SelectionRank == null);
            var rankedSelection = SelectSharedPromptRecords(weakerRecord + "\n\n" + bestRecord, "", bestRecord.Length + 2, new List<object>());
            Check("shared_allocator_honors_rank_above_legacy_score", rankedSelection.Context.Contains(bestRecord)
                && !rankedSelection.Context.Contains(weakerRecord));
            string rankedFloor = RenderContinuityRecord("required_boundary", "boundary", "npc", "now", 2d, 1, true,
                "The price is only a proposal; no payment was made.");
            var rankedValues = new Dictionary<string, string> { ["playerText"] = "When will you decide?", ["priorDialogueText"] = "",
                ["contextPullText"] = rankedFloor + "\n\n" + weakerRecord + "\n\n" + bestRecord };
            string rankedLive = AssembleConversationLiveTurn("dialogue_live_turn_template.txt", rankedValues, "", "", "");
            int rankedBaseTokens = EstimateContinuityTokens(Json.Serialize(new[] {
                TestDict("role", "system", "content", ""), TestDict("role", "system", "content", "Profile."),
                TestDict("role", "user", "content", rankedLive) }));
            string rankedPrefix = new string('x', Math.Max(0, (31000 - rankedBaseTokens) * 3));
            var rankedBudget = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", rankedValues,
                "", "", "", rankedPrefix, "Profile.", capacity: TestDict("inputAllowance", 32000, "outputFits", true, "capacityVerified", true));
            Check("serialized_allocator_preserves_better_rank_and_required_floor", rankedBudget.ContextPullText.Contains(bestRecord)
                && rankedBudget.ContextPullText.Contains(rankedFloor) && !rankedBudget.ContextPullText.Contains(weakerRecord)
                && ReadBool(rankedBudget.Diagnostics, "targetMet", false));
            Check("serialized_rank_test_reaches_actual_token_trim", ReadObjectList(rankedBudget.Diagnostics, "actions")
                .OfType<Dictionary<string, object>>().Any(d => ReadString(d, "reason", "") == "optional_whole_record_exceeds_serialized_token_allowance"));
            return results;
        }
    }
}
