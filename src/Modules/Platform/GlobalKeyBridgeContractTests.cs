using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        [DllImport("libc", EntryPoint = "geteuid")]
        private static extern uint NativeGeteuid();

        // Fake-transport contract for global key references. No real socket, store or provider traffic; vault and stat
        // checks use disposable temp files only.
        private static List<Dictionary<string, object>> RunGlobalKeyBridgeContractTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool> check = (id, passed) => rows.Add(new Dictionary<string, object> {
                ["id"] = "global_keys_" + id, ["passed"] = passed, ["summary"] = id });
            const string keyId = "11111111-2222-4333-8444-555555555555", other = "99999999-2222-4333-8444-555555555555";
            const string secret = "fixture-global-secret-value", openAi = "https://api.openai.com:443";
            var calls = new List<Dictionary<string, object>>();
            string resolveOutcome = "resolved", writeOutcome = "updated", createdOrigin = NanoGptOrigin;
            var store = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["key_id"] = keyId, ["provider"] = "openrouter", ["origin"] = OpenRouterOrigin, ["label"] = "Reign", ["preview"] = "...abcd", ["revision"] = 3 },
                new Dictionary<string, object> { ["key_id"] = other, ["provider"] = "nano_gpt", ["origin"] = NanoGptOrigin, ["label"] = "Other", ["preview"] = "...zz", ["revision"] = 1 } };
            var catalog = new List<Dictionary<string, object>> {
                new Dictionary<string, object> { ["provider"] = "openai", ["origin"] = openAi },
                new Dictionary<string, object> { ["provider"] = "custom_openai", ["origin"] = null } };
            var previous = GlobalKeyTransportOverride;
            GlobalKeyTransportOverride = request =>
            {
                calls.Add(request);
                string op = ReadString(request, "op", "");
                if (op == "status") return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "listed", ["keys"] = store, ["providers"] = catalog };
                if (op == "resolve") return resolveOutcome == "resolved"
                    ? new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "resolved", ["secret"] = secret, ["revision"] = 3 } : GlobalKeyFail(resolveOutcome);
                bool ok = writeOutcome == "updated" || writeOutcome == "created" || writeOutcome == "unchanged";
                return ok ? new Dictionary<string, object> { ["ok"] = true, ["outcome"] = writeOutcome, ["key"] = op == "create"
                        ? new Dictionary<string, object> { ["key_id"] = other, ["provider"] = "nano_gpt", ["origin"] = createdOrigin }
                        : new Dictionary<string, object> { ["key_id"] = keyId } }
                    : GlobalKeyFail(writeOutcome);
            };
            string root = Path.Combine(Path.GetTempPath(), "reign-global-keys-" + Guid.NewGuid().ToString("N"));
            try
            {
                var binding = new Dictionary<string, object> { ["keyId"] = keyId, ["provider"] = "openrouter", ["origin"] = OpenRouterOrigin };
                var settings = new Dictionary<string, object> { ["llmProvider"] = "openrouter", ["openRouterApiKey"] = "",
                    ["nanoGptApiKey"] = "fixture-local", ["globalKeyBindings"] = new Dictionary<string, object> { ["openRouterApiKey"] = binding } };

                check("unbound_slot_keeps_local_value", ProviderSecret(settings, "nanoGptApiKey", NanoGptChatCompletionsUrl) == "fixture-local" && calls.Count == 0);
                bool read = LlmApiKey(settings) == secret && ProviderSecret(settings, "openRouterApiKey", "https://openrouter.ai/api/v1/images") == secret;
                var resolves = calls.Where(c => ReadString(c, "op", "") == "resolve").ToList();
                check("bound_slot_reads_current_value_every_use", read && resolves.Count == 2
                    && resolves.All(c => ReadString(ReadDictionary(c, "body"), "key_id", "") == keyId && ReadString(ReadDictionary(c, "body"), "origin", "") == OpenRouterOrigin));
                calls.Clear();
                check("origin_mismatch_fails_closed", ProviderSecret(settings, "openRouterApiKey", "https://openrouter.ai.attacker.invalid/v1") == ""
                    && ProviderSecret(settings, "openRouterApiKey", OpenRouterChatUrl, "https://other.invalid/") == "" && calls.Count == 0
                    && ProviderKeyUnavailable(settings, "openRouterApiKey", "") && !ProviderKeyUnavailable(settings, "nanoGptApiKey", ""));
                foreach (string outcome in new[] { "changed", "missing", "wrong_provider", "unavailable", "not_enrolled" })
                {
                    resolveOutcome = outcome;
                    check("resolve_" + outcome + "_has_no_fallback", ProviderSecret(settings, "openRouterApiKey", OpenRouterChatUrl) == "");
                }

                // A bound key that cannot be resolved stops the LLM request before any provider dispatch.
                resolveOutcome = "unavailable";
                var customBound = new Dictionary<string, object> { ["llmProvider"] = "openai_compatible", ["apiUrl"] = "https://custom.invalid/v1/chat/completions",
                    ["globalKeyBindings"] = new Dictionary<string, object> { ["apiKey"] = new Dictionary<string, object> { ["keyId"] = keyId, ["provider"] = "custom_openai", ["origin"] = "https://custom.invalid:443" } } };
                var refused = ChatWithLlm(new Dictionary<string, object> { ["requestType"] = "dialogue", ["singleProviderAttempt"] = true }, null, null, customBound);
                var redirected = ChatWithLlm(new Dictionary<string, object> { ["apiUrl"] = "https://elsewhere.invalid/v1/chat/completions", ["singleProviderAttempt"] = true }, null, null, customBound);
                check("bound_key_unavailable_fails_closed_before_dispatch", ReadString(refused, "errorCode", "") == "global_key_unavailable"
                    && ReadInt(refused, "providerCallCount", -1) == 0 && ReadString(redirected, "errorCode", "") == "global_key_unavailable");
                resolveOutcome = "resolved";
                calls.Clear();
                SettingsFingerprint(settings);
                SettingsForUi(settings);
                check("diagnostics_never_resolve", calls.Count == 0);

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
                MergeProviderSettings(merged, new Dictionary<string, object> { ["globalKeyBindings"] = new Dictionary<string, object> {
                    ["nanoGptApiKey"] = new Dictionary<string, object> { ["keyId"] = other, ["provider"] = "nano_gpt", ["origin"] = NanoGptOrigin } } });
                check("generic_settings_cannot_bind", GlobalKeyBinding(merged, "nanoGptApiKey") == null && GlobalKeyBinding(merged, "openRouterApiKey") != null);
                check("new_local_provider_secret_refused", LocalProviderKeyWriteRefusal(new Dictionary<string, object> { ["openRouterApiKey"] = "sk-new" }) != null
                    && LocalProviderKeyWriteRefusal(new Dictionary<string, object> { ["apiKey"] = "************", ["nanoGptApiKey"] = "" }) == null
                    && LocalProviderKeyWriteRefusal(new Dictionary<string, object> { ["qdrantApiKey"] = "qdrant-local" }) == null);

                var ui = SettingsForUi(settings);
                check("ui_and_diagnostics_hold_no_secret", !Json.Serialize(ui).Contains(secret) && ReadBool(ui, "openRouterApiKeyBound", false)
                    && ProviderKeyConfigured(settings, "openRouterApiKey") && !Json.Serialize(settings).Contains(secret));

                var bound = new List<string>();
                bool bindResult = true;
                Func<string, Dictionary<string, object>, string, string, bool> bind = (slot, b, expected, origin) =>
                { bound.Add(slot + "=" + ReadString(b, "keyId", "") + "<" + expected); return bindResult; };
                Func<Dictionary<string, object>, Dictionary<string, object>> save = payload => SaveGlobalKey(payload, bind, () => settings);
                writeOutcome = "updated";
                var rotate = new Dictionary<string, object> { ["slot"] = "openRouterApiKey", ["mode"] = "rotate", ["keyId"] = keyId, ["revision"] = 3, ["label"] = "Reign",
                    ["secret"] = "fixture-new", ["expectedKeyId"] = keyId };
                var rotated = save(rotate);
                var edit = ReadDictionary(calls.Last(), "body");
                check("rotate_same_uuid_with_revision", ReadBool(rotated, "ok", false) && ReadString(edit, "key_id", "") == keyId && ReadInt(edit, "revision", 0) == 3
                    && bound.SequenceEqual(new[] { "openRouterApiKey=" + keyId + "<" + keyId }) && !rotate.ContainsKey("secret") && !Json.Serialize(rotated).Contains("fixture-new"));
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

                // Mutation guard: custom header, exact same-origin, loopback direct peer, Apache-overwritten peer/host only.
                Func<string, bool> peers = address => address == "127.0.0.1" || address == "172.20.0.1";
                Func<HttpRequest, bool> allowed = r => GlobalKeyRequestAllowed(r, peers);
                var direct = new HttpRequest { KeysHeader = "1", RemoteAddress = "127.0.0.1", Host = "127.0.0.1:5101", Origin = "http://127.0.0.1:5101" };
                var apache = new HttpRequest { KeysHeader = "1", RemoteAddress = "127.0.0.1", Host = "127.0.0.1:5101", ProxyPeer = "172.20.0.1",
                    ProxyHost = "127.0.0.1:8089", Origin = "http://127.0.0.1:8089" };
                Func<HttpRequest, Action<HttpRequest>, HttpRequest> with = (r, change) => { var copy = (HttpRequest)r.GetType().GetMethod("MemberwiseClone",
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
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            return rows;
        }
    }
}
