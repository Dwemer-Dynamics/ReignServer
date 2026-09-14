using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string PromptContentHash(string content)
        {
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(content ?? ""))).Replace("-", "");
        }

        private static string RedactPromptEvidence(string text, string apiKey = "")
        {
            text = text ?? "";
            if (!string.IsNullOrEmpty(apiKey)) text = text.Replace(apiKey, "[REDACTED]");
            text = Regex.Replace(text, @"(?i)\bBearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
            return Regex.Replace(text, @"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|token|password|client[_-]?secret|secret)[""']?\s*[:=]\s*[""']?[^\s,;""'}]+", "$1=[REDACTED]", RegexOptions.None, TimeSpan.FromSeconds(1));
        }

        private static Dictionary<string, object> BuildPromptEvidenceRecord(string campaignId, string correlationId,
            string requestType, string model, Dictionary<string, object> request, Dictionary<string, object> diagnostics, string apiKey, string captureStage = "before_provider_attempt")
        {
            var messages = new List<Dictionary<string, object>>();
            foreach (var message in ReadDictionaryList(request, "messages"))
            {
                // Only outbound role/content, never returned provider reasoning or credentials.
                string original = ReadString(message, "content", "");
                string content = RedactPromptEvidence(original, apiKey);
                messages.Add(new Dictionary<string, object> {
                    ["index"] = messages.Count, ["role"] = ReadString(message, "role", ""),
                    ["content"] = content, ["characters"] = content.Length,
                    ["originalCharacters"] = original.Length, ["redacted"] = content != original,
                    ["contentSha256"] = PromptContentHash(content)
                });
            }
            // Sanitize bounded diagnostics as well, including strings with labeled secrets.
            string metadata = Json.Serialize(RedactPromptEvidenceMetadata(SanitizeAuditValue(diagnostics ?? new Dictionary<string, object>(), 0), apiKey));
            return new Dictionary<string, object> {
                ["schema"] = "reign-prompt-evidence-v1", ["evidenceId"] = Guid.NewGuid().ToString("N"),
                ["campaignId"] = campaignId, ["correlationId"] = correlationId,
                ["timestamp"] = DateTimeOffset.UtcNow.ToString("o"), ["requestType"] = requestType,
                ["model"] = model, ["complete"] = true, ["messages"] = messages,
                ["diagnosticsJson"] = metadata, ["captureStage"] = captureStage,
                ["hashScope"] = "exact redacted UTF-8 message content"
            };
        }

        private static object RedactPromptEvidenceMetadata(object value, string apiKey)
        {
            if (value is string text) return RedactPromptEvidence(text, apiKey);
            if (value is IDictionary dictionary)
            {
                var clean = new Dictionary<string, object>();
                foreach (DictionaryEntry entry in dictionary)
                    clean[Convert.ToString(entry.Key)] = RedactPromptEvidenceMetadata(entry.Value, apiKey);
                return clean;
            }
            if (value is IEnumerable list)
            {
                var clean = new List<object>();
                foreach (object item in list) clean.Add(RedactPromptEvidenceMetadata(item, apiKey));
                return clean;
            }
            return value;
        }

        private static void CapturePromptEvidence(string campaignId, string correlationId, string requestType,
            string model, Dictionary<string, object> request, Dictionary<string, object> diagnostics, string apiKey, string captureStage = "before_provider_attempt")
        {
            if (diagnostics == null) return; // Only assembled envelopes, not arbitrary audit ingestion.
            try
            {
                var row = BuildPromptEvidenceRecord(campaignId, correlationId, requestType, model, request, diagnostics, apiKey, captureStage);
                AppendBoundedJsonLineToPath(CampaignFile(campaignId, "audit", "prompt-evidence.jsonl"), row,
                    8L * 1024L * 1024L, 4L * 1024L * 1024L);
            }
            catch
            {
                // Evidence failure must not disrupt an otherwise valid conversation.
                LogOperational("prompts.evidence_unavailable", new Dictionary<string, object> { ["correlationId"] = correlationId });
            }
        }

        private static Dictionary<string, object> PromptEvidencePage(Dictionary<string, object> row, int messageIndex, int offset, int length)
        {
            var messages = ReadDictionaryList(row, "messages");
            if (messageIndex < 0 || messageIndex >= messages.Count || offset < 0 || length < 1 || length > 12000)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Invalid prompt page bounds." };
            var message = messages[messageIndex];
            string content = ReadString(message, "content", "");
            if (offset > content.Length) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Offset exceeds message length." };
            if (offset > 0 && offset < content.Length && char.IsLowSurrogate(content[offset]) && char.IsHighSurrogate(content[offset - 1]))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Offset splits a Unicode character; use the previous page's nextOffset." };
            int count = Math.Min(length, content.Length - offset);
            if (count > 0 && offset + count < content.Length && char.IsHighSurrogate(content[offset + count - 1]) && char.IsLowSurrogate(content[offset + count]))
            {
                if (count == 1) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "This Unicode character needs a page length of at least 2." };
                count--;
            }
            return new Dictionary<string, object> {
                ["ok"] = true, ["schema"] = "reign-prompt-evidence-v1", ["complete"] = ReadBool(row, "complete", false),
                ["evidenceId"] = ReadString(row, "evidenceId", ""), ["correlationId"] = ReadString(row, "correlationId", ""),
                ["captureStage"] = ReadString(row, "captureStage", ""),
                ["messageIndex"] = messageIndex, ["offset"] = offset, ["length"] = count,
                ["content"] = content.Substring(offset, count), ["nextOffset"] = offset + count,
                ["hasMore"] = offset + count < content.Length, ["messageCount"] = messages.Count,
                ["messages"] = messages.Select(item => item.Where(pair => pair.Key != "content").ToDictionary(pair => pair.Key, pair => pair.Value)).ToList(),
                ["diagnosticsJson"] = ReadString(row, "diagnosticsJson", "{}"),
                ["hashScope"] = ReadString(row, "hashScope", "")
            };
        }

        private static Dictionary<string, object> ReadPromptEvidenceApi(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = ResolveLogCampaignId(query.TryGetValue("campaignId", out string campaign) ? campaign : "");
            string correlationId = query.TryGetValue("correlationId", out string correlation) ? correlation : "";
            if (string.IsNullOrWhiteSpace(correlationId) || correlationId.Length > 200)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "An exact correlationId is required." };
            string evidenceId = query.TryGetValue("evidenceId", out string evidence) ? evidence : "";
            var matches = ReadJsonLinesFromPath(CampaignFile(campaignId, "audit", "prompt-evidence.jsonl"))
                .Where(row => ReadString(row, "correlationId", "") == correlationId).ToList();
            var selected = string.IsNullOrEmpty(evidenceId) ? matches.LastOrDefault()
                : matches.LastOrDefault(row => ReadString(row, "evidenceId", "") == evidenceId);
            if (selected == null) return new Dictionary<string, object> { ["ok"] = false, ["complete"] = false,
                ["error"] = "Complete prompt evidence is unavailable for this correlation/attempt (predates capture, expired retention, or capture failure). The truncated audit is not a full prompt." };
            int messageIndex = query.TryGetValue("messageIndex", out string mi) && int.TryParse(mi, out int m) ? m : 0;
            int offset = query.TryGetValue("offset", out string off) && int.TryParse(off, out int o) ? o : 0;
            int length = query.TryGetValue("length", out string len) && int.TryParse(len, out int l) ? l : 4000;
            var result = PromptEvidencePage(selected, messageIndex, offset, length);
            result["retainedAttemptCount"] = matches.Count;
            result["attemptListTruncated"] = matches.Count > 64;
            result["attempts"] = matches.Skip(Math.Max(0, matches.Count - 64)).Select(row => new Dictionary<string, object> {
                ["evidenceId"] = ReadString(row, "evidenceId", ""), ["timestamp"] = ReadString(row, "timestamp", ""),
                ["requestType"] = ReadString(row, "requestType", ""), ["model"] = ReadString(row, "model", "") }).ToList();
            return result;
        }
    }
}
