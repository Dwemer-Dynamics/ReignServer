using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static void EnsurePromptCacheLayoutMigration()
        {
            Directory.CreateDirectory(Program.PromptsDir);
            RetireLegacyPromptTemplates(Program.PromptsDir);

            Dictionary<string, string> defaults = Program.DefaultPromptTemplates();
            foreach (KeyValuePair<string, HashSet<string>> pair in VersionTwoReasoningPromptHashes)
            {
                string path = Program.PromptPath(pair.Key);
                if (!File.Exists(path)) continue;
                string current = File.ReadAllText(path, Encoding.UTF8);
                string currentHash = PromptHash(current);
                string updated = Program.DefaultPromptText(pair.Key, defaults);
                if (string.Equals(currentHash, PromptHash(updated), StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value.Contains(currentHash))
                {
                    File.WriteAllText(path, updated, Encoding.UTF8);
                    continue;
                }
                string backup = path + ".v2.custom.bak";
                if (!File.Exists(backup)) File.Copy(path, backup, false);
            }
        }

        private static Dictionary<string, object> PromptLayoutMigrationStatus()
        {
            List<string> custom = UnreviewedRetiredPromptFiles(Program.PromptsDir);
            List<string> customReasoning = VersionTwoReasoningPromptHashes.Keys.Where(name =>
            {
                string path = Program.PromptPath(name);
                if (!File.Exists(path)) return false;
                string currentHash = PromptHash(File.ReadAllText(path, Encoding.UTF8));
                string defaultHash = PromptHash(Program.DefaultPromptText(name, Program.DefaultPromptTemplates()));
                return !string.Equals(currentHash, defaultHash, StringComparison.OrdinalIgnoreCase) && !VersionTwoReasoningPromptHashes[name].Contains(currentHash);
            }).ToList();
            return new Dictionary<string, object>
            {
                ["layoutVersion"] = PromptLayoutVersion,
                ["customLegacyTemplates"] = custom,
                ["customReasoningTemplates"] = customReasoning,
                ["migrationWarning"] = custom.Count == 0 && customReasoning.Count == 0
                    ? ""
                    : "Customized templates were preserved as backups. Review any custom reasoning template so it requests decisionBrief and never requests raw chain-of-thought."
            };
        }

        internal static void NormalizePromptCacheSettings(Dictionary<string, object> settings)
        {
            if (settings == null) return;
            // NanoGPT's explicit `caching: true` route can select a pay-as-you-go
            // cache provider even when the model is covered by a subscription.
            // Reign relies only on NanoGPT/provider-native implicit caching so no
            // request can silently leave subscription coverage.
            settings["promptCacheMode"] = "implicit";
            settings["promptCacheFallbackCooldownSeconds"] = Math.Max(30, Math.Min(3600, ReadInt(settings, "promptCacheFallbackCooldownSeconds", 300)));
        }

        internal static void NormalizeReasoningSettings(Dictionary<string, object> settings)
        {
            if (settings == null) return;
            string mode = ReadString(settings, "reasoningMode", "selective").Trim().ToLowerInvariant();
            if (mode != "off" && mode != "selective" && mode != "all") mode = "selective";
            string effort = ReadString(settings, "reasoningEffort", "medium").Trim().ToLowerInvariant();
            if (!(new[] { "minimal", "low", "medium", "high", "xhigh" }).Contains(effort)) effort = "medium";
            settings["reasoningMode"] = mode;
            settings["reasoningEffort"] = effort;
            settings["reasoningFallbackCooldownSeconds"] = Math.Max(30, Math.Min(3600, ReadInt(settings, "reasoningFallbackCooldownSeconds", 300)));
        }

        internal static string EffectiveReasoningEffort(Dictionary<string, object> settings, Dictionary<string, object> payload, string requestType)
        {
            NormalizeReasoningSettings(settings);
            if (ReadBool(payload, "reasoningDisabled", false)) return "none";
            string mode = ReadString(settings, "reasoningMode", "selective");
            if (mode == "off") return "none";
            string normalizedType = (requestType ?? "").Trim().ToLowerInvariant();
            if (mode == "selective" && !SelectiveReasoningRequestTypes.Contains(normalizedType)) return "none";
            if (normalizedType == "dialogue_answer_support") return "high";
            string effort = ReadString(settings, "reasoningEffort", "medium");
            if (normalizedType == "memory" && (effort == "medium" || effort == "high" || effort == "xhigh")) return "low";
            return effort;
        }

        internal static Dictionary<string, object> ConfigureReasoningRouting(
            Dictionary<string, object> settings,
            Dictionary<string, object> payload,
            Dictionary<string, object> requestBody,
            string apiUrl,
            string model,
            string requestType)
        {
            string requestedEffort = EffectiveReasoningEffort(settings, payload, requestType);
            bool mandatoryReasoning = RequiresGlm53Reasoning(apiUrl, model);
            string effort = mandatoryReasoning ? Glm53ReasoningEffort(requestedEffort) : requestedEffort;
            bool requested = effort != "none";
            bool nano = IsNanoGptApiUrl(apiUrl);
            string key = ReasoningRouteKey(apiUrl, model);
            DateTime cooldownUntil = ReasoningFallbackCooldownUntil(key);
            bool coolingDown = cooldownUntil > DateTime.UtcNow;
            bool openrouter = Program.IsOpenRouterApiUrl(apiUrl);
            bool ollama = Program.IsOllamaCloudApiUrl(apiUrl);
            bool applied = (nano || openrouter || ollama) && (!coolingDown || mandatoryReasoning);
            if (mandatoryReasoning)
            {
                // Never let stale extra controls or a generic fallback cooldown
                // disable an always-thinking model or default it to max effort.
                requestBody.Remove("reasoning_effort");
                requestBody.Remove("thinking");
                requestBody.Remove("enable_thinking");
                requestBody.Remove("thinking_budget");
            }
            if (!requested && (nano || openrouter || ollama))
            {
                // NanoGPT's top-level effort overrides the nested control.
                // Neither an extra field nor hidden-output mode may turn a
                // deliberately disabled checker back into a reasoning call.
                requestBody.Remove("reasoning_effort");
                requestBody.Remove("reasoning");
            }
            if (ollama)
            {
                requestBody.Remove("reasoning");
                requestBody.Remove("reasoning_effort");
                if (applied) requestBody["reasoning_effort"] = effort;
            }
            else if (applied)
            {
                requestBody["reasoning"] = new Dictionary<string, object>
                {
                    ["effort"] = effort,
                    ["exclude"] = true
                };
            }

            return new Dictionary<string, object>
            {
                ["mode"] = ReadString(settings, "reasoningMode", "selective"),
                ["requestType"] = requestType ?? "",
                ["requested"] = requested,
                ["requestedEffort"] = requestedEffort,
                ["modelRequiresReasoning"] = mandatoryReasoning,
                ["effort"] = effort,
                ["nanoGpt"] = nano,
                ["openRouter"] = Program.IsOpenRouterApiUrl(apiUrl),
                ["ollamaCloud"] = ollama,
                ["providerControlsApplied"] = applied,
                ["reasoningExcluded"] = true,
                ["cooldownActive"] = coolingDown,
                ["cooldownUntilUtc"] = cooldownUntil == DateTime.MinValue ? "" : cooldownUntil.ToString("o"),
                ["fallbackUsed"] = false,
                ["attemptCount"] = 1
            };
        }

        internal static void ApplyPrivateReasoningPolicyToMessages(List<Dictionary<string, object>> messages)
        {
            if (messages == null || messages.Count == 0) return;
            Dictionary<string, object> system = messages.FirstOrDefault(x => ReadString(x, "role", "").Equals("system", StringComparison.OrdinalIgnoreCase));
            if (system == null)
            {
                system = new Dictionary<string, object> { ["role"] = "system", ["content"] = "" };
                messages.Insert(0, system);
            }
            string current = ReadString(system, "content", "");
            const string policy = "PRIVATE REASONING POLICY: Deliberate privately before producing the answer. Check supplied facts, relevant motives, constraints, uncertainty, and executable-action requirements. Never output chain-of-thought, internalThoughts, reasoning, reasoning_content, or <think> blocks. If the required schema includes decisionBrief, return only that compact factor-and-outcome summary; otherwise return only the concise reasons or decision factors required by the schema.";
            if (current.IndexOf("PRIVATE REASONING POLICY:", StringComparison.OrdinalIgnoreCase) < 0)
            {
                system["content"] = string.IsNullOrWhiteSpace(current) ? policy : current.TrimEnd() + "\n\n" + policy;
            }
        }

        internal static string ReasoningRouteKey(string apiUrl, string model)
        {
            return (apiUrl ?? "") + "|" + (model ?? "");
        }

        internal static DateTime ReasoningFallbackCooldownUntil(string key)
        {
            lock (PromptCacheStateLock)
            {
                return ReasoningFallbackCooldowns.TryGetValue(key, out DateTime until) ? until : DateTime.MinValue;
            }
        }

        internal static void StartReasoningFallbackCooldown(Dictionary<string, object> settings, string apiUrl, string model)
        {
            int seconds = Math.Max(30, Math.Min(3600, ReadInt(settings, "reasoningFallbackCooldownSeconds", 300)));
            lock (PromptCacheStateLock) ReasoningFallbackCooldowns[ReasoningRouteKey(apiUrl, model)] = DateTime.UtcNow.AddSeconds(seconds);
        }

        internal static bool ShouldRetryWithoutReasoning(Dictionary<string, object> routing, string error)
        {
            if (!ReadBool(routing, "providerControlsApplied", false)
                || ReadBool(routing, "modelRequiresReasoning", false)) return false;
            string lower = (error ?? "").ToLowerInvariant();
            if (lower.Contains("invalid_api_key") || lower.Contains("unauthorized") || lower.Contains("rate_limit") || lower.Contains("timeout") || lower.Contains("context_length")) return false;
            bool mentionsReasoning = lower.Contains("reasoning") || lower.Contains("reasoning_effort") || lower.Contains("thinking");
            bool rejectedParameter = lower.Contains("unsupported") || lower.Contains("unrecognized") || lower.Contains("unknown parameter")
                || lower.Contains("invalid parameter") || lower.Contains("additional properties") || lower.Contains("extra fields")
                || lower.Contains("not permitted") || lower.Contains("not allowed");
            return mentionsReasoning && rejectedParameter;
        }

        internal static Dictionary<string, object> ReasoningPolicyStatus(Dictionary<string, object> settings = null)
        {
            settings = settings ?? Program.LoadSettings();
            NormalizeReasoningSettings(settings);
            return new Dictionary<string, object>
            {
                ["mode"] = ReadString(settings, "reasoningMode", "selective"),
                ["effort"] = ReadString(settings, "reasoningEffort", "medium"),
                ["reasoningOutputStored"] = false,
                ["decisionBriefStored"] = true,
                ["selectiveRequestTypes"] = SelectiveReasoningRequestTypes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                ["alwaysDirectRequestTypes"] = new List<string> { "action_router", "action_repair", "format_repair", "diagnostics", "prompt_cache_probe" },
                ["fallbackCooldownSeconds"] = ReadInt(settings, "reasoningFallbackCooldownSeconds", 300)
            };
        }

        internal static Dictionary<string, object> SanitizeLlmResponseForStorage(Dictionary<string, object> raw)
        {
            return WithoutConversationReferenceMetadata(SanitizeReasoningValue(raw, "")) as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static object SanitizeReasoningValue(object value, string parentKey)
        {
            if (value == null) return null;
            if (value is Dictionary<string, object> dictionary)
            {
                Dictionary<string, object> clean = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, object> pair in dictionary)
                {
                    if (IsPrivateReasoningKey(pair.Key)) continue;
                    clean[pair.Key] = SanitizeReasoningValue(pair.Value, pair.Key);
                }
                return clean;
            }
            if (value is IDictionary genericDictionary)
            {
                Dictionary<string, object> clean = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry pair in genericDictionary)
                {
                    string key = Convert.ToString(pair.Key, CultureInfo.InvariantCulture) ?? "";
                    if (IsPrivateReasoningKey(key)) continue;
                    clean[key] = SanitizeReasoningValue(pair.Value, key);
                }
                return clean;
            }
            if (value is ArrayList array)
            {
                ArrayList clean = new ArrayList();
                foreach (object item in array) clean.Add(SanitizeReasoningValue(item, parentKey));
                return clean;
            }
            if (value is string text)
            {
                return parentKey.Equals("content", StringComparison.OrdinalIgnoreCase) ? SanitizeReasoningContent(text) : text;
            }
            return value;
        }

        private static bool IsPrivateReasoningKey(string key)
        {
            string normalized = (key ?? "").Replace("_", "").Replace("-", "").ToLowerInvariant();
            return normalized == "reasoning" || normalized == "reasoningcontent" || normalized == "internalthoughts" || normalized == "thoughts";
        }

        internal static string SanitizeReasoningContent(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text ?? "";
            // Only a provider preamble is removable. Literal tags inside a JSON
            // string belong to the authored reply and cannot be regex-stripped.
            string clean = text.Trim();
            while (clean.StartsWith("<think>", StringComparison.OrdinalIgnoreCase))
            {
                int end = clean.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (end < 0) return "";
                clean = clean.Substring(end + "</think>".Length).Trim();
            }
            Dictionary<string, object> parsed = TryParseStrictJsonObject(clean);
            if (parsed == null) return clean;
            RemovePrivateReasoningFields(parsed);
            return Json.Serialize(parsed);
        }

        internal static void RemovePrivateReasoningFields(Dictionary<string, object> parsed)
        {
            if (parsed == null) return;
            foreach (string key in parsed.Keys.Where(IsPrivateReasoningKey).ToList()) parsed.Remove(key);
        }

        internal static Dictionary<string, object> NormalizeDecisionBrief(Dictionary<string, object> parsed, string intent, Dictionary<string, object> actionGate)
        {
            Dictionary<string, object> source = ReadDictionary(parsed, "decisionBrief") ?? ReadDictionary(parsed, "decision_brief") ?? new Dictionary<string, object>();
            List<string> facts = ReadStringList(source, "facts").Where(x => !string.IsNullOrWhiteSpace(x)).Take(5).Select(x => Program.LimitText(x, 240)).ToList();
            List<string> goals = ReadStringList(source, "goals").Where(x => !string.IsNullOrWhiteSpace(x)).Take(3).Select(x => Program.LimitText(x, 240)).ToList();
            List<string> constraints = ReadStringList(source, "constraints").Where(x => !string.IsNullOrWhiteSpace(x)).Take(4).Select(x => Program.LimitText(x, 240)).ToList();
            List<string> activeDomains = ReadStringList(source, "activeDomains").Concat(ReadStringList(source, "active_domains")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(3).ToList();
            List<string> evidenceKeys = ReadStringList(source, "appliedEvidenceKeys").Concat(ReadStringList(source, "applied_evidence_keys")).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
            string decision = Program.LimitText(FirstNonEmpty(ReadString(source, "decision", ""), intent, ReadString(actionGate, "reason", "")), 360);
            double confidence = Program.ClampDouble(ReadDouble(source, "confidence", ReadDouble(actionGate, "confidence", 0.5d)), 0d, 1d);
            string courtTactic = Program.LimitText(ReadFirstString(source, "courtTactic", "court_tactic"), 40).ToLowerInvariant();
            return new Dictionary<string, object>
            {
                ["facts"] = facts,
                ["goals"] = goals,
                ["constraints"] = constraints,
                ["decision"] = decision,
                ["confidence"] = confidence,
                ["activeDomains"] = activeDomains,
                ["appliedEvidenceKeys"] = evidenceKeys,
                ["postureAlignment"] = Program.LimitText(ReadFirstString(source, "postureAlignment", "posture_alignment"), 80),
                ["courtTactic"] = courtTactic,
                ["courtCharacterCell"] = Program.LimitText(ReadFirstString(source, "courtCharacterCell", "court_character_cell"), 40)
            };
        }

        private static bool IsPromptCacheEligibleRequestType(string requestType)
        {
            return new[] { "dialogue", "social_event", "party_chat", "generated_wilderness_event", "correspondence", "action_planner", "action_router", "action_repair", "prompt_cache_probe" }
                .Contains(requestType ?? "", StringComparer.OrdinalIgnoreCase);
        }

        internal static Dictionary<string, object> ConfigurePromptCacheRouting(Dictionary<string, object> settings, Dictionary<string, object> payload, Dictionary<string, object> requestBody, string apiUrl, string model, string requestType)
        {
            NormalizePromptCacheSettings(settings);
            string mode = ReadString(settings, "promptCacheMode", "implicit");
            bool eligible = ReadBool(payload, "promptCacheEligible", IsPromptCacheEligibleRequestType(requestType));
            bool nano = IsNanoGptApiUrl(apiUrl);
            bool coolingDown = false;
            bool routed = false;
            requestBody.Remove("caching");
            requestBody.Remove("stickyprovider");
            requestBody.Remove("stickyProvider");
            if (nano) requestBody["model"] = RemoveNanoGptExplicitCacheSuffixes(ReadString(requestBody, "model", model));
            return new Dictionary<string, object>
            {
                ["mode"] = mode,
                ["eligible"] = eligible,
                ["nanoGpt"] = nano,
                ["openRouter"] = Program.IsOpenRouterApiUrl(apiUrl),
                ["cacheCapableRouteRequested"] = routed,
                ["subscriptionSafeImplicit"] = nano,
                ["cooldownActive"] = coolingDown,
                ["cooldownUntilUtc"] = "",
                ["fallbackUsed"] = false,
                ["attemptCount"] = 1,
                ["prefix"] = ReadDictionary(payload, "promptEnvelope") ?? new Dictionary<string, object>()
            };
        }

        internal static bool IsNanoGptApiUrl(string apiUrl)
        {
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out Uri uri)) return false;
            return uri.Host.Equals("nano-gpt.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".nano-gpt.com", StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveNanoGptExplicitCacheSuffixes(string model)
        {
            string[] segments = (model ?? "").Split(new[] { ':' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(":", segments.Where(segment =>
                !segment.Equals("cache", StringComparison.OrdinalIgnoreCase)
                && !segment.Equals("cached", StringComparison.OrdinalIgnoreCase)
                && !segment.Equals("caching", StringComparison.OrdinalIgnoreCase)));
        }

        internal static string PromptCacheRouteKey(string apiUrl, string model)
        {
            return (apiUrl ?? "") + "|" + (model ?? "");
        }

        internal static DateTime PromptCacheCooldownUntil(string key)
        {
            lock (PromptCacheStateLock)
            {
                return PromptCacheCooldowns.TryGetValue(key, out DateTime until) ? until : DateTime.MinValue;
            }
        }

        internal static void StartPromptCacheCooldown(Dictionary<string, object> settings, string apiUrl, string model)
        {
            int seconds = Math.Max(30, Math.Min(3600, ReadInt(settings, "promptCacheFallbackCooldownSeconds", 300)));
            lock (PromptCacheStateLock) PromptCacheCooldowns[PromptCacheRouteKey(apiUrl, model)] = DateTime.UtcNow.AddSeconds(seconds);
        }

        internal static bool ShouldRetryWithoutCache(Dictionary<string, object> routing, string error)
        {
            if (!ReadBool(routing, "cacheCapableRouteRequested", false) || !ReadString(routing, "mode", "").Equals("prefer", StringComparison.OrdinalIgnoreCase)) return false;
            string lower = (error ?? "").ToLowerInvariant();
            if (lower.Contains("invalid_api_key") || lower.Contains("unauthorized") || lower.Contains("rate_limit") || lower.Contains("timeout") || lower.Contains("context_length")) return false;
            bool explicitCacheRouteFailure = lower.Contains("no_cache_capable_provider")
                || lower.Contains("cache_provider_unavailable")
                || lower.Contains("no_fallback_available")
                || ((lower.Contains("cache-capable") || lower.Contains("cache capable")) && (lower.Contains("unavailable") || lower.Contains("no usable") || lower.Contains("not available")));
            if (explicitCacheRouteFailure) return true;
            if (lower.Contains("invalid_request_error")) return false;
            return false;
        }

        internal static Dictionary<string, object> BuildPromptCacheDiagnostics(Dictionary<string, object> raw, Dictionary<string, object> routing, long durationMs, string requestType, string model, string error, bool recordAggregate = true)
        {
            Dictionary<string, object> usage = ReadDictionary(raw, "usage") ?? new Dictionary<string, object>();
            Dictionary<string, object> details = ReadDictionary(usage, "prompt_tokens_details") ?? new Dictionary<string, object>();
            Dictionary<string, object> pricing = ReadDictionary(raw, "x_nanogpt_pricing") ?? new Dictionary<string, object>();
            bool promptReported = usage.ContainsKey("prompt_tokens") || usage.ContainsKey("input_tokens");
            bool completionReported = usage.ContainsKey("completion_tokens") || usage.ContainsKey("output_tokens");
            bool totalReported = usage.ContainsKey("total_tokens");
            bool cacheReadReported = usage.ContainsKey("cache_read_input_tokens") || pricing.ContainsKey("cacheReadInputTokens");
            bool cacheWriteReported = usage.ContainsKey("cache_creation_input_tokens") || pricing.ContainsKey("cacheCreationInputTokens");
            bool cachedReported = details.ContainsKey("cached_tokens") || cacheReadReported;
            long promptTokens = ReadLong(usage, "prompt_tokens", ReadLong(usage, "input_tokens", 0));
            long completionTokens = ReadLong(usage, "completion_tokens", ReadLong(usage, "output_tokens", 0));
            long totalTokens = ReadLong(usage, "total_tokens", promptTokens + completionTokens);
            long nestedCached = ReadLong(details, "cached_tokens", 0);
            long cacheRead = Math.Max(ReadLong(usage, "cache_read_input_tokens", 0), ReadLong(pricing, "cacheReadInputTokens", 0));
            long cacheWrite = Math.Max(ReadLong(usage, "cache_creation_input_tokens", 0), ReadLong(pricing, "cacheCreationInputTokens", 0));
            long cached = Math.Max(nestedCached, cacheRead);
            bool reported = promptReported || completionReported || totalReported || cachedReported || cacheWriteReported;
            Dictionary<string, object> diagnostics = new Dictionary<string, object>(routing ?? new Dictionary<string, object>(), StringComparer.OrdinalIgnoreCase)
            {
                ["usageReported"] = reported,
                ["promptTokens"] = promptReported ? (object)promptTokens : null,
                ["completionTokens"] = completionReported ? (object)completionTokens : null,
                ["totalTokens"] = totalReported || (promptReported && completionReported) ? (object)totalTokens : null,
                ["cachedTokens"] = cachedReported ? (object)cached : null,
                ["cacheReadTokens"] = cacheReadReported ? (object)cacheRead : null,
                ["cacheWriteTokens"] = cacheWriteReported ? (object)cacheWrite : null,
                ["cacheHit"] = cachedReported ? (object)(cached > 0) : null,
                ["cachedTokenRatio"] = promptReported && cachedReported && promptTokens > 0 ? (object)Math.Round((double)cached / promptTokens, 4) : null,
                ["tokenSavings"] = cachedReported ? (object)cached : null,
                ["pricingReported"] = pricing.Count > 0,
                ["pricingMetadata"] = pricing.Count > 0 ? (object)pricing : null,
                ["durationMs"] = durationMs,
                ["lastError"] = error ?? ""
            };
            if (recordAggregate) UpdatePromptCacheAggregate(requestType, model, diagnostics, error);
            return diagnostics;
        }

        private static void UpdatePromptCacheAggregate(string requestType, string model, Dictionary<string, object> diagnostics, string error)
        {
            string key = (requestType ?? "") + "|" + (model ?? "");
            lock (PromptCacheStateLock)
            {
                if (!PromptCacheAggregates.TryGetValue(key, out PromptCacheAggregate aggregate))
                {
                    aggregate = new PromptCacheAggregate { RequestType = requestType ?? "", Model = model ?? "" };
                    PromptCacheAggregates[key] = aggregate;
                }
                aggregate.Calls++;
                if (ReadBool(diagnostics, "eligible", false)) aggregate.EligibleCalls++;
                if (ReadBool(diagnostics, "cacheCapableRouteRequested", false)) aggregate.RoutedCalls++;
                if (ReadBool(diagnostics, "fallbackUsed", false)) aggregate.FallbackCalls++;
                if (ReadBool(diagnostics, "usageReported", false)) aggregate.UsageReportedCalls++;
                if (ReadBool(diagnostics, "cacheHit", false)) aggregate.HitCalls++;
                aggregate.PromptTokens += ReadLong(diagnostics, "promptTokens", 0);
                aggregate.CompletionTokens += ReadLong(diagnostics, "completionTokens", 0);
                aggregate.CachedTokens += ReadLong(diagnostics, "cachedTokens", 0);
                aggregate.CacheReadTokens += ReadLong(diagnostics, "cacheReadTokens", 0);
                aggregate.CacheWriteTokens += ReadLong(diagnostics, "cacheWriteTokens", 0);
                aggregate.DurationMs += ReadLong(diagnostics, "durationMs", 0);
                aggregate.LastPrefix = ReadDictionary(diagnostics, "prefix") ?? new Dictionary<string, object>();
                if (string.IsNullOrWhiteSpace(error)) aggregate.LastSuccessUtc = DateTime.UtcNow.ToString("o");
                else aggregate.LastError = Program.LimitText(error, 600);
            }
        }

        // Status is a bounded summary. The immutable full prompt/continuity
        // evidence remains in conversation diagnostics, never in this UI poll.
        internal static Dictionary<string, object> PromptCachePrefixStatus(Dictionary<string, object> prefix)
        {
            prefix = prefix ?? TestDict();
            var result = TestDict("statusSchema", "reign-prompt-cache-prefix-status-v1",
                "detailLocation", "Conversation Diagnostics contains complete request evidence.");
            foreach (string key in new[] { "layoutVersion", "requestType", "variant", "cacheEligible", "globalPrefixChars",
                "characterPrefixChars", "liveTurnChars", "messageCount", "globalPrefixHash", "characterPrefixHash", "liveTurnHash" })
                if (prefix.ContainsKey(key)) result[key] = prefix[key] is string text ? Program.LimitText(text, 160) : prefix[key];
            foreach (string section in new[] { "continuityPreflight", "providerPreflight" })
            {
                var source = ReadDictionary(prefix, section);
                if (source == null) continue;
                var selected = TestDict();
                foreach (string key in new[] { "capacityVerified", "verifiedContextTokens", "inputAllowance", "inputTokenEstimate",
                    "estimatedPromptTokens", "requestCharacters", "messageCharacters", "fits", "physicalCapacityFits",
                    "requiredSourceCoverageComplete", "capacityAccounting", "hardCharacterLimit", "hardLimitExceeded" })
                    if (source.ContainsKey(key)) selected[key] = source[key] is string text ? Program.LimitText(text, 160) : source[key];
                result[section] = selected;
            }
            return result;
        }

        internal static Dictionary<string, object> PromptCacheStatusApi()
        {
            Dictionary<string, object> settings = Program.LoadSettings();
            List<Dictionary<string, object>> stats;
            List<Dictionary<string, object>> cooldowns;
            long calls;
            long usageReportedCalls;
            long hitCalls;
            long promptTokens;
            long cachedTokens;
            lock (PromptCacheStateLock)
            {
                stats = PromptCacheAggregates.Values.OrderBy(x => x.RequestType).ThenBy(x => x.Model).Select(x => new Dictionary<string, object>
                {
                    ["requestType"] = x.RequestType,
                    ["model"] = x.Model,
                    ["calls"] = x.Calls,
                    ["eligibleCalls"] = x.EligibleCalls,
                    ["cacheRoutedCalls"] = x.RoutedCalls,
                    ["fallbackCalls"] = x.FallbackCalls,
                    ["usageReportedCalls"] = x.UsageReportedCalls,
                    ["cacheHitCalls"] = x.HitCalls,
                    ["hitRate"] = x.UsageReportedCalls > 0 ? Math.Round((double)x.HitCalls / x.UsageReportedCalls, 4) : 0d,
                    ["promptTokens"] = x.PromptTokens,
                    ["completionTokens"] = x.CompletionTokens,
                    ["cachedTokens"] = x.CachedTokens,
                    ["cacheReadTokens"] = x.CacheReadTokens,
                    ["cacheWriteTokens"] = x.CacheWriteTokens,
                    ["cachedTokenRatio"] = x.PromptTokens > 0 ? Math.Round((double)x.CachedTokens / x.PromptTokens, 4) : 0d,
                    ["averageDurationMs"] = x.Calls > 0 ? Math.Round((double)x.DurationMs / x.Calls, 1) : 0d,
                    ["lastSuccessUtc"] = x.LastSuccessUtc,
                    ["lastError"] = x.LastError,
                    ["lastPrefix"] = PromptCachePrefixStatus(x.LastPrefix)
                }).ToList();
                cooldowns = PromptCacheCooldowns.Where(x => x.Value > DateTime.UtcNow).Select(x => new Dictionary<string, object> { ["route"] = PromptHash(x.Key).Substring(0, 16), ["untilUtc"] = x.Value.ToString("o") }).ToList();
                calls = PromptCacheAggregates.Values.Sum(x => x.Calls);
                usageReportedCalls = PromptCacheAggregates.Values.Sum(x => x.UsageReportedCalls);
                hitCalls = PromptCacheAggregates.Values.Sum(x => x.HitCalls);
                promptTokens = PromptCacheAggregates.Values.Sum(x => x.PromptTokens);
                cachedTokens = PromptCacheAggregates.Values.Sum(x => x.CachedTokens);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["mode"] = ReadString(settings, "promptCacheMode", "implicit"),
                ["fallbackCooldownSeconds"] = ReadInt(settings, "promptCacheFallbackCooldownSeconds", 300),
                ["apiProvider"] = Program.UsesCodexSubscription(settings) ? Program.CodexSubscriptionProvider : Program.IsOpenRouterApiUrl(Program.LlmApiUrl(settings)) ? Program.OpenRouterProvider : IsNanoGptApiUrl(Program.LlmApiUrl(settings)) ? Program.NanoGptProvider : Program.OpenAiCompatibleProvider,
                ["routingHealth"] = cooldowns.Count > 0 ? "degraded" : "healthy",
                ["cooldownState"] = cooldowns.Count > 0 ? "active" : "none",
                ["totals"] = new Dictionary<string, object>
                {
                    ["calls"] = calls,
                    ["usageReportedCalls"] = usageReportedCalls,
                    ["cacheHitCalls"] = hitCalls,
                    ["hitRate"] = usageReportedCalls > 0 ? Math.Round((double)hitCalls / usageReportedCalls, 4) : 0d,
                    ["promptTokens"] = usageReportedCalls > 0 ? (object)promptTokens : null,
                    ["cachedTokens"] = usageReportedCalls > 0 ? (object)cachedTokens : null,
                    ["tokenSavings"] = usageReportedCalls > 0 ? (object)cachedTokens : null,
                    ["cachedTokenRatio"] = usageReportedCalls > 0 && promptTokens > 0 ? (object)Math.Round((double)cachedTokens / promptTokens, 4) : null,
                    ["lastCacheError"] = stats.Select(x => ReadString(x, "lastError", "")).LastOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? ""
                },
                ["cooldowns"] = cooldowns,
                ["stats"] = stats,
                ["reasoningPolicy"] = ReasoningPolicyStatus(settings),
                ["migration"] = PromptLayoutMigrationStatus()
            };
        }

        internal static bool SupportsPromptCacheProbe(Dictionary<string, object> settings)
            => !Program.UsesCodexSubscription(settings) && IsNanoGptApiUrl(Program.LlmApiUrl(settings));

        internal static Dictionary<string, object> PromptCacheProbeApi(Dictionary<string, object> payload)
        {
            Dictionary<string, object> settings = Program.LoadSettings();
            if (!SupportsPromptCacheProbe(settings)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The GLM cache diagnostic requires NanoGPT. Select and save NanoGPT before running it." };
            if (!Program.ProviderKeyConfigured(settings, Program.LlmApiKeySlot(settings))) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The main LLM API key is not configured." };
            string staticPrefix = BuildDialogueGlobalPrefix(false, false, Program.CompactActionCatalog(Program.ActionCatalog()));
            string characterPrefix = "CACHE PROBE CHARACTER\nThis synthetic diagnostic character contains no campaign or player data.";
            Dictionary<string, object> first = RunPromptCacheProbeCall(staticPrefix, characterPrefix, "Probe suffix one. Return {\"ok\":true}.");
            if (!ReadBool(first, "ok", false)) return new Dictionary<string, object> { ["ok"] = false, ["first"] = first, ["error"] = ReadString(first, "error", "The first cache probe call failed.") };
            Dictionary<string, object> second = RunPromptCacheProbeCall(staticPrefix, characterPrefix, "Probe suffix two. Return {\"ok\":true}.");
            Dictionary<string, object> secondCache = ReadDictionary(second, "cache") ?? new Dictionary<string, object>();
            bool reported = ReadBool(secondCache, "usageReported", false);
            bool hit = ReadBool(secondCache, "cacheHit", false);
            return new Dictionary<string, object>
            {
                ["ok"] = ReadBool(second, "ok", false),
                ["verified"] = hit,
                ["usageReported"] = reported,
                ["message"] = hit ? "The second GLM request reported cached input tokens." : reported ? "The provider reported usage but no cache hit on the second call." : "The provider did not return cache metrics, so reuse cannot be verified from this response.",
                ["first"] = ReadDictionary(first, "cache") ?? new Dictionary<string, object>(),
                ["second"] = secondCache
            };
        }

        private static Dictionary<string, object> RunPromptCacheProbeCall(string staticPrefix, string characterPrefix, string suffix)
        {
            PromptEnvelope envelope = CreatePromptEnvelope("prompt_cache_probe", "synthetic", staticPrefix, characterPrefix, suffix);
            return Program.ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "prompt_cache_probe",
                ["model"] = "zai-org/glm-5.2",
                ["messages"] = envelope.Messages,
                ["promptEnvelope"] = envelope.Diagnostics,
                ["promptCacheEligible"] = true,
                ["temperature"] = 0d,
                ["maxTokens"] = 48,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });
        }
    }
}
