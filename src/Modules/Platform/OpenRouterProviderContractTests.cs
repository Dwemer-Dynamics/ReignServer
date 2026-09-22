using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunOpenRouterProviderContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => rows.Add(new Dictionary<string, object> {
                ["id"] = "openrouter_" + id, ["passed"] = passed, ["summary"] = id });
            var settings = new Dictionary<string, object> { ["llmProvider"] = "openai_compatible",
                ["apiUrl"] = NanoGptChatCompletionsUrl, ["apiKey"] = "fixture-nano", ["dialogueModel"] = "legacy-dialogue",
                ["selectorModel"] = "legacy-selector", ["actionPlannerModel"] = "legacy-planner", ["actionRouterModel"] = "legacy-router" };
            check("migration", NormalizeLlmProviderSettings(settings) && LlmApiUrl(settings) == NanoGptChatCompletionsUrl
                && LlmApiKey(settings) == "fixture-nano" && ReadString(settings, "llmProvider", "") == NanoGptProvider
                && ReadString(settings, "openRouterApiKey", "") == "" && !NormalizeLlmProviderSettings(settings));
            var legacyModels = ChatModelProfile(settings, "");
            MergeProviderSettings(settings, new Dictionary<string, object> { ["llmProvider"] = "openrouter", ["openRouterApiKey"] = "fixture-router" });
            check("switch_endpoint_key_models", LlmApiUrl(settings) == OpenRouterChatUrl && LlmApiKey(settings) == "fixture-router"
                && LlmProviderConfigured(settings) && ChatModelKeys.All(k => ReadString(settings, k, "") == "openai/gpt-5.5"));
            var edits = ChatModelKeys.ToDictionary(k => k, k => (object)("openai/fixture-" + k));
            MergeProviderSettings(settings, edits);
            settings = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
            NormalizeLlmProviderSettings(settings);
            foreach (string destination in new[] { NanoGptProvider, CodexSubscriptionProvider, OpenRouterProvider })
            {
                MergeProviderSettings(settings, new Dictionary<string, object> { ["llmProvider"] = destination });
                check("restore_" + destination, destination == NanoGptProvider
                    ? ChatModelKeys.All(k => ReadString(settings, k, "") == ReadString(legacyModels, k, "")) && LlmApiKey(settings) == "fixture-nano"
                    : destination == OpenRouterProvider ? ChatModelKeys.All(k => ReadString(settings, k, "") == ReadString(edits, k, ""))
                    : UsesCodexSubscription(settings));
            }
            foreach (object blank in new object[] { "", "  ", "************", null })
            {
                MergeProviderSettings(settings, new Dictionary<string, object> { ["nanoGptApiKey"] = blank, ["openRouterApiKey"] = blank });
                check("preserve_blank_" + rows.Count, ReadString(settings, "nanoGptApiKey", "") == "fixture-nano" && LlmApiKey(settings) == "fixture-router");
            }
            var ui = SettingsForUi(settings);
            string uiJson = Json.Serialize(ui);
            check("masked_keys", !uiJson.Contains("fixture-nano") && !uiJson.Contains("fixture-router")
                && ReadBool(ui, "openRouterApiKeyPresent", false) && ReadBool(ui, "nanoGptApiKeyPresent", false));
            MergeProviderSettings(settings, new Dictionary<string, object> { ["clearKeys"] = new[] { "openRouterApiKey" } });
            NormalizeLlmProviderSettings(settings);
            check("clear_isolated_no_fallback", LlmApiKey(settings) == "" && !LlmProviderConfigured(settings)
                && ReadString(settings, "nanoGptApiKey", "") == "fixture-nano");
            var custom = new Dictionary<string, object> { ["apiUrl"] = "https://custom.invalid/v1/chat/completions", ["apiKey"] = "fixture-custom", ["dialogueModel"] = "custom-model" };
            NormalizeLlmProviderSettings(custom);
            check("custom_migration", LlmApiUrl(custom).StartsWith("https://custom.invalid/") && LlmApiKey(custom) == "fixture-custom"
                && ReadString(custom, "nanoGptApiKey", "") == "" && ReadString(custom, "openRouterApiKey", "") == "");
            check("host_boundary", IsOpenRouterApiUrl(OpenRouterChatUrl) && !IsOpenRouterApiUrl("https://openrouter.ai.attacker.invalid/api/v1")
                && !IsOpenRouterApiUrl("http://openrouter.ai/api/v1") && NormalizeLlmProvider("OpenRouter") == OpenRouterProvider);
            var payload = new Dictionary<string, object> { ["maxTokens"] = 3600,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" } };
            var messages = new List<Dictionary<string, object>> { new Dictionary<string, object> { ["role"] = "user", ["content"] = "Unicode 雪 😀" } };
            var body = BuildChatRequestBody(settings, payload, messages, "openai/gpt-5.5");
            check("chat_wire_contract", ReadString(body, "model", "") == "openai/gpt-5.5" && ReadInt(body, "max_tokens", 0) == 3600
                && !ReadBool(body, "stream", true) && ReadDictionary(body, "response_format") != null
                && ReadString(ReadDictionaryList(Json.Deserialize<Dictionary<string, object>>(Json.Serialize(body)), "messages")[0], "content", "") == "Unicode 雪 😀");
            settings["reasoningMode"] = "selective"; settings["reasoningEffort"] = "medium";
            foreach (string type in new[] { "dialogue", "context_selector", "action_router" })
            {
                var b = new Dictionary<string, object>();
                var route = ConfigureReasoningRouting(settings, new Dictionary<string, object>(), b, OpenRouterChatUrl, "openai/gpt-5.5", type);
                var reasoning = ReadDictionary(b, "reasoning");
                check("reasoning_" + type, ReadBool(route, "providerControlsApplied", false) && ReadBool(reasoning, "exclude", false)
                    && ReadString(reasoning, "effort", "") == (type == "dialogue" ? "medium" : "none"));
            }
            body["caching"] = true; body["stickyprovider"] = true;
            ConfigurePromptCacheRouting(settings, payload, body, OpenRouterChatUrl, "openai/gpt-5.5", "dialogue");
            check("no_nano_cache_flags", !body.ContainsKey("caching") && !body.ContainsKey("stickyprovider"));
            var drafts = new Dictionary<string, object> { [NanoGptProvider] = new Dictionary<string, object> { ["dialogueModel"] = "draft-nano" },
                [OpenRouterProvider] = new Dictionary<string, object> { ["dialogueModel"] = "openai/draft-router" } };
            MergeProviderSettings(settings, new Dictionary<string, object> { ["llmModelProfiles"] = drafts, ["dialogueModel"] = "openai/draft-router" });
            MergeProviderSettings(settings, new Dictionary<string, object> { ["llmProvider"] = NanoGptProvider });
            check("inactive_draft_preserved", ReadString(settings, "dialogueModel", "") == "draft-nano");
            check("nano_cache_probe_allowed", SupportsPromptCacheProbe(settings));
            settings["llmProvider"] = OpenRouterProvider;
            check("router_cache_probe_blocked", !SupportsPromptCacheProbe(settings));
            settings["llmProvider"] = CodexSubscriptionProvider;
            check("codex_cache_probe_blocked", !SupportsPromptCacheProbe(settings));
            return rows;
        }
    }
}
