using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly string[] CodexExperimentKeys = {
            "structuredOutputs", "compactMetadata", "stablePromptMapping",
            "parallelContextPreparation", "reuseThreads", "asyncThreadCleanup"
        };
        private sealed class CodexConversationScope
        {
            internal Dictionary<string, object> Settings;
            internal string CorrelationId;
            internal bool RepairOverride;
            internal bool RepairUsed;
            internal long QueueMs;
            internal Dictionary<string, object> FinalContract;
            internal readonly List<Tuple<Dictionary<string, object>, Dictionary<string, object>>> Results = new List<Tuple<Dictionary<string, object>, Dictionary<string, object>>>();
        }
        private static readonly AsyncLocal<CodexConversationScope> ActiveCodexConversationScope = new AsyncLocal<CodexConversationScope>();
        private sealed class CodexConversationGate
        {
            internal readonly SemaphoreSlim Semaphore = new SemaphoreSlim(1, 1);
            internal int Users;
        }
        private static readonly object CodexConversationGateLock = new object();
        private static readonly Dictionary<string, CodexConversationGate> CodexConversationGates = new Dictionary<string, CodexConversationGate>();
        private static readonly object CodexTimelineGate = new object();
        private static readonly Dictionary<string, string> CodexConversationTimelines = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private static void InvalidateCodexConversationTimeline(string campaignId)
        {
            lock (CodexTimelineGate) CodexConversationTimelines[campaignId ?? ""] = Guid.NewGuid().ToString("N");
            InvalidateCodexThreads("save_load_or_rollback");
        }

        private static string CodexConversationTimeline(string campaignId)
        {
            lock (CodexTimelineGate)
            {
                string value;
                return CodexConversationTimelines.TryGetValue(campaignId ?? "", out value) ? value : "";
            }
        }

        private static string CodexSettingsIdentity(Dictionary<string, object> settings)
        {
            var identity = new Dictionary<string, object>();
            foreach (string key in new[] { "llmProvider", "codexExecutable", "codexHome" }.Concat(ChatModelKeys))
                identity[key] = ReadString(settings, key, "");
            identity["codexOptions"] = ReadDictionary(settings, "codexOptions");
            return Json.Serialize(identity);
        }

        private static void AttachCodexAuthoritativeConversationMetadata(Dictionary<string, object> request, Dictionary<string, object> source,
            string campaignId, string speakerId, string mode, string sessionId, Dictionary<string, object> identityView,
            Dictionary<string, object> profile, Dictionary<string, object> state, Dictionary<string, object> relationships,
            Dictionary<string, object> summary, Dictionary<string, object> sceneState, object memories, object contextBundles,
            List<Dictionary<string, object>> canonicalHistory, string currentTurnText)
        {
            if (ActiveCodexConversationScope.Value == null) return;
            string generation = CodexConversationTimeline(campaignId);
            var authority = new Dictionary<string, object> {
                ["identity"] = identityView, ["profile"] = profile, ["state"] = state, ["relationships"] = relationships,
                ["memorySummary"] = summary, ["memories"] = memories, ["scene"] = sceneState, ["context"] = contextBundles
            };
            var metadata = new Dictionary<string, object> {
                ["campaignId"] = campaignId, ["timelineId"] = generation, ["loadGeneration"] = generation,
                ["sessionId"] = sessionId ?? "", ["ownSessionId"] = sessionId ?? "", ["speakerHeroStringId"] = speakerId,
                ["conversationMode"] = FirstNonEmpty(ReadString(source, "conversationMode", ""), mode),
                ["visibilityScope"] = PromptHash(Json.Serialize(identityView) + "|" + mode + "|" + speakerId),
                ["authorityHash"] = PromptHash(Json.Serialize(authority)),
                ["canonicalHistorySignature"] = CodexCanonicalHistorySignature(canonicalHistory),
                ["currentTurnText"] = currentTurnText ?? "",
                ["promptVersion"] = ReadString(ReadDictionary(request, "promptEnvelope"), "globalPrefixHash", "")
            };
            foreach (string key in new[] { "castleOpening", "channel", "mode", "governmentPrivateContext", "ambassadorOfficialContext", "nobleDocketTurn", "courtLifeContext", "guardedPromptOverride", "outputSchema", "customOutputContract" })
                if (source != null && source.ContainsKey(key)) metadata[key] = source[key];
            request["codexAuthoritativeMetadata"] = metadata;
        }

        private static string CodexCanonicalHistorySignature(List<Dictionary<string, object>> lines)
        {
            return PromptHash(Json.Serialize((lines ?? new List<Dictionary<string, object>>()).Select(line => new[] {
                ReadString(line, "role", ""), ReadString(line, "speaker", ""), ReadString(line, "text", "") }).ToList()));
        }

        private static Dictionary<string, object> RunCodexConversationScope(Dictionary<string, object> payload, Func<Dictionary<string, object>> generate)
        {
            if (ActiveCodexConversationScope.Value != null) return generate();
            var settings = LoadSettings();
            if (!UsesCodexSubscription(settings)) return generate();
            var scope = new CodexConversationScope { Settings = DeepCloneProfileDictionary(settings), CorrelationId = EnsureCorrelationId(payload) };
            string gateKey = "";
            CodexConversationGate gate = null;
            bool entered = false;
            string session = ReadFirstString(payload, "conversationSessionId", "sessionId", "conversationId");
            string campaign = ReadString(payload, "campaignId", "");
            string speaker = FirstNonEmpty(ReadFirstString(payload, "heroStringId", "speakerHeroStringId", "heroId"), ReadString(ReadDictionary(payload, "hero"), "heroStringId", ""));
            if (ReadBool(ReadDictionary(settings, "codexOptions"), "reuseThreads", false) && session.Length > 0 && campaign.Length > 0 && speaker.Length > 0)
            {
                // Broader than visibility scope: all turns by this speaker/session serialize through final state effects.
                gateKey = Json.Serialize(new[] { campaign, CodexConversationTimeline(campaign), session, speaker, ReadString(payload, "conversationMode", "dialogue") });
                lock (CodexConversationGateLock)
                {
                    if (!CodexConversationGates.TryGetValue(gateKey, out gate) && CodexConversationGates.Count < 64)
                        CodexConversationGates[gateKey] = gate = new CodexConversationGate();
                    if (gate != null) gate.Users++;
                    else ReadDictionary(scope.Settings, "codexOptions")["reuseThreads"] = false;
                }
            }
            ActiveCodexConversationScope.Value = scope;
            Dictionary<string, object> final = null;
            try
            {
                if (gate != null)
                {
                    var queue = System.Diagnostics.Stopwatch.StartNew();
                    entered = gate.Semaphore.Wait(Math.Max(1000, ReadInt(settings, "providerQueueTimeoutMs", 120000)));
                    scope.QueueMs = queue.ElapsedMilliseconds;
                    if (!entered) throw new TimeoutException("Codex conversation turn queue timed out before generation.");
                }
                final = generate(); return final;
            }
            finally
            {
                try
                {
                bool accepted = ReadBool(final, "ok", false);
                foreach (var entry in scope.Results)
                {
                    var context = entry.Item2;
                    context["accepted"] = accepted;
                    var draft = TryParseJsonObject(ReadString(entry.Item1, "content", ""));
                    bool changed = scope.Results.Count != 1 || draft == null || !accepted;
                    if (!changed)
                    {
                        foreach (var field in draft)
                        {
                            object finalValue;
                            if (scope.FinalContract == null || !scope.FinalContract.TryGetValue(field.Key, out finalValue)
                                || Json.Serialize(field.Value) != Json.Serialize(finalValue)) { changed = true; break; }
                        }
                    }
                    context["substantivelyChanged"] = changed;
                    context["acceptedHistorySignature"] = final != null && final.ContainsKey("lines")
                        ? CodexCanonicalHistorySignature(ReadDictionaryList(final, "lines")) : "";
                    try { FinalizeCodexThreadRequest(entry.Item1, context); }
                    catch (Exception ex) { LogOperational("codex.finalization_cleanup_failed", new Dictionary<string, object> { ["error"] = LimitText(ex.Message, 500) }); }
                }
                if (final != null) final["codexDiagnostics"] = BuildCodexConversationPerformanceDiagnostics(scope, final);
                }
                finally
                {
                ActiveCodexConversationScope.Value = null;
                if (gate != null)
                {
                    if (entered) gate.Semaphore.Release();
                    lock (CodexConversationGateLock)
                        if (--gate.Users == 0) { CodexConversationGates.Remove(gateKey); gate.Semaphore.Dispose(); }
                }
                }
            }
        }

        private static void RecordCodexFinalRepairEvidence(Dictionary<string, object> llm)
        {
            var scope = ActiveCodexConversationScope.Value;
            if (scope == null) return;
            foreach (var pair in llm ?? new Dictionary<string, object>())
            {
                if (pair.Key.IndexOf("repair", StringComparison.OrdinalIgnoreCase) < 0) continue;
                var evidence = pair.Value as Dictionary<string, object>;
                if (evidence == null) continue;
                scope.RepairUsed = true;
                scope.RepairOverride |= ReadBool(evidence, "secondAttemptReturned", false) && !ReadBool(evidence, "revalidationCleared", true);
            }
        }

        private static void RecordCodexFinalizedContract(Dictionary<string, object> parsed, Dictionary<string, object> response)
        {
            var scope = ActiveCodexConversationScope.Value;
            if (scope == null || parsed == null) return;
            scope.FinalContract = DeepCloneProfileDictionary(parsed);
            foreach (string key in parsed.Keys)
                if (response != null && response.ContainsKey(key)) scope.FinalContract[key] = response[key];
        }

        private static Dictionary<string, object> BuildCodexConversationPerformanceDiagnostics(CodexConversationScope scope, Dictionary<string, object> final)
        {
            var timing = ReadDictionary(final, "timing");
            var calls = scope.Results.Select(entry => new Dictionary<string, object> {
                ["requestType"] = ReadString(entry.Item2, "requestType", ""),
                ["model"] = ReadString(entry.Item2, "model", ""),
                ["durationMs"] = ReadLong(entry.Item1, "durationMs", 0),
                ["runtime"] = ReadDictionary(entry.Item1, "codexDiagnostics") ?? new Dictionary<string, object>(),
                ["schemaId"] = ReadString(entry.Item2, "schemaId", ""),
                ["schemaContractFingerprint"] = ReadString(entry.Item2, "schemaContractFingerprint", "unknown"),
                ["schemaFingerprint"] = !ReadBool(ReadDictionary(ReadDictionary(entry.Item1, "codexDiagnostics"), "request"), "schemaApplied", false)
                    ? "not_applied" : PromptHash(Json.Serialize(ReadDictionary(entry.Item2, "outputSchema"))),
                ["promptMappingVersion"] = ReadString(entry.Item2, "promptMappingVersion", "unknown"),
                ["promptSegments"] = ReadDictionaryList(entry.Item2, "promptSegments").Select(segment => new Dictionary<string, object> {
                    ["name"] = ReadString(segment, "name", ""), ["role"] = ReadString(segment, "role", ""),
                    ["stable"] = ReadBool(segment, "stable", false), ["hash"] = ReadString(segment, "hash", "unknown") }).ToList(),
                ["usage"] = ReadDictionary(ReadDictionary(entry.Item1, "raw"), "usage")
            }).ToList();
            long repairMs = calls.Where(row => ReadString(row, "requestType", "").IndexOf("repair", StringComparison.OrdinalIgnoreCase) >= 0).Sum(row => ReadLong(row, "durationMs", 0));
            var diagnostics = new Dictionary<string, object> {
                ["schema"] = "reign-codex-conversation-diagnostics-v1",
                ["outcome"] = !ReadBool(final, "ok", false) ? "unusable_response" : scope.RepairOverride ? "accepted_repair_override" : scope.RepairUsed || repairMs > 0 ? "successful_repair" : "clean_first_attempt",
                ["stages"] = new Dictionary<string, object> {
                    ["context"] = timing == null ? null : (object)(ReadLong(timing, "contextLoadMs", 0) + ReadLong(timing, "promptBuildMs", 0)),
                    ["repair"] = repairMs, ["finalization"] = timing == null ? null : (object)(ReadLong(timing, "parseMs", 0) + ReadLong(timing, "storageMs", 0)),
                    ["generation"] = calls.Sum(row => ReadLong(row, "durationMs", 0)),
                    ["queue"] = null, ["firstText"] = null, ["threadStartup"] = null, ["cleanup"] = null
                }, ["calls"] = calls, ["settingsFingerprint"] = PromptHash(Json.Serialize(ReadDictionary(scope.Settings, "codexOptions")))
            };
            var stages = ReadDictionary(diagnostics, "stages");
            foreach (string stage in new[] { "queue", "threadStartup", "generation", "cleanup" })
            {
                var rows = calls.Select(row => ReadDictionary(ReadDictionary(ReadDictionary(row, "runtime"), "timings"), "stages")).ToList();
                stages[stage] = rows.Count > 0 && rows.All(row => row != null && row.ContainsKey(stage) && row[stage] != null)
                    ? (object)rows.Sum(row => ReadDouble(row, stage, 0)) : null;
            }
            var firstStages = calls.Count == 0 ? null : ReadDictionary(ReadDictionary(ReadDictionary(calls[0], "runtime"), "timings"), "stages");
            if (stages["queue"] != null) stages["queue"] = ReadDouble(stages, "queue", 0) + scope.QueueMs;
            stages["firstText"] = firstStages != null && firstStages.ContainsKey("firstText") ? firstStages["firstText"] : null;
            var usages = calls.Select(row => ReadDictionary(row, "usage")).ToList();
            var usage = new Dictionary<string, object>();
            foreach (var mapping in new[] { new[] { "inputTokens", "prompt_tokens" }, new[] { "outputTokens", "completion_tokens" }, new[] { "totalTokens", "total_tokens" } })
                usage[mapping[0]] = usages.Count > 0 && usages.All(row => row != null && row.ContainsKey(mapping[1]) && row[mapping[1]] != null)
                    ? (object)usages.Sum(row => ReadLong(row, mapping[1], 0)) : null;
            diagnostics["usage"] = usage;
            return diagnostics;
        }

        // This bank is deliberately independent of the legacy API-provider reasoning controls.
        private static bool NormalizeCodexOptions(Dictionary<string, object> settings)
        {
            if (settings == null) return false;
            var previous = ReadDictionary(settings, "codexOptions");
            string before = previous == null ? "" : Json.Serialize(previous);
            var source = previous ?? settings;
            string mode = ReadString(source, "reasoningMode", "selective").Trim().ToLowerInvariant();
            string effort = ReadString(source, "reasoningEffort", "medium").Trim().ToLowerInvariant();
            if (!new[] { "off", "selective", "all" }.Contains(mode)) mode = "selective";
            if (!new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra" }.Contains(effort)) effort = "medium";
            var normalized = new Dictionary<string, object> {
                ["schema"] = "reign-codex-options-v1", ["version"] = 1, ["reasoningMode"] = mode,
                ["reasoningEffort"] = effort, ["fastMode"] = ReadBool(previous, "fastMode", false)
            };
            foreach (string key in CodexExperimentKeys) normalized[key] = ReadBool(previous, key, false);
            settings["codexOptions"] = normalized;
            return before != Json.Serialize(normalized);
        }

        private static string EffectiveCodexReasoningEffort(Dictionary<string, object> settings,
            Dictionary<string, object> payload, string requestType)
        {
            NormalizeCodexOptions(settings);
            var options = ReadDictionary(settings, "codexOptions");
            if (ReadBool(payload, "reasoningDisabled", false)) return "none";
            string mode = ReadString(options, "reasoningMode", "selective");
            string type = (requestType ?? "").Trim().ToLowerInvariant();
            if (mode == "off" || (mode == "selective" && !SelectiveReasoningRequestTypes.Contains(type))) return "none";
            string effort = ReadString(options, "reasoningEffort", "medium");
            return type == "memory" && new[] { "medium", "high", "xhigh", "max", "ultra" }.Contains(effort) ? "low" : effort;
        }

        private static Dictionary<string, object> BuildCodexGenerationContext(Dictionary<string, object> settings,
            Dictionary<string, object> payload, string requestType, string model, string correlationId)
        {
            if (!UsesCodexSubscription(settings)) return null;
            NormalizeCodexOptions(settings);
            var context = new Dictionary<string, object> {
                ["options"] = DeepCloneProfileDictionary(ReadDictionary(settings, "codexOptions")),
                ["requestType"] = requestType, ["model"] = model, ["correlationId"] = correlationId,
                ["attemptId"] = Guid.NewGuid().ToString("N"),
                ["parentCorrelationId"] = FirstNonEmpty(ReadFirstString(payload, "parentCorrelationId", "repairParentCorrelationId"), ActiveCodexConversationScope.Value?.CorrelationId ?? ""),
                ["deferFinalization"] = ActiveCodexConversationScope.Value != null,
                ["authoritativeMetadata"] = ActiveCodexConversationScope.Value == null ? new Dictionary<string, object>()
                    : DeepCloneProfileDictionary(ReadDictionary(payload, "codexAuthoritativeMetadata") ?? new Dictionary<string, object>()),
                ["requestedReasoningEffort"] = EffectiveCodexReasoningEffort(settings, payload, requestType),
                ["campaignId"] = ReadString(payload, "campaignId", ""),
                ["timelineId"] = ReadFirstString(payload, "timelineId", "loadGeneration"),
                ["sessionId"] = ReadFirstString(payload, "sessionId", "conversationSessionId"),
                ["speakerId"] = ReadFirstString(payload, "speakerHeroStringId", "heroStringId", "heroId", "characterId"),
                ["conversationMode"] = ReadString(payload, "conversationMode", ""),
                ["visibilityScope"] = ReadString(payload, "visibilityScope", "")
            };
            var authority = ReadDictionary(context, "authoritativeMetadata");
            foreach (string key in new[] { "canonicalHistorySignature", "currentTurnText" })
                context[key] = ReadString(authority, key, "");
            return context;
        }

        private sealed class CodexPerformanceBudget
        {
            internal int Maximum;
            internal int Used;
            internal bool Closed;
            internal readonly object Gate = new object();
            internal readonly List<Dictionary<string, object>> Calls = new List<Dictionary<string, object>>();
        }
        private static readonly AsyncLocal<CodexPerformanceBudget> ActiveCodexPerformanceBudget = new AsyncLocal<CodexPerformanceBudget>();
        private static readonly AsyncLocal<Dictionary<string, object>> CodexPerformanceSettings = new AsyncLocal<Dictionary<string, object>>();

        // Reserve at physical turn/start, so tier retries, repairs and probes all consume the same cap.
        private static void ReserveCodexPerformanceCall(Dictionary<string, object> context)
        {
            var budget = ActiveCodexPerformanceBudget.Value;
            if (budget == null) return;
            lock (budget.Gate)
            {
                if (budget.Closed || budget.Used >= budget.Maximum) throw new InvalidOperationException("Codex performance total provider-call cap reached or closed before dispatch.");
                budget.Used++;
                budget.Calls.Add(new Dictionary<string, object> {
                    ["call"] = budget.Used, ["requestType"] = ReadString(context, "requestType", "unknown"),
                    ["dispatchId"] = Guid.NewGuid().ToString("N"), ["attemptId"] = ReadString(context, "attemptId", ""),
                    ["correlationId"] = ReadString(context, "correlationId", ""), ["parentCorrelationId"] = ReadString(context, "parentCorrelationId", ""),
                    ["model"] = ReadString(context, "model", ""), ["utc"] = DateTime.UtcNow.ToString("o")
                });
            }
        }
    }
}
