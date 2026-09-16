using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletLongHorizonBudgetSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type type = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletLongHorizonPlans");
            MethodInfo build = type?.GetMethod(
                "Build", BindingFlags.Public | BindingFlags.Static);
            add("long_horizon_plan_contract_exists", build != null,
                "Long-horizon work exposes provider-free literal plans.");
            if (build == null) return checks;
            Dictionary<string, object> plans =
                build.Invoke(null, null) as Dictionary<string, object>;
            Dictionary<string, object> focal =
                ReadDictionary(plans, "lng001");
            Dictionary<string, object> court =
                ReadDictionary(plans, "lng002");
            add("lng001_exact_arithmetic",
                ReadInt(focal, "dialogueReplies", -1) == 100
                    && ReadInt(focal, "scenes", -1) == 10
                    && ReadInt(focal, "summaryCalls", -1) == 10
                    && ReadInt(focal, "providerCalls", -1) == 110,
                "LNG-001 is ten scenes of ten replies plus ten summaries.");
            add("lng002_exact_arithmetic",
                ReadInt(court, "groupReplies", -1) == 20
                    && ReadInt(court, "groupSummaries", -1) == 4
                    && ReadInt(court, "privateProbes", -1) == 10
                    && ReadInt(court, "privateSummaries", -1) == 10
                    && ReadInt(court, "providerCalls", -1) == 44,
                "LNG-002 is twenty group replies, four group summaries, ten private probes, and ten private summaries.");
            add("stable_correlation_and_knowledge_contracts",
                ReadInt(court, "rosterSize", -1) == 20
                    && ReadInt(court, "knowledgeProbeCount", -1) == 10
                    && ReadInt(court, "witnessRecallProbes", -1) == 5
                    && ReadInt(court, "nonWitnessIsolationProbes", -1) == 5
                    && ReadStringList(court, "knowledgeStates").Count == 7
                    && ReadBool(plans, "stableCorrelations", false),
                "Twenty distinct adults retain explicit knowledge states and stable correlations; private follow-ups balance witnessed recall against non-witness isolation.");
            return checks;
        }
    }
}
