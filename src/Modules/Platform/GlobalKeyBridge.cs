using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    // Provider keys stored in DwemerDistro's global API keys. Reign keeps only {keyId, provider, origin} references and
    // resolves the current secret at each outbound call through Core's root-owned consumer socket; nothing is cached.
    // New provider secrets are never written to local settings: they go to the global store or are refused.
    internal static partial class Program
    {
        private const string GlobalKeysConfigPath = "/etc/reignserver/global-keys.json";
        private const string GlobalKeysSocketDir = "/mnt/wsl/dwemerdistro-keys";
        private const string NanoGptOrigin = "https://nano-gpt.com:443";
        private const string AtlasOrigin = "https://api.atlascloud.ai:443";
        private const string OpenRouterOrigin = "https://openrouter.ai:443";
        private const string GlobalKeyUnavailableError = "The selected Global API key is unavailable. No provider request was sent; start DwemerDistro or choose another key in API Settings.";
        private const int GlobalKeyRequestLimit = 16 * 1024, GlobalKeyReplyLimit = 256 * 1024;
        private static readonly SemaphoreSlim GlobalKeyWriteGate = new SemaphoreSlim(1, 1);
        // Test seams: an in-process fake transport and a per-thread allowance for legacy fixtures that store local keys.
        // Neither is reachable from requests or settings.
        internal static Func<Dictionary<string, object>, Dictionary<string, object>> GlobalKeyTransportOverride;
        [ThreadStatic] internal static bool LegacyLocalKeyWritesForTests;
        private static readonly string[] GlobalKeySlots = { "apiKey", "openRouterApiKey", "nanoGptApiKey", "portraitNanoGptApiKey", "portraitAtlasApiKey" };

        private static string NormalizedOrigin(string url)
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out Uri uri) || (uri.Scheme != "https" && uri.Scheme != "http")
                || !string.IsNullOrEmpty(uri.UserInfo) || string.IsNullOrEmpty(uri.Host)) return null;
            // The store's form: IPv6 literals stay bracketed (http://[::1]:1234); scoped or unusual host kinds are refused.
            string host = uri.HostNameType switch
            {
                UriHostNameType.IPv6 when !uri.Host.Contains('%') && !uri.IdnHost.Contains('%') => uri.Host,
                UriHostNameType.Dns or UriHostNameType.IPv4 => uri.IdnHost,
                _ => null
            };
            return host == null ? null : uri.Scheme + "://" + host.ToLowerInvariant() + ":" + uri.Port;
        }

        // Server-controlled provider and origin for a slot; the browser never chooses them. The custom endpoint maps to the
        // live catalog's fixed-origin provider with exactly that origin (OpenAI, Groq, ...), otherwise custom_openai.
        private static (string Provider, string Origin) GlobalKeyTarget(Dictionary<string, object> settings, string slot,
            List<Dictionary<string, object>> catalog = null)
        {
            switch (slot)
            {
                case "openRouterApiKey": return ("openrouter", OpenRouterOrigin);
                case "nanoGptApiKey": case "portraitNanoGptApiKey": return ("nano_gpt", NanoGptOrigin);
                case "portraitAtlasApiKey": return ("atlascloud", AtlasOrigin);
                case "apiKey":
                    string origin = NormalizedOrigin(ReadString(settings, "apiUrl", ""));
                    if (origin == null) return (null, null);
                    string fixedProvider = (catalog ?? new List<Dictionary<string, object>>())
                        .Where(p => ReadString(p, "origin", "") == origin && ReadString(p, "provider", "") != "custom_openai")
                        .Select(p => ReadString(p, "provider", "")).FirstOrDefault(p => p.Length > 0);
                    return (fixedProvider ?? "custom_openai", origin);
                default: return (null, null);
            }
        }

        private static Dictionary<string, object> GlobalKeyBinding(Dictionary<string, object> settings, string slot)
        {
            var binding = ReadDictionary(ReadDictionary(settings, "globalKeyBindings"), slot);
            return binding != null && Guid.TryParse(ReadString(binding, "keyId", ""), out _) ? binding : null;
        }

        private static string BoundKeyId(Dictionary<string, object> settings, string slot) => ReadString(GlobalKeyBinding(settings, slot), "keyId", "");

        private static bool ProviderKeyConfigured(Dictionary<string, object> settings, string slot) =>
            GlobalKeyBinding(settings, slot) != null || !string.IsNullOrWhiteSpace(ReadString(settings, slot, ""));

        // Bound slots never keep a local value in settings.json or the installed vault.
        private static void SanitizeBoundKeySlots(Dictionary<string, object> settings)
        {
            foreach (string slot in GlobalKeySlots)
                if (GlobalKeyBinding(settings, slot) != null && settings.ContainsKey(slot)) settings[slot] = "";
        }

        private static string LlmApiKeySlot(Dictionary<string, object> settings)
        {
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            return provider == OpenRouterProvider ? "openRouterApiKey" : provider == NanoGptProvider ? "nanoGptApiKey" : "apiKey";
        }

        // The key for one outbound request. Unbound slots keep their legacy local value; a bound slot requires every
        // destination to share the bound origin and resolves the current global value, with no fallback of any kind.
        // Callers must check ProviderKeyUnavailable before dispatch: "" for a bound slot is a refusal, never "no auth".
        private static string ProviderSecret(Dictionary<string, object> settings, string slot, params string[] destinations)
        {
            var binding = GlobalKeyBinding(settings, slot);
            if (binding == null) return ReadString(settings, slot, "");
            string origin = ReadString(binding, "origin", "");
            if (destinations.Length == 0 || destinations.Any(url => NormalizedOrigin(url) != origin))
            {
                LogOperational("global_keys.resolve", new Dictionary<string, object> { ["slot"] = slot, ["outcome"] = "origin_mismatch" });
                return "";
            }
            var reply = GlobalKeyCall("resolve", new Dictionary<string, object> { ["key_id"] = ReadString(binding, "keyId", ""),
                ["provider"] = ReadString(binding, "provider", ""), ["origin"] = origin });
            if (ReadBool(reply, "ok", false) && reply.TryGetValue("secret", out object secret) && secret is string value && value.Length > 0)
                return value;
            LogOperational("global_keys.resolve", new Dictionary<string, object> { ["slot"] = slot, ["outcome"] = ReadString(reply, "outcome", "unavailable") });
            return "";
        }

        private static bool ProviderKeyUnavailable(Dictionary<string, object> settings, string slot, string resolved) =>
            GlobalKeyBinding(settings, slot) != null && string.IsNullOrEmpty(resolved);

        [DllImport("libc", EntryPoint = "lstat", SetLastError = true)]
        private static extern int NativeLstat(string path, byte[] buffer);

        // Linux x86_64 glibc struct stat: st_mode at 24, st_uid at 28. Other platforms report nothing (fail closed).
        private static bool LinuxLstat(string path, out uint mode, out uint uid)
        {
            mode = uid = 0;
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
            var buffer = new byte[256];
            if (NativeLstat(path, buffer) != 0) return false;
            mode = BitConverter.ToUInt32(buffer, 24);
            uid = BitConverter.ToUInt32(buffer, 28);
            return true;
        }

        // Root-owned, of the expected type, and no ancestor another account could rename or replace (sticky dirs excepted).
        // owner is only for tests on disposable paths; ancestors may then be root's or that owner's.
        private static bool RootOwnedPath(string path, uint type, uint forbiddenBits, uint owner = 0)
        {
            if (!LinuxLstat(path, out uint mode, out uint uid) || uid != owner || (mode & 0xF000) != type || (mode & forbiddenBits) != 0) return false;
            for (string dir = Path.GetDirectoryName(path); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (!LinuxLstat(dir, out uint m, out uint u) || (u != 0 && u != owner) || (m & 0xF000) != 0x4000 || ((m & 0x12) != 0 && (m & 0x200) == 0)) return false;
            return true;
        }

        // At most root:reign 0640: no group write and nothing for others (0x1F = g+w, o+rwx).
        private const uint GlobalKeysConfigForbiddenBits = 0x1F;
        private const int GlobalKeysConfigLimit = 4096;

        private static Dictionary<string, object> GlobalKeyFail(string outcome) =>
            new Dictionary<string, object> { ["ok"] = false, ["outcome"] = outcome };

        private static bool GlobalKeyWriteOp(string op) => op == "edit" || op == "create";

        // One request per connection with a 15 s total deadline and bounded request/reply. Credentials are re-read each call.
        // After a write is sent, any transport or reply failure is "unknown": the write may have happened.
        private static Dictionary<string, object> GlobalKeyCall(string op, Dictionary<string, object> body)
        {
            if (GlobalKeyTransportOverride != null)
                return GlobalKeyTransportOverride(new Dictionary<string, object> { ["op"] = op, ["body"] = body }) ?? GlobalKeyFail(GlobalKeyWriteOp(op) ? "unknown" : "unavailable");
            if (!File.Exists(GlobalKeysConfigPath)) return GlobalKeyFail("not_enrolled");
            string socketPath, token;
            try
            {
                // root:reign 0640 in a root-owned /etc/reignserver: nobody but root can substitute the destination.
                if (!RootOwnedPath(GlobalKeysConfigPath, 0x8000, GlobalKeysConfigForbiddenBits)) return GlobalKeyFail("unavailable");
                using var stream = new FileStream(GlobalKeysConfigPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > GlobalKeysConfigLimit) return GlobalKeyFail("unavailable");
                var bytes = new byte[GlobalKeysConfigLimit + 1];
                int read = 0;
                for (int n; read < bytes.Length && (n = stream.Read(bytes, read, bytes.Length - read)) > 0;) read += n;
                if (read > GlobalKeysConfigLimit) return GlobalKeyFail("unavailable");
                var config = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes, 0, read));
                socketPath = ReadString(config, "socket", "");
                token = ReadString(config, "token", "");
            }
            catch (Exception) { return GlobalKeyFail("unavailable"); }
            if (token.Length < 20 || socketPath != GlobalKeysSocketDir + "/keys.sock" || !RootOwnedPath(GlobalKeysSocketDir, 0x4000, 0x12)
                || !RootOwnedPath(socketPath, 0xC000, 0))
                return GlobalKeyFail("unavailable");
            byte[] request = Encoding.UTF8.GetBytes(Json.Serialize(new Dictionary<string, object> { ["op"] = op, ["token"] = token, ["body"] = body }) + "\n");
            if (request.Length > GlobalKeyRequestLimit) return GlobalKeyFail("invalid");
            bool dispatched = false;
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                var deadline = DateTime.UtcNow.AddSeconds(15);
                if (!socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath)).Wait(TimeSpan.FromSeconds(5))) return GlobalKeyFail("unavailable");
                // The listening process must be root; a substituted socket never receives the token.
                var cred = new byte[12];
                if (socket.GetRawSocketOption(1, 17, cred) != 12 || BitConverter.ToUInt32(cred, 4) != 0) return GlobalKeyFail("unavailable");
                Func<int> remaining = () => Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds);
                for (int sent = 0; sent < request.Length;)
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException();
                    socket.SendTimeout = remaining();
                    dispatched = true;
                    sent += socket.Send(request, sent, request.Length - sent, SocketFlags.None);
                }
                var data = new MemoryStream();
                var chunk = new byte[8192];
                while (true)
                {
                    if (DateTime.UtcNow >= deadline) throw new TimeoutException();
                    socket.ReceiveTimeout = remaining();
                    int n = socket.Receive(chunk);
                    if (n <= 0) break;
                    data.Write(chunk, 0, n);
                    if (data.Length > GlobalKeyReplyLimit) throw new InvalidDataException();
                    if (chunk[n - 1] == (byte)'\n') break;
                }
                var reply = Json.Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(data.ToArray()));
                if (reply == null || !(reply.TryGetValue("ok", out object ok) && ok is bool)) throw new InvalidDataException();
                return reply;
            }
            catch (Exception) { return GlobalKeyFail(dispatched && GlobalKeyWriteOp(op) ? "unknown" : "unavailable"); }
        }

        private static string GlobalKeySlotName(string slot) => slot switch
        {
            "openRouterApiKey" => "OpenRouter", "nanoGptApiKey" => "NanoGPT text", "portraitNanoGptApiKey" => "NanoGPT images",
            "portraitAtlasApiKey" => "AtlasCloud images", _ => "Custom endpoint"
        };

        private static Dictionary<string, object> GlobalKeyRow(Dictionary<string, object> settings, string slot,
            (string Provider, string Origin) target, List<Dictionary<string, object>> keys)
        {
            var binding = GlobalKeyBinding(settings, slot);
            return new Dictionary<string, object>
            {
                ["slot"] = slot, ["name"] = GlobalKeySlotName(slot), ["provider"] = target.Provider ?? "", ["origin"] = target.Origin ?? "",
                ["legacy"] = !string.IsNullOrWhiteSpace(ReadString(settings, slot, "")),
                ["binding"] = binding == null ? null : new Dictionary<string, object> { ["keyId"] = ReadString(binding, "keyId", ""),
                    ["provider"] = ReadString(binding, "provider", ""), ["origin"] = ReadString(binding, "origin", ""),
                    ["originMatches"] = ReadString(binding, "origin", "") == target.Origin },
                ["keys"] = keys.Where(k => ReadString(k, "provider", "") == target.Provider && ReadString(k, "origin", "") == target.Origin)
                    .Select(k => new Dictionary<string, object> { ["keyId"] = ReadString(k, "key_id", ""), ["label"] = ReadString(k, "label", ""),
                        ["preview"] = ReadString(k, "preview", ""), ["revision"] = ReadInt(k, "revision", 0), ["configured"] = ReadBool(k, "configured", false) }).ToList()
            };
        }

        // Masked status for the API Settings rows. Rows are always returned, so an unenrolled or stopped store shows
        // disabled controls with the reason instead of silently offering a local save.
        private static Dictionary<string, object> GlobalKeysStatus(string onlySlot = null)
        {
            var settings = LoadSettings();
            var status = GlobalKeyCall("status", new Dictionary<string, object>());
            bool available = ReadBool(status, "ok", false);
            var keys = available ? ReadDictionaryList(status, "keys") : new List<Dictionary<string, object>>();
            var catalog = available ? ReadDictionaryList(status, "providers") : null;
            var slots = GlobalKeySlots.Where(slot => onlySlot == null || slot == onlySlot)
                .Select(slot => GlobalKeyRow(settings, slot, GlobalKeyTarget(settings, slot, catalog), keys)).ToList();
            return new Dictionary<string, object> { ["ok"] = true, ["available"] = available, ["enrolled"] = ReadString(status, "outcome", "") != "not_enrolled",
                ["outcome"] = available ? "listed" : ReadString(status, "outcome", "unavailable"), ["slots"] = slots,
                ["settingsRevision"] = ReadString(settings, "settingsRevision", "") };
        }

        // Explicit per-slot save: create a new global key, rotate the bound key (same UUID, revision CAS) or bind an
        // existing key. Serialized, checked against the caller's binding snapshot, and only a confirmed global result with
        // matching metadata changes the local reference; a later local conflict reports saved_not_bound and never retries.
        private static Dictionary<string, object> SaveGlobalKey(Dictionary<string, object> payload,
            Func<string, Dictionary<string, object>, string, string, bool> bind = null, Func<Dictionary<string, object>> settingsSource = null)
        {
            string slot = ReadString(payload, "slot", ""), mode = ReadString(payload, "mode", "");
            string secret = ReadString(payload, "secret", "").Trim();
            payload.Remove("secret");
            if (!GlobalKeySlots.Contains(slot)) return GlobalKeyFail("invalid");
            if (!GlobalKeyWriteGate.Wait(TimeSpan.FromSeconds(20))) return GlobalKeyFail("busy");
            try
            {
                var settings = (settingsSource ?? LoadSettings)();
                string boundId = BoundKeyId(settings, slot);
                if (!payload.ContainsKey("expectedKeyId") || ReadString(payload, "expectedKeyId", "") != boundId) return GlobalKeyFail("conflict");
                var status = GlobalKeyCall("status", new Dictionary<string, object>());
                if (!ReadBool(status, "ok", false)) return GlobalKeyFail(ReadString(status, "outcome", "unavailable"));
                var target = GlobalKeyTarget(settings, slot, ReadDictionaryList(status, "providers"));
                if (target.Provider == null) return GlobalKeyFail("invalid_endpoint");
                string label = ReadString(payload, "label", "").Trim();
                if (label.Length == 0) label = "Reign - " + GlobalKeySlotName(slot);
                string keyId = ReadString(payload, "keyId", "");
                Dictionary<string, object> reply;
                if (mode == "create")
                {
                    if (secret.Length == 0 || IsMaskedSecretValue(secret)) return GlobalKeyFail("invalid");
                    reply = GlobalKeyCall("create", new Dictionary<string, object> { ["provider"] = target.Provider, ["label"] = label,
                        ["origin"] = target.Provider == "custom_openai" ? target.Origin : null, ["secret"] = secret });
                    var key = ReadDictionary(reply, "key");
                    keyId = ReadString(key, "key_id", "");
                    // A created key must be exactly what was asked for; anything else is never bound.
                    if (ReadBool(reply, "ok", false) && (ReadString(key, "provider", target.Provider) != target.Provider || ReadString(key, "origin", "") != target.Origin))
                        reply = GlobalKeyFail("unknown");
                }
                else if (mode == "rotate")
                {
                    if (boundId.Length == 0 || keyId != boundId || ReadString(GlobalKeyBinding(settings, slot), "origin", "") != target.Origin)
                        return GlobalKeyFail("conflict");
                    if (IsMaskedSecretValue(secret)) secret = "";
                    reply = GlobalKeyCall("edit", new Dictionary<string, object> { ["key_id"] = keyId, ["revision"] = ReadInt(payload, "revision", 0),
                        ["label"] = label, ["secret"] = secret });
                    if (ReadBool(reply, "ok", false) && ReadString(ReadDictionary(reply, "key"), "key_id", keyId) != keyId) reply = GlobalKeyFail("unknown");
                }
                else if (mode == "bind")
                {
                    var match = ReadDictionaryList(status, "keys").FirstOrDefault(k => ReadString(k, "key_id", "") == keyId);
                    reply = match == null ? GlobalKeyFail("missing")
                        : ReadString(match, "provider", "") != target.Provider || ReadString(match, "origin", "") != target.Origin ? GlobalKeyFail("wrong_provider")
                        : new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "bound" };
                }
                else return GlobalKeyFail("invalid");
                secret = null;
                string outcome = ReadString(reply, "outcome", "unavailable");
                LogOperational("global_keys.save", new Dictionary<string, object> { ["slot"] = slot, ["mode"] = mode, ["outcome"] = outcome });
                if (!ReadBool(reply, "ok", false) || !Guid.TryParse(keyId, out _))
                    return GlobalKeyFail(ReadBool(reply, "ok", false) ? "unknown" : outcome);
                if (!(bind ?? SetGlobalKeyBinding)(slot, new Dictionary<string, object> { ["keyId"] = keyId, ["provider"] = target.Provider, ["origin"] = target.Origin },
                        boundId, target.Origin))
                    return new Dictionary<string, object> { ["ok"] = false, ["outcome"] = "saved_not_bound", ["keyId"] = keyId };
                return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = outcome, ["keyId"] = keyId };
            }
            finally { GlobalKeyWriteGate.Release(); }
        }

        // Records (or removes) a reference only if the slot still has the expected binding and endpoint origin, then clears
        // the slot's legacy local value. The global key is never deleted.
        private static bool SetGlobalKeyBinding(string slot, Dictionary<string, object> binding, string expectedKeyId, string expectedOrigin)
        {
            try
            {
                lock (SettingsSaveGate)
                {
                    var settings = LoadSettingsCore();
                    if (BoundKeyId(settings, slot) != (expectedKeyId ?? "")
                        || (expectedOrigin != null && GlobalKeyTarget(settings, slot, null).Origin != expectedOrigin)) return false;
                    var bindings = new Dictionary<string, object>(ReadDictionary(settings, "globalKeyBindings") ?? new Dictionary<string, object>());
                    if (binding == null) bindings.Remove(slot); else bindings[slot] = binding;
                    settings["globalKeyBindings"] = bindings;
                    if (binding != null) settings[slot] = "";
                    settings["settingsRevision"] = DateTime.UtcNow.ToString("o");
                    Directory.CreateDirectory(DataDir);
                    WriteJsonObject(SettingsPath, settings);
                    PersistInstalledSettingsSecrets(settings, true);
                }
                InvalidateCodexThreads("provider_or_codex_settings_changed");
                return true;
            }
            catch (Exception ex)
            {
                LogOperational("global_keys.bind_failed", new Dictionary<string, object> { ["slot"] = slot, ["error"] = ex.GetType().Name });
                return false;
            }
        }

        // Removes the reference only; the global key stays in DwemerDistro for other products.
        private static Dictionary<string, object> UnbindGlobalKey(Dictionary<string, object> payload)
        {
            string slot = ReadString(payload, "slot", "");
            if (!GlobalKeySlots.Contains(slot)) return GlobalKeyFail("invalid");
            if (!GlobalKeyWriteGate.Wait(TimeSpan.FromSeconds(20))) return GlobalKeyFail("busy");
            try
            {
                string boundId = BoundKeyId(LoadSettings(), slot);
                if (payload.ContainsKey("expectedKeyId") && ReadString(payload, "expectedKeyId", "") != boundId) return GlobalKeyFail("conflict");
                if (boundId.Length == 0) return new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "unchanged" };
                return SetGlobalKeyBinding(slot, null, boundId, null) ? new Dictionary<string, object> { ["ok"] = true, ["outcome"] = "unbound" } : GlobalKeyFail("conflict");
            }
            finally { GlobalKeyWriteGate.Release(); }
        }

        // New provider secrets go only to Global API Keys. Legacy values stay readable; this refuses any new local value.
        private static string LocalProviderKeyWriteRefusal(Dictionary<string, object> incoming)
        {
            if (LegacyLocalKeyWritesForTests || incoming == null || !GlobalKeySlots.Any(slot => incoming.TryGetValue(slot, out object value)
                && !string.IsNullOrWhiteSpace(Convert.ToString(value)) && !IsMaskedSecretValue(Convert.ToString(value)))) return null;
            return GlobalKeysEnrolled()
                ? "Provider keys are stored in DwemerDistro Global API Keys. Use Save key beside the key; nothing was saved."
                : "Provider keys are stored only in DwemerDistro Global API Keys, which is not set up for Reign. Run scripts/Connect-GlobalKeys.ps1 once, then use Save key; nothing was saved.";
        }

        private static readonly HashSet<string> GlobalKeyApacheHosts = new HashSet<string>(StringComparer.Ordinal) { "127.0.0.1:8089", "localhost:8089", "[::1]:8089" };
        private static readonly HashSet<string> GlobalKeyDirectHosts = new HashSet<string>(StringComparer.Ordinal) { "127.0.0.1:5101", "localhost:5101", "[::1]:5101" };

        // reign-control.py's local-peer model: loopback plus the exact WSL default gateway (Windows localhost through NAT).
        private static bool GlobalKeyLocalPeer(string address)
        {
            if (!IPAddress.TryParse(address ?? "", out IPAddress ip)) return false;
            if (IPAddress.IsLoopback(ip)) return true;
            try
            {
                foreach (string line in File.ReadAllLines("/proc/net/route").Skip(1))
                {
                    var f = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 4 && f[1] == "00000000" && (Convert.ToInt32(f[3], 16) & 2) != 0
                        && new IPAddress(BitConverter.GetBytes(Convert.ToUInt32(f[2], 16))).Equals(ip)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        // Key mutations need the custom header (no cross-site preflight passes), an exact same-origin Origin, and a local
        // caller. Through Apache, the peer and host come from headers Apache always overwrites (X-Reign-Peer/X-Reign-Host),
        // so a browser cannot forge them; X-Forwarded-* are never trusted. Direct callers must be on loopback.
        private static bool GlobalKeyRequestAllowed(HttpRequest request, Func<string, bool> localPeer = null)
        {
            localPeer = localPeer ?? GlobalKeyLocalPeer;
            if (request.KeysHeader != "1" || !IPAddress.TryParse(request.RemoteAddress ?? "", out IPAddress direct) || !IPAddress.IsLoopback(direct)) return false;
            bool viaApache = !string.IsNullOrEmpty(request.ProxyPeer);
            string host = viaApache ? request.ProxyHost : request.Host;
            if (string.IsNullOrEmpty(host) || !(viaApache ? GlobalKeyApacheHosts : GlobalKeyDirectHosts).Contains(host)) return false;
            if (viaApache && !localPeer(request.ProxyPeer)) return false;
            return request.Origin == "http://" + host;
        }

        private static bool GlobalKeysEnrolled() => GlobalKeyTransportOverride != null || File.Exists(GlobalKeysConfigPath);
    }
}
