using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationFailureRecoverySelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => rows.Add(TestDict("caseId", "conversation_recovery_" + id,
                "passed", passed, "summary", id));
            const string speech = "I remember your choice of the old mill. Which gate should we use?";
            foreach (string failure in new[] { "action_verification", "continuity_evidence", "memory_rewrite", "dialogue_validation" })
            {
                var failed = VisibleFailedRepairWithoutEffects(TestDict("reply", speech,
                    "memoryWrites", new[] { TestDict("text", "Unsupported lasting fact.") },
                    "relationshipAssessments", new[] { TestDict("valence", "negative") }), failure);
                string displayed = ReadString(failed, "reply", "");
                var envelope = TestDict("ok", true, "content", Json.Serialize(failed), "visibleRepairFailure", true);
                Check(failure + "_retains_speech_and_failure", ReadBool(EnsureConversationDelivery(envelope), "ok", false)
                    && ConversationSpeechForRecall(displayed) == speech && IsTechnicalConversationNotice(displayed)
                    && ReadDictionaryList(failed, "memoryWrites").Count == 0
                    && ReadDictionaryList(failed, "relationshipAssessments").Count == 0);
                var canonical = TestDict("role", "npc", "text", displayed, "speaker_id", "npc",
                    "participants_json", "[\"npc\",\"player\"]", "status", "active");
                var projected = ProjectConversationTurnForRecall(canonical);
                Check(failure + "_immutable_source_and_speech_only_projection",
                    ReadString(canonical, "text", "") == displayed && ReadString(projected, "text", "") == speech
                    && ReadString(projected, "canonicalSourceTextHash", "") == PromptHash(displayed)
                    && !ReadBool(ReadDictionary(projected, "conversationRecovery"), "effectsConfirmed", true)
                    && PrecisionTurnVisible(canonical, "npc", null)
                    && !BuildCanonicalEmbeddingText("conversation_turn", canonical).Contains("could not be verified")
                    && RenderExactHistoryTurn(canonical, null).Contains(speech));
            }
            const string notice = "[Dialogue memory correction could not be verified. No new memory or world change is confirmed.].,";
            Check("normal_speech_preserves_bytes", ConversationSpeechForRecall(speech + "  ") == speech + "  ");
            Check("stacked_diagnostics_excluded", ConversationSpeechForRecall(speech + "\r\n\r\n" + notice + "\n\n" + notice) == speech);
            Check("synthetic_fallback_not_an_acknowledgment", ConversationSpeechForRecall(
                "I hear you. We can keep talking through the details.\n\n" + notice).Length == 0);
            Check("player_can_quote_error_without_losing_words",
                ConversationTurnTextForRecall(TestDict("role", "player", "text", notice)) == notice);
            foreach (string content in new[] { "", "{}", "{", Json.Serialize(TestDict("reply", notice)),
                Json.Serialize(TestDict("reply", "[Bannerlord Reign: malformed response hidden.]")) })
            {
                var failed = EnsureConversationDelivery(TestDict("ok", true, "content", content));
                Check("unusable_output_" + rows.Count, !ReadBool(failed, "ok", true)
                    && ReadString(failed, "content", "").Length == 0
                    && ReadBool(ReadDictionary(failed, "conversationRecovery"), "canContinue", false)
                    && !ReadBool(ReadDictionary(failed, "conversationRecovery"), "generationSucceeded", true));
            }
            var outage = EnsureConversationDelivery(TestDict("ok", false, "error", "provider timeout", "finishReason", "timeout"));
            Check("outage_keeps_failure_no_retry_or_fabricated_reply", !ReadBool(outage, "ok", true)
                && ReadString(outage, "error", "") == "provider timeout"
                && ReadInt(ReadDictionary(outage, "conversationRecovery"), "additionalProviderCalls", -1) == 0);
            var quiet = TestDict("ok", true, "content", Json.Serialize(TestDict("participation", "quiet", "reply", "")));
            Check("intentional_group_silence_remains_valid", ReadBool(EnsureConversationDelivery(quiet, true), "ok", false)
                && !ReadBool(EnsureConversationDelivery(quiet), "ok", true));
            Check("empty_object_is_not_group_silence",
                !ReadBool(EnsureConversationDelivery(TestDict("ok", true, "content", "{}"), true), "ok", true));
            var failedLetter = PrepareCorrespondenceDelivery(TestDict("ok", true, "content",
                Json.Serialize(TestDict("body", notice, "shouldReply", true))));
            Check("error_notice_is_never_sent_as_a_letter", !ReadBool(failedLetter, "shouldReply", true)
                && ReadString(failedLetter, "body", "").Length == 0 && ReadString(failedLetter, "error", "").Length > 0);
            var safeLetter = PrepareCorrespondenceDelivery(TestDict("ok", true, "visibleRepairFailure", true, "content",
                Json.Serialize(TestDict("body", speech + "\n\n" + notice, "shouldReply", true,
                    "repairStatus", TestDict("stageCount", 2, "allChecksCleared", false)))));
            Check("letter_speech_retains_failed_effect_status", ReadBool(safeLetter, "shouldReply", false)
                && ReadString(safeLetter, "body", "") == speech && ConversationRepairFailed(safeLetter)
                && ReadInt(ReadDictionary(safeLetter, "repairStatus"), "stageCount", 0) == 2);
            var noLetter = PrepareCorrespondenceDelivery(TestDict("ok", true, "content",
                Json.Serialize(TestDict("shouldReply", false, "body", ""))));
            Check("intentional_no_letter_does_not_retry", !ReadBool(noLetter, "shouldReply", true)
                && ReadString(noLetter, "error", "").Length == 0);
            string campaign = "unanswered_input_" + Guid.NewGuid().ToString("N"), oldGeneration = _playerActionGeneration;
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                    _playerActionGeneration = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                foreach (string route in new[] { "individual", "party", "event", "private_party" })
                {
                    bool group = route != "individual", secret = route == "private_party";
                    string session = "unanswered_" + route, words = secret ? "Use SECRET-EAST-GATE-901." : "Use the east gate.";
                    var input = TestDict("campaignId", campaign, "conversationSessionId", session, "sceneTurnId", route,
                        "turnId", route, "timelineId", "input_branch", "worldDay", 3d, "playerHeroStringId", "player",
                        "heroStringId", "npc", "participants", new[] { "npc", "player", "listener" },
                        "playerText", words, "publicPlayerText", secret ? "I speak privately." : words,
                        "privatePlayerInputForSpeaker", secret, "mode", group ? "party_chat" : "dialogue");
                    using (var connection = OpenCampaignConnection(campaign))
                        ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,'npc','player','open',1,1,'[""npc"",""player"",""listener""]','{""timelineId"":""input_branch""}');",
                            TestDict("session", session, "campaign", campaign));
                    var retained = RetainUnansweredPlayerContribution(campaign, input, "npc", "player", "Player", "Mira",
                        words, group, route == "event" ? "event-input" : "");
                    var replay = RetainUnansweredPlayerContribution(campaign, input, "npc", "player", "Player", "Mira",
                        words, group, route == "event" ? "event-input" : "");
                    using (var connection = OpenCampaignConnection(campaign))
                    {
                        string unit = AcceptedConversationEventId(campaign, "npc", input);
                        var originals = QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id;",
                            TestDict("id", unit));
                        Check(route + "_failed_generation_retains_input_once_without_npc", ReadString(retained, "status", "") == "player_input_retained"
                            && ReadStringList(retained, "sourceIds").SequenceEqual(ReadStringList(replay, "sourceIds"))
                            && originals.Count == (secret ? 2 : 1) && originals.All(t => ReadString(t, "role", "") == "player")
                            && QuerySql(connection, "SELECT * FROM memories;").Count == 0);
                        if (secret)
                            Check("failed_private_input_is_not_heard_by_bystander", originals
                                .Where(t => ReadString(t, "text", "").Contains("SECRET-EAST-GATE-901"))
                                .All(t => !PrecisionTurnVisible(t, "listener", null))
                                && originals.Any(t => ReadString(t, "text", "").Contains("SECRET-EAST-GATE-901")
                                    && PrecisionTurnVisible(t, "npc", null)));
                        else
                            Check(route + "_answer_available_after_failed_generation", ReadLogicalConversationTurns(connection,
                                ReadString(retained, "sessionId", ""), 30).Any(t => ReadString(t, "text", "") == words));
                    }
                }
            }
            finally
            {
                _playerActionGeneration = oldGeneration;
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
            return rows;
        }
    }
}
