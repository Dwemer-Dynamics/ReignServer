using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunOllamaCloudProviderContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => rows.Add(TestDict("id", "ollama_cloud_" + id, "passed", passed, "summary", id));
            var settings = TestDict("llmProvider", NanoGptProvider, "apiUrl", "https://custom.invalid/chat",
                "apiKey", "fixture-custom", "nanoGptApiKey", "fixture-nano", "openRouterApiKey", "fixture-router",
                "dialogueModel", "nano-dialogue");
            NormalizeLlmProviderSettings(settings);
            check("aliases", NormalizeLlmProvider("Ollama Cloud") == OllamaCloudProvider
                && NormalizeLlmProvider("ollama-cloud") == OllamaCloudProvider && NormalizeLlmProvider("ollama") == OllamaCloudProvider);
            check("missing_key_no_fallback", !LlmProviderConfigured(TestDict("llmProvider", OllamaCloudProvider,
                "apiKey", "unrelated", "nanoGptApiKey", "unrelated", "openRouterApiKey", "unrelated")));
            MergeProviderSettings(settings, TestDict("llmProvider", OllamaCloudProvider, "ollamaCloudApiKey", "fixture-ollama"));
            check("endpoint_key_defaults", IsNamedLlmApiProvider(settings) && LlmApiUrl(settings) == OllamaCloudChatUrl
                && LlmApiKey(settings) == "fixture-ollama" && LlmProviderConfigured(settings)
                && ChatModelKeys.All(k => ReadString(settings, k, "") == DefaultOllamaCloudModel));
            var overrides = TestDict("apiUrl", "https://attacker.invalid/chat", "apiKey", "untrusted-key");
            check("payload_cannot_redirect_cloud_key", LlmRequestApiUrl(settings, overrides) == OllamaCloudChatUrl
                && LlmRequestApiKey(settings, overrides) == "fixture-ollama");
            var edits = ChatModelKeys.ToDictionary(k => k, k => (object)("ollama-fixture:" + k));
            MergeProviderSettings(settings, edits);
            settings = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(settings));
            NormalizeLlmProviderSettings(settings);
            foreach (string provider in ChatProviders.Where(p => p != OllamaCloudProvider))
            {
                MergeProviderSettings(settings, TestDict("llmProvider", provider));
                check("separate_route_" + provider, provider == CodexSubscriptionProvider ? UsesCodexSubscription(settings)
                    : LlmApiKey(settings) != "fixture-ollama" && LlmApiUrl(settings) != OllamaCloudChatUrl);
                MergeProviderSettings(settings, TestDict("llmProvider", OllamaCloudProvider));
                check("restore_models_" + provider, ChatModelKeys.All(k => ReadString(settings, k, "") == ReadString(edits, k, ""))
                    && LlmApiKey(settings) == "fixture-ollama");
            }
            foreach (object blank in new object[] { "", "  ", "************", null })
            {
                MergeProviderSettings(settings, TestDict("ollamaCloudApiKey", blank));
                check("preserve_masked_or_blank_" + rows.Count, LlmApiKey(settings) == "fixture-ollama");
            }
            var ui = SettingsForUi(settings);
            check("secret_masking", SensitiveSettingsKeys().Contains("ollamaCloudApiKey")
                && !Json.Serialize(ui).Contains("fixture-ollama") && ReadBool(ui, "ollamaCloudApiKeyPresent", false)
                && IsMaskedSecretValue(ReadString(ui, "ollamaCloudApiKey", "")));
            MergeProviderSettings(settings, TestDict("clearKeys", new[] { "ollamaCloudApiKey" }));
            check("clear_only_ollama", !LlmProviderConfigured(settings) && LlmApiKey(settings) == ""
                && ReadString(settings, "nanoGptApiKey", "") == "fixture-nano"
                && ReadString(settings, "openRouterApiKey", "") == "fixture-router" && ReadString(settings, "apiKey", "") == "fixture-custom");
            var legacy = TestDict("llmProvider", OpenAiCompatibleProvider, "apiUrl", OllamaCloudChatUrl,
                "apiKey", "fixture-legacy-ollama", "dialogueModel", "saved-model:tag");
            NormalizeLlmProviderSettings(legacy);
            check("legacy_migration", ReadString(legacy, "llmProvider", "") == OllamaCloudProvider
                && LlmApiKey(legacy) == "fixture-legacy-ollama" && ReadString(legacy, "dialogueModel", "") == "saved-model:tag"
                && !NormalizeLlmProviderSettings(legacy));
            check("host_boundary", IsOllamaCloudApiUrl(OllamaCloudChatUrl) && IsOllamaCloudApiUrl("https://OLLAMA.COM/v1")
                && !IsOllamaCloudApiUrl("https://ollama.com.attacker.invalid/v1") && !IsOllamaCloudApiUrl("http://ollama.com/v1")
                && !IsOllamaCloudApiUrl("https://ollama.com:8443/v1") && !IsOllamaCloudApiUrl("https://user@ollama.com/v1")
                && !IsOllamaCloudApiUrl("http://localhost:11434/v1"));
            var oldBank = TestDict("llmProvider", NanoGptProvider, "chatProviderProfilesInitialized", true, "dialogueModel", "existing-nano");
            NormalizeLlmProviderSettings(oldBank);
            check("existing_settings_add_bank_without_switch", ReadString(oldBank, "llmProvider", "") == NanoGptProvider
                && ReadString(oldBank, "dialogueModel", "") == "existing-nano"
                && ReadString(ReadDictionary(ReadDictionary(oldBank, "llmModelProfiles"), OllamaCloudProvider), "dialogueModel", "") == DefaultOllamaCloudModel);
            var messages = new List<Dictionary<string, object>> { TestDict("role", "user", "content", "Return JSON. Unicode 雪 😀") };
            var payload = TestDict("maxTokens", 3600, "response_format", TestDict("type", "json_object"),
                "extra", TestDict("response_format", TestDict("type", "json_schema"), "caching", true, "stickyprovider", true));
            var body = BuildChatRequestBody(settings, payload, messages, "exact-model:tag");
            check("chat_wire", ReadString(body, "model", "") == "exact-model:tag" && ReadInt(body, "max_tokens", 0) == 3600
                && !ReadBool(body, "stream", true) && !body.ContainsKey("response_format")
                && ReadString(ReadDictionaryList(body, "messages")[0], "content", "") == "Return JSON. Unicode 雪 😀"
                && ReadDictionary(payload, "response_format") != null);
            ConfigurePromptCacheRouting(settings, payload, body, OllamaCloudChatUrl, "exact-model:tag", "dialogue");
            check("no_nano_cache_or_probe", !body.ContainsKey("caching") && !body.ContainsKey("stickyprovider") && !SupportsPromptCacheProbe(settings));
            settings["reasoningMode"] = "selective"; settings["reasoningEffort"] = "medium";
            foreach (string type in new[] { "dialogue", "action_router" })
            {
                var wire = TestDict("reasoning", TestDict("effort", "high"), "reasoning_effort", "high");
                var route = ConfigureReasoningRouting(settings, TestDict(), wire, OllamaCloudChatUrl, "exact-model:tag", type);
                check("reasoning_" + type, ReadBool(route, "providerControlsApplied", false)
                    && ReadString(wire, "reasoning_effort", "") == (type == "dialogue" ? "medium" : "none") && !wire.ContainsKey("reasoning"));
            }
            var disabled = TestDict("reasoning_effort", "high");
            ConfigureReasoningRouting(settings, TestDict("reasoningDisabled", true), disabled, OllamaCloudChatUrl, "exact-model:tag", "dialogue");
            check("explicit_reasoning_disabled", ReadString(disabled, "reasoning_effort", "") == "none");
            var banter = TestDict("llmProvider", NanoGptProvider, "banterProvider", OllamaCloudProvider,
                "ollamaCloudApiKey", "fixture-banter", "llmModelProfiles", ReadDictionary(settings, "llmModelProfiles"));
            var banterRoute = BanterProviderSettings(TestDict("requestType", "party_banter"), banter);
            check("banter_uses_saved_ollama", ValidateBanterOptions(banter) == "" && LlmApiUrl(banterRoute) == OllamaCloudChatUrl
                && LlmApiKey(banterRoute) == "fixture-banter" && ReadString(banterRoute, "dialogueModel", "") == ReadString(edits, "dialogueModel", ""));
            string html = ControlCenterHtml();
            check("webui_settings", html.Contains("value='ollama_cloud'>Ollama Cloud") && html.Contains("id='ollamaCloudApiKey' type='password'")
                && html.Contains("https://ollama.com/settings/keys") && html.Contains("https://ollama.com/search?c=cloud"));
            return rows;
        }
    }
}
