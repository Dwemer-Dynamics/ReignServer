using System;
using System.Collections.Generic;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static Dictionary<string, object> PartyAgencyControl(string[] args)
        {
            string profile = Value(args, "--profile", "contracts").Trim().ToLowerInvariant();
            if (profile != "contracts" && profile != "status" && profile != "preflight")
                throw new InvalidOperationException("Unsupported party-agency profile '" + profile + "'.");
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            int timeout = Math.Max(60, IntValue(args, "--timeout", 180));
            Dictionary<string, object> started = Post("/tests/live/run/start",
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["mode"] = "party_agency",
                    ["presentation"] = "headless",
                    ["effects"] = "observed",
                    ["label"] = "Temporary noble guest observational " + profile,
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["schemaVersion"] = 2,
                            ["operation"] = "party_agency_test",
                            ["profile"] = profile,
                            ["timeoutSeconds"] = Math.Min(timeout, 120)
                        }
                    }
                });
            if (!IsOk(started) || Has(args, "--no-wait")) return started;
            return WaitForRun(campaignId, String(started, "runId"), "", timeout, true);
        }
    }
}
