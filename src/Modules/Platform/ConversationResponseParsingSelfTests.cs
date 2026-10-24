using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationResponseParsingSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => rows.Add(TestDict("caseId", "response_recovery_" + id,
                "suite", "interaction_architecture", "passed", pass, "summary", id, "data", data));
            string campaign = "response_recovery_test_" + Guid.NewGuid().ToString("N");
            var profile = TestDict("heroStringId", "npc", "name", "Fixture companion", "age", 30,
                "attributes", TestDict("endurance", 4));
            Dictionary<string, object> Decision() => TestDict("kind", "none", "proposalId", "", "revision", 0,
                "termsHash", "", "reason", "Ordinary conversation.", "clauses", new object[0]);
            Dictionary<string, object> Drink() => TestDict("id", "sip_1", "operation", "consume",
                "holderHeroId", "npc", "vesselId", "fixture_skin", "beverage", "whiskey", "kind", "skin",
                "serving", "sip", "count", 1, "completed", true);
            Dictionary<string, object> Answer()
            {
                var answer = TestDict("reply", "*She takes one short pull.* Partners, then. When do we start?",
                    "structuredActionDecision", Decision(), "drinkActions", new[] { Drink() },
                    "memoryWrites", new[] { TestDict("text", "The player named her a partner.", "importance", .7) },
                    "comprehensionWrites", new[] { TestDict("text", "PRIVATE interpretation, never spoken.",
                        "stance", "cautiously satisfied", "confidence", .7, "sourceEvidence", TestDict("quote", "partner")) },
                    "continuityWrites", new[] { TestDict("kind", "question", "status", "resolved", "subject", "partnership") });
                return CompleteConversationStructuredResponse(answer, "dialogue", out _);
            }
            Dictionary<string, object> Request() => TestDict("requestType", "dialogue", "privateDrinkActionsRequired", true,
                "structuredActionProtocolVersion", Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion,
                "structuredActionRepairContext", TestDict("playerId", "player", "npcId", "npc", "conversationId", "fixture"),
                "messages", new[] { TestDict("role", "system", "content", "Fixture original context.") });
            Dictionary<string, object> Recover(string content, Dictionary<string, object> request, Func<Dictionary<string, object>, Dictionary<string, object>> responder) =>
                RetryMalformedStructuredResponse(TestDict("ok", true, "content", SanitizeReasoningContent(content), "finishReason", "length"),
                    request, campaign, "fixture", "dialogue", "npc", "", responder);
            try
            {
                var complete = Answer();
                string full = Json.Serialize(complete);
                string cutDrink = "{\"reply\":" + Json.Serialize(ReadString(complete, "reply", ""))
                    + ",\"memoryWrites\":[{\"text\":\"PRIVATE memory\",\"importance\":0.7}],"
                    + "\"comprehensionWrites\":[{\"text\":\"PRIVATE interpretation, never spoken.\",\"stance\":\"satisfied\",\"confidence\":0.7}],"
                    + "\"structuredActionDecision\":" + Json.Serialize(Decision())
                    + ",\"drinkActions\":[{\"id\":\"sip_1\",\"operation\":\"consume\",\"vesselId\":\"cut";
                Check("sanitizer_preserves_truncated_outer_document", SanitizeReasoningContent(cutDrink) == cutDrink
                    && TryParseJsonObject(cutDrink) == null);
                var prefix = ReadConversationResponseEnvelope(cutDrink);
                Check("captured_shape_keeps_outer_reply_and_decision", prefix.Truncated && prefix.Error == ""
                    && prefix.IncompleteField == "drinkActions" && !prefix.Fields.ContainsKey("drinkActions")
                    && ReadString(prefix.Fields, "reply", "") == ReadString(complete, "reply", "")
                    && ReadDictionary(prefix.Fields, "structuredActionDecision") != null);
                int calls = 0;
                var repaired = Recover(cutDrink, Request(), request =>
                {
                    calls++;
                    Check("regeneration_has_full_context_and_16000_allowance", ReadInt(request, "maxTokens", 0) >= 16000
                        && ReadBool(request, "reasoningDisabled", false)
                        && ReadDictionaryList(request, "messages").Any(m => ReadString(m, "content", "").Contains("Fixture original context.")));
                    return TestDict("ok", true, "content", full);
                });
                var accepted = TryParseJsonObject(ReadString(repaired, "content", ""));
                Check("full_pipeline_never_promotes_private_text", calls == 1 && ReadBool(repaired, "ok", false)
                    && ReadString(accepted, "reply", "").StartsWith("*She takes one short pull.*", StringComparison.Ordinal)
                    && !ReadString(accepted, "reply", "").Contains("PRIVATE")
                    && ReadDictionaryList(accepted, "drinkActions").Count == 1
                    && ReadDictionaryList(accepted, "continuityWrites").Count == 1);
                var evidence = ReadDictionary(repaired, "responseRecovery");
                Check("regenerated_turn_retains_failure_provenance", ReadString(evidence, "incompleteField", "") == "drinkActions"
                    && !ReadBool(evidence, "effectsComplete", true) && ReadBool(evidence, "regenerated", false)
                    && ReadString(evidence, "originalContentSha256", "") == ConversationExactTextHash(cutDrink));

                string completePrefix = full.Substring(0, full.Length - 1);
                calls = 0;
                var recovered = Recover(completePrefix, Request(), request => { calls++; throw new InvalidOperationException("No call expected."); });
                var recoveredObject = TryParseJsonObject(ReadString(recovered, "content", ""));
                Check("closed_outer_fields_preserved_without_provider_call", calls == 0 && ReadBool(recovered, "ok", false)
                    && ReadString(recoveredObject, "reply", "") == ReadString(complete, "reply", "")
                    && CanonicalJson(recoveredObject["memoryWrites"]) == CanonicalJson(complete["memoryWrites"])
                    && CanonicalJson(recoveredObject["structuredActionDecision"]) == CanonicalJson(complete["structuredActionDecision"]));

                calls = 0;
                var failed = Recover(cutDrink, Request(), request => { calls++; return TestDict("ok", true, "content", cutDrink); });
                Check("repeat_truncation_fails_after_one_regeneration", calls == 1 && !ReadBool(failed, "ok", true)
                    && ReadInt(ReadDictionary(failed, "formatRetry"), "maximumAttempts", 0) == 1);
                foreach (string unknownTail in new[] { ",\"futureEffect\":[", ",\"futureEff" })
                {
                    calls = 0;
                    var unknown = Recover(completePrefix + unknownTail, Request(), request => { calls++; return TestDict("ok", true, "content", full); });
                    Check("unfinished_unknown_field_requires_regeneration_" + unknownTail.Length,
                        calls == 1 && ReadBool(unknown, "ok", false));
                }
                var partialArray = ReadConversationResponseEnvelope("{\"reply\":\"visible\",\"drinkActions\":["
                    + Json.Serialize(Drink()) + ",{\"id\":\"second");
                Check("partial_array_contributes_no_completed_entries", partialArray.Truncated
                    && !partialArray.Fields.ContainsKey("drinkActions"));

                foreach (var privateObject in new[]
                {
                    TestDict("text", "PRIVATE memory", "importance", .7, "tags", new[] { "memory" }),
                    TestDict("text", "PRIVATE comprehension", "stance", "warm", "confidence", .7, "sourceEvidence", TestDict("quote", "yes")),
                    TestDict("text", "PRIVATE observation", "sourceEvidence", TestDict("speakerId", "npc"))
                }) Check("private_text_alias_rejected_" + privateObject.Count, !ConversationStructuredObjectHasUsableVisibleReply(privateObject));

                foreach (string bad in new[]
                {
                    "{\"reply\":\"first\",\"reply\":\"second\"}",
                    "{\"reply\":\"first\",\"Reply\":\"second\"}",
                    "{\"reply\":\"first\"}{\"reply\":\"second\"}",
                    "{\"reply\":\"abandoned prefix {\"reply\":\"nested\",\"actionGate\":{\"needed\":false}}",
                    "{\"comprehensionWrites\":[{\"reply\":\"PRIVATE nested alias\"}],\"unfinished\":"
                })
                {
                    var parsed = ReadConversationResponseEnvelope(bad);
                    Check("ambiguous_document_rejected_" + PromptHash(bad).Substring(0, 8), TryParseJsonObject(bad) == null
                        && (parsed.Error.Length > 0 || parsed.ReplyStart < 0));
                }
                string escaped = "\"Partner\" \\ é 中 👑 {\"reply\":\"quoted JSON\"} <think>literal quotation</think>";
                string escapedJson = "{\"reply\":" + Json.Serialize(escaped) + ",\"drinkActions\":[]";
                var escapedEnvelope = ReadConversationResponseEnvelope(escapedJson);
                Check("unicode_quotes_and_json_in_reply_are_preserved", ReadString(escapedEnvelope.Fields, "reply", "") == escaped
                    && ReadString(TryParseJsonObject(SanitizeReasoningContent(escapedJson + "}")), "reply", "") == escaped);
                Check("provider_reasoning_preamble_removed_only_outside_json", ReadString(TryParseJsonObject(
                    SanitizeReasoningContent("<think>private preamble</think>" + Json.Serialize(TestDict("reply", escaped)))), "reply", "") == escaped);
                Check("literal_unescaped_think_tags_are_authored_text", ReadString(TryParseJsonObject(
                    SanitizeReasoningContent("{\"reply\":\"<think>quoted tag</think>\"}")), "reply", "") == "<think>quoted tag</think>");
                Check("complete_fenced_envelope_supported", ReadString(TryParseJsonObject("```json\n" + full + "\n```"), "reply", "") == ReadString(complete, "reply", ""));
                string fencedSource = " \r\n```json\r\n  {\"reply\":\"é👑\",\"drinkActions\":[]\r\n```  ";
                var fencedEnvelope = ReadConversationResponseEnvelope(fencedSource);
                byte[] fencedBytes = System.Text.Encoding.UTF8.GetBytes(fencedSource);
                string spannedReply = System.Text.Encoding.UTF8.GetString(fencedBytes, fencedEnvelope.ReplyStart,
                    fencedEnvelope.ReplyEnd - fencedEnvelope.ReplyStart);
                Check("reply_span_binds_original_fenced_utf8", Json.Deserialize<string>(spannedReply) == "é👑"
                    && ConversationExactTextHash(fencedSource) != ConversationExactTextHash(fencedSource.Trim()));

                var watch = Stopwatch.StartNew();
                bool allCutoffs = true;
                for (int cut = 0; cut < full.Length; cut++)
                {
                    var part = ReadConversationResponseEnvelope(full.Substring(0, cut));
                    if (part.Fields.TryGetValue("reply", out object value) && !Equals(value, complete["reply"])) allCutoffs = false;
                    if (part.Fields.TryGetValue("drinkActions", out value) && CanonicalJson(value) != CanonicalJson(complete["drinkActions"])) allCutoffs = false;
                }
                Check("every_cutoff_keeps_only_complete_root_values", allCutoffs, TestDict("cutoffs", full.Length, "durationMs", watch.ElapsedMilliseconds));
                Check("oversized_recovery_is_bounded", ReadConversationResponseEnvelope("{" + new string(' ', ConversationResponseRecoveryCharacterLimit)).Error == "recovery_size_limit");
                Check("explicit_larger_token_allowance_retained", StructuredDialogueResponseMaxTokens(TestDict("maxTokens", 32000)) == 32000
                    && StructuredEventResponseMaxTokens(TestDict("maxTokens", 32000)) == 32000);
                foreach (bool social in new[] { false, true })
                {
                    var envelope = new PromptEnvelope { Messages = new List<Dictionary<string, object>> { TestDict("role", "system", "content", "Fixture rules.") },
                        Diagnostics = TestDict("drinkPromptBudget", TestDict("schema", "fixture")) };
                    var generationRequest = BuildConversationGenerationRequest(TestDict("maxTokens", 900), envelope,
                        campaign, "budget", "npc", "Hello.", social);
                    generationRequest["hero"] = profile;
                    var body = BuildChatRequestBody(TestDict(), generationRequest, ReadDictionaryList(generationRequest, "messages"), "fixture-model");
                    Check("actual_outbound_16000_" + social, ReadInt(body, "max_tokens", 0) == 16000
                        && ConversationResponseNeedsDrinkActions(generationRequest));
                }

                var payload = TestDict("campaignId", campaign, "timelineId", "fixture", "turnId", "same-turn",
                    "sceneTurnId", "same-turn", "worldDay", 10d, "playerHeroStringId", "player", "conversationSessionId", "fixture");
                ApplyConversationDrinking(campaign, "npc", payload, profile, null, accepted, ReadString(accepted, "reply", ""));
                var retryPayload = CloneDictionary(payload);
                ApplyConversationDrinking(campaign, "npc", retryPayload, profile, null, accepted, ReadString(accepted, "reply", ""));
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var state = TryParseJsonObject(ReadString(QuerySql(connection,
                        "SELECT state_json FROM conversation_intoxication WHERE hero_id='npc';").Single(), "state_json", "{}"));
                    Check("recovered_drink_commits_exactly_once_across_retry", ReadDouble(state, "drinks", -1) == .25
                        && ReadLong(state, "alcoholConsumptionEvents", 0) == 1
                        && QuerySql(connection, "SELECT turn_key FROM conversation_drinking_turns WHERE hero_id='npc';").Count == 1);
                }
            }
            catch (Exception ex)
            {
                rows.Add(TestDict("caseId", "response_recovery_exception", "suite", "interaction_architecture",
                    "passed", false, "summary", ex.ToString()));
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return rows;
        }
    }
}
