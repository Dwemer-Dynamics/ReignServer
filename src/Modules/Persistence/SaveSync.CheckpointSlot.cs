using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
    {
        internal static Dictionary<string, object> SaveSyncStatusRequestApi(Dictionary<string, string> query)
        {
            if (query == null || !query.TryGetValue("checkpointSlot", out string slot))
                return SaveSyncStatusApi(query);

            query.TryGetValue("campaignId", out string campaignId);
            ValidateSaveSyncId(campaignId, "campaignId");
            if (slot == null || !Regex.IsMatch(slot, @"\AReignTest_[A-Za-z0-9_]{1,54}\z",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw new InvalidDataException("checkpointSlot must be a generated Reign campaign-test save name.");

            // The ordinary status route is gate-exempt. This ownership lookup
            // needs a consistent view while campaign ledgers can be replaced.
            Program.CampaignDataGate.EnterReadLock();
            try
            {
                Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
                List<Dictionary<string, object>> points = ReadDictionaryList(ledger, "points");
                List<Dictionary<string, object>> matchingPoints = points
                    .Where(point => MatchesCheckpointSlot(point, slot)).ToList();
                bool foreignRegistration = SaveSyncCampaignIds()
                    .Where(id => id != campaignId)
                    .Any(id => ReadDictionaryList(ReadSaveSyncLedger(id), "points")
                        .Any(point => MatchesCheckpointSlot(point, slot)));

                // Native slots are shared across campaigns. A skipped ledger
                // can hide a registration even when no native file is present.
                if (!foreignRegistration && FencedSaveSyncCampaignIds().Count != 0)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["result"] = "checkpoint_occupancy_unverified",
                        ["campaignId"] = campaignId, ["checkpointSlot"] = slot,
                        ["error"] = "Save Sync recovery prevents verifying this shared native checkpoint slot."
                    };

                var result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["enabled"] = IsSaveSyncEnabled(), ["campaignId"] = campaignId,
                    ["checkpointSlot"] = slot, ["foreignRegistration"] = foreignRegistration,
                    ["registrationCount"] = matchingPoints.Count,
                    ["registrationUniqueStateCount"] = SaveSyncUniqueStateCount(ledger)
                };
                if (matchingPoints.Count == 1)
                    result["point"] = PublicSaveSyncPoint(campaignId, matchingPoints[0]);
                return result;
            }
            finally { Program.CampaignDataGate.ExitReadLock(); }
        }

        private static bool MatchesCheckpointSlot(Dictionary<string, object> point, string slot) =>
            NormalizeNativeSaveSlot(ReadString(point, "nativeSaveName", ""))
                .Equals(slot, StringComparison.OrdinalIgnoreCase);
    }
}
