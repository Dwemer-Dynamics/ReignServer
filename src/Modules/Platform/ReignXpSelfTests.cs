using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunReignXpOptionsSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, string summary) => results.Add(TestDict(
                "caseId", "xp_options_" + id, "suite", "interaction_architecture", "passed", passed, "summary", summary));

            var legacy = ReignXpOptionsForClient(TestDict("reignXpMultiplier", 2d));
            Check("legacy_defaults", ReignXpCategoryOptionKeys.All(key => Convert.ToDouble(legacy[key]) == 1)
                && Convert.ToDouble(legacy["reignXpMultiplier"]) == 2 && ReadBool(legacy, "reignXpEnabled", false),
                "Missing categories default to one while retaining the existing universal multiplier and toggle.");
            Check("new_defaults", ReignXpCategoryOptionKeys.All(key => Convert.ToDouble(DefaultSettings()[key]) == 1),
                "New server settings initialize every category to one.");

            foreach (string key in ReignXpCategoryOptionKeys)
            {
                Check(key + "_valid", new[] { 0d, .25d, 1d, 5d }.All(value => ValidateReignXpOptions(TestDict(key, value)) == ""),
                    "Partial updates accept zero, quarter steps, default and maximum category values.");
                object[] invalid = { null, true, false, "1", -.25d, .3d, 5.25d, double.NaN, double.PositiveInfinity, TestDict() };
                Check(key + "_invalid", invalid.All(value => ValidateReignXpOptions(TestDict(key, value)).Length > 0),
                    "Malformed, nonnumeric, nonfinite and out-of-range category values are rejected before persistence.");
                Check(key + "_projection_fallback", new object[] { null, true, "bad", -.25d, .3d, 6d, double.NaN }.All(value =>
                    Convert.ToDouble(ReignXpOptionsForClient(TestDict(key, value))[key]) == 1),
                    "Invalid stored category values return the safe default in the gameplay-options projection.");
            }

            var settings = DefaultSettings();
            settings["xp_test_unrelated"] = "retain";
            double[] values = { 0, .25, 2, 5 };
            for (int i = 0; i < ReignXpCategoryOptionKeys.Length; i++)
                MergeProviderSettings(settings, TestDict(ReignXpCategoryOptionKeys[i], values[i]));
            MergeProviderSettings(settings, TestDict("reignXpMultiplier", .25, "reignXpEnabled", false));
            var roundTrip = TryParseJsonObject(Json.Serialize(settings));
            var projected = ReignXpOptionsForClient(roundTrip);
            Check("partial_merge_roundtrip", ReignXpCategoryOptionKeys.Select((key, i) =>
                    Convert.ToDouble(projected[key], CultureInfo.InvariantCulture) == values[i]).All(value => value)
                && Convert.ToDouble(projected["reignXpMultiplier"]) == .25
                && !ReadBool(projected, "reignXpEnabled", true)
                && ReadString(roundTrip, "xp_test_unrelated", "") == "retain"
                && !projected.ContainsKey("xp_test_unrelated"),
                "Independent partial merges and production JSON serialization retain all categories and unrelated settings without exposing them.");
            Check("universal_bounds_unchanged", ValidateReignXpOptions(TestDict("reignXpMultiplier", 0d)).Length > 0
                && ValidateReignXpOptions(TestDict("reignXpMultiplier", .25d)) == ""
                && ValidateReignXpOptions(TestDict("reignXpMultiplier", 5d)) == "",
                "The universal multiplier retains its existing quarter-to-five bounds.");
            return results;
        }
    }
}
