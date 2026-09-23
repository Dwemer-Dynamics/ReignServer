using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void ApplyPlayerInputAudienceToPayload(Dictionary<string, object> payload,
            string listenerId)
        {
            List<Dictionary<string, object>> attendees = ReadDictionaryList(payload, "attendees");
            if (attendees.Count < 2) return;
            string input = ReadFirstString(payload, "playerText", "text", "message");
            string projected = ProjectPlayerInputForAudience(input, listenerId, attendees,
                out string publicText, out bool privateForListener);
            if (!string.Equals(input, publicText, StringComparison.Ordinal))
            {
                payload["playerText"] = projected;
                payload["publicPlayerText"] = publicText;
                payload["privatePlayerInputForSpeaker"] = privateForListener;
                payload["privateAudienceHeroStringId"] = privateForListener ? listenerId : string.Empty;
                if (privateForListener)
                    payload["participants"] = new List<string> {
                        listenerId, ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId") };
            }
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
            List<Dictionary<string, object>> attendees, out string publicText, out bool privateForListener)
        {
            string source = input ?? string.Empty;
            StringBuilder visible = new StringBuilder();
            StringBuilder shared = new StringBuilder();
            privateForListener = false;
            string pendingWhisperTarget = string.Empty;
            int cursor = 0;
            foreach (Match action in Regex.Matches(source, @"\*([^*]+)\*", RegexOptions.Singleline))
            {
                AppendPlayerSpeechSegment(source.Substring(cursor, action.Index - cursor),
                    pendingWhisperTarget, listenerId, visible, shared, ref privateForListener);
                pendingWhisperTarget = string.Empty;
                string actionText = action.Groups[1].Value;
                if (Regex.IsMatch(actionText, @"\bwhisper(?:s|ed|ing)?\b", RegexOptions.IgnoreCase))
                {
                    pendingWhisperTarget = ResolveWhisperRecipient(actionText, attendees);
                    if (string.IsNullOrWhiteSpace(pendingWhisperTarget))
                        pendingWhisperTarget = "__private_unknown__";
                    string recipientName = AttendeeName(attendees, pendingWhisperTarget);
                    string publicAction = "*The player whispers privately"
                        + (string.IsNullOrWhiteSpace(recipientName) ? ".*" : " to " + recipientName + ".*");
                    shared.Append(publicAction);
                    if (string.Equals(pendingWhisperTarget, listenerId, StringComparison.OrdinalIgnoreCase))
                    {
                        visible.Append(action.Value);
                        privateForListener = true;
                    }
                    else visible.Append(publicAction);
                }
                else
                {
                    visible.Append(action.Value);
                    shared.Append(action.Value);
                }
                cursor = action.Index + action.Length;
            }
            AppendPlayerSpeechSegment(source.Substring(cursor), pendingWhisperTarget,
                listenerId, visible, shared, ref privateForListener);
            publicText = shared.ToString().Trim();
            return visible.ToString().Trim();
        }

        private static void AppendPlayerSpeechSegment(string speech, string recipientId,
            string listenerId, StringBuilder visible, StringBuilder shared, ref bool privateForListener)
        {
            if (string.IsNullOrEmpty(speech)) return;
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
