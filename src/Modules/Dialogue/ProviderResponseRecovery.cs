using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // NanoGPT's observed GLM 5.3 reasoning_required rejection and the upstream
        // model contract require low/high/max. Omitting effort defaults to max;
        // a repair asking for direct output must use low rather than none.
        private static bool RequiresGlm53Reasoning(string apiUrl, string model)
        {
            if (!IsNanoGptApiUrl(apiUrl) && !IsOpenRouterApiUrl(apiUrl)) return false;
            string baseModel = (model ?? "").Trim().Split(':')[0];
            return Regex.IsMatch(baseModel, @"^(?:(?:z-ai|zai-org)/)?glm-5\.3(?:-flash)?(?:-uncensored)?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string Glm53ReasoningEffort(string requested)
        {
            switch ((requested ?? "").Trim().ToLowerInvariant())
            {
                case "high": case "medium": return "high";
                case "xhigh": case "max": return "max";
                default: return "low";
            }
        }

        private static List<Dictionary<string, object>> RunProviderResponseRecoverySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed) => results.Add(TestDict("caseId", "provider_recovery_" + name,
                "passed", passed, "summary", name));
            var settings = TestDict("reasoningMode", "selective", "reasoningEffort", "high");
            foreach (string url in new[] { NanoGptChatCompletionsUrl,
                "https://nano-gpt.com/api/subscription/v1/chat/completions", "https://openrouter.ai/api/v1/chat/completions" })
            foreach (string model in new[] { "z-ai/glm-5.3", "z-ai/glm-5.3-flash", "z-ai/glm-5.3-flash-uncensored",
                "zai-org/GLM-5.3", "glm-5.3:fast:reasoning-exclude" })
            {
                var body = TestDict("reasoning_effort", "none", "reasoning", TestDict("enabled", false),
                    "thinking", TestDict("type", "disabled"), "enable_thinking", false, "thinking_budget", 0, "max_tokens", 2200);
                var routing = ConfigureReasoningRouting(CloneDictionary(settings), TestDict("reasoningDisabled", true),
                    body, url, model, "dialogue_continuity_repair");
                Check("mandatory_low_" + PromptHash(url + model).Substring(0, 8),
                    ReadString(ReadDictionary(body, "reasoning"), "effort", "") == "low"
                    && ReadBool(ReadDictionary(body, "reasoning"), "exclude", false)
                    && !body.ContainsKey("reasoning_effort") && !body.ContainsKey("thinking")
                    && !body.ContainsKey("enable_thinking") && !body.ContainsKey("thinking_budget")
                    && ReadBool(routing, "modelRequiresReasoning", false)
                    && ReadInt(body, "max_tokens", 0) == 2200);
                Check("mandatory_effort_not_dropped_" + PromptHash(url + model).Substring(0, 8),
                    !ShouldRetryWithoutReasoning(routing, "unsupported reasoning parameter"));
            }
            foreach (string model in new[] { "z-ai/glm-5.2", "z-ai/glm-5.30", "custom/glm-5.3", "fixture-disabled-checker" })
            {
                var body = TestDict();
                ConfigureReasoningRouting(CloneDictionary(settings), TestDict("reasoningDisabled", true), body,
                    NanoGptChatCompletionsUrl, model, "repair");
                Check("unrelated_disabled_" + PromptHash(model).Substring(0, 8),
                    ReadString(ReadDictionary(body, "reasoning"), "effort", "") == "none");
            }
            Check("custom_endpoint_unchanged", !RequiresGlm53Reasoning("https://example.invalid/v1/chat/completions", "z-ai/glm-5.3"));
            string cooldownKey = ReasoningRouteKey(NanoGptChatCompletionsUrl, "z-ai/glm-5.3-flash");
            try
            {
                lock (PromptCacheStateLock) ReasoningFallbackCooldowns[cooldownKey] = DateTime.UtcNow.AddMinutes(1);
                var body = TestDict("reasoning_effort", "none");
                ConfigureReasoningRouting(CloneDictionary(settings), TestDict("reasoningDisabled", true), body,
                    NanoGptChatCompletionsUrl, "z-ai/glm-5.3-flash", "repair");
                Check("mandatory_model_cooldown_never_defaults_to_max", !body.ContainsKey("reasoning_effort")
                    && ReadString(ReadDictionary(body, "reasoning"), "effort", "") == "low");
            }
            finally { lock (PromptCacheStateLock) ReasoningFallbackCooldowns.Remove(cooldownKey); }
            Check("supported_effort_mapping", Glm53ReasoningEffort("none") == "low"
                && Glm53ReasoningEffort("minimal") == "low" && Glm53ReasoningEffort("low") == "low"
                && Glm53ReasoningEffort("medium") == "high" && Glm53ReasoningEffort("high") == "high"
                && Glm53ReasoningEffort("xhigh") == "max");

            // Reproduce the production failure: complete metadata and visible
            // speech under the single empty key, rather than the older two-key shift.
            var shifted = CompleteConversationStructuredResponse(TestDict("reply", "No disappointment. We can listen together."), "dialogue", out _);
            shifted["structuredActionDecision"] = StructuredActionDecisionExample();
            string speech = ReadString(shifted, "reply", ""); shifted.Remove("reply"); shifted[""] = speech;
            int calls = 0;
            var request = TestDict("requestType", "dialogue", "model", "z-ai/glm-5.3-flash", "structuredActionProtocolVersion", 1);
            var recovered = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(shifted)), request,
                "", "empty-key-regression", "dialogue", "npc", "", _ => { calls++; throw new InvalidOperationException("Local repair must not call a provider."); });
            var parsed = TryParseJsonObject(ReadString(recovered, "content", ""));
            Check("single_empty_reply_key_no_provider", calls == 0 && ReadBool(recovered, "ok", false)
                && ReadString(parsed, "reply", "") == speech && !parsed.ContainsKey("")
                && StructuredActionResponseHasDecision(ReadString(recovered, "content", ""), request));
            Check("single_empty_key_preserves_metadata", Json.Serialize(ReadDictionary(parsed, "structuredActionDecision"))
                == Json.Serialize(ReadDictionary(shifted, "structuredActionDecision")));
            var malformed = CloneDictionary(shifted); malformed["structuredActionDecision"] = TestDict("kind", "accept");
            var malformedResult = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(malformed)), request,
                "", "malformed-consent-regression", "dialogue", "npc", "", _ => { calls++; throw new InvalidOperationException("Malformed consent must not be repaired."); });
            Check("empty_key_cannot_repair_malformed_consent", calls == 0 && !ReadBool(malformedResult, "ok", true));
            var ambiguous = CloneDictionary(shifted); ambiguous[" "] = "Another possible answer.";
            Check("ambiguous_empty_keys_rejected", !TryRepairShiftedConversationReplyKeys(ambiguous, out _));
            var explicitReply = CloneDictionary(shifted); explicitReply["reply"] = "";
            Check("explicit_empty_reply_not_overwritten", !TryRepairShiftedConversationReplyKeys(explicitReply, out _));
            Check("untyped_empty_key_not_promoted", !TryRepairShiftedConversationReplyKeys(
                new Dictionary<string, object> { [""] = "A private memory, not known dialogue." }, out _));

            // A reply-only result needs both conservative schema completion and
            // a real verified action decision. Never fabricate consent locally.
            var sparseRequest = CloneDictionary(request);
            sparseRequest["structuredActionRepairContext"] = TestDict("playerId", "player", "npcId", "npc", "conversationId", "fixture", "worldDay", 1d);
            sparseRequest["playerText"] = "I enjoy listening.";
            calls = 0;
            var sparse = RetryMalformedStructuredResponse(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", speech))),
                sparseRequest, "", "reply-only-regression", "dialogue", "npc", "", repair =>
                {
                    calls++;
                    Check("metadata_repair_preserves_selected_model", ReadString(repair, "model", "") == "z-ai/glm-5.3-flash");
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("structuredActionDecision", StructuredActionDecisionExample())));
                });
            Check("reply_only_recovers_full_shape", calls == 1 && ReadBool(sparse, "ok", false)
                && StructuredResponseIsComplete(ReadString(sparse, "content", ""), "dialogue")
                && ReadString(TryParseJsonObject(ReadString(sparse, "content", "")), "reply", "") == speech);
            return results;
        }
    }
}
