using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletManifest
    {
        public const int RepresentativeBudget = 90;
        public const int FinalSceneBudget = 60;
        public const int LongHorizonOneBudget = 110;
        public const int LongHorizonTwoBudget = 44;
        public const int ReserveBudget = 156;
        public const int StageAMaximum = 40;

        public static Dictionary<string, object> BuildStageBManifest(
            List<FinalGauntletCaseDescriptor> catalog,
            int seed)
        {
            List<FinalGauntletCaseDescriptor> source =
                catalog ?? new List<FinalGauntletCaseDescriptor>();
            List<FinalGauntletCaseDescriptor> selected =
                new List<FinalGauntletCaseDescriptor>();
            foreach (FinalGauntletCaseDescriptor descriptor in
                FinalConversationGauntletCoverage.BuildLiveCoveringSet(
                    source, seed, RepresentativeBudget))
                selected.Add(Partition(descriptor, "representative", 1, true));
            foreach (FinalGauntletCaseDescriptor descriptor in source.Where(
                item => item.CaseId.StartsWith(
                    "GAUNTLET-", StringComparison.OrdinalIgnoreCase)))
                selected.Add(Partition(descriptor, "finalScenes", 1, true));
            FinalGauntletCaseDescriptor lng001 = source.First(item =>
                item.CaseId.Equals("LNG-001", StringComparison.OrdinalIgnoreCase));
            FinalGauntletCaseDescriptor lng002 = source.First(item =>
                item.CaseId.Equals("LNG-002", StringComparison.OrdinalIgnoreCase));
            lng001.EstimatedProviderCalls = LongHorizonOneBudget;
            lng002.EstimatedProviderCalls = LongHorizonTwoBudget;
            selected.Add(Partition(lng001, "lng001", 1, true));
            selected.Add(Partition(lng002, "lng002", 1, true));

            return new Dictionary<string, object>
            {
                ["schema"] = "reign-final-gauntlet-manifest-v2",
                ["seed"] = seed,
                ["partitions"] = new Dictionary<string, object>
                {
                    ["representative"] = RepresentativeBudget,
                    ["finalScenes"] = FinalSceneBudget,
                    ["lng001"] = LongHorizonOneBudget,
                    ["lng002"] = LongHorizonTwoBudget,
                    ["reserve"] = ReserveBudget
                },
                ["stageBPlusReserve"] = RepresentativeBudget
                    + FinalSceneBudget + LongHorizonOneBudget
                    + LongHorizonTwoBudget + ReserveBudget,
                ["absoluteWithStageAMax"] = RepresentativeBudget
                    + FinalSceneBudget + LongHorizonOneBudget
                    + LongHorizonTwoBudget + ReserveBudget
                    + StageAMaximum,
                ["cases"] = selected.Select(Map).Cast<object>().ToList(),
                ["descriptors"] = selected
            };
        }

        private static FinalGauntletCaseDescriptor Partition(
            FinalGauntletCaseDescriptor descriptor,
            string partition,
            int maxExecutions,
            bool continueAfterFailure)
        {
            descriptor.Tags = (descriptor.Tags ?? Array.Empty<string>())
                .Where(tag => !tag.StartsWith(
                    "partition:", StringComparison.OrdinalIgnoreCase)
                    && !tag.StartsWith(
                        "max_executions:", StringComparison.OrdinalIgnoreCase))
                .Concat(new[]
                {
                    "partition:" + partition,
                    "max_executions:" + maxExecutions,
                    "continue_after_failure:" + continueAfterFailure
                        .ToString().ToLowerInvariant()
                }).ToArray();
            return descriptor;
        }

        private static Dictionary<string, object> Map(
            FinalGauntletCaseDescriptor descriptor)
        {
            string partition = (descriptor.Tags ?? Array.Empty<string>())
                .First(tag => tag.StartsWith(
                    "partition:", StringComparison.OrdinalIgnoreCase))
                .Substring("partition:".Length);
            return new Dictionary<string, object>
            {
                ["caseId"] = descriptor.CaseId,
                ["partition"] = partition,
                ["estimatedProviderCalls"] =
                    descriptor.EstimatedProviderCalls,
                ["maxExecutions"] = 1,
                ["continueAfterFailure"] = true,
                ["representedRequirementIds"] =
                    (descriptor.RepresentedRequirementIds
                        ?? Array.Empty<string>()).Cast<object>().ToList()
            };
        }
    }
}
