using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CodexPerformanceReportSchema = "reign-codex-performance-report-v1";
        private static readonly string[] CodexPerformanceStageNames =
        {
            "context", "queue", "threadStartup", "firstText", "generation", "repair", "finalization", "cleanup"
        };
        private static readonly string[] CodexPerformanceChangedOptions =
        {
            "fastMode", "reasoningEffort", "structuredOutputs", "compactMetadata", "stablePromptMapping",
            "parallelContextPreparation", "reuseThreads", "asyncThreadCleanup"
        };
        private static readonly string[] CodexPerformanceOptionKeys =
        {
            "schema", "version", "reasoningMode", "reasoningEffort", "fastMode", "structuredOutputs",
            "compactMetadata", "stablePromptMapping", "parallelContextPreparation", "reuseThreads", "asyncThreadCleanup"
        };
        private static readonly string[] CodexPerformanceReasoningEfforts =
        {
            "none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"
        };

        // Builds a gated, provider-free experiment plan. The caller must supply an exact model,
        // selected case IDs, one changed option, and a positive total provider-call cap before a
        // live Verification Lab route can use the plan. This helper never starts a request.
        internal static Dictionary<string, object> BuildCodexPerformancePlan(Dictionary<string, object> request)
        {
            request = request ?? new Dictionary<string, object>();
            List<string> caseIds = NormalizeCodexPerformanceCaseIds(ReadStringList(request, "caseIds"));
            string model = ReadString(request, "model", "").Trim();
            string requestedChangedOption = ReadString(request, "changedOption", "").Trim();
            string changedOption = CodexPerformanceChangedOptions.FirstOrDefault(key =>
                string.Equals(key, requestedChangedOption, StringComparison.OrdinalIgnoreCase));
            int cap = ReadInt(request, "totalProviderCallCap", 0);
            if (string.IsNullOrWhiteSpace(model)) throw new ArgumentException("codex_performance requires an exact model.", "model");
            if (caseIds.Count == 0) throw new ArgumentException("codex_performance requires at least one explicit case ID.", "caseIds");
            if (cap < 1 || cap > 1000) throw new ArgumentException("codex_performance requires a total provider-call cap between 1 and 1000.", "totalProviderCallCap");
            if (changedOption == null)
                throw new ArgumentException("codex_performance changes one supported option at a time.", "changedOption");
            Dictionary<string, object> baselineOptions = ReadDictionary(request, "baselineOptions");
            Dictionary<string, object> variantOptions = ReadDictionary(request, "variantOptions");
            if (baselineOptions == null || variantOptions == null)
                throw new ArgumentException("codex_performance requires baselineOptions and variantOptions.", "baselineOptions");
            ValidateCodexPerformanceOptions(baselineOptions, "baselineOptions");
            ValidateCodexPerformanceOptions(variantOptions, "variantOptions");
            ValidateCodexPerformanceArmIdentity(request, model, caseIds);
            foreach (string fixedOption in new[] { "schema", "version", "reasoningMode" })
                if (!string.Equals(CanonicalValue(baselineOptions, fixedOption), CanonicalValue(variantOptions, fixedOption), StringComparison.Ordinal))
                    throw new ArgumentException("Both comparison arms must use the same " + fixedOption + ".", "variantOptions");
            List<string> changedKeys = CodexPerformanceChangedOptions.Where(key =>
                !string.Equals(CanonicalValue(baselineOptions, key), CanonicalValue(variantOptions, key), StringComparison.Ordinal)).ToList();
            if (changedKeys.Count != 1 || !string.Equals(changedKeys[0], changedOption, StringComparison.Ordinal))
                throw new ArgumentException("baselineOptions and variantOptions must differ in exactly the declared option.", "variantOptions");

            Dictionary<string, object> plan = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema"] = CodexPerformanceReportSchema,
                ["suite"] = "codex_performance",
                ["status"] = "deferred",
                ["providerCallsAuthorized"] = false,
                ["requiresExplicitUsageAuthorization"] = true,
                ["model"] = model,
                ["caseIds"] = caseIds,
                ["changedOption"] = changedOption,
                ["baselineOptions"] = baselineOptions,
                ["variantOptions"] = variantOptions,
                ["sameModelAcrossArms"] = true,
                ["oneOptionAtATime"] = true,
                ["totalProviderCallCap"] = cap,
                ["countsRepairsRetriesProbesAndJudges"] = true,
                ["sourceFingerprint"] = ReadString(request, "sourceFingerprint", "unknown"),
                ["configuration"] = ReadString(request, "configuration", "Release"),
                ["runtimeVersion"] = ReadString(request, "runtimeVersion", "unknown"),
                ["settingsFingerprint"] = ReadString(request, "settingsFingerprint", "unknown"),
                ["schemaFingerprint"] = ReadString(request, "schemaFingerprint", "unknown"),
                ["promptFingerprint"] = ReadString(request, "promptFingerprint", "unknown"),
                ["validationFingerprint"] = ReadString(request, "validationFingerprint", "unknown"),
                ["providerCallKinds"] = new[] { "primary", "repair", "retry", "probe", "judge" },
                ["notes"] = new[]
                {
                    "Compare the same cases and exact model while changing one option at a time.",
                    "Do not infer a default recommendation from latency alone.",
                    "Unknown usage and missing runtime cache signals remain unknown."
                }
            };
            return plan;
        }

        private static List<string> NormalizeCodexPerformanceCaseIds(IEnumerable<string> values)
        {
            return (values ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ValidateCodexPerformanceOptions(Dictionary<string, object> options, string argumentName)
        {
            foreach (string key in options.Keys)
            {
                if (!CodexPerformanceOptionKeys.Contains(key, StringComparer.Ordinal))
                    throw new ArgumentException(argumentName + " contains unknown Codex option '" + key + "'.", argumentName);
            }

            if (options.ContainsKey("schema")
                && !string.Equals(ReadString(options, "schema", ""), "reign-codex-options-v1", StringComparison.Ordinal))
                throw new ArgumentException(argumentName + " has an unsupported Codex options schema.", argumentName);
            if (options.ContainsKey("version") && ReadInt(options, "version", 0) != 1)
                throw new ArgumentException(argumentName + " must use Codex options version 1.", argumentName);

            if (options.ContainsKey("reasoningMode"))
            {
                string mode = ReadString(options, "reasoningMode", "").Trim().ToLowerInvariant();
                if (!new[] { "off", "selective", "all" }.Contains(mode, StringComparer.Ordinal))
                    throw new ArgumentException(argumentName + " has an unsupported reasoningMode.", argumentName);
            }
            if (options.ContainsKey("reasoningEffort"))
            {
                string effort = ReadString(options, "reasoningEffort", "").Trim().ToLowerInvariant();
                if (!CodexPerformanceReasoningEfforts.Contains(effort, StringComparer.Ordinal))
                    throw new ArgumentException(argumentName + " has an unsupported reasoningEffort.", argumentName);
            }
            foreach (string key in CodexPerformanceChangedOptions.Where(key => !string.Equals(key, "reasoningEffort", StringComparison.Ordinal)))
            {
                if (options.ContainsKey(key) && !IsCodexPerformanceBoolean(options[key]))
                    throw new ArgumentException(argumentName + "." + key + " must be boolean.", argumentName);
            }
        }

        private static bool IsCodexPerformanceBoolean(object value)
        {
            if (value is bool) return true;
            string text = Convert.ToString(value, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return text == "true" || text == "false" || text == "on" || text == "off" || text == "1" || text == "0";
        }

        private static void ValidateCodexPerformanceArmIdentity(
            Dictionary<string, object> request, string model, List<string> caseIds)
        {
            foreach (string key in new[] { "baselineModel", "variantModel" })
            {
                if (request.ContainsKey(key) && !string.Equals(ReadString(request, key, "").Trim(), model, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(key + " must match the exact plan model.", key);
            }
            foreach (string key in new[] { "baselineCaseIds", "variantCaseIds" })
            {
                if (!request.ContainsKey(key)) continue;
                List<string> supplied = NormalizeCodexPerformanceCaseIds(ReadStringList(request, key));
                bool same = supplied.Count == caseIds.Count
                    && supplied.All(value => caseIds.Contains(value, StringComparer.OrdinalIgnoreCase));
                if (!same) throw new ArgumentException(key + " must contain the same explicit cases as caseIds.", key);
            }
        }

        // Creates the report from deterministic sample receipts supplied by the deferred suite.
        // Samples are already isolated by the suite; this method only summarizes evidence and
        // never calls a provider or mutates campaign state.
        internal static Dictionary<string, object> BuildCodexPerformanceReport(
            Dictionary<string, object> configuration,
            IEnumerable<Dictionary<string, object>> sampleReceipts,
            int providerCallCount)
        {
            configuration = configuration ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> samples = (sampleReceipts ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(sample => sample != null).ToList();
            int cap = ReadInt(configuration, "totalProviderCallCap", 0);
            Dictionary<string, object> fingerprints = BuildCodexPerformanceFingerprints(configuration);
            Dictionary<string, object> report = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["schema"] = CodexPerformanceReportSchema,
                ["suite"] = "codex_performance",
                ["status"] = "deferred-result",
                ["model"] = ReadString(configuration, "model", "unknown"),
                ["configuration"] = ReadString(configuration, "configuration", "Release"),
                ["runtimeVersion"] = ReadString(configuration, "runtimeVersion", "unknown"),
                ["caseIds"] = ReadStringList(configuration, "caseIds"),
                ["changedOption"] = ReadString(configuration, "changedOption", ""),
                ["baselineOptions"] = ReadDictionary(configuration, "baselineOptions") ?? new Dictionary<string, object>(),
                ["variantOptions"] = ReadDictionary(configuration, "variantOptions") ?? new Dictionary<string, object>(),
                ["totalProviderCallCap"] = cap,
                ["modelConsistency"] = CodexPerformanceModelConsistency(configuration, samples),
                ["providerCalls"] = new Dictionary<string, object>
                {
                    ["total"] = providerCallCount,
                    ["cap"] = cap,
                    ["withinCap"] = cap > 0 && providerCallCount <= cap,
                    ["repairsRetriesProbesAndJudgesCounted"] = true
                },
                ["sampleCount"] = samples.Count,
                ["stageTimings"] = CodexPerformanceStageSummary(samples),
                ["outcomes"] = CodexPerformanceOutcomeSummary(samples),
                ["usage"] = CodexPerformanceUsageSummary(samples),
                ["capabilities"] = CodexPerformanceCapabilitySummary(samples),
                ["arms"] = CodexPerformanceArmSummary(samples),
                ["fingerprints"] = fingerprints,
                ["sourceFingerprint"] = ReadString(configuration, "sourceFingerprint", "unknown"),
                ["evidenceLimits"] = new Dictionary<string, object>
                {
                    ["actualUsageMissingIsNull"] = true,
                    ["unusableResponsesSeparateFromSuccessfulRepairs"] = true,
                    ["noLiveRunPerformedByReportBuilder"] = true
                }
            };
            return report;
        }

        internal static Dictionary<string, object> BuildCodexPerformanceCapabilityStatuses(
            Dictionary<string, object> requested,
            Dictionary<string, object> applied,
            Dictionary<string, object> unsupported,
            Dictionary<string, object> unknown)
        {
            return new Dictionary<string, object>
            {
                ["requested"] = requested ?? new Dictionary<string, object>(),
                ["applied"] = applied ?? new Dictionary<string, object>(),
                ["unsupported"] = unsupported ?? new Dictionary<string, object>(),
                ["unknown"] = unknown ?? new Dictionary<string, object>()
            };
        }

        // A small deterministic contract used by the offline suite. It deliberately exercises
        // a baseline/variant pair, an unknown usage receipt, and the outcome separation without
        // creating a provider transport or a campaign fixture.
        internal static Dictionary<string, object> RunCodexPerformanceReportSelfTests()
        {
            Dictionary<string, object> baseline = new Dictionary<string, object>
            {
                ["schema"] = "reign-codex-options-v1", ["version"] = 1, ["reasoningMode"] = "selective",
                ["fastMode"] = false, ["reasoningEffort"] = "medium", ["structuredOutputs"] = false,
                ["compactMetadata"] = false, ["stablePromptMapping"] = false,
                ["parallelContextPreparation"] = false, ["reuseThreads"] = false, ["asyncThreadCleanup"] = false
            };
            Dictionary<string, object> variant = new Dictionary<string, object>(baseline)
            {
                ["fastMode"] = true
            };
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["model"] = "gpt-5.5", ["caseIds"] = new List<object> { "fixture-1" }, ["changedOption"] = "fastMode",
                ["totalProviderCallCap"] = 4, ["baselineOptions"] = baseline, ["variantOptions"] = variant,
                ["runtimeVersion"] = "fixture-runtime", ["settingsFingerprint"] = "settings-hash",
                ["schemaFingerprint"] = "schema-hash", ["promptFingerprint"] = "prompt-hash",
                ["sourceFingerprint"] = "source-hash", ["validationFingerprint"] = "validation-hash"
            };
            Dictionary<string, object> plan = BuildCodexPerformancePlan(request);
            Dictionary<string, object> report = BuildCodexPerformanceReport(request, new[]
            {
                new Dictionary<string, object>
                {
                    ["variant"] = "baseline", ["outcome"] = "clean_first_attempt",
                    ["stages"] = new Dictionary<string, object> { ["context"] = 10d, ["firstText"] = 20d },
                    ["usage"] = new Dictionary<string, object> { ["total_tokens"] = 100d }
                },
                new Dictionary<string, object>
                {
                    ["variant"] = "variant", ["outcome"] = "successful_repair",
                    ["stages"] = new Dictionary<string, object> { ["context"] = 12d, ["firstText"] = 24d }
                }
            }, 2);
            Dictionary<string, object> usage = ReadDictionary(report, "usage") ?? new Dictionary<string, object>();
            Dictionary<string, object> usageTotals = ReadDictionary(usage, "totalTokens") ?? new Dictionary<string, object>();

            Dictionary<string, object> reasoningVariant = new Dictionary<string, object>(baseline)
            {
                ["reasoningEffort"] = "high"
            };
            Dictionary<string, object> reasoningRequest = new Dictionary<string, object>(request)
            {
                ["changedOption"] = "reasoningEffort", ["variantOptions"] = reasoningVariant
            };
            bool reasoningEffortChangeAccepted = BuildCodexPerformancePlan(reasoningRequest) != null;

            Dictionary<string, object> unknownBaseline = new Dictionary<string, object>(baseline)
            {
                ["mysteryFlag"] = true
            };
            bool unknownSettingsRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["baselineOptions"] = unknownBaseline
            });
            Dictionary<string, object> twoChangesVariant = new Dictionary<string, object>(variant)
            {
                ["reasoningEffort"] = "high"
            };
            bool multipleChangesRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["variantOptions"] = twoChangesVariant
            });
            bool reasoningModeChangeRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["variantOptions"] = new Dictionary<string, object>(variant) { ["reasoningMode"] = "off" }
            });
            bool declaredOptionMismatchRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["changedOption"] = "reasoningEffort"
            });
            bool invalidCapsRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["totalProviderCallCap"] = 0
            }) && CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["totalProviderCallCap"] = 1001
            });
            bool modelMismatchRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["baselineModel"] = "gpt-5.4"
            });
            bool caseMismatchRejected = CodexPerformancePlanRejects(new Dictionary<string, object>(request)
            {
                ["variantCaseIds"] = new List<object> { "different-fixture" }
            });
            bool unknownModelRemainsUnknown = !ReadBool(ReadDictionary(report, "modelConsistency"), "ok", true)
                && ReadString(ReadDictionary(report, "modelConsistency"), "state", "") == "unknown";
            var substituted = CodexPerformanceModelConsistency(request, new[] { new Dictionary<string, object> {
                ["model"] = "gpt-5.5", ["diagnostics"] = new Dictionary<string, object> { ["calls"] = new List<object> {
                    new Dictionary<string, object> { ["runtime"] = new Dictionary<string, object> {
                        ["effectiveModelKnown"] = true, ["servedModel"] = "gpt-substituted" } } } } } });
            bool substitutionReported = !ReadBool(substituted, "ok", true) && ReadString(substituted, "state", "") == "mismatch";
            return new Dictionary<string, object>
            {
                ["ok"] = ReadString(plan, "suite", "") == "codex_performance"
                    && ReadInt(ReadDictionary(report, "providerCalls"), "total", -1) == 2
                    && usage.ContainsKey("totalTokens")
                    && ReadDictionary(report, "arms") != null
                    && ReadDictionary(report, "arms").ContainsKey("baseline")
                    && ReadDictionary(report, "arms").ContainsKey("variant")
                    && reasoningEffortChangeAccepted
                    && unknownSettingsRejected && multipleChangesRejected && reasoningModeChangeRejected
                    && ReadInt(usage, "unknownSampleCount", -1) == 1
                    && ReadInt(usageTotals, "sampleCount", -1) == 1
                    && Math.Abs(CodexReadNullableDouble(usageTotals, "median").GetValueOrDefault(-1d) - 100d) < 0.001d
                    && declaredOptionMismatchRejected && invalidCapsRejected
                    && modelMismatchRejected && caseMismatchRejected && unknownModelRemainsUnknown && substitutionReported,
                ["providerCalls"] = 0,
                ["reportSchema"] = ReadString(report, "schema", ""),
                ["unknownUsageIsNull"] = ReadInt(usage, "unknownSampleCount", -1) == 1,
                ["knownUsageIsNotZero"] = ReadInt(usageTotals, "sampleCount", -1) == 1
                    && Math.Abs(CodexReadNullableDouble(usageTotals, "median").GetValueOrDefault(-1d) - 100d) < 0.001d,
                ["reasoningEffortChangeAccepted"] = reasoningEffortChangeAccepted,
                ["unknownSettingsRejected"] = unknownSettingsRejected,
                ["multipleChangesRejected"] = multipleChangesRejected,
                ["reasoningModeChangeRejected"] = reasoningModeChangeRejected,
                ["declaredOptionMismatchRejected"] = declaredOptionMismatchRejected,
                ["invalidCapsRejected"] = invalidCapsRejected,
                ["sameModelRejected"] = modelMismatchRejected,
                ["unknownModelRemainsUnknown"] = unknownModelRemainsUnknown,
                ["substitutionReported"] = substitutionReported,
                ["sameCasesRejected"] = caseMismatchRejected
            };
        }

        private static bool CodexPerformancePlanRejects(Dictionary<string, object> request)
        {
            try
            {
                BuildCodexPerformancePlan(request);
                return false;
            }
            catch (ArgumentException)
            {
                return true;
            }
        }

        private static Dictionary<string, object> CodexPerformanceStageSummary(
            IEnumerable<Dictionary<string, object>> samples)
        {
            List<Dictionary<string, object>> rows = samples.ToList();
            Dictionary<string, object> summary = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string stage in CodexPerformanceStageNames)
            {
                List<double> values = rows.Select(sample =>
                {
                    Dictionary<string, object> stages = ReadDictionary(sample, "stages") ?? sample;
                    return CodexReadNullableDouble(stages, stage) ?? CodexReadNullableDouble(stages, stage + "Ms");
                }).Where(value => value.HasValue && value.Value >= 0d).Select(value => value.Value).ToList();
                summary[stage] = new Dictionary<string, object>
                {
                    ["sampleCount"] = values.Count,
                    ["unknownCount"] = rows.Count - values.Count,
                    ["medianMs"] = values.Count == 0 ? (object)null : Percentile(values, 0.50d),
                    ["p95Ms"] = values.Count == 0 ? (object)null : Percentile(values, 0.95d)
                };
            }
            return summary;
        }

        private static Dictionary<string, object> CodexPerformanceOutcomeSummary(
            IEnumerable<Dictionary<string, object>> samples)
        {
            string[] categories = { "cleanFirstAttempts", "successfulRepairs", "acceptedRepairOverrides", "unusableResponses" };
            Dictionary<string, object> result = categories.ToDictionary(category => category, category => (object)0,
                StringComparer.OrdinalIgnoreCase);
            result["unknown"] = 0;
            foreach (Dictionary<string, object> sample in samples)
            {
                string outcome = ReadString(sample, "outcome", "").Trim().ToLowerInvariant();
                string key = outcome switch
                {
                    "clean_first_attempt" => "cleanFirstAttempts",
                    "successful_repair" => "successfulRepairs",
                    "accepted_repair_override" => "acceptedRepairOverrides",
                    "unusable" => "unusableResponses",
                    "unusable_response" => "unusableResponses",
                    _ => "unknown"
                };
                result[key] = Convert.ToInt32(result[key], CultureInfo.InvariantCulture) + 1;
            }
            return result;
        }

        private static Dictionary<string, object> CodexPerformanceUsageSummary(
            IEnumerable<Dictionary<string, object>> samples)
        {
            List<Dictionary<string, object>> rows = samples.ToList();
            string[] fields = { "inputTokens", "outputTokens", "totalTokens" };
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["sampleCount"] = rows.Count,
                ["knownSampleCount"] = 0,
                ["unknownSampleCount"] = rows.Count
            };
            foreach (string field in fields)
            {
                List<double> values = rows.Select(sample => ReadNullableUsageNumber(
                    ReadDictionary(sample, "usage") ?? ReadDictionary(sample, "actualUsage") ?? sample, field))
                    .Where(value => value.HasValue && value.Value >= 0d).Select(value => value.Value).ToList();
                result[field] = values.Count == 0 ? (object)null : new Dictionary<string, object>
                {
                    ["median"] = Percentile(values, 0.50d),
                    ["p95"] = Percentile(values, 0.95d),
                    ["sampleCount"] = values.Count
                };
                if (field == "totalTokens")
                {
                    result["knownSampleCount"] = values.Count;
                    result["unknownSampleCount"] = rows.Count - values.Count;
                }
            }
            return result;
        }

        private static double? ReadNullableUsageNumber(Dictionary<string, object> source, string field)
        {
            if (field == "inputTokens")
                return CodexReadNullableDouble(source, "inputTokens") ?? CodexReadNullableDouble(source, "prompt_tokens") ?? CodexReadNullableDouble(source, "input_tokens");
            if (field == "outputTokens")
                return CodexReadNullableDouble(source, "outputTokens") ?? CodexReadNullableDouble(source, "completion_tokens") ?? CodexReadNullableDouble(source, "output_tokens");
            if (field == "totalTokens")
                return CodexReadNullableDouble(source, "totalTokens") ?? CodexReadNullableDouble(source, "total_tokens");
            return CodexReadNullableDouble(source, field);
        }

        private static Dictionary<string, object> CodexPerformanceCapabilitySummary(
            IEnumerable<Dictionary<string, object>> samples)
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["requested"] = new Dictionary<string, object>(),
                ["applied"] = new Dictionary<string, object>(),
                ["unsupported"] = new Dictionary<string, object>(),
                ["unknown"] = new Dictionary<string, object>()
            };
            foreach (Dictionary<string, object> sample in samples)
            {
                Dictionary<string, object> nested = ReadDictionary(sample, "capabilities")
                    ?? ReadDictionary(ReadDictionary(sample, "diagnostics"), "capabilities");
                foreach (string state in new[] { "requested", "applied", "unsupported", "unknown" })
                {
                    Dictionary<string, object> target = (Dictionary<string, object>)result[state];
                    Dictionary<string, object> source = ReadDictionary(sample, state) ?? ReadDictionary(nested, state);
                    if (source == null) continue;
                    foreach (KeyValuePair<string, object> pair in source)
                    {
                        target[pair.Key] = pair.Value;
                    }
                }
            }
            return result;
        }

        private static Dictionary<string, object> CodexPerformanceArmSummary(
            IEnumerable<Dictionary<string, object>> samples)
        {
            Dictionary<string, object> arms = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (IGrouping<string, Dictionary<string, object>> group in samples.GroupBy(
                sample => ReadString(sample, "variant", "unknown"), StringComparer.OrdinalIgnoreCase))
            {
                List<Dictionary<string, object>> armSamples = group.ToList();
                arms[group.Key] = new Dictionary<string, object>
                {
                    ["sampleCount"] = armSamples.Count,
                    ["stageTimings"] = CodexPerformanceStageSummary(armSamples),
                    ["outcomes"] = CodexPerformanceOutcomeSummary(armSamples),
                    ["usage"] = CodexPerformanceUsageSummary(armSamples)
                };
            }
            return arms;
        }

        private static Dictionary<string, object> CodexPerformanceModelConsistency(
            Dictionary<string, object> configuration,
            IEnumerable<Dictionary<string, object>> samples)
        {
            string expected = ReadString(configuration, "model", "").Trim();
            var rows = samples.ToList();
            List<string> requestedMismatches = rows.Select(sample => ReadString(sample, "model", "").Trim())
                .Where(actual => !string.IsNullOrWhiteSpace(actual) && !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var calls = rows.SelectMany(sample => ReadDictionaryList(ReadDictionary(sample, "diagnostics"), "calls")).ToList();
            var knownModels = calls.Select(call => ReadDictionary(call, "runtime"))
                .Where(runtime => ReadBool(runtime, "effectiveModelKnown", false))
                .Select(runtime => ReadString(runtime, "servedModel", "").Trim()).Where(model => model.Length > 0).ToList();
            var mismatches = knownModels.Where(model => !string.Equals(model, expected, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            int unknownCalls = calls.Count - knownModels.Count;
            string state = mismatches.Count > 0 || requestedMismatches.Count > 0 ? "mismatch"
                : calls.Count == 0 || unknownCalls > 0 ? "unknown" : "verified";
            return new Dictionary<string, object>
            {
                ["expected"] = string.IsNullOrWhiteSpace(expected) ? "unknown" : expected,
                ["mismatchedModels"] = mismatches,
                ["requestedModelMismatches"] = requestedMismatches,
                ["authoritativeModels"] = knownModels.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ["unknownCallCount"] = unknownCalls,
                ["state"] = state,
                ["ok"] = !string.IsNullOrWhiteSpace(expected) && state == "verified"
            };
        }

        private static Dictionary<string, object> BuildCodexPerformanceFingerprints(Dictionary<string, object> configuration)
        {
            return new Dictionary<string, object>
            {
                ["settings"] = ExactFingerprint(configuration, "settingsFingerprint", "settings"),
                ["schema"] = ReadString(configuration, "schemaFingerprint", "unknown"),
                ["prompt"] = ReadString(configuration, "promptFingerprint", "unknown"),
                ["source"] = ReadString(configuration, "sourceFingerprint", "unknown"),
                ["validation"] = ReadString(configuration, "validationFingerprint", "unknown")
            };
        }

        private static string ExactFingerprint(Dictionary<string, object> configuration, string fingerprintKey, string valueKey)
        {
            string supplied = ReadString(configuration, fingerprintKey, "").Trim();
            if (!string.IsNullOrWhiteSpace(supplied)) return supplied;
            Dictionary<string, object> value = ReadDictionary(configuration, valueKey);
            return value == null ? "unknown" : Fingerprint(value);
        }

        private static string CanonicalValue(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return "<missing>";
            return Json.Serialize(value);
        }

        private static double? CodexReadNullableDouble(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return null;
            if (value is double doubleValue) return doubleValue;
            if (value is float floatValue) return floatValue;
            if (value is decimal decimalValue) return (double)decimalValue;
            if (value is int intValue) return intValue;
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double parsed)) return parsed;
            return null;
        }

        private static double Percentile(List<double> values, double percentile)
        {
            if (values == null || values.Count == 0) return double.NaN;
            List<double> ordered = values.OrderBy(value => value).ToList();
            double index = Math.Max(0d, Math.Min(ordered.Count - 1d, (ordered.Count - 1d) * percentile));
            int lower = (int)Math.Floor(index);
            int upper = (int)Math.Ceiling(index);
            if (lower == upper) return ordered[lower];
            return ordered[lower] + (ordered[upper] - ordered[lower]) * (index - lower);
        }

        private static string Fingerprint(object value)
        {
            string canonical = value is Dictionary<string, object> dictionary
                ? Json.Serialize(dictionary.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal))
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return string.Concat(bytes.Select(valueByte => valueByte.ToString("x2", CultureInfo.InvariantCulture)));
            }
        }
    }
}
