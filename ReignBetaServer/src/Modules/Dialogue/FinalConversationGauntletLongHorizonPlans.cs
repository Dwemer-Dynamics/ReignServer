using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletLongHorizonPlans
    {
        public static Dictionary<string, object> Build()
        {
            return new Dictionary<string, object>
            {
                ["stableCorrelations"] = true,
                ["lng001"] = new Dictionary<string, object>
                {
                    ["dialogueReplies"] = 100,
                    ["scenes"] = 10,
                    ["repliesPerScene"] = 10,
                    ["summaryCalls"] = 10,
                    ["providerCalls"] = 110,
                    ["rotatingSaves"] = new[]
                    {
                        "ConvTest_Gauntlet_A",
                        "ConvTest_Gauntlet_B"
                    }
                },
                ["lng002"] = new Dictionary<string, object>
                {
                    ["rosterSize"] = 20,
                    ["groups"] = 4,
                    ["groupSize"] = 5,
                    ["groupReplies"] = 20,
                    ["groupSummaries"] = 4,
                    ["privateProbes"] = 10,
                    ["witnessRecallProbes"] = 5,
                    ["nonWitnessIsolationProbes"] = 5,
                    ["privateSummaries"] = 10,
                    ["knowledgeProbeCount"] = 10,
                    ["providerCalls"] = 44,
                    ["knowledgeStates"] = new[]
                    {
                        "said", "heard", "witnessed", "inferred",
                        "rumor", "public", "unknown"
                    }
                }
            };
        }
    }
}
