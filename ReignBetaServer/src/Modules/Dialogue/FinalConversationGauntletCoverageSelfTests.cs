using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletCoverageSelfTests()
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

            Type type = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletCoverage");
            MethodInfo classify = type?.GetMethod(
                "ClassifyCatalog",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo build = type?.GetMethod(
                "BuildLiveCoveringSet",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo validate = type?.GetMethod(
                "ValidateCoverage",
                BindingFlags.Static | BindingFlags.Public);
            add(
                "coverage_contract_exists",
                classify != null && build != null && validate != null,
                "The gauntlet exposes catalog classification, bounded live selection, and complete coverage validation.");
            if (classify == null || build == null || validate == null)
                return checks;

            List<FinalGauntletCaseDescriptor> catalog =
                FinalConversationGauntletCatalog.BuildExecutableCatalog(
                    new[] { "action_a", "action_b" });
            classify.Invoke(null, new object[] { catalog });
            List<FinalGauntletCaseDescriptor> first = build.Invoke(
                null,
                new object[] { catalog, 1042, 90 })
                as List<FinalGauntletCaseDescriptor>
                ?? new List<FinalGauntletCaseDescriptor>();
            List<FinalGauntletCaseDescriptor> second = build.Invoke(
                null,
                new object[] { catalog, 1042, 90 })
                as List<FinalGauntletCaseDescriptor>
                ?? new List<FinalGauntletCaseDescriptor>();

            add(
                "live_covering_set_has_approved_shape",
                first.Count(x => x.Tags.Contains("dense:single")) == 25
                    && first.Count(x => x.Tags.Contains("dense:group")) == 10
                    && first.Sum(x =>
                        ReadMember<int>(
                            x, "EstimatedProviderCalls")) == 90,
                "The live covering set contains twenty-five two-turn individual fixtures and ten two-turn, two-NPC group fixtures.");
            add(
                "live_covering_set_is_family_coherent",
                first.All(row =>
                    (ReadMember<string[]>(
                        row, "RepresentedRequirementIds")
                        ?? Array.Empty<string>())
                    .Select(id => id.Split('-')[0])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() == 1),
                "Every dense fixture maps requirements from one coherent behavior family rather than unrelated round-robin assertions.");
            add(
                "live_covering_set_carries_real_objectives",
                first.All(row =>
                    !string.IsNullOrWhiteSpace(
                        ReadMember<string>(
                            row, "BehavioralRequirement"))
                    && ReadMember<string>(
                            row, "BehavioralRequirement")
                        .Contains(":", StringComparison.Ordinal)),
                "Each dense fixture carries the real source-case behavioral objectives for semantic grading instead of crediting attached IDs alone.");
            add(
                "live_covering_set_is_seed_deterministic",
                first.Select(x => x.CaseId).SequenceEqual(
                    second.Select(x => x.CaseId),
                    StringComparer.Ordinal)
                    && first.SelectMany(x =>
                            ReadMember<string[]>(
                                x, "RepresentedRequirementIds")
                            ?? Array.Empty<string>())
                        .SequenceEqual(
                            second.SelectMany(
                                x => ReadMember<string[]>(
                                    x, "RepresentedRequirementIds")
                                    ?? Array.Empty<string>()),
                            StringComparer.Ordinal),
                "The same catalog and seed produce the same ordered cases and requirement mapping.");
            HashSet<string> groupModes = new HashSet<string>(
                first.Where(x => x.Tags.Contains("dense:group"))
                    .Select(x => x.Mode),
                StringComparer.OrdinalIgnoreCase);
            add(
                "live_group_cover_uses_only_implemented_modes",
                groupModes.SetEquals(new[]
                    {
                        "party_chat", "social_event", "wilderness_event"
                    })
                    && !first.Any(x => x.Mode.Equals(
                        "court_event",
                        StringComparison.OrdinalIgnoreCase)),
                "Dense group coverage rotates only through implemented party, tournament/social, and wilderness adapters.");

            List<string> errors = validate.Invoke(
                null,
                new object[] { catalog, first })
                as List<string> ?? new List<string>();
            add(
                "all_exhaustive_requirements_are_mapped",
                errors.Count == 0
                    && catalog.All(x =>
                        !string.IsNullOrWhiteSpace(
                            ReadMember<string>(x, "CoverageKind"))),
                "Every exhaustive catalog row maps to deterministic, represented-live, dedicated-live, long-horizon, or final-scene evidence.");

            string represented = first
                .SelectMany(x =>
                    ReadMember<string[]>(
                        x, "RepresentedRequirementIds")
                    ?? Array.Empty<string>())
                .FirstOrDefault();
            foreach (FinalGauntletCaseDescriptor row in first)
            {
                FieldInfo field = row.GetType().GetField(
                    "RepresentedRequirementIds",
                    BindingFlags.Instance | BindingFlags.Public
                    | BindingFlags.NonPublic);
                string[] values = ReadMember<string[]>(
                    row, "RepresentedRequirementIds")
                    ?? Array.Empty<string>();
                field?.SetValue(
                    row,
                    values
                        .Where(id => !string.Equals(
                            id, represented,
                            StringComparison.OrdinalIgnoreCase))
                        .ToArray());
            }
            List<string> missing = validate.Invoke(
                null,
                new object[] { catalog, first })
                as List<string> ?? new List<string>();
            add(
                "missing_representative_fails_coverage",
                !string.IsNullOrWhiteSpace(represented)
                    && missing.Any(value =>
                        value.IndexOf(
                            represented,
                            StringComparison.OrdinalIgnoreCase) >= 0),
                "Removing the only live representative for a requirement creates an explicit coverage failure.");
            return checks;
        }
    }
}
