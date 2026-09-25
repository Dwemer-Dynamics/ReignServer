using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int ConversationUnverifiedInputLimit = 48000;
        private const int ConversationVerifiedInputLimit = 64000;
        private const int ConversationCapacitySafetyReserve = 1024;
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
                estimate <= 56000 ? 56000 : ConversationVerifiedInputLimit;

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
            bool verified = certificate != null && ReadString(certificate, "endpoint", "") == endpoint
                && ReadString(certificate, "model", "") == model && !string.IsNullOrWhiteSpace(ReadString(certificate, "source", ""))
                && DateTimeOffset.TryParse(ReadString(certificate, "validUntilUtc", ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expiry) && expiry > DateTimeOffset.UtcNow;
            int context = verified ? PositiveCapacityInteger(certificate, "contextTokens") : 0;
            verified = verified && context > 0;
            int output = PositiveCapacityInteger(body, "max_tokens");
            int maxOutput = verified ? PositiveCapacityInteger(certificate, "maxOutputTokens") : 0;
            int allowance = verified
                ? (int)Math.Min(ConversationVerifiedInputLimit, Math.Max(0L, (long)context - output - ConversationCapacitySafetyReserve))
                : ConversationUnverifiedInputLimit;
            return new Dictionary<string, object>
            {
                ["routeKey"] = routeKey, ["capacityVerified"] = verified, ["verifiedContextTokens"] = verified ? context : 0,
                ["inputAllowance"] = allowance, ["outputReserve"] = output, ["safetyReserve"] = ConversationCapacitySafetyReserve,
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
