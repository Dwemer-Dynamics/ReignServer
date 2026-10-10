using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class AcceptedPlayerInputSource
        {
            internal string Original;
            internal string SourceId = "", InputId = "", Generation = "";
            internal Dictionary<string, object> Receipt, Interpretation;
        }

        // Original input is server-captured, never accepted from a request field.
        // It is kept out of prompt dictionaries and copied explicitly at acceptance.
        private static readonly ConditionalWeakTable<Dictionary<string, object>, AcceptedPlayerInputSource> PlayerInputSources = new();

        private static string ProjectCurrentPlayerPromptText(string input, string listenerId, Dictionary<string, object> payload)
        {
            var attendees = ReadDictionaryList(payload, "attendees");
            if (attendees.Count == 0) attendees = MergedInteractionParticipantProfiles(payload ?? TestDict());
            // Interpretation annotates scene changes; it never decides which
            // parts of the player's story the writer is allowed to receive.
            string original = payload != null && PlayerInputSources.TryGetValue(payload, out var captured)
                ? captured.Original : input;
            return ProjectPlayerInputForAudience(original, listenerId, attendees, out _, out _);
        }

        private static void CopyPlayerInputSource(Dictionary<string, object> source, Dictionary<string, object> target)
        {
            if (source != null && target != null && PlayerInputSources.TryGetValue(source, out var captured))
            {
                PlayerInputSources.Remove(target);
                PlayerInputSources.Add(target, captured);
                foreach (string field in new[] { "conversationDateLabel", "settlementName", "currentSettlementName" })
                    if (!target.ContainsKey(field) && source.ContainsKey(field)) target[field] = source[field];
            }
        }

        private static void PreservePlayerInputSource(ReignDbConnection connection, Dictionary<string, object> request,
            string sessionId, string eventId, string exchangeId, string playerId, string playerName,
            double worldDay, long ts, string locationId)
        {
            if (request == null || !PlayerInputSources.TryGetValue(request, out var captured)
                || string.IsNullOrWhiteSpace(captured.Original)) return;
            string sourceId = FirstNonEmpty(captured.SourceId, "input_source_" + PromptHash(sessionId + "|" + exchangeId + "|" + playerId + "|" + captured.Original).Substring(0, 32));
            captured.SourceId = sourceId;
            // This immutable audit source is deliberately not an active NPC turn,
            // not indexed for recall, and known only to its author. Save Sync already
            // preserves conversation_turns, including non-active original records.
            // The table also has UNIQUE(session_id,turn_order). A constant -1
            // silently discarded every later original in the same session. Allocate
            // distinct negative slots under the publisher's transaction/session lock;
            // active observer turns retain their independent nonnegative sequence.
            LockConversationSourcePublication(connection, sessionId);
            var existingSource = QuerySql(connection, "SELECT text,status,speaker_id FROM conversation_turns WHERE turn_id=$id;", TestDict("id", sourceId)).SingleOrDefault();
            if (existingSource != null)
            {
                if (ReadString(existingSource, "text", "") != captured.Original || ReadString(existingSource, "status", "") != "source_only"
                    || ReadString(existingSource, "speaker_id", "") != playerId)
                    throw new InvalidOperationException("The saved original does not match this contribution; no input was replaced.");
                return;
            }
            int sourceOrder = ReadInt(QuerySql(connection,
                "SELECT COALESCE(MIN(turn_order),0)-1 AS next_order FROM conversation_turns WHERE session_id=$session AND turn_order<0;",
                TestDict("session", sessionId)).Single(), "next_order", -1);
            ExecuteSql(connection, @"INSERT INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,location_id,participants_json,status,payload_json)
VALUES($id,$session,$event,$order,$exchange,'player_source',$player,$name,$text,'player_input_source',$day,$ts,$location,$participants,'source_only',$payload);",
                TestDict("id", sourceId, "session", sessionId, "event", eventId, "exchange", exchangeId,
                    "order", sourceOrder,
                    "player", playerId, "name", playerName, "text", captured.Original, "day", worldDay, "ts", ts,
                    "location", locationId, "participants", Json.Serialize(new[] { playerId }), "payload", Json.Serialize(TestDict(
                        "timelineId", ContinuityTimeline(request), "worldDay", worldDay,
                        "sourceHash", PromptHash(captured.Original), "evidenceType", "original_player_input",
                        "exactTextHash", ConversationExactTextHash(captured.Original),
                        "retrievalEligible", false, "observerProjectionVersion", 2))));
        }

        private static void ApplyPlayerInputAudienceToPayload(Dictionary<string, object> payload,
            string listenerId, bool allowInterpretation = true)
        {
            if (payload == null) return;
            List<Dictionary<string, object>> attendees = ReadDictionaryList(payload, "attendees");
            bool group = attendees.Count >= 2;
            if (attendees.Count == 0) attendees = MergedInteractionParticipantProfiles(payload);
            var captured = CapturePlayerInput(payload);
            string input = captured.Original;
            if (captured.InputId.Length > 0 && allowInterpretation)
                InterpretConversationInput(ReadString(payload, "campaignId", ""), payload);
            bool interpreted = ReadString(captured.Interpretation, "status", "") == "validated";
            payload["playerNarrationProjectionVersion"] = interpreted ? 2 : 1;
            string publicText; bool privateForListener;
            string projected = interpreted
                ? ProjectInterpretedInput(payload, listenerId, attendees, out publicText, out privateForListener)
                : ProjectPlayerInputForAudience(input, listenerId, attendees, out publicText, out privateForListener);
            payload["playerText"] = projected;
            payload["narrativeInputIntegrity"] = TestDict("schema", "reign-narrative-input-v1",
                "originalHash", ConversationExactTextHash(input), "writerHash", ConversationExactTextHash(projected),
                "narrationPreserved", true, "audienceScopedSpeech", input != projected,
                "thoughtFiltering", false, "reactionEvidenceRequired", false);
            // Aliases must not leave the unprojected body in a provider-bound payload.
            foreach (string alias in new[] { "text", "message" })
                if (payload.ContainsKey(alias)) payload[alias] = projected;
            if (!string.Equals(input, publicText, StringComparison.Ordinal))
            {
                payload["publicPlayerText"] = publicText;
                payload["privatePlayerInputForSpeaker"] = privateForListener;
                payload["privateAudienceHeroStringId"] = privateForListener ? listenerId : string.Empty;
                if (privateForListener)
                    payload["participants"] = new List<string> {
                        listenerId, ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId") };
            }
            if (!group) return;
            List<Dictionary<string, object>> transcript = ReadDictionaryList(payload, "groupTranscript");
            if (transcript.Count == 0)
            {
                // Social-event history is loaded from the server's public transcript.
                // Client legacy lines have no audience metadata and can contain a
                // private NPC reply from an earlier sequential speaker.
                payload["transcript"] = new List<string>();
                return;
            }
            List<Dictionary<string, object>> projectedTranscript = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> row in transcript)
            {
                Dictionary<string, object> copy = new Dictionary<string, object>(row,
                    StringComparer.OrdinalIgnoreCase);
                string audience = ReadString(copy, "audienceHeroStringId", "");
                if (!string.IsNullOrWhiteSpace(audience)
                    && !string.Equals(audience, listenerId, StringComparison.OrdinalIgnoreCase))
                {
                    copy["text"] = "*A private reply was exchanged.*";
                }
                else if (ReadString(copy, "role", "") == "player")
                {
                    copy["text"] = ProjectPlayerInputForAudience(ReadString(copy, "text", ""),
                        listenerId, attendees, out _, out _);
                }
                projectedTranscript.Add(copy);
            }
            payload["groupTranscript"] = projectedTranscript;
            payload["transcript"] = projectedTranscript.Select(row =>
                ReadString(row, "speaker", "Unknown") + ": " + ReadString(row, "text", "")).ToList();
        }

        private static string ProjectPlayerInputForAudience(string input, string listenerId,
            List<Dictionary<string, object>> attendees, out string publicText, out bool privateForListener,
            bool validatedNarration = false)
        {
            string source = input ?? string.Empty;
            StringBuilder visible = new StringBuilder();
            StringBuilder shared = new StringBuilder();
            privateForListener = false;
            string pendingWhisperTarget = string.Empty;
            int cursor = 0;
            foreach (var action in InputNarrationBlocks(source))
            {
                int start = ReadInt(action, "outerStart", 0), length = ReadInt(action, "outerLength", 0);
                string narration = source.Substring(start, length);
                AppendPlayerSpeechSegment(source.Substring(cursor, start - cursor),
                    pendingWhisperTarget, listenerId, visible, shared, ref privateForListener);
                pendingWhisperTarget = string.Empty;
                string actionText = ReadString(action, "text", "");
                if (Regex.IsMatch(actionText, @"\bwhisper(?:s|ed|ing)?\b", RegexOptions.IgnoreCase))
                {
                    pendingWhisperTarget = ResolveWhisperRecipient(actionText, attendees);
                    if (string.IsNullOrWhiteSpace(pendingWhisperTarget))
                    {
                        var recipients = (attendees ?? new()).Where(a => ReadString(a, "role", "") != "player"
                            && CharacterIdFrom(a) != "main_hero").Select(CharacterIdFrom).Where(id => id.Length > 0).Distinct().ToList();
                        pendingWhisperTarget = recipients.Count == 1 ? recipients[0] : "__private_unknown__";
                        if (attendees == null || attendees.Count == 0) pendingWhisperTarget = listenerId.Length > 0 ? listenerId : "__private_unknown__";
                    }
                    // Keep the surrounding action even when its following
                    // speech is private. Only explicit quoted whisper words
                    // inside the block need a separate audience view.
                    string publicAction = Regex.Replace(narration, "[\\\"“][^\\\"”]*[\\\"”]", "[private words]");
                    string recipientName = AttendeeName(attendees, pendingWhisperTarget);
                    string recipient = recipientName.Length > 0 ? Regex.Escape(recipientName) + "|" : "";
                    // Reported speech can be inside the action without quotes:
                    // "I take her hand and whisper to Mira that we leave at dawn."
                    // Preserve the gesture and whisper attribution, withholding
                    // only that explicit speech clause from other listeners.
                    publicAction = Regex.Replace(publicAction,
                        @"\bwhisper(?:s|ed|ing)?(?:\s+(?:softly|quietly|privately))*\s+(?:to\s+(?:" + recipient + @"her|him|them)\s+)?that\s+(?<words>[^.!?*]+)",
                        match => match.Value.Substring(0, match.Groups["words"].Index - match.Index) + "[private words]",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    shared.Append(publicAction);
                    if (string.Equals(pendingWhisperTarget, listenerId, StringComparison.OrdinalIgnoreCase))
                    {
                        visible.Append(narration);
                        privateForListener = true;
                    }
                    else visible.Append(publicAction);
                }
                else
                {
                    visible.Append(narration);
                    shared.Append(narration);
                }
                cursor = start + length;
            }
            AppendPlayerSpeechSegment(source.Substring(cursor), pendingWhisperTarget,
                listenerId, visible, shared, ref privateForListener);
            publicText = shared.ToString();
            return visible.ToString();
        }

        private static void AppendPlayerSpeechSegment(string speech, string recipientId,
            string listenerId, StringBuilder visible, StringBuilder shared, ref bool privateForListener)
        {
            if (string.IsNullOrEmpty(speech)) return;
            if (string.IsNullOrWhiteSpace(speech))
            {
                visible.Append(speech);
                shared.Append(speech);
                return;
            }
            if (string.IsNullOrWhiteSpace(recipientId))
            {
                visible.Append(speech);
                shared.Append(speech);
                return;
            }
            if (string.Equals(recipientId, listenerId, StringComparison.OrdinalIgnoreCase))
            {
                visible.Append(speech);
                privateForListener = true;
            }
            else visible.Append(Regex.Match(speech, @"^\s*").Value).Append(Regex.Match(speech, @"\s*$").Value);
            shared.Append(Regex.Match(speech, @"^\s*").Value).Append(Regex.Match(speech, @"\s*$").Value);
        }

        private static string ResolveWhisperRecipient(string action,
            List<Dictionary<string, object>> attendees)
        {
            List<string> matches = new List<string>();
            foreach (Dictionary<string, object> attendee in attendees ?? new List<Dictionary<string, object>>())
            {
                string id = CharacterIdFrom(attendee);
                string name = ReadString(attendee, "name", "");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
                if (Regex.IsMatch(action, @"\b" + Regex.Escape(name) + @"\b", RegexOptions.IgnoreCase)
                    && !matches.Contains(id, StringComparer.OrdinalIgnoreCase)) matches.Add(id);
            }
            return matches.Count == 1 ? matches[0] : string.Empty;
        }

        private static string AttendeeName(List<Dictionary<string, object>> attendees, string id)
        {
            return attendees?.Where(row => string.Equals(CharacterIdFrom(row), id,
                    StringComparison.OrdinalIgnoreCase))
                .Select(row => ReadString(row, "name", ""))
                .FirstOrDefault() ?? string.Empty;
        }
    }
}
