using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal enum FinalGauntletFaultPoint
    {
        AfterProviderReceipt,
        AfterParsing,
        AfterValidation,
        AfterExecution,
        AfterPersistence,
        AfterResponseDelivery
    }

    internal static class FinalConversationGauntletFaults
    {
        private sealed class ArmedFault
        {
            internal string RunId = string.Empty;
            internal string CaseInstanceId = string.Empty;
            internal string CampaignId = string.Empty;
            internal string GameInstanceId = string.Empty;
            internal FinalGauntletFaultPoint Point;
            internal bool Consumed;
            internal DateTimeOffset ExpiresUtc;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, ArmedFault> Faults =
            new Dictionary<string, ArmedFault>(StringComparer.OrdinalIgnoreCase);

        internal static Dictionary<string, object> Arm(
            string runId,
            string caseInstanceId,
            string campaignId,
            string gameInstanceId,
            FinalGauntletFaultPoint point,
            string confirmation,
            DateTimeOffset expiresUtc)
        {
            if (!string.Equals(
                confirmation, "armed_gauntlet_fault",
                StringComparison.Ordinal))
                return Error("Explicit gauntlet fault confirmation is required.");
            if (string.IsNullOrWhiteSpace(runId)
                || string.IsNullOrWhiteSpace(caseInstanceId)
                || string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(gameInstanceId))
                return Error("Run, case, campaign, and game instance are required.");
            string key = Key(runId, caseInstanceId, point);
            lock (Gate)
                Faults[key] = new ArmedFault
                {
                    RunId = runId,
                    CaseInstanceId = caseInstanceId,
                    CampaignId = campaignId,
                    GameInstanceId = gameInstanceId,
                    Point = point,
                    ExpiresUtc = expiresUtc
                };
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["armed"] = true,
                ["faultKey"] = key,
                ["point"] = point.ToString(),
                ["expiresUtc"] = expiresUtc.ToString("o")
            };
        }

        internal static bool Consume(
            string runId,
            string caseInstanceId,
            string campaignId,
            string gameInstanceId,
            FinalGauntletFaultPoint point)
        {
            string key = Key(runId, caseInstanceId, point);
            lock (Gate)
            {
                if (!Faults.TryGetValue(key, out ArmedFault fault)
                    || fault.Consumed
                    || fault.ExpiresUtc <= DateTimeOffset.UtcNow
                    || !string.Equals(
                        fault.CampaignId, campaignId,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        fault.GameInstanceId, gameInstanceId,
                        StringComparison.OrdinalIgnoreCase))
                    return false;
                fault.Consumed = true;
                return true;
            }
        }

        internal static void ClearRun(string runId)
        {
            lock (Gate)
            {
                List<string> remove = new List<string>();
                foreach (KeyValuePair<string, ArmedFault> pair in Faults)
                    if (string.Equals(
                        pair.Value.RunId, runId,
                        StringComparison.OrdinalIgnoreCase))
                        remove.Add(pair.Key);
                foreach (string key in remove) Faults.Remove(key);
            }
        }

        private static string Key(
            string runId,
            string caseInstanceId,
            FinalGauntletFaultPoint point)
        {
            return (runId ?? string.Empty) + "|"
                + (caseInstanceId ?? string.Empty) + "|" + point;
        }

        private static Dictionary<string, object> Error(string message)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["error"] = message
            };
        }
    }
}
