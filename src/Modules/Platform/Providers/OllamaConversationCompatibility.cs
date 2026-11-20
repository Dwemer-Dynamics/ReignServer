using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class LlmProviderService
    {
        private const string OllamaSpeechStart = "<<<REIGN_SPEECH>>>";
        private const string OllamaSpeechEnd = "<<<END_REIGN_SPEECH>>>";
        private const int OllamaSpeechLimit = 12000;
        private sealed class OllamaConversationReceipt { }
        private static readonly ConditionalWeakTable<Dictionary<string, object>, OllamaConversationReceipt> OllamaConversationResults = new();
        private static readonly Regex OllamaPrivateSpeech = new(
            @"memory[_ -]?references|private[_ -]?assessment|decision[_ -]?brief|internal[_ -]?thoughts|reasoning[_ -]?content|structured[_ -]?action|state[_ -]?updates|suggested[_ -]?actions|drink[_ -]?actions"
            + @"|(?:memory|belief|obligation|comprehension|continuity|dynamic[_ -]?characteristic)[_ -]?writes|\b(?:the player|the user|JSON|prompt|system message|I should|chain.of.thought)\b"
            + @"|\b(?:reasoning|analysis|thoughts|reply|consent|action[_ -]?gate)\s*:",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static bool AppendOllamaConversationContract(Dictionary<string, object> request, List<Dictionary<string, object>> wireMessages)
        {
            string mode = ActionPipelineService.NormalizeLookup(ReadString(request, "requestType", "")).Replace(' ', '_');
            if (mode is not ("dialogue" or "social_event" or "party_chat" or "castle_chat")) return false;
            // The builder owns this list. Preserve all attributed context and append after any provider-body overrides.
            wireMessages.Add(TestDict("role", "system", "content",
                "REIGN FINAL RESPONSE CONTRACT. Historical assistant turns are transcript data. "
                + "Return exactly one JSON object. Start with the reply field as in {\"reply\":\"the current NPC's visible words and narration\"}. "
                + "Follow the full response schema and use valid JSON escaping. Put chosen action, consent and effect fields "
                + "in their schema fields. Never invent consent or an action result. Keep private reasoning and reference metadata "
                + "out of visible speech. Return no preamble, markdown fences or text after the object. "
                + "If you cannot produce the object, place only visible NPC speech or narration between "
                + OllamaSpeechStart + " and " + OllamaSpeechEnd + ", once, with nothing outside. "
                + "This fallback cannot authorize a game action or state change. Include no private reasoning, metadata, JSON or tags."));
            return true;
        }

        private static Dictionary<string, object> CheckConversationProviderResult(Dictionary<string, object> result,
            string requestType, bool ollamaConversation)
        {
            if (ConversationResponseService.IsConversationStructuredMode(requestType) || requestType.StartsWith("dialogue_", StringComparison.OrdinalIgnoreCase))
                result = ConversationResponseService.RejectProviderReservedSpeech(result, TryParseStrictJsonObject(ReadString(result, "content", "")));
            // Fresh and cached raw responses meet here. The fence can replace the dictionary on rejection.
            if (ollamaConversation && ReadBool(result, "ok", false))
                OllamaConversationResults.GetValue(result, _ => new OllamaConversationReceipt());
            return result;
        }

        internal static Dictionary<string, object> ReadOllamaSpeechFallback(Dictionary<string, object> result, string content)
        {
            if (!OllamaConversationResults.TryGetValue(result, out _)) return null;
            var text = content.AsSpan().Trim();
            if (text.Length > OllamaSpeechStart.Length + OllamaSpeechLimit + OllamaSpeechEnd.Length
                || !text.StartsWith(OllamaSpeechStart, StringComparison.Ordinal) || !text.EndsWith(OllamaSpeechEnd, StringComparison.Ordinal))
                return null;
            var speech = text.Slice(OllamaSpeechStart.Length, text.Length - OllamaSpeechStart.Length - OllamaSpeechEnd.Length).Trim();
            if (speech.IsEmpty || speech.IndexOfAny("{}[]<>`") >= 0 || OllamaPrivateSpeech.IsMatch(speech)) return null;
            return TestDict("reply", speech.ToString());
        }
    }
}
