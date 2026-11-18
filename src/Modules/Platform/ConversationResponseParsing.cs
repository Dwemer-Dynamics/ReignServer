using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int ConversationResponseRecoveryCharacterLimit = 1024 * 1024;
        private const string DialogueActionQueuedNoticePrefix = "[Bannerlord Reign action queued:";
        internal const string DialogueActionFailureNotice = "[Reign could not carry out the agreed action. No game action was queued.]";
        private static readonly Regex EmbeddedProviderDialogueMetadata = new Regex(
            @"\{\s*""(?:decision_?brief|structuredActionDecision|stateUpdates|suggestedActions|drinkActions|(?:memory|belief|obligation|comprehension|continuity|dynamicCharacteristic)Writes)""\s*:\s*[\[{]",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static bool ContainsProviderDialogueOutcomeNotice(string reply)
        {
            reply ??= "";
            return reply.Contains(DialogueActionQueuedNoticePrefix, StringComparison.OrdinalIgnoreCase)
                || reply.Contains(DialogueActionFailureNotice, StringComparison.OrdinalIgnoreCase)
                || DialogueService.TechnicalConversationNoticePrefixes.Any(notice => reply.Contains(notice, StringComparison.OrdinalIgnoreCase));
        }

        // Engine notices and structured metadata belong outside authored speech.
        // Check the provider text before any effects are accepted.
        internal static Dictionary<string, object> RejectProviderReservedSpeech(Dictionary<string, object> response,
            Dictionary<string, object> parsed)
        {
            string reply = ReadFirstString(parsed, "reply", "response", "text", "content");
            bool outcomeNotice = ContainsProviderDialogueOutcomeNotice(reply);
            if (!outcomeNotice && !EmbeddedProviderDialogueMetadata.IsMatch(reply))
                return response;
            var rejected = CloneDictionary(response);
            rejected["ok"] = false;
            rejected["content"] = "";
            rejected["errorCode"] = outcomeNotice ? "provider_dialogue_outcome_notice" : "provider_dialogue_embedded_metadata";
            rejected["error"] = outcomeNotice
                ? "The character's response included a reserved Reign notice. No NPC reply or effect was accepted from it."
                : "The character's response included internal structured metadata. No NPC reply or effect was accepted from it.";
            return rejected;
        }

        internal sealed class ConversationResponseEnvelope
        {
            internal Dictionary<string, object> Fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            internal bool Complete;
            internal bool Truncated;
            internal string IncompleteField = "";
            internal string Error = "";
            internal int ReplyStart = -1;
            internal int ReplyEnd = -1;
        }

        // Read one outer object in source order. TryParseValue consumes a whole
        // value or nothing, so an unfinished array cannot contribute its earlier
        // entries, and nested/quoted objects never become replacement envelopes.
        internal static ConversationResponseEnvelope ReadConversationResponseEnvelope(string text)
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
            var parsed = TryParseStrictJsonObject(original);
            if (parsed != null) return RejectProviderReservedSpeech(llm, parsed);
            var envelope = ReadConversationResponseEnvelope(original);
            var checkedResponse = RejectProviderReservedSpeech(llm, envelope.Fields);
            if (!ReadBool(checkedResponse, "ok", false)) return checkedResponse;
            bool hasReply = envelope.ReplyStart >= 0 && !string.IsNullOrWhiteSpace(ReadString(envelope.Fields, "reply", ""));
            bool incompleteEffect = envelope.IncompleteField.Length > 0;
            bool missingDrinks = ConversationResponseNeedsDrinkActions(request) && !envelope.Fields.ContainsKey("drinkActions");
            bool recovered = envelope.Truncated && envelope.Error.Length == 0 && hasReply && !incompleteEffect && !missingDrinks;
            var evidence = TestDict("schema", "reign-conversation-response-recovery-v1",
                "originalContentSha256", DialogueService.ConversationExactTextHash(original), "originalCharacters", original.Length,
                "sourceStage", "sanitized_assistant_content",
                "providerAssistantContentSha256", ReadString(llm, "providerAssistantContentSha256", DialogueService.ConversationExactTextHash(original)),
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
                DialogueService.RemovePrivateReasoningFields(envelope.Fields);
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
