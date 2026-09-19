using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletReadinessSelectionSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type type = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletReadinessSelection");
            MethodInfo select = type?.GetMethod(
                "SelectMissingReadinessCases",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo selectRequested = type?.GetMethod(
                "SelectRequestedReadinessCases",
                BindingFlags.Static | BindingFlags.Public);
            add(
                "readiness_selection_contract_exists",
                select != null && selectRequested != null,
                "Stage A exposes a deterministic missing-evidence selector.");
            if (select == null || selectRequested == null) return checks;

            List<FinalGauntletCaseDescriptor> catalog =
                FinalConversationGauntletCatalog.BuildExecutableCatalog(
                    new[] { "action_a" });
            Dictionary<string, object> allHigh =
                ReadinessScorecard(true, Array.Empty<string>());
            Dictionary<string, object> empty = select.Invoke(
                null,
                new object[] { catalog, allHigh, "build-a", 40 })
                as Dictionary<string, object>;
            add(
                "compatible_high_evidence_costs_zero",
                ReadBool(empty, "ok", false)
                    && ReadInt(empty, "estimatedProviderCalls", -1) == 0
                    && ReadDictionaryList(empty, "cases").Count == 0,
                "Compatible categories already marked high are not regenerated.");

            Dictionary<string, object> oneGap =
                ReadinessScorecard(
                    true, new[] { "group_awareness" });
            Dictionary<string, object> oneSelected = select.Invoke(
                null,
                new object[] { catalog, oneGap, "build-a", 40 })
                as Dictionary<string, object>;
            List<Dictionary<string, object>> oneCases =
                ReadDictionaryList(oneSelected, "cases");
            add(
                "only_missing_category_is_selected",
                ReadBool(oneSelected, "ok", false)
                    && ReadInt(
                        oneSelected,
                        "estimatedProviderCalls", 0) == 4
                    && oneCases.Count == 1
                    && ReadStringList(
                        oneCases[0], "readinessCategories")
                        .SequenceEqual(
                            new[] { "group_awareness" },
                            StringComparer.OrdinalIgnoreCase),
                "A single missing group category schedules one four-call group probe and nothing else.");

            Dictionary<string, object> stale =
                ReadinessScorecard(
                    false, Array.Empty<string>());
            Dictionary<string, object> staleSelected = select.Invoke(
                null,
                new object[] { catalog, stale, "build-b", 40 })
                as Dictionary<string, object>;
            add(
                "incompatible_build_does_not_reuse_high_status",
                ReadBool(staleSelected, "ok", false)
                    && ReadDictionaryList(
                        staleSelected, "cases").Count > 0
                    && ReadInt(
                        staleSelected,
                        "estimatedProviderCalls", 0) <= 40,
                "A build mismatch schedules bounded fresh proof instead of silently carrying high labels.");

            Dictionary<string, object> tooSmall = select.Invoke(
                null,
                new object[] { catalog, oneGap, "build-a", 3 })
                as Dictionary<string, object>;
            add(
                "unfit_stage_a_is_rejected",
                !ReadBool(tooSmall, "ok", true)
                    && ReadString(
                        tooSmall, "error", "")
                        .IndexOf(
                            "budget",
                            StringComparison.OrdinalIgnoreCase) >= 0,
                "Stage A fails explicitly when its complete missing-evidence set cannot fit the configured partition.");

            Dictionary<string, object> targeted = selectRequested.Invoke(
                null,
                new object[]
                {
                    new[] { "STAGEA-SHARED_RELATIONSHIP_HISTORY" },
                    40
                }) as Dictionary<string, object>;
            List<Dictionary<string, object>> targetedCases =
                ReadDictionaryList(targeted, "cases");
            add(
                "targeted_stage_a_readiness_retry_is_resolvable",
                ReadBool(targeted, "ok", false)
                    && ReadInt(
                        targeted,
                        "estimatedProviderCalls", 0) == 4
                    && targetedCases.Count == 1
                    && ReadString(
                        targetedCases[0], "caseId", "")
                        .Equals(
                            "STAGEA-SHARED_RELATIONSHIP_HISTORY",
                            StringComparison.OrdinalIgnoreCase),
                "A failed synthetic Stage A readiness case can be rerun alone, and shared relationship history reserves its full group-call budget.");
            return checks;
        }

        private static Dictionary<string, object> ReadinessScorecard(
            bool sameBuild,
            IEnumerable<string> qualifying)
        {
            HashSet<string> missing = new HashSet<string>(
                qualifying ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            string[] categories =
            {
                "structural_pipeline", "identity_evidence",
                "identity_role_authority", "short_cross_scene_memory",
                "long_term_memory", "dynamic_characteristics",
                "shared_relationship_history", "group_awareness",
                "world_local_knowledge", "lie_relationship",
                "clan_tier_recognition", "manipulation_capabilities",
                "personality_consistency", "detailed_factual_accuracy",
                "performance_resilience"
            };
            return new Dictionary<string, object>
            {
                ["sameBuild"] = sameBuild,
                ["currentBuildVersion"] = sameBuild ? "build-a" : "build-b",
                ["categories"] = categories.Select(category =>
                    (object)new Dictionary<string, object>
                    {
                        ["category"] = category,
                        ["status"] = missing.Contains(category)
                            ? "qualifying" : "high"
                    }).ToList()
            };
        }
    }
}
