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
                List<Dictionary<string, object>> points = ReadDictionaryList(ReadSaveSyncLedger(campaignId), "points");
                List<Dictionary<string, object>> matchingPoints = points
                    .Where(point => MatchesCheckpointSlot(point, slot)).ToList();
                bool foreignRegistration = SaveSyncCampaignIds()
                    .Where(id => !id.Equals(campaignId, StringComparison.OrdinalIgnoreCase))
                    .Any(id => ReadDictionaryList(ReadSaveSyncLedger(id), "points")
                        .Any(point => MatchesCheckpointSlot(point, slot)));

                var availableLedger = new Dictionary<string, object>
                {
                    ["points"] = points.Where(point => SaveSyncPointSnapshotAvailable(campaignId, point)).ToList()
                };
                var result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["enabled"] = IsSaveSyncEnabled(), ["campaignId"] = campaignId,
                    ["checkpointSlot"] = slot, ["foreignRegistration"] = foreignRegistration,
                    ["registrationCount"] = matchingPoints.Count,
                    ["uniqueStateCount"] = SaveSyncUniqueStateCount(availableLedger)
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
