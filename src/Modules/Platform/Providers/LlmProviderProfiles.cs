using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class LlmProviderService
    {
        internal const string NanoGptProvider = "nanogpt";
        internal const string OpenRouterProvider = "openrouter";
        internal const string OpenRouterChatUrl = "https://openrouter.ai/api/v1/chat/completions";
        internal const string OllamaCloudProvider = "ollama_cloud";
        internal const string OllamaCloudChatUrl = "https://ollama.com/v1/chat/completions";
        private const string DefaultOllamaCloudModel = "gemma4:31b";
        internal static readonly string[] ChatModelKeys = { "dialogueModel", "characterConstructionModel", "diplomacyModel",
            "eventsModel", "fastModel", "memoryModel", "relationshipModel", "correspondenceModel", "strategyModel",
            "actionPlannerModel", "actionRouterModel" };
        internal static readonly string[] ChatProviders = { NanoGptProvider, OpenRouterProvider, OllamaCloudProvider, OpenAiCompatibleProvider, CodexSubscriptionProvider };

        // Compatibility only: old settings must not resurrect retired remote memory stages.
        private static readonly string[] RetiredMemorySettings = { "highMemoryMode", "memoryCheckerModel", "memoryCheckerTimeoutMs", "memoryEvidenceModel", "memoryEvidenceTimeoutMs", "memoryEvidenceReasoningEffort", "memoryEvidencePlanningReasoningEffort", "memoryEvidenceInputTokenBudget", "memoryEvidenceAdaptiveRetrieval", "memoryFinalCheckEnabled", "enableMemoryPrecisionHelper", "selectorModel" };

        private static bool RemoveRetiredMemorySettings(Dictionary<string, object> settings)
        {
            bool changed = false;
            if (settings != null) foreach (string key in RetiredMemorySettings) changed |= settings.Remove(key);
            return changed;
        }

        internal static bool IsRetiredMemoryRequest(string purpose) =>
            purpose == "dialogue_evidence" || purpose == "dialogue_verification" || purpose == "dialogue_memory_repair" || purpose == "context_selector";

        internal static List<string> MergeProviderSettings(Dictionary<string, object> settings, Dictionary<string, object> incoming)
        {
            NormalizeLlmProviderSettings(settings);
            NormalizeCodexOptions(settings);
            string previousProvider = ReadString(settings, "llmProvider", "");
            var previousBank = ReadDictionary(settings, "llmModelProfiles");
            var clearKeys = ReadStringList(incoming, "clearKeys");
            string previousOrigin = SettingsService.NormalizedOrigin(ReadString(settings, "apiUrl", ""));
            foreach (var pair in incoming ?? new Dictionary<string, object>())
            {
                // Bindings change only through the Global API Keys routes, which choose provider and origin themselves.
                if (pair.Key == "clearKeys" || pair.Key == "chatProviderProfilesInitialized" || pair.Key == "globalKeyBindings") continue;
                if (pair.Key == "codexOptions")
                {
                    var suppliedCodexOptions = ReadDictionary(incoming, "codexOptions");
                    var mergedCodexOptions = CharactersService.DeepCloneProfileDictionary(ReadDictionary(settings, "codexOptions") ?? new Dictionary<string, object>());
                    if (suppliedCodexOptions != null) foreach (var option in suppliedCodexOptions) mergedCodexOptions[option.Key] = option.Value;
                    settings["codexOptions"] = mergedCodexOptions;
                    continue;
                }
                if (SettingsService.SensitiveSettingsKeys().Contains(pair.Key) && !clearKeys.Contains(pair.Key)
                    && (pair.Value == null || string.IsNullOrWhiteSpace(Convert.ToString(pair.Value)) || SettingsService.IsMaskedSecretValue(Convert.ToString(pair.Value)))) continue;
                settings[pair.Key] = pair.Value;
            }
            // A legacy local custom key belongs to the origin it was saved for. A new origin clears it (settings and, through
            // the explicit-clear vault write, the installed vault) unless this merge supplies its own key; a bound slot
            // already fails closed on mismatch and keeps its reference, but a Reign-only key saved beside it is cleared too.
            string suppliedApiKey = ReadString(incoming, "apiKey", "");
            bool apiKeyClearable = SettingsService.GlobalKeyBinding(settings, "apiKey") == null || !string.IsNullOrWhiteSpace(ReadString(settings, "apiKey", ""));
            if (SettingsService.NormalizedOrigin(ReadString(settings, "apiUrl", "")) != previousOrigin && apiKeyClearable
                && (string.IsNullOrWhiteSpace(suppliedApiKey) || SettingsService.IsMaskedSecretValue(suppliedApiKey)) && !clearKeys.Contains("apiKey"))
                clearKeys.Add("apiKey");
            foreach (string key in clearKeys) if (SettingsService.SensitiveSettingsKeys().Contains(key)) settings[key] = "";
            MergeChatProviderProfiles(settings, incoming, previousBank, previousProvider);
            NormalizeLlmProviderSettings(settings);
            NormalizeCodexOptions(settings);
            return clearKeys;
        }

        internal static Dictionary<string, object> BuildChatRequestBody(Dictionary<string, object> settings,
            Dictionary<string, object> payload, List<Dictionary<string, object>> messages, string model)
            => BuildChatRequestBody(settings, payload, messages, model, out _);

        private static Dictionary<string, object> BuildChatRequestBody(Dictionary<string, object> settings,
            Dictionary<string, object> payload, List<Dictionary<string, object>> messages, string model, out bool ollamaConversation)
        {
            var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages,
                ["temperature"] = ReadDouble(payload, "temperature", ReadDouble(settings, "temperature", 0.7d)),
                ["max_tokens"] = ReadInt(payload, "maxTokens", ReadInt(settings, "maxTokens", 900)), ["stream"] = false };
            foreach (string key in new[] { "response_format", "tools", "tool_choice", "stop", "seed" }) ActionPipelineService.CopyOptional(payload, body, key);
            var extra = ReadDictionary(payload, "extra");
            if (extra != null) foreach (var pair in extra) body[pair.Key] = pair.Value;
            var wireMessages = CharactersService.RequiredPoliticalMessages(payload, ReadDictionaryList(body, "messages"));
            body["messages"] = wireMessages;
            ollamaConversation = false;
            if (NormalizeLlmProvider(ReadString(settings, "llmProvider", "")) == OllamaCloudProvider)
            {
                body.Remove("response_format");
                ollamaConversation = AppendOllamaConversationContract(payload, wireMessages);
            }
            return body;
        }

        internal static bool IsOllamaCloudApiUrl(string value) =>
            Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.Scheme == "https" && uri.IsDefaultPort
            && string.IsNullOrEmpty(uri.UserInfo) && uri.Host.Equals("ollama.com", StringComparison.OrdinalIgnoreCase);

        private static bool IsNamedLlmApiProvider(Dictionary<string, object> settings)
        {
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            return provider == NanoGptProvider || provider == OpenRouterProvider || provider == OllamaCloudProvider;
        }

        // Outbound provider URLs are absolute http(s) with a host and no embedded credentials; anything else (file:, ftp:,
        // userinfo) is never handed to the transport. Plain HTTP stays valid for local/private OpenAI-compatible servers.
        internal static bool IsDispatchableProviderUrl(string value) =>
            Uri.TryCreate((value ?? "").Trim(), UriKind.Absolute, out Uri uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && string.IsNullOrEmpty(uri.UserInfo) && !string.IsNullOrEmpty(uri.Host);

        // Named providers always use their fixed URL. The custom endpoint may be overridden per request; a stored key then
        // follows only a same-origin override, while an explicit one-off payload key goes where the caller sent it.
        // ChatWithLlm refuses an override that would rely on the saved key elsewhere (StoredKeyForeignDestination) and
        // ProviderSecret withholds it as well; a bound key keeps its own global_key_unavailable refusal.
        private static string LlmRequestApiUrl(Dictionary<string, object> settings, Dictionary<string, object> payload) =>
            IsNamedLlmApiProvider(settings) ? LlmApiUrl(settings) : ReadString(payload, "apiUrl", LlmApiUrl(settings));

        // True when the request carries no key of its own and would otherwise use the slot's saved Reign-only key (which
        // wins over any binding) at a destination outside that key's origin. Empty saved keys (deliberately keyless
        // endpoints) never match.
        internal static bool StoredKeyForeignDestination(Dictionary<string, object> settings, Dictionary<string, object> payload, string apiUrl)
        {
            if (UsesCodexSubscription(settings) || (payload != null && payload.ContainsKey("apiKey"))) return false;
            string slot = SettingsService.LlmApiKeySlot(settings);
            return !string.IsNullOrWhiteSpace(ReadString(settings, slot, ""))
                && !SettingsService.LocalKeyDestinationAllowed(settings, slot, new[] { apiUrl });
        }

        private static string LlmRequestApiKey(Dictionary<string, object> settings, Dictionary<string, object> payload)
            => ResolveLlmRequestApiKey(settings, payload, out _);

        internal static string ResolveLlmRequestApiKey(Dictionary<string, object> settings,
            Dictionary<string, object> payload, out bool usesBinding, string actualApiUrl = null)
        {
            usesBinding = false;
            if (NormalizeLlmProvider(ReadString(settings, "llmProvider", "")) == CodexSubscriptionProvider) return "";
            bool named = IsNamedLlmApiProvider(settings);
            if (!named && payload != null && payload.ContainsKey("apiKey")) return ReadString(payload, "apiKey", "");
            string slot = SettingsService.LlmApiKeySlot(settings);
            string destination = named ? LlmApiUrl(settings) : actualApiUrl ?? ReadString(payload, "apiUrl", LlmApiUrl(settings));
            string key = SettingsService.ProviderSecret(settings, slot, destination);
            // usesBinding: the slot is bound, or its automatic global key could not be resolved. Either way an empty key is
            // a refusal (global_key_unavailable), never a keyless request. Checked immediately, on this thread.
            usesBinding = SettingsService.HasGlobalKeyBindingEntry(settings, slot) || SettingsService.ProviderKeyUnavailable(settings, slot, key);
            return key;
        }

        private static string DefaultChatProviderModel(string provider) => provider == OllamaCloudProvider ? DefaultOllamaCloudModel
            : provider == OpenRouterProvider ? "openai/gpt-5.5" : provider == CodexSubscriptionProvider ? "gpt-5.5" : "zai-org/glm-5.2";

        internal static bool IsOpenRouterApiUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.Scheme == "https"
                && uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);
        }

        internal static string LlmApiUrl(Dictionary<string, object> settings)
        {
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            return provider == OpenRouterProvider ? OpenRouterChatUrl
                : provider == OllamaCloudProvider ? OllamaCloudChatUrl
                : provider == NanoGptProvider ? PortraitsService.NanoGptChatCompletionsUrl : ReadString(settings, "apiUrl", "");
        }

        // Outbound use only: resolves a bound global key for the configured endpoint. Presence checks use ProviderKeyConfigured.
        internal static string LlmApiKey(Dictionary<string, object> settings) =>
            NormalizeLlmProvider(ReadString(settings, "llmProvider", "")) == CodexSubscriptionProvider ? ""
                : SettingsService.ProviderSecret(settings, SettingsService.LlmApiKeySlot(settings), LlmApiUrl(settings));

        // Flat active model fields remain the runtime contract. The bank contains only model IDs, never credentials.
        private static Dictionary<string, object> ChatModelProfile(Dictionary<string, object> source, string fallback)
        {
            return ChatModelKeys.ToDictionary(key => key, key => (object)ReadString(source, key,
                fallback).Trim());
        }

        internal static bool NormalizeChatProviderProfiles(Dictionary<string, object> settings)
        {
            string before = Json.Serialize(settings);
            // Snapshot before any plaintext compatibility copy can change source/target precedence.
            var beforeNormalization = CloneDictionary(settings);
            SettingsService.MigrateLegacyOllamaGlobalBinding(settings);
            bool legacyBound = SettingsService.HasGlobalKeyBindingEntry(beforeNormalization, "apiKey");
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            if (!ReadBool(settings, "chatProviderProfilesInitialized", false))
            {
                string legacyUrl = ReadString(settings, "apiUrl", "");
                if (provider == OpenAiCompatibleProvider && DialogueService.IsNanoGptApiUrl(legacyUrl)) provider = NanoGptProvider;
                if (provider == OpenAiCompatibleProvider && IsOpenRouterApiUrl(legacyUrl)) provider = OpenRouterProvider;
                if (provider == OpenAiCompatibleProvider && IsOllamaCloudApiUrl(legacyUrl)) provider = OllamaCloudProvider;
                // Copy the old key only to its proven endpoint owner, including when Codex is currently selected.
                if (!settings.ContainsKey("nanoGptApiKey")) settings["nanoGptApiKey"] = !legacyBound && DialogueService.IsNanoGptApiUrl(legacyUrl) ? ReadString(beforeNormalization, "apiKey", "") : "";
                if (!settings.ContainsKey("openRouterApiKey")) settings["openRouterApiKey"] = !legacyBound && IsOpenRouterApiUrl(legacyUrl) ? ReadString(beforeNormalization, "apiKey", "") : "";
                if (!settings.ContainsKey("ollamaCloudApiKey")) settings["ollamaCloudApiKey"] = !legacyBound && IsOllamaCloudApiUrl(legacyUrl) ? ReadString(beforeNormalization, "apiKey", "") : "";
                settings["chatProviderProfilesInitialized"] = true;
            }
            settings["llmProvider"] = provider;
            var prior = ReadDictionary(settings, "llmModelProfiles") ?? new Dictionary<string, object>();
            var bank = new Dictionary<string, object>();
            foreach (string id in ChatProviders)
                bank[id] = ChatModelProfile(ReadDictionary(prior, id), DefaultChatProviderModel(id));
            bank[provider] = ChatModelProfile(settings, "");
            settings["llmModelProfiles"] = bank;
            return before != Json.Serialize(settings);
        }

        private static void MergeChatProviderProfiles(Dictionary<string, object> settings, Dictionary<string, object> incoming,
            Dictionary<string, object> previousBank, string previousProvider)
        {
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            var supplied = ReadDictionary(incoming, "llmModelProfiles");
            var bank = new Dictionary<string, object>();
            foreach (string id in ChatProviders)
            {
                var source = ReadDictionary(supplied, id) ?? ReadDictionary(previousBank, id);
                bank[id] = ChatModelProfile(source, id == OllamaCloudProvider ? DefaultOllamaCloudModel : id == OpenRouterProvider ? "openai/gpt-5.5" : "gpt-5.5");
            }
            var active = ReadDictionary(bank, provider);
            foreach (string key in ChatModelKeys)
            {
                // Provider-only API updates restore the destination bank; explicit model edits target the selected provider.
                if (incoming != null && incoming.ContainsKey(key)) active[key] = ReadString(incoming, key, "").Trim();
                else if (provider == previousProvider) active[key] = ReadString(settings, key, "");
                settings[key] = active[key];
            }
            settings["llmModelProfiles"] = bank;
        }
    }
}
