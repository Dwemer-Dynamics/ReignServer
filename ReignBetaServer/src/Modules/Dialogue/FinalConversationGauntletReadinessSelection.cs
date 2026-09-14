using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletReadinessSelection
    {
        private static readonly string[] ReadinessCategories =
        {
            "structural_pipeline",
            "identity_evidence",
            "identity_role_authority",
            "short_cross_scene_memory",
            "long_term_memory",
            "dynamic_characteristics",
            "shared_relationship_history",
            "group_awareness",
            "world_local_knowledge",
            "lie_relationship",
            "clan_tier_recognition",
            "manipulation_capabilities",
            "personality_consistency",
            "detailed_factual_accuracy",
            "performance_resilience"
        };

        public static Dictionary<string, object> SelectMissingReadinessCases(
            List<FinalGauntletCaseDescriptor> catalog,
            Dictionary<string, object> scorecard,
            string currentBuildVersion,
            int maximumProviderCalls)
        {
            HashSet<string> high = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            bool sameBuild = Bool(scorecard, "sameBuild", false)
                && string.Equals(
                    Text(scorecard, "currentBuildVersion"),
                    currentBuildVersion ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            if (sameBuild)
            {
                foreach (Dictionary<string, object> row in
                    DictionaryList(scorecard, "categories"))
                {
                    if (string.Equals(
                        Text(row, "status"),
                        "high",
                        StringComparison.OrdinalIgnoreCase))
                        high.Add(Text(row, "category"));
                }
            }

            List<FinalGauntletCaseDescriptor> selected =
                new List<FinalGauntletCaseDescriptor>();
            foreach (string category in ReadinessCategories)
            {
                if (high.Contains(category))
                    continue;
                selected.Add(BuildProbe(
                    category, IsGroupCategory(category)));
            }

            int estimated = selected.Sum(item =>
                Math.Max(0, item.EstimatedProviderCalls));
            if (estimated > Math.Max(0, maximumProviderCalls))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "The complete missing-evidence selection requires "
                        + estimated + " provider calls, which exceeds the Stage A budget of "
                        + Math.Max(0, maximumProviderCalls) + ".",
                    ["estimatedProviderCalls"] = estimated,
                    ["requiredProviderCalls"] = estimated,
                    ["cases"] = new List<object>(),
                    ["descriptors"] =
                        new List<FinalGauntletCaseDescriptor>()
                };
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["sameBuildEvidence"] = sameBuild,
                ["estimatedProviderCalls"] = estimated,
                ["missingCategories"] = selected.Select(item =>
                    item.Tags.First(tag => tag.StartsWith(
                        "readiness:",
                        StringComparison.OrdinalIgnoreCase))
                    .Substring("readiness:".Length)).Cast<object>().ToList(),
                ["cases"] = selected.Select(CaseMap).Cast<object>().ToList(),
                ["descriptors"] = selected
            };
        }

        public static Dictionary<string, object>
            SelectRequestedReadinessCases(
                IEnumerable<string> requestedCaseIds,
                int maximumProviderCalls)
        {
            List<string> requested = (requestedCaseIds
                    ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<string> categories = new List<string>();
            List<string> unknown = new List<string>();
            foreach (string value in requested)
            {
                string category = value.StartsWith(
                        "STAGEA-",
                        StringComparison.OrdinalIgnoreCase)
                    ? value.Substring("STAGEA-".Length)
                        .Replace('-', '_').ToLowerInvariant()
                    : value.Replace('-', '_').ToLowerInvariant();
                string canonical = ReadinessCategories.FirstOrDefault(
                    candidate => candidate.Equals(
                        category,
                        StringComparison.OrdinalIgnoreCase));
                if (string.IsNullOrWhiteSpace(canonical))
                    unknown.Add(value);
                else if (!categories.Contains(
                    canonical, StringComparer.OrdinalIgnoreCase))
                    categories.Add(canonical);
            }
            if (unknown.Count > 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Unknown Stage A readiness case(s): "
                        + string.Join(", ", unknown),
                    ["estimatedProviderCalls"] = 0,
                    ["missingCategories"] = new List<object>(),
                    ["descriptors"] =
                        new List<FinalGauntletCaseDescriptor>()
                };

            List<FinalGauntletCaseDescriptor> selected = categories
                .Select(category => BuildProbe(
                    category, IsGroupCategory(category)))
                .ToList();
            int estimated = selected.Sum(item =>
                Math.Max(0, item.EstimatedProviderCalls));
            bool withinBudget = estimated <= Math.Max(
                0, maximumProviderCalls);
            return new Dictionary<string, object>
            {
                ["ok"] = withinBudget,
                ["error"] = withinBudget
                    ? string.Empty
                    : "The requested Stage A readiness cases require "
                        + estimated + " provider calls, which exceeds the "
                        + "Stage A budget of "
                        + Math.Max(0, maximumProviderCalls) + ".",
                ["estimatedProviderCalls"] = estimated,
                ["missingCategories"] = categories
                    .Cast<object>().ToList(),
                ["cases"] = selected.Select(CaseMap)
                    .Cast<object>().ToList(),
                ["descriptors"] = selected
            };
        }

        private static bool IsGroupCategory(string category)
        {
            return string.Equals(
                    category,
                    "group_awareness",
                    StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    category,
                    "shared_relationship_history",
                    StringComparison.OrdinalIgnoreCase);
        }

        private static FinalGauntletCaseDescriptor BuildProbe(
            string category,
            bool group)
        {
            string requirement = "READINESS-"
                + category.Replace('_', '-').ToUpperInvariant();
            return new FinalGauntletCaseDescriptor
            {
                CaseId = "STAGEA-" + category.ToUpperInvariant(),
                Family = "readiness_qualification",
                EvaluationKind = "hard_and_semantic",
                ExecutionKind = "production_live",
                Mode = group ? "party_chat" : "individual_chat",
                RequiresProvider = true,
                RequiresGame = true,
                RequirementIds = new[] { requirement },
                Tags = new[]
                {
                    "stage_a",
                    "readiness:" + category,
                    group ? "dense:group" : "dense:single"
                },
                BehavioralRequirement =
                    "Provide fresh same-build evidence for " + category + ".",
                HardProhibitions = new[]
                {
                    "wrong_identity",
                    "unsupported_world_mutation",
                    "private_knowledge_leak"
                },
                PrerequisiteCapabilities = new[]
                {
                    "live_interaction_bridge",
                    "production_prompt_pipeline"
                },
                EvidenceNeeds = new[]
                {
                    "prompt",
                    "raw_response",
                    "parsed_response",
                    "state_before_after"
                },
                CoverageKind =
                    FinalGauntletCoverageKind.DedicatedLive.ToString(),
                EstimatedProviderCalls = group ? 4 : 2,
                RepresentedRequirementIds = new[] { requirement },
                RiskWeight = group ? 10 : 8
            };
        }

        private static Dictionary<string, object> CaseMap(
            FinalGauntletCaseDescriptor descriptor)
        {
            string category = descriptor.Tags.First(tag =>
                tag.StartsWith(
                    "readiness:",
                    StringComparison.OrdinalIgnoreCase))
                .Substring("readiness:".Length);
            return new Dictionary<string, object>
            {
                ["caseId"] = descriptor.CaseId,
                ["mode"] = descriptor.Mode,
                ["estimatedProviderCalls"] =
                    descriptor.EstimatedProviderCalls,
                ["readinessCategories"] =
                    new List<object> { category }
            };
        }

        private static List<Dictionary<string, object>> DictionaryList(
            Dictionary<string, object> source,
            string key)
        {
            if (source == null || !source.TryGetValue(key, out object value)
                || value == null)
                return new List<Dictionary<string, object>>();
            if (value is List<Dictionary<string, object>> typed)
                return typed;
            if (value is IEnumerable<object> sequence)
                return sequence
                    .OfType<Dictionary<string, object>>()
                    .ToList();
            return new List<Dictionary<string, object>>();
        }

        private static string Text(
            Dictionary<string, object> source,
            string key)
        {
            return source != null
                && source.TryGetValue(key, out object value)
                && value != null
                ? Convert.ToString(value) ?? string.Empty
                : string.Empty;
        }

        private static bool Bool(
            Dictionary<string, object> source,
            string key,
            bool fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value)
                || value == null)
                return fallback;
            if (value is bool typed)
                return typed;
            return bool.TryParse(Convert.ToString(value), out bool parsed)
                ? parsed
                : fallback;
        }
    }
}
