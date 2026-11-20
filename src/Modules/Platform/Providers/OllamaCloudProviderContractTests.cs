using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class LlmProviderService
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
            var ui = SettingsService.SettingsForUi(settings);
            check("secret_masking", SettingsService.SensitiveSettingsKeys().Contains("ollamaCloudApiKey")
                && !Json.Serialize(ui).Contains("fixture-ollama") && ReadBool(ui, "ollamaCloudApiKeyPresent", false)
                && SettingsService.IsMaskedSecretValue(ReadString(ui, "ollamaCloudApiKey", "")));
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
            DialogueService.ConfigurePromptCacheRouting(settings, payload, body, OllamaCloudChatUrl, "exact-model:tag", "dialogue");
            check("no_nano_cache_or_probe", !body.ContainsKey("caching") && !body.ContainsKey("stickyprovider") && !DialogueService.SupportsPromptCacheProbe(settings));
            settings["reasoningMode"] = "selective"; settings["reasoningEffort"] = "medium";
            foreach (string type in new[] { "dialogue", "action_router" })
            {
                var wire = TestDict("reasoning", TestDict("effort", "high"), "reasoning_effort", "high");
                var route = DialogueService.ConfigureReasoningRouting(settings, TestDict(), wire, OllamaCloudChatUrl, "exact-model:tag", type);
                check("reasoning_" + type, ReadBool(route, "providerControlsApplied", false)
                    && ReadString(wire, "reasoning_effort", "") == (type == "dialogue" ? "medium" : "none") && !wire.ContainsKey("reasoning"));
            }
            var disabled = TestDict("reasoning_effort", "high");
            DialogueService.ConfigureReasoningRouting(settings, TestDict("reasoningDisabled", true), disabled, OllamaCloudChatUrl, "exact-model:tag", "dialogue");
            check("explicit_reasoning_disabled", ReadString(disabled, "reasoning_effort", "") == "none");
            var banter = TestDict("llmProvider", NanoGptProvider, "banterProvider", OllamaCloudProvider,
                "ollamaCloudApiKey", "fixture-banter", "llmModelProfiles", ReadDictionary(settings, "llmModelProfiles"));
            var banterRoute = SettingsService.BanterProviderSettings(TestDict("requestType", "party_banter"), banter);
            check("banter_uses_saved_ollama", SettingsService.ValidateBanterOptions(banter) == "" && LlmApiUrl(banterRoute) == OllamaCloudChatUrl
                && LlmApiKey(banterRoute) == "fixture-banter" && ReadString(banterRoute, "dialogueModel", "") == ReadString(edits, "dialogueModel", ""));
            string html = Program.ControlCenterHtml();
            check("webui_settings", html.Contains("value='ollama_cloud'>Ollama Cloud") && html.Contains("id='ollamaCloudApiKey' type='password'")
                && html.Contains("https://ollama.com/settings/keys") && html.Contains("https://ollama.com/search?c=cloud"));
            RunOllamaConversationCompatibilityTests(rows);
            return rows;
        }

        internal static Dictionary<string, object> OllamaConversationFixtureResult(Dictionary<string, object> request,
            string content, string provider = OllamaCloudProvider, string reasoning = "")
        {
            BuildChatRequestBody(TestDict("llmProvider", provider), request, ReadDictionaryList(request, "messages"), "fixture-model", out bool contract);
            var message = TestDict("content", content, "reasoning", reasoning);
            var raw = TestDict("choices", new[] { TestDict("message", message, "finish_reason", "stop") });
            return CheckConversationProviderResult(TestDict("ok", true, "finishReason", ExtractFinishReason(raw),
                "content", DialogueService.SanitizeReasoningContent(ExtractAssistantContent(raw))), ReadString(request, "requestType", ""), contract);
        }

        private static void RunOllamaConversationCompatibilityTests(List<Dictionary<string, object>> rows)
        {
            void Check(string id, bool passed) => rows.Add(TestDict("id", "ollama_conversation_" + id, "passed", passed, "summary", id));
            string Wrap(string text) => OllamaSpeechStart + text + OllamaSpeechEnd;
            const string speech = "*She sets down the basket.* Shall we walk to the gate?";
            var request = TestDict("requestType", "social_event", "response_format", TestDict("type", "json_object"));
            var cloud = TestDict("llmProvider", OllamaCloudProvider);
            Dictionary<string, object> Deliver(Dictionary<string, object> result) =>
                DialogueService.GenerateSinglePassConversation(request, TestDict(), "social_event", responder: _ => result);
            bool Accepted(string text) => ReadBool(Deliver(OllamaConversationFixtureResult(request, text)), "ok", false);

            foreach (string mode in new[] { "selective", "off" })
            {
                cloud["reasoningMode"] = mode;
                var history = new List<Dictionary<string, object>> { TestDict("role", "system", "content", "Full response schema."),
                    TestDict("role", "user", "content", new string('x', 180000)), TestDict("role", "assistant", "content", speech),
                    TestDict("role", "user", "content", "Meet me by the gate.") };
                string original = Json.Serialize(history);
                var body = BuildChatRequestBody(cloud, request, history, "fixture-model", out bool contract);
                DialogueService.ConfigurePromptCacheRouting(cloud, request, body, OllamaCloudChatUrl, "fixture-model", "social_event");
                DialogueService.ConfigureReasoningRouting(cloud, request, body, OllamaCloudChatUrl, "fixture-model", "social_event");
                var wire = ReadDictionaryList(body, "messages");
                string last = ReadString(wire.Last(), "content", "");
                Check(mode + "_final_wire_contract", contract && wire.Count == history.Count + 1 && !body.ContainsKey("response_format")
                    && Json.Serialize(wire.Take(history.Count).ToList()) == original && Json.Serialize(history) == original
                    && ReadString(wire.Last(), "role", "") == "system" && last.Contains("{\"reply\"")
                    && last.Contains(OllamaSpeechStart) && last.Contains(OllamaSpeechEnd));
                var capacity = DialogueService.BuildContinuityProviderPreflight(cloud, request, body, OllamaCloudChatUrl, "fixture-model");
                string serialized = Json.Serialize(body);
                Check(mode + "_capacity_uses_complete_wire", ReadString(capacity, "finalSerializedRequestHash", "") == DialogueService.PromptHash(serialized)
                    && ReadInt(capacity, "requestCharacters", 0) == serialized.Length
                    && ReadInt(capacity, "inputTokenByteUpperBound", 0) == System.Text.Encoding.UTF8.GetByteCount(serialized) + 128);
                int calls = 0;
                var delivered = DialogueService.GenerateSinglePassConversation(request, TestDict(), "social_event", out bool speechOnly, responder: _ =>
                {
                    calls++;
                    return CheckConversationProviderResult(TestDict("ok", true, "content", Wrap(speech)), "social_event", contract);
                });
                var parsed = TryParseJsonObject(ReadString(delivered, "content", ""));
                var delivery = ReadDictionary(delivered, "conversationDelivery");
                Check(mode + "_one_call_speech_without_effects", calls == 1 && ReadBool(delivered, "ok", false) && speechOnly
                    && ReadString(parsed, "reply", "") == speech && ReadBool(delivered, "conversationEffectsUnavailable", false)
                    && ReadString(delivery, "outputContract", "") == "ollama_delimited_speech" && ReadInt(delivery, "additionalProviderCalls", -1) == 0
                    && !parsed.ContainsKey("structuredActionDecision") && !parsed.ContainsKey("drinkActions")
                    && !ActionPipelineService.ActionGateShouldPlan(ReadDictionary(parsed, "actionGate")) && ReadDictionary(parsed, "stateUpdates")?.Count == 0
                    && new[] { "suggestedActions", "relationshipAssessments", "memoryWrites", "beliefWrites", "obligationWrites" }.All(key => ReadDictionaryList(parsed, key).Count == 0));
            }

            var fixtures = new (string Name, string Text)[] {
                ("untagged_speech", speech), ("untagged_narration", "*The guard looks up.*"),
                ("untagged_scene", "Rain dampens the market road."), ("untagged_question", "Who sent you?"),
                ("redacted", "[private material omitted]"), ("empty_object", "{}"), ("nested_reply", "{\"private\":{\"reply\":\"hidden\"}}"),
                ("unfinished_json", "{\"reply\":\"unfinished"), ("duplicate_reply", "{\"reply\":\"one\",\"reply\":\"two\"}"),
                ("multiple_json", "{\"reply\":\"one\"} {\"reply\":\"two\"}"), ("empty_speech", Wrap(" ")),
                ("embedded_json", Wrap("{\"reply\":\"hidden\"}")), ("metadata", Wrap("memoryReferences: hidden")),
                ("flat_relationship_metadata", Wrap("relationship_updates: hidden")), ("quoted_intent", Wrap("\"intent\": hidden")),
                ("private_field_alias", Wrap("decision_brief: hidden")), ("reasoning_tag", Wrap("<think>hidden")),
                ("meta_instruction", Wrap("The player wants me to respond.")), ("private_plan", Wrap("I should agree.")),
                ("reserved_notice", Wrap(ConversationResponseService.DialogueActionQueuedNoticePrefix + " invalid]")),
                ("code_fence", Wrap("```json hidden```")), ("oversize", Wrap(new string('x', OllamaSpeechLimit + 1))),
                ("repeated", Wrap(speech) + Wrap(speech)), ("preamble", "Preamble " + Wrap(speech)), ("trailing", Wrap(speech) + " tail"),
                ("unclosed", OllamaSpeechStart + speech), ("nested", Wrap(Wrap(speech))), ("placeholder", Wrap("[private material omitted]")) };
            foreach (var fixture in fixtures) Check("reject_" + fixture.Name, !Accepted(fixture.Text));
            Check("exact_limit", Accepted(Wrap(new string('x', OllamaSpeechLimit))));

            var trusted = OllamaConversationFixtureResult(request, Wrap(speech));
            var forged = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(trusted));
            forged["provider"] = OllamaCloudProvider;
            forged["ollamaConversationOutput"] = true;
            Check("serialized_and_copied_results_have_no_receipt", !ReadBool(Deliver(forged), "ok", true)
                && !ReadBool(Deliver(CloneDictionary(trusted)), "ok", true));
            trusted["ok"] = false;
            Check("transport_error_preserved", !ReadBool(Deliver(trusted), "ok", true));
            var reserved = TestDict("ok", true, "content", Json.Serialize(TestDict("reply", ConversationResponseService.DialogueActionFailureNotice)));
            var rejected = CheckConversationProviderResult(reserved, "social_event", true);
            Check("replacement_fence_keeps_failure", !ReferenceEquals(reserved, rejected) && !ReadBool(Deliver(rejected), "ok", true)
                && !OllamaConversationResults.TryGetValue(reserved, out _) && !OllamaConversationResults.TryGetValue(rejected, out _));
            var decision = TestDict("kind", "none");
            var ordinary = Deliver(OllamaConversationFixtureResult(request, Json.Serialize(TestDict("reply", "Hello.", "structuredActionDecision", decision))));
            var ordinaryJson = TryParseJsonObject(ReadString(ordinary, "content", ""));
            Check("valid_json_preserves_decision", ReadBool(ordinary, "ok", false) && !ReadBool(ordinary, "conversationEffectsUnavailable", true)
                && ReadString(ordinaryJson, "reply", "") == "Hello." && Json.Serialize(ReadDictionary(ordinaryJson, "structuredActionDecision")) == Json.Serialize(decision));
            var cleaned = Deliver(OllamaConversationFixtureResult(request, "<think>private preamble</think>" + Wrap(speech)));
            Check("only_visible_content", ReadString(TryParseJsonObject(ReadString(cleaned, "content", "")), "reply", "") == speech
                && !Accepted("<think>unclosed" + Wrap(speech))
                && !ReadBool(Deliver(OllamaConversationFixtureResult(request, "", reasoning: Wrap(speech))), "ok", true));
            var originalMessages = new List<Dictionary<string, object>> { TestDict("role", "user", "content", "Hello.") };
            foreach (string provider in ChatProviders.Where(id => id != OllamaCloudProvider))
            {
                var body = BuildChatRequestBody(TestDict("llmProvider", provider), request, originalMessages, "fixture-model", out bool contract);
                Check("other_provider_" + provider, !contract && body.ContainsKey("response_format")
                    && Json.Serialize(ReadDictionaryList(body, "messages")) == Json.Serialize(originalMessages)
                    && !ReadBool(Deliver(OllamaConversationFixtureResult(request, Wrap(speech), provider)), "ok", true));
            }
            foreach (string type in new[] { "dialogue", "social_event", "party_chat", "castle_chat" })
                Check("writer_" + type, ReadBool(Deliver(OllamaConversationFixtureResult(TestDict("requestType", type), Wrap(speech))), "ok", false));
            foreach (string type in new[] { "", "diagnostics", "character_construction", "memory", "correspondence", "action_router",
                "npc_approach", "party_banter", "dialogue_integrity_repair", "social_event_continuity_repair", "party_chat_agency_repair" })
            {
                var other = TestDict("requestType", type);
                var body = BuildChatRequestBody(cloud, other, originalMessages, "fixture-model", out bool contract);
                Check("nonwriter_" + (type.Length == 0 ? "unspecified" : type), !contract
                    && Json.Serialize(ReadDictionaryList(body, "messages")) == Json.Serialize(originalMessages)
                    && !ReadBool(Deliver(OllamaConversationFixtureResult(other, Wrap(speech))), "ok", true));
            }
        }
    }
}
