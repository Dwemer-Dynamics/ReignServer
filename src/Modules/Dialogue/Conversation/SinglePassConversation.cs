using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Only a server route can opt into this delivery policy. Serialized flags
        // from a client/model are never a receipt and never authorize an action.
        private sealed class SinglePassConversationReceipt { }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, SinglePassConversationReceipt> SinglePassConversations = new();
        internal static bool IsSinglePassConversation(Dictionary<string, object> payload) =>
            payload != null && SinglePassConversations.TryGetValue(payload, out _);
        internal static void BeginSinglePassConversation(Dictionary<string, object> payload) =>
            SinglePassConversations.GetValue(payload, _ => new SinglePassConversationReceipt());

        internal static Dictionary<string, object> GenerateSinglePassConversation(Dictionary<string, object> request,
            Dictionary<string, object> payload, string mode, bool allowQuiet = false,
            Func<Dictionary<string, object>, Dictionary<string, object>> responder = null)
        {
            BeginSinglePassConversation(payload);
            return PrepareSinglePassConversationResponse((responder ?? LlmProviderService.ChatWithLlm)(request), mode, allowQuiet);
        }

        // Decode the one authored answer. No judge, rewrite, missing-metadata
        // model call or content-driven regeneration is dispatched here.
        private static Dictionary<string, object> PrepareSinglePassConversationResponse(Dictionary<string, object> result,
            string mode, bool allowQuiet = false)
        {
            if (!ReadBool(result, "ok", false)) return EnsureConversationDelivery(result, allowQuiet);
            var response = VerificationService.CloneDictionary(result);
            string content = ReadString(response, "content", "");
            var parsed = TryParseStrictJsonObject(content);
            bool speechOnly = false;
            if (parsed == null)
            {
                var envelope = ConversationResponseService.ReadConversationResponseEnvelope(content);
                // Only a complete outer reply string is eligible. Never search
                // nested/private fields, broken strings or duplicate documents.
                if (envelope.Truncated && envelope.Error.Length == 0 && envelope.ReplyStart >= 0)
                {
                    parsed = TestDict("reply", ReadString(envelope.Fields, "reply", ""));
                    speechOnly = true; // Discard even complete effects before a truncated tail.
                }
            }
            bool letter = mode.Contains("correspond", StringComparison.OrdinalIgnoreCase);
            string visible = new[] { letter ? "body" : "reply", "response", "text", letter ? "letter" : "content" }
                .Select(key => parsed != null && parsed.TryGetValue(key, out var value) && value is string text ? text : "")
                .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text)) ?? "";
            bool quiet = parsed != null && allowQuiet && IsIntentionalConversationSilence(parsed);
            bool noLetter = letter && parsed != null && !ReadBool(parsed, "shouldReply", true) && string.IsNullOrWhiteSpace(visible);
            if (parsed == null || (!quiet && !noLetter && string.IsNullOrWhiteSpace(visible)))
            {
                response["ok"] = false;
                response["content"] = "";
                response["errorCode"] = "conversation_response_unavailable";
                response["error"] = "The character did not return a readable response. Your message can be retried.";
                return EnsureConversationDelivery(response, allowQuiet);
            }
            parsed[letter ? "body" : "reply"] = visible;
            var speechView = parsed;
            if (letter) { speechView = VerificationService.CloneDictionary(parsed); speechView["reply"] = visible; }
            response = ConversationResponseService.RejectProviderReservedSpeech(response, speechView);
            if (!ReadBool(response, "ok", false)) return EnsureConversationDelivery(response, allowQuiet);
            parsed = ConversationResponseService.CompleteConversationStructuredResponse(parsed, ActionPipelineService.NormalizeLookup(mode).Replace(' ', '_'), out var fields);
            // Missing consent stays missing. The independent action protocol checks
            // participants, proposal identity, terms, resources and replay receipts.
            response["content"] = Json.Serialize(parsed);
            response["conversationEffectsUnavailable"] = speechOnly;
            response["conversationDelivery"] = TestDict("policy", "single_pass", "additionalProviderCalls", 0,
                "metadataDefaults", fields, "speechOnlyRecovery", speechOnly);
            return letter ? response : EnsureConversationDelivery(response, allowQuiet);
        }
    }
}
