using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletCoverage
    {
        private const int SingleFixtureCount = 25;
        private const int GroupFixtureCount = 10;
        private const int RequiredProviderCalls = 90;

        public static void ClassifyCatalog(
            List<FinalGauntletCaseDescriptor> catalog)
        {
            foreach (FinalGauntletCaseDescriptor descriptor in
                catalog ?? new List<FinalGauntletCaseDescriptor>())
            {
                if (descriptor == null) continue;
                if (descriptor.CaseId.Equals(
                        "LNG-001", StringComparison.OrdinalIgnoreCase)
                    || descriptor.CaseId.Equals(
                        "LNG-002", StringComparison.OrdinalIgnoreCase))
                {
                    descriptor.CoverageKind =
                        FinalGauntletCoverageKind.LongHorizon.ToString();
                    descriptor.EstimatedProviderCalls =
                        descriptor.CaseId.EndsWith(
                            "001", StringComparison.Ordinal) ? 110 : 44;
                    descriptor.RepresentedRequirementIds =
                        descriptor.RequirementIds ?? Array.Empty<string>();
                }
                else if (descriptor.CaseId.StartsWith(
                    "GAUNTLET-", StringComparison.OrdinalIgnoreCase))
                {
                    descriptor.CoverageKind =
                        FinalGauntletCoverageKind.FinalScene.ToString();
                    descriptor.EstimatedProviderCalls = 3;
                    descriptor.RepresentedRequirementIds =
                        descriptor.RequirementIds ?? Array.Empty<string>();
                }
                else if (IsGeneratedDeterministic(descriptor))
                {
                    descriptor.CoverageKind =
                        FinalGauntletCoverageKind.Deterministic.ToString();
                    descriptor.EstimatedProviderCalls = 0;
                    descriptor.RepresentedRequirementIds =
                        descriptor.RequirementIds ?? Array.Empty<string>();
                }
                else
                {
                    descriptor.CoverageKind =
                        FinalGauntletCoverageKind.RepresentedLive.ToString();
                    descriptor.EstimatedProviderCalls = 0;
                    descriptor.RepresentedRequirementIds =
                        Array.Empty<string>();
                }
                descriptor.RiskWeight = RiskWeight(descriptor);
            }
        }

        public static List<FinalGauntletCaseDescriptor>
            BuildLiveCoveringSet(
                List<FinalGauntletCaseDescriptor> catalog,
                int seed,
                int maximumProviderCalls)
        {
            if (maximumProviderCalls < RequiredProviderCalls)
                throw new InvalidOperationException(
                    "The approved dense live covering set requires exactly "
                    + RequiredProviderCalls + " provider calls.");
            ClassifyCatalog(catalog);
            List<string> requirements = (catalog
                    ?? new List<FinalGauntletCaseDescriptor>())
                .Where(item => item != null
                    && item.CoverageKind.Equals(
                        FinalGauntletCoverageKind.RepresentedLive.ToString(),
                        StringComparison.Ordinal))
                .SelectMany(item =>
                    item.RequirementIds ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (requirements.Count == 0)
                throw new InvalidOperationException(
                    "The exhaustive catalog contains no represented-live requirements.");

            HashSet<string> groupFamilies = new HashSet<string>(
                new[] { "GRP", "MOD", "ROM", "RUM" },
                StringComparer.OrdinalIgnoreCase);
            List<List<string>> singleBuckets = BuildFamilyBuckets(
                requirements.Where(value => !groupFamilies.Contains(
                    RequirementFamily(value))),
                SingleFixtureCount,
                seed);
            List<List<string>> groupBuckets = BuildFamilyBuckets(
                requirements.Where(value => groupFamilies.Contains(
                    RequirementFamily(value))),
                GroupFixtureCount,
                seed + 7919);

            List<FinalGauntletCaseDescriptor> selected =
                new List<FinalGauntletCaseDescriptor>();
            for (int index = 0; index < SingleFixtureCount; index++)
            {
                FinalGauntletCaseDescriptor dense = DenseDescriptor(
                    "LIVE-SINGLE-" + (index + 1).ToString("000"),
                    "individual_chat", 2, singleBuckets[index],
                    "dense:single", index);
                EnrichDenseDescriptor(dense, catalog);
                selected.Add(dense);
            }
            for (int index = 0; index < GroupFixtureCount; index++)
            {
                FinalGauntletCaseDescriptor dense = DenseDescriptor(
                    "LIVE-GROUP-" + (index + 1).ToString("000"),
                    index % 3 == 0
                        ? "social_event"
                        : index % 3 == 1
                            ? "party_chat"
                            : "wilderness_event",
                    4,
                    groupBuckets[index],
                    "dense:group",
                    index);
                EnrichDenseDescriptor(dense, catalog);
                selected.Add(dense);
            }
            return selected;
        }

        public static List<string> ValidateCoverage(
            List<FinalGauntletCaseDescriptor> catalog,
            List<FinalGauntletCaseDescriptor> liveCoveringSet)
        {
            List<string> errors = new List<string>();
            HashSet<string> represented = new HashSet<string>(
                (liveCoveringSet
                    ?? new List<FinalGauntletCaseDescriptor>())
                .SelectMany(item =>
                    item.RepresentedRequirementIds
                    ?? Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase);
            foreach (FinalGauntletCaseDescriptor descriptor in
                catalog ?? new List<FinalGauntletCaseDescriptor>())
            {
                if (descriptor == null) continue;
                if (string.IsNullOrWhiteSpace(descriptor.CoverageKind))
                {
                    errors.Add(
                        descriptor.CaseId + " has no coverage kind.");
                    continue;
                }
                if (!descriptor.CoverageKind.Equals(
                        FinalGauntletCoverageKind.RepresentedLive.ToString(),
                        StringComparison.Ordinal))
                    continue;
                foreach (string requirement in
                    descriptor.RequirementIds ?? Array.Empty<string>())
                    if (!represented.Contains(requirement))
                        errors.Add(
                            requirement
                            + " has no selected live representative.");
            }
            int estimated = (liveCoveringSet
                    ?? new List<FinalGauntletCaseDescriptor>())
                .Sum(item => item.EstimatedProviderCalls);
            if (estimated > RequiredProviderCalls)
                errors.Add(
                    "Live covering set exceeds 90 calls: "
                    + estimated + ".");
            return errors;
        }

        private static FinalGauntletCaseDescriptor DenseDescriptor(
            string caseId,
            string mode,
            int calls,
            List<string> represented,
            string densityTag,
            int ordinal)
        {
            string[] requirements = (represented ?? new List<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            return new FinalGauntletCaseDescriptor
            {
                CaseId = caseId,
                Family = densityTag == "dense:group"
                    ? "dense_group_coverage"
                    : "dense_single_coverage",
                EvaluationKind = "hard_and_semantic",
                ExecutionKind = densityTag == "dense:group"
                    ? "two_turn_two_npc_dense_fixture"
                    : "two_turn_single_npc_dense_fixture",
                Mode = mode,
                RequiresProvider = true,
                RequiresGame = true,
                RequirementIds = requirements,
                RepresentedRequirementIds = requirements,
                Tags = new[]
                {
                    "live_covering_set",
                    densityTag,
                    "family:" + RequirementFamily(requirements[0]),
                    "ordinal:" + ordinal.ToString("000")
                },
                BehavioralRequirement =
                    "Exercise every mapped requirement in one coherent "
                    + (densityTag == "dense:group"
                        ? "two-turn, two-NPC group scene."
                        : "two-turn individual scene."),
                HardProhibitions = new[]
                {
                    "Do not sacrifice identity, live state, knowledge boundaries, or action safety while combining assertions."
                },
                PrerequisiteCapabilities = new[]
                {
                    "production_prompt_builder",
                    "live_interaction_bridge",
                    "complete_evidence_capture"
                },
                EvidenceNeeds = new[]
                {
                    "production_prompt",
                    "parsed_reply",
                    "state_before_and_after",
                    "mapped_requirement_assertions"
                },
                CoverageKind =
                    FinalGauntletCoverageKind.DedicatedLive.ToString(),
                EstimatedProviderCalls = calls,
                RiskWeight = 100
            };
        }

        private static List<List<string>> BuildFamilyBuckets(
            IEnumerable<string> source,
            int bucketCount,
            int seed)
        {
            List<IGrouping<string, string>> families = (source
                    ?? Enumerable.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .GroupBy(RequirementFamily, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => StableTieKey(group.Key, seed))
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .ToList();
            if (families.Count == 0 || families.Count > bucketCount)
                throw new InvalidOperationException(
                    "Cannot build " + bucketCount
                    + " coherent live buckets from " + families.Count
                    + " requirement families.");

            Dictionary<string, int> allocations = families.ToDictionary(
                group => group.Key,
                _ => 1,
                StringComparer.OrdinalIgnoreCase);
            while (allocations.Values.Sum() < bucketCount)
            {
                IGrouping<string, string> selected = families
                    .Where(group => allocations[group.Key] < group.Count())
                    .OrderByDescending(group =>
                        (double)group.Count()
                        / (allocations[group.Key] + 1))
                    .ThenBy(group => StableTieKey(group.Key, seed))
                    .ThenBy(group => group.Key, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (selected == null)
                    throw new InvalidOperationException(
                        "There are not enough represented-live requirements "
                        + "to fill the approved coherent bucket count.");
                allocations[selected.Key]++;
            }

            List<List<string>> result = new List<List<string>>();
            foreach (IGrouping<string, string> family in families)
            {
                int count = allocations[family.Key];
                List<List<string>> familyBuckets = Enumerable.Range(0, count)
                    .Select(_ => new List<string>())
                    .ToList();
                List<string> values = family
                    .OrderBy(value => StableTieKey(value, seed))
                    .ThenBy(value => value, StringComparer.Ordinal)
                    .ToList();
                for (int index = 0; index < values.Count; index++)
                    familyBuckets[index % count].Add(values[index]);
                result.AddRange(familyBuckets);
            }
            if (result.Count != bucketCount
                || result.Any(bucket => bucket.Count == 0))
                throw new InvalidOperationException(
                    "The coherent live covering set did not fill every bucket.");
            return result;
        }

        private static string RequirementFamily(string requirementId)
        {
            return (requirementId ?? string.Empty)
                .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "OTHER";
        }

        private static void EnrichDenseDescriptor(
            FinalGauntletCaseDescriptor dense,
            IEnumerable<FinalGauntletCaseDescriptor> catalog)
        {
            HashSet<string> represented = new HashSet<string>(
                dense.RepresentedRequirementIds ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            List<FinalGauntletCaseDescriptor> source = (catalog
                    ?? Enumerable.Empty<FinalGauntletCaseDescriptor>())
                .Where(row => (row.RequirementIds
                        ?? Array.Empty<string>())
                    .Any(represented.Contains))
                .OrderBy(row => row.CaseId, StringComparer.Ordinal)
                .ToList();
            dense.BehavioralRequirement = string.Join(
                " | ",
                source.Select(row => row.CaseId + ": "
                    + row.BehavioralRequirement));
            dense.HardProhibitions = source
                .SelectMany(row => row.HardProhibitions
                    ?? Array.Empty<string>())
                .Concat(dense.HardProhibitions
                    ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static bool IsGeneratedDeterministic(
            FinalGauntletCaseDescriptor descriptor)
        {
            string id = descriptor.CaseId ?? string.Empty;
            return id.StartsWith("DISC-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(
                    "ACTION-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("PAIR-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("HB-", StringComparison.OrdinalIgnoreCase);
        }

        private static int RiskWeight(
            FinalGauntletCaseDescriptor descriptor)
        {
            string family = descriptor.Family ?? string.Empty;
            if (new[]
                {
                    "output_contract", "identity_biography",
                    "world_knowledge", "memory", "group_awareness",
                    "action_conformance", "state_consequence",
                    "adversarial", "robustness"
                }.Contains(family, StringComparer.OrdinalIgnoreCase))
                return 100;
            if (new[]
                {
                    "personality_agency", "relationship_hierarchy",
                    "romance", "conflict_manipulation",
                    "situational_awareness"
                }.Contains(family, StringComparer.OrdinalIgnoreCase))
                return 70;
            return 40;
        }

        private static uint StableTieKey(string value, int seed)
        {
            unchecked
            {
                uint hash = 2166136261u ^ (uint)seed;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }
                return hash;
            }
        }
    }
}
