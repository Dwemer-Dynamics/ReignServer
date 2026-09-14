using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletActionSelfTests()
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
            string[] actions = { "GiftGold", "StartDuel" };
            List<FinalGauntletActionApplicability> matrix =
                FinalConversationGauntletActionConformance.BuildMatrix(actions);
            add(
                "all_action_rows_have_applicability_evidence",
                matrix.Count == 70
                    && FinalConversationGauntletActionConformance
                        .ValidateMatrix(actions, matrix).Count == 0,
                "Every registered action has all 35 ACT rows and explicit routing evidence.");
            matrix.Remove(matrix.First(row =>
                row.Action == "GiftGold"
                && row.RequirementId == "ACT-021"));
            add(
                "missing_action_applicability_fails_coverage",
                FinalConversationGauntletActionConformance
                    .ValidateMatrix(actions, matrix)
                    .Any(error => error.Contains("GiftGold ACT-021")),
                "Catalog coverage fails when an action requirement lacks applicability evidence.");

            DateTimeOffset future = DateTimeOffset.UtcNow.AddMinutes(5);
            Dictionary<string, object> rejected =
                FinalConversationGauntletFaults.Arm(
                    "run", "case", "campaign", "game",
                    FinalGauntletFaultPoint.AfterExecution,
                    "wrong", future);
            Dictionary<string, object> armed =
                FinalConversationGauntletFaults.Arm(
                    "run", "case", "campaign", "game",
                    FinalGauntletFaultPoint.AfterExecution,
                    "armed_gauntlet_fault", future);
            bool wrongInstance = FinalConversationGauntletFaults.Consume(
                "run", "case", "campaign", "other-game",
                FinalGauntletFaultPoint.AfterExecution);
            bool first = FinalConversationGauntletFaults.Consume(
                "run", "case", "campaign", "game",
                FinalGauntletFaultPoint.AfterExecution);
            bool duplicate = FinalConversationGauntletFaults.Consume(
                "run", "case", "campaign", "game",
                FinalGauntletFaultPoint.AfterExecution);
            add(
                "gauntlet_faults_are_explicit_scoped_and_one_shot",
                !ReadBool(rejected, "ok", false)
                    && ReadBool(armed, "ok", false)
                    && !wrongInstance && first && !duplicate,
                "Faults require confirmation, match run/case/campaign/game, and fire once.");
            FinalConversationGauntletFaults.ClearRun("run");
            return checks;
        }
    }
}
