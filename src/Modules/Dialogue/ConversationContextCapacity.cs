using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int ConversationUnverifiedInputLimit = 48000;
        private const int ConversationCapacitySafetyReserve = 1024;
        private const int CodexConversationRuntimeReserve = 16384;
        private static readonly AsyncLocal<string> CodexCapacityMetadataOverride = new AsyncLocal<string>();
        private static readonly object CodexCapacityMetadataGate = new object();
        private static string CodexCapacityRefreshHome = "";
        private static DateTimeOffset CodexCapacityRefreshAfter;
        private const string NanoConversationModelsUrl = "https://nano-gpt.com/api/v1/models?detailed=true";
        private static readonly HttpClient ConversationCapacityHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(5),
            MaxResponseContentBufferSize = 8 * 1024 * 1024
        };
        private static readonly ConversationCapacityCatalog ConversationModelCapacities =
            new ConversationCapacityCatalog(FetchConversationModelCatalog, () => DateTimeOffset.UtcNow);

        // The source is public metadata only: no credentials, prompts or generation.
        // Verification fixtures always inject metadata; production discovery is disabled
        // inside the Verification Lab's isolated campaign scope.
        private static string FetchConversationModelCatalog()
        {
            if (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1") return null;
            return ConversationCapacityHttp.GetStringAsync(NanoConversationModelsUrl).GetAwaiter().GetResult();
        }

        private static int PositiveCapacityInteger(Dictionary<string, object> source, string key)
        {
            return int.TryParse(ReadString(source, key, ""), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                && value > 0 ? value : 0;
        }

        private static Dictionary<string, object> ParseCodexCapacityCertificate(string json, string model, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(json) || json.Length > 8 * 1024 * 1024) return null;
            Dictionary<string, object> metadata;
            try { metadata = Json.Deserialize<Dictionary<string, object>>(json); }
            catch (System.Text.Json.JsonException) { return null; }
            if (!DateTimeOffset.TryParse(ReadString(metadata, "fetched_at", ""), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fetched) || fetched > now.AddMinutes(5) || fetched.AddHours(6) <= now
                || string.IsNullOrWhiteSpace(ReadString(metadata, "client_version", ""))) return null;
            var matches = ReadDictionaryList(metadata, "models").Where(row => ReadString(row, "slug", "") == model).ToList();
            if (matches.Count != 1) return null;
            var row = matches[0];
            int reported = PositiveCapacityInteger(row, "context_window");
            int percent = row.ContainsKey("effective_context_window_percent") ? PositiveCapacityInteger(row, "effective_context_window_percent") : 95;
            if (reported == 0 || percent < 1 || percent > 100) return null;
            int effective = (int)((long)reported * percent / 100);
            var capacity = TestDict("model", model, "contextTokens", effective, "reportedContextTokens", reported,
                "effectiveContextPercent", percent, "clientVersion", ReadString(metadata, "client_version", ""));
            capacity["endpoint"] = "codex-app-server://local";
            capacity["source"] = "codex-runtime:models_cache.json";
            capacity["sourceHash"] = PromptHash(CanonicalJson(capacity));
            capacity["validUntilUtc"] = fetched.AddHours(6).ToString("o");
            capacity["runtimeManagedContext"] = true;
            return capacity;
        }

        private static Dictionary<string, object> ReadCodexCapacityCertificate(Dictionary<string, object> settings, string model)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (CodexCapacityMetadataOverride.Value != null)
                return ParseCodexCapacityCertificate(CodexCapacityMetadataOverride.Value, model, now);
            // Isolated verification must never read the signed-in user's model cache,
            // start their runtime, or discover models. Fixtures inject public metadata.
            if (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1") return null;
            string home = ReadString(settings, "codexHome", "").Trim();
            if (home.Length == 0) home = Environment.GetEnvironmentVariable("CODEX_HOME") ?? "";
            if (home.Length == 0) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string path = Path.Combine(home, "models_cache.json");
            string ReadMetadata()
            {
                try { return new FileInfo(path) is { Exists: true, Length: > 0 and <= 8388608 } ? File.ReadAllText(path) : null; }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return null; }
            }
            lock (CodexCapacityMetadataGate)
            {
                var certificate = ParseCodexCapacityCertificate(ReadMetadata(), model, now);
                if (certificate != null) return certificate;
                if (CodexCapacityRefreshHome == home && now < CodexCapacityRefreshAfter) return null;
                CodexCapacityRefreshHome = home;
                CodexCapacityRefreshAfter = now.AddSeconds(30);
                // A cold/expired cache needs metadata discovery before the very first
                // large prompt. This bounded RPC never starts an inference turn.
                try
                {
                    EnsureCodexAppServer(settings);
                    CodexRpc("model/list", TestDict("includeHidden", true), Math.Min(15000, CodexRpcTimeout(settings)));
                }
                catch (Exception ex) { LogOperational("codex.capacity_discovery_unavailable", TestDict("error", LimitText(ex.Message, 300))); }
                return ParseCodexCapacityCertificate(ReadMetadata(), model, DateTimeOffset.UtcNow);
            }
        }

        private sealed class ConversationCapacityCatalog
        {
            private readonly object gate = new object();
            private readonly Func<string> fetch;
            private readonly Func<DateTimeOffset> clock;
            private Dictionary<string, Dictionary<string, object>> certificates = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            private DateTimeOffset refreshAfter = DateTimeOffset.MinValue;

            internal ConversationCapacityCatalog(Func<string> fetch, Func<DateTimeOffset> clock)
            {
                this.fetch = fetch;
                this.clock = clock;
            }

            internal Dictionary<string, object> Read(string model)
            {
                lock (gate)
                {
                    DateTimeOffset now = clock();
                    if (now >= refreshAfter)
                    {
                        // Discard expired metadata even if refresh fails. A short negative
                        // cache bounds retries by concurrent speakers during an outage.
                        certificates = new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
                        refreshAfter = now.AddSeconds(30);
                        try
                        {
                            string json = fetch();
                            if (string.IsNullOrWhiteSpace(json)) return null;
                            var catalog = Json.Deserialize<Dictionary<string, object>>(json);
                            var rows = ReadDictionaryList(catalog, "data");
                            if (rows.Count == 0) return null;
                            string hash = PromptHash(json);
                            DateTimeOffset validUntil = now.AddHours(6);
                            foreach (var group in rows.GroupBy(row => ReadString(row, "id", ""), StringComparer.Ordinal))
                            {
                                if (string.IsNullOrWhiteSpace(group.Key) || group.Count() != 1) continue;
                                var row = group.Single();
                                int context = PositiveCapacityInteger(row, "context_length");
                                int output = PositiveCapacityInteger(row, "max_output_tokens");
                                if (context == 0 || output == 0 || output > context) continue;
                                certificates[group.Key] = new Dictionary<string, object>
                                {
                                    ["endpoint"] = NanoGptChatCompletionsUrl, ["model"] = group.Key,
                                    ["contextTokens"] = context, ["maxOutputTokens"] = output,
                                    ["source"] = NanoConversationModelsUrl, ["sourceHash"] = hash,
                                    ["checkedAtUtc"] = now.ToString("o"), ["validUntilUtc"] = validUntil.ToString("o")
                                };
                            }
                            refreshAfter = validUntil;
                        }
                        catch (Exception ex) when (ex is HttpRequestException || ex is System.Threading.Tasks.TaskCanceledException
                            || ex is System.Text.Json.JsonException || ex is ArgumentException || ex is InvalidOperationException)
                        {
                            // Unknown capacity retains the conservative allowance.
                        }
                    }
                    return certificates.TryGetValue(model ?? "", out var certificate)
                        ? new Dictionary<string, object>(certificate) : null;
                }
            }
        }

        private static Dictionary<string, object> ConversationCapacityRouting(Dictionary<string, object> body)
        {
            var routing = new Dictionary<string, object>();
            foreach (string key in new[] { "provider", "routing", "stickyprovider", "stickyProvider", "models", "route" })
                if (body != null && body.ContainsKey(key)) routing[key] = body[key];
            return routing;
        }

        private static int SelectConversationInputStep(int estimate) =>
            estimate <= 32000 ? 32000 : estimate <= 48000 ? 48000 :
                (int)Math.Min(int.MaxValue / 3, ((long)estimate + 7999) / 8000 * 8000);

        private static int ConversationPromptHardCharacterLimit(Dictionary<string, object> settings,
            Dictionary<string, object> preflight)
        {
            int configured = ReadInt(settings, "promptHardCharacterLimit", 240000);
            // The legacy character limit is a fallback, not a second model capacity.
            // Expansion is allowed only after the actual serialized request passed
            // exact-route, output, byte-bound and required-content validation.
            if (!ReadBool(preflight, "capacityVerified", false) || !ReadBool(preflight, "fits", false)) return configured;
            if (ReadBool(preflight, "runtimeManagedContext", false))
                return (int)Math.Min(int.MaxValue, Math.Max(configured, (long)ReadInt(preflight, "inputAllowance", 0) * 3));
            long available = (long)ReadInt(preflight, "verifiedContextTokens", 0)
                - ReadInt(preflight, "outputReserve", 0) - ReadInt(preflight, "safetyReserve", ConversationCapacitySafetyReserve) - 128;
            return (int)Math.Min(int.MaxValue, Math.Max(configured, available));
        }

        private static Dictionary<string, object> ResolveConversationCapacity(Dictionary<string, object> settings,
            Dictionary<string, object> body, string endpoint, string model, ConversationCapacityCatalog catalog = null)
        {
            var routing = ConversationCapacityRouting(body);
            string provider = NormalizeLlmProvider(ReadString(settings, "llmProvider", ""));
            string routeKey = provider + ":" + model + ":" + PromptHash(endpoint + "|" + CanonicalJson(routing)).Substring(0, 20);
            var certificate = ReadDictionary(ReadDictionary(settings, "verifiedConversationContextWindows"), routeKey);
            // An explicit certificate remains authoritative, including expiry. Public
            // model metadata describes only this provider's default exact model route.
            if (certificate == null && provider == NanoGptProvider && endpoint == NanoGptChatCompletionsUrl
                && routing.Count == 0 && !string.IsNullOrWhiteSpace(model) && !model.Contains(':'))
                certificate = (catalog ?? ConversationModelCapacities).Read(model);
            if (certificate == null && provider == CodexSubscriptionProvider && endpoint == "codex-app-server://local"
                && routing.Count == 0 && !string.IsNullOrWhiteSpace(model)) certificate = ReadCodexCapacityCertificate(settings, model);
            bool verified = certificate != null && ReadString(certificate, "endpoint", "") == endpoint
                && ReadString(certificate, "model", "") == model && !string.IsNullOrWhiteSpace(ReadString(certificate, "source", ""))
                && DateTimeOffset.TryParse(ReadString(certificate, "validUntilUtc", ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expiry) && expiry > DateTimeOffset.UtcNow;
            int context = verified ? PositiveCapacityInteger(certificate, "contextTokens") : 0;
            verified = verified && context > 0;
            int output = PositiveCapacityInteger(body, "max_tokens");
            bool runtimeManaged = verified && provider == CodexSubscriptionProvider && endpoint == "codex-app-server://local";
            int runtimeReserve = runtimeManaged ? CodexConversationRuntimeReserve : 0;
            int maxOutput = verified ? PositiveCapacityInteger(certificate, "maxOutputTokens") : 0;
            int allowance = verified
                ? (int)Math.Min(int.MaxValue / 3, Math.Max(0L, (long)context - output - ConversationCapacitySafetyReserve - runtimeReserve))
                : ConversationUnverifiedInputLimit;
            return new Dictionary<string, object>
            {
                ["routeKey"] = routeKey, ["capacityVerified"] = verified, ["verifiedContextTokens"] = verified ? context : 0,
                ["inputAllowance"] = allowance, ["outputReserve"] = output, ["safetyReserve"] = ConversationCapacitySafetyReserve,
                ["runtimeManagedContext"] = runtimeManaged, ["runtimeReserve"] = runtimeReserve,
                ["outputFits"] = output > 0 && (!verified || maxOutput == 0 || output <= maxOutput),
                ["maxOutputTokens"] = maxOutput, ["capacitySource"] = verified ? ReadString(certificate, "source", "") : "",
                ["capacitySourceHash"] = verified ? ReadString(certificate, "sourceHash", "") : "",
                ["capacityValidUntilUtc"] = verified ? ReadString(certificate, "validUntilUtc", "") : ""
            };
        }

        // Dialogue/event builders use the same model, endpoint and adapters as their
        // server-owned LLM request. Final preflight independently rechecks the actual
        // body, so later overrides or metadata expiry can never inherit permission.
        private static Dictionary<string, object> ResolveConversationPromptCapacity(Dictionary<string, object> payload,
            string requestType, Dictionary<string, object> settings = null, ConversationCapacityCatalog catalog = null)
        {
            settings = settings ?? LoadSettings();
            string model = ModelForRequest(settings, requestType);
            string endpoint = UsesCodexSubscription(settings) ? "codex-app-server://local" : LlmApiUrl(settings);
            int output = ReadInt(payload, "continuityOutputReserve", requestType == "social_event"
                ? StructuredEventResponseMaxTokens(settings) : StructuredDialogueResponseMaxTokens(settings));
            var request = new Dictionary<string, object> { ["maxTokens"] = output, ["requestType"] = requestType };
            var body = BuildChatRequestBody(settings, request, new List<Dictionary<string, object>>(), model);
            ConfigurePromptCacheRouting(settings, request, body, endpoint, model, requestType);
            ConfigureReasoningRouting(settings, request, body, endpoint, model, requestType);
            return ResolveConversationCapacity(settings, body, endpoint, ReadString(body, "model", model), catalog);
        }
    }
}
