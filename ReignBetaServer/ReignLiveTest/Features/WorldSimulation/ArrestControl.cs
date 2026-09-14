using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static readonly HashSet<string> ArrestProfiles = new HashSet<string>(new[]
        {
            "smoke", "feature", "save_prepare", "save_verify", "relationship",
            "evidence", "reputation", "town", "castle", "player_party", "surrender",
            "escape", "duel", "battle", "sovereignty", "evaluation", "cleanup"
        }, StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, object> ArrestControl(string[] args)
        {
            string profile = Value(args, "--profile", "feature").Trim().ToLowerInvariant();
            if (!ArrestProfiles.Contains(profile))
                throw new InvalidOperationException("Unsupported arrest profile '" + profile + "'.");
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            string fixtureRunId = Value(args, "--fixture-run", "arrest_release_matrix");
            string disposableSave = Value(args, "--disposable-save", "");
            string targetHeroId = Value(args, "--target-hero", "");
            int timeout = Math.Max(90, IntValue(args, "--timeout", 600));
            List<object> steps = new List<object>();
            steps.Add(new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["operation"] = "arrest_test",
                ["profile"] = profile,
                ["fixtureRunId"] = fixtureRunId,
                ["targetHeroId"] = targetHeroId,
                ["disposableSaveName"] = disposableSave,
                ["timeoutSeconds"] = Math.Min(timeout, 300)
            });
            if (profile == "save_prepare")
            {
                if (string.IsNullOrWhiteSpace(disposableSave))
                    throw new InvalidOperationException("save_prepare requires --disposable-save with the exact loaded save name.");
                steps.Add(new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["operation"] = "save_checkpoint",
                    ["saveName"] = disposableSave,
                    ["timeoutSeconds"] = 300
                });
            }
            Dictionary<string, object> started = Post("/tests/live/run/start",
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["mode"] = "individual_chat",
                    ["presentation"] = Has(args, "--visible") ? "visible" : "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Conversation arrest " + profile,
                    ["autoCompleteWhenIdle"] = true,
                    ["steps"] = steps
                });
            if (!IsOk(started) || Has(args, "--no-wait")) return started;
            return WaitForRun(campaignId, String(started, "runId"), "", timeout, true);
        }
    }
}
