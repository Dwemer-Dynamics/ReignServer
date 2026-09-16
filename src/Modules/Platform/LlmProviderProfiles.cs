using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string NanoGptProvider = "nanogpt";
        private const string OpenRouterProvider = "openrouter";
        private const string OpenRouterChatUrl = "https://openrouter.ai/api/v1/chat/completions";
        private static readonly string[] ChatModelKeys = { "dialogueModel", "characterConstructionModel", "diplomacyModel",
            "eventsModel", "fastModel", "memoryModel", "relationshipModel", "correspondenceModel", "strategyModel",
            "selectorModel", "actionPlannerModel", "actionRouterModel" };
        private static readonly string[] ChatProviders = { NanoGptProvider, OpenRouterProvider, OpenAiCompatibleProvider, CodexSubscriptionProvider };

        private static List<string> MergeProviderSettings(Dictionary<string, object> settings, Dictionary<string, object> incoming)
        {
            NormalizeLlmProviderSettings(settings);
            NormalizeCodexOptions(settings);
            string previousProvider = ReadString(settings, "llmProvider", "");
            var previousBank = ReadDictionary(settings, "llmModelProfiles");
            var clearKeys = ReadStringList(incoming, "clearKeys");
            foreach (var pair in incoming ?? new Dictionary<string, object>())
            {
                if (pair.Key == "clearKeys" || pair.Key == "chatProviderProfilesInitialized") continue;
                if (pair.Key == "codexOptions")
                {
                    var suppliedCodexOptions = ReadDictionary(incoming, "codexOptions");
                    var mergedCodexOptions = DeepCloneProfileDictionary(ReadDictionary(settings, "codexOptions") ?? new Dictionary<string, object>());
                    if (suppliedCodexOptions != null) foreach (var option in suppliedCodexOptions) mergedCodexOptions[option.Key] = option.Value;
                    settings["codexOptions"] = mergedCodexOptions;
                    continue;
                }
                if (SensitiveSettingsKeys().Contains(pair.Key) && !clearKeys.Contains(pair.Key)
                    && (pair.Value == null || string.IsNullOrWhiteSpace(Convert.ToString(pair.Value)) || IsMaskedSecretValue(Convert.ToString(pair.Value)))) continue;
                settings[pair.Key] = pair.Value;
            }
            foreach (string key in clearKeys) if (SensitiveSettingsKeys().Contains(key)) settings[key] = "";
            MergeChatProviderProfiles(settings, incoming, previousBank, previousProvider);
            NormalizeLlmProviderSettings(settings);
            NormalizeCodexOptions(settings);
            return clearKeys;
        }

        private static Dictionary<string, object> BuildChatRequestBody(Dictionary<string, object> settings,
            Dictionary<string, object> payload, List<Dictionary<string, object>> messages, string model)
        {
            var body = new Dictionary<string, object> { ["model"] = model, ["messages"] = messages,
                ["temperature"] = ReadDouble(payload, "temperature", ReadDouble(settings, "temperature", 0.7d)),
                ["max_tokens"] = ReadInt(payload, "maxTokens", ReadInt(settings, "maxTokens", 900)), ["stream"] = false };
            foreach (string key in new[] { "response_format", "tools", "tool_choice", "stop", "seed" }) CopyOptional(payload, body, key);
            var extra = ReadDictionary(payload, "extra");
            if (extra != null) foreach (var pair in extra) body[pair.Key] = pair.Value;
            return body;
        }

        private static bool IsOpenRouterApiUrl(string value)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out Uri uri) && uri.Scheme == "https"
                && uri.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);
        }

        private static string LlmApiUrl(Dictionary<string, object> settings)
        {
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            return provider == OpenRouterProvider ? OpenRouterChatUrl
                : provider == NanoGptProvider ? NanoGptChatCompletionsUrl : ReadString(settings, "apiUrl", "");
        }

        private static string LlmApiKey(Dictionary<string, object> settings)
        {
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            return ReadString(settings, provider == OpenRouterProvider ? "openRouterApiKey"
                : provider == NanoGptProvider ? "nanoGptApiKey" : "apiKey", "");
        }

        // Flat active model fields remain the runtime contract. The bank contains only model IDs, never credentials.
        private static Dictionary<string, object> ChatModelProfile(Dictionary<string, object> source, string fallback)
        {
            return ChatModelKeys.ToDictionary(key => key, key => (object)ReadString(source, key, fallback).Trim());
        }

        private static bool NormalizeChatProviderProfiles(Dictionary<string, object> settings)
        {
            string before = Json.Serialize(settings);
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            if (!ReadBool(settings, "chatProviderProfilesInitialized", false))
            {
                string legacyUrl = ReadString(settings, "apiUrl", "");
                if (provider == OpenAiCompatibleProvider && IsNanoGptApiUrl(legacyUrl)) provider = NanoGptProvider;
                if (provider == OpenAiCompatibleProvider && IsOpenRouterApiUrl(legacyUrl)) provider = OpenRouterProvider;
                // Copy the old key only to its proven endpoint owner, including when Codex is currently selected.
                if (!settings.ContainsKey("nanoGptApiKey")) settings["nanoGptApiKey"] = IsNanoGptApiUrl(legacyUrl) ? ReadString(settings, "apiKey", "") : "";
                if (!settings.ContainsKey("openRouterApiKey")) settings["openRouterApiKey"] = IsOpenRouterApiUrl(legacyUrl) ? ReadString(settings, "apiKey", "") : "";
                settings["chatProviderProfilesInitialized"] = true;
            }
            settings["llmProvider"] = provider;
            var prior = ReadDictionary(settings, "llmModelProfiles") ?? new Dictionary<string, object>();
            var bank = new Dictionary<string, object>();
            foreach (string id in ChatProviders)
                bank[id] = ChatModelProfile(ReadDictionary(prior, id), id == OpenRouterProvider ? "openai/gpt-5.5" : id == CodexSubscriptionProvider ? "gpt-5.5" : "zai-org/glm-5.2");
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
                bank[id] = ChatModelProfile(source, id == OpenRouterProvider ? "openai/gpt-5.5" : "gpt-5.5");
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
