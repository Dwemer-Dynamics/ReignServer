using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // The displayed diagnostic is not something the character said. Keeping
        // it separate from safe speech must never change failed-effect gates.
        private static string ConversationSpeechForRecall(string text)
        {
            string original = text ?? "";
            string speech = original;
            const string notice = @"(?:\A\s*|(?:\r?\n)+[ \t]*)\[(?:The NPC's decision could not be verified|Dialogue continuity evidence could not be verified|Dialogue memory correction could not be verified|Dialogue validation could not be completed)[^\]]*\][.,\s]*\z";
            for (int i = 0; i < 8; i++)
            {
                var suffix = Regex.Match(speech, notice, RegexOptions.CultureInvariant);
                if (!suffix.Success) break;
                speech = speech.Substring(0, suffix.Index).TrimEnd();
            }
            // A malformed or embedded technical diagnostic is not safe source
            // text. Historical rows are projected, never rewritten here.
            if (IsTechnicalConversationNotice(speech)) return "";
            if (speech != original && speech.Trim() == "I hear you. We can keep talking through the details.") return "";
            return speech;
        }

        private static bool HasRecallableConversationSpeech(string text) =>
            !string.IsNullOrWhiteSpace(ConversationSpeechForRecall(text));

        private static string ConversationTurnTextForRecall(Dictionary<string, object> turn)
        {
            string text = ReadString(turn, "text", "");
            return ReadString(turn, "role", "").Equals("player", StringComparison.OrdinalIgnoreCase)
                ? text : ConversationSpeechForRecall(text);
        }

        private static Dictionary<string, object> ProjectConversationTurnForRecall(Dictionary<string, object> turn)
        {
            var copy = CloneDictionary(turn);
            string original = ReadString(turn, "text", ""), spoken = ConversationTurnTextForRecall(turn);
            copy["text"] = spoken;
            if (original != spoken)
            {
                copy["canonicalSourceTextHash"] = PromptHash(original);
                copy["conversationRecovery"] = TestDict("status", "speech_only", "effectsConfirmed", false,
                    "diagnosticExcluded", true);
            }
            return copy;
        }

        // This is local delivery recovery after the bounded existing repair path.
        // Transport failure stays a failure; a server placeholder is not a model
        // answer, an acknowledgment, a memory, or authorization for an effect.
        private static Dictionary<string, object> EnsureConversationDelivery(Dictionary<string, object> result,
            bool allowQuiet = false)
        {
            if (!ReadBool(result, "ok", false))
            {
                var unavailable = CloneDictionary(result);
                if (ReadDictionary(unavailable, "conversationRecovery") == null)
                    unavailable["conversationRecovery"] = TestDict("status", "generation_unavailable",
                        "generationSucceeded", false, "canContinue", true, "additionalProviderCalls", 0);
                return unavailable;
            }
            var parsed = TryParseJsonObject(ReadString(result, "content", ""));
            if (allowQuiet && parsed != null && NormalizeSocialEventParticipation(parsed) == "quiet") return result;
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            string sanitized = SanitizeVisibleReply(reply);
            bool hidden = sanitized.StartsWith("[Bannerlord Reign:", StringComparison.Ordinal);
            if (HasRecallableConversationSpeech(reply) && !hidden && !string.IsNullOrWhiteSpace(sanitized)) return result;
            var recovered = CloneDictionary(result);
            recovered["ok"] = false;
            recovered["content"] = "";
            recovered["error"] = "The character's response could not be completed. You can continue the conversation or try again.";
            recovered["visibleRepairFailure"] = true;
            recovered["technicalFailureClass"] = "dialogue_validation";
            recovered["conversationRecovery"] = TestDict("status", "no_usable_model_reply",
                "generationSucceeded", false, "canContinue", true, "additionalProviderCalls", 0);
            return recovered;
        }

        private static Dictionary<string, object> RetainUnansweredPlayerContribution(string campaign,
            Dictionary<string, object> payload, string hero, string player, string playerName, string heroName,
            string playerText, bool grouped = false, string socialEvent = "")
        {
            if (string.IsNullOrWhiteSpace(playerText) || string.IsNullOrWhiteSpace(player))
                return TestDict("status", "no_player_contribution", "npcResponseStored", false);
            try
            {
                if (string.IsNullOrWhiteSpace(_playerActionGeneration))
                    throw new InvalidOperationException("The generation boundary for this player input is unavailable.");
                string unitId = AcceptedConversationEventId(campaign, hero, payload);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                bool whisper = grouped && ReadBool(payload, "privatePlayerInputForSpeaker", false);
                var unit = new AcceptedConversationUnit { Campaign = campaign, Hero = hero, Player = player,
                    PlayerName = playerName, HeroName = heroName, EventId = unitId, Payload = payload,
                    Location = ReadFirstString(payload, "locationId", "currentSettlementId", "settlementId"),
                    PlayerText = playerText, Reply = "", Timestamp = now, Generation = _playerActionGeneration };
                ConversationSourceAcceptance privateSource = whisper
                    ? (connection, source) => StoreAcceptedWhisperSources(connection, unit, source) : null;
                string publicText = grouped ? ReadString(payload, "publicPlayerText", playerText) : playerText;
                var stored = !grouped
                    ? StoreConversationExchange(campaign, payload, hero, player, playerName, heroName, publicText, "", unitId, now)
                    : socialEvent.Length == 0 || socialEvent == "party_chat"
                        ? StorePartyConversationExchange(campaign, payload, hero, player, playerName, heroName, publicText, "", unitId, now, privateSource)
                        : StoreSocialEventConversationExchange(campaign, payload, hero, player, playerName, heroName, publicText, "", unitId, socialEvent, now, privateSource);
                var ids = new List<string>();
                using (var connection = OpenCampaignConnection(campaign))
                    foreach (string id in ReadStringList(stored, "turnIds")
                        .Concat(ReadStringList(ReadDictionary(stored, "acceptedUnit"), "turnIds")))
                        if (QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id AND role='player';",
                            TestDict("id", id)).Count == 1) ids.Add(id);
                return TestDict("status", ids.Count > 0 ? "player_input_retained" : "player_input_unavailable",
                    "sessionId", ReadString(stored, "sessionId", ""), "sourceIds", ids,
                    "npcResponseStored", false, "effectsConfirmed", false);
            }
            catch (Exception ex)
            {
                LogOperational("conversation.unanswered_input_incomplete", TestDict("campaignId", campaign,
                    "heroStringId", hero, "error", LimitText(ex.Message, 500)));
                return TestDict("status", "player_input_unavailable", "coverageGap", "Player input could not be persisted.",
                    "npcResponseStored", false, "effectsConfirmed", false);
            }
        }

        private static Dictionary<string, object> PrepareCorrespondenceDelivery(Dictionary<string, object> result)
        {
            var parsed = ReadBool(result, "ok", false) ? TryParseJsonObject(ReadString(result, "content", "")) : null;
            if (parsed == null) return TestDict("shouldReply", false, "body", "", "error",
                FirstNonEmpty(ReadString(result, "error", ""), "No valid letter draft was returned."));
            parsed = CloneDictionary(parsed);
            string body = ReadFirstString(parsed, "body", "response", "letter");
            if (!ReadBool(parsed, "shouldReply", true) && string.IsNullOrWhiteSpace(body)) return parsed;
            string speech = ConversationSpeechForRecall(body);
            if (string.IsNullOrWhiteSpace(speech)
                || SanitizeVisibleReply(speech).StartsWith("[Bannerlord Reign:", StringComparison.Ordinal))
                return TestDict("shouldReply", false, "body", "", "error", "No usable letter draft was returned.",
                    "conversationRecovery", TestDict("status", "letter_not_generated", "canContinue", true));
            if (body != speech || ReadBool(result, "visibleRepairFailure", false))
            {
                var status = CloneDictionary(ReadDictionary(parsed, "repairStatus") ?? TestDict());
                status["allChecksCleared"] = false;
                status["stageCount"] = Math.Max(1, ReadInt(status, "stageCount", 0));
                parsed["repairStatus"] = status;
                parsed["conversationRecovery"] = TestDict("status", "speech_only", "effectsConfirmed", false);
            }
            parsed["body"] = speech;
            return parsed;
        }
    }
}
