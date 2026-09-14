using System.Collections.Generic;
using System;
using System.Linq;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static readonly string[] FinalGauntletRotatingSaves =
        {
            "ConvTest_Gauntlet_A",
            "ConvTest_Gauntlet_B"
        };

        private static Dictionary<string, object>
            FinalGauntletCheckpoint(
                string[] args,
                int checkpointIndex)
        {
            string save = FinalGauntletRotatingSaves[
                checkpointIndex % FinalGauntletRotatingSaves.Length];
            return SaveGame(new[]
            {
                "game", "save", "--save", save,
                "--campaign", Value(args, "--campaign", "")
            });
        }

        private static Dictionary<string, object>
            FinalGauntletSavePreflight(
                string campaignId,
                string baselineSaveName)
        {
            if (!string.Equals(
                    baselineSaveName,
                    "ConvTest",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The final gauntlet baseline must be the protected ConvTest save.");
            Dictionary<string, object> response = Get(
                "/api/save-sync/status?campaignId="
                + Uri.EscapeDataString(campaignId ?? string.Empty));
            if (!IsOk(response))
                throw new InvalidOperationException(
                    "Save Sync status is unavailable: "
                    + Json.Serialize(response));
            Dictionary<string, object> saveSync =
                ReadObject(response, "saveSync");
            if (!ReadBoolean(saveSync, "enabled"))
                throw new InvalidOperationException(
                    "Save Sync must be enabled before the final gauntlet starts.");
            List<string> registeredNames = ReadObjects(
                    saveSync,
                    "points")
                .Select(point => String(point, "nativeSaveName"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!registeredNames.Contains(
                    baselineSaveName,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The protected ConvTest baseline has no registered Save Sync snapshot. Save ConvTest after campaign preparation completes, then retry.");
            int missingDerivativeSlots =
                FinalGauntletRotatingSaves.Count(save =>
                    !registeredNames.Contains(
                        save,
                        StringComparer.OrdinalIgnoreCase));
            long remaining = ReadLong(
                saveSync,
                "remainingUniqueStates",
                0);
            if (remaining < missingDerivativeSlots)
                throw new InvalidOperationException(
                    "The final gauntlet needs "
                    + missingDerivativeSlots
                    + " additional Save Sync snapshot slot(s), but only "
                    + remaining
                    + " remain. Delete obsolete saves manually in Bannerlord; BaseOne, BaseTwo, BaseThree, and ConvTest must remain untouched.");
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["baselineSaveName"] = baselineSaveName,
                ["registeredSaveNames"] = registeredNames,
                ["missingDerivativeSlots"] =
                    missingDerivativeSlots,
                ["remainingUniqueStates"] = remaining,
                ["uniqueStateLimit"] = ReadLong(
                    saveSync,
                    "uniqueStateLimit",
                    15)
            };
        }
    }
}
