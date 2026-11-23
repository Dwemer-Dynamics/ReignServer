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
    internal static partial class PersistenceService
    {
        private const string CampaignBackupFormat = "bannerlord-reign-campaign-backup";
        private const int CampaignBackupVersion = 3;
        private const int CampaignBackupMinimumSupportedVersion = 2;
        private const int CampaignBackupMaxEntries = 250000;
        internal const long CampaignBackupMaxExpandedBytes = 32L * 1024L * 1024L * 1024L;
        private const int CampaignRetirementVersion = 1;
        private const int CampaignSemanticDeleteBatchSize = 200;
        private const int CampaignSemanticDeleteTimeoutMs = 180000;
        private const string ZeroSaveCampaignCleanupConfirmation = "delete Reign campaigns with no saves";
        private static readonly object CampaignBackupMutationLock = new object();
        private static readonly object CampaignRetirementLedgerLock = new object();
        private static bool CampaignMutationAllowDuringTests;
        private static string CampaignRetirementFailureInjectionForTests = "";
        private static string CampaignNativeSaveRootOverrideForTests = "";

        internal static Dictionary<string, object> CampaignListApi()
        {
            List<Dictionary<string, object>> campaigns = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> databaseCampaign
                in ReignPostgreSqlStorage.ListCampaigns())
            {
                string campaignId =
                    ReadString(databaseCampaign, "campaignId", "");
                string blocked = FencedSaveSyncOperation(campaignId);
                if (blocked != null)
                {
                    campaigns.Add(SaveSyncFencedResult(campaignId, blocked));
                    continue;
                }
                string directory = CampaignFileService.CampaignDirectory(campaignId);
                if (!Directory.Exists(directory)) continue;
                DirectoryInfo directoryInfo = new DirectoryInfo(directory);
                if (!CampaignFileService.IsRealCampaignDirectory(directoryInfo)) continue;
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

        internal static Dictionary<string, object> CampaignStorageAuditApi()
        {
            return CampaignStorageAuditCore();
        }

        private static Dictionary<string, object> CampaignStorageAuditCore()
        {
            List<Dictionary<string, object>> databaseCampaigns =
                ReignPostgreSqlStorage.ListCampaigns();
            HashSet<string> databaseIds = new HashSet<string>(
                StringComparer.Ordinal);
            int ignoredDatabaseRecords = 0;
            Dictionary<string, Dictionary<string, object>> pending =
                ReadDictionaryList(ReadCampaignRetirementLedger(), "pending")
                    .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "campaignId", "")))
                    .GroupBy(row => ReadString(row, "campaignId", ""), StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
            List<Dictionary<string, object>> campaigns = new List<Dictionary<string, object>>();
            List<string> fenced = FencedSaveSyncCampaignIds();
            databaseIds.UnionWith(fenced);
            foreach (Dictionary<string, object> databaseCampaign in databaseCampaigns)
            {
                string campaignId = ReadString(databaseCampaign, "campaignId", "");
                if (FencedSaveSyncOperation(campaignId) != null) continue;
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
            campaigns.AddRange(fenced.Select(id => SaveSyncFencedResult(id, FencedSaveSyncOperation(id))));

            List<Dictionary<string, object>> orphanPortraitRoots =
                EnumerateOrphanPortraitRoots(databaseIds);
            List<Dictionary<string, object>> orphanCampaignRoots =
                EnumerateOrphanCampaignRoots(databaseIds);
            // The active directory stays owned by the journal even if a partially
            // restored metadata file identifies another campaign.
            orphanCampaignRoots.RemoveAll(row => FencedSaveSyncOperation(ReadString(row, "name", "")) != null);
            List<Dictionary<string, object>> orphanSaveSyncRoots =
                EnumerateOrphanSaveSyncRoots(databaseIds);
            var protectedRoots = fenced.Select(SaveSyncCampaignRoot).ToHashSet(StringComparer.Ordinal);
            orphanSaveSyncRoots.RemoveAll(row => protectedRoots.Contains(ReadString(row, "path", "")));
            string portraitCacheRoot = CampaignPortraitCacheRoot();
            string installedPortraitCacheRoot = CharactersService.CharacterEditorPortraitCacheRoot();
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

                Dictionary<string, object> metadata = CampaignFileService.ReadJsonObject(Path.Combine(
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
            string saveRoot = ReadString(CampaignFileService.ReadJsonObject(Path.Combine(Program.DataDir, "windows-bridge.json")), "nativeSaveRoot", "");
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
                || CampaignFileService.IsInternalCampaignId(campaignId)) return false;
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

        internal static Dictionary<string, object> CleanupZeroSaveCampaignsApi(
            Dictionary<string, object> payload)
        {
            Program.CampaignDataGate.EnterWriteLock();
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
                        audit, "orphanCampaignRoots"), CampaignFileService.CampaignsRoot(), false,
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
                OperationsService.LogOperational("campaign.zero_save_cleanup", response);
                return response;
            }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
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
                : CampaignFileService.ReadJsonObject(Path.Combine(dir.FullName, "campaign.json"));
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

        internal static void WriteCampaignBackupDownload(NetworkStream stream, Dictionary<string, string> query)
        {
            string campaignId = query != null && query.TryGetValue("campaignId", out string requested) ? requested : "";
            if (string.IsNullOrWhiteSpace(campaignId))
            {
                Program.WriteJson(stream, 400, new Dictionary<string, object> { ["ok"] = false, ["error"] = "campaignId is required." });
                return;
            }
            Dictionary<string, object> archive;
            Program.CampaignDataGate.EnterWriteLock();
            try { archive = CreateCampaignBackupArchive(campaignId); }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
            if (!ReadBool(archive, "ok", false))
            {
                Program.WriteJson(stream, 500, archive);
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
            string campaignDir = CampaignFileService.CampaignDirectory(campaignId);
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
            string backupRoot = Path.Combine(Program.DataDir, "backups");
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

                Dictionary<string, object> meta = CampaignFileService.ReadJsonObject(Path.Combine(campaignDir, "campaign.json"));
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
                CampaignFileService.WriteJsonObject(Path.Combine(stagingRoot, "manifest.json"), manifest);
                string safeLabel = CampaignFileService.SafePathSegment(ReadString(manifest, "campaignLabel", campaignId), campaignId);
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
                OperationsService.LogOperational("campaign.backup_exported", result);
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

        private enum DatabaseImportPhase { Untouched, MutationAttempted, Installed }
        private enum ImportRename { Untouched, Attempted, Completed }

        internal static Dictionary<string, object> ImportCampaignBackup(string uploadPath, Dictionary<string, string> query)
            => ImportCampaignBackup(uploadPath, query, null);

        // The optional callbacks belong to a single isolated self-test invocation.
        private static Dictionary<string, object> ImportCampaignBackup(string uploadPath, Dictionary<string, string> query,
            Action<string, string> beforeStep, Func<string, bool> entryExists = null)
        {
            Program.CampaignDataGate.EnterWriteLock();
            try { return ImportCampaignBackupCore(uploadPath, query, beforeStep, entryExists ?? Path.Exists); }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> ImportCampaignBackupCore(string uploadPath, Dictionary<string, string> query,
            Action<string, string> beforeStep, Func<string, bool> entryExists)
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
            string operationRoot = Path.Combine(Program.DataDir, "backups", "import-recovery", Guid.NewGuid().ToString("N"));
            string extractRoot = Path.Combine(operationRoot, "archive");
            bool retainOperation = false;
            try
            {
                ExtractCampaignBackupSecurely(uploadPath, extractRoot);
                Dictionary<string, object> manifest = CampaignFileService.ReadJsonObject(Path.Combine(extractRoot, "manifest.json"));
                ValidateCampaignBackupManifest(extractRoot, manifest);
                string campaignId = ReadString(manifest, "campaignId", "");
                ValidateSaveSyncId(campaignId, "campaignId");
                string destinationPortraits = CampaignPortraitCacheDirectory(campaignId);
                if (Path.GetFileName(destinationPortraits).Equals("_shared", StringComparison.OrdinalIgnoreCase))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false, ["conflict"] = true, ["campaignId"] = campaignId,
                        ["error"] = "A campaign backup cannot replace the shared portrait library."
                    };
                string sourceCampaign = Path.Combine(extractRoot, "campaign");
                string sourcePortraits = Path.Combine(extractRoot, "portrait_cache");
                string sourceSaveSync = Path.Combine(extractRoot, "save_sync");
                string sourceNativeSaves = Path.Combine(
                    extractRoot, "native_saves");
                string sourceDatabase = SafeBackupPath(extractRoot, ReadString(manifest, "databaseArchive", "postgresql.dump"));
                string destinationCampaign = StrictCampaignDirectory(campaignId);
                string destinationSaveSync = SaveSyncCampaignRoot(campaignId);
                string destinationJournals = SaveSyncJournalDirectory(campaignId);
                bool MatchesCampaign(Dictionary<string, object> row) =>
                    string.Equals(ReadString(row, "campaignId", ""), campaignId, StringComparison.Ordinal);
                var campaigns = ReignPostgreSqlStorage.ListCampaignMetadata();
                var originalMetadata = campaigns.FirstOrDefault(MatchesCampaign);
                string[] otherSpellings = campaigns.Select(row => ReadString(row, "campaignId", ""))
                    .Where(id => id != campaignId && id.Equals(campaignId, StringComparison.OrdinalIgnoreCase)).ToArray();
                void CheckDirectoryOwnership()
                {
                    CheckImportDirectorySpelling(destinationCampaign, otherSpellings, entryExists);
                    CheckImportDirectorySpelling(destinationPortraits, otherSpellings, entryExists);
                    CheckImportDirectorySpelling(destinationJournals, otherSpellings, entryExists);
                }
                CheckDirectoryOwnership();
                bool databaseExists = originalMetadata != null;
                if (databaseExists && !replace)
                    return new Dictionary<string, object> { ["ok"] = false, ["conflict"] = true, ["campaignId"] = campaignId, ["error"] = "This campaign already exists. Enable Replace existing campaign to restore over it." };

                List<string> nativeSaveSources = Directory.Exists(
                        sourceNativeSaves)
                    ? Directory.GetFiles(sourceNativeSaves, "*", SearchOption.TopDirectoryOnly).ToList()
                    : new List<string>();
                string nativeSaveRoot = CampaignNativeSaveRoot();
                var nativeSaveConflicts = new List<string>();
                bool occupiedNativeSlot = false;
                foreach (string source in nativeSaveSources)
                {
                    string name = ValidateNativeSaveFileName(Path.GetFileName(source));
                    string destination = Path.Combine(nativeSaveRoot, name);
                    if (Directory.Exists(destination))
                    {
                        occupiedNativeSlot = true;
                        nativeSaveConflicts.Add(name);
                    }
                    else if (File.Exists(destination) && FileSha256(destination) != FileSha256(source) && !replaceNativeSaves)
                        nativeSaveConflicts.Add(name);
                }
                if (nativeSaveConflicts.Count > 0)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["conflict"] = true,
                        ["nativeSaveConflict"] = true,
                        ["campaignId"] = campaignId,
                        ["nativeSaveConflicts"] = nativeSaveConflicts,
                        ["error"] = occupiedNativeSlot
                            ? "A directory occupies a requested native Bannerlord save path."
                            : "Native Bannerlord save files with the same names already exist. Enable Replace native saves to import this diagnostic bundle without silently overwriting them."
                    };

                var directories = new List<ImportDirectory>
                {
                    new ImportDirectory(sourceCampaign, destinationCampaign, operationRoot),
                    new ImportDirectory(sourcePortraits, destinationPortraits, operationRoot),
                    new ImportDirectory(sourceSaveSync, destinationSaveSync, operationRoot)
                };
                if (!replace && (directories.Any(dir => Path.Exists(dir.Destination))
                    || Path.Exists(destinationJournals)))
                    return new Dictionary<string, object> { ["ok"] = false, ["conflict"] = true, ["campaignId"] = campaignId,
                        ["error"] = "A destination directory already exists. Enable Replace existing campaign to restore over it." };

                var snapshots = ReadDictionaryList(manifest, "saveSyncSnapshots");
                ReignPostgreSqlStorage.VerifyCampaignArchive(campaignId, sourceDatabase, snapshots);
                if (replace && PrepareSaveSyncJournalsForCampaignRemoval(campaignId) is string journalDirectory)
                {
                    directories.Insert(0, new ImportDirectory(null, journalDirectory, operationRoot, "save_sync_journals"));
                    // Journal settlement may prune recovery snapshots from the original registry inventory.
                    originalMetadata = ReignPostgreSqlStorage.ListCampaignMetadata().FirstOrDefault(MatchesCampaign);
                    if (databaseExists != (originalMetadata != null))
                        throw new IOException("Campaign registration changed during backup import preflight.");
                }
                string oldDatabase = Path.Combine(operationRoot, "original.dump");
                string oldDatabaseHash = null;
                var oldSnapshots = new List<Dictionary<string, object>>();
                var databasePhase = DatabaseImportPhase.Untouched;
                var nativeSaves = new List<ImportNativeSave>();
                List<Dictionary<string, object>> nativeSaveResults;
                try
                {
                    if (databaseExists)
                    {
                        beforeStep?.Invoke("list_snapshots", campaignId);
                        oldSnapshots = ReignPostgreSqlStorage.ListSnapshots(campaignId);
                        if (ReadString(originalMetadata, "schemaName", "") != ReignPostgreSqlStorage.CampaignSchemaName(campaignId)
                            || ReadLong(originalMetadata, "snapshotCount", -1) != oldSnapshots.Count)
                            throw new InvalidDataException("The existing PostgreSQL campaign and snapshot registry cannot be fully backed up.");
                        string partial = oldDatabase + ".partial";
                        beforeStep?.Invoke("export_recovery", partial);
                        ReignPostgreSqlStorage.ExportCampaignArchive(campaignId, partial);
                        beforeStep?.Invoke("verify_recovery", partial);
                        ReignPostgreSqlStorage.VerifyCampaignArchive(campaignId, partial, oldSnapshots);
                        oldDatabaseHash = FileSha256(partial);
                        File.Move(partial, oldDatabase);
                    }
                    CampaignFileService.WriteJsonObject(Path.Combine(operationRoot, "inventory.json"), new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["directories"] = directories.Select(dir => dir.Inventory()).ToArray(),
                        ["nativeSaveRoot"] = nativeSaveRoot,
                        ["originalRegisteredCampaignFound"] = databaseExists,
                        ["originalCampaign"] = originalMetadata,
                        ["originalDatabaseSha256"] = oldDatabaseHash,
                        ["originalDatabaseBytes"] = databaseExists ? new FileInfo(oldDatabase).Length : 0,
                        ["originalSnapshots"] = oldSnapshots
                    });
                    foreach (ImportDirectory directory in directories)
                        directory.Install(replace, beforeStep);
                    // Installing the first entry in an empty parent can expose a registered sibling's alias.
                    // The renames are still reversible here and PostgreSQL is untouched.
                    if (otherSpellings.Length > 0) CheckDirectoryOwnership();
                    ReignPostgreSqlStorage.ImportCampaignArchive(campaignId, sourceDatabase, snapshots, databaseExists,
                        () =>
                        {
                            databasePhase = DatabaseImportPhase.MutationAttempted;
                            beforeStep?.Invoke("database_mutation", sourceDatabase);
                        });
                    databasePhase = DatabaseImportPhase.Installed;
                    nativeSaveResults = InstallImportedNativeSaves(nativeSaveSources, nativeSaveRoot, replaceNativeSaves,
                        Path.Combine(operationRoot, "original-native"), nativeSaves);
                    beforeStep?.Invoke("finalize", operationRoot);
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
                }
                catch (Exception failure)
                {
                    retainOperation = true;
                    var errors = new List<string>();
                    void Recover(string resource, Action restore)
                    {
                        try { restore(); }
                        catch (Exception ex) { errors.Add(resource + ": " + ex.Message); }
                    }
                    foreach (ImportNativeSave save in nativeSaves.AsEnumerable().Reverse())
                        Recover(save.Destination, save.Restore);
                    string databaseState = "untouched";
                    if (databasePhase == DatabaseImportPhase.Installed && databaseExists)
                    {
                        databaseState = "recovery_required";
                        Recover("PostgreSQL", () =>
                        {
                            if (oldDatabaseHash == null || FileSha256(oldDatabase) != oldDatabaseHash)
                                throw new InvalidDataException("The verified recovery export has changed.");
                            ReignPostgreSqlStorage.ImportCampaignArchive(campaignId, oldDatabase, oldSnapshots, true);
                            databaseState = "archive_reapplied";
                            // Archive import rebuilds registry metadata and stamps the restored load context.
                            // Keep the original even when pg_restore and that metadata transaction both succeed.
                            errors.Add("The original PostgreSQL archive was reapplied. Its prior registry and load context require reconciliation.");
                        });
                    }
                    else if (databasePhase == DatabaseImportPhase.Installed)
                    {
                        // A missing joined campaign-registry row does not establish that every
                        // snapshot DropCampaign would delete was created by this import.
                        databaseState = "new_install_retained";
                        errors.Add("The installed PostgreSQL campaign was retained for reconciliation.");
                    }
                    else if (databasePhase == DatabaseImportPhase.MutationAttempted)
                    {
                        databaseState = "recovery_required";
                        errors.Add("PostgreSQL import stopped during mutation. Its schema and registry state require inspection.");
                    }
                    foreach (ImportDirectory directory in directories.AsEnumerable().Reverse())
                        Recover(directory.Destination, directory.Restore);
                    if (errors.Count == 0)
                    {
                        retainOperation = false;
                        throw;
                    }
                    var recovery = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["campaignId"] = campaignId,
                        ["error"] = failure.Message + " Backup import requires recovery. Retained operation files are at " + operationRoot + ".",
                        ["recoveryRequired"] = true,
                        ["recoveryPath"] = operationRoot,
                        ["recoveryErrors"] = errors,
                        ["databaseState"] = databaseState,
                        ["directories"] = directories.Select(dir => dir.Inventory()).ToArray(),
                        ["nativeSaves"] = nativeSaves.Select(save => save.Inventory()).ToArray()
                    };
                    Recover("recovery receipt", () =>
                        CampaignFileService.WriteJsonObject(Path.Combine(operationRoot, "recovery-required.json"), recovery));
                    OperationsService.LogOperational("campaign.backup_import_failed", recovery);
                    return recovery;
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
                OperationsService.LogOperational("campaign.backup_imported", result);
                return result;
            }
            catch (ImportDirectoryAliasException ex)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["conflict"] = true, ["campaignId"] = ex.CampaignId, ["error"] = ex.Message };
            }
            catch (Exception ex)
            {
                OperationsService.LogOperational("campaign.backup_import_failed", new Dictionary<string, object> { ["error"] = ex.Message });
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
            }
            finally
            {
                TryDeleteFile(uploadPath);
                if (!retainOperation) TryDeleteDirectory(operationRoot);
            }
        }

        private sealed class ImportDirectoryAliasException : IOException
        {
            internal readonly string CampaignId;

            internal ImportDirectoryAliasException(string destination)
                : base("The import destination shares a filesystem name with a differently spelled campaign: " + destination)
                => CampaignId = Path.GetFileName(destination);
        }

        private static void CheckImportDirectorySpelling(string destination, string[] otherSpellings, Func<string, bool> entryExists)
        {
            if (!entryExists(destination)) return;
            string parent = Path.GetDirectoryName(destination);
            string requested = Path.GetFileName(destination);
            // Enumeration returns stored spellings even when a lookup accepts a case variant.
            var stored = Directory.EnumerateFileSystemEntries(parent).Select(Path.GetFileName)
                .Where(name => requested.Equals(name, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
            if (!stored.Contains(requested)
                || otherSpellings.Any(id => !stored.Contains(id) && entryExists(Path.Combine(parent, id))))
                throw new ImportDirectoryAliasException(destination);
        }

        private sealed class ImportNativeSave
        {
            internal readonly string Destination;
            private readonly string backup, originalHash, incomingHash;
            private ImportRename installation;

            internal ImportNativeSave(string destination, string backup, string originalHash, string incomingHash)
            {
                Destination = destination;
                this.backup = backup;
                this.originalHash = originalHash;
                this.incomingHash = incomingHash;
            }

            internal void Install(string source)
            {
                if (originalHash != null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    CopyVerified(Destination, backup, originalHash, false);
                }
                installation = ImportRename.Attempted;
                CopyVerified(source, Destination, incomingHash, originalHash != null);
                installation = ImportRename.Completed;
                if (FileSha256(Destination) != incomingHash)
                    throw new InvalidDataException("Imported native save verification failed: " + Destination);
            }

            internal void Restore()
            {
                if (installation == ImportRename.Untouched) return;
                bool present = File.Exists(Destination);
                if (Directory.Exists(Destination))
                    throw new IOException("A directory occupies the native save path. Recovery copy retained.");
                if (installation == ImportRename.Attempted)
                {
                    if ((present ? FileSha256(Destination) : null) != originalHash)
                        throw new IOException("Native installation did not complete and the original state cannot be confirmed.");
                    return;
                }
                if (present && FileSha256(Destination) != incomingHash)
                    throw new IOException("Native save changed since installation. Recovery copy retained.");
                if (originalHash != null)
                    CopyVerified(backup, Destination, originalHash, present);
                else if (present)
                    File.Delete(Destination);
                installation = ImportRename.Untouched;
            }

            internal Dictionary<string, object> Inventory() => new Dictionary<string, object>
            {
                ["destination"] = Destination, ["original"] = backup, ["originalSha256"] = originalHash,
                ["incomingSha256"] = incomingHash, ["installation"] = installation.ToString()
            };

            private static void CopyVerified(string source, string destination, string hash, bool overwrite)
            {
                string temporary = Path.Combine(Path.GetDirectoryName(destination), ".ri-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    File.Copy(source, temporary, false);
                    if (FileSha256(temporary) != hash)
                        throw new InvalidDataException("Native save copy failed hash verification: " + source);
                    File.Move(temporary, destination, overwrite);
                }
                finally { TryDeleteFile(temporary); }
            }
        }

        private static List<Dictionary<string, object>> InstallImportedNativeSaves(
            List<string> sources, string destinationRoot, bool replace, string recoveryRoot, List<ImportNativeSave> rollback)
        {
            var results = new List<Dictionary<string, object>>();
            if (sources.Count == 0) return results;
            Directory.CreateDirectory(destinationRoot);
            foreach (string source in sources)
            {
                string fileName = ValidateNativeSaveFileName(Path.GetFileName(source));
                string destination = Path.Combine(destinationRoot, fileName);
                string incomingHash = FileSha256(source);
                string originalHash = File.Exists(destination) ? FileSha256(destination) : null;
                string result = "already_present";
                if (incomingHash != originalHash)
                {
                    if (originalHash != null && !replace)
                        throw new IOException("Native Bannerlord save conflict: " + fileName);
                    var save = new ImportNativeSave(destination, Path.Combine(recoveryRoot, fileName), originalHash, incomingHash);
                    rollback.Add(save);
                    save.Install(source);
                    result = originalHash == null ? "installed" : "replaced";
                }
                results.Add(new Dictionary<string, object>
                {
                    ["fileName"] = fileName, ["result"] = result, ["bytes"] = new FileInfo(destination).Length
                });
            }
            return results;
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
            if (campaignId != campaignId.Trim())
                throw new InvalidDataException("The campaign ID in the backup must exactly match its storage identity.");
            if (!Directory.Exists(Path.Combine(root, "campaign"))) throw new InvalidDataException("The backup does not contain campaign data.");
            string databaseArchive = SafeBackupPath(root, ReadString(manifest, "databaseArchive", "postgresql.dump"));
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

        private sealed class ImportDirectory
        {
            internal readonly string Destination;
            private readonly string source, incoming, original;
            private ImportRename moveAside, installation;

            internal ImportDirectory(string source, string destination, string operationRoot, string stagingName = null)
            {
                this.source = source;
                Destination = destination;
                string name = stagingName ?? Path.GetFileName(source);
                incoming = Path.Combine(operationRoot, "incoming", name);
                original = Path.Combine(operationRoot, "original", name);
            }

            internal void Install(bool replace, Action<string, string> beforeStep)
            {
                bool hasSource = source != null && Directory.Exists(source);
                if (hasSource) CopyDirectoryTree(source, incoming, null);
                if (Directory.Exists(Destination))
                {
                    if (!replace) throw new IOException("Destination campaign already exists: " + Destination);
                    Directory.CreateDirectory(Path.GetDirectoryName(original));
                    moveAside = ImportRename.Attempted;
                    beforeStep?.Invoke("move_original", Destination);
                    MoveDirectoryWithRetries(Destination, original);
                    moveAside = ImportRename.Completed;
                }
                // Missing optional directories in a replacement intentionally retire the old tree.
                if (!hasSource) return;
                Directory.CreateDirectory(Path.GetDirectoryName(Destination));
                installation = ImportRename.Attempted;
                beforeStep?.Invoke("install_directory", Destination);
                MoveDirectoryWithRetries(incoming, Destination);
                installation = ImportRename.Completed;
            }

            internal void Restore()
            {
                // If a rename failed and its source is gone, ownership is uncertain.
                // Preserve both paths for inspection instead of issuing another rename.
                if (moveAside == ImportRename.Attempted && (!Directory.Exists(Destination) || Directory.Exists(original)))
                    throw new IOException("The original directory move has an unconfirmed outcome.");
                if (installation == ImportRename.Attempted && !Directory.Exists(incoming))
                    throw new IOException("The incoming directory move has an unconfirmed outcome.");
                if (installation == ImportRename.Completed)
                {
                    MoveDirectoryWithRetries(Destination, incoming);
                    installation = ImportRename.Untouched;
                }
                if (moveAside == ImportRename.Completed)
                {
                    MoveDirectoryWithRetries(original, Destination);
                    moveAside = ImportRename.Untouched;
                }
            }

            internal Dictionary<string, object> Inventory() => new Dictionary<string, object>
            {
                ["destination"] = Destination, ["incoming"] = incoming, ["original"] = original,
                ["moveAside"] = moveAside.ToString(), ["installation"] = installation.ToString()
            };
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

        internal static string CampaignPortraitCacheDirectory(string campaignId)
        {
            string root = CampaignPortraitCacheRoot();
            return string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, CampaignFileService.SafePathSegment(campaignId, "unknown"));
        }

        private static string CampaignPortraitCacheRoot()
        {
            return Path.Combine(Program.DataDir, "PortraitCache");
        }
    }
}
