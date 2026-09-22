using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletManifestSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type type = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletManifest");
            MethodInfo build = type?.GetMethod(
                "BuildStageBManifest",
                BindingFlags.Public | BindingFlags.Static);
            add("manifest_contract_exists", build != null,
                "Stage B exposes a frozen bounded manifest builder.");
            if (build == null) return checks;
            Dictionary<string, object> manifest = build.Invoke(
                null, new object[]
                {
                    FinalConversationGauntletCatalog.BuildExecutableCatalog(
                        new[] { "action_a", "action_b" }), 1042
                }) as Dictionary<string, object>;
            List<Dictionary<string, object>> cases =
                ReadDictionaryList(manifest, "cases");
            List<Dictionary<string, object>> scenes = cases.Where(row =>
                ReadString(row, "caseId", "").StartsWith(
                    "GAUNTLET-", StringComparison.OrdinalIgnoreCase)).ToList();
            add("twenty_final_scenes_once",
                scenes.Count == 20
                    && scenes.Select(row => ReadString(row, "caseId", ""))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 20
                    && scenes.All(row => ReadInt(row, "maxExecutions", 0) == 1
                        && ReadBool(row, "continueAfterFailure", false)),
                "All twenty final scenes occur once and never fail fast.");
            Dictionary<string, object> partitions =
                ReadDictionary(manifest, "partitions");
            add("approved_partition_arithmetic",
                ReadInt(partitions, "representative", -1) == 90
                    && ReadInt(partitions, "finalScenes", -1) == 60
                    && ReadInt(partitions, "lng001", -1) == 110
                    && ReadInt(partitions, "lng002", -1) == 44
                    && ReadInt(partitions, "reserve", -1) == 156
                    && ReadInt(manifest, "stageBPlusReserve", -1) == 460
                    && ReadInt(manifest, "absoluteWithStageAMax", -1) == 500,
                "Approved Stage B partitions plus reserve equal 460 and leave 40 calls for Stage A.");
            add("representative_selection_is_bounded",
                cases.Where(row => ReadString(row, "partition", "")
                        == "representative")
                    .Sum(row => ReadInt(row, "estimatedProviderCalls", 0))
                    <= 90,
                "Representative live evidence stays within 90 calls.");
            add("long_horizon_cases_have_literal_budgets",
                cases.Any(row => ReadString(row, "caseId", "") == "LNG-001"
                    && ReadInt(row, "estimatedProviderCalls", 0) == 110)
                    && cases.Any(row => ReadString(row, "caseId", "")
                        == "LNG-002"
                        && ReadInt(row, "estimatedProviderCalls", 0) == 44),
                "Both long-horizon cases carry literal approved budgets.");
            return checks;
        }
    }
}
