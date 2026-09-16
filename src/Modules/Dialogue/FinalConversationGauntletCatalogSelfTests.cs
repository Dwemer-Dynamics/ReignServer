using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletCatalogSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["passed"] = passed,
                    ["message"] = message
                });

            Type catalogType = typeof(Program).Assembly.GetType(
                "ReignBetaServer.FinalConversationGauntletCatalog",
                false);
            MethodInfo build = catalogType?.GetMethod(
                "BuildApprovedCatalog",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo discover = catalogType?.GetMethod(
                "DiscoverFinalGauntletCoverage",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            List<FinalGauntletCaseDescriptor> catalog =
                build?.Invoke(null, null)
                as List<FinalGauntletCaseDescriptor>
                ?? new List<FinalGauntletCaseDescriptor>();

            HashSet<string> expected = BuildExpectedFinalGauntletIds();
            HashSet<string> actual = new HashSet<string>(
                catalog.Select(x => x.CaseId),
                StringComparer.OrdinalIgnoreCase);
            add(
                "approved_catalog_has_every_original_case_once",
                catalog.Count == expected.Count
                    && actual.SetEquals(expected)
                    && catalog.GroupBy(x => x.CaseId, StringComparer.OrdinalIgnoreCase)
                        .All(group => group.Count() == 1),
                "All 447 approved atomic and final-scene identifiers are explicit and unique.");
            add(
                "approved_catalog_preserves_scenario_semantics",
                catalog.Count > 0
                    && catalog.All(x =>
                        !string.IsNullOrWhiteSpace(x.BehavioralRequirement)
                        && x.HardProhibitions != null
                        && x.HardProhibitions.Length > 0
                        && x.PrerequisiteCapabilities != null
                        && x.PrerequisiteCapabilities.Length > 0
                        && x.EvidenceNeeds != null
                        && x.EvidenceNeeds.Length > 0),
                "Every approved case contains a behavioral rule, hard boundary, prerequisite, and evidence contract.");

            object discovery = discover?.Invoke(
                null,
                new object[]
                {
                    new[] { "action_a", "action_b" },
                    new[] { "Honor", "Boldness", "Mercy" },
                    new[] { "Empire", "Vlandia" },
                    new[] { "individual_chat", "party_chat", "social_event", "wilderness_event", "correspondence" },
                    new[] { "ruler", "lord", "notable", "wanderer" },
                    new[] { "hero", "settlement" },
                    new[] { "Favored", "Flirt" }
                });
            List<FinalGauntletCaseDescriptor> generated =
                ReadMember<List<FinalGauntletCaseDescriptor>>(
                    discovery,
                    "GeneratedCases")
                ?? new List<FinalGauntletCaseDescriptor>();
            List<string> missing =
                ReadMember<List<string>>(discovery, "MissingCoverage")
                ?? new List<string>();
            add(
                "runtime_discovery_covers_every_registry_dimension",
                discovery != null
                    && missing.Count == 0
                    && generated.Any(x => x.Tags.Contains("action:action_a"))
                    && generated.Any(x => x.Tags.Contains("trait:Mercy"))
                    && generated.Any(x => x.Tags.Contains("culture:Vlandia"))
                    && generated.Any(x => x.Tags.Contains("mode:correspondence"))
                    && generated.Any(x => x.Tags.Contains("occupation:notable"))
                    && generated.Any(x => x.Tags.Contains("resolver:settlement"))
                    && generated.Any(x => x.Tags.Contains("rumor:Flirt")),
                "Runtime actions, traits, cultures, modes, occupations, resolvers, and rumors all receive traceable coverage.");
            add(
                "honor_boldness_matrix_is_exhaustive",
                generated.Count(x => x.Tags.Contains("matrix:honor_boldness")) == 50,
                "The five-by-five Honor/Boldness matrix is generated for male and female nobles.");
            add(
                "pairwise_axes_are_covered",
                PairwiseCoverageIsComplete(generated),
                "Every value pair across the approved pairwise axes appears in at least one generated fixture.");

            object incomplete = discover?.Invoke(
                null,
                new object[]
                {
                    Array.Empty<string>(),
                    new[] { "Honor" },
                    Array.Empty<string>(),
                    new[] { "individual_chat" },
                    Array.Empty<string>(),
                    Array.Empty<string>(),
                    Array.Empty<string>()
                });
            List<string> incompleteMissing =
                ReadMember<List<string>>(incomplete, "MissingCoverage")
                ?? new List<string>();
            add(
                "empty_runtime_registries_fail_catalog_creation",
                incompleteMissing.Count >= 5,
                "Missing enabled runtime registries are explicit catalog failures.");
            return checks;
        }

        private static HashSet<string> BuildExpectedFinalGauntletIds()
        {
            Dictionary<string, int> ranges =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["CON"] = 16, ["IDN"] = 16, ["SIT"] = 20, ["WLD"] = 22,
                    ["PER"] = 24, ["REL"] = 25, ["ROM"] = 15, ["MEM"] = 40,
                    ["GRP"] = 28, ["RUM"] = 28, ["ACT"] = 35, ["STA"] = 12,
                    ["MOD"] = 16, ["SOC"] = 18, ["ADV"] = 18, ["ROB"] = 24,
                    ["DIF"] = 20, ["STO"] = 10, ["LNG"] = 20,
                    ["GAUNTLET"] = 20
                };
            HashSet<string> ids =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> range in ranges)
                for (int number = 1; number <= range.Value; number++)
                    ids.Add(range.Key + "-" + number.ToString("000"));
            return ids;
        }

        private static T ReadMember<T>(object instance, string name)
        {
            if (instance == null) return default(T);
            Type type = instance.GetType();
            FieldInfo field = type.GetField(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field != null && field.GetValue(instance) is T fieldValue)
                return fieldValue;
            PropertyInfo property = type.GetProperty(
                name,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.GetValue(instance) is T propertyValue)
                return propertyValue;
            return default(T);
        }

        private static bool PairwiseCoverageIsComplete(
            List<FinalGauntletCaseDescriptor> generated)
        {
            string[] axes =
            {
                "mode:", "npc_type:", "relationship:", "honor:", "boldness:",
                "other_traits:", "status:", "privacy:", "familiarity:",
                "political:", "family:", "time:", "location:", "action_state:",
                "memory_state:", "rumor_state:"
            };
            List<FinalGauntletCaseDescriptor> pairwise = generated
                .Where(x => x.Tags.Contains("matrix:pairwise"))
                .ToList();
            for (int left = 0; left < axes.Length; left++)
            {
                string[] leftValues = pairwise
                    .SelectMany(x => x.Tags)
                    .Where(x => x.StartsWith(axes[left], StringComparison.Ordinal))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                for (int right = left + 1; right < axes.Length; right++)
                {
                    string[] rightValues = pairwise
                        .SelectMany(x => x.Tags)
                        .Where(x => x.StartsWith(axes[right], StringComparison.Ordinal))
                        .Distinct(StringComparer.Ordinal)
                        .ToArray();
                    foreach (string leftValue in leftValues)
                        foreach (string rightValue in rightValues)
                            if (!pairwise.Any(x =>
                                x.Tags.Contains(leftValue)
                                && x.Tags.Contains(rightValue)))
                                return false;
                }
            }
            return pairwise.Count > 0;
        }
    }
}
