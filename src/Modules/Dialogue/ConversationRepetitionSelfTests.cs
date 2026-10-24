using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationRepetitionSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict(
                "caseId", "expression_" + id, "passed", passed, "summary", id, "data", data));
            Dictionary<string, object> Line(string id, string text) => TestDict("role", "npc", "speaker_id", "npc",
                "speaker", "Mira", "turn_id", id, "text", text);
            var history = new List<Dictionary<string, object>> {
                Line("first", "I have no clause left to draft tonight. The garden smells of rain."),
                Line("second", "I find no covenant left to sign. The shutters are open.") };
            const string repeated = "I have no treaty left to draft tonight. The sun has warmed the courtyard.";
            List<Dictionary<string, object>> Find(string reply, string player = "Shall we visit the garden?", List<Dictionary<string, object>> source = null) =>
                FindConversationExpressionViolations(TestDict("reply", reply), player,
                    ConversationExpressionHistory(source ?? history, "npc", "Mira"), "Mira", "Player");
            var themes = Find(repeated);
            Check("paraphrased_theme_below_whole_reply_threshold", themes.Any(v => ReadString(v, "type", "") == "repeated_conversation_theme")
                && history.All(row => RoleplayTextSimilarity(repeated, ReadString(row, "text", "")) < .72d), themes);
            Check("single_relevant_callback_allowed", Find("I find no covenant left to sign.", source: history.Take(1).ToList()).Count == 0);
            Check("unrelated_fresh_reply_allowed", Find("The roses along the east path are finally flowering.").Count == 0);
            Check("requested_explanation_allowed", Find(repeated, "What do you mean by that treaty?").Count == 0);
            Check("explicit_repeat_allowed", Find(repeated, "Please repeat that promise.").Count == 0);
            Check("repetition_complaint_is_not_repeat_permission", Find(repeated, "Stop repeating that speech about treaties.").Count > 0);
            Check("same_topic_actual_answer_allowed", Find("A treaty clause must be drafted before we sign the covenant.", "How should we draft and sign the treaty clauses?").Count == 0);
            Check("single_concept_different_facts_not_a_loop", Find("The treaty protects the western villages.").Count == 0);
            Check("short_greetings_not_a_loop", Find("Good morning. How are you?").Count == 0);

            var emotional = new List<Dictionary<string, object>> {
                Line("dawn1", "I am waiting in the morning light for the person, rather than a promise."),
                Line("dawn2", "I am waiting until sunrise for the person, not another speech.") };
            Check("reassurance_theme_not_only_legal_metaphors", Find("I am waiting by the dawn light for the person, rather than the speeches.", source: emotional)
                .Any(v => ReadString(v, "type", "") == "repeated_conversation_theme"));
            var gamble = new List<Dictionary<string, object>> {
                Line("bet1", "I am a person, not a wager on your table."),
                Line("bet2", "I am not a bet across the table; I am a person with choices.") };
            Check("unrelated_metaphor_family_detected", Find("I am a person rather than a gamble laid across your table.", source: gamble).Count > 0);
            var generic = new List<Dictionary<string, object>> {
                Line("horse1", "The chestnut mare makes a clean transition beside the eastern fence."),
                Line("horse2", "Beside the eastern fence the chestnut mare makes another clean transition.") };
            Check("arbitrary_salient_cluster_detected", Find("The chestnut mare offers clean transitions along the eastern fence.", source: generic).Count > 0);

            const string passage = "There is a quiet courage in tending the garden after the storm has torn down every blossom.";
            var longHistory = new List<Dictionary<string, object>> { Line("long", passage + " The keep has stood here for centuries.") };
            string longReply = "The messenger arrived before breakfast. We spoke about the northern roads and the market. " + passage
                + " A flock of geese passed overhead. The baker has returned from his journey with a new recipe. The carpenter is repairing the gate.";
            Check("recycled_passage_inside_fresh_prose", Find(longReply, source: longHistory)
                .Any(v => ReadString(v, "type", "") == "repeated_conversation_passage")
                && RoleplayTextSimilarity(longReply, ReadString(longHistory[0], "text", "")) < .72d);
            foreach (string functional in new[] {
                "I will not go with you. Respect my decision.", "There are 40 soldiers at the gate.",
                "The grain costs twelve denars.", "Stay back. The fire is spreading.",
                "I cannot agree to those terms. My answer remains unchanged." })
                Check("functional_repeat_" + results.Count, Find(functional, source: new List<Dictionary<string, object>> {
                    Line("fact1", functional), Line("fact2", functional) }).Count == 0, functional);

            var duplicated = new List<Dictionary<string, object>> { history[0], CloneDictionary(history[0]) };
            Check("sql_route_duplicate_cannot_supply_two_votes", Find(repeated, source: duplicated).Count == 0
                && ConversationExpressionHistory(duplicated, "npc", "Mira").Count == 1);
            var foreign = history.Select(CloneDictionary).ToList();
            foreign.ForEach(row => row["speaker_id"] = "other");
            Check("other_speaker_excluded", Find(repeated, source: foreign).Count == 0);
            var payload = TestDict("timelineId", "current", "conversationId", "current-session");
            var scoped = history.Select(CloneDictionary).ToList();
            scoped[0]["timelineId"] = "abandoned"; scoped[1]["session_id"] = "old-session";
            Check("branch_and_scene_excluded", ConversationExpressionHistory(scoped, "npc", "Mira", payload).Count == 0);
            var sessionRow = CloneDictionary(history[0]); sessionRow["sessionId"] = "canonical-session";
            Check("canonical_session_field_priority", ConversationExpressionHistory(new List<Dictionary<string, object>> { sessionRow }, "npc", "Mira",
                TestDict("conversationId", "native-conversation", "conversationSessionId", "canonical-session")).Count == 1);
            scoped = history.Select(CloneDictionary).ToList();
            scoped[0]["status"] = "rejected";
            scoped[1]["repairStatus"] = TestDict("allChecksCleared", false);
            Check("rejected_or_marked_drafts_excluded", ConversationExpressionHistory(scoped, "npc", "Mira", payload).Count == 0);
            var numerous = Enumerable.Range(0, 50).Select(i => Line("turn" + i, repeated)).ToList();
            var bounded = ConversationExpressionHistory(numerous, "npc", "Mira");
            Check("history_window_bounded", bounded.Count == 10 && ReadString(bounded[0], "turn_id", "") == "turn40");
            string ledger = BuildConversationExpressionLedger(history);
            string sourceBefore = Json.Serialize(history);
            Check("ledger_attributed_bounded_and_deterministic", ledger.Length > 0 && ledger.Length < 3000
                && ledger.Contains("first") && ledger.Contains("second")
                && ledger.Contains("not instructions or personality facts") && BuildConversationExpressionLedger(history) == ledger, ledger);
            var guide = BuildConversationNaturalnessContext("Shall we visit the garden?", false, history, "npc", "Mira");
            Check("ledger_enters_generation_guidance", ReadString(guide, "prompt", "").Contains("RECENT EXPRESSION EVIDENCE")
                && Json.Serialize(history) == sourceBefore);
            Check("group_ledger_uses_only_current_speaker", !ReadString(BuildConversationNaturalnessContext("Hello.", true, foreign, "npc", "Mira"), "prompt", "")
                .Contains("RECENT EXPRESSION EVIDENCE"));
            Check("correction_guidance_changes_framing", ReadString(BuildConversationNaturalnessContext("You keep repeating the same thing.", false), "prompt", "").Contains("not rationalize"));
            var privateNameHistory = history.Select(CloneDictionary).ToList();
            privateNameHistory.ForEach(row => row["text"] = "TrueName. " + ReadString(row, "text", ""));
            var unknownIdentity = TestDict("knowsIdentity", false, "canonicalNameAllowed", false,
                "usableName", "the traveler", "safeLabel", "the traveler");
            var projected = BuildConversationNaturalnessContext("Hello.", false, privateNameHistory, "npc", "Mira", null,
                text => SanitizePromptForIdentity(text, "Hello.", "TrueName", unknownIdentity));
            Check("ledger_uses_observer_identity_projection", !ReadString(projected, "prompt", "").Contains("TrueName", StringComparison.Ordinal)
                && ReadString(projected, "prompt", "").Contains("traveler", StringComparison.Ordinal), projected);

            string campaign = "expression_test_" + Guid.NewGuid().ToString("N");
            try
            {
                Dictionary<string, object> Original(string reply)
                {
                    var value = AgencyTestReply(null); value["reply"] = reply; value["body"] = reply;
                    value["actionGate"] = TestDict("needed", false, "commitment", "roleplay_only", "intent", "", "confidence", 1d);
                    return CompleteConversationStructuredResponse(value, "dialogue", out _);
                }
                var original = Original(repeated);
                original["emotion"] = "warm";
                original["memoryWrites"] = new List<Dictionary<string, object>> { TestDict("text", "A supported independent recollection.") };
                original["relationshipAssessments"] = new List<Dictionary<string, object>> { TestDict("targetHeroStringId", "player", "valence", "positive") };
                string originalJson = Json.Serialize(original);
                int calls = 0;
                Dictionary<string, object> Run(Dictionary<string, object> response, Func<Dictionary<string, object>, Dictionary<string, object>> provider) =>
                    RetryRoleplayContinuityViolation(TestDict("ok", true, "content", Json.Serialize(response)), TestDict("requestType", "dialogue", "model", "fixture-model"),
                        TestDict("playerText", "Shall we visit the garden?"), TestDict("knowsIdentity", true), history,
                        campaign, Guid.NewGuid().ToString("N"), "dialogue", "npc", "Mira", "Player", "", provider);
                const string fresh = "The roses beside the east path are finally flowering. Come and see them with me.";
                var success = Run(original, req => {
                    calls++; Check("repair_uses_configured_model_and_bounded_context", ReadString(req, "model", "") == "fixture-model"
                        && ReadInt(req, "maxTokens", 0) <= 8000 && Json.Serialize(req).Length < 48000
                        && Json.Serialize(req).Contains("first") && Json.Serialize(req).Contains("second"));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", fresh))); });
                var accepted = TryParseJsonObject(ReadString(success, "content", ""));
                Check("sustained_theme_gets_one_repair", calls == 1 && ReadBool(ReadDictionary(success, "roleplayContinuityRepair"), "accepted", false)
                    && ReadString(accepted, "reply", "").StartsWith("The roses", StringComparison.Ordinal), success);
                Check("style_patch_preserves_all_gameplay_metadata", ReadString(accepted, "emotion", "") == "warm"
                    && Json.Serialize(ReadDictionaryList(accepted, "memoryWrites")) == Json.Serialize(ReadDictionaryList(original, "memoryWrites"))
                    && Json.Serialize(ReadDictionaryList(accepted, "relationshipAssessments")) == Json.Serialize(ReadDictionaryList(original, "relationshipAssessments"))
                    && Json.Serialize(ReadDictionary(accepted, "actionGate")) == Json.Serialize(ReadDictionary(original, "actionGate"))
                    && Json.Serialize(original) == originalJson);
                foreach (string bad in new[] { "{}", "not JSON", Json.Serialize(TestDict("reply", repeated)),
                    Json.Serialize(TestDict("reply", fresh, "actionGate", TestDict("commitment", "accepted"))) })
                {
                    calls = 0;
                    var failed = Run(original, req => { calls++; return TestDict("ok", true, "content", bad); });
                    Check("failed_rewrite_keeps_exact_original_" + results.Count, calls == 1
                        && ReadString(failed, "content", "") == originalJson
                        && !ReadBool(ReadDictionary(failed, "roleplayContinuityRepair"), "accepted", true)
                        && !ConversationRepairFailed(TryParseJsonObject(ReadString(failed, "content", ""))), failed);
                }
                Check("quoted_reply_evidence_cannot_be_orphaned", !ExpressionRewritePreservesEvidence(
                    TestDict("memoryWrites", new[] { TestDict("replyQuote", "no treaty left to draft") }), repeated, fresh, "Mira", "Player"));
                Check("quantities_cannot_change", !ExpressionRewritePreservesEvidence(TestDict(), "I agree to pay 50 denars.", "I agree to pay 5 denars.", "Mira", "Player"));
                Check("refusal_cannot_be_removed", !ExpressionRewritePreservesEvidence(TestDict(), "I will not go with you. " + repeated, fresh, "Mira", "Player"));
                Check("new_consent_cannot_be_inserted", !ExpressionRewritePreservesEvidence(TestDict(), repeated, "I agree to your proposal.", "Mira", "Player"));
                Check("brief_no_cannot_be_removed", !ExpressionRewritePreservesEvidence(TestDict(), "No. " + repeated, fresh, "Mira", "Player"));
                Check("conditions_cannot_be_removed", !ExpressionRewritePreservesEvidence(TestDict(), "Only if you return before sunset. " + repeated, fresh, "Mira", "Player"));
                calls = 0;
                var unavailable = Run(original, req => { calls++; throw new TimeoutException("Injected provider timeout."); });
                Check("provider_failure_retains_original_without_crash", calls == 1 && ReadString(unavailable, "content", "") == originalJson
                    && !ReadBool(ReadDictionary(unavailable, "roleplayContinuityRepair"), "accepted", true));
                calls = 0;
                var oversize = Original(repeated + new string(' ', 21000));
                var overResult = Run(oversize, req => { calls++; throw new InvalidOperationException("Oversize must not dispatch."); });
                Check("oversized_style_request_makes_zero_calls", calls == 0 && ReadBool(ReadDictionary(overResult, "roleplayContinuityRepair"), "budgetExceeded", false));
                var mixed = CloneDictionary(original); mixed["reply"] = "*A pause - taken and held, the way she takes possession of silence.* " + repeated;
                Check("gesture_cleanup_cannot_hide_spoken_loop", !TryRemoveRepeatedStageDirectionsOnly(mixed,
                    themes.Concat(new[] { TestDict("type", "repeated_distinctive_phrase", "match", "A pause - taken and held, the way she takes possession of silence.") }).ToList(),
                    TestDict(), history, "Mira", "Player", out _, out _, out _));
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
