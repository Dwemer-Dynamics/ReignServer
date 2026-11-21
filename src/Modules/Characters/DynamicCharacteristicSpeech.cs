using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class CharactersService
    {
        private const string ReportedSpeechSchema = "reign-character-reported-speech-v1";
        private const string ReportedSpeechProvenance = "npc_reported_speech";

        private static string ReportedSpeechRecordId(string heroId, string eventId) =>
            "reported_speech_" + DialogueService.PromptHash(eventId + "|" + heroId).Substring(0, 24).ToLowerInvariant();

        private static Dictionary<string, object> FindAcceptedCharacteristicSource(
            ReignDbConnection connection, Dictionary<string, object> payload, Dictionary<string, object> exchange,
            string heroId, string eventId, string reply)
        {
            if (!ReadBool(exchange, "ok", false) || string.IsNullOrWhiteSpace(reply)) return null;
            var receipt = ReadDictionary(exchange, "acceptedUnit");
            var unit = DialogueStore.SelectConversationAcceptedUnits2(connection, TestDict("unit", eventId)).SingleOrDefault();
            string responseHash = ReadString(unit, "response_hash", "");
            long timestamp = ReadLong(unit, "created_ts", -1);
            string generation = ReadString(unit, "restore_generation", "");
            var storedReceipt = TryParseJsonObject(ReadString(unit, "receipt_json", "{}"));
            if (responseHash.Length == 0 || responseHash != ReadString(receipt, "responseHash", "")
                || !ReadBool(storedReceipt, "ok", false) || ReadString(storedReceipt, "unitId", "") != eventId
                || ReadString(storedReceipt, "acceptedReplyHash", "") != DialogueService.PromptHash(reply)
                || ReadString(storedReceipt, "outputContract", "") == DialogueService.OllamaDelimitedSpeechContract
                || timestamp < 0 || generation.Length == 0 || generation != ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", ""))
                return null;

            // A private reply is committed in the whisper exchange. Its public
            // placeholder must never supply a turn ID, audience or observation.
            var privateExchange = ReadDictionary(storedReceipt, "privateExchange");
            bool whisper = ReadBool(privateExchange, "ok", false);
            var sourceExchange = whisper ? privateExchange : exchange;
            string session = ReadString(sourceExchange, "sessionId", "");
            string encounter = ReadString(exchange, "sessionId", "");
            // Each private beat has its own source session. Episode counting
            // still uses the enclosing conversation, as it did before binding.
            if (encounter.Length == 0 || (whisper && !session.StartsWith(encounter + "_whisper_", StringComparison.Ordinal)))
                return null;
            string exchangeId = ReadString(sourceExchange, "exchangeId", "");
            var acceptedIds = ReadStringList(storedReceipt, "sourceIds").ToHashSet(StringComparer.Ordinal);
            string timeline = DialogueService.ContinuityTimeline(payload);
            var candidates = ReadStringList(sourceExchange, "turnIds").Distinct(StringComparer.Ordinal)
                .Where(acceptedIds.Contains)
                .SelectMany(id => DialogueStore.SelectConversationTurns6(connection, TestDict("id", id)))
                .Where(row => ReadString(row, "role", "") == "npc" && ReadString(row, "status", "") == "active"
                    && ReadString(row, "speaker_id", "") == heroId && ReadString(row, "event_id", "") == eventId
                    && ReadString(row, "session_id", "") == session && ReadString(row, "exchange_id", "") == exchangeId
                    && ReadString(row, "text", "") == reply && ReadLong(row, "ts", -1) == timestamp
                    && (ReadString(row, "channel", "") == "private_whisper") == whisper
                    && DialogueService.ContinuityTimeline(TryParseJsonObject(ReadString(row, "payload_json", "{}"))) == timeline)
                .ToList();
            if (session.Length == 0 || exchangeId.Length == 0 || candidates.Count != 1) return null;
            var source = candidates[0];
            var audience = DialogueService.TextListFromJson(ReadString(source, "participants_json", "[]"));
            if (!audience.Contains(heroId, StringComparer.Ordinal)) return null;
            return TestDict("schema", ReportedSpeechSchema, "speakerId", heroId,
                "speakerName", ReadString(source, "speaker_name", heroId), "unitId", eventId,
                "turnId", ReadString(source, "turn_id", ""), "sessionId", session, "encounterId", encounter, "exchangeId", exchangeId,
                "timelineId", timeline, "restoreGeneration", generation, "responseHash", responseHash,
                "sourceHash", DialogueService.ConversationExactTextHash(reply), "channel", ReadString(source, "channel", ""),
                "audienceIds", audience, "timestamp", timestamp, "worldDay", ReadDouble(source, "world_day", 0),
                "acceptedSequence", ReadLong(source, "accepted_sequence", 0));
        }

        private static Dictionary<string, object> BindCharacteristicSpeech(Dictionary<string, object> accepted,
            Dictionary<string, object> citation, string reply, int speechStart, string speech)
        {
            if (accepted == null || speech.Length == 0) return null;
            string hero = ReadString(accepted, "speakerId", ""), turn = ReadString(citation, "turnId", "");
            if (ReadString(citation, "speakerId", "") != hero
                || (turn != "current_npc" && turn != ReadString(accepted, "turnId", ""))) return null;
            string quote = ReadString(citation, "quote", "");
            if (quote.Length < 8 || string.IsNullOrWhiteSpace(quote)) return null;
            int start = reply.IndexOf(quote, StringComparison.Ordinal);
            if (start < 0 || reply.IndexOf(quote, start + 1, StringComparison.Ordinal) >= 0) return null;
            int end = start + quote.Length;
            if ((start > 0 && char.IsLetterOrDigit(reply[start - 1]) && char.IsLetterOrDigit(reply[start]))
                || (end < reply.Length && char.IsLetterOrDigit(reply[end - 1]) && char.IsLetterOrDigit(reply[end]))) return null;
            // Preserve offsets in the accepted display reply while projecting
            // only authored speech. Keep all spoken segments for context.
            var projection = DialogueService.ProjectConversationNarration(speech, hero, "");
            if (ReadString(projection, "formatIntegrity", "") != "balanced_narration_markers") return null;
            var spans = ReadDictionaryList(projection, "spans")
                .Where(span => ReadString(span, "kind", "") == "spoken_statement").ToList();
            if (!spans.Any(span => start >= speechStart + ReadInt(span, "start", -1)
                && end <= speechStart + ReadInt(span, "start", -1) + ReadInt(span, "length", 0))) return null;
            var proof = new Dictionary<string, object>(accepted)
            {
                ["quote"] = quote, ["start"] = start, ["length"] = quote.Length,
                ["quoteCoversAuthoredBody"] = speech.Trim() == quote,
                ["spokenSegments"] = spans.Select(span => ReadString(span, "text", "")).ToList()
            };
            return proof;
        }

        private static string CharacteristicSpeechBody(string reply, string displayPrefix, string queuedActionNotice)
        {
            string body = DialogueService.ConversationTurnTextForRecall(TestDict("role", "npc", "text", reply));
            if (body.Length <= displayPrefix.Length || !body.StartsWith(displayPrefix, StringComparison.Ordinal)) return "";
            body = body.Substring(displayPrefix.Length);
            foreach (string notice in new[] { queuedActionNotice, ConversationResponseService.DialogueActionFailureNotice })
            {
                if (notice.Length == 0) continue;
                if (body == notice) return "";
                string suffix = "\n\n" + notice;
                if (body.EndsWith(suffix, StringComparison.Ordinal))
                    body = body.Substring(0, body.Length - suffix.Length);
            }
            return body;
        }

        private static string FormatReportedCharacteristicSpeech(Dictionary<string, object> proof, string owner)
        {
            if (owner.Length == 0 || ReadString(proof, "schema", "") != ReportedSpeechSchema
                || ReadString(proof, "speakerId", "") != owner || ReadString(proof, "sourceHash", "").Length == 0
                || ReadString(proof, "turnId", "").Length == 0
                || !ReadStringList(proof, "audienceIds").Contains(owner, StringComparer.Ordinal)) return "";
            var segments = ReadStringList(proof, "spokenSegments");
            if (segments.Count == 0) return "";
            return "Prior speech by " + Json.Serialize(ReadString(proof, "speakerName", owner))
                + " on campaign day " + ReadDouble(proof, "worldDay", 0).ToString("0.##", CultureInfo.InvariantCulture)
                + " (spoken segments in order): " + Json.Serialize(segments);
        }

        private static Dictionary<string, object> ReadCharacteristicSpeechProof(Dictionary<string, object> row)
        {
            var payload = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
            return ReadDictionary(ReadDictionary(payload, "narrativeDevelopment") ?? payload, "sourceEvidence");
        }

        internal static string CharacteristicSpeechSourceHash(Dictionary<string, object> row) =>
            ReadString(ReadCharacteristicSpeechProof(row), "sourceHash", "");

        internal static Dictionary<string, object> ScopeReportedCharacteristicSpeech(
            string campaign, Dictionary<string, object> characteristics, string owner, string peer, double worldDay,
            Dictionary<string, object> context, IEnumerable<string> otherParticipants, Func<string, string> identityProjection)
        {
            var document = ReadDictionary(characteristics, "dynamicCharacteristics");
            var active = ReadDictionaryList(document, "active");
            if (!active.Any(IsReportedDynamicCharacteristic)) return characteristics;
            var currentAudience = (otherParticipants ?? Enumerable.Empty<string>()).Append(owner).Append(peer)
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            var access = new DialogueService.KnowledgeAccessContext
            {
                NpcId = owner, TimelineId = DialogueService.ContinuityTimeline(context), WorldDay = worldDay,
                AsOfAcceptedSequence = ReadLong(context, "memoryAsOfAcceptedSequence", long.MaxValue),
                AsOfTimestamp = ReadLong(context, "memoryAsOfTimestamp", long.MaxValue)
            };
            bool official = ReadBool(context, "officialMemoryFirewall", false)
                || ReadString(context, "conversationMode", "") == "ambassador_official";
            var inherited = new Lazy<ReignDbConnection>(() => ReignDatabase.OpenCampaignConnection(campaign));
            try
            {
                var scoped = active.Where(row => !IsReportedDynamicCharacteristic(row)
                    || (!official && peer.Length > 0 && ReadString(row, "owner_id", "") == owner
                        && ReadString(document, "heroStringId", "") == owner
                        && CharacteristicSpeechVisible(row, access, currentAudience, inherited, identityProjection))).ToList();
                return new Dictionary<string, object>(characteristics)
                {
                    ["reportedSpeechPromptScope"] = true,
                    ["dynamicCharacteristics"] = new Dictionary<string, object>(document) { ["active"] = scoped }
                };
            }
            finally
            {
                if (inherited.IsValueCreated) inherited.Value.Dispose();
            }
        }

        private static bool CharacteristicSpeechVisible(Dictionary<string, object> row, DialogueService.KnowledgeAccessContext access,
            HashSet<string> currentAudience, Lazy<ReignDbConnection> inherited, Func<string, string> identityProjection)
        {
            var proof = ReadCharacteristicSpeechProof(row);
            if (ReadDouble(proof, "worldDay", double.PositiveInfinity) > access.WorldDay
                || ReadLong(proof, "acceptedSequence", long.MaxValue) > access.AsOfAcceptedSequence
                || ReadLong(proof, "timestamp", long.MaxValue) > access.AsOfTimestamp
                || !currentAudience.IsSubsetOf(ReadStringList(proof, "audienceIds"))) return false;
            string speech = FormatReportedCharacteristicSpeech(proof, access.NpcId);
            if (speech.Length == 0 || (identityProjection != null && identityProjection(speech) != speech)) return false;
            if (ReadString(proof, "timelineId", "") == access.TimelineId) return true;

            // A fork may use the original source only when the existing
            // verified restore membership still qualifies that exact turn.
            var connection = inherited.Value;
            var source = DialogueStore.SelectConversationTurns6(connection, TestDict("id", ReadString(proof, "turnId", ""))).SingleOrDefault();
            return ReadString(source, "role", "") == "npc" && ReadString(source, "speaker_id", "") == access.NpcId
                && ReadString(source, "event_id", "") == ReadString(proof, "unitId", "")
                && DialogueService.ConversationExactTextHash(ReadString(source, "text", "")) == ReadString(proof, "sourceHash", "")
                && DialogueService.PrecisionTurnVisible(source, access.NpcId, access, connection);
        }
    }
}
