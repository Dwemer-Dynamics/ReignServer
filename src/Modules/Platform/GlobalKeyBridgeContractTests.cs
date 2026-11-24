using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ReignBetaServer
{
    internal static partial class SettingsService
    {
        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint NativeGeteuid();

        // Fake-transport contract for global key references. No real socket, store or provider traffic; vault and stat
        // checks use disposable temp files only.
        internal static List<Dictionary<string, object>> RunGlobalKeyBridgeContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => rows.Add(new Dictionary<string, object> {
                ["id"] = "global_keys_" + id, ["passed"] = passed, ["summary"] = id });
            const string keyId = "11111111-2222-4333-8444-555555555555", other = "99999999-2222-4333-8444-555555555555";
            const string secret = "fixture-global-secret-value", openAi = "https://api.openai.com:443";
            var calls = new List<Dictionary<string, object>>();
            string resolveOutcome = "resolved", writeOutcome = "updated", createdOrigin = NanoGptOrigin, createdProvider = "nano_gpt";
            var store = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["key_id"] = keyId, ["provider"] = "openrouter", ["origin"] = OpenRouterOrigin, ["label"] = "Reign", ["preview"] = "...abcd", ["revision"] = 3, ["configured"] = true },
                new Dictionary<string, object> { ["key_id"] = other, ["provider"] = "nano_gpt", ["origin"] = NanoGptOrigin, ["label"] = "Other", ["preview"] = "...zz", ["revision"] = 1, ["configured"] = true } };
            var catalog = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["provider"] = "openai", ["origin"] = openAi },
                new Dictionary<string, object> { ["provider"] = "custom_openai", ["origin"] = null } };
            var previous = GlobalKeyTransportOverride;
            bool previousAutoSelection = GlobalKeyAutoSelectionOffForTests;
            GlobalKeyAutoSelectionOffForTests = false;
            InvalidateGlobalKeyStatusCache();
            GlobalKeyTransportOverride = request =>
            {
                calls.Add(request);
                string op = ReadString(request, "op", "");
                if (op == "status") return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "listed", ["keys"] = store, ["providers"] = catalog };
                if (op == "resolve") return resolveOutcome == "resolved"
                    ? new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "resolved", ["secret"] = secret, ["revision"] = 3 } : GlobalKeyFail(resolveOutcome);
                bool ok = writeOutcome == "updated" || writeOutcome == "created" || writeOutcome == "unchanged";
                return ok ? new Dictionary<string, object> { ["ok"] = true, ["outcome"] = writeOutcome, ["key"] = op == "create"
                        ? new Dictionary<string, object> { ["key_id"] = other, ["provider"] = createdProvider, ["origin"] = createdOrigin }
                        : new Dictionary<string, object> { ["key_id"] = keyId } }
                    : GlobalKeyFail(writeOutcome);
            };
            string root = Path.Combine(Path.GetTempPath(), "reign-global-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                // Named Ollama migration carries an opaque reference once, without
                // depending on the store being available or resolving a secret.
                string ollamaOrigin = NormalizedOrigin(LlmProviderService.OllamaCloudChatUrl);
                Func<Dictionary<string, object>> legacyOllama = () => TestDict("llmProvider", "openai_compatible",
                    "apiUrl", LlmProviderService.OllamaCloudChatUrl, "apiKey", "shadow-local",
                    "globalKeyBindings", TestDict("apiKey", TestDict("keyId", keyId, "provider", "custom_openai", "origin", ollamaOrigin)));
                var migration = legacyOllama();
                LlmProviderService.NormalizeChatProviderProfiles(migration);
                check("ollama_migration_reference_only_offline", BoundKeyId(migration, "ollamaCloudApiKey") == keyId
                    && BoundKeyId(migration, "apiKey") == keyId && ReadString(migration, "ollamaCloudApiKey", "missing") == ""
                    && calls.Count == 0);
                var migrationBindings = ReadDictionary(migration, "globalKeyBindings");
                migrationBindings.Remove("ollamaCloudApiKey");
                LlmProviderService.NormalizeChatProviderProfiles(migration);
                check("ollama_migration_not_replayed_after_unbind", !HasGlobalKeyBindingEntry(migration, "ollamaCloudApiKey") && calls.Count == 0);
                foreach (string defect in new[] { "origin", "provider", "keyId" })
                {
                    var bad = legacyOllama();
                    ReadDictionary(ReadDictionary(bad, "globalKeyBindings"), "apiKey")[defect] = "invalid";
                    LlmProviderService.NormalizeChatProviderProfiles(bad);
                    check("ollama_invalid_legacy_" + defect + "_no_plaintext_copy", !HasGlobalKeyBindingEntry(bad, "ollamaCloudApiKey")
                        && ReadString(bad, "ollamaCloudApiKey", "missing") == "" && calls.Count == 0);
                }
                var dedicated = legacyOllama();
                dedicated["ollamaCloudApiKey"] = "existing-dedicated";
                LlmProviderService.NormalizeChatProviderProfiles(dedicated);
                check("ollama_migration_preserves_dedicated_value", ReadString(dedicated, "ollamaCloudApiKey", "") == "existing-dedicated"
                    && !HasGlobalKeyBindingEntry(dedicated, "ollamaCloudApiKey"));
                var malformed = legacyOllama();
                ReadDictionary(malformed, "globalKeyBindings")["ollamaCloudApiKey"] = TestDict("keyId", "bad", "provider", "custom_openai", "origin", ollamaOrigin);
                malformed["ollamaCloudApiKey"] = "shadow-local";
                LlmProviderService.NormalizeChatProviderProfiles(malformed);
                // A pre-upgrade shadow beside a binding is cleared once on load; later Reign-only keys are kept.
                SanitizeBoundKeySlots(malformed);
                check("ollama_malformed_binding_blocks_fallback", ReadString(malformed, "ollamaCloudApiKey", "x") == ""
                    && ProviderSecret(malformed, "ollamaCloudApiKey", LlmProviderService.OllamaCloudChatUrl) == ""
                    && ProviderKeyUnavailable(malformed, "ollamaCloudApiKey", "") && calls.Count == 0);
                malformed["ollamaCloudApiKey"] = "fixture-reign-only";
                SanitizeBoundKeySlots(malformed);
                check("bound_shadow_cleanup_runs_once", ReadString(malformed, "ollamaCloudApiKey", "") == "fixture-reign-only"
                    && ReadBool(ReadDictionary(malformed, "globalKeyMigrations"), BoundShadowCleanupMarker, false));
                var markerPatch = TestDict("globalKeyMigrations", TestDict("ollamaCloudFromApiKey", false), "legacyMemoryMode", true);
                var filteredPatch = WithoutGlobalKeyMigrationPatch(markerPatch);
                check("migration_marker_server_owned_patch_clone", !filteredPatch.ContainsKey("globalKeyMigrations")
                    && markerPatch.ContainsKey("globalKeyMigrations") && ReadBool(filteredPatch, "legacyMemoryMode", false));
                var boundOllama = legacyOllama(); LlmProviderService.NormalizeChatProviderProfiles(boundOllama);
                bool namedBinding;
                string namedKey = LlmProviderService.ResolveLlmRequestApiKey(boundOllama, TestDict("apiUrl", "https://attacker.invalid/", "apiKey", "payload-secret"), out namedBinding);
                check("ollama_named_request_resolves_once_canonical", namedKey == secret && namedBinding && calls.Count == 1
                    && ReadString(ReadDictionary(calls[0], "body"), "origin", "") == ollamaOrigin);
                calls.Clear();
                boundOllama["llmProvider"] = LlmProviderService.CodexSubscriptionProvider;
                check("subscription_never_resolves_global_key", LlmProviderService.ResolveLlmRequestApiKey(boundOllama, null, out namedBinding) == "" && !namedBinding && calls.Count == 0);
                var customOverride = legacyOllama();
                check("custom_explicit_key_does_not_resolve_stored_binding", LlmProviderService.ResolveLlmRequestApiKey(customOverride,
                    TestDict("apiKey", "explicit-fixture", "apiUrl", "https://fixture.invalid/"), out namedBinding) == "explicit-fixture"
                    && !namedBinding && calls.Count == 0);
                boundOllama["llmProvider"] = LlmProviderService.OllamaCloudProvider;
                ReadDictionary(ReadDictionary(boundOllama, "globalKeyBindings"), "ollamaCloudApiKey")["provider"] = "openrouter";
                check("named_slot_wrong_provider_refused_locally", LlmProviderService.ResolveLlmRequestApiKey(boundOllama, null, out namedBinding) == "" && namedBinding && calls.Count == 0);

                var binding = new Dictionary<string, object> { ["keyId"] = keyId, ["provider"] = "openrouter", ["origin"] = OpenRouterOrigin };
                var settings = new Dictionary<string, object> { ["llmProvider"] = "openrouter", ["openRouterApiKey"] = "",
                    ["nanoGptApiKey"] = "fixture-local", ["globalKeyBindings"] = new Dictionary<string, object> { ["openRouterApiKey"] = binding } };

                check("unbound_slot_keeps_local_value", ProviderSecret(settings, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == "fixture-local" && calls.Count == 0);
                bool read = LlmProviderService.LlmApiKey(settings) == secret && ProviderSecret(settings, "openRouterApiKey", "https://openrouter.ai/api/v1/images") == secret;
                var resolves = calls.Where(c => ReadString(c, "op", "") == "resolve").ToList();
                check("bound_slot_reads_current_value_every_use", read && resolves.Count == 2
                    && resolves.All(c => ReadString(ReadDictionary(c, "body"), "key_id", "") == keyId && ReadString(ReadDictionary(c, "body"), "origin", "") == OpenRouterOrigin));
                calls.Clear();
                check("origin_mismatch_fails_closed", ProviderSecret(settings, "openRouterApiKey", "https://openrouter.ai.attacker.invalid/v1") == ""
                    && ProviderSecret(settings, "openRouterApiKey", LlmProviderService.OpenRouterChatUrl, "https://other.invalid/") == "" && calls.Count == 0
                    && ProviderKeyUnavailable(settings, "openRouterApiKey", "") && !ProviderKeyUnavailable(settings, "nanoGptApiKey", ""));
                foreach (string outcome in new[] { "changed", "missing", "wrong_provider", "unavailable", "not_enrolled" })
                {
                    resolveOutcome = outcome;
                    check("resolve_" + outcome + "_has_no_fallback", ProviderSecret(settings, "openRouterApiKey", LlmProviderService.OpenRouterChatUrl) == "");
                }

                // A bound key that cannot be resolved stops the LLM request before any provider dispatch.
                resolveOutcome = "unavailable";
                var customBound = new Dictionary<string, object> { ["llmProvider"] = "openai_compatible", ["apiUrl"] = "https://custom.invalid/v1/chat/completions",
                    ["globalKeyBindings"] = new Dictionary<string, object> { ["apiKey"] = new Dictionary<string, object> { ["keyId"] = keyId, ["provider"] = "custom_openai", ["origin"] = "https://custom.invalid:443" } } };
                var refused = LlmProviderService.ChatWithLlm(new Dictionary<string, object> { ["requestType"] = "dialogue", ["singleProviderAttempt"] = true }, null, null, customBound);
                var redirected = LlmProviderService.ChatWithLlm(new Dictionary<string, object> { ["apiUrl"] = "https://elsewhere.invalid/v1/chat/completions", ["singleProviderAttempt"] = true }, null, null, customBound);
                check("bound_key_unavailable_fails_closed_before_dispatch", ReadString(refused, "errorCode", "") == "global_key_unavailable"
                    && ReadInt(refused, "providerCallCount", -1) == 0 && ReadString(redirected, "errorCode", "") == "global_key_unavailable");
                resolveOutcome = "resolved";
                calls.Clear();
                InvalidateGlobalKeyStatusCache();
                calls.Clear();
                SettingsFingerprint(TestDict("llmProvider", LlmProviderService.NanoGptProvider, "nanoGptApiKey", ""));
                SettingsFingerprint(settings);
                SettingsForUi(settings);
                check("diagnostics_never_resolve", calls.Count == 0);

                // Layered resolution: Reign-only key, then binding, then the single matching global key, then nothing.
                Func<string, int> resolvesOf = id => calls.Count(c => ReadString(c, "op", "") == "resolve" && ReadString(ReadDictionary(c, "body"), "key_id", "") == id);
                var layered = TestDict("llmProvider", "openrouter", "openRouterApiKey", "fixture-reign-only",
                    "globalKeyBindings", TestDict("openRouterApiKey", binding));
                calls.Clear();
                check("reign_only_key_overrides_binding", ProviderSecret(layered, "openRouterApiKey", LlmProviderService.OpenRouterChatUrl) == "fixture-reign-only"
                    && !ProviderKeyUnavailable(layered, "openRouterApiKey", "fixture-reign-only") && calls.Count == 0);
                layered["openRouterApiKey"] = "";
                check("cleared_reign_only_key_falls_back_to_binding", ProviderSecret(layered, "openRouterApiKey", LlmProviderService.OpenRouterChatUrl) == secret
                    && resolvesOf(keyId) == 1);
                var auto = TestDict("llmProvider", LlmProviderService.NanoGptProvider, "nanoGptApiKey", "fixture-reign-only-nano");
                calls.Clear();
                bool localFirst = ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == "fixture-reign-only-nano" && calls.Count == 0;
                auto["nanoGptApiKey"] = "";
                check("auto_picks_single_matching_key", localFirst && ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == secret
                    && resolvesOf(other) == 1 && !HasGlobalKeyBindingEntry(auto, "nanoGptApiKey")
                    && ProviderKeyConfigured(auto, "nanoGptApiKey") && !ProviderKeyConfigured(auto, "nanoGptApiKey", false));
                calls.Clear();
                check("auto_destination_origin_mismatch_no_auto", ProviderSecret(auto, "nanoGptApiKey", "https://nano-gpt.com.attacker.invalid/v1") == ""
                    && ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl, "https://other.invalid/") == ""
                    && !ProviderKeyUnavailable(auto, "nanoGptApiKey", "") && calls.Count == 0);
                check("auto_zero_matches_no_key", ProviderSecret(TestDict(), "portraitAtlasApiKey", "https://api.atlascloud.ai/api/v1/model/generateImage") == ""
                    && !ProviderKeyUnavailable(TestDict(), "portraitAtlasApiKey", "") && !calls.Any(c => ReadString(c, "op", "") == "resolve"));
                const string secondNano = "44444444-2222-4333-8444-555555555555";
                store.Add(new Dictionary<string, object> { ["key_id"] = secondNano, ["provider"] = "nano_gpt", ["origin"] = NanoGptOrigin, ["label"] = "Second", ["preview"] = "...yy", ["revision"] = 1, ["configured"] = true });
                InvalidateGlobalKeyStatusCache();
                calls.Clear();
                check("auto_two_matches_never_guesses", ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == ""
                    && !ProviderKeyUnavailable(auto, "nanoGptApiKey", "") && !ProviderKeyConfigured(auto, "nanoGptApiKey")
                    && !calls.Any(c => ReadString(c, "op", "") == "resolve")
                    && ReadString(GlobalKeyRow(auto, "nanoGptApiKey", GlobalKeyTarget(auto, "nanoGptApiKey", catalog), store), "source", "") == "none");
                store.RemoveAt(store.Count - 1);
                InvalidateGlobalKeyStatusCache();
                resolveOutcome = "unavailable";
                bool autoBinding;
                string autoKey = LlmProviderService.ResolveLlmRequestApiKey(auto, null, out autoBinding);
                bool autoUnavailable = ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == ""
                    && ProviderKeyUnavailable(auto, "nanoGptApiKey", "");
                var autoRefused = LlmProviderService.ChatWithLlm(new Dictionary<string, object> { ["requestType"] = "dialogue", ["singleProviderAttempt"] = true }, null, null, auto);
                check("auto_failed_resolve_is_unavailable", autoKey == "" && autoBinding && autoUnavailable
                    && ReadString(autoRefused, "errorCode", "") == "global_key_unavailable" && ReadInt(autoRefused, "providerCallCount", -1) == 0);
                resolveOutcome = "resolved";
                InvalidateGlobalKeyStatusCache();
                calls.Clear();
                Func<int> statusCalls = () => calls.Count(c => ReadString(c, "op", "") == "status");
                bool gated = ProviderKeyConfigured(auto, "nanoGptApiKey") && ProviderKeyConfigured(auto, "nanoGptApiKey");
                bool resolvedOnce = ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == secret
                    && ProviderSecret(auto, "nanoGptApiKey", PortraitsService.NanoGptChatCompletionsUrl) == secret;
                int warmStatus = statusCalls();
                InvalidateGlobalKeyStatusCache();
                ProviderKeyConfigured(auto, "nanoGptApiKey");
                check("status_metadata_cached_and_invalidated", gated && resolvedOnce && warmStatus == 1 && resolvesOf(other) == 2 && statusCalls() == 2
                    && !Json.Serialize(CachedGlobalKeyStatus()).Contains(secret));
                var autoRow = GlobalKeyRow(auto, "nanoGptApiKey", GlobalKeyTarget(auto, "nanoGptApiKey", catalog), store);
                var layeredRow = GlobalKeyRow(TestDict("openRouterApiKey", "fixture-reign-only", "globalKeyBindings", TestDict("openRouterApiKey", binding)),
                    "openRouterApiKey", GlobalKeyTarget(layered, "openRouterApiKey", catalog), store);
                check("status_row_reports_source", ReadString(autoRow, "source", "") == "global_auto" && ReadInt(autoRow, "autoCandidates", 0) == 1
                    && ReadString(ReadDictionary(autoRow, "autoKey"), "label", "") == "Other" && ReadString(layeredRow, "source", "") == "reign_only"
                    && ReadString(GlobalKeyRow(layered, "openRouterApiKey", GlobalKeyTarget(layered, "openRouterApiKey", catalog), store), "source", "") == "global_bound"
                    && !Json.Serialize(autoRow).Contains(secret) && !Json.Serialize(layeredRow).Contains("fixture-reign-only"));

                var custom = new Dictionary<string, object> { ["apiUrl"] = "https://Custom.invalid:8443/v1/chat" };
                check("custom_endpoint_exact_origin", GlobalKeyTarget(custom, "apiKey", catalog) == ("custom_openai", "https://custom.invalid:8443")
                    && GlobalKeyTarget(new Dictionary<string, object> { ["apiUrl"] = "https://api.openai.com/v1/chat/completions" }, "apiKey", catalog) == ("openai", openAi)
                    && GlobalKeyTarget(new Dictionary<string, object> { ["apiUrl"] = "https://api.openai.com:8443/v1" }, "apiKey", catalog).Provider == "custom_openai"
                    && GlobalKeyTarget(custom, "qdrantApiKey").Provider == null && GlobalKeyTarget(custom, "portraitAtlasApiKey").Origin == AtlasOrigin);

                // IPv6 literals keep the store's bracketed form; malformed or scoped origins are refused.
                const string loopback6 = "http://[::1]:1234";
                var v6 = new Dictionary<string, object> { ["apiUrl"] = loopback6 + "/v1", ["globalKeyBindings"] = new Dictionary<string, object> {
                    ["apiKey"] = new Dictionary<string, object> { ["keyId"] = keyId, ["provider"] = "custom_openai", ["origin"] = loopback6 } } };
                resolveOutcome = "resolved";
                calls.Clear();
                check("ipv6_origin_bracketed", NormalizedOrigin("http://[::1]:1234") == loopback6 && NormalizedOrigin("http://[::1]/v1") == "http://[::1]:80"
                    && GlobalKeyTarget(v6, "apiKey", catalog) == ("custom_openai", loopback6)
                    && ProviderSecret(v6, "apiKey", loopback6 + "/v1/chat/completions") == secret
                    && ReadString(ReadDictionary(calls.Last(), "body"), "origin", "") == loopback6
                    && new[] { "http://[::1", "http://[fe80::1%25eth0]:1234", "ftp://[::1]:1", "http://u@[::1]:1", "http://::1:1234" }.All(bad => NormalizedOrigin(bad) == null));

                var merged = new Dictionary<string, object>(settings);
                LlmProviderService.MergeProviderSettings(merged, new Dictionary<string, object> { ["globalKeyBindings"] = new Dictionary<string, object> {
                    ["nanoGptApiKey"] = new Dictionary<string, object> { ["keyId"] = other, ["provider"] = "nano_gpt", ["origin"] = NanoGptOrigin } } });
                check("generic_settings_cannot_bind", GlobalKeyBinding(merged, "nanoGptApiKey") == null && GlobalKeyBinding(merged, "openRouterApiKey") != null);

                var ui = SettingsForUi(settings);
                check("ui_and_diagnostics_hold_no_secret", !Json.Serialize(ui).Contains(secret) && ReadBool(ui, "openRouterApiKeyBound", false)
                    && ProviderKeyConfigured(settings, "openRouterApiKey") && !Json.Serialize(settings).Contains(secret));

                var bound = new List<string>();
                bool bindResult = true;
                var clearsReignOnly = new List<bool>();
                Func<string, Dictionary<string, object>, string, string, bool, bool> bind = (slot, b, expected, origin, clearLocal) =>
                { bound.Add(slot + "=" + ReadString(b, "keyId", "") + "<" + expected); clearsReignOnly.Add(clearLocal); return bindResult; };
                Func<Dictionary<string, object>, Dictionary<string, object>> save = payload => SaveGlobalKey(payload, bind, () => settings);
                writeOutcome = "updated";
                var rotate = new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "rotate", ["keyId"] = keyId, ["revision"] = 3, ["label"] = "Reign",
                    ["secret"] = "fixture-new", ["expectedKeyId"] = keyId };
                var rotated = save(rotate);
                var edit = ReadDictionary(calls.Last(), "body");
                check("rotate_same_uuid_with_revision", ReadBool(rotated, "ok", false) && ReadString(edit, "key_id", "") == keyId && ReadInt(edit, "revision", 0) == 3
                    && bound.SequenceEqual(new[] { "openRouterApiKey=" + keyId + "<" + keyId }) && !rotate.ContainsKey("secret") && !Json.Serialize(rotated).Contains("fixture-new"));
                clearsReignOnly.Clear();
                save(new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "rotate", ["keyId"] = keyId, ["revision"] = 3,
                    ["label"] = "Renamed", ["secret"] = "", ["expectedKeyId"] = keyId });
                save(new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "rotate", ["keyId"] = keyId, ["revision"] = 3,
                    ["label"] = "Renamed", ["secret"] = "************", ["expectedKeyId"] = keyId });
                save(new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "rotate", ["keyId"] = keyId, ["revision"] = 3,
                    ["label"] = "Reign", ["secret"] = "fixture-new", ["expectedKeyId"] = keyId });
                save(new Dictionary<string, object> { ["slot"] = "portraitNanoGptApiKey", ["mode"] = "bind", ["keyId"] = other, ["expectedKeyId"] = "" });
                var renamed = TestDict("openRouterApiKey", "fixture-kept", "globalKeyBindings", TestDict("openRouterApiKey", binding));
                ApplyGlobalKeyBinding(renamed, "openRouterApiKey", binding, false);
                bool renameKeeps = ReadString(renamed, "openRouterApiKey", "") == "fixture-kept";
                ApplyGlobalKeyBinding(renamed, "openRouterApiKey", null, true);
                bool unbindKeeps = ReadString(renamed, "openRouterApiKey", "") == "fixture-kept" && !HasGlobalKeyBindingEntry(renamed, "openRouterApiKey");
                ApplyGlobalKeyBinding(renamed, "openRouterApiKey", binding, true);
                check("rename_keeps_reign_only_key_bind_clears_it", clearsReignOnly.SequenceEqual(new[] { false, false, true, true })
                    && renameKeeps && unbindKeeps && ReadString(renamed, "openRouterApiKey", "x") == "" && BoundKeyId(renamed, "openRouterApiKey") == keyId);
                bound.Clear();
                check("rotate_other_uuid_refused", ReadString(save(new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "rotate", ["keyId"] = other,
                    ["revision"] = 1, ["secret"] = "x", ["expectedKeyId"] = keyId }), "outcome", "") == "conflict");
                int beforeStale = calls.Count;
                check("stale_binding_snapshot_refused_before_any_call", ReadString(save(new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "bind",
                    ["keyId"] = keyId, ["expectedKeyId"] = "" }), "outcome", "") == "conflict"
                    && ReadString(save(new Dictionary<string, object> { ["slot"] = "nanoGptApiKey", ["mode"] = "create", ["secret"] = "x" }), "outcome", "") == "conflict"
                    && calls.Count == beforeStale);
                writeOutcome = "created";
                bound.Clear();
                var created = save(new Dictionary<string, object> { ["slot"] = "nanoGptApiKey", ["mode"] = "create", ["secret"] = "fixture-nano-new", ["expectedKeyId"] = "" });
                var create = ReadDictionary(calls.Last(), "body");
                check("create_uses_server_provider_and_binds", ReadBool(created, "ok", false) && ReadString(create, "provider", "") == "nano_gpt"
                    && ReadString(create, "label", "") == "Reign - NanoGPT text" && bound.SequenceEqual(new[] { "nanoGptApiKey=" + other + "<" }));
                bound.Clear();
                createdOrigin = "https://other.invalid:443";
                check("created_key_with_other_metadata_is_never_bound", ReadString(save(new Dictionary<string, object> { ["slot"] = "nanoGptApiKey", ["mode"] = "create",
                    ["secret"] = "fixture-nano-new", ["expectedKeyId"] = "" }), "outcome", "") == "unknown" && bound.Count == 0);
                createdOrigin = NanoGptOrigin;
                foreach (string outcome in new[] { "unknown", "conflict", "limit", "busy", "unavailable" })
                {
                    writeOutcome = outcome;
                    bound.Clear();
                    int before = calls.Count(c => ReadString(c, "op", "") == "create");
                    var result = save(new Dictionary<string, object> { ["slot"] = "nanoGptApiKey", ["mode"] = "create", ["secret"] = "fixture-nano-new", ["expectedKeyId"] = "" });
                    check("write_" + outcome + "_changes_nothing_and_never_retries", !ReadBool(result, "ok", false) && ReadString(result, "outcome", "") == outcome
                        && bound.Count == 0 && calls.Count(c => ReadString(c, "op", "") == "create") - before == 1);
                }
                bound.Clear();
                check("bind_requires_matching_provider", ReadString(save(new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "bind", ["keyId"] = other, ["expectedKeyId"] = keyId }), "outcome", "") == "wrong_provider"
                    && ReadBool(save(new Dictionary<string, object> { ["slot"] = "portraitNanoGptApiKey", ["mode"] = "bind", ["keyId"] = other, ["expectedKeyId"] = "" }), "ok", false)
                    && bound.SequenceEqual(new[] { "portraitNanoGptApiKey=" + other + "<" }));
                bound.Clear();
                check("masked_or_blank_create_refused", ReadString(save(new Dictionary<string, object> { ["slot"] = "apiKey", ["mode"] = "create", ["secret"] = "x", ["expectedKeyId"] = "" }), "outcome", "") == "invalid_endpoint"
                    && ReadString(save(new Dictionary<string, object> { ["slot"] = "nanoGptApiKey", ["mode"] = "create", ["secret"] = "************", ["expectedKeyId"] = "" }), "outcome", "") == "invalid" && bound.Count == 0);
                // Custom endpoint: the key follows the endpoint the caller sees. An unsaved or stale URL is refused before any
                // store call; rotation never edits a key filed under another origin.
                const string oldUrl = "https://old.invalid/v1/chat/completions", newOrigin = "https://new.invalid:443", customId = "33333333-2222-4333-8444-555555555555";
                store.Add(new Dictionary<string, object> { ["key_id"] = customId, ["provider"] = "custom_openai", ["origin"] = "https://old.invalid:443", ["label"] = "Old", ["revision"] = 1 });
                var newEndpoint = new Dictionary<string, object> { ["llmProvider"] = "openai_compatible", ["apiUrl"] = "https://new.invalid/v1/chat/completions" };
                Func<Dictionary<string, object>, Dictionary<string, object>, Dictionary<string, object>> saveAt = (s, payload) => SaveGlobalKey(payload, bind, () => s);
                writeOutcome = "created";
                bound.Clear();
                int beforeEndpoint = calls.Count;
                check("custom_key_for_unsaved_or_stale_endpoint_refused_before_any_call",
                    ReadString(saveAt(newEndpoint, new Dictionary<string, object> { ["slot"] = "apiKey", ["mode"] = "create", ["secret"] = "x-new", ["expectedKeyId"] = "", ["expectedApiUrl"] = oldUrl }), "outcome", "") == "endpoint_changed"
                    && ReadString(saveAt(new Dictionary<string, object> { ["apiUrl"] = oldUrl }, new Dictionary<string, object> { ["slot"] = "apiKey", ["mode"] = "create", ["secret"] = "x-new",
                        ["expectedKeyId"] = "", ["expectedApiUrl"] = "https://new.invalid/v1" }), "outcome", "") == "endpoint_changed"
                    && ReadString(saveAt(newEndpoint, new Dictionary<string, object> { ["slot"] = "apiKey", ["mode"] = "create", ["secret"] = "x-new", ["expectedKeyId"] = "" }), "outcome", "") == "endpoint_changed"
                    && calls.Count == beforeEndpoint && bound.Count == 0);
                createdProvider = "custom_openai";
                createdOrigin = newOrigin;
                var customCreated = saveAt(newEndpoint, new Dictionary<string, object> { ["slot"] = "apiKey", ["mode"] = "create", ["secret"] = "x-new", ["expectedKeyId"] = "",
                    ["expectedApiUrl"] = "https://NEW.invalid/other" });
                var customCreate = ReadDictionary(calls.Last(), "body");
                check("custom_key_for_saved_endpoint_created_and_bound", ReadBool(customCreated, "ok", false) && ReadString(customCreate, "provider", "") == "custom_openai"
                    && ReadString(customCreate, "origin", "") == newOrigin && bound.SequenceEqual(new[] { "apiKey=" + other + "<" }));
                createdProvider = "nano_gpt";
                createdOrigin = NanoGptOrigin;
                writeOutcome = "updated";
                bound.Clear();
                Func<string, Dictionary<string, object>> boundTo = origin => new Dictionary<string, object>(newEndpoint) { ["globalKeyBindings"] = new Dictionary<string, object> {
                    ["apiKey"] = new Dictionary<string, object> { ["keyId"] = customId, ["provider"] = "custom_openai", ["origin"] = origin } } };
                Func<Dictionary<string, object>> rotateCustom = () => new Dictionary<string, object> { ["slot"] = "apiKey", ["mode"] = "rotate", ["keyId"] = customId, ["revision"] = 1,
                    ["secret"] = "x-new", ["expectedKeyId"] = customId, ["expectedApiUrl"] = newEndpoint["apiUrl"] };
                int edits = calls.Count(c => ReadString(c, "op", "") == "edit");
                check("custom_rotation_never_edits_other_origin_key", ReadString(saveAt(boundTo("https://old.invalid:443"), rotateCustom()), "outcome", "") == "conflict"
                    && ReadString(saveAt(boundTo(newOrigin), rotateCustom()), "outcome", "") == "wrong_provider"
                    && calls.Count(c => ReadString(c, "op", "") == "edit") == edits && bound.Count == 0);
                store.RemoveAt(store.Count - 1);
                writeOutcome = "created";
                bindResult = false;
                int creates = calls.Count(c => ReadString(c, "op", "") == "create");
                var orphan = save(new Dictionary<string, object> { ["slot"] = "nanoGptApiKey", ["mode"] = "create", ["secret"] = "x-key", ["expectedKeyId"] = "" });
                check("local_conflict_after_global_save_is_saved_not_bound_without_retry", ReadString(orphan, "outcome", "") == "saved_not_bound"
                    && ReadString(orphan, "keyId", "") == other && calls.Count(c => ReadString(c, "op", "") == "create") - creates == 1);
                bindResult = true;

                // Vault persistence with a disposable file: a bound slot never keeps or recovers a value; unrelated keys survive.
                Directory.CreateDirectory(root);
                string vault = Path.Combine(root, "settings-secrets.dpapi");
                WriteSettingsSecretsVault(vault, new Dictionary<string, object> { ["openRouterApiKey"] = "old-local-router", ["qdrantApiKey"] = "qdrant-local" });
                var reloaded = new Dictionary<string, object> { ["openRouterApiKey"] = "", ["qdrantApiKey"] = "", ["globalKeyBindings"] = settings["globalKeyBindings"] };
                RecoverSettingsSecretsFromVault(vault, reloaded, false, out _);
                var fullRestore = new Dictionary<string, object> { ["globalKeyBindings"] = settings["globalKeyBindings"] };
                RecoverSettingsSecretsFromVault(vault, fullRestore, true, out _);
                WriteSettingsSecretsVault(vault, new Dictionary<string, object> { ["openRouterApiKey"] = "leaked", ["globalKeyBindings"] = settings["globalKeyBindings"] });
                var afterWrite = new Dictionary<string, object>();
                RecoverSettingsSecretsFromVault(vault, afterWrite, true, out _);
                check("vault_never_resurrects_or_stores_bound_slot", ReadString(reloaded, "openRouterApiKey", "x") == "" && ReadString(reloaded, "qdrantApiKey", "") == "qdrant-local"
                    && ReadString(fullRestore, "openRouterApiKey", "") == "" && ReadString(afterWrite, "openRouterApiKey", "x") == ""
                    && !File.ReadAllText(vault).Contains("leaked"));
                // After the one-time cleanup, a Reign-only key saved beside a binding is persisted to the vault and restored
                // into that same bound slot (partial and full recovery); a vault value without that binding never is (above).
                WriteSettingsSecretsVault(vault, new Dictionary<string, object> { ["openRouterApiKey"] = "fixture-vault-reign-only",
                    ["globalKeyBindings"] = settings["globalKeyBindings"], ["globalKeyMigrations"] = TestDict(BoundShadowCleanupMarker, true) });
                var reignOnlyRestore = new Dictionary<string, object> { ["openRouterApiKey"] = "", ["globalKeyBindings"] = settings["globalKeyBindings"] };
                RecoverSettingsSecretsFromVault(vault, reignOnlyRestore, false, out _);
                var reignOnlyFull = new Dictionary<string, object> { ["globalKeyBindings"] = settings["globalKeyBindings"] };
                RecoverSettingsSecretsFromVault(vault, reignOnlyFull, true, out _);
                var otherBinding = new Dictionary<string, object> { ["openRouterApiKey"] = "", ["globalKeyBindings"] = TestDict("openRouterApiKey",
                    TestDict("keyId", other, "provider", "openrouter", "origin", OpenRouterOrigin)) };
                RecoverSettingsSecretsFromVault(vault, otherBinding, false, out _);
                check("vault_keeps_reign_only_key_on_bound_slot", ReadString(reignOnlyRestore, "openRouterApiKey", "") == "fixture-vault-reign-only"
                    && BoundKeyId(reignOnlyRestore, "openRouterApiKey") == keyId
                    && ReadString(reignOnlyFull, "openRouterApiKey", "") == "fixture-vault-reign-only"
                    && ReadString(otherBinding, "openRouterApiKey", "x") == "");

                // Saving and clearing through the real settings paths, against a disposable settings file.
                string settingsFile = Path.Combine(root, "settings.json");
                Func<Dictionary<string, object>> readFile = () => Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(settingsFile));
                var routerBinding = TestDict("openRouterApiKey", binding);
                CampaignFileService.WriteJsonObject(settingsFile, TestDict("openRouterApiKey", "stale-shadow", "globalKeyBindings", routerBinding));
                var unrelatedSave = SaveSettingsCore(TestDict("siteUrl", "https://fixture.invalid/"), out _, out _, settingsFile);
                var afterUnrelated = readFile();
                check("save_clears_preupgrade_shadow_once", ReadBool(unrelatedSave, "ok", false) && ReadString(afterUnrelated, "openRouterApiKey", "x") == ""
                    && ReadBool(ReadDictionary(afterUnrelated, "globalKeyMigrations"), BoundShadowCleanupMarker, false)
                    && BoundKeyId(afterUnrelated, "openRouterApiKey") == keyId);
                CampaignFileService.WriteJsonObject(settingsFile, TestDict("openRouterApiKey", "stale-shadow", "globalKeyBindings", routerBinding));
                var reignOnlySave = SaveSettingsCore(TestDict("openRouterApiKey", "fixture-file-reign-only"), out _, out _, settingsFile);
                var reloadedFile = readFile();
                SanitizeBoundKeySlots(reloadedFile);
                calls.Clear();
                check("reign_only_key_saved_on_bound_slot_survives_reload", ReadBool(reignOnlySave, "ok", false)
                    && ReadString(reloadedFile, "openRouterApiKey", "") == "fixture-file-reign-only" && BoundKeyId(reloadedFile, "openRouterApiKey") == keyId
                    && ProviderSecret(reloadedFile, "openRouterApiKey", LlmProviderService.OpenRouterChatUrl) == "fixture-file-reign-only" && calls.Count == 0
                    && !Json.Serialize(reignOnlySave).Contains("fixture-file-reign-only"));
                var localClear = ClearSettingsKey(TestDict("key", "openRouterApiKey"), null, settingsFile);
                var afterClear = readFile();
                var guardedUnbind = ClearSettingsKey(TestDict("key", "openRouterApiKey"), null, settingsFile);
                var nothingToClear = ClearSettingsKey(TestDict("key", "nanoGptApiKey"), null, settingsFile);
                check("clear_on_bound_slot_clears_reign_only_key_and_keeps_binding", ReadString(localClear, "outcome", "") == "local_cleared"
                    && ReadString(afterClear, "openRouterApiKey", "x") == "" && BoundKeyId(afterClear, "openRouterApiKey") == keyId
                    && ReadString(guardedUnbind, "outcome", "") == "forbidden" && BoundKeyId(readFile(), "openRouterApiKey") == keyId
                    && ReadBool(nothingToClear, "ok", false) && ReadString(nothingToClear, "outcome", "") == "unchanged");
                // The custom endpoint's Reign-only key is refused unless the URL the caller saw is the endpoint left in place.
                CampaignFileService.WriteJsonObject(settingsFile, TestDict("llmProvider", "openai_compatible", "apiUrl", "https://old.invalid/v1/chat/completions"));
                var staleEndpoint = SaveSettingsCore(TestDict("apiKey", "fixture-endpoint-key", "expectedApiUrl", "https://new.invalid/v1"), out _, out _, settingsFile);
                bool staleStoredNothing = ReadString(readFile(), "apiKey", "") == "";
                var sameEndpoint = SaveSettingsCore(TestDict("apiKey", "fixture-endpoint-key", "expectedApiUrl", "https://OLD.invalid/other"), out _, out _, settingsFile);
                var afterEndpoint = readFile();
                check("reign_only_custom_key_endpoint_checked", ReadString(staleEndpoint, "outcome", "") == "endpoint_changed" && staleStoredNothing
                    && ReadBool(sameEndpoint, "ok", false) && ReadString(afterEndpoint, "apiKey", "") == "fixture-endpoint-key"
                    && !afterEndpoint.ContainsKey("expectedApiUrl"));

                // A legacy local custom key never survives a new origin, in settings or the vault; same-origin edits, a bound
                // reference, an explicitly supplied key and the other slots keep theirs.
                const string newUrl = "https://new.invalid/v1/chat/completions";
                var legacy = new Dictionary<string, object> { ["llmProvider"] = "openai_compatible", ["apiUrl"] = oldUrl, ["apiKey"] = "old-secret",
                    ["nanoGptApiKey"] = "fixture-local", ["openRouterApiKey"] = "router-local", ["chatProviderProfilesInitialized"] = true };
                Func<Dictionary<string, object>, Dictionary<string, object>, (Dictionary<string, object> S, List<string> Clears)> mergeInto = (start, incoming) =>
                { var s = new Dictionary<string, object>(start); return (s, LlmProviderService.MergeProviderSettings(s, incoming)); };
                var same = mergeInto(legacy, new Dictionary<string, object> { ["apiUrl"] = "https://OLD.invalid/v2", ["apiKey"] = "" });
                var moved = mergeInto(legacy, new Dictionary<string, object> { ["apiUrl"] = newUrl, ["apiKey"] = "" });
                var maskedMove = mergeInto(legacy, new Dictionary<string, object> { ["apiUrl"] = newUrl, ["apiKey"] = "************" });
                var explicitKey = mergeInto(legacy, new Dictionary<string, object> { ["apiUrl"] = newUrl, ["apiKey"] = "fixture-explicit" });
                var boundMove = mergeInto(boundTo("https://old.invalid:443"), new Dictionary<string, object> { ["apiUrl"] = "https://third.invalid/v1" });
                WriteSettingsSecretsVault(vault, legacy);
                bool plainWriteSkipped = !CanSafelyPersistSettingsSecretVault(vault, new Dictionary<string, object> { ["apiUrl"] = newUrl, ["apiKey"] = "" }, false);
                bool clearWriteAllowed = CanSafelyPersistSettingsSecretVault(vault, moved.S, moved.Clears.Count > 0);
                if (clearWriteAllowed) WriteSettingsSecretsVault(vault, moved.S);
                var afterMove = new Dictionary<string, object> { ["apiUrl"] = newUrl, ["apiKey"] = "", ["nanoGptApiKey"] = "", ["openRouterApiKey"] = "" };
                RecoverSettingsSecretsFromVault(vault, afterMove, false, out _);
                check("legacy_custom_key_cleared_on_origin_change", moved.Clears.SequenceEqual(new[] { "apiKey" }) && ReadString(moved.S, "apiKey", "x") == ""
                    && ProviderSecret(moved.S, "apiKey", LlmProviderService.LlmApiUrl(moved.S)) == "" && ReadString(maskedMove.S, "apiKey", "x") == ""
                    && ReadString(moved.S, "nanoGptApiKey", "") == "fixture-local" && ReadString(moved.S, "openRouterApiKey", "") == "router-local");
                check("legacy_custom_key_kept_for_same_origin_or_explicit_key", same.Clears.Count == 0 && ReadString(same.S, "apiKey", "") == "old-secret"
                    && explicitKey.Clears.Count == 0 && ReadString(explicitKey.S, "apiKey", "") == "fixture-explicit"
                    && boundMove.Clears.Count == 0 && BoundKeyId(boundMove.S, "apiKey") == customId);
                check("vault_does_not_restore_cleared_custom_key", plainWriteSkipped && clearWriteAllowed && ReadString(afterMove, "apiKey", "x") == ""
                    && ReadString(afterMove, "nanoGptApiKey", "") == "fixture-local" && ReadString(afterMove, "openRouterApiKey", "") == "router-local");

                // Mutation guard: custom header, exact same-origin, loopback direct peer, Apache-overwritten peer/host only.
                Func<string, bool> peers = address => address == "127.0.0.1" || address == "172.20.0.1";
                Func<Program.HttpRequest, bool> allowed = r => GlobalKeyRequestAllowed(r, peers);
                var direct = new Program.HttpRequest { KeysHeader = "1", RemoteAddress = "127.0.0.1", Host = "127.0.0.1:5101", Origin = "http://127.0.0.1:5101" };
                var apache = new Program.HttpRequest { KeysHeader = "1", RemoteAddress = "127.0.0.1", Host = "127.0.0.1:5101", ProxyPeer = "172.20.0.1",
                    ProxyHost = "127.0.0.1:8089", Origin = "http://127.0.0.1:8089" };
                Func<Program.HttpRequest, Action<Program.HttpRequest>, Program.HttpRequest> with = (r, change) => { var copy = (Program.HttpRequest)r.GetType().GetMethod("MemberwiseClone",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(r, null); change(copy); return copy; };
                check("mutation_guard", allowed(direct) && allowed(apache)
                    && !allowed(with(direct, r => r.KeysHeader = ""))
                    && !allowed(with(direct, r => r.Origin = ""))
                    && !allowed(with(direct, r => r.Origin = "https://evil.invalid"))
                    && !allowed(with(direct, r => r.Host = "evil.invalid:5101"))
                    && !allowed(with(direct, r => r.RemoteAddress = "192.168.1.5"))
                    && !allowed(with(apache, r => r.ProxyPeer = "192.168.1.5"))
                    && !allowed(with(apache, r => r.ProxyHost = "evil.invalid"))
                    && !allowed(with(apache, r => r.Origin = "http://127.0.0.1:5101")));

                // Central ingress policy: real loopback socket, Apache claims only together and canonical, exact local
                // authority; Origin/Sec-Fetch-Site only ever refuse. No protocol header or Origin absence grants access.
                Func<Program.HttpRequest, string> refusal = r => Program.IngressAccessRefusal(r, peers, 5101);
                var native = new Program.HttpRequest { Method = "POST", Path = "/llm/chat", RemoteAddress = "127.0.0.1", Host = "127.0.0.1:5101" };
                var nativeApache = with(native, r => { r.ProxyPeer = "172.20.0.1"; r.ProxyHost = "127.0.0.1:8089"; });
                var panel = with(nativeApache, r => r.Origin = "http://127.0.0.1:8089");
                var panelGet = with(nativeApache, r => { r.Method = "GET"; r.Path = "/api/settings"; r.FetchSite = "same-origin"; });
                check("ingress_allows_native_apache_and_same_origin", refusal(native) == null && refusal(nativeApache) == null
                    && refusal(panel) == null && refusal(panelGet) == null
                    && refusal(with(native, r => r.Host = "localhost:5101")) == null
                    && refusal(with(panel, r => { r.ProxyHost = "localhost:8089"; r.Origin = "http://localhost:8089"; })) == null
                    && refusal(with(panelGet, r => r.FetchSite = "none")) == null);
                check("ingress_refuses_cross_site_browser", refusal(with(panel, r => r.Origin = "https://evil.invalid")) == "origin"
                    && refusal(with(panel, r => r.Origin = "null")) == "origin"
                    && refusal(with(panel, r => r.Origin = "http://127.0.0.1:5101")) == "origin"
                    && refusal(with(nativeApache, r => r.FetchSite = "cross-site")) == "fetch_site"
                    && refusal(with(nativeApache, r => r.FetchSite = "same-site")) == "fetch_site"
                    && refusal(with(nativeApache, r => r.FetchSite = "none")) == "fetch_site");
                check("ingress_refuses_untrusted_transport", refusal(with(native, r => r.RemoteAddress = "192.168.1.5")) == "socket_peer"
                    && refusal(with(native, r => r.RemoteAddress = "")) == "socket_peer"
                    && refusal(with(native, r => r.Host = "evil.invalid:5101")) == "host"
                    && refusal(with(native, r => r.Host = null)) == "host"
                    && refusal(with(native, r => r.Host = "127.0.0.1:8089")) == "host"
                    && refusal(with(nativeApache, r => r.ProxyPeer = "192.168.1.5")) == "proxy_peer"
                    && refusal(with(nativeApache, r => r.ProxyPeer = "127.1")) == "proxy_peer"
                    && refusal(with(nativeApache, r => r.ProxyPeer = "")) == "proxy_peer"
                    && refusal(with(nativeApache, r => r.ProxyHost = "rebind.invalid:8089")) == "proxy_host"
                    && refusal(with(nativeApache, r => r.ProxyHost = null)) == "proxy_incomplete"
                    && refusal(with(native, r => r.ProxyHost = "127.0.0.1:8089")) == "proxy_incomplete"
                    && refusal(with(native, r => { r.RemoteAddress = "192.168.1.5"; r.ReignProtocol = "1"; })) == "socket_peer"
                    && refusal(with(native, r => { r.Path = "/health"; r.RemoteAddress = "192.168.1.5"; })) == "socket_peer"
                    && refusal(with(native, r => { r.Method = "GET"; r.Path = "/health"; r.RemoteAddress = "192.0.2.7"; })) == "socket_peer");
                // X-Forwarded-For presence (ProxyAddHeaders On) marks an Apache hop even when Connection stripped the custom
                // fields; the marker never authorizes, so the overwritten pair and its peer/host checks still decide.
                check("ingress_proxy_marker_requires_overwritten_pair", refusal(with(native, r => r.ProxyForwarded = true)) == "proxy_incomplete"
                    && refusal(with(nativeApache, r => { r.ProxyForwarded = true; r.ProxyPeer = null; })) == "proxy_incomplete"
                    && refusal(with(nativeApache, r => { r.ProxyForwarded = true; r.ProxyHost = null; })) == "proxy_incomplete"
                    && refusal(with(nativeApache, r => { r.ProxyForwarded = true; r.ProxyPeer = "192.168.1.5"; })) == "proxy_peer"
                    && refusal(with(nativeApache, r => { r.ProxyForwarded = true; r.RemoteAddress = "192.168.1.5"; })) == "socket_peer"
                    && refusal(with(nativeApache, r => { r.ProxyForwarded = true; r.Host = "127.0.0.1:8089"; })) == "host"
                    && refusal(with(nativeApache, r => r.ProxyForwarded = true)) == null
                    && refusal(with(panel, r => r.ProxyForwarded = true)) == null
                    && refusal(native) == null);

                // Framing and limits are decided from the headers alone: a 403/413 below arrives with no body sent at all.
                Func<string, string, object> ingressFrom = (remote, raw) =>
                {
                    try { return Program.ReadRequest(new MemoryStream(System.Text.Encoding.Latin1.GetBytes(raw)), remote, peers, 5101); }
                    catch (Program.HttpIngressException ex) { return ex.Status + ":" + ex.Reason; }
                };
                Func<string, object> ingress = raw =>
                {
                    object result = ingressFrom("127.0.0.1", raw);
                    return result is string failure ? int.Parse(failure.Substring(0, failure.IndexOf(':')), System.Globalization.CultureInfo.InvariantCulture) : result;
                };
                const string line = "POST /api/settings HTTP/1.1\r\nHost: 127.0.0.1:5101\r\n";
                var parsed = ingress(line + "Content-Length: 7\r\n\r\n{\"a\":1}") as Program.HttpRequest;
                bool parsedHeldBudget = parsed != null && parsed.ReservedJsonBytes == 7;
                Program.ReleaseIngressReservations(parsed);
                check("ingress_parses_bounded_json", parsed != null && ReadInt(parsed.JsonBody, "a", 0) == 1 && parsed.Origin == null
                    && parsedHeldBudget && parsed.ReservedJsonBytes == 0
                    && ingress("") == null && ingress(line + "\r\n") is Program.HttpRequest);
                check("ingress_rejects_malformed_framing", object.Equals(ingress(line + "Content-Length: 1\r\nContent-Length: 1\r\n\r\n{"), 400)
                    && object.Equals(ingress(line + "Transfer-Encoding: chunked\r\n\r\n0\r\n\r\n"), 411)
                    && object.Equals(ingress(line + "Transfer-Encoding: chunked\r\nContent-Length: 2\r\n\r\n{}"), 400)
                    && object.Equals(ingress(line + "Content-Length: -1\r\n\r\n"), 400)
                    && object.Equals(ingress(line + "Content-Length: +2\r\n\r\n{}"), 400)
                    && object.Equals(ingress(line + "Content-Length: 1,1\r\n\r\n{"), 400)
                    && object.Equals(ingress(line + "Content-Length: 10\r\n\r\n{\"a\""), 400)
                    && object.Equals(ingress(line + "Content-Length: 5\r\n\r\n{bad}"), 400)
                    && object.Equals(ingress(line + "X-Long: a\r\n b\r\n\r\n"), 400)
                    && object.Equals(ingress("POST /api/settings\r\nHost: 127.0.0.1:5101\r\n\r\n"), 400)
                    && object.Equals(ingress(line + "Content-Length: 0\r\n"), 400)
                    && object.Equals(ingress(line + "Host: 127.0.0.1:5101\r\n\r\n"), 400)
                    && object.Equals(ingress(line + "X-Reign-Peer: 127.0.0.1\r\nX-Reign-Peer: 127.0.0.1\r\n\r\n"), 400)
                    && object.Equals(ingress("POST /api/settings HTTP/1.1\r\nHost: " + new string('a', 300) + "\r\n\r\n"), 400)
                    && object.Equals(ingress(line + "X-Pad: " + new string('a', 70 * 1024) + "\r\n\r\n"), 431));
                check("ingress_policy_and_limits_before_body", object.Equals(ingress(line + "Origin: https://evil.invalid\r\nContent-Length: 100\r\n\r\n"), 403)
                    && object.Equals(ingress("POST /api/campaigns/import HTTP/1.1\r\nHost: 127.0.0.1:5101\r\nSec-Fetch-Site: cross-site\r\nContent-Length: 100\r\n\r\n"), 403)
                    && object.Equals(ingress(line + "Content-Length: " + (64L * 1024 * 1024 + 1) + "\r\n\r\n"), 413)
                    && object.Equals(ingress("POST /api/support-bundle HTTP/1.1\r\nHost: 127.0.0.1:5101\r\nContent-Length: " + (8L * 1024 * 1024 + 1) + "\r\n\r\n"), 413)
                    && object.Equals(ingress("POST /api/campaigns/import HTTP/1.1\r\nHost: 127.0.0.1:5101\r\nContent-Type: application/zip\r\nContent-Length: "
                        + (Program.IngressMaxCampaignUploadBytes + 1) + "\r\n\r\n"), 413)
                    && object.Equals(ingress("GET /api/settings HTTP/1.1\r\nHost: 127.0.0.1:5101\r\nX-Reign-Peer: 192.168.1.5\r\nX-Reign-Host: 127.0.0.1:8089\r\n\r\n"), 403)
                    && ingress("GET /health HTTP/1.1\r\n\r\n") is Program.HttpRequest);

                // Post-Apache wire shapes: loopback socket, rewritten loopback Host, X-Forwarded-For appended by mod_proxy.
                const string proxied = "GET /api/settings HTTP/1.1\r\nHost: 127.0.0.1:5101\r\n";
                var proxiedPair = ingressFrom("127.0.0.1", proxied + "X-Reign-Peer: 172.20.0.1\r\nX-Reign-Host: 127.0.0.1:8089\r\n"
                    + "X-Forwarded-For: 192.168.1.5\r\nSec-Fetch-Site: same-origin\r\n\r\n") as Program.HttpRequest;
                check("ingress_proxy_marker_wire_shapes", (ingressFrom("127.0.0.1", proxied + "X-Forwarded-For: 192.168.1.5\r\n\r\n") as string) == "403:proxy_incomplete"
                    && (ingressFrom("127.0.0.1", proxied + "X-Forwarded-For: 127.0.0.1\r\n\r\n") as string) == "403:proxy_incomplete"
                    && (ingressFrom("127.0.0.1", proxied + "X-Reign-Host: 127.0.0.1:8089\r\nX-Forwarded-For: 192.168.1.5\r\n\r\n") as string) == "403:proxy_incomplete"
                    && (ingressFrom("127.0.0.1", proxied + "X-Reign-Peer: 192.168.1.5\r\nX-Reign-Host: 127.0.0.1:8089\r\nX-Forwarded-For: 172.20.0.1\r\n\r\n") as string) == "403:proxy_peer"
                    && proxiedPair != null && proxiedPair.ProxyForwarded
                    && ingressFrom("127.0.0.1", proxied + "\r\n") is Program.HttpRequest
                    && ingressFrom("192.0.2.7", "GET /health HTTP/1.1\r\nX-Forwarded-For: 192.0.2.7\r\n\r\n") is Program.HttpRequest);

                // Public routes: exact raw "GET" plus exact raw path, from any transport, with no body. A declared body is
                // refused from the headers ("public_body", never body_eof: no body bytes are sent here); every alias, case
                // or method variant is an ordinary route and meets the transport policy (socket_peer from this address).
                Func<string, string> publicOutcome = raw => ingressFrom("192.0.2.7", raw) is string failure ? failure : "ok";
                check("ingress_public_get_exact_and_bodiless", new[] { "GET /health HTTP/1.1\r\n\r\n", "GET / HTTP/1.1\r\n\r\n",
                        "GET /index.html?v=1 HTTP/1.1\r\n\r\n", "GET /assets/ReignLogo.png HTTP/1.0\r\n\r\n",
                        "GET /assets/reign-icon.webp HTTP/1.1\r\nContent-Length: 0\r\n\r\n" }.All(raw => publicOutcome(raw) == "ok")
                    && new[] { "GET /health HTTP/1.1\r\nContent-Length: 5\r\n\r\n", "GET / HTTP/1.1\r\nContent-Length: 1\r\n\r\n",
                        "GET /health HTTP/1.1\r\nContent-Length: 999999999999999999\r\n\r\n" }.All(raw => publicOutcome(raw) == "400:public_body")
                    && new[] { "get /health", "Get /health", "HEAD /health", "POST /health", "GET /HEALTH", "GET /health/", "GET //health",
                        "GET /./health", "GET /assets/../health", "GET /%68ealth", "GET /Index.html", "GET /assets/reignlogo.png" }
                        .All(start => publicOutcome(start + " HTTP/1.1\r\nContent-Length: 5\r\n\r\n") == "403:socket_peer"));

                // Shared JSON input budget. Exact accounting under contention on a private counter, then the real ingress path
                // with a synthetic reservation that leaves only ten bytes of the shared budget free (no large buffers).
                long localActive = 0; int granted = 0;
                System.Threading.Tasks.Parallel.For(0, 1000, _ =>
                {
                    if (Program.TryReserveIngressBytes(ref localActive, 3, 300)) System.Threading.Interlocked.Increment(ref granted);
                });
                check("ingress_json_budget_exact_under_concurrency", granted == 100 && System.Threading.Interlocked.Read(ref localActive) == 300
                    && !Program.TryReserveIngressBytes(ref localActive, 1, 300) && Program.TryReserveIngressBytes(ref localActive, 0, 300));
                // The synthetic process-wide reservation and exclusive upload slot below would refuse real traffic, so they run
                // only in non-listening verification (RunTestSuiteApi can reach this suite while the server is listening).
                if (Program.ActiveServerPort <= 0)
                {
                    long synthetic = Program.IngressMaxActiveJsonBytes - 10;
                    bool syntheticHeld = Program.TryReserveIngressBytes(ref Program.ActiveIngressJsonBytes, synthetic, Program.IngressMaxActiveJsonBytes);
                    bool budgetHonoured = false;
                    try
                    {
                        Func<string, object> json = body => ingressFrom("127.0.0.1", line + "Content-Length: " + body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n" + body);
                        bool exhaustedBeforeRead = (ingressFrom("127.0.0.1", line + "Content-Length: 11\r\n\r\n") as string) == "429:json_budget";
                        bool parseErrorReleased = (json("{bad json}") as string) == "400:json";
                        bool disconnectReleased = (ingressFrom("127.0.0.1", line + "Content-Length: 10\r\n\r\n{\"a") as string) == "400:body_eof";
                        var held = json("{\"a\":1234}") as Program.HttpRequest;
                        bool heldBlocksOthers = held != null && held.ReservedJsonBytes == 10 && (json(" ") as string) == "429:json_budget";
                        Program.ReleaseIngressReservations(held);
                        Program.ReleaseIngressReservations(held);
                        var afterRelease = json(" ") as Program.HttpRequest;
                        bool releasedOnce = afterRelease != null && afterRelease.ReservedJsonBytes == 1
                            && (json("{\"a\":1234}") as string) == "429:json_budget";
                        Program.ReleaseIngressReservations(afterRelease);
                        budgetHonoured = exhaustedBeforeRead && parseErrorReleased && disconnectReleased && heldBlocksOthers && releasedOnce;
                    }
                    finally
                    {
                        if (syntheticHeld) System.Threading.Interlocked.Add(ref Program.ActiveIngressJsonBytes, -synthetic);
                    }
                    check("ingress_json_budget_shared_and_released", syntheticHeld && budgetHonoured);

                    // One staged campaign upload at a time: a second is refused before any staging file exists, and the slot
                    // returns with the holder's release.
                    bool slotTaken = System.Threading.Interlocked.CompareExchange(ref Program.ActiveCampaignUploads, 1, 0) == 0;
                    var uploadHolder = new Program.HttpRequest { HoldsUploadSlot = slotTaken ? 1 : 0 };
                    string secondUpload;
                    try
                    {
                        secondUpload = ingressFrom("127.0.0.1", "POST /api/campaigns/import HTTP/1.1\r\nHost: 127.0.0.1:5101\r\n"
                            + "Content-Type: application/zip\r\nContent-Length: 5\r\n\r\n") as string;
                    }
                    finally { Program.ReleaseIngressReservations(uploadHolder); }
                    check("ingress_single_staged_upload", slotTaken && secondUpload == "429:upload_busy" && uploadHolder.HoldsUploadSlot == 0
                        && System.Threading.Volatile.Read(ref Program.ActiveCampaignUploads) == 0);

                    // A disconnected normal upload must leave neither a partial archive nor an occupied slot.
                    string uploadRoot = Path.Combine(Program.DataDir, "backups", "uploads");
                    string[] StagedUploads() => Directory.Exists(uploadRoot)
                        ? Directory.GetFiles(uploadRoot, "*.reignbackup") : Array.Empty<string>();
                    var beforeInterruptedUpload = new HashSet<string>(StagedUploads(), StringComparer.Ordinal);
                    string interruptedUpload = ingressFrom("127.0.0.1",
                        "POST /api/campaigns/import HTTP/1.1\r\nHost: 127.0.0.1:5101\r\n"
                        + "Content-Type: application/zip\r\nContent-Length: 5\r\n\r\nAB") as string;
                    check("ingress_interrupted_upload_cleans_partial_file_and_slot",
                        interruptedUpload == "400:body_eof" && beforeInterruptedUpload.SetEquals(StagedUploads())
                        && System.Threading.Volatile.Read(ref Program.ActiveCampaignUploads) == 0);
                }

                // A stored custom or named-chat key follows only its configured origin; a payload key goes where it is sent.
                var storedCustom = TestDict("llmProvider", "openai_compatible", "apiUrl", "https://custom.invalid/v1/chat/completions",
                    "apiKey", "stored-custom", "openRouterApiKey", "stored-router");
                Func<Dictionary<string, object>, string> customKey = payload => LlmProviderService.ResolveLlmRequestApiKey(storedCustom, payload, out _, ReadString(payload, "apiUrl", null));
                calls.Clear();
                check("stored_key_withheld_from_foreign_origin", customKey(TestDict("apiUrl", "https://attacker.invalid/v1/chat/completions")) == ""
                    && customKey(TestDict("apiUrl", "http://custom.invalid/v1/chat/completions")) == ""
                    && customKey(TestDict("apiUrl", "https://custom.invalid:8443/v1")) == ""
                    && customKey(TestDict("apiUrl", "https://user@custom.invalid/v1")) == ""
                    && customKey(TestDict("apiUrl", "file:///var/www/html/ReignServer/data/settings.json")) == ""
                    && ProviderSecret(storedCustom, "openRouterApiKey", "https://openrouter.ai.attacker.invalid/v1") == ""
                    && ProviderSecret(storedCustom, "apiKey") == "" && calls.Count == 0);
                check("stored_key_kept_for_same_origin_and_explicit_key", customKey(TestDict("apiUrl", "https://CUSTOM.invalid/v2/other")) == "stored-custom"
                    && customKey(null) == "stored-custom" && LlmProviderService.LlmApiKey(storedCustom) == "stored-custom"
                    && customKey(TestDict("apiUrl", "https://elsewhere.invalid/v1", "apiKey", "one-off")) == "one-off"
                    && ProviderSecret(storedCustom, "openRouterApiKey", LlmProviderService.OpenRouterChatUrl) == "stored-router" && calls.Count == 0);
                var unsafeUrl = LlmProviderService.ChatWithLlm(TestDict("apiUrl", "file:///tmp/reign-provider", "apiKey", "one-off", "singleProviderAttempt", true), null, null, storedCustom);
                check("unsafe_provider_url_refused_before_dispatch", ReadString(unsafeUrl, "errorCode", "") == "provider_url_invalid"
                    && ReadInt(unsafeUrl, "providerCallCount", -1) == 0
                    && LlmProviderService.IsDispatchableProviderUrl("http://192.168.1.20:8000/v1/chat/completions") && LlmProviderService.IsDispatchableProviderUrl("http://localhost:11434/v1")
                    && !new[] { "file:///etc/passwd", "ftp://host/x", "https://u:p@host/x", "/v1/chat", "" }.Any(LlmProviderService.IsDispatchableProviderUrl));

                // An implicit saved key aimed at another origin is refused by ChatWithLlm itself (complete and streaming), never
                // sent keyless; a bound key keeps global_key_unavailable. Neither reaches the store, a provider or the stream.
                int streamedFragments = 0;
                Func<Dictionary<string, object>, Action<string>, Dictionary<string, object>> chat = (settingsFixture, delta) =>
                    LlmProviderService.ChatWithLlm(new Dictionary<string, object> { ["apiUrl"] = "https://attacker.invalid/v1/chat/completions", ["singleProviderAttempt"] = true },
                        delta, delta == null ? null : (Func<bool>)(() => false), settingsFixture);
                Action<string> countFragments = _ => streamedFragments++;
                calls.Clear();
                var storedForeign = new[] { chat(storedCustom, null), chat(storedCustom, countFragments) };
                var boundForeign = new[] { chat(customBound, null), chat(customBound, countFragments) };
                check("stored_key_foreign_origin_refused_before_dispatch",
                    storedForeign.All(r => ReadString(r, "errorCode", "") == "stored_key_origin_mismatch" && ReadInt(r, "providerCallCount", -1) == 0 && !ReadBool(r, "ok", true))
                    && boundForeign.All(r => ReadString(r, "errorCode", "") == "global_key_unavailable" && ReadInt(r, "providerCallCount", -1) == 0)
                    && streamedFragments == 0 && calls.Count == 0);
                var keyless = TestDict("llmProvider", "openai_compatible", "apiUrl", "http://127.0.0.1:8000/v1/chat/completions", "apiKey", "");
                check("stored_key_rule_scope", !LlmProviderService.StoredKeyForeignDestination(storedCustom, TestDict("apiKey", "one-off"), "https://attacker.invalid/v1")
                    && !LlmProviderService.StoredKeyForeignDestination(storedCustom, null, "https://CUSTOM.invalid/v2/other")
                    && !LlmProviderService.StoredKeyForeignDestination(keyless, null, "http://192.168.1.20:8000/v1/chat/completions")
                    && !LlmProviderService.StoredKeyForeignDestination(customBound, null, "https://attacker.invalid/v1")
                    && !LlmProviderService.StoredKeyForeignDestination(TestDict("llmProvider", LlmProviderService.OpenRouterProvider, "openRouterApiKey", "stored-router"), null, LlmProviderService.OpenRouterChatUrl)
                    && LlmProviderService.StoredKeyForeignDestination(storedCustom, TestDict("apiUrl", "x"), "http://custom.invalid/v1/chat/completions"));

                // Process-selecting settings: HTTP saves keep the stored value or return to the official default only.
                var operatorOwned = TestDict("codexExecutable", "/opt/codex/bin/codex", "codexHome", "/srv/codex-home", "portraitNativeSourceGeneratorPath", "");
                check("process_settings_operator_owned", LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned,
                        TestDict("codexExecutable", " /opt/codex/bin/codex ", "codexHome", "/srv/codex-home", "portraitNativeSourceGeneratorPath", "")) == null
                    && LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("codexExecutable", "codex", "codexHome", "")) == null
                    && LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("codexExecutable", "")) == null
                    && LlmProviderService.OperatorOwnedProcessSettingRefusal(TestDict(), TestDict("codexExecutable", "", "codexHome", "")) == null
                    && LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("apiUrl", "https://x.invalid")) == null
                    && (LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("codexExecutable", "/tmp/evil")) ?? "").Contains("codexExecutable")
                    && (LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("codexHome", "/tmp/home")) ?? "").Contains("codexHome")
                    && LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("portraitNativeSourceGeneratorPath", "/mnt/c/x/Generator.exe")) != null
                    && LlmProviderService.OperatorOwnedProcessSettingRefusal(operatorOwned, TestDict("codexExecutable", TestDict("path", "/tmp/evil"))) != null);

                // struct stat offsets on Linux x64 against disposable paths; other platforms must report nothing.
                if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64)
                {
                    string file = Path.Combine(root, "config.json"), sock = Path.Combine(root, "s.sock");
                    File.WriteAllText(file, "{}");
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                    using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    listener.Bind(new UnixDomainSocketEndPoint(sock));
                    uint uid = NativeGeteuid();
                    check("linux_x64_stat_offsets", LinuxLstat(file, out uint fm, out uint fu) && (fm & 0xF000) == 0x8000 && (fm & 0xFFF) == 0x1A0 && fu == uid
                        && LinuxLstat(root, out uint dm, out _) && (dm & 0xF000) == 0x4000
                        && LinuxLstat(sock, out uint sm, out _) && (sm & 0xF000) == 0xC000
                        && (uid == 0 || (!RootOwnedPath(file, 0x8000, GlobalKeysConfigForbiddenBits) && !RootOwnedPath(sock, 0xC000, 0))));
                    // Config mode policy on a disposable file owned by this account: 0640 accepted, 0644 and 0660 refused.
                    Func<UnixFileMode, bool> configAccepted = mode => { File.SetUnixFileMode(file, mode); return RootOwnedPath(file, 0x8000, GlobalKeysConfigForbiddenBits, uid); };
                    check("linux_config_mode_0640_only", configAccepted(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead)
                        && !configAccepted(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead)
                        && !configAccepted(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite));
                }
                else check("non_linux_stat_fails_closed", !LinuxLstat(Path.GetTempPath(), out _, out _));
            }
            catch (Exception ex)
            {
                check("unexpected_" + ex.GetType().Name, false);
            }
            finally
            {
                GlobalKeyTransportOverride = previous;
                InvalidateGlobalKeyStatusCache();
                GlobalKeyAutoSelectionOffForTests = previousAutoSelection;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            return rows;
        }
    }
}
