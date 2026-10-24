using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int ConversationResponseRecoveryCharacterLimit = 1024 * 1024;
        private sealed class ConversationResponseEnvelope
        {
            internal Dictionary<string, object> Fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            internal bool Complete;
            internal bool Truncated;
            internal string IncompleteField = "";
            internal string Error = "";
            internal int ReplyStart = -1;
            internal int ReplyEnd = -1;
        }

        private static string UnwrapJsonResponse(string text) => UnwrapJsonResponse(text, out _);

        private static string UnwrapJsonResponse(string text, out int sourceOffset)
        {
            string original = text ?? "";
            sourceOffset = original.Length - original.TrimStart().Length;
            string source = original.Trim();
            if (!source.StartsWith("```", StringComparison.Ordinal)) return source;
            int line = source.IndexOf('\n');
            if (line < 0 || !source.EndsWith("```", StringComparison.Ordinal)) return source;
            string language = source.Substring(3, line - 3).Trim();
            if (language.Length != 0 && !language.Equals("json", StringComparison.OrdinalIgnoreCase)) return source;
            string body = source.Substring(line + 1, source.Length - line - 4);
            sourceOffset += line + 1 + body.Length - body.TrimStart().Length;
            return body.Trim();
        }

        // Generic storage/configuration parsing never searches nested values or
        // quoted JSON for a replacement document. Recovery belongs to the caller.
        private static Dictionary<string, object> TryParseStrictJsonObject(string text)
        {
            string source = UnwrapJsonResponse(text);
            if (!source.StartsWith("{", StringComparison.Ordinal)) return null;
            try
            {
                using var document = JsonDocument.Parse(source);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.EnumerateObject().Any(property => !names.Add(property.Name))) return null;
                return Json.Deserialize<Dictionary<string, object>>(source);
            }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is InvalidOperationException)
            {
                return null;
            }
        }

        // Read one outer object in source order. TryParseValue consumes a whole
        // value or nothing, so an unfinished array cannot contribute its earlier
        // entries, and nested/quoted objects never become replacement envelopes.
        private static ConversationResponseEnvelope ReadConversationResponseEnvelope(string text)
        {
            var result = new ConversationResponseEnvelope();
            if ((text?.Length ?? 0) > ConversationResponseRecoveryCharacterLimit)
            {
                result.Error = "recovery_size_limit";
                return result;
            }
            string source = UnwrapJsonResponse(text, out int sourceOffset);
            byte[] bytes = Encoding.UTF8.GetBytes(source);
            int sourceByteOffset = Encoding.UTF8.GetByteCount((text ?? "").Substring(0, sourceOffset));
            var reader = new Utf8JsonReader(bytes, false, new JsonReaderState(new JsonReaderOptions { MaxDepth = 64 }));
            try
            {
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    result.Error = "outer_object_required";
                    return result;
                }
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        if (source.Length > 0 && Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed,
                            bytes.Length - (int)reader.BytesConsumed).Trim().Length != 0)
                            result.Error = "trailing_or_multiple_documents";
                        else result.Complete = true;
                        return result;
                    }
                    if (reader.TokenType != JsonTokenType.PropertyName || result.Fields.Count >= 256)
                    {
                        result.Error = "invalid_outer_property";
                        return result;
                    }
                    string name = reader.GetString();
                    if (result.Fields.ContainsKey(name))
                    {
                        result.Error = "duplicate_outer_property";
                        return result;
                    }
                    result.IncompleteField = name;
                    if (!reader.Read()) { result.Truncated = true; return result; }
                    int start = (int)reader.TokenStartIndex;
                    if (!JsonDocument.TryParseValue(ref reader, out var value))
                    {
                        result.Truncated = true;
                        return result;
                    }
                    using (value)
                    {
                        var member = Json.Deserialize<Dictionary<string, object>>(
                            "{" + Json.Serialize(name) + ":" + value.RootElement.GetRawText() + "}");
                        result.Fields.Add(name, member[name]);
                        if (name.Equals("reply", StringComparison.OrdinalIgnoreCase)
                            && value.RootElement.ValueKind == JsonValueKind.String)
                        {
                            result.ReplyStart = sourceByteOffset + start;
                            result.ReplyEnd = sourceByteOffset + (int)reader.BytesConsumed;
                        }
                    }
                    result.IncompleteField = "";
                }
                // A partial property name is not evidence that no further effect
                // was chosen. Only whitespace after the last complete value may
                // be recovered as an omitted closing brace.
                if (Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed,
                    bytes.Length - (int)reader.BytesConsumed).Trim().Length != 0)
                    result.Error = "incomplete_outer_property";
                result.Truncated = true;
            }
            catch (JsonException)
            {
                result.Error = "invalid_json_structure";
            }
            return result;
        }

        private static bool ConversationResponseNeedsDrinkActions(Dictionary<string, object> request) =>
            ReadBool(request, "privateDrinkActionsRequired", false)
            || ReadDictionary(ReadDictionary(request, "promptEnvelope"), "drinkPromptBudget") != null;

        private static Dictionary<string, object> RecoverConversationResponseEnvelope(
            Dictionary<string, object> llm, Dictionary<string, object> request, string auditMode,
            string campaignId, string correlationId, string heroId, string eventId)
        {
            if (!ReadBool(llm, "ok", false) || !IsConversationStructuredMode(NormalizeLookup(auditMode).Replace(' ', '_')))
                return llm;
            string original = ReadString(llm, "content", "");
            if (TryParseStrictJsonObject(original) != null) return llm;
            var envelope = ReadConversationResponseEnvelope(original);
            bool hasReply = envelope.ReplyStart >= 0 && !string.IsNullOrWhiteSpace(ReadString(envelope.Fields, "reply", ""));
            bool incompleteEffect = envelope.IncompleteField.Length > 0;
            bool missingDrinks = ConversationResponseNeedsDrinkActions(request) && !envelope.Fields.ContainsKey("drinkActions");
            bool recovered = envelope.Truncated && envelope.Error.Length == 0 && hasReply && !incompleteEffect && !missingDrinks;
            var evidence = TestDict("schema", "reign-conversation-response-recovery-v1",
                "originalContentSha256", ConversationExactTextHash(original), "originalCharacters", original.Length,
                "sourceStage", "sanitized_assistant_content",
                "providerAssistantContentSha256", ReadString(llm, "providerAssistantContentSha256", ConversationExactTextHash(original)),
                "providerFinishReason", ReadString(llm, "finishReason", ""), "complete", envelope.Complete,
                "truncated", envelope.Truncated, "replyPath", hasReply ? "$.reply" : "",
                "replyStartUtf8", envelope.ReplyStart, "replyEndUtf8", envelope.ReplyEnd,
                "completedRootFields", envelope.Fields.Keys.ToList(), "incompleteField", envelope.IncompleteField,
                "error", envelope.Error, "effectsComplete", envelope.Error.Length == 0 && !incompleteEffect && !missingDrinks,
                "recovered", recovered, "requiresRegeneration", !recovered,
                "additionalProviderCalls", 0);
            llm["responseRecovery"] = evidence;
            if (recovered)
            {
                RemovePrivateReasoningFields(envelope.Fields);
                envelope.Fields["conversationResponseRecovery"] = evidence;
                llm["content"] = Json.Serialize(envelope.Fields);
            }
            else llm["responseRecoveryRequiresRegeneration"] = true;
            WriteAudit(campaignId, correlationId, "server", auditMode, "llm.response_recovery", heroId, "", eventId,
                recovered ? "completed" : "rejected", 0,
                recovered ? "Recovered complete outer response fields without promoting private nested data."
                    : "The response envelope is incomplete or ambiguous; no partial effects may be accepted.", evidence);
            return llm;
        }
    }
}
