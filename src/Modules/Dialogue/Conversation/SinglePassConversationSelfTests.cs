using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunSinglePassConversationSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool ok) => rows.Add(TestDict("caseId", "single_pass_" + id, "passed", ok, "summary", id));
            const string speech = "I'll put it to each of them. If the caravan stands, would you go?";
            foreach (string mode in new[] { "dialogue", "party_chat", "social_event", "castle_chat", "court_life", "family_chambers", "royal_council" })
            {
                int calls = 0;
                var payload = TestDict("worldDay", 20.25d);
                var result = GenerateSinglePassConversation(TestDict(), payload, mode, responder: _ =>
                { calls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", speech))); });
                var parsed = TryParseJsonObject(ReadString(result, "content", ""));
                Check(mode + "_one_call_missing_optional_metadata_delivers", calls == 1 && ReadBool(result, "ok", false)
                    && ReadString(parsed, "reply", "") == speech && !parsed.ContainsKey("structuredActionDecision"));
                string final = speech;
                parsed["repairStatus"] = TestDict("allChecksCleared", false);
                Check(mode + "_no_late_semantic_veto", FinalizeConversationGrounding(payload, "npc", parsed, ref final) && final == speech);
                Check(mode + "_clone_keeps_server_policy", IsSinglePassConversation(Program.CloneDictionary(payload)));
                Check(mode + "_no_time_charge", IntoxicationWorldHour(payload) == 486d && ReadDouble(payload, "worldDay", 0) == 20.25d);
            }
            var forged = TestDict("conversationDeliveryPolicy", "single_pass", "singlePass", true);
            Check("serialized_flags_have_no_authority", !IsSinglePassConversation(forged));
            var transport = TestDict("ok", false, "content", Json.Serialize(TestDict("reply", speech)), "error", "connection failed");
            Check("real_transport_failure_not_promoted", !ReadBool(PrepareSinglePassConversationResponse(transport, "dialogue"), "ok", true));
            foreach (string content in new[] { "{}", "", "{\"reply\":{\"text\":\"secret\"}}", "{\"reply\":12}", "{\"private\":{\"reply\":\"secret\"}}", "{\"reply\":\"first\",\"reply\":\"second\"}", "{\"reply\":\"unfinished", "{\"reply\":\"first\"} {\"reply\":\"second\"}" })
                Check("no_invented_reply_" + PromptHash(content).Substring(0, 8), !ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true, "content", content), "dialogue"), "ok", true));
            var partial = PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"structuredActionDecision\":{\"kind\":\"accept\"},\"reply\":\"Keep talking.\",\"drinkActions\":["), "dialogue");
            var partialParsed = TryParseJsonObject(ReadString(partial, "content", ""));
            Check("truncated_tail_keeps_only_closed_outer_speech", ReadBool(partial, "ok", false)
                && ReadString(partialParsed, "reply", "") == "Keep talking." && !partialParsed.ContainsKey("structuredActionDecision")
                && !partialParsed.ContainsKey("drinkActions"));
            var quiet = PrepareSinglePassConversationResponse(TestDict("ok", true, "content", "{\"participation\":\"quiet\",\"reply\":\"\"}"), "party_chat", true);
            Check("intentional_quiet_survives", ReadBool(quiet, "ok", false));
            foreach (string bad in new[] { "not JSON", "{\"reply\":\"first\",\"reply\":\"second\"}" })
                Check("malformed_group_is_failure_" + PromptHash(bad).Substring(0, 8), !ReadBool(
                    PrepareSinglePassConversationResponse(TestDict("ok", true, "content", bad), "party_chat", true), "ok", true));
            Check("unnamed_private_text_is_never_promoted", !ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"\":\"private thought\",\"privateAssessment\":\"secret\",\"decisionBrief\":{}}"), "dialogue"), "ok", true));
            var letter = GenerateSinglePassConversation(TestDict(), TestDict(), "correspondence", responder: _ =>
                TestDict("ok", true, "content", "{\"body\":\"I received your letter.\"}"));
            Check("letter_delivered_without_repairs", ReadBool(letter, "ok", false)
                && ReadString(PrepareCorrespondenceDelivery(letter), "body", "") == "I received your letter.");
            Check("letter_can_decline_reply", ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"shouldReply\":false,\"body\":\"\"}"), "correspondence"), "ok", false));
            Check("truncated_letter_is_not_regenerated", !ReadBool(PrepareSinglePassConversationResponse(TestDict("ok", true,
                "content", "{\"body\":\"I received it.\",\"privateAssessment\":["), "correspondence"), "ok", true));
            var actionPayload = TestDict("structuredActionProtocolVersion", Reign.Core.Contracts.Actions.ActionNegotiation.ProtocolVersion,
                "playerHeroStringId", "player", "heroStringId", "npc", "structuredActionContext", TestDict("playerId", "player", "npcId", "npc", "conversationId", "session"));
            var actionResult = GenerateSinglePassConversation(TestDict(), actionPayload, "dialogue", responder: _ => TestDict("ok", true,
                "content", Json.Serialize(TestDict("reply", speech, "structuredActionDecision", TestDict("kind", "accept", "proposalId", "forged", "revision", 99, "termsHash", "wrong", "clauses", new object[0])))));
            var actionParsed = TryParseJsonObject(ReadString(actionResult, "content", ""));
            CaptureStructuredActionDecision(actionPayload, actionParsed);
            Check("invalid_consent_does_not_hide_speech_or_authorize_action", ReadBool(actionResult, "ok", false)
                && ReadString(actionParsed, "reply", "") == speech
                && !ReadBool(ReadDictionary(actionPayload, "validatedStructuredActionDecision"), "valid", true));
            var oldState = TestDict("drinks", 4d, "hour", 510d, "hasClock", true);
            var rebased = RecoverIntoxication(oldState, 4d, 480d);
            Check("legacy_future_clock_rebased_without_recovery", rebased.Hour == 480d && rebased.Drinks == 4d && ReadDouble(oldState, "hour", 0) == 510d);
            Check("native_progression_recovers_after_rebase", RecoverIntoxication(IntoxicationRecord(rebased), 4d, 482d).Drinks == 3d);
            var narrated = TestDict("worldDay", 20d, "narrativeClockDiagnostic", TestDict("hour", 9999d));
            PrepareNarrativeClock(null, narrated, "npc");
            Check("old_or_forged_narrative_clock_has_no_effect", IntoxicationWorldHour(narrated) == 480d && !narrated.ContainsKey("narrativeClockDiagnostic"));
            return rows;
        }
    }
}
