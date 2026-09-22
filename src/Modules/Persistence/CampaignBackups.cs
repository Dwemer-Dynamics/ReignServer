using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CampaignBackupFormat = "bannerlord-reign-campaign-backup";
        private const int CampaignBackupVersion = 3;
        private const int CampaignBackupMinimumSupportedVersion = 2;
        private const int CampaignBackupMaxEntries = 250000;
        private const long CampaignBackupMaxExpandedBytes = 32L * 1024L * 1024L * 1024L;
        private const int CampaignRetirementVersion = 1;
        private const int CampaignSemanticDeleteBatchSize = 200;
        private const int CampaignSemanticDeleteTimeoutMs = 180000;
        private const string ZeroSaveCampaignCleanupConfirmation = "delete Reign campaigns with no saves";
        private static readonly object CampaignBackupMutationLock = new object();
        private static readonly object CampaignRetirementLedgerLock = new object();
        private static bool CampaignMutationAllowDuringTests;
        private static string CampaignRetirementFailureInjectionForTests = "";
        private static string CampaignNativeSaveRootOverrideForTests = "";

        private static Dictionary<string, object> CampaignListApi()
        {
            List<Dictionary<string, object>> campaigns = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> databaseCampaign
                in ReignPostgreSqlStorage.ListCampaigns())
            {
                string campaignId =
                    ReadString(databaseCampaign, "campaignId", "");
                string directory = CampaignDirectory(campaignId);
                if (!Directory.Exists(directory)) continue;
                DirectoryInfo directoryInfo = new DirectoryInfo(directory);
                if (!IsRealCampaignDirectory(directoryInfo)) continue;
                campaigns.Add(BuildCampaignBackupSummary(
                    campaignId,
                    directoryInfo,
                    ReadLong(databaseCampaign, "databaseBytes", 0)));
            }
            campaigns = campaigns
                .OrderByDescending(row =>
                    ReadString(row, "updatedUtc", ""))
                .ToList();
            ReignPostgreSqlStorage.ClearAllPools();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["campaigns"] = campaigns,
                ["latestCampaignId"] = campaigns.Count == 0 ? "default" : ReadString(campaigns[0], "campaignId", "default"),
                ["saveSyncEnabled"] = IsSaveSyncEnabled(),
                ["backupFormat"] = CampaignBackupFormat,
                ["backupVersion"] = CampaignBackupVersion,
                ["scopeNote"] = "Version-3 archives contain all Bannerlord Reign campaign state, portrait caches, and every matching physical native Bannerlord .sav file for portable load-ready diagnostics."
            };
        }

        private static Dictionary<string, object> CampaignStorageAuditApi()
        {
            return CampaignStorageAuditCore();
        }

        private static Dictionary<string, object> CampaignStorageAuditCore()
        {
            List<Dictionary<string, object>> databaseCampaigns =
                ReignPostgreSqlStorage.ListCampaigns();
            HashSet<string> databaseIds = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
            int ignoredDatabaseRecords = 0;
            Dictionary<string, Dictionary<string, object>> pending =
                ReadDictionaryList(ReadCampaignRetirementLedger(), "pending")
                    .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "campaignId", "")))
                    .GroupBy(row => ReadString(row, "campaignId", ""), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> campaigns = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> databaseCampaign in databaseCampaigns)
            {
                string campaignId = ReadString(databaseCampaign, "campaignId", "");
                if (!TryResolveAuditableCampaignDirectory(
                    campaignId, out string directory))
                {
                    ignoredDatabaseRecords++;
                    continue;
                }
                databaseIds.Add(campaignId);
                DirectoryInfo directoryInfo = Directory.Exists(directory)
                    ? new DirectoryInfo(directory)
                    : null;
                Dictionary<string, object> row = BuildCampaignBackupSummary(
                    campaignId,
                    directoryInfo,
                    ReadLong(databaseCampaign, "databaseBytes", 0));
                Dictionary<string, object> saveSync =
                    ReadDictionary(row, "saveSync") ?? new Dictionary<string, object>();
                campaigns.Add(row);
            }

            Dictionary<string, object> nativeSaveInventory =
                ApplyNativeSaveInventory(campaigns, pending);

            List<Dictionary<string, object>> orphanPortraitRoots =
                EnumerateOrphanPortraitRoots(databaseIds);
            List<Dictionary<string, object>> orphanCampaignRoots =
                EnumerateOrphanCampaignRoots(databaseIds);
            List<Dictionary<string, object>> orphanSaveSyncRoots =
                EnumerateOrphanSaveSyncRoots(databaseIds);
            string portraitCacheRoot = CampaignPortraitCacheRoot();
            string installedPortraitCacheRoot = CharacterEditorPortraitCacheRoot();
            string installedSharedRoot = string.IsNullOrWhiteSpace(installedPortraitCacheRoot)
                ? ""
                : Path.Combine(installedPortraitCacheRoot, "_shared");
            string sharedRoot = Directory.Exists(installedSharedRoot)
                ? installedSharedRoot
                : Path.Combine(portraitCacheRoot, "_shared");
            Dictionary<string, object> shared = new Dictionary<string, object>
            {
                ["category"] = "shared_portrait_library",
                ["name"] = "_shared",
                ["exists"] = Directory.Exists(sharedRoot),
                ["rootCount"] = Directory.Exists(sharedRoot) ? 1 : 0,
                ["productCount"] = SafeDirectoryCount(sharedRoot),
                ["fileCount"] = SafeFileCount(sharedRoot, "*"),
                ["bytes"] = SafeDirectorySize(sharedRoot),
                ["protected"] = true
            };
            ReignPostgreSqlStorage.ClearAllPools();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schema"] = "reign-campaign-storage-audit-v2",
                ["auditedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["saveSyncEnabled"] = IsSaveSyncEnabled(),
                ["campaigns"] = campaigns.OrderByDescending(row =>
                    ReadString(row, "updatedUtc", "")).ToList(),
                ["campaignsWithSaves"] = campaigns.Count(row =>
                    ReadString(row, "storageCategory", "") == "campaign_with_saves"),
                ["pendingRetirements"] = campaigns.Count(row =>
                    ReadString(row, "storageCategory", "") == "pending_retirement"),
                ["legacyZeroSaveCampaigns"] = campaigns.Count(row =>
                    ReadString(row, "storageCategory", "") == "legacy_zero_save_campaign"),
                ["importedAwaitingFirstLoad"] = campaigns.Count(row =>
                    ReadString(row, "storageCategory", "") == "imported_awaiting_first_load"),
                ["nativeSaveInventoryUnavailableCampaigns"] = campaigns.Count(row =>
                    ReadString(row, "storageCategory", "") == "native_save_inventory_unavailable"),
                ["campaignsWithStaleRegistrations"] = campaigns.Count(row =>
                    ReadInt(row, "staleRegisteredSavePoints", 0) > 0),
                ["staleRegisteredSavePoints"] = campaigns.Sum(row =>
                    ReadInt(row, "staleRegisteredSavePoints", 0)),
                ["ignoredInternalOrInvalidDatabaseRecords"] =
                    ignoredDatabaseRecords,
                ["nativeSaveInventory"] = nativeSaveInventory,
                ["orphanPortraitRoots"] = orphanPortraitRoots,
                ["orphanCampaignRoots"] = orphanCampaignRoots,
                ["orphanSaveSyncRoots"] = orphanSaveSyncRoots,
                ["sharedPortraitLibrary"] = shared,
                ["cleanupConfirmation"] = ZeroSaveCampaignCleanupConfirmation,
                ["cleanupNote"] = "The physical Bannerlord .sav inventory is authoritative. Missing and overwritten registrations are stale. Confirmed cleanup removes campaigns with no matching physical save, prunes stale registrations from retained campaigns, and preserves imports awaiting first load plus _shared."
            };
        }

        private static Dictionary<string, object> ApplyNativeSaveInventory(
            List<Dictionary<string, object>> campaigns,
            Dictionary<string, Dictionary<string, object>> pending)
        {
            string root = CampaignNativeSaveRoot();
            bool available = Directory.Exists(root);
            Dictionary<string, FileInfo> files = new Dictionary<string, FileInfo>(
                StringComparer.OrdinalIgnoreCase);
            if (available)
            {
                foreach (string path in Directory.GetFiles(
                    root, "*.sav", SearchOption.TopDirectoryOnly))
                {
                    FileInfo file = new FileInfo(path);
                    string slot = NormalizeNativeSaveSlot(file.Name);
                    if (!string.IsNullOrWhiteSpace(slot)) files[slot] = file;
                }
            }

            Dictionary<string, Dictionary<string, object>> ownerBySlot =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> campaign in campaigns)
            {
                string campaignId = ReadString(campaign, "campaignId", "");
                Dictionary<string, object> saveSync = ReadDictionary(
                    campaign, "saveSync") ?? new Dictionary<string, object>();
                foreach (Dictionary<string, object> point in
                    ReadDictionaryList(saveSync, "points"))
                {
                    string slot = NormalizeNativeSaveSlot(
                        ReadString(point, "nativeSaveName", ""));
                    if (string.IsNullOrWhiteSpace(slot) || !files.ContainsKey(slot))
                        continue;
                    Dictionary<string, object> candidate =
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["savePointId"] = ReadString(point, "savePointId", ""),
                            ["sortUtc"] = FirstNonEmpty(
                                ReadString(point, "saveFinalizedUtc", ""),
                                ReadString(point, "registeredUtc", ""),
                                ReadString(point, "capturedUtc", ""))
                        };
                    if (!ownerBySlot.TryGetValue(slot,
                            out Dictionary<string, object> current)
                        || CompareNativeSaveOwners(candidate, current) > 0)
                        ownerBySlot[slot] = candidate;
                }
            }

            foreach (Dictionary<string, object> campaign in campaigns)
            {
                string campaignId = ReadString(campaign, "campaignId", "");
                Dictionary<string, object> saveSync = ReadDictionary(
                    campaign, "saveSync") ?? new Dictionary<string, object>();
                List<Dictionary<string, object>> points =
                    ReadDictionaryList(saveSync, "points");
                List<Dictionary<string, object>> matching =
                    new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> stale =
                    new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> point in points)
                {
                    Dictionary<string, object> observed =
                        new Dictionary<string, object>(point,
                            StringComparer.OrdinalIgnoreCase);
                    string slot = NormalizeNativeSaveSlot(
                        ReadString(point, "nativeSaveName", ""));
                    bool exists = available && !string.IsNullOrWhiteSpace(slot)
                        && files.ContainsKey(slot);
                    bool owns = exists && ownerBySlot.TryGetValue(slot,
                            out Dictionary<string, object> owner)
                        && ReadString(owner, "campaignId", "").Equals(
                            campaignId, StringComparison.OrdinalIgnoreCase)
                        && ReadString(owner, "savePointId", "").Equals(
                            ReadString(point, "savePointId", ""),
                            StringComparison.OrdinalIgnoreCase);
                    observed["nativeSaveExists"] = exists;
                    observed["matchesPhysicalNativeSave"] = owns;
                    if (owns)
                    {
                        observed["nativeSaveMatchStatus"] = "matching";
                        matching.Add(observed);
                    }
                    else
                    {
                        observed["nativeSaveMatchStatus"] = !available
                            ? "inventory_unavailable"
                            : string.IsNullOrWhiteSpace(slot)
                                ? "missing_native_save_name"
                                : !exists
                                    ? "native_save_missing"
                                    : "slot_owned_by_newer_registration";
                        stale.Add(observed);
                    }
                }

                int matchingCount = matching.Count;
                int registeredCount = points.Count;
                campaign["registeredSavePoints"] = registeredCount;
                campaign["remainingSavePoints"] = matchingCount;
                campaign["staleRegisteredSavePoints"] = stale.Count;
                campaign["matchingNativeSavePoints"] = matching;
                campaign["staleNativeSavePoints"] = stale;
                saveSync["registeredPointCount"] = registeredCount;
                saveSync["matchingNativeSavePointCount"] = matchingCount;
                saveSync["staleRegisteredPointCount"] = stale.Count;
                saveSync["matchingNativeSavePoints"] = matching;
                saveSync["staleNativeSavePoints"] = stale;

                Dictionary<string, object> metadata = ReadJsonObject(Path.Combine(
                    StrictCampaignDirectory(campaignId), "campaign.json"));
                string lifecycle = ReadString(metadata, "saveSyncLifecycle", "");
                bool pendingRetirement = pending.ContainsKey(campaignId);
                bool awaitingFirstLoad = lifecycle.Equals(
                    "imported_awaiting_first_load", StringComparison.OrdinalIgnoreCase);
                string category = pendingRetirement
                    ? "pending_retirement"
                    : !available
                        ? "native_save_inventory_unavailable"
                        : matchingCount > 0
                            ? "campaign_with_saves"
                            : awaitingFirstLoad
                                ? "imported_awaiting_first_load"
                                : "legacy_zero_save_campaign";
                campaign["storageCategory"] = category;
                campaign["hasMatchingBannerlordSave"] = matchingCount > 0;
                campaign["pendingFinalSaveDeletion"] = pendingRetirement;
                campaign["eligibleForPermanentDeletion"] = available
                    && matchingCount == 0 && !pendingRetirement
                    && !awaitingFirstLoad;
                campaign["storageStatusLabel"] = !available
                    ? "Bannerlord save inventory unavailable; campaign preserved"
                    : matchingCount > 0
                        ? matchingCount.ToString(CultureInfo.InvariantCulture) +
                            " matching physical Bannerlord save" +
                            (matchingCount == 1 ? "" : "s") +
                            (stale.Count == 0 ? "" : "; " +
                                stale.Count.ToString(CultureInfo.InvariantCulture) +
                                " stale registration" + (stale.Count == 1 ? "" : "s"))
                        : pendingRetirement
                            ? "Final save deleted; permanent campaign deletion is pending unload"
                            : awaitingFirstLoad
                                ? "Imported campaign awaiting its first confirmed Bannerlord save"
                                : "No matching physical Bannerlord save; eligible for permanent deletion";
                if (pendingRetirement)
                    campaign["pendingRetirement"] = pending[campaignId];
            }

            List<Dictionary<string, object>> inventoryFiles = files
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair =>
                {
                    ownerBySlot.TryGetValue(pair.Key,
                        out Dictionary<string, object> owner);
                    return new Dictionary<string, object>
                    {
                        ["nativeSaveName"] = pair.Key,
                        ["fileName"] = pair.Value.Name,
                        ["bytes"] = pair.Value.Length,
                        ["lastWriteUtc"] = pair.Value.LastWriteTimeUtc.ToString(
                            "o", CultureInfo.InvariantCulture),
                        ["matchingCampaignId"] = ReadString(owner,
                            "campaignId", ""),
                        ["matchingSavePointId"] = ReadString(owner,
                            "savePointId", "")
                    };
                }).ToList();
            return new Dictionary<string, object>
            {
                ["root"] = root,
                ["available"] = available,
                ["physicalSaveCount"] = inventoryFiles.Count,
                ["matchedPhysicalSaveCount"] = inventoryFiles.Count(row =>
                    !string.IsNullOrWhiteSpace(ReadString(
                        row, "matchingCampaignId", ""))),
                ["unregisteredPhysicalSaveCount"] = inventoryFiles.Count(row =>
                    string.IsNullOrWhiteSpace(ReadString(
                        row, "matchingCampaignId", ""))),
                ["files"] = inventoryFiles
            };
        }

        private static string CampaignNativeSaveRoot()
        {
            if (!string.IsNullOrWhiteSpace(
                CampaignNativeSaveRootOverrideForTests))
                return Path.GetFullPath(
                    CampaignNativeSaveRootOverrideForTests);
            // Local deployment records the selected Windows user's save folder in WSL notation.
            string saveRoot = ReadString(ReadJsonObject(Path.Combine(DataDir, "windows-bridge.json")), "nativeSaveRoot", "");
            if (!saveRoot.StartsWith("/mnt/", StringComparison.Ordinal)
                || !saveRoot.EndsWith("/Mount and Blade II Bannerlord/Game Saves", StringComparison.Ordinal)
                || Path.GetFullPath(saveRoot) != saveRoot)
                throw new InvalidOperationException("Set up the local Reign client bridge before managing native Bannerlord saves.");
            return saveRoot;
        }

        private static string NormalizeNativeSaveSlot(string value)
        {
            string name = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(name)) return "";
            if (!Path.GetFileName(name).Equals(name,
                StringComparison.Ordinal)) return "";
            return name.EndsWith(".sav", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(name)
                : name;
        }

        private static string ValidateNativeSaveFileName(string value)
        {
            string fileName = (value ?? "").Trim();
            if (string.IsNullOrWhiteSpace(fileName)
                || fileName.Length > 260
                || !Path.GetFileName(fileName).Equals(
                    fileName, StringComparison.Ordinal)
                || !fileName.EndsWith(
                    ".sav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Invalid native Bannerlord save file name.");
            return fileName;
        }

        private static int CompareNativeSaveOwners(
            Dictionary<string, object> left,
            Dictionary<string, object> right)
        {
            int compared = string.Compare(
                ReadString(left, "sortUtc", ""),
                ReadString(right, "sortUtc", ""),
                StringComparison.OrdinalIgnoreCase);
            if (compared != 0) return compared;
            compared = string.Compare(
                ReadString(left, "campaignId", ""),
                ReadString(right, "campaignId", ""),
                StringComparison.OrdinalIgnoreCase);
            if (compared != 0) return compared;
            return string.Compare(
                ReadString(left, "savePointId", ""),
                ReadString(right, "savePointId", ""),
                StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryResolveAuditableCampaignDirectory(
            string campaignId,
            out string directory)
        {
            directory = "";
            if (string.IsNullOrWhiteSpace(campaignId)
                || IsInternalCampaignId(campaignId)) return false;
            try
            {
                directory = StrictCampaignDirectory(campaignId);
                return true;
            }
            catch (InvalidDataException)
            {
                directory = "";
                return false;
            }
        }

        private static Dictionary<string, object> CleanupZeroSaveCampaignsApi(
            Dictionary<string, object> payload)
        {
            CampaignDataGate.EnterWriteLock();
            try
            {
                payload = payload ?? new Dictionary<string, object>();
                if (!ReadString(payload, "confirmation", "").Equals(
                    ZeroSaveCampaignCleanupConfirmation, StringComparison.Ordinal))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "Type " + ZeroSaveCampaignCleanupConfirmation +
                            " exactly to permanently delete legacy campaigns with no saves."
                    };
                string running = CampaignMutationBlockReason();
                if (!string.IsNullOrWhiteSpace(running))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = running };

                Dictionary<string, object> audit = CampaignStorageAuditCore();
                Dictionary<string, object> nativeInventory =
                    ReadDictionary(audit, "nativeSaveInventory") ??
                        new Dictionary<string, object>();
                if (!ReadBool(nativeInventory, "available", false))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "The Bannerlord save directory is unavailable. Reign did not delete campaigns, stale registrations, or orphan roots because physical save ownership could not be verified."
                    };
                List<Dictionary<string, object>> targets =
                    ReadDictionaryList(audit, "campaigns")
                        .Where(row => ReadBool(row, "eligibleForPermanentDeletion", false))
                        .ToList();
                List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
                int staleSavePointsSelected = ReadInt(
                    audit, "staleRegisteredSavePoints", 0);
                int staleSavePointsDeleted = 0;
                int staleSaveStatesDeleted = 0;
                int retainedCampaignsReconciled = 0;
                foreach (Dictionary<string, object> retained in
                    ReadDictionaryList(audit, "campaigns").Where(row =>
                        ReadBool(row, "hasMatchingBannerlordSave", false)
                        && ReadInt(row, "staleRegisteredSavePoints", 0) > 0))
                {
                    Dictionary<string, object> reconciliation =
                        ReconcileStaleSaveSyncPoints(
                            ReadString(retained, "campaignId", ""),
                            ReadDictionaryList(retained,
                                "staleNativeSavePoints"));
                    staleSavePointsDeleted += ReadInt(
                        reconciliation, "deletedPoints", 0);
                    staleSaveStatesDeleted += ReadInt(
                        reconciliation, "deletedUniqueStates", 0);
                    if (ReadInt(reconciliation, "deletedPoints", 0) > 0)
                        retainedCampaignsReconciled++;
                }
                foreach (Dictionary<string, object> target in targets)
                {
                    string campaignId = ReadString(target, "campaignId", "");
                    Dictionary<string, object> deletion =
                        DeleteCampaignOwnedDataCore(
                        campaignId,
                        ReadString(target, "label", campaignId),
                        false,
                        "confirmed_physical_zero_save_cleanup");
                    results.Add(deletion);
                    if (ReadBool(deletion, "campaignDeleted", false))
                        staleSavePointsDeleted += ReadInt(
                            target, "staleRegisteredSavePoints", 0);
                }
                List<Dictionary<string, object>> orphanResults =
                    new List<Dictionary<string, object>>();
                DeleteAuditedOrphanRoots(ReadDictionaryList(
                        audit, "orphanCampaignRoots"), CampaignsRoot(), false,
                    orphanResults);
                DeleteAuditedOrphanRoots(ReadDictionaryList(
                        audit, "orphanSaveSyncRoots"), SaveSyncRoot(), false,
                    orphanResults);
                DeleteAuditedOrphanRoots(ReadDictionaryList(
                        audit, "orphanPortraitRoots"),
                    CampaignPortraitCacheRoot(), true, orphanResults);

                int deleted = results.Count(row =>
                    ReadBool(row, "campaignDeleted", false));
                int campaignFailures = results.Count - deleted;
                int orphanRootsDeleted = orphanResults.Count(row =>
                    ReadBool(row, "deleted", false));
                int orphanRootFailures = orphanResults.Count - orphanRootsDeleted;
                int failed = campaignFailures + orphanRootFailures;
                Dictionary<string, object> response = new Dictionary<string, object>
                {
                    ["ok"] = failed == 0,
                    ["result"] = failed == 0 ? "completed" : "partial",
                    ["campaignsSelected"] = targets.Count,
                    ["campaignsDeleted"] = deleted,
                    ["campaignsFailed"] = campaignFailures,
                    ["campaignIds"] = targets.Select(row =>
                        ReadString(row, "campaignId", "")).ToList(),
                    ["results"] = results,
                    ["staleSavePointsSelected"] = staleSavePointsSelected,
                    ["staleSavePointsDeleted"] = staleSavePointsDeleted,
                    ["staleSaveStatesDeleted"] = staleSaveStatesDeleted,
                    ["retainedCampaignsReconciled"] =
                        retainedCampaignsReconciled,
                    ["orphanRootsSelected"] = orphanResults.Count,
                    ["orphanRootsDeleted"] = orphanRootsDeleted,
                    ["orphanRootsFailed"] = orphanRootFailures,
                    ["orphanResults"] = orphanResults,
                    ["sharedPortraitLibraryPreserved"] = true,
                    ["message"] = failed == 0
                        ? deleted.ToString(CultureInfo.InvariantCulture) +
                            " Reign campaign(s) with no matching physical Bannerlord save were permanently deleted and " +
                            staleSavePointsDeleted.ToString(CultureInfo.InvariantCulture) +
                            " stale Save Sync registration(s) were removed. The _shared portrait library was preserved."
                        : "Some campaigns were deleted, but residual cleanup requires attention. Review the per-campaign results."
                };
                LogOperational("campaign.zero_save_cleanup", response);
                return response;
            }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static void DeleteAuditedOrphanRoots(
            IEnumerable<Dictionary<string, object>> auditedRoots,
            string ownedRoot,
            bool rejectShared,
            List<Dictionary<string, object>> results)
        {
            foreach (Dictionary<string, object> audited in
                auditedRoots ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                string name = ReadString(audited, "name", "");
                string path = ReadString(audited, "path", "");
                Dictionary<string, object> result =
                    new Dictionary<string, object>
                    {
                        ["category"] = ReadString(audited, "category", "orphan_root"),
                        ["name"] = name,
                        ["path"] = path,
                        ["files"] = ReadInt(audited, "fileCount", 0),
                        ["bytes"] = ReadLong(audited, "bytes", 0),
                        ["deleted"] = false
                    };
                try
                {
                    string expected = Path.GetFullPath(Path.Combine(
                        ownedRoot ?? "", name ?? ""));
                    if (!Path.GetFullPath(path ?? "").Equals(
                        expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            "Audited orphan path no longer matches its owned root.");
                    ValidateCampaignOwnedDirectory(path, ownedRoot, rejectShared);
                    DeleteDirectoryCompletely(path);
                    if (Directory.Exists(path))
                        throw new IOException(
                            "Orphan root still exists after cleanup.");
                    result["deleted"] = true;
                }
                catch (Exception ex)
                {
                    result["error"] = ex.Message;
                }
                results.Add(result);
            }
        }

        private static Dictionary<string, object> BuildCampaignBackupSummary(
            string campaignId,
            DirectoryInfo dir,
            long databaseBytes)
        {
            Dictionary<string, object> meta = dir == null
                ? new Dictionary<string, object>()
                : ReadJsonObject(Path.Combine(dir.FullName, "campaign.json"));
            string cacheDir = CampaignPortraitCacheDirectory(campaignId);
            long campaignFileBytes =
                dir == null ? 0 : SafeDirectorySize(dir.FullName);
            long portraitCacheBytes = SafeDirectorySize(cacheDir);
            int characterCount = dir == null
                ? 0 : SafeDirectoryCount(
                    Path.Combine(dir.FullName, "characters"));
            int portraitCount = (dir == null
                    ? 0 : SafeFileCount(
                        Path.Combine(dir.FullName, "characters"), "*.png"))
                + SafeFileCount(cacheDir, "*.png");
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["folder"] = dir == null ? "" : dir.Name,
                ["label"] = ReadString(
                    meta, "campaignLabel",
                    ReadString(meta, "mainHeroName", campaignId)),
                ["mainHeroName"] = ReadString(meta, "mainHeroName", ""),
                ["playerClanName"] = ReadString(meta, "playerClanName", ""),
                ["playerKingdomName"] =
                    ReadString(meta, "playerKingdomName", ""),
                ["updatedUtc"] = ReadString(
                    meta, "updatedUtc",
                    dir == null
                        ? DateTime.UtcNow.ToString("o")
                        : dir.LastWriteTimeUtc.ToString("o")),
                ["campaignBytes"] = campaignFileBytes + databaseBytes,
                ["databaseBytes"] = databaseBytes,
                ["portraitCacheBytes"] = portraitCacheBytes,
                ["totalBytes"] =
                    campaignFileBytes + databaseBytes + portraitCacheBytes,
                ["characterCount"] = characterCount,
                ["portraitCount"] = portraitCount,
                ["memoryCount"] =
                    ReignPostgreSqlStorage.TableRowCount(
                        campaignId, "memories"),
                ["acquaintanceCount"] =
                    ReignPostgreSqlStorage.TableRowCount(
                        campaignId, "acquaintances"),
                ["eventCount"] =
                    ReignPostgreSqlStorage.TableRowCount(
                        campaignId, "world_events"),
                ["hasWorldMemory"] = true,
                ["databaseProvider"] = "postgresql",
                ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                ["hasPortraitCache"] = Directory.Exists(cacheDir),
                ["saveSync"] = SaveSyncStatusSummary(campaignId)
            };
        }

        private static void WriteCampaignBackupDownload(NetworkStream stream, Dictionary<string, string> query)
        {
            string campaignId = query != null && query.TryGetValue("campaignId", out string requested) ? requested : "";
            if (string.IsNullOrWhiteSpace(campaignId))
            {
                WriteJson(stream, 400, new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." });
                return;
            }
            Dictionary<string, object> archive;
            CampaignDataGate.EnterWriteLock();
            try { archive = CreateCampaignBackupArchive(campaignId); }
            finally { CampaignDataGate.ExitWriteLock(); }
            if (!ReadBool(archive, "ok", false))
            {
                WriteJson(stream, 500, archive);
                return;
            }
            string path = ReadString(archive, "path", "");
            try
            {
                WriteDownloadFile(stream, path, "application/zip", ReadString(archive, "fileName", "Bannerlord-Reign-Campaign.reignbackup"));
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }

        private static Dictionary<string, object> CreateCampaignBackupArchive(string campaignId)
        {
            string campaignDir = CampaignDirectory(campaignId);
            bool databaseExists = ReignPostgreSqlStorage.ListCampaignMetadata().Any(
                row => ReadString(row, "campaignId", "")
                    .Equals(campaignId, StringComparison.OrdinalIgnoreCase));
            if (!databaseExists)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Campaign was not found in PostgreSQL."
                };
            string operationId = Guid.NewGuid().ToString("N");
            string backupRoot = Path.Combine(DataDir, "backups");
            // Recovery folders already contain long campaign-relative paths. Staging
            // beneath the installed Steam module adds another avoidable directory chain.
            string stagingRoot = Path.Combine(Path.GetTempPath(), "reign-b", operationId.Substring(0, 12));
            string stagedCampaign = Path.Combine(stagingRoot, "campaign");
            string stagedPortraits = Path.Combine(stagingRoot, "portrait_cache");
            string stagedSaveSync = Path.Combine(stagingRoot, "save_sync");
            string stagedNativeSaves = Path.Combine(stagingRoot, "native_saves");
            string generatedRoot = Path.Combine(backupRoot, "generated");
            Directory.CreateDirectory(stagedCampaign);
            Directory.CreateDirectory(generatedRoot);
            try
            {
                if (Directory.Exists(campaignDir))
                    CopyDirectoryTree(
                        campaignDir, stagedCampaign,
                        path => !IsLiveSqliteSidecar(path));
                string databaseArchive =
                    Path.Combine(stagingRoot, "postgresql.dump");
                ReignPostgreSqlStorage.ExportCampaignArchive(
                    campaignId, databaseArchive);
                string portraitCache = CampaignPortraitCacheDirectory(campaignId);
                if (Directory.Exists(portraitCache)) CopyDirectoryTree(portraitCache, stagedPortraits, null);
                string saveSyncRoot = SaveSyncCampaignRoot(campaignId);
                if (Directory.Exists(saveSyncRoot)) CopyDirectoryTree(saveSyncRoot, stagedSaveSync, null);
                Dictionary<string, object> storageAudit =
                    CampaignStorageAuditCore();
                Dictionary<string, object> nativeInventory =
                    ReadDictionary(storageAudit, "nativeSaveInventory") ??
                        new Dictionary<string, object>();
                if (!ReadBool(nativeInventory, "available", false))
                    throw new InvalidOperationException(
                        "The Bannerlord save directory is unavailable. A complete campaign backup was not created.");
                List<Dictionary<string, object>> matchingNativeSaves =
                    ReadDictionaryList(nativeInventory, "files")
                        .Where(row => ReadString(row,
                            "matchingCampaignId", "").Equals(
                                campaignId,
                                StringComparison.OrdinalIgnoreCase))
                        .ToList();
                if (matchingNativeSaves.Count == 0)
                    throw new InvalidOperationException(
                        "This campaign has no matching physical Bannerlord save. A load-ready complete backup was not created.");
                Directory.CreateDirectory(stagedNativeSaves);
                foreach (Dictionary<string, object> nativeSave in
                    matchingNativeSaves)
                {
                    string nativeFileName = ValidateNativeSaveFileName(
                        ReadString(nativeSave, "fileName", ""));
                    string source = Path.Combine(
                        CampaignNativeSaveRoot(), nativeFileName);
                    if (!File.Exists(source))
                        throw new FileNotFoundException(
                            "A matching native Bannerlord save disappeared during export.",
                            source);
                    File.Copy(source,
                        Path.Combine(stagedNativeSaves, nativeFileName), false);
                }

                Dictionary<string, object> meta = ReadJsonObject(Path.Combine(campaignDir, "campaign.json"));
                List<Dictionary<string, object>> files = BackupFileManifest(stagingRoot);
                Dictionary<string, object> manifest = new Dictionary<string, object>
                {
                    ["format"] = CampaignBackupFormat,
                    ["version"] = CampaignBackupVersion,
                    ["databaseProvider"] = "postgresql",
                    ["databaseName"] =
                        ReignPostgreSqlOptions.RequiredDatabaseName,
                    ["databaseEncoding"] =
                        ReignPostgreSqlOptions.RequiredEncoding,
                    ["databaseOwner"] = ReignPostgreSqlStorage.Options.Username,
                    ["databaseArchive"] = "postgresql.dump",
                    ["databaseBytes"] =
                        ReignPostgreSqlStorage.CampaignSizeBytes(campaignId),
                    ["saveSyncSnapshots"] =
                        ReignPostgreSqlStorage.ListSnapshots(campaignId),
                    ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                    ["campaignId"] = campaignId,
                    ["campaignFolder"] = Path.GetFileName(campaignDir),
                    ["campaignLabel"] = ReadString(meta, "campaignLabel", ReadString(meta, "mainHeroName", campaignId)),
                    ["mainHeroName"] = ReadString(meta, "mainHeroName", ""),
                    ["playerClanName"] = ReadString(meta, "playerClanName", ""),
                    ["playerKingdomName"] = ReadString(meta, "playerKingdomName", ""),
                    ["serverAssemblyVersion"] = typeof(Program).Assembly.GetName().Version?.ToString() ?? "",
                    ["scope"] = "Complete Bannerlord Reign campaign data, Save Sync ledger/snapshots/recovery metadata, campaign portrait cache, and matching native Bannerlord .sav files. Global settings and API keys are excluded.",
                    ["saveSyncIncluded"] = Directory.Exists(stagedSaveSync),
                    ["nativeSavesIncluded"] = true,
                    ["nativeSaveCount"] = matchingNativeSaves.Count,
                    ["nativeSaveRequired"] = false,
                    ["fileCount"] = files.Count,
                    ["expandedBytes"] = files.Sum(x => ReadLong(x, "bytes", 0)),
                    ["files"] = files
                };
                WriteJsonObject(Path.Combine(stagingRoot, "manifest.json"), manifest);
                string safeLabel = SafePathSegment(ReadString(manifest, "campaignLabel", campaignId), campaignId);
                if (safeLabel.Length > 70) safeLabel = safeLabel.Substring(0, 70);
                string fileName = "Bannerlord-Reign-" + safeLabel + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".reignbackup";
                string archivePath = Path.Combine(generatedRoot, operationId + ".reignbackup");
                ZipFile.CreateFromDirectory(stagingRoot, archivePath, CompressionLevel.Optimal, false, Encoding.UTF8);
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["path"] = archivePath,
                    ["fileName"] = fileName,
                    ["archiveBytes"] = new FileInfo(archivePath).Length,
                    ["expandedBytes"] = ReadLong(manifest, "expandedBytes", 0),
                    ["fileCount"] = files.Count,
                    ["nativeSaveCount"] = matchingNativeSaves.Count
                };
                LogOperational("campaign.backup_exported", result);
                return result;
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message, ["campaignId"] = campaignId };
            }
            finally
            {
                TryDeleteDirectory(stagingRoot);
            }
        }

        private static Dictionary<string, object> ImportCampaignBackup(string uploadPath, Dictionary<string, string> query)
        {
            CampaignDataGate.EnterWriteLock();
            try { return ImportCampaignBackupCore(uploadPath, query); }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> ImportCampaignBackupCore(string uploadPath, Dictionary<string, string> query)
        {
            bool replace = query != null && query.TryGetValue("replace", out string replaceText) && replaceText.Equals("true", StringComparison.OrdinalIgnoreCase);
            bool replaceNativeSaves = query != null
                && query.TryGetValue("replaceNativeSaves",
                    out string replaceNativeText)
                && replaceNativeText.Equals(
                    "true", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(uploadPath) || !File.Exists(uploadPath)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "No backup archive was uploaded." };
            string running = CampaignMutationBlockReason();
            if (!string.IsNullOrWhiteSpace(running)) { TryDeleteFile(uploadPath); return new Dictionary<string, object> { ["ok"] = false, ["error"] = running }; }
            string extractRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, ".x", Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                ExtractCampaignBackupSecurely(uploadPath, extractRoot);
                Dictionary<string, object> manifest = ReadJsonObject(Path.Combine(extractRoot, "manifest.json"));
                ValidateCampaignBackupManifest(extractRoot, manifest);
                string campaignId = ReadString(manifest, "campaignId", "");
                ValidateSaveSyncId(campaignId, "campaignId");
                string sourceCampaign = Path.Combine(extractRoot, "campaign");
                string sourcePortraits = Path.Combine(extractRoot, "portrait_cache");
                string sourceSaveSync = Path.Combine(extractRoot, "save_sync");
                string sourceNativeSaves = Path.Combine(
                    extractRoot, "native_saves");
                string sourceDatabase =
                    Path.Combine(extractRoot,
                        ReadString(manifest, "databaseArchive",
                            "postgresql.dump"));
                string destinationCampaign = StrictCampaignDirectory(campaignId);
                string destinationPortraits = CampaignPortraitCacheDirectory(campaignId);
                string destinationSaveSync = SaveSyncCampaignRoot(campaignId);
                bool databaseExists =
                    ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "")
                            .Equals(
                                campaignId,
                                StringComparison.OrdinalIgnoreCase));
                if (databaseExists && !replace)
                    return new Dictionary<string, object> { ["ok"] = false, ["conflict"] = true, ["campaignId"] = campaignId, ["error"] = "This campaign already exists. Enable Replace existing campaign to restore over it." };

                List<string> nativeSaveSources = Directory.Exists(
                        sourceNativeSaves)
                    ? Directory.GetFiles(sourceNativeSaves, "*",
                        SearchOption.TopDirectoryOnly)
                        .Select(path =>
                        {
                            ValidateNativeSaveFileName(
                                Path.GetFileName(path));
                            return path;
                        }).ToList()
                    : new List<string>();
                string nativeSaveRoot = CampaignNativeSaveRoot();
                List<string> nativeSaveConflicts = nativeSaveSources
                    .Where(source =>
                    {
                        string destination = Path.Combine(nativeSaveRoot,
                            Path.GetFileName(source));
                        return File.Exists(destination)
                            && !FileSha256(destination).Equals(
                                FileSha256(source),
                                StringComparison.OrdinalIgnoreCase);
                    })
                    .Select(Path.GetFileName).ToList();
                if (nativeSaveConflicts.Count > 0
                    && !replaceNativeSaves)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["conflict"] = true,
                        ["nativeSaveConflict"] = true,
                        ["campaignId"] = campaignId,
                        ["nativeSaveConflicts"] = nativeSaveConflicts,
                        ["error"] = "Native Bannerlord save files with the same names already exist. Enable Replace native saves to import this diagnostic bundle without silently overwriting them."
                    };

                string oldCampaign = "", oldPortraits = "", oldSaveSync = "";
                string oldDatabase = Path.Combine(
                    extractRoot, "recovery-postgresql.dump");
                List<Dictionary<string, object>> oldSnapshots =
                    new List<Dictionary<string, object>>();
                string nativeSaveRecoveryRoot = Path.Combine(
                    nativeSaveRoot,
                    ".reign-import-" + Guid.NewGuid().ToString("N")
                        .Substring(0, 12));
                List<Dictionary<string, object>> nativeSaveRollback =
                    new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> nativeSaveResults =
                    new List<Dictionary<string, object>>();
                try
                {
                    if (databaseExists)
                    {
                        oldSnapshots =
                            ReignPostgreSqlStorage.ListSnapshots(campaignId);
                        ReignPostgreSqlStorage.ExportCampaignArchive(
                            campaignId, oldDatabase);
                    }
                    oldCampaign = InstallImportedDirectory(sourceCampaign, destinationCampaign, replace);
                    if (Directory.Exists(sourcePortraits)) oldPortraits = InstallImportedDirectory(sourcePortraits, destinationPortraits, replace);
                    else if (replace && Directory.Exists(destinationPortraits))
                    {
                        oldPortraits = MoveImportedDirectoryAside(destinationPortraits);
                    }
                    if (Directory.Exists(sourceSaveSync)) oldSaveSync = InstallImportedDirectory(sourceSaveSync, destinationSaveSync, replace);
                    else if (replace && Directory.Exists(destinationSaveSync))
                    {
                        oldSaveSync = MoveImportedDirectoryAside(destinationSaveSync);
                    }
                    ReignPostgreSqlStorage.ImportCampaignArchive(
                        campaignId,
                        sourceDatabase,
                        ReadDictionaryList(manifest, "saveSyncSnapshots"),
                        replace);
                    nativeSaveResults = InstallImportedNativeSaves(
                        nativeSaveSources,
                        nativeSaveRoot,
                        replaceNativeSaves,
                        nativeSaveRecoveryRoot,
                        nativeSaveRollback);
                    int importedSavePoints = ReadDictionaryList(
                        ReadSaveSyncLedger(campaignId), "points").Count;
                    MarkCampaignSaveSyncLifecycle(
                        campaignId,
                        nativeSaveSources.Count > 0
                            ? "registered"
                            : "imported_awaiting_first_load",
                        importedSavePoints > 0);
                    CancelPendingCampaignRetirement(
                        campaignId, "campaign_backup_imported");
                    TryDeleteDirectory(oldCampaign);
                    TryDeleteDirectory(oldPortraits);
                    TryDeleteDirectory(oldSaveSync);
                    TryDeleteDirectory(nativeSaveRecoveryRoot);
                }
                catch
                {
                    RestoreImportedNativeSaves(nativeSaveRollback);
                    TryDeleteDirectory(nativeSaveRecoveryRoot);
                    RestoreImportedDirectory(destinationCampaign, oldCampaign);
                    RestoreImportedDirectory(destinationPortraits, oldPortraits);
                    RestoreImportedDirectory(destinationSaveSync, oldSaveSync);
                    if (File.Exists(oldDatabase))
                    {
                        try
                        {
                            ReignPostgreSqlStorage.ImportCampaignArchive(
                                campaignId,
                                oldDatabase,
                                oldSnapshots,
                                true);
                        }
                        catch { }
                    }
                    throw;
                }
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["campaignId"] = campaignId,
                    ["label"] = ReadString(manifest, "campaignLabel", campaignId),
                    ["replaced"] = replace,
                    ["nativeSavesRestored"] = nativeSaveResults.Count(row =>
                        ReadString(row, "result", "") == "installed"
                        || ReadString(row, "result", "") == "replaced"),
                    ["nativeSavesAlreadyPresent"] = nativeSaveResults.Count(row =>
                        ReadString(row, "result", "") == "already_present"),
                    ["nativeSaveResults"] = nativeSaveResults,
                    ["fileCount"] = ReadInt(manifest, "fileCount", 0),
                    ["expandedBytes"] = ReadLong(manifest, "expandedBytes", 0),
                    ["message"] = nativeSaveSources.Count > 0
                        ? "Campaign and native Bannerlord save files restored. Load the imported save to continue with its fully paired Reign history."
                        : "Legacy Reign campaign restored. Supply its matching native Bannerlord save before loading it."
                };
                LogOperational("campaign.backup_imported", result);
                return result;
            }
            catch (Exception ex)
            {
                LogOperational("campaign.backup_import_failed", new Dictionary<string, object> { ["error"] = ex.Message });
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
            }
            finally
            {
                TryDeleteFile(uploadPath);
                TryDeleteDirectory(extractRoot);
            }
        }

        private static List<Dictionary<string, object>> InstallImportedNativeSaves(
            IEnumerable<string> sourceFiles,
            string destinationRoot,
            bool replace,
            string recoveryRoot,
            List<Dictionary<string, object>> rollback)
        {
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            List<string> sources = (sourceFiles ?? Enumerable.Empty<string>())
                .ToList();
            if (sources.Count == 0) return results;
            Directory.CreateDirectory(destinationRoot);
            foreach (string source in sources)
            {
                string fileName = ValidateNativeSaveFileName(
                    Path.GetFileName(source));
                string destination = Path.Combine(destinationRoot, fileName);
                if (File.Exists(destination)
                    && FileSha256(destination).Equals(
                        FileSha256(source),
                        StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["fileName"] = fileName,
                        ["result"] = "already_present",
                        ["bytes"] = new FileInfo(destination).Length
                    });
                    continue;
                }
                if (File.Exists(destination) && !replace)
                    throw new IOException(
                        "Native Bannerlord save conflict: " + fileName);

                Directory.CreateDirectory(recoveryRoot);
                string backup = Path.Combine(recoveryRoot, fileName);
                bool existed = File.Exists(destination);
                if (existed) File.Copy(destination, backup, false);
                rollback.Add(new Dictionary<string, object>
                {
                    ["destination"] = destination,
                    ["backup"] = backup,
                    ["existed"] = existed
                });
                string temporary = Path.Combine(destinationRoot,
                    ".ri-" + Guid.NewGuid().ToString("N")
                        .Substring(0, 12) + ".tmp");
                try
                {
                    File.Copy(source, temporary, true);
                    if (existed)
                    {
                        // Use the same atomic rename primitive as profile persistence. The
                        // separate recovery copy above retains the original native save.
                        const uint replaceExisting = 0x1;
                        const uint writeThrough = 0x8;
                        if (!MoveFileEx(ExtendedLengthPath(temporary), ExtendedLengthPath(destination),
                            replaceExisting | writeThrough))
                            throw new IOException("Atomic native-save replacement failed for " + fileName + ".",
                                new System.ComponentModel.Win32Exception(
                                    System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
                    }
                    else
                        File.Move(temporary, destination);
                }
                finally
                {
                    TryDeleteFile(temporary);
                }
                if (!FileSha256(destination).Equals(
                    FileSha256(source), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Imported native save hash verification failed: " +
                        fileName);
                results.Add(new Dictionary<string, object>
                {
                    ["fileName"] = fileName,
                    ["result"] = existed ? "replaced" : "installed",
                    ["bytes"] = new FileInfo(destination).Length
                });
            }
            return results;
        }

        private static void RestoreImportedNativeSaves(
            IEnumerable<Dictionary<string, object>> rollback)
        {
            foreach (Dictionary<string, object> item in
                (rollback ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Reverse())
            {
                string destination = ReadString(
                    item, "destination", "");
                string backup = ReadString(item, "backup", "");
                try
                {
                    if (ReadBool(item, "existed", false)
                        && File.Exists(backup))
                        File.Copy(backup, destination, true);
                    else
                        TryDeleteFile(destination);
                }
                catch { }
            }
        }

        private static Dictionary<string, object> DeleteCampaignBackupData(Dictionary<string, object> payload)
        {
            CampaignDataGate.EnterWriteLock();
            try { return DeleteCampaignBackupDataCore(payload); }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> DeleteCampaignBackupDataCore(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            string confirmation = ReadString(payload, "confirmation", "");
            string campaignDir = CampaignDirectory(campaignId);
            bool databaseExists =
                ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                    ReadString(row, "campaignId", "")
                        .Equals(campaignId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(campaignId) || !databaseExists)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Campaign was not found."
                };
            Dictionary<string, object> meta = ReadJsonObject(Path.Combine(campaignDir, "campaign.json"));
            string label = ReadString(meta, "campaignLabel", ReadString(meta, "mainHeroName", campaignId));
            if (!confirmation.Equals(campaignId, StringComparison.OrdinalIgnoreCase) && !confirmation.Equals(label, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Confirmation must exactly match the campaign ID or label." };
            return DeleteCampaignOwnedDataCore(
                campaignId, label, false, "confirmed_campaign_delete");
        }

        private static Dictionary<string, object> DeleteCampaignOwnedDataCore(
            string campaignId,
            string label,
            bool allowBannerlordRunning,
            string reason)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            if (campaignId.Equals("_shared", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["campaignDeleted"] = false,
                    ["error"] = "The shared portrait library cannot be deleted as a campaign."
                };
            if (!allowBannerlordRunning)
            {
                string running = CampaignMutationBlockReason();
                if (!string.IsNullOrWhiteSpace(running))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["campaignDeleted"] = false,
                        ["error"] = running
                    };
            }

            lock (CampaignBackupMutationLock)
            {
                string campaignDir = StrictCampaignDirectory(campaignId);
                string portraitDir = CampaignPortraitCacheDirectory(campaignId);
                string saveSyncDir = SaveSyncCampaignRoot(campaignId);
                string campaignRoot = CampaignsRoot();
                string portraitRoot = CampaignPortraitCacheRoot();
                string saveSyncRoot = SaveSyncRoot();
                ValidateCampaignOwnedDirectory(campaignDir, campaignRoot, false);
                ValidateCampaignOwnedDirectory(portraitDir, portraitRoot, true);
                ValidateCampaignOwnedDirectory(saveSyncDir, saveSyncRoot, false);

                bool databaseExists = ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                    ReadString(row, "campaignId", "").Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase));
                bool campaignExists = Directory.Exists(campaignDir);
                bool portraitExists = Directory.Exists(portraitDir);
                bool saveSyncExists = Directory.Exists(saveSyncDir);
                if (!databaseExists && !campaignExists && !portraitExists && !saveSyncExists)
                {
                    CancelPendingCampaignRetirement(campaignId, "already_deleted");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "already_deleted",
                        ["campaignId"] = campaignId,
                        ["campaignDeleted"] = true,
                        ["cleanupComplete"] = true,
                        ["countsBySubsystem"] = EmptyCampaignDeletionCounts()
                    };
                }

                Dictionary<string, object> counts = EmptyCampaignDeletionCounts();
                counts["campaignFiles"] = CampaignDeletionCount(campaignDir);
                counts["portraitCache"] = CampaignDeletionCount(portraitDir);
                counts["saveSync"] = CampaignDeletionCount(saveSyncDir);
                Dictionary<string, object> databaseCounts =
                    ReadDictionary(counts, "postgresql") ?? new Dictionary<string, object>();
                databaseCounts["schemas"] = databaseExists ? 1 : 0;
                databaseCounts["snapshots"] = databaseExists
                    ? ReignPostgreSqlStorage.ListSnapshots(campaignId).Count
                    : 0;

                string operationId = SafePathSegment(campaignId, "campaign") + "_" +
                    Guid.NewGuid().ToString("N");
                string dataStageRoot = Path.Combine(
                    DataDir, "campaign-retirement-staging", operationId);
                string portraitParent = Directory.GetParent(
                    Path.GetFullPath(portraitRoot).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar))?.FullName ?? DataDir;
                string portraitStageRoot = Path.Combine(
                    portraitParent, ".reign-campaign-retirement-staging", operationId);
                string movedCampaign = Path.Combine(dataStageRoot, "campaign");
                string movedSaveSync = Path.Combine(dataStageRoot, "save-sync");
                string movedPortrait = Path.Combine(portraitStageRoot, "portrait-cache");
                bool movedCampaignReady = false;
                bool movedPortraitReady = false;
                bool movedSaveSyncReady = false;
                bool databaseDropped = false;
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    int vectorsDeleted = databaseExists
                        ? DeleteCampaignSemanticVectors(campaignId)
                        : 0;
                    Dictionary<string, object> semanticCounts =
                        ReadDictionary(counts, "semanticVectors") ?? new Dictionary<string, object>();
                    semanticCounts["records"] = vectorsDeleted;

                    Directory.CreateDirectory(dataStageRoot);
                    Directory.CreateDirectory(portraitStageRoot);
                    WriteCampaignRetirementTransaction(
                        dataStageRoot, campaignId, "staging");
                    WriteCampaignRetirementTransaction(
                        portraitStageRoot, campaignId, "staging");
                    if (campaignExists)
                    {
                        MoveDirectoryWithRetries(campaignDir, movedCampaign);
                        VerifyStagedCampaignDirectory(campaignDir, movedCampaign);
                        movedCampaignReady = true;
                    }
                    if (saveSyncExists)
                    {
                        MoveDirectoryWithRetries(saveSyncDir, movedSaveSync);
                        VerifyStagedCampaignDirectory(saveSyncDir, movedSaveSync);
                        movedSaveSyncReady = true;
                    }
                    if (portraitExists)
                    {
                        MoveDirectoryWithRetries(portraitDir, movedPortrait);
                        VerifyStagedCampaignDirectory(portraitDir, movedPortrait);
                        movedPortraitReady = true;
                    }
                    if (CampaignRetirementFailureInjectionForTests == "after_stage")
                        throw new IOException("Injected campaign-retirement staging failure.");

                    if (databaseExists)
                    {
                        ReignPostgreSqlStorage.DropCampaign(campaignId);
                        databaseDropped = true;
                    }
                    WriteCampaignRetirementTransaction(
                        dataStageRoot, campaignId, "committed");
                    WriteCampaignRetirementTransaction(
                        portraitStageRoot, campaignId, "committed");
                    ClearSaveSyncCampaignRuntime(campaignId);
                    CancelPendingCampaignRetirement(campaignId, "campaign_deleted");

                    List<string> residuals = new List<string>();
                    if (CampaignRetirementFailureInjectionForTests == "after_database_drop")
                    {
                        if (Directory.Exists(dataStageRoot)) residuals.Add(dataStageRoot);
                        if (Directory.Exists(portraitStageRoot)) residuals.Add(portraitStageRoot);
                    }
                    else
                    {
                        DeleteCampaignStagingRoot(dataStageRoot, residuals);
                        DeleteCampaignStagingRoot(portraitStageRoot, residuals);
                    }
                    bool rootsGone = !Directory.Exists(campaignDir)
                        && !Directory.Exists(saveSyncDir)
                        && !Directory.Exists(portraitDir);
                    bool databaseGone = !ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "").Equals(
                            campaignId, StringComparison.OrdinalIgnoreCase));
                    bool campaignDeleted = rootsGone && databaseGone;
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = campaignDeleted,
                        ["result"] = campaignDeleted
                            ? residuals.Count == 0 ? "deleted" : "deleted_cleanup_pending"
                            : "delete_verification_failed",
                        ["campaignId"] = campaignId,
                        ["label"] = label ?? campaignId,
                        ["reason"] = reason ?? "",
                        ["campaignDeleted"] = campaignDeleted,
                        ["campaignDeletionPending"] = false,
                        ["cleanupComplete"] = residuals.Count == 0,
                        ["residualCleanup"] = residuals,
                        ["countsBySubsystem"] = counts,
                        ["sharedPortraitLibraryPreserved"] = true,
                        ["message"] = residuals.Count == 0
                            ? "Campaign data, Save Sync storage, semantic vectors, and its portrait cache were permanently deleted."
                            : "The campaign is no longer active, but staged files remain for startup cleanup."
                    };
                    LogOperational("campaign.deleted", result);
                    return result;
                }
                catch (Exception ex)
                {
                    List<string> rollbackErrors = new List<string>();
                    if (!databaseDropped)
                    {
                        RestoreStagedCampaignDirectory(
                            movedCampaign, campaignDir, movedCampaignReady, rollbackErrors);
                        RestoreStagedCampaignDirectory(
                            movedSaveSync, saveSyncDir, movedSaveSyncReady, rollbackErrors);
                        RestoreStagedCampaignDirectory(
                            movedPortrait, portraitDir, movedPortraitReady, rollbackErrors);
                    }
                    Dictionary<string, object> failed = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["result"] = databaseDropped
                            ? "deleted_cleanup_pending"
                            : "delete_failed_recovered",
                        ["campaignId"] = campaignId,
                        ["campaignDeleted"] = databaseDropped,
                        ["campaignDeletionPending"] = !databaseDropped,
                        ["cleanupComplete"] = false,
                        ["countsBySubsystem"] = counts,
                        ["residualCleanup"] = new[] { dataStageRoot, portraitStageRoot }
                            .Where(Directory.Exists).ToList(),
                        ["rollbackErrors"] = rollbackErrors,
                        ["error"] = ex.Message
                    };
                    LogOperational("campaign.delete_failed", failed);
                    return failed;
                }
            }
        }

        private static Dictionary<string, object> DeleteAllCampaignBackupData(Dictionary<string, object> payload)
        {
            CampaignDataGate.EnterWriteLock();
            try { return DeleteAllCampaignBackupDataCore(payload); }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> DeleteAllCampaignBackupDataCore(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadString(payload, "confirmation", "").Equals("delete", StringComparison.Ordinal))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Type delete exactly to confirm permanent removal of every Reign campaign." };

            string running = CampaignMutationBlockReason();
            if (!string.IsNullOrWhiteSpace(running)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = running };
            if (VerificationTask != null && !VerificationTask.IsCompleted)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Wait for the active verification run to finish before deleting all campaign and test data." };

            lock (CampaignBackupMutationLock)
            {
                string campaignsRoot = CampaignsRoot();
                string backupsRoot = Path.Combine(DataDir, "backups");
                string vectorsRoot = Path.Combine(DataDir, "vectors");
                string portraitsRoot = CampaignPortraitCacheRoot();
                string saveSyncRoot = SaveSyncRoot();
                string legacySaveSyncRoot = Path.Combine(DataDir, "save-sync");
                string testRunsRoot = Path.Combine(TestsDir, "runs");
                string testSnapshotsRoot = Path.Combine(TestsDir, "snapshots");
                string verificationRoot = Path.Combine(TestsDir, "verification");
                Dictionary<string, object> settings = LoadSettings();
                bool localVectors = !ReadString(settings, "vectorProvider", "local").Equals("qdrant", StringComparison.OrdinalIgnoreCase);
                int campaignCount = SafeDirectoryCount(campaignsRoot);
                int backupCount = SafeFileCount(backupsRoot, "*.reignbackup");
                long bytesDeleted = SafeDirectorySize(campaignsRoot) + SafeDirectorySize(backupsRoot) + CampaignPortraitDataSizeExcludingShared(portraitsRoot)
                    + SafeDirectorySize(saveSyncRoot) + SafeDirectorySize(legacySaveSyncRoot)
                    + SafeDirectorySize(testRunsRoot) + SafeDirectorySize(testSnapshotsRoot) + SafeDirectorySize(verificationRoot);
                if (localVectors) bytesDeleted += SafeDirectorySize(vectorsRoot);
                string legacyActionQueue = Path.Combine(DataDir, "action-queue.json");
                try { if (File.Exists(legacyActionQueue)) bytesDeleted += new FileInfo(legacyActionQueue).Length; } catch { }
                try
                {
                    if (!localVectors) DeleteExternalSemanticCollection(settings);
                    DeleteAllCampaignResponseSent.Reset();
                    DeleteAllCampaignDataPending = true;
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["serverStopping"] = true,
                        ["campaignsScheduled"] = campaignCount,
                        ["backupArchivesScheduled"] = backupCount,
                        ["bytesScheduled"] = bytesDeleted,
                        ["semanticProvider"] = localVectors ? "local" : "qdrant",
                        ["cleanupMode"] = "server_shutdown",
                        ["message"] = "Campaign deletion is confirmed. Reign is shutting down, then this same server process will remove all campaign data, campaign-specific portraits, backups, queued actions, semantic indexes, and accumulated test results. The pregenerated shared portrait library, test definitions, global settings, API key, and native Bannerlord saves are preserved. Restart the Reign server after this page disconnects."
                    };
                    LogOperational("campaign.all_delete_scheduled", result);
                    RequestServerShutdown("campaign_delete_all");
                    return result;
                }
                catch (Exception ex)
                {
                    DeleteAllCampaignDataPending = false;
                    DeleteAllCampaignResponseSent.Set();
                    LogOperational("campaign.all_delete_failed", new Dictionary<string, object> { ["error"] = ex.Message });
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
                }
            }
        }

        private static int CompleteDeferredDeleteAllCampaignData(string[] args)
        {
            int parentPid = ReadIntArgument(args ?? new string[0], "--wait-pid", 0);
            try
            {
                if (parentPid > 0)
                {
                    try
                    {
                        using (Process parent = Process.GetProcessById(parentPid))
                        {
                            if (!parent.WaitForExit(60000)) throw new IOException("The Reign server did not release its campaign files within 60 seconds.");
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        ["error"] = ex.Message
                    };
                    Directory.CreateDirectory(LogsDir);
                    WriteJsonObject(Path.Combine(LogsDir, "delete-all-last.json"), result);
                    LogOperational("campaign.all_delete_failed", result);
                }
                catch { }
                return 1;
            }
            return CompleteDeleteAllCampaignData();
        }

        private static int CompleteDeleteAllCampaignData()
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            try
            {
                string running = CampaignMutationBlockReason();
                if (!string.IsNullOrWhiteSpace(running)) throw new InvalidOperationException(running);
                ReignPostgreSqlStorage.ClearAllPools();
                ReignPostgreSqlStorage.DropAllCampaigns();
                List<string> directoryTargets = new List<string>
                {
                    CampaignsRoot(),
                    Path.Combine(DataDir, "backups"),
                    SaveSyncRoot(),
                    Path.Combine(DataDir, "save-sync"),
                    Path.Combine(DataDir, "vectors"),
                    Path.Combine(DataDir, ".i"),
                    Path.Combine(TestsDir, "runs"),
                    Path.Combine(TestsDir, "snapshots"),
                    Path.Combine(TestsDir, "verification")
                };
                int rootsDeleted = 0;
                foreach (string target in directoryTargets.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(target)) continue;
                    DeleteDirectoryCompletely(target);
                    rootsDeleted++;
                }
                int portraitCachesDeleted = DeleteCampaignPortraitDataPreservingShared(CampaignPortraitCacheRoot());
                TryDeleteFile(Path.Combine(DataDir, "action-queue.json"));
                TryDeleteFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification-latest.json"));
                lock (WorldHistoryCacheLock) WorldHistoryClaimCache.Clear();
                lock (CharacterEditorAuthorityCacheLock) CharacterEditorAuthorityByCampaign.Clear();
                result["ok"] = true;
                result["rootsDeleted"] = rootsDeleted;
                result["portraitCachesDeleted"] = portraitCachesDeleted;
                result["sharedPortraitLibraryPreserved"] = Directory.Exists(Path.Combine(CampaignPortraitCacheRoot(), "_shared"));
                result["testDefinitionsPreserved"] = Directory.Exists(Path.Combine(TestsDir, "cases"));
                result["settingsPreserved"] = File.Exists(SettingsPath);
                result["message"] = "All Reign campaign and accumulated test data was deleted after the server released its files. The pregenerated shared portrait library, test definitions, global settings, API key, and native Bannerlord saves were preserved.";
                Directory.CreateDirectory(LogsDir);
                WriteJsonObject(Path.Combine(LogsDir, "delete-all-last.json"), result);
                LogOperational("campaign.all_deleted", result);
                return 0;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
                try
                {
                    Directory.CreateDirectory(LogsDir);
                    WriteJsonObject(Path.Combine(LogsDir, "delete-all-last.json"), result);
                    LogOperational("campaign.all_delete_failed", result);
                }
                catch { }
                return 1;
            }
        }

        private static void DeleteExternalSemanticCollection(Dictionary<string, object> settings)
        {
            if (string.IsNullOrWhiteSpace(ReadString(settings, "qdrantUrl", "")))
                throw new InvalidOperationException("External Qdrant is selected, but its URL is not configured. No campaign data was deleted.");

            StartManagedSemanticWorker(settings);
            Dictionary<string, object> request = SemanticProviderRequest(settings);
            request["collection"] = SemanticCollection;
            request["deleteCollection"] = true;
            Exception last = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Dictionary<string, object> response = TryParseJsonObject(PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/delete"), Json.Serialize(request), 15000)) ?? new Dictionary<string, object>();
                    if (!ReadBool(response, "ok", false)) throw new InvalidOperationException(ReadString(response, "error", "Qdrant collection deletion failed."));
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 19) Thread.Sleep(250);
                }
            }
            throw new InvalidOperationException("Reign could not remove its external Qdrant campaign index. No local campaign data was deleted. " + (last?.Message ?? ""));
        }

        private static void DeleteAllCampaignStorageCore(IEnumerable<string> directoryTargets, IEnumerable<string> fileTargets, string stagingRoot)
        {
            List<KeyValuePair<string, string>> movedDirectories = new List<KeyValuePair<string, string>>();
            List<KeyValuePair<string, string>> movedFiles = new List<KeyValuePair<string, string>>();
            Directory.CreateDirectory(stagingRoot);
            try
            {
                int rootSerial = 0;
                foreach (string root in (directoryTargets ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root)) continue;
                    string stagedRoot = Path.Combine(stagingRoot, "root_" + (++rootSerial).ToString("D2", CultureInfo.InvariantCulture) + "_" + SafePathSegment(Path.GetFileName(root), "data"));
                    Directory.CreateDirectory(stagedRoot);
                    int entrySerial = 0;
                    foreach (string source in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        string destination = Path.Combine(stagedRoot, "directory_" + (++entrySerial).ToString("D4", CultureInfo.InvariantCulture));
                        MoveDirectoryWithRetries(source, destination);
                        movedDirectories.Add(new KeyValuePair<string, string>(source, destination));
                    }
                    foreach (string source in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        string destination = Path.Combine(stagedRoot, "file_" + (++entrySerial).ToString("D4", CultureInfo.InvariantCulture));
                        MoveFileWithRetries(source, destination);
                        movedFiles.Add(new KeyValuePair<string, string>(source, destination));
                    }
                }
                int serial = 0;
                foreach (string source in (fileTargets ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(source)) continue;
                    string destination = Path.Combine(stagingRoot, "file_" + (++serial).ToString("D2", CultureInfo.InvariantCulture) + "_" + SafePathSegment(Path.GetFileName(source), "data"));
                    MoveFileWithRetries(source, destination);
                    movedFiles.Add(new KeyValuePair<string, string>(source, destination));
                }
            }
            catch
            {
                foreach (KeyValuePair<string, string> item in movedFiles.AsEnumerable().Reverse())
                    try { if (!File.Exists(item.Key) && File.Exists(item.Value)) MoveFileWithRetries(item.Value, item.Key); } catch { }
                foreach (KeyValuePair<string, string> item in movedDirectories.AsEnumerable().Reverse())
                    try { if (!Directory.Exists(item.Key) && Directory.Exists(item.Value)) MoveDirectoryWithRetries(item.Value, item.Key); } catch { }
                TryDeleteDirectory(stagingRoot);
                throw;
            }
            DeleteDirectoryCompletely(stagingRoot);
        }

        private static long CampaignPortraitDataSizeExcludingShared(string portraitRoot)
        {
            if (string.IsNullOrWhiteSpace(portraitRoot) || !Directory.Exists(portraitRoot)) return 0;
            long bytes = 0;
            foreach (string directory in Directory.GetDirectories(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals("_shared", StringComparison.OrdinalIgnoreCase)) continue;
                bytes += SafeDirectorySize(directory);
            }
            foreach (string file in Directory.GetFiles(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { bytes += new FileInfo(file).Length; } catch { }
            }
            return bytes;
        }

        private static int DeleteCampaignPortraitDataPreservingShared(string portraitRoot)
        {
            if (string.IsNullOrWhiteSpace(portraitRoot) || !Directory.Exists(portraitRoot)) return 0;
            int deleted = 0;
            foreach (string directory in Directory.GetDirectories(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals("_shared", StringComparison.OrdinalIgnoreCase)) continue;
                DeleteDirectoryCompletely(directory);
                deleted++;
            }
            foreach (string file in Directory.GetFiles(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(file);
            }
            return deleted;
        }

        private static void ValidateCampaignBackupManifest(string root, Dictionary<string, object> manifest)
        {
            if (!ReadString(manifest, "format", "").Equals(CampaignBackupFormat, StringComparison.Ordinal)) throw new InvalidDataException("This is not a Bannerlord Reign campaign backup.");
            int version = ReadInt(manifest, "version", 0);
            if (version < CampaignBackupMinimumSupportedVersion
                || version > CampaignBackupVersion)
                throw new InvalidDataException(
                    "Unsupported campaign backup version.");
            if (!ReadString(manifest, "databaseProvider", "")
                    .Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                || !ReadString(manifest, "databaseName", "")
                    .Equals(
                        ReignPostgreSqlOptions.RequiredDatabaseName,
                        StringComparison.Ordinal)
                || !ReadString(manifest, "databaseEncoding", "")
                    .Equals(
                        ReignPostgreSqlOptions.RequiredEncoding,
                        StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "This archive is not a UTF-8 PostgreSQL Reign database backup.");
            string campaignId = ReadString(manifest, "campaignId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            if (!Directory.Exists(Path.Combine(root, "campaign"))) throw new InvalidDataException("The backup does not contain campaign data.");
            string databaseArchive = Path.Combine(
                root,
                ReadString(manifest, "databaseArchive", "postgresql.dump"));
            if (!File.Exists(databaseArchive)
                || new FileInfo(databaseArchive).Length <= 0)
                throw new InvalidDataException(
                    "The backup does not contain its PostgreSQL archive.");
            List<Dictionary<string, object>> files = ReadDictionaryList(manifest, "files");
            if (files.Count > CampaignBackupMaxEntries) throw new InvalidDataException("The backup contains too many files.");
            if (version >= 3)
            {
                string nativeRoot = Path.Combine(root, "native_saves");
                if (!Directory.Exists(nativeRoot))
                    throw new InvalidDataException(
                        "The complete backup does not contain native Bannerlord saves.");
                List<string> nativeFiles = Directory.GetFiles(
                    nativeRoot, "*", SearchOption.TopDirectoryOnly).ToList();
                foreach (string nativeFile in nativeFiles)
                    ValidateNativeSaveFileName(Path.GetFileName(nativeFile));
                int expectedNativeSaves = ReadInt(
                    manifest, "nativeSaveCount", -1);
                if (!ReadBool(manifest, "nativeSavesIncluded", false)
                    || expectedNativeSaves <= 0
                    || nativeFiles.Count != expectedNativeSaves)
                    throw new InvalidDataException(
                        "The native Bannerlord save inventory is incomplete.");
            }
            long total = 0;
            foreach (Dictionary<string, object> file in files)
            {
                string relative = ReadString(file, "path", "").Replace('/', Path.DirectorySeparatorChar);
                string full = SafeBackupPath(root, relative);
                if (!File.Exists(full)) throw new InvalidDataException("Backup file is missing: " + relative);
                long expectedBytes = ReadLong(file, "bytes", -1);
                long actualBytes = new FileInfo(full).Length;
                total += actualBytes;
                if (expectedBytes != actualBytes) throw new InvalidDataException("Backup file size mismatch: " + relative);
                if (!FileSha256(full).Equals(ReadString(file, "sha256", ""), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Backup hash mismatch: " + relative);
            }
            if (total > CampaignBackupMaxExpandedBytes) throw new InvalidDataException("The expanded backup exceeds the safety limit.");
        }

        private static void ExtractCampaignBackupSecurely(string archivePath, string destination)
        {
            Directory.CreateDirectory(destination);
            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                if (archive.Entries.Count > CampaignBackupMaxEntries) throw new InvalidDataException("The archive contains too many entries.");
                long expanded = archive.Entries.Sum(x => x.Length);
                if (expanded > CampaignBackupMaxExpandedBytes) throw new InvalidDataException("The expanded archive exceeds the safety limit.");
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string full = SafeBackupPath(destination, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                    if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(full); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    using (Stream input = entry.Open())
                    using (FileStream output = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
                }
            }
        }

        private static string InstallImportedDirectory(string source, string destination, bool replace)
        {
            if (!Directory.Exists(source)) return "";
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string incoming = Path.Combine(DataDir, ".i", Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(Path.GetDirectoryName(incoming));
            CopyDirectoryTree(source, incoming, null);
            string old = "";
            if (Directory.Exists(destination))
            {
                if (!replace) { TryDeleteDirectory(incoming); throw new IOException("Destination campaign already exists."); }
                old = MoveImportedDirectoryAside(destination);
            }
            MoveDirectoryWithRetries(incoming, destination);
            return old;
        }

        private static string MoveImportedDirectoryAside(string destination)
        {
            string old = Path.Combine(DataDir, ".r", Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(Path.GetDirectoryName(old));
            MoveDirectoryWithRetries(destination, old);
            return old;
        }

        private static void RestoreImportedDirectory(string destination, string old)
        {
            string failed = "";
            try
            {
                if (Directory.Exists(destination))
                {
                    failed = Path.Combine(DataDir, ".f", Guid.NewGuid().ToString("N").Substring(0, 12));
                    Directory.CreateDirectory(Path.GetDirectoryName(failed));
                    MoveDirectoryWithRetries(destination, failed);
                }
                if (!string.IsNullOrWhiteSpace(old) && Directory.Exists(old)) MoveDirectoryWithRetries(old, destination);
            }
            catch { }
            finally { TryDeleteDirectory(failed); }
        }

        private static List<Dictionary<string, object>> BackupFileManifest(string root)
        {
            string[] paths = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Dictionary<string, object>[] rows = new Dictionary<string, object>[paths.Length];
            Parallel.For(
                0,
                paths.Length,
                new ParallelOptions
                {
                    // Save Sync manifests are dominated by thousands of small
                    // character files. Hashing them serially made a 2,000-file
                    // snapshot spend roughly 29 seconds reopening freshly copied
                    // files. A conservative bounded fan-out preserves exact SHA-256
                    // integrity while avoiding unbounded disk or antivirus pressure.
                    MaxDegreeOfParallelism = Math.Max(1, Math.Min(8, Environment.ProcessorCount))
                },
                index =>
                {
                    string path = paths[index];
                    rows[index] = new Dictionary<string, object>
                    {
                        ["path"] = RelativeBackupPath(root, path),
                        ["bytes"] = new FileInfo(path).Length,
                        ["sha256"] = FileSha256(path)
                    };
                });
            return rows.ToList();
        }

        private static string CampaignPortraitCacheDirectory(string campaignId)
        {
            string root = CampaignPortraitCacheRoot();
            return string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, SafePathSegment(campaignId, "unknown"));
        }

        private static string CampaignPortraitCacheRoot()
        {
            return Path.Combine(DataDir, "PortraitCache");
        }

        private static string CampaignRetirementLedgerPath()
        {
            return Path.Combine(DataDir, "campaign-retirements.json");
        }

        private static Dictionary<string, object> ReadCampaignRetirementLedger()
        {
            lock (CampaignRetirementLedgerLock)
            {
                Dictionary<string, object> ledger =
                    ReadJsonObject(CampaignRetirementLedgerPath());
                ledger["version"] = CampaignRetirementVersion;
                if (!ledger.ContainsKey("pending"))
                    ledger["pending"] = new List<Dictionary<string, object>>();
                return ledger;
            }
        }

        private static void WriteCampaignRetirementLedger(
            Dictionary<string, object> ledger)
        {
            lock (CampaignRetirementLedgerLock)
            {
                ledger = ledger ?? new Dictionary<string, object>();
                ledger["version"] = CampaignRetirementVersion;
                ledger["updatedUtc"] = DateTime.UtcNow.ToString(
                    "o", CultureInfo.InvariantCulture);
                if (!ledger.ContainsKey("pending"))
                    ledger["pending"] = new List<Dictionary<string, object>>();
                WriteJsonObject(CampaignRetirementLedgerPath(), ledger);
            }
        }

        private static Dictionary<string, object> SchedulePendingCampaignRetirement(
            string campaignId,
            string savePointId,
            string nativeSaveName)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            lock (CampaignRetirementLedgerLock)
            {
                Dictionary<string, object> ledger = ReadCampaignRetirementLedger();
                List<Dictionary<string, object>> pending =
                    ReadDictionaryList(ledger, "pending");
                Dictionary<string, object> entry = pending.FirstOrDefault(row =>
                    ReadString(row, "campaignId", "").Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new Dictionary<string, object>();
                    pending.Add(entry);
                }
                entry["campaignId"] = campaignId;
                entry["state"] = "pending_final_save_deletion";
                entry["savePointId"] = savePointId ?? "";
                entry["nativeSaveName"] = nativeSaveName ?? "";
                entry["requestedUtc"] = DateTime.UtcNow.ToString(
                    "o", CultureInfo.InvariantCulture);
                entry["reason"] = "confirmed_final_native_save_deleted";
                ledger["pending"] = pending;
                WriteCampaignRetirementLedger(ledger);
                LogOperational("campaign.retirement_pending", entry);
                return entry;
            }
        }

        private static bool CancelPendingCampaignRetirement(
            string campaignId,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(campaignId)) return false;
            lock (CampaignRetirementLedgerLock)
            {
                Dictionary<string, object> ledger = ReadCampaignRetirementLedger();
                List<Dictionary<string, object>> pending =
                    ReadDictionaryList(ledger, "pending");
                int removed = pending.RemoveAll(row =>
                    ReadString(row, "campaignId", "").Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return false;
                ledger["pending"] = pending;
                WriteCampaignRetirementLedger(ledger);
                LogOperational("campaign.retirement_cancelled", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["reason"] = reason ?? ""
                });
                return true;
            }
        }

        private static bool IsCampaignRetirementPending(string campaignId)
        {
            return ReadDictionaryList(ReadCampaignRetirementLedger(), "pending")
                .Any(row => ReadString(row, "campaignId", "").Equals(
                    campaignId ?? "", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> CampaignUnloadedApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            CampaignDataGate.EnterWriteLock();
            try
            {
                if (!IsCampaignRetirementPending(campaignId))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "no_pending_retirement",
                        ["campaignId"] = campaignId,
                        ["campaignDeleted"] = false,
                        ["campaignDeletionPending"] = false,
                        ["noOp"] = true
                    };
                int remaining = ReadDictionaryList(
                    ReadSaveSyncLedger(campaignId), "points").Count;
                if (remaining > 0)
                {
                    CancelPendingCampaignRetirement(
                        campaignId, "confirmed_save_exists_at_unload");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "retirement_cancelled",
                        ["campaignId"] = campaignId,
                        ["remainingSavePoints"] = remaining,
                        ["campaignDeleted"] = false,
                        ["campaignDeletionPending"] = false
                    };
                }
                Dictionary<string, object> metadata = ReadJsonObject(
                    Path.Combine(StrictCampaignDirectory(campaignId), "campaign.json"));
                return DeleteCampaignOwnedDataCore(
                    campaignId,
                    ReadString(metadata, "campaignLabel",
                        ReadString(metadata, "mainHeroName", campaignId)),
                    true,
                    "active_campaign_unloaded_after_final_save_deletion");
            }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static void ProcessPendingCampaignRetirementsAtStartup()
        {
            CleanupCampaignRetirementStagingAtStartup();
            if (!string.IsNullOrWhiteSpace(CampaignMutationBlockReason())) return;
            ProcessPendingCampaignRetirements(false);
        }

        private static void StartCampaignRetirementMonitor()
        {
            Task.Run(async () =>
            {
                while (!ShutdownRequested)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (ShutdownRequested || !string.IsNullOrWhiteSpace(
                        CampaignMutationBlockReason())) continue;
                    if (ReadDictionaryList(
                        ReadCampaignRetirementLedger(), "pending").Count == 0) continue;
                    ProcessPendingCampaignRetirements(false);
                }
            });
        }

        private static void ProcessPendingCampaignRetirements(
            bool allowBannerlordRunning)
        {
            if (!allowBannerlordRunning
                && !string.IsNullOrWhiteSpace(CampaignMutationBlockReason())) return;
            CampaignDataGate.EnterWriteLock();
            try
            {
                List<Dictionary<string, object>> pending = ReadDictionaryList(
                    ReadCampaignRetirementLedger(), "pending").ToList();
                foreach (Dictionary<string, object> entry in pending)
                {
                    string campaignId = ReadString(entry, "campaignId", "");
                    try
                    {
                        ValidateSaveSyncId(campaignId, "campaignId");
                        int remaining = ReadDictionaryList(
                            ReadSaveSyncLedger(campaignId), "points").Count;
                        if (remaining > 0)
                        {
                            CancelPendingCampaignRetirement(
                                campaignId, "confirmed_save_created_before_retirement");
                            continue;
                        }
                        Dictionary<string, object> metadata = ReadJsonObject(
                            Path.Combine(StrictCampaignDirectory(campaignId), "campaign.json"));
                        DeleteCampaignOwnedDataCore(
                            campaignId,
                            ReadString(metadata, "campaignLabel",
                                ReadString(metadata, "mainHeroName", campaignId)),
                            allowBannerlordRunning,
                            "pending_final_save_deletion_recovery");
                    }
                    catch (Exception ex)
                    {
                        LogOperational("campaign.retirement_retry_failed", new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["error"] = ex.Message
                        });
                    }
                }
            }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static void MarkCampaignSaveSyncLifecycle(
            string campaignId,
            string lifecycle,
            bool everRegistered)
        {
            string directory = StrictCampaignDirectory(campaignId);
            string path = Path.Combine(directory, "campaign.json");
            Dictionary<string, object> metadata = ReadJsonObject(path);
            metadata["campaignId"] = campaignId;
            metadata["saveSyncLifecycle"] = lifecycle ?? "";
            metadata["everRegisteredSaveSyncPoint"] = everRegistered;
            metadata["updatedUtc"] = DateTime.UtcNow.ToString(
                "o", CultureInfo.InvariantCulture);
            WriteJsonObject(path, metadata);
        }

        private static Dictionary<string, object> EmptyCampaignDeletionCounts()
        {
            return new Dictionary<string, object>
            {
                ["postgresql"] = new Dictionary<string, object>
                    { ["schemas"] = 0, ["snapshots"] = 0 },
                ["campaignFiles"] = new Dictionary<string, object>
                    { ["roots"] = 0, ["files"] = 0, ["bytes"] = 0L },
                ["saveSync"] = new Dictionary<string, object>
                    { ["roots"] = 0, ["files"] = 0, ["bytes"] = 0L },
                ["portraitCache"] = new Dictionary<string, object>
                    { ["roots"] = 0, ["files"] = 0, ["bytes"] = 0L },
                ["semanticVectors"] = new Dictionary<string, object>
                    { ["records"] = 0 }
            };
        }

        private static Dictionary<string, object> CampaignDeletionCount(string path)
        {
            return new Dictionary<string, object>
            {
                ["roots"] = Directory.Exists(path) ? 1 : 0,
                ["files"] = SafeFileCount(path, "*"),
                ["bytes"] = SafeDirectorySize(path)
            };
        }

        private static int DeleteCampaignSemanticVectors(string campaignId)
        {
            if (SaveSyncSkipSemanticForTests) return 0;
            List<string> ids;
            using (ReignDbConnection connection =
                ReignPostgreSqlStorage.OpenCampaignConnection(campaignId))
            {
                bool hasEmbeddingDocuments = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_name='embedding_documents';").FirstOrDefault(), "count", 0) > 0;
                ids = hasEmbeddingDocuments
                    ? QuerySql(connection,
                            "SELECT vector_id FROM embedding_documents WHERE vector_id<>'' AND status<>'deleted';")
                        .Select(row => ReadString(row, "vector_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
            }
            if (ids.Count == 0) return 0;
            Dictionary<string, object> settings = LoadSettings();
            StartManagedSemanticWorker(settings);
            for (int offset = 0; offset < ids.Count;
                offset += CampaignSemanticDeleteBatchSize)
            {
                Dictionary<string, object> request = SemanticProviderRequest(settings);
                request["collection"] = SemanticCollection;
                request["ids"] = ids.Skip(offset)
                    .Take(CampaignSemanticDeleteBatchSize).ToList();
                Dictionary<string, object> response = TryParseJsonObject(
                    PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/delete"),
                        Json.Serialize(request), CampaignSemanticDeleteTimeoutMs))
                    ?? new Dictionary<string, object>();
                if (!ReadBool(response, "ok", false))
                    throw new IOException(ReadString(response, "error",
                        "Campaign semantic vector deletion failed."));
            }
            return ids.Count;
        }

        private static void ValidateCampaignOwnedDirectory(
            string path,
            string root,
            bool rejectShared)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
                throw new InvalidDataException("Campaign storage root is unavailable.");
            string fullRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(fullPath) ?? "";
            if (!parent.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Campaign deletion target escaped its owned storage root.");
            if (rejectShared && Path.GetFileName(fullPath).Equals(
                "_shared", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The shared portrait library cannot be staged for campaign deletion.");
            if (Directory.Exists(fullPath)
                && (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Campaign deletion refused a reparse-point storage root: " + fullPath);
        }

        private static void VerifyStagedCampaignDirectory(
            string source,
            string staged)
        {
            if (Directory.Exists(source) || !Directory.Exists(staged))
                throw new IOException(
                    "Campaign deletion staging could not be verified for " + source + ".");
        }

        private static void RestoreStagedCampaignDirectory(
            string staged,
            string destination,
            bool wasMoved,
            List<string> errors)
        {
            if (!wasMoved || !Directory.Exists(staged)) return;
            try
            {
                if (Directory.Exists(destination))
                    throw new IOException("Rollback destination already exists: " + destination);
                MoveDirectoryWithRetries(staged, destination);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        private static void DeleteCampaignStagingRoot(
            string path,
            List<string> residuals)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                ValidateCampaignRetirementStagingRoot(path);
                DeleteDirectoryCompletely(path);
            }
            catch
            {
                residuals.Add(path);
            }
        }

        private static void WriteCampaignRetirementTransaction(
            string stagingRoot,
            string campaignId,
            string state)
        {
            WriteJsonObject(Path.Combine(stagingRoot, "transaction.json"),
                new Dictionary<string, object>
                {
                    ["version"] = CampaignRetirementVersion,
                    ["campaignId"] = campaignId,
                    ["state"] = state,
                    ["updatedUtc"] = DateTime.UtcNow.ToString(
                        "o", CultureInfo.InvariantCulture)
                });
        }

        private static void ValidateCampaignRetirementStagingRoot(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool owned = CampaignRetirementStagingRoots().Any(root =>
                (Path.GetDirectoryName(full) ?? "").Equals(
                    Path.GetFullPath(root).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
            if (!owned) throw new InvalidDataException(
                "Campaign retirement staging path escaped its owned root.");
            if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Campaign retirement cleanup refused a reparse point.");
        }

        private static List<string> CampaignRetirementStagingRoots()
        {
            string portraitRoot = CampaignPortraitCacheRoot();
            string portraitParent = string.IsNullOrWhiteSpace(portraitRoot)
                ? DataDir
                : Directory.GetParent(Path.GetFullPath(portraitRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))?.FullName ?? DataDir;
            return new[]
            {
                Path.Combine(DataDir, "campaign-retirement-staging"),
                Path.Combine(portraitParent, ".reign-campaign-retirement-staging")
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void CleanupCampaignRetirementStagingAtStartup()
        {
            foreach (string root in CampaignRetirementStagingRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (string child in Directory.GetDirectories(
                    root, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        ValidateCampaignRetirementStagingRoot(child);
                        Dictionary<string, object> transaction = ReadJsonObject(
                            Path.Combine(child, "transaction.json"));
                        string campaignId = ReadString(
                            transaction, "campaignId", "");
                        if (string.IsNullOrWhiteSpace(campaignId))
                            campaignId = CampaignIdFromRetirementStagingName(child);
                        ValidateSaveSyncId(campaignId, "campaignId");
                        bool databaseExists = ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                            ReadString(row, "campaignId", "").Equals(
                                campaignId, StringComparison.OrdinalIgnoreCase));
                        if (databaseExists)
                        {
                            RestoreRetirementTransactionRoot(child, campaignId);
                        }
                        else
                        {
                            DeleteDirectoryCompletely(child);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogOperational("campaign.retirement_staging_cleanup_failed",
                            new Dictionary<string, object>
                            {
                                ["path"] = child,
                                ["error"] = ex.Message
                            });
                    }
                }
            }
        }

        private static string CampaignIdFromRetirementStagingName(string path)
        {
            string name = Path.GetFileName(Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            const int operationTokenLength = 32;
            int separator = name.Length - operationTokenLength - 1;
            if (separator <= 0 || name[separator] != '_')
                throw new InvalidDataException(
                    "Campaign retirement staging receipt is missing or invalid.");
            string operationToken = name.Substring(separator + 1);
            if (operationToken.Length != operationTokenLength
                || operationToken.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException(
                    "Campaign retirement staging receipt is missing or invalid.");
            string campaignId = name.Substring(0, separator);
            ValidateSaveSyncId(campaignId, "campaignId");
            HashSet<string> allowedEntries = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "campaign", "save-sync", "portrait-cache", "transaction.json"
            };
            if (Directory.GetFileSystemEntries(path).Any(entry =>
                !allowedEntries.Contains(Path.GetFileName(entry))))
                throw new InvalidDataException(
                    "Campaign retirement staging receipt is missing and its contents are not recognized.");
            return campaignId;
        }

        private static void RestoreRetirementTransactionRoot(
            string stagingRoot,
            string campaignId)
        {
            Dictionary<string, string> candidates = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(stagingRoot, "campaign")] =
                    StrictCampaignDirectory(campaignId),
                [Path.Combine(stagingRoot, "save-sync")] =
                    SaveSyncCampaignRoot(campaignId),
                [Path.Combine(stagingRoot, "portrait-cache")] =
                    CampaignPortraitCacheDirectory(campaignId)
            };
            foreach (KeyValuePair<string, string> candidate in candidates)
            {
                if (!Directory.Exists(candidate.Key)) continue;
                if (Directory.Exists(candidate.Value))
                    throw new IOException(
                        "Campaign retirement recovery found both staged and active roots for "
                        + campaignId + ". Manual review is required.");
                if (candidate.Key.EndsWith("portrait-cache",
                    StringComparison.OrdinalIgnoreCase))
                    ValidateCampaignOwnedDirectory(candidate.Value,
                        CampaignPortraitCacheRoot(), true);
                else if (candidate.Key.EndsWith("save-sync",
                    StringComparison.OrdinalIgnoreCase))
                    ValidateCampaignOwnedDirectory(candidate.Value,
                        SaveSyncRoot(), false);
                else
                    ValidateCampaignOwnedDirectory(candidate.Value,
                        CampaignsRoot(), false);
                MoveDirectoryWithRetries(candidate.Key, candidate.Value);
            }
            DeleteDirectoryCompletely(stagingRoot);
            LogOperational("campaign.retirement_staging_recovered",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["stagingRoot"] = stagingRoot
                });
        }

        private static List<Dictionary<string, object>> EnumerateOrphanPortraitRoots(
            HashSet<string> campaignIds)
        {
            string root = CampaignPortraitCacheRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return new List<Dictionary<string, object>>();
            return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals(
                    "_shared", StringComparison.OrdinalIgnoreCase))
                .Where(path => !campaignIds.Contains(Path.GetFileName(path)))
                .Select(path => OrphanStorageRoot("orphan_portrait_root", path))
                .ToList();
        }

        private static List<Dictionary<string, object>> EnumerateOrphanCampaignRoots(
            HashSet<string> campaignIds)
        {
            string root = CampaignsRoot();
            if (!Directory.Exists(root))
                return new List<Dictionary<string, object>>();
            return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                .Select(path => new
                {
                    Path = path,
                    Id = ReadString(ReadJsonObject(Path.Combine(path, "campaign.json")),
                        "campaignId", Path.GetFileName(path))
                })
                .Where(item => !campaignIds.Contains(item.Id))
                .Select(item => OrphanStorageRoot("orphan_campaign_root", item.Path))
                .ToList();
        }

        private static List<Dictionary<string, object>> EnumerateOrphanSaveSyncRoots(
            HashSet<string> campaignIds)
        {
            string root = SaveSyncRoot();
            if (!Directory.Exists(root))
                return new List<Dictionary<string, object>>();
            return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals(
                    "t", StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Path = path,
                    Id = ReadString(ReadJsonObject(Path.Combine(path, "ledger.json")),
                        "campaignId", "")
                })
                .Where(item => string.IsNullOrWhiteSpace(item.Id)
                    || !campaignIds.Contains(item.Id))
                .Select(item => OrphanStorageRoot("orphan_save_sync_root", item.Path))
                .ToList();
        }

        private static Dictionary<string, object> OrphanStorageRoot(
            string category,
            string path)
        {
            bool reparse = Directory.Exists(path)
                && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
            return new Dictionary<string, object>
            {
                ["category"] = category,
                ["name"] = Path.GetFileName(path),
                ["path"] = path,
                ["reparsePoint"] = reparse,
                ["fileCount"] = reparse ? 0 : SafeFileCount(path, "*"),
                ["bytes"] = reparse ? 0L : SafeDirectorySize(path),
                ["eligibleForPermanentDeletion"] = !reparse
            };
        }

        private static string CampaignMutationBlockReason()
        {
            if (CampaignMutationAllowDuringTests) return "";
            try
            {
                bool running = Process.GetProcesses().Any(x => x.ProcessName.IndexOf("Bannerlord", StringComparison.OrdinalIgnoreCase) >= 0);
                return running ? "Close Bannerlord before importing or deleting campaign data." : "";
            }
            catch { return ""; }
        }

        private static void CopyDirectoryTree(string source, string destination, Func<string, bool> include)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, RelativeBackupPath(source, directory).Replace('/', Path.DirectorySeparatorChar)));
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (include != null && !include(file)) continue;
                string target = Path.Combine(destination, RelativeBackupPath(source, file).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static bool IsLiveSqliteSidecar(string path)
        {
            string name = Path.GetFileName(path);
            return name.Equals("world_memory.sqlite", StringComparison.OrdinalIgnoreCase) || name.Equals("world_memory.sqlite-wal", StringComparison.OrdinalIgnoreCase) || name.Equals("world_memory.sqlite-shm", StringComparison.OrdinalIgnoreCase);
        }

        private static string RelativeBackupPath(string root, string path)
        {
            Uri rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('\\', '/');
        }

        private static string SafeBackupPath(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative ?? ""));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !full.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive contains an unsafe path.");
            return full;
        }

        private static string FileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static long SafeDirectorySize(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? 0 : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length); }
            catch { return 0; }
        }

        private static int SafeDirectoryCount(string path) { try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0; } catch { return 0; } }
        private static int SafeFileCount(string path, string pattern) { try { return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? 0 : Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories).Count(); } catch { return 0; } }
        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            ReignPostgreSqlStorage.ClearAllPools();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
                catch { if (attempt < 3) Thread.Sleep(100 * (attempt + 1)); }
            }
        }
        private static void MoveDirectoryWithRetries(string source, string destination)
        {
            ReignPostgreSqlStorage.ClearAllPools();
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Move(source, destination); return; }
                catch (Exception ex) { last = ex; if (attempt < 4) Thread.Sleep(120 * (attempt + 1)); }
            }
            throw last ?? new IOException("Directory move failed.");
        }
        private static void MoveFileWithRetries(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { File.Move(source, destination); return; }
                catch (Exception ex) { last = ex; if (attempt < 4) Thread.Sleep(120 * (attempt + 1)); }
            }
            throw last ?? new IOException("File move failed.");
        }
        private static void DeleteDirectoryCompletely(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            ReignPostgreSqlStorage.ClearAllPools();
            Exception last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    DeleteDirectoryTreePass(path);
                    if (!Directory.Exists(path)) return;
                }
                catch (Exception ex) { last = ex; }
                if (attempt < 7) Thread.Sleep(200 * (attempt + 1));
            }
            throw new IOException("Reign staged the campaign data but could not finish deleting it. " + (last?.Message ?? ""));
        }

        private static void DeleteDirectoryTreePass(string path)
        {
            Stack<string> pending = new Stack<string>();
            List<string> directories = new List<string>();
            pending.Push(ExtendedLengthPath(path));
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!Directory.Exists(current)) continue;
                directories.Add(current);
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(current); }
                catch (DirectoryNotFoundException) { continue; }
                foreach (string entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    bool reparse = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (directory && !reparse)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    try
                    {
                        File.SetAttributes(entry,
                            attributes & ~FileAttributes.ReadOnly);
                        if (directory) Directory.Delete(entry, false);
                        else File.Delete(entry);
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }

            foreach (string directory in directories
                .OrderByDescending(value => value.Length))
            {
                if (!Directory.Exists(directory)) continue;
                try
                {
                    FileAttributes attributes = File.GetAttributes(directory);
                    File.SetAttributes(directory,
                        attributes & ~FileAttributes.ReadOnly);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                try { Directory.Delete(directory, false); }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static string ExtendedLengthPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (Path.DirectorySeparatorChar != '\\'
                || full.StartsWith("\\\\?\\", StringComparison.Ordinal))
                return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal))
                return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }
        private static void TryDeleteFile(string path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }

        private static void WriteDownloadFile(NetworkStream stream, string path, string contentType, string downloadName)
        {
            if (!File.Exists(path)) { WriteJson(stream, 404, new Dictionary<string, object> { ["ok"] = false, ["error"] = "File not found." }); return; }
            FileInfo info = new FileInfo(path);
            string header = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: " + info.Length.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Disposition: attachment; filename=\"" + (downloadName ?? "campaign.reignbackup").Replace("\"", "") + "\"\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            byte[] buffer = new byte[1024 * 1024];
            using (FileStream input = File.OpenRead(path))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) stream.Write(buffer, 0, read);
            }
        }

        private static List<Dictionary<string, object>> RunCampaignBackupSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = passed, ["suite"] = "campaign_backups", ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0 });
            string campaignId = "cbt_" + Guid.NewGuid().ToString("N");
            string campaignDir = CampaignDirectory(campaignId), portraitDir = CampaignPortraitCacheDirectory(campaignId), archivePath = "";
            string purgeFixture = Path.Combine(TestsDir, "campaign_delete_all_" + Guid.NewGuid().ToString("N"));
            string priorNativeSaveRootOverride =
                CampaignNativeSaveRootOverrideForTests;
            try
            {
                CampaignMutationAllowDuringTests = true;
                Dictionary<string, object> rejected = DeleteAllCampaignBackupData(new Dictionary<string, object> { ["confirmation"] = "DELETE" });
                add("delete_all_requires_exact_confirmation", !ReadBool(rejected, "ok", true), "Delete all requires the exact lowercase word delete.");

                List<string> purgeDirectories = new List<string>
                {
                    Path.Combine(purgeFixture, "campaigns"), Path.Combine(purgeFixture, "backups"),
                    Path.Combine(purgeFixture, "vectors"),
                    Path.Combine(purgeFixture, "save-sync")
                };
                foreach (string directory in purgeDirectories)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "campaign-data.txt"), "delete me", Encoding.UTF8);
                }
                string purgePortraits = Path.Combine(purgeFixture, "portraits");
                string sharedPortraits = Path.Combine(purgePortraits, "_shared");
                string campaignPortraits = Path.Combine(purgePortraits, "campaign_test");
                Directory.CreateDirectory(sharedPortraits);
                Directory.CreateDirectory(campaignPortraits);
                File.WriteAllText(Path.Combine(sharedPortraits, "pregenerated-portrait.png"), "preserve me", Encoding.UTF8);
                File.WriteAllText(Path.Combine(campaignPortraits, "campaign-portrait.png"), "delete me", Encoding.UTF8);
                string purgeQueue = Path.Combine(purgeFixture, "action-queue.json");
                string preservedSettings = Path.Combine(purgeFixture, "settings.json");
                File.WriteAllText(purgeQueue, "[]", Encoding.UTF8);
                File.WriteAllText(preservedSettings, "{\"apiKey\":\"preserved\"}", Encoding.UTF8);
                DeleteAllCampaignStorageCore(purgeDirectories, new List<string> { purgeQueue }, Path.Combine(purgeFixture, ".delete_all_test"));
                DeleteCampaignPortraitDataPreservingShared(purgePortraits);
                bool allTargetsDeleted = purgeDirectories.All(x => Directory.Exists(x) && !Directory.EnumerateFileSystemEntries(x).Any()) && !File.Exists(purgeQueue);
                bool sharedPortraitsPreserved = File.Exists(Path.Combine(sharedPortraits, "pregenerated-portrait.png")) && !Directory.Exists(campaignPortraits);
                add("delete_all_wipes_campaign_storage", allTargetsDeleted && sharedPortraitsPreserved, "Delete all empties campaign, backup, vector, Save Sync, and campaign-portrait storage while preserving the pregenerated shared portrait library.");
                add("delete_all_preserves_shared_portrait_library", sharedPortraitsPreserved, "Delete all never removes the pregenerated _shared portrait seed library.");
                add("delete_all_preserves_global_settings", File.Exists(preservedSettings), "Delete all leaves global settings and API keys outside the campaign purge.");

                Directory.CreateDirectory(Path.Combine(campaignDir, "characters", "hero_test", "memory"));
                File.WriteAllText(Path.Combine(campaignDir, "characters", "hero_test", "memory", "memories.jsonl"), "{\"memory\":\"kept\"}\n", Encoding.UTF8);
                WriteJsonObject(Path.Combine(campaignDir, "campaign.json"), new Dictionary<string, object> { ["campaignId"] = campaignId, ["campaignLabel"] = "Backup Test", ["mainHeroName"] = "Tester" });
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId)) ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('backup_test','preserved');");
                Directory.CreateDirectory(Path.Combine(portraitDir, "hero_test"));
                File.WriteAllBytes(Path.Combine(portraitDir, "hero_test", "custom.png"), new byte[] { 1, 2, 3, 4, 5 });
                string nativeSaveFixture = Path.Combine(
                    purgeFixture, "native-game-saves");
                Directory.CreateDirectory(nativeSaveFixture);
                string nativeSavePath = Path.Combine(
                    nativeSaveFixture, "Diagnostic Campaign.sav");
                byte[] nativeSaveBytes = new byte[] { 9, 7, 5, 3, 1 };
                File.WriteAllBytes(nativeSavePath, nativeSaveBytes);
                CampaignNativeSaveRootOverrideForTests = nativeSaveFixture;
                Dictionary<string, object> ledger =
                    ReadSaveSyncLedger(campaignId);
                ledger["points"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["savePointId"] = "diagnostic_backup_point",
                        ["nativeSaveName"] = "Diagnostic Campaign",
                        ["registeredUtc"] = DateTime.UtcNow.ToString("o"),
                        ["status"] = "ready"
                    }
                };
                WriteSaveSyncLedger(campaignId, ledger);
                Dictionary<string, object> export = CreateCampaignBackupArchive(campaignId);
                archivePath = ReadString(export, "path", "");
                add("export_archive_created", ReadBool(export, "ok", false) && File.Exists(archivePath), "A complete campaign archive is created.");
                if (!ReadBool(export, "ok", false)) throw new InvalidOperationException("Export failed: " + ReadString(export, "error", "unknown error"));
                string uploadCopy = Path.Combine(DataDir, "backups", "importing", Guid.NewGuid().ToString("N") + ".reignbackup");
                Directory.CreateDirectory(Path.GetDirectoryName(uploadCopy));
                File.Copy(archivePath, uploadCopy, true);
                File.Delete(nativeSavePath);
                TryDeleteDirectory(campaignDir);
                TryDeleteDirectory(portraitDir);
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                if (Directory.Exists(campaignDir) || Directory.Exists(portraitDir)) throw new IOException("The disposable source campaign could not be cleared before the import round trip.");
                Dictionary<string, object> imported = ImportCampaignBackup(uploadCopy, new Dictionary<string, string> { ["replace"] = "false" });
                bool importRoundTrip = ReadBool(imported, "ok", false) && File.Exists(Path.Combine(campaignDir, "characters", "hero_test", "memory", "memories.jsonl")) && File.Exists(Path.Combine(portraitDir, "hero_test", "custom.png"))
                    && File.Exists(nativeSavePath)
                    && File.ReadAllBytes(nativeSavePath).SequenceEqual(nativeSaveBytes)
                    && ReadInt(imported, "nativeSavesRestored", 0) == 1;
                add("import_round_trip", importRoundTrip, importRoundTrip ? "Import restores campaign files, the external portrait cache, and the exact native Bannerlord save." : "Import round trip failed: " + Json.Serialize(imported));
                bool postgresPreserved = false;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId)) postgresPreserved = ReadString(QuerySql(connection, "SELECT value FROM schema_meta WHERE key='backup_test';").FirstOrDefault(), "value", "") == "preserved";
                add("postgresql_snapshot_preserved", postgresPreserved, postgresPreserved ? "The consistent PostgreSQL campaign archive survives export and import." : "The imported PostgreSQL schema did not contain the exported backup_test marker. Import result: " + Json.Serialize(imported));
                add("manifest_hashes_present", ReadInt(export, "fileCount", 0) >= 4 && ReadInt(export, "nativeSaveCount", 0) == 1, "The archive manifest covers campaign, database, portrait, Save Sync, and native Bannerlord save files.");

                File.WriteAllBytes(nativeSavePath,
                    new byte[] { 8, 6, 4, 2 });
                string conflictUpload = Path.Combine(DataDir, "backups",
                    "importing", Guid.NewGuid().ToString("N") +
                        ".reignbackup");
                File.Copy(archivePath, conflictUpload, true);
                Dictionary<string, object> conflictImport =
                    ImportCampaignBackup(conflictUpload,
                        new Dictionary<string, string>
                        {
                            ["replace"] = "true",
                            ["replaceNativeSaves"] = "false"
                        });
                add("native_save_conflict_requires_explicit_replace",
                    !ReadBool(conflictImport, "ok", true)
                    && ReadBool(conflictImport,
                        "nativeSaveConflict", false)
                    && File.ReadAllBytes(nativeSavePath)
                        .SequenceEqual(new byte[] { 8, 6, 4, 2 }),
                    "Import never silently overwrites a different native Bannerlord save with the same slot name.");

                string replaceUpload = Path.Combine(DataDir, "backups",
                    "importing", Guid.NewGuid().ToString("N") +
                        ".reignbackup");
                File.Copy(archivePath, replaceUpload, true);
                Dictionary<string, object> replacedImport =
                    ImportCampaignBackup(replaceUpload,
                        new Dictionary<string, string>
                        {
                            ["replace"] = "true",
                            ["replaceNativeSaves"] = "true"
                        });
                bool replacementVerified = ReadBool(replacedImport, "ok", false)
                    && File.ReadAllBytes(nativeSavePath)
                        .SequenceEqual(nativeSaveBytes)
                    && ReadInt(replacedImport,
                        "nativeSavesRestored", 0) == 1;
                add("native_save_conflict_replace_is_verified", replacementVerified,
                    replacementVerified
                        ? "A separately confirmed native-save replacement restores the archive bytes and verifies their hash before completing import."
                        : "Confirmed native-save replacement failed: " + Json.Serialize(replacedImport));
            }
            catch (Exception ex) { add("campaign_backup_exception", false, ex.Message); }
            finally
            {
                CampaignMutationAllowDuringTests = false;
                CampaignNativeSaveRootOverrideForTests =
                    priorNativeSaveRootOverride;
                TryDeleteFile(archivePath);
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                TryDeleteDirectory(campaignDir);
                TryDeleteDirectory(portraitDir);
                TryDeleteDirectory(purgeFixture);
            }
            return results;
        }
    }
}
