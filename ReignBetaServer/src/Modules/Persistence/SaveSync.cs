using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int SaveSyncFormatVersion = 5;
        private const int SaveSyncRecoveryRetention = 2;
        private const int SaveSyncUniqueStateLimit = 15;
        private const int SaveSyncUniqueStateWarning = 10;
        private static readonly object SaveSyncLedgerLock = new object();
        private static readonly object SaveSyncActiveStateLock = new object();
        private static readonly ReaderWriterLockSlim SaveSyncStorageGate =
            new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
        private static readonly SemaphoreSlim SaveSyncOptimizationSemaphore = new SemaphoreSlim(1, 1);
        private static readonly HashSet<string> SaveSyncOptimizationQueued =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> SaveSyncSemanticBlockedCampaigns =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, SaveSyncActiveState> SaveSyncActiveStates =
            new Dictionary<string, SaveSyncActiveState>(StringComparer.OrdinalIgnoreCase);
        private static string SaveSyncFailureInjectionForTests = "";
        private static bool? SaveSyncEnabledOverrideForTests;
        private static bool SaveSyncSkipSemanticForTests;
        private static bool SaveSyncDisableBackgroundOptimizationForTests;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLink(string newFileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool MoveFileEx(
            string existingFileName,
            string newFileName,
            uint flags);

        private sealed class SaveSyncActiveState
        {
            internal string SavePointId = "";
            internal string CalendarSha256 = "";
            internal bool Dirty = true;
            internal bool Loaded;
        }

        private static readonly HashSet<string> SaveSyncRetainedCharacterFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "appearance.json", "background.json", "characteristics.json", "constructed.json", "enrichment.json",
            "profile_library.json", "save_quirks.json", "schema_manifest.json", "template.json", "traits.json", "voice.json"
        };

        private static Dictionary<string, object> SaveSyncRegisterApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsSaveSyncEnabled()) return SaveSyncDisabledResult(payload);
            Stopwatch requestTimer = Stopwatch.StartNew();
            CampaignDataGate.EnterWriteLock();
            long gateWaitMs = requestTimer.ElapsedMilliseconds;
            try
            {
                var result = SaveSyncRegisterCore(payload);
                result["gateWaitMs"] = gateWaitMs;
                result["requestDurationMs"] = requestTimer.ElapsedMilliseconds;
                LogOperational("save_sync.register_completed", new Dictionary<string, object>
                {
                    ["campaignId"] = ReadString(payload, "campaignId", ""),
                    ["gateWaitMs"] = gateWaitMs, ["requestDurationMs"] = requestTimer.ElapsedMilliseconds,
                    ["result"] = ReadString(result, "result", "")
                });
                return result;
            }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> SaveSyncRegisterCore(Dictionary<string, object> payload)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "");
            string savePointId = ReadString(payload, "savePointId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            ValidateSaveSyncId(savePointId, "savePointId");
            if (payload.ContainsKey("worldHistoryOutboxDrained")
                && !ReadBool(payload, "worldHistoryOutboxDrained", false))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["result"] = "critical_history_pending",
                    ["campaignId"] = campaignId,
                    ["savePointId"] = savePointId,
                    ["error"] = "Save Sync did not capture an inconsistent state because critical world-history events were still pending."
                };
            string campaignDir = StrictCampaignDirectory(campaignId);
            Directory.CreateDirectory(campaignDir);
            string campaignMetadataPath = Path.Combine(campaignDir, "campaign.json");
            if (!File.Exists(campaignMetadataPath))
            {
                WriteJsonObject(campaignMetadataPath, new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["campaignLabel"] = ReadString(payload, "campaignLabel", campaignId),
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                });
            }

            Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
            Dictionary<string, object> existing = SaveSyncPoint(ledger, savePointId);
            string pointRoot = existing == null ? "" : SaveSyncSnapshotRoot(campaignId, existing);
            if (existing != null
                && ReignPostgreSqlStorage.SnapshotExists(campaignId,
                    SaveSyncPostgreSqlSnapshotPointId(existing))
                && File.Exists(Path.Combine(pointRoot, "manifest.json"))
                && Directory.Exists(Path.Combine(pointRoot, "campaign")))
            {
                Dictionary<string, object> existingManifest = ReadJsonObject(Path.Combine(pointRoot, "manifest.json"));
                if (!ReadString(existingManifest, "campaignId", "").Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                    || !ReadString(existingManifest, "fingerprint", "").Equals(ReadString(existing, "snapshotFingerprint", ""), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Save Sync storage-key collision detected; no snapshot was changed.");
                timer.Stop();
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["result"] = "already_registered", ["noOp"] = true,
                    ["campaignId"] = campaignId, ["savePointId"] = savePointId,
                    ["durationMs"] = timer.ElapsedMilliseconds, ["point"] = PublicSaveSyncPoint(existing)
                };
            }

            Dictionary<string, object> point = NormalizeSaveSyncPoint(payload);
            Dictionary<string, object> cleanSharedPoint =
                SaveSyncCleanActivePoint(campaignId, ledger);
            if (cleanSharedPoint != null)
            {
                point["postgresqlSnapshotPointId"] =
                    SaveSyncPostgreSqlSnapshotPointId(cleanSharedPoint);
                point["snapshotStorageId"] = ReadString(cleanSharedPoint, "snapshotStorageId", "");
                point["snapshotFingerprint"] = ReadString(cleanSharedPoint, "snapshotFingerprint", "");
                point["snapshotBytes"] = ReadLong(cleanSharedPoint, "snapshotBytes", 0);
                point["snapshotFiles"] = ReadInt(cleanSharedPoint, "snapshotFiles", 0);
                point["snapshotState"] = ReadString(cleanSharedPoint, "snapshotState", "optimized");
                point["registeredUtc"] = DateTime.UtcNow.ToString("o");
                point["status"] = "registered";
                UpsertSaveSyncPoint(ledger, point);
                ledger["lastRegisteredPointId"] = savePointId;
                ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteSaveSyncLedger(campaignId, ledger);
                MarkSaveSyncActiveStateClean(campaignId, point);
                timer.Stop();
                int sharedUniqueStates = SaveSyncUniqueStateCount(ledger);
                Dictionary<string, object> sharedResult = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["result"] = "registered_shared_generation",
                    ["noOp"] = true,
                    ["campaignId"] = campaignId,
                    ["savePointId"] = savePointId,
                    ["snapshotFingerprint"] = ReadString(point, "snapshotFingerprint", ""),
                    ["snapshotBytes"] = ReadLong(point, "snapshotBytes", 0),
                    ["snapshotFiles"] = ReadInt(point, "snapshotFiles", 0),
                    ["snapshotState"] = ReadString(point, "snapshotState", ""),
                    ["uniqueStateCount"] = sharedUniqueStates,
                    ["uniqueStateLimit"] = SaveSyncUniqueStateLimit,
                    ["remainingUniqueStates"] = Math.Max(0, SaveSyncUniqueStateLimit - sharedUniqueStates),
                    ["durationMs"] = timer.ElapsedMilliseconds
                };
                AppendSaveSyncAudit(campaignId, "save_point.registered_shared_generation", sharedResult);
                return sharedResult;
            }
            int uniqueStates = SaveSyncUniqueStateCount(ledger);
            int effectiveUniqueStates = SaveSyncEffectiveUniqueStateCountForRegistration(ledger, point);
            if (effectiveUniqueStates >= SaveSyncUniqueStateLimit)
            {
                Dictionary<string, object> limited = new Dictionary<string, object>
                {
                    ["ok"] = false, ["result"] = "snapshot_limit_reached",
                    ["campaignId"] = campaignId, ["savePointId"] = savePointId,
                    ["uniqueStateCount"] = uniqueStates, ["uniqueStateLimit"] = SaveSyncUniqueStateLimit,
                    ["remainingUniqueStates"] = 0,
                    ["error"] = "Save Sync already retains 15 unique states. Delete an old native Bannerlord save to remove its matching Reign snapshot, then save again."
                };
                AppendSaveSyncAudit(campaignId, "save_point.limit_reached", limited);
                return limited;
            }

            string storageId = Guid.NewGuid().ToString("N").Substring(0, 12);
            string contentRoot = SaveSyncContentRoot(campaignId, storageId);
            Dictionary<string, object> manifest;
            try
            {
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaignId, savePointId);
                point["postgresqlSnapshotPointId"] = savePointId;
                manifest = CreateRawSaveSyncSnapshot(campaignId, contentRoot, point);
            }
            catch
            {
                try { ReignPostgreSqlStorage.DropSnapshot(campaignId, savePointId); } catch { }
                TryDeleteDirectory(contentRoot);
                throw;
            }
            string fingerprint = ReadString(manifest, "fingerprint", "");
            point["snapshotStorageId"] = storageId;
            point["snapshotFingerprint"] = fingerprint;
            point["snapshotBytes"] = ReadLong(manifest, "expandedBytes", 0);
            point["snapshotFiles"] = ReadInt(manifest, "fileCount", 0);
            point["registeredUtc"] = DateTime.UtcNow.ToString("o");
            point["snapshotState"] = "postgresql_ready";
            point["status"] = "registered";
            UpsertSaveSyncPoint(ledger, point);
            ledger["lastRegisteredPointId"] = savePointId;
            ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
            WriteSaveSyncLedger(campaignId, ledger);
            MarkSaveSyncActiveStateClean(campaignId, point);
            timer.Stop();
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true, ["result"] = "registered", ["noOp"] = false,
                ["campaignId"] = campaignId, ["savePointId"] = savePointId,
                ["snapshotFingerprint"] = ReadString(manifest, "fingerprint", ""),
                ["snapshotBytes"] = ReadLong(manifest, "expandedBytes", 0),
                ["snapshotFiles"] = ReadInt(manifest, "fileCount", 0),
                // An overwrite may temporarily coexist with the superseded
                // snapshot until Bannerlord confirms the native save. Report the
                // post-finalization capacity; failure discards the new point and
                // retains the old one.
                ["uniqueStateCount"] = effectiveUniqueStates + 1,
                ["uniqueStateLimit"] = SaveSyncUniqueStateLimit,
                ["remainingUniqueStates"] = Math.Max(
                    0,
                    SaveSyncUniqueStateLimit - (effectiveUniqueStates + 1)),
                ["inventory"] = ReadDictionary(manifest, "inventory") ?? new Dictionary<string, object>(),
                ["snapshotState"] = "postgresql_ready",
                ["optimizationQueued"] = false,
                ["durationMs"] = timer.ElapsedMilliseconds
            };
            AppendSaveSyncAudit(campaignId, "save_point.registered", result);
            QueueSaveSyncOptimization(campaignId, savePointId);
            return result;
        }

        private static Dictionary<string, object> SaveSyncFinalizeApi(Dictionary<string, object> payload)
        {
            Stopwatch finalizeTimer = Stopwatch.StartNew();
            payload = payload ?? new Dictionary<string, object>();
            if (!IsSaveSyncEnabled()) return SaveSyncDisabledResult(payload);
            string campaignId = ReadString(payload, "campaignId", "");
            string savePointId = ReadString(payload, "savePointId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            ValidateSaveSyncId(savePointId, "savePointId");
            // A skipped registration has no server state to finalize. Check that
            // cheap, immutable fact before joining the global writer queue. A
            // missing point otherwise lets a long-running campaign reader turn a
            // harmless client cleanup call into writer starvation for every route.
            Dictionary<string, object> preflightLedger = ReadSaveSyncLedger(campaignId);
            if (SaveSyncPoint(preflightLedger, savePointId) == null)
                return SaveSyncMissingFinalizeResult(campaignId, savePointId);
            long preflightMs = finalizeTimer.ElapsedMilliseconds;
            CampaignDataGate.EnterWriteLock();
            long gateWaitMs = finalizeTimer.ElapsedMilliseconds - preflightMs;
            try
            {
                Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
                Dictionary<string, object> point = SaveSyncPoint(ledger, savePointId);
                if (point == null) return SaveSyncMissingFinalizeResult(campaignId, savePointId);
                bool successful = ReadBool(payload, "successful", false);
                point["nativeSaveName"] = LimitText(ReadString(payload, "nativeSaveName", ReadString(point, "nativeSaveName", "")), 240);
                point["saveFinalizedUtc"] = DateTime.UtcNow.ToString("o");
                point["status"] = successful ? "ready" : "save_failed";
                if (!successful)
                {
                    DeleteSaveSyncPointStorageIfUnreferenced(campaignId, ledger, point);
                    RemoveSaveSyncPoint(ledger, savePointId);
                }
                else
                {
                    RemoveSupersededNativeSavePoints(campaignId, ledger, point);
                }
                ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteSaveSyncLedger(campaignId, ledger);
                List<Dictionary<string, object>> supersededCampaigns =
                    new List<Dictionary<string, object>>();
                if (successful)
                {
                    MarkCampaignSaveSyncLifecycle(
                        campaignId, "registered", true);
                    CancelPendingCampaignRetirement(
                        campaignId, "new_confirmed_save_created");
                    supersededCampaigns =
                        RemoveSupersededNativeSavePointsFromOtherCampaigns(
                            campaignId, point);
                }
                int uniqueStates = SaveSyncUniqueStateCount(ledger);
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true, ["result"] = successful ? "finalized" : "discarded_failed_save",
                    ["preflightMs"] = preflightMs, ["gateWaitMs"] = gateWaitMs,
                    ["finalizationMs"] = finalizeTimer.ElapsedMilliseconds - preflightMs - gateWaitMs,
                    ["requestDurationMs"] = finalizeTimer.ElapsedMilliseconds,
                    ["campaignId"] = campaignId, ["savePointId"] = savePointId,
                    ["nativeSaveName"] = ReadString(payload, "nativeSaveName", ""),
                    ["uniqueStateCount"] = uniqueStates,
                    ["uniqueStateLimit"] = SaveSyncUniqueStateLimit,
                    ["warningThreshold"] = SaveSyncUniqueStateWarning,
                    ["remainingUniqueStates"] = Math.Max(0, SaveSyncUniqueStateLimit - uniqueStates),
                    ["storageWarning"] = uniqueStates >= SaveSyncUniqueStateWarning,
                    ["supersededCampaigns"] = supersededCampaigns,
                    ["supersededCampaignsDeleted"] = supersededCampaigns.Count(row =>
                        ReadBool(row, "campaignDeleted", false)),
                    ["supersededSavePointsDeleted"] = supersededCampaigns.Sum(row =>
                        ReadInt(row, "deletedPoints", 0))
                };
                AppendSaveSyncAudit(campaignId, "save_point.finalized", result);
                return result;
            }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> SaveSyncMissingFinalizeResult(
            string campaignId,
            string savePointId)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["result"] = "snapshot_missing",
                ["campaignId"] = campaignId ?? string.Empty,
                ["savePointId"] = savePointId ?? string.Empty,
                ["error"] = "The save point was not registered."
            };
        }

        private static Dictionary<string, object> SaveSyncLoadApi(Dictionary<string, object> payload, bool forcePreview)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsSaveSyncEnabled()) return SaveSyncDisabledResult(payload);
            CampaignDataGate.EnterWriteLock();
            try { return SaveSyncLoadCore(payload, forcePreview || ReadBool(payload, "dryRun", false)); }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> SaveSyncLoadCore(Dictionary<string, object> payload, bool dryRun)
        {
            if (!dryRun) InvalidateCodexConversationTimeline(ReadString(payload, "campaignId", ""));
            return SaveSyncLoadPostgreSql(payload, dryRun);
        }


        private static Dictionary<string, object> SaveSyncStatusApi(Dictionary<string, string> query)
        {
            string campaignId = query != null && query.TryGetValue("campaignId", out string value) ? value : "";
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object> { ["ok"] = true, ["enabled"] = IsSaveSyncEnabled(), ["campaigns"] = SaveSyncCampaignStatuses() };
            ValidateSaveSyncId(campaignId, "campaignId");
            return new Dictionary<string, object> { ["ok"] = true, ["enabled"] = IsSaveSyncEnabled(), ["campaignId"] = campaignId, ["saveSync"] = SaveSyncStatusSummary(campaignId) };
        }

        private static Dictionary<string, object> SaveSyncStatusSummary(string campaignId)
        {
            Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
            List<Dictionary<string, object>> allPoints = ReadDictionaryList(ledger, "points");
            List<Dictionary<string, object>> availablePoints = allPoints
                .Where(point => SaveSyncPointSnapshotAvailable(campaignId, point))
                .ToList();
            List<Dictionary<string, object>> points = allPoints
                .OrderByDescending(x => ReadString(x, "capturedUtc", ReadString(x, "registeredUtc", "")), StringComparer.OrdinalIgnoreCase)
                .Take(50).Select(point => PublicSaveSyncPoint(campaignId, point)).ToList();
            int uniqueStateCount = SaveSyncUniqueStateCount(new Dictionary<string, object>
            {
                ["points"] = availablePoints
            });
            string lastRegisteredPointId = ReadString(ledger, "lastRegisteredPointId", "");
            bool lastRegisteredPointAvailable = availablePoints.Any(point =>
                ReadString(point, "savePointId", "").Equals(
                    lastRegisteredPointId, StringComparison.OrdinalIgnoreCase));
            return new Dictionary<string, object>
            {
                ["enabled"] = IsSaveSyncEnabled(), ["campaignId"] = campaignId,
                ["pointCount"] = availablePoints.Count,
                ["storedPointCount"] = allPoints.Count,
                ["unavailablePointCount"] = allPoints.Count - availablePoints.Count,
                ["points"] = points,
                ["uniqueStateCount"] = uniqueStateCount,
                ["uniqueStateLimit"] = SaveSyncUniqueStateLimit,
                ["warningThreshold"] = SaveSyncUniqueStateWarning,
                ["remainingUniqueStates"] = Math.Max(0, SaveSyncUniqueStateLimit - uniqueStateCount),
                ["storageWarning"] = uniqueStateCount >= SaveSyncUniqueStateWarning,
                ["lastRegisteredPointId"] = lastRegisteredPointId,
                ["lastRegisteredPointAvailable"] = lastRegisteredPointAvailable,
                ["lastLoadedSavePoint"] = PublicSaveSyncPoint(campaignId,
                    ReadDictionary(ledger, "lastLoadedSavePoint")),
                ["lastSyncResult"] = ReadDictionary(ledger, "lastSyncResult") ?? new Dictionary<string, object>(),
                ["lastBackupId"] = ReadString(ledger, "lastBackupId", ""),
                ["pendingFinalSaveDeletion"] = IsCampaignRetirementPending(campaignId),
                ["updatedUtc"] = ReadString(ledger, "updatedUtc", "")
            };
        }

        private static Dictionary<string, object> SaveSyncDeleteNativeSaveApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string requestedCampaignId = ReadString(payload, "campaignId", "");
            string savePointId = ReadString(payload, "savePointId", "");
            string nativeSaveName = ReadString(payload, "nativeSaveName", "");
            if (string.IsNullOrWhiteSpace(savePointId) && string.IsNullOrWhiteSpace(nativeSaveName))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "savePointId or nativeSaveName is required." };

            CampaignDataGate.EnterWriteLock();
            try
            {
                IEnumerable<string> campaignIds = string.IsNullOrWhiteSpace(requestedCampaignId)
                    ? SaveSyncCampaignIds()
                    : new[] { requestedCampaignId };
                int deletedPoints = 0;
                int deletedStates = 0;
                int remainingSavePoints = 0;
                bool campaignDeleted = false;
                bool campaignDeletionPending = false;
                Dictionary<string, object> deletionCounts =
                    EmptyCampaignDeletionCounts();
                List<Dictionary<string, object>> campaignDeletionResults =
                    new List<Dictionary<string, object>>();
                foreach (string campaignId in campaignIds)
                {
                    Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
                    List<Dictionary<string, object>> matches = ReadDictionaryList(ledger, "points")
                        .Where(point =>
                            (!string.IsNullOrWhiteSpace(savePointId)
                                && ReadString(point, "savePointId", "").Equals(savePointId, StringComparison.OrdinalIgnoreCase))
                            || (string.IsNullOrWhiteSpace(savePointId)
                                && !string.IsNullOrWhiteSpace(nativeSaveName)
                                && ReadString(point, "nativeSaveName", "").Equals(nativeSaveName, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                    foreach (Dictionary<string, object> point in matches)
                    {
                        string storageId = ReadString(point, "snapshotStorageId", "");
                        RemoveSaveSyncPoint(ledger, ReadString(point, "savePointId", ""));
                        DeleteSaveSyncPointStorageIfUnreferenced(
                            campaignId, ledger, point);
                        bool stillReferenced = ReadDictionaryList(ledger, "points")
                            .Any(candidate => ReadString(candidate, "snapshotStorageId", "")
                                .Equals(storageId, StringComparison.OrdinalIgnoreCase));
                        if (!stillReferenced) deletedStates++;
                        deletedPoints++;
                    }
                    if (matches.Count > 0)
                    {
                        ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
                        WriteSaveSyncLedger(campaignId, ledger);
                        int campaignRemaining = ReadDictionaryList(
                            ledger, "points").Count;
                        remainingSavePoints += campaignRemaining;
                        AppendSaveSyncAudit(campaignId, "save_point.native_deleted", new Dictionary<string, object>
                        {
                            ["result"] = "deleted", ["savePointId"] = savePointId,
                            ["nativeSaveName"] = nativeSaveName, ["deletedPoints"] = matches.Count,
                            ["remainingSavePoints"] = campaignRemaining
                        });
                        bool exactCampaignMatch = !string.IsNullOrWhiteSpace(
                            requestedCampaignId)
                            && requestedCampaignId.Equals(
                                campaignId, StringComparison.OrdinalIgnoreCase);
                        if (campaignRemaining == 0 && exactCampaignMatch)
                        {
                            bool loaded = payload.ContainsKey("campaignLoaded")
                                ? ReadBool(payload, "campaignLoaded", false)
                                : !string.IsNullOrWhiteSpace(
                                    CampaignMutationBlockReason());
                            if (loaded)
                            {
                                SchedulePendingCampaignRetirement(
                                    campaignId, savePointId, nativeSaveName);
                                campaignDeletionPending = true;
                            }
                            else
                            {
                                Dictionary<string, object> metadata = ReadJsonObject(
                                    Path.Combine(StrictCampaignDirectory(campaignId),
                                        "campaign.json"));
                                Dictionary<string, object> deletion =
                                    DeleteCampaignOwnedDataCore(
                                        campaignId,
                                        ReadString(metadata, "campaignLabel",
                                            ReadString(metadata, "mainHeroName", campaignId)),
                                        true,
                                        "confirmed_final_native_save_deleted");
                                campaignDeletionResults.Add(deletion);
                                campaignDeleted = campaignDeleted
                                    || ReadBool(deletion, "campaignDeleted", false);
                                Dictionary<string, object> returnedCounts =
                                    ReadDictionary(deletion, "countsBySubsystem");
                                if (returnedCounts != null)
                                    deletionCounts = returnedCounts;
                                if (!ReadBool(deletion, "campaignDeleted", false))
                                {
                                    SchedulePendingCampaignRetirement(
                                        campaignId, savePointId, nativeSaveName);
                                    campaignDeletionPending = true;
                                }
                            }
                        }
                    }
                }
                if (deletedPoints == 0
                    && !string.IsNullOrWhiteSpace(requestedCampaignId))
                    remainingSavePoints = ReadDictionaryList(
                        ReadSaveSyncLedger(requestedCampaignId), "points").Count;
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["result"] = campaignDeleted
                        ? "campaign_deleted"
                        : campaignDeletionPending
                            ? "campaign_deletion_pending"
                            : deletedPoints > 0 ? "deleted" : "not_found",
                    ["deletedPoints"] = deletedPoints, ["deletedUniqueStates"] = deletedStates,
                    ["remainingSavePoints"] = remainingSavePoints,
                    ["campaignDeleted"] = campaignDeleted,
                    ["campaignDeletionPending"] = campaignDeletionPending,
                    ["countsBySubsystem"] = deletionCounts,
                    ["campaignDeletionResults"] = campaignDeletionResults,
                    ["savePointId"] = savePointId, ["nativeSaveName"] = nativeSaveName
                };
            }
            finally { CampaignDataGate.ExitWriteLock(); }
        }

        private static List<Dictionary<string, object>> SaveSyncCampaignStatuses()
        {
            return SaveSyncCampaignIds()
                .Select(id => SaveSyncStatusSummary(id)).ToList();
        }

        private static List<string> SaveSyncCampaignIds()
        {
            string root = SaveSyncRoot();
            if (!Directory.Exists(root)) return new List<string>();
            return Directory.GetDirectories(root).Where(path => File.Exists(Path.Combine(path, "ledger.json")))
                .Select(path => ReadString(ReadJsonObject(Path.Combine(path, "ledger.json")), "campaignId", ""))
                .Where(id => HasLocalCampaignMetadata(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void ClearSaveSyncCampaignRuntime(string campaignId)
        {
            lock (SaveSyncActiveStateLock)
                SaveSyncActiveStates.Remove(campaignId ?? "");
            lock (SaveSyncSemanticBlockedCampaigns)
                SaveSyncSemanticBlockedCampaigns.Remove(campaignId ?? "");
            lock (SaveSyncOptimizationQueued)
            {
                string prefix = (campaignId ?? "") + "|";
                SaveSyncOptimizationQueued.RemoveWhere(key =>
                    key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static Dictionary<string, object> CreateRawSaveSyncSnapshot(
            string campaignId,
            string destinationRoot,
            Dictionary<string, object> metadata)
        {
            Stopwatch timer = Stopwatch.StartNew();
            if (Directory.Exists(destinationRoot))
                throw new IOException("Save Sync raw snapshot destination already exists.");
            string staging = Path.Combine(SaveSyncRoot(), "t", "r" + Guid.NewGuid().ToString("N").Substring(0, 12));
            string sourceCampaign = StrictCampaignDirectory(campaignId);
            string stagedCampaign = Path.Combine(staging, "campaign");
            int fileCount = 0;
            long expandedBytes = 0;
            int linkedProfiles = 0;
            try
            {
                Directory.CreateDirectory(stagedCampaign);
                if (Directory.Exists(sourceCampaign))
                {
                    foreach (string source in Directory.EnumerateFiles(sourceCampaign, "*", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileName(source).StartsWith("world_memory.sqlite", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!ShouldIncludeSaveSyncSnapshotFile(sourceCampaign, source))
                            continue;
                        string relative = RelativeBackupPath(sourceCampaign, source);
                        string target = Path.Combine(stagedCampaign, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        if (relative.Replace('\\', '/').EndsWith("/profile.json", StringComparison.OrdinalIgnoreCase)
                            && TryCreateSaveSyncHardLink(target, source))
                            linkedProfiles++;
                        else
                            File.Copy(source, target, false);
                        FileInfo info = new FileInfo(source);
                        fileCount++;
                        expandedBytes += info.Length;
                    }
                }
                Stopwatch dbTimer = Stopwatch.StartNew();
                string savePointId = SaveSyncPostgreSqlSnapshotPointId(metadata);
                long databaseBytes = ReignPostgreSqlStorage.SnapshotSizeBytes(
                    campaignId, savePointId);
                string databaseStateToken =
                    ReignPostgreSqlStorage.SnapshotStateToken(
                        campaignId, savePointId);
                expandedBytes += databaseBytes;
                dbTimer.Stop();
                string rawFingerprint = databaseStateToken + "_"
                    + Guid.NewGuid().ToString("N");
                timer.Stop();
                Dictionary<string, object> manifest = new Dictionary<string, object>
                {
                    ["format"] = "bannerlord-reign-save-sync-snapshot",
                    ["version"] = SaveSyncFormatVersion,
                    ["snapshotState"] = "postgresql_ready",
                    ["campaignId"] = campaignId,
                    ["savePointId"] = savePointId,
                    ["savePointKind"] = ReadString(metadata, "savePointKind", "native_save"),
                    ["campaignTimeDays"] = ReadDouble(metadata, "campaignTimeDays", 0d),
                    ["campaignTimeMilliseconds"] = ReadDouble(metadata, "campaignTimeMilliseconds", 0d),
                    ["capturedUtc"] = ReadString(metadata, "capturedUtc", DateTime.UtcNow.ToString("o")),
                    ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                    ["fileCount"] = fileCount,
                    ["expandedBytes"] = expandedBytes,
                    ["databaseProvider"] = "postgresql",
                    ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                    ["databaseEncoding"] = ReignPostgreSqlOptions.RequiredEncoding,
                    ["databaseBytes"] = databaseBytes,
                    ["databaseStateToken"] = databaseStateToken,
                    ["files"] = new List<Dictionary<string, object>>(),
                    ["fingerprint"] = rawFingerprint,
                    ["inventory"] = SaveSyncSubsystemNames().ToDictionary(
                        x => x, x => (object)0L, StringComparer.OrdinalIgnoreCase),
                    ["inventoryDeferred"] = true,
                    ["integrityDeferred"] = true,
                    ["timing"] = new Dictionary<string, object>
                    {
                        ["postgresqlSnapshotMs"] = dbTimer.ElapsedMilliseconds,
                        ["linkedProfileFiles"] = linkedProfiles,
                        ["totalMs"] = timer.ElapsedMilliseconds
                    }
                };
                WriteJsonObject(Path.Combine(staging, "manifest.json"), manifest);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationRoot));
                MoveDirectoryWithRetries(staging, destinationRoot);
                return manifest;
            }
            catch
            {
                TryDeleteDirectory(staging);
                throw;
            }
        }

        private static bool TryCreateSaveSyncHardLink(string destination, string source)
        {
            try
            {
                return Environment.OSVersion.Platform == PlatformID.Win32NT
                    && CreateHardLink(destination, source, IntPtr.Zero);
            }
            catch { return false; }
        }

        private static void CopySaveSyncTreeForOptimization(string sourceRoot, string destinationRoot)
        {
            foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = RelativeBackupPath(sourceRoot, source);
                string target = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                if (relative.Replace('\\', '/').EndsWith("/profile.json", StringComparison.OrdinalIgnoreCase)
                    && TryCreateSaveSyncHardLink(target, source))
                    continue;
                File.Copy(source, target, false);
            }
        }

        private static void QueueSaveSyncOptimization(string campaignId, string savePointId)
        {
            if (SaveSyncDisableBackgroundOptimizationForTests || ShutdownRequested) return;
            string key = campaignId + "|" + savePointId;
            lock (SaveSyncOptimizationQueued)
            {
                if (!SaveSyncOptimizationQueued.Add(key)) return;
            }
            Task.Run(async () =>
            {
                await SaveSyncOptimizationSemaphore.WaitAsync().ConfigureAwait(false);
                try { OptimizeRawSaveSyncSnapshot(campaignId, savePointId); }
                catch (Exception ex)
                {
                    AppendSaveSyncAudit(campaignId, "snapshot.optimize_failed", new Dictionary<string, object>
                    {
                        ["result"] = "raw_retained", ["savePointId"] = savePointId,
                        ["error"] = ex.ToString()
                    });
                }
                finally
                {
                    SaveSyncOptimizationSemaphore.Release();
                    lock (SaveSyncOptimizationQueued) SaveSyncOptimizationQueued.Remove(key);
                }
            });
        }

        private static void OptimizeRawSaveSyncSnapshot(string campaignId, string savePointId)
        {
            Dictionary<string, object> initialLedger = ReadSaveSyncLedger(campaignId);
            Dictionary<string, object> initialPoint = SaveSyncPoint(initialLedger, savePointId);
            if (initialPoint == null
                || !ReadString(initialPoint, "snapshotState", "")
                    .Equals("raw_ready", StringComparison.OrdinalIgnoreCase))
                return;
            string rawStorageId = ReadString(initialPoint, "snapshotStorageId", "");
            string rawRoot = SaveSyncSnapshotRoot(campaignId, initialPoint);
            if (!Directory.Exists(Path.Combine(rawRoot, "campaign"))) return;

            Stopwatch timer = Stopwatch.StartNew();
            string temp = Path.Combine(SaveSyncRoot(), "t", "o" + Guid.NewGuid().ToString("N").Substring(0, 12));
            try
            {
                SaveSyncStorageGate.EnterReadLock();
                try
                {
                    if (!Directory.Exists(rawRoot)) return;
                    Directory.CreateDirectory(temp);
                    CopySaveSyncTreeForOptimization(rawRoot, temp);
                }
                finally { SaveSyncStorageGate.ExitReadLock(); }

                string campaignRoot = Path.Combine(temp, "campaign");
                Stopwatch pruneTimer = Stopwatch.StartNew();
                Dictionary<string, object> prune =
                    new Dictionary<string, object>
                    {
                        ["tablesDropped"] = 0,
                        ["rowsRemoved"] = 0L,
                        ["databaseProvider"] = "postgresql"
                    };
                pruneTimer.Stop();
                Stopwatch manifestTimer = Stopwatch.StartNew();
                List<Dictionary<string, object>> files = BackupFileManifest(campaignRoot);
                manifestTimer.Stop();
                Dictionary<string, object> oldManifest = ReadJsonObject(Path.Combine(temp, "manifest.json"));
                string fingerprint = SaveSyncManifestFingerprint(files);
                Dictionary<string, object> optimizedManifest =
                    new Dictionary<string, object>(oldManifest, StringComparer.OrdinalIgnoreCase)
                    {
                        ["snapshotState"] = "optimized",
                        ["optimizedUtc"] = DateTime.UtcNow.ToString("o"),
                        ["fileCount"] = files.Count,
                        ["expandedBytes"] = files.Sum(x => ReadLong(x, "bytes", 0)),
                        ["files"] = files,
                        ["fingerprint"] = fingerprint,
                        ["inventory"] = BuildSaveSyncInventory(campaignRoot),
                        ["inventoryDeferred"] = false,
                        ["integrityDeferred"] = false,
                        ["databasePrune"] = prune,
                        ["optimizationTiming"] = new Dictionary<string, object>
                        {
                            ["databasePruneMs"] = pruneTimer.ElapsedMilliseconds,
                            ["fileManifestMs"] = manifestTimer.ElapsedMilliseconds
                        }
                    };
                WriteJsonObject(Path.Combine(temp, "manifest.json"), optimizedManifest);
                string optimizedStorageId = SaveSyncStorageKey("content|" + fingerprint);
                string optimizedRoot = SaveSyncContentRoot(campaignId, optimizedStorageId);

                CampaignDataGate.EnterWriteLock();
                try
                {
                    SaveSyncStorageGate.EnterWriteLock();
                    try
                    {
                        Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
                        Dictionary<string, object> point = SaveSyncPoint(ledger, savePointId);
                        if (point == null
                            || !ReadString(point, "snapshotStorageId", "")
                                .Equals(rawStorageId, StringComparison.OrdinalIgnoreCase))
                            return;
                        Dictionary<string, object> shared = ReadDictionaryList(ledger, "points")
                            .FirstOrDefault(candidate =>
                                !ReadString(candidate, "savePointId", "")
                                    .Equals(savePointId, StringComparison.OrdinalIgnoreCase)
                                && ReadString(candidate, "snapshotFingerprint", "")
                                    .Equals(fingerprint, StringComparison.OrdinalIgnoreCase)
                                && Directory.Exists(SaveSyncSnapshotRoot(campaignId, candidate)));
                        if (shared != null)
                        {
                            optimizedStorageId = ReadString(shared, "snapshotStorageId", optimizedStorageId);
                            TryDeleteDirectory(temp);
                        }
                        else if (Directory.Exists(optimizedRoot))
                        {
                            Dictionary<string, object> existingManifest =
                                ReadJsonObject(Path.Combine(optimizedRoot, "manifest.json"));
                            if (!ReadString(existingManifest, "fingerprint", "")
                                .Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Save Sync optimized content-key collision.");
                            TryDeleteDirectory(temp);
                        }
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(optimizedRoot));
                            MoveDirectoryWithRetries(temp, optimizedRoot);
                        }
                        point["snapshotStorageId"] = optimizedStorageId;
                        point["snapshotFingerprint"] = fingerprint;
                        point["snapshotBytes"] = ReadLong(optimizedManifest, "expandedBytes", 0);
                        point["snapshotFiles"] = ReadInt(optimizedManifest, "fileCount", 0);
                        point["snapshotState"] = "optimized";
                        point["optimizedUtc"] = DateTime.UtcNow.ToString("o");
                        ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
                        WriteSaveSyncLedger(campaignId, ledger);
                        TryDeleteDirectory(rawRoot);
                    }
                    finally { SaveSyncStorageGate.ExitWriteLock(); }
                }
                finally { CampaignDataGate.ExitWriteLock(); }
                timer.Stop();
                AppendSaveSyncAudit(campaignId, "snapshot.optimized", new Dictionary<string, object>
                {
                    ["result"] = "optimized", ["savePointId"] = savePointId,
                    ["durationMs"] = timer.ElapsedMilliseconds
                });
            }
            finally { TryDeleteDirectory(temp); }
        }

        private static void CreateSaveSyncSnapshot(
            string campaignId,
            string destinationRoot,
            Dictionary<string, object> metadata)
        {
            Dictionary<string, object> snapshotMetadata =
                new Dictionary<string, object>(
                    metadata ?? new Dictionary<string, object>(),
                    StringComparer.OrdinalIgnoreCase);
            string comparisonPointId = "comparison_"
                + Guid.NewGuid().ToString("N");
            try
            {
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(
                    campaignId, comparisonPointId);
                snapshotMetadata["postgresqlSnapshotPointId"] =
                    comparisonPointId;
                snapshotMetadata["savePointId"] = comparisonPointId;
                CreateRawSaveSyncSnapshot(
                    campaignId, destinationRoot, snapshotMetadata);
            }
            finally
            {
                ReignPostgreSqlStorage.DropSnapshot(
                    campaignId, comparisonPointId);
            }
        }


        private static Dictionary<string, object> BuildSaveSyncInventory(string campaignRoot)
        {
            Dictionary<string, long> counts = SaveSyncSubsystemNames().ToDictionary(x => x, x => 0L, StringComparer.OrdinalIgnoreCase);
            string manifestPath = Path.Combine(
                Directory.GetParent(campaignRoot)?.FullName ?? string.Empty,
                "manifest.json");
            if (File.Exists(manifestPath))
            {
                Dictionary<string, object> manifest = ReadJsonObject(manifestPath);
                string campaignId = ReadString(manifest, "campaignId", "");
                string savePointId = SaveSyncPostgreSqlSnapshotPointId(manifest);
                if (ReadString(manifest, "databaseProvider", "")
                        .Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(campaignId)
                    && !string.IsNullOrWhiteSpace(savePointId)
                    && ReignPostgreSqlStorage.SnapshotExists(campaignId, savePointId))
                {
                    string schema = ReignPostgreSqlStorage.SnapshotSchemaName(
                        campaignId, savePointId);
                    using (Npgsql.NpgsqlConnection connection =
                        ReignPostgreSqlStorage.OpenUtilityConnection(schema))
                    {
                        foreach (string table in ReadPostgreSqlSchemaTables(
                            connection, schema))
                        {
                            counts[SaveSyncSubsystemForTable(table)] +=
                                CountPostgreSqlTableRows(
                                    connection, schema, table);
                        }
                    }
                }
            }
            if (Directory.Exists(campaignRoot))
            {
                foreach (string path in Directory.GetFiles(campaignRoot, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(path).StartsWith("world_memory.sqlite", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = RelativeBackupPath(campaignRoot, path);
                    string subsystem = SaveSyncSubsystemForPath(relative);
                    if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) counts[subsystem] += SafeLineCount(path);
                    else if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) counts[subsystem] += SaveSyncJsonRecordCount(path);
                    else counts[subsystem] += 1;
                }
            }
            return counts.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> CompareSaveSyncInventory(Dictionary<string, object> current, Dictionary<string, object> target,
            string currentSnapshotRoot, string targetSnapshotRoot, Dictionary<string, object> currentManifest,
            Dictionary<string, object> targetManifest, bool analyzeDifferences)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string subsystem in SaveSyncSubsystemNames())
            {
                long before = ReadLong(current, subsystem, 0), after = ReadLong(target, subsystem, 0);
                if (subsystem == "audit") after = before;
                result[subsystem] = new Dictionary<string, object>
                {
                    ["current"] = before, ["target"] = after,
                    ["removed"] = 0L, ["restored"] = 0L, ["affected"] = 0L, ["netChange"] = after - before
                };
            }
            if (!analyzeDifferences) return result;

            Dictionary<string, Dictionary<string, object>> beforeFiles = ReadDictionaryList(currentManifest, "files")
                .Where(x => !IsSaveSyncFingerprintExcluded(ReadString(x, "path", "")))
                .ToDictionary(x => ReadString(x, "path", ""), x => x, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> afterFiles = ReadDictionaryList(targetManifest, "files")
                .Where(x => !IsSaveSyncFingerprintExcluded(ReadString(x, "path", "")))
                .ToDictionary(x => ReadString(x, "path", ""), x => x, StringComparer.OrdinalIgnoreCase);
            foreach (string relative in beforeFiles.Keys.Union(afterFiles.Keys, StringComparer.OrdinalIgnoreCase))
            {
                if (Path.GetFileName(relative).StartsWith("world_memory.sqlite", StringComparison.OrdinalIgnoreCase)) continue;
                bool hasBefore = beforeFiles.TryGetValue(relative, out Dictionary<string, object> beforeFile);
                bool hasAfter = afterFiles.TryGetValue(relative, out Dictionary<string, object> afterFile);
                if (hasBefore && hasAfter && ReadString(beforeFile, "sha256", "").Equals(ReadString(afterFile, "sha256", ""), StringComparison.OrdinalIgnoreCase)) continue;
                string subsystem = SaveSyncSubsystemForPath(relative);
                string beforePath = hasBefore ? SaveSyncSnapshotFile(currentSnapshotRoot, relative) : "";
                string afterPath = hasAfter ? SaveSyncSnapshotFile(targetSnapshotRoot, relative) : "";
                long removed = 0, restored = 0;
                if (hasBefore && hasAfter && relative.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    CompareSaveSyncJsonLines(beforePath, afterPath, out removed, out restored);
                else
                {
                    if (hasBefore) removed = SaveSyncFileRecordCount(beforePath);
                    if (hasAfter) restored = SaveSyncFileRecordCount(afterPath);
                }
                AddSaveSyncAffectedCounts(result, subsystem, removed, restored);
            }

            foreach (string subsystem in SaveSyncSubsystemNames())
            {
                long before = ReadLong(current, subsystem, 0);
                long after = ReadLong(target, subsystem, 0);
                AddSaveSyncAffectedCounts(
                    result,
                    subsystem,
                    Math.Max(0L, before - after),
                    Math.Max(0L, after - before));
            }
            foreach (Dictionary<string, object> row in result.Values.OfType<Dictionary<string, object>>())
                row["affected"] = ReadLong(row, "removed", 0) + ReadLong(row, "restored", 0);
            return result;
        }

        private static string SaveSyncSnapshotFile(string snapshotRoot, string relative)
        {
            return Path.Combine(snapshotRoot, "campaign", (relative ?? "").Replace('/', Path.DirectorySeparatorChar));
        }

        private static long SaveSyncFileRecordCount(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return 0;
            if (path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return SafeLineCount(path);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return Math.Max(1, SaveSyncJsonRecordCount(path));
            return 1;
        }

        private static void CompareSaveSyncJsonLines(string beforePath, string afterPath, out long removed, out long restored)
        {
            removed = 0; restored = 0;
            using (StreamReader before = new StreamReader(beforePath, Encoding.UTF8, true))
            using (StreamReader after = new StreamReader(afterPath, Encoding.UTF8, true))
            {
                while (true)
                {
                    string left = before.ReadLine(), right = after.ReadLine();
                    if (left == null && right == null) break;
                    if (left == null) { restored++; continue; }
                    if (right == null) { removed++; continue; }
                    if (!left.Equals(right, StringComparison.Ordinal)) { removed++; restored++; }
                }
            }
        }


        private static void AddSaveSyncAffectedCounts(Dictionary<string, object> counts, string subsystem, long removed, long restored)
        {
            Dictionary<string, object> row = ReadDictionary(counts, subsystem);
            if (row == null) return;
            row["removed"] = ReadLong(row, "removed", 0) + Math.Max(0, removed);
            row["restored"] = ReadLong(row, "restored", 0) + Math.Max(0, restored);
        }

        private static IEnumerable<string> SaveSyncSubsystemNames()
        {
            return new[] { "conversations", "events", "memories", "relationships", "worldHistory", "rumors", "actions", "diplomacy", "clanConflicts", "rebellions", "court", "identity", "reputation", "characterState", "semantic", "audit", "other" };
        }

        private static string SaveSyncSubsystemForTable(string table)
        {
            string value = (table ?? "").ToLowerInvariant();
            if (value.StartsWith("conversation_") || value == "letters" || value == "correspondence_threads") return "conversations";
            if (value == "events") return "events";
            if (value == "memories" || value == "summaries" || value == "comprehension" || value == "memory_sources" || value == "knowledge_receipts" || value == "beliefs") return "memories";
            if (value.StartsWith("relationship_") || value.StartsWith("ambient_relationship") || value == "relationships" || value.StartsWith("passive_relationship") || value.StartsWith("life_change") || value.StartsWith("conception") || value == "parentage" || value.StartsWith("marriage_")) return "relationships";
            if (value.StartsWith("world_history_")) return "worldHistory";
            if (value.StartsWith("rumor_" ) || value == "rumors") return "rumors";
            if (value.Contains("action") || value == "director_outbox") return "actions";
            if (value.StartsWith("clan_conflict_")) return "clanConflicts";
            if (value.StartsWith("rebellion_") || value.StartsWith("negotiated_")) return "rebellions";
            if (value.StartsWith("court_") || value.StartsWith("ambassador_") || value.StartsWith("intelligence_") || value == "obligations") return "court";
            if (value.StartsWith("acquaintance") || value.StartsWith("identity_")) return "identity";
            if (value.StartsWith("reputation_") || value == "character_reputations"
                || value == "directional_social_projections") return "reputation";
            if (value.StartsWith("character_editor_")) return "characterState";
            if (value.StartsWith("embedding_")) return "semantic";
            return "other";
        }

        private static string SaveSyncSubsystemForPath(string relative)
        {
            string value = (relative ?? "").Replace('\\', '/').ToLowerInvariant();
            if (value.StartsWith("audit/")) return "audit";
            if (value.StartsWith("actions/")) return "actions";
            if (value.StartsWith("diplomacy/")) return "diplomacy";
            if (value.StartsWith("clan-conflicts/") || value.StartsWith("clan_conflicts/")) return "clanConflicts";
            if (value.StartsWith("rebellions/") || value.StartsWith("negotiations/")) return "rebellions";
            if (value.StartsWith("events/")) return "events";
            if (value.StartsWith("world/")) return value.Contains("memor") ? "memories" : "worldHistory";
            if (value.Contains("/history/dialogue") || value.Contains("/transcript") || value.Contains("correspondence")) return "conversations";
            if (value.Contains("/history/events")) return "events";
            if (value.Contains("/memory/relationships")) return "relationships";
            if (value.Contains("/memory/") || value.Contains("hidden_history") || value.Contains("secrets")) return "memories";
            if (value.StartsWith("characters/")) return "characterState";
            if (value.StartsWith("court/")) return "court";
            if (value.StartsWith("rumors/")) return "rumors";
            return "other";
        }

        private static bool IsSaveSyncFtsShadowTable(string table)
        {
            string value = (table ?? "").ToLowerInvariant();
            return value.EndsWith("_data") || value.EndsWith("_idx") || value.EndsWith("_content") || value.EndsWith("_docsize") || value.EndsWith("_config") || value.EndsWith("_fts");
        }

        private static long SaveSyncJsonRecordCount(string path)
        {
            try
            {
                object parsed = Json.DeserializeObject(File.ReadAllText(path, Encoding.UTF8));
                if (parsed is ArrayList list) return list.Count;
                if (parsed is Dictionary<string, object> dictionary) return dictionary.Count == 0 ? 0 : 1;
            }
            catch { }
            return 1;
        }

        private static long SafeLineCount(string path)
        {
            try { long count = 0; using (StreamReader reader = new StreamReader(path)) while (reader.ReadLine() != null) count++; return count; }
            catch { return 0; }
        }

        private static int CountChangedSnapshotFiles(Dictionary<string, object> current, Dictionary<string, object> target)
        {
            Dictionary<string, string> before = ReadDictionaryList(current, "files").Where(x => !IsSaveSyncFingerprintExcluded(ReadString(x, "path", ""))).ToDictionary(x => ReadString(x, "path", ""), x => ReadString(x, "sha256", ""), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> after = ReadDictionaryList(target, "files").Where(x => !IsSaveSyncFingerprintExcluded(ReadString(x, "path", ""))).ToDictionary(x => ReadString(x, "path", ""), x => ReadString(x, "sha256", ""), StringComparer.OrdinalIgnoreCase);
            return before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase).Count(path => !before.TryGetValue(path, out string left) || !after.TryGetValue(path, out string right) || !left.Equals(right, StringComparison.OrdinalIgnoreCase));
        }

        private static long SumSaveSyncCount(Dictionary<string, object> counts, string field)
        {
            return (counts ?? new Dictionary<string, object>()).Values.OfType<Dictionary<string, object>>().Sum(x => ReadLong(x, field, 0));
        }

        private static string SaveSyncManifestFingerprint(List<Dictionary<string, object>> files)
        {
            StringBuilder builder = new StringBuilder();
            foreach (Dictionary<string, object> file in (files ?? new List<Dictionary<string, object>>()).Where(x => !IsSaveSyncFingerprintExcluded(ReadString(x, "path", ""))).OrderBy(x => ReadString(x, "path", ""), StringComparer.OrdinalIgnoreCase))
                builder.Append(ReadString(file, "path", "").ToLowerInvariant()).Append('|').Append(ReadLong(file, "bytes", 0)).Append('|').Append(ReadString(file, "sha256", "")).Append('\n');
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()))).Replace("-", "").ToLowerInvariant();
        }

        private static int MergeSaveSyncRetainedAssets(string activeCampaign, string stagedCampaign)
        {
            int copied = 0;
            foreach (string relative in new[] { "campaign.json", "characters/index.json", "portrait_roster.json" })
            {
                string source = Path.Combine(activeCampaign, relative.Replace('/', Path.DirectorySeparatorChar));
                string target = Path.Combine(stagedCampaign, relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
                copied++;
            }
            string activeCharacters = Path.Combine(activeCampaign, "characters"), targetCharacters = Path.Combine(stagedCampaign, "characters");
            if (Directory.Exists(activeCharacters) && Directory.Exists(targetCharacters))
            {
                foreach (string targetCharacter in Directory.GetDirectories(targetCharacters))
                {
                    string activeCharacter = Path.Combine(activeCharacters, Path.GetFileName(targetCharacter));
                    if (!Directory.Exists(activeCharacter)) continue;
                    foreach (string name in SaveSyncRetainedCharacterFiles)
                    {
                        string source = Path.Combine(activeCharacter, name), destination = Path.Combine(targetCharacter, name);
                        if (!File.Exists(source)) continue;
                        File.Copy(source, destination, true);
                        copied++;
                    }
                    string sourcePortraits = Path.Combine(activeCharacter, "portraits"), targetPortraits = Path.Combine(targetCharacter, "portraits");
                    if (Directory.Exists(sourcePortraits))
                    {
                        CopyDirectoryTree(sourcePortraits, targetPortraits, null);
                        copied += SafeFileCount(sourcePortraits, "*");
                    }
                }
            }
            string activeAudit = Path.Combine(activeCampaign, "audit"), targetAudit = Path.Combine(stagedCampaign, "audit");
            if (Directory.Exists(activeAudit))
            {
                CopyDirectoryTree(activeAudit, targetAudit, null);
                copied += SafeFileCount(activeAudit, "*");
            }
            string activeTests = Path.Combine(activeCampaign, "tests"), targetTests = Path.Combine(stagedCampaign, "tests");
            if (Directory.Exists(activeTests))
            {
                CopyDirectoryTree(activeTests, targetTests, null);
                copied += SafeFileCount(activeTests, "*");
            }
            return copied;
        }

        private static bool IsSaveSyncFingerprintExcluded(string relativePath)
        {
            string value = (relativePath ?? "").Replace('\\', '/').ToLowerInvariant();
            if (value == "campaign.json"
                || value == "characters/index.json"
                || value == "portrait_roster.json"
                || value.StartsWith("audit/")
                || value.StartsWith("tests/")) return true;
            if (!value.StartsWith("characters/")) return false;
            string name = Path.GetFileName(value);
            return value.Contains("/portraits/") || SaveSyncRetainedCharacterFiles.Contains(name);
        }

        private static bool ShouldIncludeSaveSyncSnapshotFile(string campaignRoot, string path)
        {
            if (IsLiveSqliteSidecar(path)) return false;
            string relative = RelativeBackupPath(campaignRoot, path);
            // These assets belong to the campaign but are not temporal save state. They remain
            // in the live campaign and are overlaid after rollback instead of being recopied into
            // every native-save snapshot.
            return !IsSaveSyncFingerprintExcluded(relative);
        }

        private static SaveSyncSemanticReconciliationPlan BuildSaveSyncSemanticReconciliationPlan(
            string currentSnapshotRoot, string targetSnapshotRoot)
        {
            Dictionary<string, Dictionary<string, object>> current = ReadSaveSyncEmbeddingDocuments(currentSnapshotRoot);
            Dictionary<string, Dictionary<string, object>> target = ReadSaveSyncEmbeddingDocuments(targetSnapshotRoot);
            return BuildSaveSyncSemanticReconciliationPlan(current, target);
        }

        private static void SetSaveSyncSemanticBlocked(string campaignId, bool blocked)
        {
            lock (SaveSyncSemanticBlockedCampaigns)
            {
                if (blocked) SaveSyncSemanticBlockedCampaigns.Add(campaignId);
                else SaveSyncSemanticBlockedCampaigns.Remove(campaignId);
            }
        }

        private static bool IsSaveSyncSemanticBlocked(string campaignId)
        {
            lock (SaveSyncSemanticBlockedCampaigns)
                return SaveSyncSemanticBlockedCampaigns.Contains(campaignId ?? "");
        }

        private static void QueueSaveSyncSemanticReconciliation(
            string campaignId,
            SaveSyncSemanticReconciliationPlan plan,
            Dictionary<string, object> settings)
        {
            if (SaveSyncSkipSemanticForTests)
            {
                SetSaveSyncSemanticBlocked(campaignId, false);
                return;
            }
            Task.Run(() =>
            {
                bool stopped = false;
                try
                {
                    if (!InvalidateSaveSyncSemanticVectors(plan, settings))
                        throw new InvalidOperationException(
                            "Semantic vector invalidation failed; lexical search remains active.");
                    StopSemanticMemorySubsystem();
                    stopped = true;
                    Dictionary<string, object> queued =
                        ReconcileSemanticMemoryAfterSaveSync(campaignId, plan, settings);
                    AppendSaveSyncAudit(campaignId, "load.semantic_reconciled",
                        new Dictionary<string, object>
                        {
                            ["result"] = "completed",
                            ["semanticReindexQueued"] = ReadInt(queued, "queued", 0),
                            ["semanticSourcesRequeued"] = ReadInt(queued, "explicitlyRequeued", 0)
                        });
                }
                catch (Exception ex)
                {
                    AppendSaveSyncAudit(campaignId, "load.semantic_reconcile_failed",
                        new Dictionary<string, object>
                        {
                            ["result"] = "lexical_fallback",
                            ["error"] = ex.ToString()
                        });
                }
                finally
                {
                    if (stopped && ReadBool(LoadSettings(), "enableSemanticMemory", true)
                        && !ShutdownRequested)
                        StartSemanticMemorySubsystem(LoadSettings());
                    SetSaveSyncSemanticBlocked(campaignId, false);
                }
            });
        }

        private static SaveSyncSemanticReconciliationPlan BuildSaveSyncSemanticReconciliationPlan(
            Dictionary<string, Dictionary<string, object>> current,
            Dictionary<string, Dictionary<string, object>> target)
        {
            SaveSyncSemanticReconciliationPlan plan = new SaveSyncSemanticReconciliationPlan();
            current = current ?? new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            target = target ?? new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            plan.CurrentDocuments = current.Count;
            plan.TargetDocuments = target.Values.Count(SaveSyncEmbeddingDocumentEligible);

            foreach (KeyValuePair<string, Dictionary<string, object>> pair in current)
            {
                Dictionary<string, object> currentRow = pair.Value;
                bool currentHasVector = !string.IsNullOrWhiteSpace(ReadString(currentRow, "vector_id", ""))
                    && !string.Equals(ReadString(currentRow, "status", ""), "deleted", StringComparison.OrdinalIgnoreCase);
                bool compatible = target.TryGetValue(pair.Key, out Dictionary<string, object> targetRow)
                    && SaveSyncEmbeddingDocumentEligible(targetRow)
                    && string.Equals(ReadString(currentRow, "status", ""), "indexed", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(targetRow, "status", ""), "indexed", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(currentRow, "content_hash", ""), ReadString(targetRow, "content_hash", ""), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(currentRow, "vector_id", ""), ReadString(targetRow, "vector_id", ""), StringComparison.OrdinalIgnoreCase);
                if (compatible)
                {
                    plan.PreservedDocuments++;
                }
                else if (currentHasVector)
                {
                    plan.DeleteVectorIds.Add(ReadString(currentRow, "vector_id", ""));
                    plan.InvalidatedCurrentSources.Add(new SemanticSourceIdentity
                    {
                        SourceType = ReadString(currentRow, "source_type", ""),
                        SourceId = ReadString(currentRow, "source_id", "")
                    });
                }
            }

            foreach (KeyValuePair<string, Dictionary<string, object>> pair in target)
            {
                Dictionary<string, object> targetRow = pair.Value;
                if (!SaveSyncEmbeddingDocumentEligible(targetRow)) continue;
                bool compatible = current.TryGetValue(pair.Key, out Dictionary<string, object> currentRow)
                    && string.Equals(ReadString(currentRow, "status", ""), "indexed", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(targetRow, "status", ""), "indexed", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(currentRow, "content_hash", ""), ReadString(targetRow, "content_hash", ""), StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(currentRow, "vector_id", ""), ReadString(targetRow, "vector_id", ""), StringComparison.OrdinalIgnoreCase);
                if (!compatible)
                {
                    plan.RequeueSources.Add(new SemanticSourceIdentity
                    {
                        SourceType = ReadString(targetRow, "source_type", ""),
                        SourceId = ReadString(targetRow, "source_id", "")
                    });
                }
            }
            return plan;
        }

        private static Dictionary<string, Dictionary<string, object>> ReadSaveSyncEmbeddingDocuments(string snapshotRoot)
        {
            Dictionary<string, Dictionary<string, object>> rows =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> manifest = ReadJsonObject(
                Path.Combine(snapshotRoot, "manifest.json"));
            string campaignId = ReadString(manifest, "campaignId", "");
            string savePointId = SaveSyncPostgreSqlSnapshotPointId(manifest);
            if (!ReadString(manifest, "databaseProvider", "")
                    .Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(savePointId)
                || !ReignPostgreSqlStorage.SnapshotExists(campaignId, savePointId))
                return rows;
            string schema = ReignPostgreSqlStorage.SnapshotSchemaName(
                campaignId, savePointId);
            using (Npgsql.NpgsqlConnection connection =
                ReignPostgreSqlStorage.OpenUtilityConnection(schema))
            {
                HashSet<string> tables = ReadPostgreSqlSchemaTables(
                    connection, schema);
                bool hasDocuments = tables.Contains("embedding_documents");
                if (!hasDocuments) return rows;
                bool hasWorldHistory = tables.Contains("world_history_events");
                string sql = hasWorldHistory
                    ? @"SELECT d.*,w.event_type,w.dissemination_class FROM embedding_documents d
LEFT JOIN world_history_events w ON d.source_type='world_history_event' AND w.event_id=d.source_id;"
                    : "SELECT d.*,'' event_type,'' dissemination_class FROM embedding_documents d;";
                foreach (Dictionary<string, object> row in QuerySql(connection, sql))
                {
                    rows[SaveSyncEmbeddingDocumentKey(row)] = row;
                }
            }
            return rows;
        }

        private static string SaveSyncEmbeddingDocumentKey(Dictionary<string, object> row)
        {
            return string.Join("\n", new[]
            {
                ReadString(row, "source_type", ""),
                ReadString(row, "source_id", ""),
                ReadString(row, "model_version", ""),
                ReadString(row, "provider", "")
            });
        }

        private static bool SaveSyncEmbeddingDocumentEligible(Dictionary<string, object> row)
        {
            if (!string.Equals(ReadString(row, "source_type", ""), "world_history_event", StringComparison.OrdinalIgnoreCase)) return true;
            return WorldHistoryEventEmbeddingEligible(row);
        }

        private static bool InvalidateSaveSyncSemanticVectors(SaveSyncSemanticReconciliationPlan plan,
            Dictionary<string, object> settings)
        {
            if (!ReadBool(settings, "enableSemanticMemory", true) || plan == null || plan.DeleteVectorIds.Count == 0) return true;
            try
            {
                StartManagedSemanticWorker(settings);
                foreach (List<string> batch in plan.DeleteVectorIds.Select((id, index) => new { id, index })
                    .GroupBy(x => x.index / 500).Select(group => group.Select(x => x.id).ToList()))
                {
                    Dictionary<string, object> request = SemanticProviderRequest(settings);
                    request["collection"] = SemanticCollection;
                    request["ids"] = batch;
                    Dictionary<string, object> response = TryParseJsonObject(PostJsonToUrl(
                        SemanticWorkerUrl(settings, "/vectors/delete"), Json.Serialize(request), 30000))
                        ?? new Dictionary<string, object>();
                    if (!ReadBool(response, "ok", false))
                        throw new InvalidOperationException(ReadString(response, "error", "Semantic vector invalidation failed."));
                }
                return true;
            }
            catch (Exception ex)
            {
                SemanticWorkerUnavailableUntilUtc = DateTime.UtcNow.AddMinutes(10);
                SemanticWorkerLastError = "Save Sync semantic invalidation: " + ex.Message;
                LogOperational("save_sync.semantic_invalidation_failed", new Dictionary<string, object>
                {
                    ["error"] = ex.Message,
                    ["fallback"] = "PostgreSQL full-text retrieval remains authoritative until selective reconciliation succeeds."
                });
                return false;
            }
        }

        private static void RestoreSaveSyncSnapshotDirectory(string snapshotRoot, string destinationCampaign)
        {
            string source = Path.Combine(snapshotRoot, "campaign");
            if (!Directory.Exists(source)) throw new IOException("Recovery snapshot is missing campaign data.");
            string incoming = Path.Combine(SaveSyncRoot(), "t", "u" + Guid.NewGuid().ToString("N").Substring(0, 12));
            Directory.CreateDirectory(Path.GetDirectoryName(incoming));
            CopyDirectoryTree(source, incoming, null);
            MoveDirectoryWithRetries(incoming, destinationCampaign);
        }

        private static void ClearSaveSyncCaches(string campaignId)
        {
            InvalidateWaitingCampaignRequests();
            lock (WorldHistoryCacheLock) WorldHistoryClaimCache.Clear();
            lock (CharacterEditorAuthorityCacheLock) CharacterEditorAuthorityByCampaign.Remove(campaignId);
            InvalidateRelationshipPairStateCache(campaignId);
            ReignPostgreSqlStorage.ClearAllPools();
            ResetRelationshipCampaignScheduling(campaignId);
        }

        private static Dictionary<string, object> NormalizeSaveSyncPoint(Dictionary<string, object> payload)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = ReadString(payload, "campaignId", ""), ["savePointId"] = ReadString(payload, "savePointId", ""),
                ["savePointKind"] = ReadString(payload, "savePointKind", "native_save"),
                ["campaignLabel"] = LimitText(ReadString(payload, "campaignLabel", ""), 500),
                ["campaignTimeDays"] = ReadDouble(payload, "campaignTimeDays", 0d),
                ["campaignTimeMilliseconds"] = ReadDouble(payload, "campaignTimeMilliseconds", 0d),
                ["capturedUtc"] = ReadString(payload, "capturedUtc", DateTime.UtcNow.ToString("o")),
                ["nativeSaveName"] = LimitText(ReadString(payload, "nativeSaveName", ""), 240),
                ["nativeCreationUtc"] = LimitText(ReadString(payload, "nativeCreationUtc", ""), 100),
                ["timelineId"] = LimitText(ReadString(payload, "timelineId", ""), 160),
                ["worldHistorySequence"] = ReadLong(payload, "worldHistorySequence", 0),
                ["worldHistoryHeadEventId"] = LimitText(ReadString(payload, "worldHistoryHeadEventId", ""), 200),
                ["clientVersion"] = LimitText(ReadString(payload, "clientVersion", ""), 100)
            };
        }

        private static Dictionary<string, object> PublicSaveSyncPoint(Dictionary<string, object> point)
        {
            if (point == null) return new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["savePointId"] = ReadString(point, "savePointId", ""), ["savePointKind"] = ReadString(point, "savePointKind", ""),
                ["campaignTimeDays"] = ReadDouble(point, "campaignTimeDays", 0d),
                ["campaignTimeMilliseconds"] = ReadDouble(point, "campaignTimeMilliseconds", 0d),
                ["capturedUtc"] = ReadString(point, "capturedUtc", ""), ["nativeSaveName"] = ReadString(point, "nativeSaveName", ""),
                ["nativeCreationUtc"] = ReadString(point, "nativeCreationUtc", ""), ["timelineId"] = ReadString(point, "timelineId", ""),
                ["worldHistorySequence"] = ReadLong(point, "worldHistorySequence", 0), ["status"] = ReadString(point, "status", ""),
                ["snapshotBytes"] = ReadLong(point, "snapshotBytes", 0), ["registeredUtc"] = ReadString(point, "registeredUtc", ""),
                ["saveFinalizedUtc"] = ReadString(point, "saveFinalizedUtc", "")
            };
        }

        private static Dictionary<string, object> PublicSaveSyncPoint(
            string campaignId,
            Dictionary<string, object> point)
        {
            Dictionary<string, object> result = PublicSaveSyncPoint(point);
            bool available = SaveSyncPointSnapshotAvailable(campaignId, point);
            result["snapshotAvailable"] = available;
            if (!available)
            {
                result["status"] = "legacy_snapshot_unavailable";
                result["availabilityReason"] =
                    "The pre-PostgreSQL rollback snapshot is not available. Save this campaign again to create a protected PostgreSQL restore point.";
            }
            return result;
        }

        private static bool SaveSyncPointSnapshotAvailable(
            string campaignId,
            Dictionary<string, object> point)
        {
            if (point == null) return false;
            string savePointId = SaveSyncPostgreSqlSnapshotPointId(point);
            if (string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(savePointId)) return false;
            try
            {
                string root = SaveSyncSnapshotRoot(campaignId, point);
                return Directory.Exists(Path.Combine(root, "campaign"))
                    && File.Exists(Path.Combine(root, "manifest.json"))
                    && ReignPostgreSqlStorage.SnapshotExists(
                    campaignId, savePointId);
            }
            catch { return false; }
        }

        private static Dictionary<string, object> ReadSaveSyncLedger(string campaignId)
        {
            lock (SaveSyncLedgerLock)
            {
                Dictionary<string, object> ledger = ReadJsonObject(Path.Combine(SaveSyncCampaignRoot(campaignId), "ledger.json"));
                if (ledger.Count == 0) ledger = new Dictionary<string, object>();
                string storedCampaignId = ReadString(ledger, "campaignId", "");
                if (!string.IsNullOrWhiteSpace(storedCampaignId) && !storedCampaignId.Equals(campaignId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Save Sync campaign storage-key collision detected.");
                ledger["format"] = "bannerlord-reign-save-sync-ledger";
                ledger["version"] = SaveSyncFormatVersion;
                ledger["campaignId"] = campaignId;
                if (!ledger.ContainsKey("points")) ledger["points"] = new List<Dictionary<string, object>>();
                return ledger;
            }
        }

        private static void WriteSaveSyncLedger(string campaignId, Dictionary<string, object> ledger)
        {
            lock (SaveSyncLedgerLock)
            {
                string root = SaveSyncCampaignRoot(campaignId);
                Directory.CreateDirectory(root);
                string path = Path.Combine(root, "ledger.json"), temp = path + ".tmp_" + Guid.NewGuid().ToString("N");
                File.WriteAllText(temp, Json.Serialize(ledger), new UTF8Encoding(false));
                ReplaceSaveSyncFileWithRetry(temp, path);
            }
        }

        private static void ReplaceSaveSyncFileWithRetry(string incomingPath, string destinationPath)
        {
            const int maximumAttempts = 6;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(destinationPath))
                    {
                        // Publish the complete same-directory file atomically without
                        // ReplaceFile's metadata merge, which can fail after a restore.
                        const uint replaceExisting = 0x1, writeThrough = 0x8;
                        if (!MoveFileEx(ExtendedLengthPath(incomingPath),
                            ExtendedLengthPath(destinationPath), replaceExisting | writeThrough))
                            throw new IOException("Atomic Save Sync file replacement failed.",
                                new System.ComponentModel.Win32Exception(
                                    System.Runtime.InteropServices.Marshal.GetLastWin32Error()));
                    }
                    else
                        File.Move(incomingPath, destinationPath);
                    return;
                }
                catch (IOException) when (attempt < maximumAttempts)
                {
                    Thread.Sleep(10 << (attempt - 1));
                }
                catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
                {
                    Thread.Sleep(10 << (attempt - 1));
                }
            }
        }

        private static Dictionary<string, object> SaveSyncPoint(Dictionary<string, object> ledger, string savePointId)
        {
            return ReadDictionaryList(ledger, "points").FirstOrDefault(x => ReadString(x, "savePointId", "").Equals(savePointId, StringComparison.OrdinalIgnoreCase));
        }

        private static void UpsertSaveSyncPoint(Dictionary<string, object> ledger, Dictionary<string, object> point)
        {
            List<Dictionary<string, object>> points = ReadDictionaryList(ledger, "points");
            string id = ReadString(point, "savePointId", "");
            points.RemoveAll(x => ReadString(x, "savePointId", "").Equals(id, StringComparison.OrdinalIgnoreCase));
            points.Add(point);
            ledger["points"] = points.OrderBy(x => ReadDouble(x, "campaignTimeMilliseconds", 0d)).ToList();
        }

        private static void RemoveSaveSyncPoint(Dictionary<string, object> ledger, string savePointId)
        {
            List<Dictionary<string, object>> points = ReadDictionaryList(ledger, "points");
            points.RemoveAll(x => ReadString(x, "savePointId", "").Equals(savePointId, StringComparison.OrdinalIgnoreCase));
            ledger["points"] = points;
        }

        private static int SaveSyncUniqueStateCount(Dictionary<string, object> ledger)
        {
            return ReadDictionaryList(ledger, "points")
                .Where(point => !ReadString(point, "status", "")
                    .Equals("legacy_snapshot_unavailable",
                        StringComparison.OrdinalIgnoreCase))
                .Select(point => FirstNonEmpty(
                    ReadString(point, "snapshotFingerprint", ""),
                    ReadString(point, "snapshotStorageId", ""),
                    ReadString(point, "savePointId", "")))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static int SaveSyncEffectiveUniqueStateCountForRegistration(
            Dictionary<string, object> ledger,
            Dictionary<string, object> incomingPoint)
        {
            string nativeSaveName = ReadString(
                incomingPoint,
                "nativeSaveName",
                "");
            IEnumerable<Dictionary<string, object>> points =
                ReadDictionaryList(ledger, "points")
                    .Where(point => !ReadString(point, "status", "")
                        .Equals("legacy_snapshot_unavailable",
                            StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(nativeSaveName))
            {
                points = points.Where(point =>
                    !ReadString(point, "nativeSaveName", "")
                        .Equals(
                            nativeSaveName,
                            StringComparison.OrdinalIgnoreCase));
            }
            return points
                .Select(point => FirstNonEmpty(
                    ReadString(point, "snapshotFingerprint", ""),
                    ReadString(point, "snapshotStorageId", ""),
                    ReadString(point, "savePointId", "")))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }

        private static void RemoveSupersededNativeSavePoints(
            string campaignId,
            Dictionary<string, object> ledger,
            Dictionary<string, object> currentPoint)
        {
            string currentId = ReadString(currentPoint, "savePointId", "");
            string nativeSaveName = ReadString(currentPoint, "nativeSaveName", "");
            if (string.IsNullOrWhiteSpace(nativeSaveName)) return;
            List<Dictionary<string, object>> superseded = ReadDictionaryList(ledger, "points")
                .Where(point => !ReadString(point, "savePointId", "").Equals(currentId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(point, "nativeSaveName", "").Equals(nativeSaveName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (Dictionary<string, object> point in superseded)
            {
                RemoveSaveSyncPoint(ledger, ReadString(point, "savePointId", ""));
                DeleteSaveSyncPointStorageIfUnreferenced(campaignId, ledger, point);
            }
        }

        private static List<Dictionary<string, object>>
            RemoveSupersededNativeSavePointsFromOtherCampaigns(
                string currentCampaignId,
                Dictionary<string, object> currentPoint)
        {
            string nativeSaveName = NormalizeNativeSaveSlot(
                ReadString(currentPoint, "nativeSaveName", ""));
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(nativeSaveName)) return results;

            foreach (string campaignId in SaveSyncCampaignIds()
                .Where(id => !id.Equals(currentCampaignId,
                    StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try
                {
                    Dictionary<string, object> ledger =
                        ReadSaveSyncLedger(campaignId);
                    List<Dictionary<string, object>> superseded =
                        ReadDictionaryList(ledger, "points")
                            .Where(point => NormalizeNativeSaveSlot(
                                ReadString(point, "nativeSaveName", ""))
                                    .Equals(nativeSaveName,
                                        StringComparison.OrdinalIgnoreCase))
                            .ToList();
                    if (superseded.Count == 0) continue;

                    Dictionary<string, object> reconciliation =
                        ReconcileStaleSaveSyncPoints(campaignId, superseded);
                    int remaining = ReadDictionaryList(
                        ReadSaveSyncLedger(campaignId), "points").Count;
                    reconciliation["nativeSaveName"] = nativeSaveName;
                    reconciliation["supersededByCampaignId"] =
                        currentCampaignId;
                    reconciliation["campaignDeleted"] = false;
                    if (remaining == 0)
                    {
                        Dictionary<string, object> metadata = ReadJsonObject(
                            Path.Combine(StrictCampaignDirectory(campaignId),
                                "campaign.json"));
                        Dictionary<string, object> deletion =
                            DeleteCampaignOwnedDataCore(
                                campaignId,
                                ReadString(metadata, "campaignLabel",
                                    ReadString(metadata, "mainHeroName", campaignId)),
                                true,
                                "native_save_slot_reassigned");
                        reconciliation["campaignDeleted"] = ReadBool(
                            deletion, "campaignDeleted", false);
                        reconciliation["campaignDeletion"] = deletion;
                    }
                    results.Add(reconciliation);
                }
                catch (Exception ex)
                {
                    results.Add(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["nativeSaveName"] = nativeSaveName,
                        ["supersededByCampaignId"] = currentCampaignId,
                        ["campaignDeleted"] = false,
                        ["error"] = ex.Message
                    });
                }
            }
            return results;
        }

        private static Dictionary<string, object> ReconcileStaleSaveSyncPoints(
            string campaignId,
            IEnumerable<Dictionary<string, object>> stalePoints)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            HashSet<string> ids = new HashSet<string>(
                (stalePoints ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Select(point => ReadString(point, "savePointId", ""))
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> ledger =
                ReadSaveSyncLedger(campaignId);
            List<Dictionary<string, object>> matches =
                ReadDictionaryList(ledger, "points")
                    .Where(point => ids.Contains(
                        ReadString(point, "savePointId", "")))
                    .ToList();
            int deletedStates = 0;
            foreach (Dictionary<string, object> point in matches)
            {
                string storageId = ReadString(
                    point, "snapshotStorageId", "");
                RemoveSaveSyncPoint(ledger,
                    ReadString(point, "savePointId", ""));
                DeleteSaveSyncPointStorageIfUnreferenced(
                    campaignId, ledger, point);
                if (!ReadDictionaryList(ledger, "points").Any(candidate =>
                    ReadString(candidate, "snapshotStorageId", "").Equals(
                        storageId, StringComparison.OrdinalIgnoreCase)))
                    deletedStates++;
            }
            if (matches.Count > 0)
            {
                ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
                WriteSaveSyncLedger(campaignId, ledger);
                AppendSaveSyncAudit(campaignId,
                    "save_point.stale_registration_reconciled",
                    new Dictionary<string, object>
                    {
                        ["result"] = "deleted",
                        ["deletedPoints"] = matches.Count,
                        ["deletedUniqueStates"] = deletedStates,
                        ["remainingSavePoints"] = ReadDictionaryList(
                            ledger, "points").Count
                    });
            }
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["deletedPoints"] = matches.Count,
                ["deletedUniqueStates"] = deletedStates,
                ["remainingSavePoints"] = ReadDictionaryList(
                    ledger, "points").Count
            };
        }

        private static void DeleteSaveSyncPointStorageIfUnreferenced(
            string campaignId,
            Dictionary<string, object> ledger,
            Dictionary<string, object> point)
        {
            string pointId = ReadString(point, "savePointId", "");
            string postgresPointId = SaveSyncPostgreSqlSnapshotPointId(point);
            bool postgresReferenced = ReadDictionaryList(ledger, "points")
                .Any(candidate =>
                    !ReadString(candidate, "savePointId", "")
                        .Equals(pointId, StringComparison.OrdinalIgnoreCase)
                    && SaveSyncPostgreSqlSnapshotPointId(candidate)
                        .Equals(postgresPointId,
                            StringComparison.OrdinalIgnoreCase));
            if (!postgresReferenced)
            {
                try
                {
                    ReignPostgreSqlStorage.DropSnapshot(campaignId,
                        postgresPointId);
                }
                catch { }
            }
            string storageId = ReadString(point, "snapshotStorageId", "");
            bool referenced = ReadDictionaryList(ledger, "points").Any(candidate =>
                !ReadString(candidate, "savePointId", "").Equals(pointId, StringComparison.OrdinalIgnoreCase)
                && ReadString(candidate, "snapshotStorageId", "").Equals(storageId, StringComparison.OrdinalIgnoreCase));
            if (!referenced)
            {
                SaveSyncStorageGate.EnterWriteLock();
                try { TryDeleteDirectory(SaveSyncSnapshotRoot(campaignId, point)); }
                finally { SaveSyncStorageGate.ExitWriteLock(); }
            }
        }

        private static string SaveSyncPostgreSqlSnapshotPointId(
            Dictionary<string, object> point)
        {
            return ReadString(point, "postgresqlSnapshotPointId",
                ReadString(point, "savePointId", ""));
        }

        private static void UpdateSaveSyncLastResult(Dictionary<string, object> ledger, Dictionary<string, object> point, Dictionary<string, object> result, string backupId)
        {
            ledger["lastLoadedSavePoint"] = PublicSaveSyncPoint(point);
            ledger["lastSyncResult"] = new Dictionary<string, object>
            {
                ["result"] = ReadString(result, "result", ""), ["ok"] = ReadBool(result, "ok", false),
                ["completedUtc"] = DateTime.UtcNow.ToString("o"), ["durationMs"] = ReadLong(result, "durationMs", 0),
                ["recordsRolledBack"] = ReadLong(result, "recordsRolledBack", 0), ["recordsRestored"] = ReadLong(result, "recordsRestored", 0),
                ["changedFiles"] = ReadInt(result, "changedFiles", 0),
                ["countsBySubsystem"] = ReadDictionary(result, "countsBySubsystem") ?? new Dictionary<string, object>(),
                ["error"] = LimitText(ReadString(result, "error", ""), 2000), ["backupId"] = backupId ?? ""
            };
            ledger["lastBackupId"] = backupId ?? "";
            ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
        }

        private static void AppendSaveSyncAudit(string campaignId, string eventType, Dictionary<string, object> data)
        {
            try
            {
                string path = Path.Combine(SaveSyncCampaignRoot(campaignId), "audit.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTime.UtcNow.ToString("o"),
                    ["eventType"] = eventType, ["campaignId"] = campaignId,
                    ["savePointId"] = ReadString(data, "savePointId", ""), ["result"] = ReadString(data, "result", ""),
                    ["durationMs"] = ReadLong(data, "durationMs", 0), ["recordsRolledBack"] = ReadLong(data, "recordsRolledBack", 0),
                    ["recordsRestored"] = ReadLong(data, "recordsRestored", 0), ["changedFiles"] = ReadInt(data, "changedFiles", 0),
                    ["backupId"] = ReadString(data, "backupId", ""), ["countsBySubsystem"] = ReadDictionary(data, "countsBySubsystem") ?? new Dictionary<string, object>(),
                    ["error"] = LimitText(ReadString(data, "error", ""), 4000)
                };
                AppendBoundedJsonLineToPath(path, row, 4L * 1024L * 1024L, 2L * 1024L * 1024L);
                LogOperational("save_sync." + eventType, row);
            }
            catch { }
        }

        private static void CleanupSaveSyncRecovery(string campaignId)
        {
            string root = Path.Combine(SaveSyncCampaignRoot(campaignId), "r");
            if (!Directory.Exists(root)) return;
            foreach (DirectoryInfo directory in new DirectoryInfo(root).GetDirectories().OrderByDescending(x => x.CreationTimeUtc).Skip(SaveSyncRecoveryRetention))
                TryDeleteDirectory(directory.FullName);
        }

        private static void CleanupAllSaveSyncRecoveryAtStartup()
        {
            string root = SaveSyncRoot();
            if (!Directory.Exists(root)) return;
            foreach (string campaignRoot in Directory.GetDirectories(root))
            {
                string ledgerPath = Path.Combine(campaignRoot, "ledger.json");
                if (!File.Exists(ledgerPath)) continue;
                string campaignId = ReadString(ReadJsonObject(ledgerPath), "campaignId", "");
                if (string.IsNullOrWhiteSpace(campaignId)) continue;
                try { CleanupSaveSyncRecovery(campaignId); } catch { }
            }
        }

        private static string SaveSyncActiveStatePath(string campaignId)
        {
            return Path.Combine(SaveSyncCampaignRoot(campaignId), "active-state.json");
        }

        private static void MarkSaveSyncActiveStateClean(
            string campaignId,
            Dictionary<string, object> point)
        {
            string calendarPath = Path.Combine(StrictCampaignDirectory(campaignId), "world", "calendar.json");
            string calendarSha = File.Exists(calendarPath) ? FileSha256(calendarPath) : "";
            SaveSyncActiveState state = new SaveSyncActiveState
            {
                SavePointId = ReadString(point, "savePointId", ""),
                CalendarSha256 = calendarSha,
                Dirty = false,
                Loaded = true
            };
            lock (SaveSyncActiveStateLock)
            {
                SaveSyncActiveStates[campaignId] = state;
                WriteSaveSyncActiveState(campaignId, state);
            }
        }

        private static void MarkSaveSyncActiveStateDirtyForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            string campaigns;
            try
            {
                full = Path.GetFullPath(path);
                campaigns = Path.GetFullPath(CampaignsRoot())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch { return; }
            if (!full.StartsWith(campaigns, StringComparison.OrdinalIgnoreCase)) return;
            string relative = full.Substring(campaigns.Length);
            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator <= 0) return;
            string campaignId = relative.Substring(0, separator);
            lock (SaveSyncActiveStateLock)
            {
                SaveSyncActiveState state = ReadSaveSyncActiveStateLocked(campaignId);
                if (state.Dirty) return;
                state.Dirty = true;
                WriteSaveSyncActiveState(campaignId, state);
            }
        }

        private static void MarkSaveSyncActiveStateDirty(string campaignId)
        {
            if (string.IsNullOrWhiteSpace(campaignId)) return;
            lock (SaveSyncActiveStateLock)
            {
                SaveSyncActiveState state =
                    ReadSaveSyncActiveStateLocked(campaignId);
                if (state.Dirty) return;
                state.Dirty = true;
                WriteSaveSyncActiveState(campaignId, state);
            }
        }

        private static bool IsSaveSyncActiveStateCleanForPoint(
            string campaignId,
            string savePointId)
        {
            lock (SaveSyncActiveStateLock)
            {
                SaveSyncActiveState state = ReadSaveSyncActiveStateLocked(campaignId);
                if (state.Dirty
                    || !state.SavePointId.Equals(savePointId, StringComparison.OrdinalIgnoreCase))
                    return false;
                string calendarPath = Path.Combine(StrictCampaignDirectory(campaignId), "world", "calendar.json");
                string calendarSha = File.Exists(calendarPath) ? FileSha256(calendarPath) : "";
                return calendarSha.Equals(state.CalendarSha256, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static Dictionary<string, object> SaveSyncCleanActivePoint(
            string campaignId,
            Dictionary<string, object> ledger)
        {
            lock (SaveSyncActiveStateLock)
            {
                SaveSyncActiveState state = ReadSaveSyncActiveStateLocked(campaignId);
                if (state.Dirty || string.IsNullOrWhiteSpace(state.SavePointId))
                    return null;
                string calendarPath = Path.Combine(StrictCampaignDirectory(campaignId), "world", "calendar.json");
                string calendarSha = File.Exists(calendarPath) ? FileSha256(calendarPath) : "";
                if (!calendarSha.Equals(state.CalendarSha256, StringComparison.OrdinalIgnoreCase))
                    return null;
                Dictionary<string, object> point = SaveSyncPoint(ledger, state.SavePointId);
                if (point == null
                    || !Directory.Exists(Path.Combine(SaveSyncSnapshotRoot(campaignId, point), "campaign")))
                    return null;
                return point;
            }
        }

        private static SaveSyncActiveState ReadSaveSyncActiveStateLocked(string campaignId)
        {
            if (SaveSyncActiveStates.TryGetValue(campaignId, out SaveSyncActiveState cached))
                return cached;
            Dictionary<string, object> json = ReadJsonObject(SaveSyncActiveStatePath(campaignId));
            SaveSyncActiveState loaded = new SaveSyncActiveState
            {
                SavePointId = ReadString(json, "savePointId", ""),
                CalendarSha256 = ReadString(json, "calendarSha256", ""),
                Dirty = ReadBool(json, "dirty", true),
                Loaded = true
            };
            SaveSyncActiveStates[campaignId] = loaded;
            return loaded;
        }

        private static void WriteSaveSyncActiveState(string campaignId, SaveSyncActiveState state)
        {
            string path = SaveSyncActiveStatePath(campaignId);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + ".tmp_" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temp, Json.Serialize(new Dictionary<string, object>
            {
                ["format"] = "bannerlord-reign-save-sync-active-state",
                ["version"] = 1,
                ["campaignId"] = campaignId,
                ["savePointId"] = state.SavePointId ?? "",
                ["calendarSha256"] = state.CalendarSha256 ?? "",
                ["dirty"] = state.Dirty,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            }), new UTF8Encoding(false));
            ReplaceSaveSyncFileWithRetry(temp, path);
        }

        private static void ResumePendingSaveSyncOptimizations()
        {
            foreach (string campaignId in SaveSyncCampaignIds())
            {
                Dictionary<string, object> ledger;
                try { ledger = ReadSaveSyncLedger(campaignId); }
                catch { continue; }
                foreach (Dictionary<string, object> point in ReadDictionaryList(ledger, "points")
                    .Where(value => ReadString(value, "snapshotState", "")
                        .Equals("raw_ready", StringComparison.OrdinalIgnoreCase)))
                    QueueSaveSyncOptimization(campaignId, ReadString(point, "savePointId", ""));
            }
        }

        private static void PurgeLegacySaveSyncSnapshotsAtStartup()
        {
            // Kept as the startup entry point, but startup is observational. Missing
            // database connectivity is never authority to delete a native save's files.
            InspectSaveSyncSnapshotsAtStartup(SaveSyncRoot(), ReignPostgreSqlStorage.SnapshotExists);
        }

        private static void InspectSaveSyncSnapshotsAtStartup(string root,
            Func<string, string, bool> snapshotExists)
        {
            if (!Directory.Exists(root)) return;
            foreach (string campaignRoot in Directory.GetDirectories(root))
            {
                string ledgerPath = Path.Combine(campaignRoot, "ledger.json");
                if (!File.Exists(ledgerPath)) continue;
                Dictionary<string, object> ledger = ReadJsonObject(ledgerPath);
                string campaignId = ReadString(ledger, "campaignId", "");
                foreach (Dictionary<string, object> point in ReadDictionaryList(ledger, "points"))
                {
                    string pointId = SaveSyncPostgreSqlSnapshotPointId(point);
                    try
                    {
                        if (snapshotExists(campaignId, pointId)) continue;
                        LogOperational("save_sync.startup_snapshot_unavailable", new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId, ["savePointId"] = pointId,
                            ["preserved"] = true
                        });
                    }
                    catch (Exception ex)
                    {
                        LogOperational("save_sync.startup_snapshot_check_deferred", new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId, ["savePointId"] = pointId,
                            ["preserved"] = true, ["errorType"] = ex.GetType().Name
                        });
                    }
                }
                // Legacy and modern snapshots both remain available for explicit,
                // confirmation-gated recovery or retirement. Never rewrite their ledger here.
            }
        }
        private static Dictionary<string, object> SaveSyncDisabledResult(Dictionary<string, object> payload)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["enabled"] = false, ["result"] = "disabled", ["noOp"] = true,
                ["campaignId"] = ReadString(payload, "campaignId", ""), ["savePointId"] = ReadString(payload, "savePointId", ""),
                ["message"] = "Save Sync is disabled. Reign campaign data was left untouched."
            };
        }

        private static bool IsSaveSyncEnabled()
        {
            return SaveSyncEnabledOverrideForTests ?? ReadBool(LoadSettings(), "saveSyncEnabled", true);
        }

        private static void ValidateSaveSyncId(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 180 || !Regex.IsMatch(value, "^[A-Za-z0-9_-]+$") || value == "." || value == "..")
                throw new InvalidDataException(name + " must contain only letters, numbers, underscores, or hyphens.");
        }

        private static string StrictCampaignDirectory(string campaignId)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            string root = Path.GetFullPath(CampaignsRoot());
            string path = Path.GetFullPath(Path.Combine(root, campaignId));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Campaign path escaped the campaign storage root.");
            return path;
        }

        private static string SaveSyncRoot()
        {
            return ResolveSaveSyncRoot(
                Environment.GetEnvironmentVariable("REIGN_SAVE_SYNC_ROOT"),
                Path.Combine(DataDir, "ss"));
        }

        private static string ResolveSaveSyncRoot(string configuredRoot, string fallbackRoot)
        {
            string candidate = string.IsNullOrWhiteSpace(configuredRoot)
                ? fallbackRoot
                : Environment.ExpandEnvironmentVariables(configuredRoot.Trim());
            if (string.IsNullOrWhiteSpace(candidate))
                throw new InvalidDataException("Save Sync storage root is empty.");
            return Path.GetFullPath(candidate);
        }

        private static string SaveSyncCampaignWorkRoot(
            string campaignDirectory,
            string operationId,
            string uniqueSuffix)
        {
            string campaignPath = Path.GetFullPath(campaignDirectory ?? "");
            string campaignsRoot = Path.GetDirectoryName(campaignPath);
            if (string.IsNullOrWhiteSpace(campaignsRoot))
                throw new InvalidDataException(
                    "Save Sync campaign directory has no parent storage root.");
            return Path.Combine(
                campaignsRoot,
                ".save-sync-work",
                (operationId ?? "operation") + "_" + (uniqueSuffix ?? "work"));
        }

        private static string SaveSyncStorageKey(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                string hex = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
                return hex.Substring(0, 12);
            }
        }

        private static string SaveSyncCampaignRoot(string campaignId)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            string root = Path.GetFullPath(SaveSyncRoot());
            string path = Path.GetFullPath(Path.Combine(root, SaveSyncStorageKey(campaignId)));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Save Sync path escaped its storage root.");
            return path;
        }

        private static string SaveSyncPointRoot(string campaignId, string savePointId)
        {
            ValidateSaveSyncId(savePointId, "savePointId");
            return Path.Combine(SaveSyncCampaignRoot(campaignId), "p", SaveSyncStorageKey(savePointId));
        }

        private static string SaveSyncContentRoot(string campaignId, string storageId)
        {
            if (string.IsNullOrWhiteSpace(storageId)
                || storageId.Length != 12
                || storageId.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException("Invalid Save Sync content storage ID.");
            return Path.Combine(SaveSyncCampaignRoot(campaignId), "c", storageId.ToLowerInvariant());
        }

        private static string SaveSyncSnapshotRoot(string campaignId, Dictionary<string, object> point)
        {
            string storageId = ReadString(point, "snapshotStorageId", "");
            return string.IsNullOrWhiteSpace(storageId)
                ? SaveSyncPointRoot(campaignId, ReadString(point, "savePointId", ""))
                : SaveSyncContentRoot(campaignId, storageId);
        }

        private static List<Dictionary<string, object>> RunSaveSyncSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "save_sync", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            string campaignId = "stx_" + token;
            string siblingId = "sss_" + token;
            string preexistingId = "ssp_" + token;
            string immediateRetirementId = "ssi_" + token;
            string activeRetirementId = "ssa_" + token;
            string unmatchedRetirementId = "ssu_" + token;
            string recoveredFailureId = "ssr_" + token;
            string residualCleanupId = "ssx_" + token;
            string lockedCleanupId = "ssl_" + token;
            string databaseOnlyRetirementId = "ssb_" + token;
            string orphanRootId = "sso_" + token;
            string currentSlotOwnerId = "ssn_" + token;
            string supersededSlotOwnerId = "ssv_" + token;
            string oldPoint = "old_" + token;
            string newPoint = "new_" + token;
            string startupRoot = Path.Combine(Path.GetTempPath(), "ReignSaveSyncStartup_" + token);
            try
            {
                string household = Path.Combine(startupRoot, "campaign");
                string payloadRoot = Path.Combine(household, "c", "retained", "campaign");
                Directory.CreateDirectory(payloadRoot);
                string retainedFile = Path.Combine(payloadRoot, "state.json");
                File.WriteAllText(retainedFile, "immutable baseline payload");
                string ledgerPath = Path.Combine(household, "ledger.json");
                string ledger = Json.Serialize(new Dictionary<string, object>
                {
                    ["version"] = 5, ["campaignId"] = campaignId,
                    ["points"] = new[] { new Dictionary<string, object>
                    {
                        ["savePointId"] = oldPoint, ["snapshotStorageId"] = "retained",
                        ["status"] = "ready"
                    } }
                });
                File.WriteAllText(ledgerPath, ledger);
                int failedProbes = 0;
                InspectSaveSyncSnapshotsAtStartup(startupRoot, (campaign, point) =>
                {
                    failedProbes++;
                    throw new InvalidOperationException("Simulated database startup outage");
                });
                add("startup_database_failure_preserves_save_files",
                    failedProbes == 1 && File.ReadAllText(ledgerPath) == ledger
                    && File.ReadAllText(retainedFile) == "immutable baseline payload",
                    "A failed database probe preserves every registered baseline file and ledger byte.");
                string legacyLedger = ledger.Replace("\"version\":5", "\"version\":3");
                File.WriteAllText(ledgerPath, legacyLedger);
                InspectSaveSyncSnapshotsAtStartup(startupRoot, (campaign, point) => false);
                add("startup_missing_snapshot_preserves_legacy_recovery",
                    File.ReadAllText(ledgerPath) == legacyLedger
                    && File.ReadAllText(retainedFile) == "immutable baseline payload",
                    "A genuinely missing PostgreSQL snapshot preserves legacy files for explicit recovery; startup does not retire saves.");
            }
            finally
            {
                if (Directory.Exists(startupRoot)) Directory.Delete(startupRoot, true);
            }
            string archivePath = "";
            string nativeSaveFixture = Path.Combine(
                TestsDir, "native_save_inventory_" + token);
            SaveSyncEnabledOverrideForTests = true;
            SaveSyncSkipSemanticForTests = true;
            SaveSyncDisableBackgroundOptimizationForTests = true;
            CampaignMutationAllowDuringTests = true;
            try
            {
                string fallbackRoot = Path.Combine(DataDir, "ss");
                string stableRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bannerlord Reign",
                    "save-sync");
                string stableRuntimeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bannerlord Reign");
                add("configured_runtime_root_is_deployment_independent",
                    ResolveRuntimeDataDirectory(stableRuntimeRoot, fallbackRoot)
                        == Path.GetFullPath(stableRuntimeRoot)
                    && ResolveRuntimeDataDirectory("", fallbackRoot)
                        == Path.GetFullPath(fallbackRoot),
                    "Packaged binaries can move or update without relocating settings, campaign sidecars, logs, backups, or Save Sync state.");
                string migrationTestRoot = Path.Combine(
                    TestsDir,
                    "runtime_migration_" + token);
                try
                {
                    string migrationSource = Path.Combine(migrationTestRoot, "source");
                    string migrationTarget = Path.Combine(migrationTestRoot, "target");
                    Directory.CreateDirectory(Path.Combine(migrationSource, "campaigns"));
                    Directory.CreateDirectory(Path.Combine(migrationTarget, "campaigns"));
                    File.WriteAllText(
                        Path.Combine(migrationSource, "settings.json"),
                        "legacy",
                        Encoding.UTF8);
                    File.WriteAllText(
                        Path.Combine(migrationSource, "campaigns", "new.json"),
                        "new",
                        Encoding.UTF8);
                    File.WriteAllText(
                        Path.Combine(migrationSource, "campaigns", "existing.json"),
                        "legacy-existing",
                        Encoding.UTF8);
                    File.WriteAllText(
                        Path.Combine(migrationTarget, "campaigns", "existing.json"),
                        "current-existing",
                        Encoding.UTF8);
                    CopyDirectoryMissingOnly(migrationSource, migrationTarget);
                    add("runtime_migration_preserves_current_files",
                        File.ReadAllText(Path.Combine(
                            migrationTarget, "campaigns", "existing.json"))
                                == "current-existing"
                        && File.ReadAllText(Path.Combine(
                            migrationTarget, "campaigns", "new.json")) == "new"
                        && File.ReadAllText(Path.Combine(
                            migrationTarget, "settings.json")) == "legacy",
                        "One-time runtime migration copies missing state while preserving settings and campaign files already present in the stable per-user root.");
                }
                finally
                {
                    TryDeleteDirectory(migrationTestRoot);
                }
                add("configured_storage_root_is_deployment_independent",
                    ResolveSaveSyncRoot(stableRoot, fallbackRoot) == Path.GetFullPath(stableRoot)
                    && ResolveSaveSyncRoot("", fallbackRoot) == Path.GetFullPath(fallbackRoot),
                    "Supported launchers can pin Save Sync manifests and ledgers to one stable per-user directory while isolated verification retains its artifact-local fallback.");
                string simulatedInstalledCampaign = Path.Combine(
                    @"D:\ReignRuntime", "campaigns", campaignId);
                string simulatedWorkRoot = SaveSyncCampaignWorkRoot(
                    simulatedInstalledCampaign, "restore", "test");
                add("restore_work_stays_on_campaign_volume",
                    string.Equals(
                        Path.GetPathRoot(simulatedInstalledCampaign),
                        Path.GetPathRoot(simulatedWorkRoot),
                        StringComparison.OrdinalIgnoreCase)
                    && simulatedWorkRoot.IndexOf(
                        ".save-sync-work",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                    "Restore staging is created beside the active campaign, allowing atomic directory swaps even when the durable Save Sync snapshot is stored on another drive.");

                string preexistingCampaign = StrictCampaignDirectory(preexistingId);
                Directory.CreateDirectory(Path.Combine(preexistingCampaign, "world"));
                File.WriteAllText(Path.Combine(preexistingCampaign, "world", "history.jsonl"), "{}" + Environment.NewLine, Encoding.UTF8);
                using (ReignDbConnection preexistingConnection =
                    OpenCampaignConnection(preexistingId))
                {
                    ExecuteSql(preexistingConnection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('preexisting_test','ready');");
                }
                Dictionary<string, object> preexistingPayload = SaveSyncTestPointPayload(preexistingId, "pre_" + token, 1d, "Preexisting World History");
                preexistingPayload["campaignLabel"] = "Preexisting World History";
                Dictionary<string, object> preexistingRegistered = SaveSyncRegisterApi(preexistingPayload);
                Dictionary<string, object> preexistingMetadata = ReadJsonObject(Path.Combine(preexistingCampaign, "campaign.json"));
                add("preexisting_campaign_directory_gets_metadata", ReadBool(preexistingRegistered, "ok", false)
                    && ReadString(preexistingMetadata, "campaignId", "") == preexistingId
                    && ReadString(preexistingMetadata, "campaignLabel", "") == "Preexisting World History",
                    "Save registration creates campaign metadata even when world-history traffic created the campaign directory first.");
                Dictionary<string, object> missingUnregisteredPayload =
                    SaveSyncTestPointPayload(preexistingId, "missing_unregistered_" + token, 2d, "Unregistered Save");
                missingUnregisteredPayload["registrationConfirmed"] = false;
                Dictionary<string, object> missingUnregistered = SaveSyncLoadApi(missingUnregisteredPayload, false);
                Dictionary<string, object> missingConfirmedPayload =
                    SaveSyncTestPointPayload(preexistingId, "missing_confirmed_" + token, 2d, "Corrupt Registered Save");
                missingConfirmedPayload["registrationConfirmed"] = true;
                Dictionary<string, object> missingConfirmed = SaveSyncLoadApi(missingConfirmedPayload, false);
                add("unregistered_native_save_releases_alignment_gate",
                    ReadBool(missingUnregistered, "ok", false)
                    && ReadString(missingUnregistered, "result", "") == "unregistered_save_baseline"
                    && ReadBool(missingUnregistered, "degraded", false)
                    && !ReadBool(missingConfirmed, "ok", true)
                    && ReadString(missingConfirmed, "result", "") == "snapshot_missing",
                    "An honestly unregistered native save continues from current Reign state, while a missing confirmed snapshot remains a corruption error.");
                ManualResetEventSlim finalizeReaderReady = new ManualResetEventSlim(false);
                Thread finalizeReader = new Thread(() =>
                {
                    CampaignDataGate.EnterReadLock();
                    try
                    {
                        finalizeReaderReady.Set();
                        Thread.Sleep(1500);
                    }
                    finally { CampaignDataGate.ExitReadLock(); }
                });
                finalizeReader.IsBackground = true;
                finalizeReader.Start();
                finalizeReaderReady.Wait();
                Stopwatch missingFinalizeTimer = Stopwatch.StartNew();
                Dictionary<string, object> missingFinalize = SaveSyncFinalizeApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = preexistingId,
                        ["savePointId"] = "never_registered_" + token,
                        ["successful"] = true,
                        ["nativeSaveName"] = "Unregistered Save"
                    });
                missingFinalizeTimer.Stop();
                finalizeReader.Join();
                add("unregistered_finalize_never_waits_for_campaign_write_gate",
                    !ReadBool(missingFinalize, "ok", true)
                    && ReadString(missingFinalize, "result", "") == "snapshot_missing"
                    && missingFinalizeTimer.ElapsedMilliseconds < 500,
                    "Finalizing a point that was never registered returns immediately without queueing a global writer behind active campaign readers.");

                PrepareSaveSyncTestCampaign(campaignId, "old");
                PrepareSaveSyncTestCampaign(siblingId, "sibling");
                string siblingFile = Path.Combine(StrictCampaignDirectory(siblingId), "world", "history.jsonl");
                string siblingHash = FileSha256(siblingFile);
                Dictionary<string, object> oldPayload = SaveSyncTestPointPayload(campaignId, oldPoint, 10d, "Old Save");
                Dictionary<string, object> oldRegistered = SaveSyncRegisterApi(oldPayload);
                Dictionary<string, object> rawOldPoint = SaveSyncPoint(ReadSaveSyncLedger(campaignId), oldPoint);
                add("register_returns_load_ready_raw_generation",
                    ReadBool(oldRegistered, "ok", false)
                    && ReadString(rawOldPoint, "snapshotState", "") == "postgresql_ready"
                    && ReignPostgreSqlStorage.SnapshotExists(
                        campaignId, oldPoint)
                    && Directory.Exists(Path.Combine(SaveSyncSnapshotRoot(campaignId, rawOldPoint), "campaign")),
                    "Registration returns only after its PostgreSQL snapshot and campaign assets are load-ready.");
                Dictionary<string, object> oldStoredPoint = SaveSyncPoint(ReadSaveSyncLedger(campaignId), oldPoint);
                string oldStoredRoot = SaveSyncSnapshotRoot(campaignId, oldStoredPoint);
                add("register_old_save_point", ReadBool(oldRegistered, "ok", false) && Directory.Exists(Path.Combine(oldStoredRoot, "campaign")), "Old native save point receives an immutable complete snapshot.");
                add("snapshot_excludes_rebuildable_portrait_roster",
                    !File.Exists(Path.Combine(oldStoredRoot, "campaign", "portrait_roster.json")),
                    "Save Sync excludes the rebuildable portrait roster from every native-save snapshot.");
                add("postgresql_snapshot_registered",
                    ReignPostgreSqlStorage.SnapshotExists(
                        campaignId, oldPoint)
                    && ReignPostgreSqlStorage.SnapshotSizeBytes(
                        campaignId, oldPoint) > 0,
                    "Save Sync registers a non-empty immutable PostgreSQL schema for the native save.");
                string duplicatePoint = "duplicate_" + token;
                Dictionary<string, object> duplicatePayload = SaveSyncTestPointPayload(campaignId, duplicatePoint, 11d, "Duplicate Save");
                Dictionary<string, object> duplicateRegistered = SaveSyncRegisterApi(duplicatePayload);
                Dictionary<string, object> duplicateLedger = ReadSaveSyncLedger(campaignId);
                Dictionary<string, object> duplicateStoredPoint = SaveSyncPoint(duplicateLedger, duplicatePoint);
                add("identical_snapshots_share_physical_storage",
                    ReadBool(duplicateRegistered, "ok", false)
                    && SaveSyncUniqueStateCount(duplicateLedger) == 1
                    && ReadString(oldStoredPoint, "snapshotStorageId", "") == ReadString(duplicateStoredPoint, "snapshotStorageId", "")
                    && SaveSyncPostgreSqlSnapshotPointId(oldStoredPoint)
                        == SaveSyncPostgreSqlSnapshotPointId(duplicateStoredPoint)
                    && !ReignPostgreSqlStorage.SnapshotExists(
                        campaignId, duplicatePoint)
                    && Directory.GetDirectories(Path.Combine(SaveSyncCampaignRoot(campaignId), "c")).Length == 1,
                    "Distinct native save identities with identical Reign state share both one PostgreSQL schema and one asset snapshot.");
                Dictionary<string, object> duplicateDeleted = SaveSyncDeleteNativeSaveApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["savePointId"] = duplicatePoint, ["nativeSaveName"] = "Duplicate Save"
                });
                add("native_save_deletion_releases_snapshot_reference",
                    ReadString(duplicateDeleted, "result", "") == "deleted"
                    && SaveSyncPoint(ReadSaveSyncLedger(campaignId), duplicatePoint) == null
                    && Directory.Exists(oldStoredRoot),
                    "Deleting a native save removes its point while preserving shared snapshot content still referenced by another native save.");

                Dictionary<string, object> capacityLedger = new Dictionary<string, object>
                {
                    ["points"] = Enumerable.Range(0, SaveSyncUniqueStateLimit)
                        .Select(index => new Dictionary<string, object>
                        {
                            ["savePointId"] = "capacity_" + index.ToString(CultureInfo.InvariantCulture),
                            ["snapshotFingerprint"] = index.ToString("x64", CultureInfo.InvariantCulture)
                        }).ToList()
                };
                add("unique_state_capacity_contract",
                    SaveSyncUniqueStateCount(capacityLedger) == 15
                    && SaveSyncUniqueStateWarning == 10
                    && SaveSyncUniqueStateLimit - SaveSyncUniqueStateWarning == 5,
                    "Save Sync warns with five slots remaining at ten unique states and enforces a fifteen-state ceiling without an age limit.");
                ((List<Dictionary<string, object>>)capacityLedger["points"])[0]["nativeSaveName"] =
                    "Qualification Rotation A";
                Dictionary<string, object> rotatingIncoming =
                    new Dictionary<string, object>
                    {
                        ["nativeSaveName"] = "Qualification Rotation A"
                    };
                Dictionary<string, object> unrelatedIncoming =
                    new Dictionary<string, object>
                    {
                        ["nativeSaveName"] = "Unrelated New Save"
                    };
                add("native_save_rotation_reserves_superseded_slot_at_capacity",
                    SaveSyncEffectiveUniqueStateCountForRegistration(
                        capacityLedger,
                        rotatingIncoming) == 14
                    && SaveSyncEffectiveUniqueStateCountForRegistration(
                        capacityLedger,
                        unrelatedIncoming) == 15,
                    "Overwriting a known native save can register safely at capacity while an unrelated sixteenth state remains blocked.");

                AppendSaveSyncTestFutureState(campaignId);
                Dictionary<string, object> newPayload = SaveSyncTestPointPayload(campaignId, newPoint, 20d, "Newest Save");
                Dictionary<string, object> newRegistered = SaveSyncRegisterApi(newPayload);
                add("register_divergent_newer_save", ReadBool(newRegistered, "ok", false), "A second divergent native save from the same campaign receives its own snapshot.");

                Dictionary<string, object> newest = SaveSyncLoadApi(newPayload, false);
                add("newest_save_no_op", ReadBool(newest, "ok", false) && ReadBool(newest, "noOp", false) && ReadString(newest, "result", "") == "no_op", "Loading the newest unchanged save is a zero-write no-op.");

                SaveSyncFailureInjectionForTests = "after_install";
                Dictionary<string, object> failed = SaveSyncLoadApi(oldPayload, false);
                SaveSyncFailureInjectionForTests = "";
                add("failed_rollback_recovers_active_state", !ReadBool(failed, "ok", true) && ReadString(failed, "result", "") == "failed_recovered" && SaveSyncTestMarkerExists(campaignId, "future"), "An injected mid-swap failure restores the exact pre-sync campaign and retains a recovery snapshot.");

                string loadSessionId = "load_session_" + token;
                Dictionary<string, object> loadSessionPayload =
                    new Dictionary<string, object>(oldPayload)
                    {
                        ["loadSessionId"] = loadSessionId
                    };
                Dictionary<string, object> rolledBack = SaveSyncLoadApi(loadSessionPayload, false);
                Dictionary<string, object> rollbackCounts = ReadDictionary(rolledBack, "countsBySubsystem") ?? new Dictionary<string, object>();
                bool affectedCountsAuditable = new[] { "conversations", "events", "memories", "relationships", "worldHistory", "rumors", "actions", "diplomacy", "rebellions", "court", "reputation", "characterState" }
                    .All(subsystem => ReadLong(ReadDictionary(rollbackCounts, subsystem), "removed", 0) > 0);
                bool storesClean = !SaveSyncTestMarkerExists(campaignId, "future")
                    && SaveSyncTestFutureSqlRows(campaignId) == 0
                    && SaveSyncTestSqlValue(campaignId, "SELECT affinity_a_to_b FROM relationship_pair_chemistry WHERE pair_key='hero_a|hero_b';", "affinity_a_to_b") == "1"
                    && SaveSyncTestSqlValue(campaignId, "SELECT reputation_value FROM character_reputations WHERE campaign_id=$campaign AND subject_id='hero_a' AND tag_id='test_reputation';", "reputation_value", campaignId) == "1"
                    && affectedCountsAuditable;
                add("older_save_rolls_back_all_temporal_stores", ReadBool(rolledBack, "ok", false) && ReadString(rolledBack, "result", "") == "rolled_back" && storesClean, "Older-save apply removes future conversations, events, memories, relationships, world history, rumors, actions, diplomacy, rebellions, court/reputation state, and character runtime state.");
                add("pre_sync_backup_created", ReadBool(rolledBack, "backupCreated", false) && Directory.Exists(ReadString(rolledBack, "backupPath", "")), "Destructive apply creates a retained pre-sync recovery snapshot.");

                MarkSaveSyncActiveStateDirty(campaignId);
                Dictionary<string, object> replayedLoad =
                    SaveSyncLoadApi(loadSessionPayload, false);
                add("timed_out_load_retry_replays_completed_receipt",
                    ReadBool(replayedLoad, "ok", false)
                    && ReadBool(replayedLoad, "idempotentReplay", false)
                    && ReadString(replayedLoad, "loadSessionId", "") == loadSessionId
                    && ReadString(replayedLoad, "backupId", "")
                        == ReadString(rolledBack, "backupId", "")
                    && ReadString(replayedLoad, "result", "")
                        == ReadString(rolledBack, "result", ""),
                    "A retry from the same native load generation replays the durable completed receipt even if the client timed out and the active-state marker changed afterward; it never performs a second rollback.");

                string background = File.ReadAllText(Path.Combine(StrictCampaignDirectory(campaignId), "characters", "hero_a", "background.json"));
                byte[] portrait = File.ReadAllBytes(Path.Combine(StrictCampaignDirectory(campaignId), "characters", "hero_a", "portraits", "portrait.png"));
                string audit = File.ReadAllText(Path.Combine(StrictCampaignDirectory(campaignId), "audit", "audit.jsonl"));
                string liveTestEvidence = File.ReadAllText(Path.Combine(StrictCampaignDirectory(campaignId), "tests", "live-interaction", "runs", "future.json"));
                Dictionary<string, object> retainedPortraitRoster = ReadJsonObject(
                    Path.Combine(StrictCampaignDirectory(campaignId), "portrait_roster.json"));
                add("static_assets_and_technical_audit_retained", background.Contains("later static definition") && portrait.SequenceEqual(new byte[] { 9, 8, 7, 6 }) && audit.Contains("future technical audit"), "Reusable existing-character definitions, portrait assets, and technical audit history survive temporal rollback.");
                add("portrait_roster_retained_across_rollback",
                    ReadString(retainedPortraitRoster, "rosterFingerprint", "") == "future",
                    "The live campaign portrait roster survives rollback even though it is excluded from temporal snapshots.");
                add("qualification_evidence_retained",
                    liveTestEvidence.Contains("future qualification evidence")
                    && !File.Exists(Path.Combine(oldStoredRoot, "campaign", "tests", "live-interaction", "runs", "future.json")),
                    "Live-test and readiness evidence is non-temporal: snapshots exclude it and rollback overlays the current qualification records.");

                Dictionary<string, object> repeated = SaveSyncLoadApi(oldPayload, false);
                add("repeated_older_load_idempotent", ReadBool(repeated, "ok", false) && !SaveSyncTestMarkerExists(campaignId, "future"), "Repeated loading of the same older save point converges on the same temporal state despite retained static assets and audit data.");

                AppendSaveSyncTestLine(campaignId, "actions", "disabled_marker");
                SaveSyncEnabledOverrideForTests = false;
                string disabledHash = SaveSyncActiveFingerprintForTests(campaignId);
                Dictionary<string, object> disabled = SaveSyncLoadApi(oldPayload, false);
                add("disabled_leaves_campaign_untouched", ReadString(disabled, "result", "") == "disabled" && disabledHash == SaveSyncActiveFingerprintForTests(campaignId), "Save Sync disabled returns before any campaign read/write snapshot or rollback mutation.");
                SaveSyncEnabledOverrideForTests = true;

                Dictionary<string, object> forward = SaveSyncLoadApi(newPayload, false);
                add("forward_load_restores_divergent_snapshot", ReadBool(forward, "ok", false) && SaveSyncTestMarkerExists(campaignId, "future"), "After loading backward, loading the newer save restores its preserved divergent future snapshot.");
                int pointCountBeforeRename = ReadDictionaryList(ReadSaveSyncLedger(campaignId), "points").Count;
                Dictionary<string, object> copied = SaveSyncLoadApi(new Dictionary<string, object>(newPayload) { ["nativeSaveName"] = "Renamed Copy" }, false);
                int pointCountAfterRename = ReadDictionaryList(ReadSaveSyncLedger(campaignId), "points").Count;
                add("copied_or_renamed_save_uses_durable_identity", ReadBool(copied, "ok", false) && SaveSyncTestMarkerExists(campaignId, "future") && pointCountBeforeRename == pointCountAfterRename, "Changing the visible native slot name does not change the serialized save-point identity or create a duplicate point.");
                add("cross_campaign_isolation", FileSha256(siblingFile) == siblingHash, "Save-point registration and backward/forward restores never write to a sibling campaign.");

                Directory.CreateDirectory(nativeSaveFixture);
                string oldNativeSavePath = Path.Combine(
                    nativeSaveFixture, "Old Save.sav");
                string newestNativeSavePath = Path.Combine(
                    nativeSaveFixture, "Newest Save.sav");
                byte[] oldNativeSaveBytes = new byte[] { 1, 3, 5, 7 };
                byte[] newestNativeSaveBytes = new byte[] { 2, 4, 6, 8 };
                File.WriteAllBytes(oldNativeSavePath, oldNativeSaveBytes);
                File.WriteAllBytes(newestNativeSavePath,
                    newestNativeSaveBytes);
                CampaignNativeSaveRootOverrideForTests = nativeSaveFixture;
                Dictionary<string, object> export = CreateCampaignBackupArchive(campaignId);
                archivePath = ReadString(export, "path", "");
                bool archiveReady = ReadBool(export, "ok", false) && File.Exists(archivePath);
                string upload = Path.Combine(DataDir, "backups", "uploads", Guid.NewGuid().ToString("N") + ".reignbackup");
                Directory.CreateDirectory(Path.GetDirectoryName(upload));
                if (archiveReady) File.Copy(archivePath, upload, true);
                TryDeleteFile(oldNativeSavePath);
                TryDeleteFile(newestNativeSavePath);
                TryDeleteDirectory(StrictCampaignDirectory(campaignId));
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                Dictionary<string, object> imported = archiveReady ? ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "false" }) : new Dictionary<string, object>();
                Dictionary<string, object> importedLedger = ReadSaveSyncLedger(campaignId);
                bool roundTrip = ReadBool(imported, "ok", false) && SaveSyncPoint(importedLedger, oldPoint) != null && SaveSyncPoint(importedLedger, newPoint) != null
                    && Directory.Exists(Path.Combine(oldStoredRoot, "campaign"))
                    && File.Exists(oldNativeSavePath)
                    && File.Exists(newestNativeSavePath)
                    && File.ReadAllBytes(oldNativeSavePath)
                        .SequenceEqual(oldNativeSaveBytes)
                    && File.ReadAllBytes(newestNativeSavePath)
                        .SequenceEqual(newestNativeSaveBytes)
                    && ReadInt(imported, "nativeSavesRestored", 0) == 2;
                add("export_import_preserves_save_sync", roundTrip, roundTrip ? "Campaign export/import round-trips the ledger, immutable save snapshots, status, recovery metadata, and exact native Bannerlord saves." : "Save Sync backup round trip failed. Export=" + Json.Serialize(export) + " Import=" + Json.Serialize(imported));
                TryDeleteFile(oldNativeSavePath);
                TryDeleteFile(newestNativeSavePath);

                bool defaultEnabled = ReadBool(DefaultSettings(), "saveSyncEnabled", false);
                string html = ControlCenterHtml();
                add("default_enabled_ui_and_preview_contract", defaultEnabled && html.Contains("id='saveSyncEnabled'") && html.Contains("previewSaveSync") && html.Contains("Last loaded point"), "The Campaign Backups UI exposes the default-on toggle, last result/backup status, and manual dry-run preview.");

                File.WriteAllText(Path.Combine(nativeSaveFixture,
                    "Shared Slot.sav"), "native", Encoding.UTF8);
                PrepareCampaignRetirementTestPoint(
                    supersededSlotOwnerId,
                    "superseded_" + token,
                    "Shared Slot");
                Dictionary<string, object> supersededLedger =
                    ReadSaveSyncLedger(supersededSlotOwnerId);
                ReadDictionaryList(supersededLedger, "points")[0]
                    ["registeredUtc"] = "2026-01-01T00:00:00.0000000Z";
                WriteSaveSyncLedger(supersededSlotOwnerId,
                    supersededLedger);
                PrepareCampaignRetirementTestPoint(
                    currentSlotOwnerId,
                    "current_" + token,
                    "Shared Slot");
                Dictionary<string, object> currentOwnerLedger =
                    ReadSaveSyncLedger(currentSlotOwnerId);
                ReadDictionaryList(currentOwnerLedger, "points")[0]
                    ["registeredUtc"] = "2026-01-02T00:00:00.0000000Z";
                WriteSaveSyncLedger(currentSlotOwnerId,
                    currentOwnerLedger);
                CampaignNativeSaveRootOverrideForTests = nativeSaveFixture;
                Dictionary<string, object> physicalAudit =
                    CampaignStorageAuditCore();
                Dictionary<string, object> currentOwnerAudit =
                    ReadDictionaryList(physicalAudit, "campaigns")
                        .FirstOrDefault(row => ReadString(
                            row, "campaignId", "") == currentSlotOwnerId);
                Dictionary<string, object> supersededOwnerAudit =
                    ReadDictionaryList(physicalAudit, "campaigns")
                        .FirstOrDefault(row => ReadString(
                            row, "campaignId", "") == supersededSlotOwnerId);
                Dictionary<string, object> physicalInventory =
                    ReadDictionary(physicalAudit, "nativeSaveInventory");
                add("physical_native_save_inventory_is_authoritative",
                    currentOwnerAudit != null
                    && ReadInt(currentOwnerAudit,
                        "remainingSavePoints", 0) == 1
                    && supersededOwnerAudit != null
                    && ReadInt(supersededOwnerAudit,
                        "remainingSavePoints", -1) == 0
                    && ReadBool(supersededOwnerAudit,
                        "eligibleForPermanentDeletion", false)
                    && ReadInt(physicalInventory,
                        "physicalSaveCount", 0) == 1,
                    "Storage audit counts real .sav slots and assigns an overwritten slot only to its newest Reign registration; stored snapshots alone never masquerade as native saves.");
                CampaignNativeSaveRootOverrideForTests = Path.Combine(
                    nativeSaveFixture, "missing");
                Dictionary<string, object> unavailableAudit =
                    ReadDictionaryList(CampaignStorageAuditCore(), "campaigns")
                        .FirstOrDefault(row => ReadString(
                            row, "campaignId", "") == currentSlotOwnerId);
                add("unavailable_native_save_inventory_preserves_campaigns",
                    unavailableAudit != null
                    && ReadString(unavailableAudit,
                        "storageCategory", "") ==
                            "native_save_inventory_unavailable"
                    && !ReadBool(unavailableAudit,
                        "eligibleForPermanentDeletion", true),
                    "A missing or unavailable Bannerlord save directory blocks cleanup instead of treating every campaign as deleted.");
                CampaignNativeSaveRootOverrideForTests = nativeSaveFixture;
                Dictionary<string, object> finalizedSlotOwner =
                    SaveSyncFinalizeApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = currentSlotOwnerId,
                        ["savePointId"] = "current_" + token,
                        ["nativeSaveName"] = "Shared Slot",
                        ["successful"] = true
                    });
                add("cross_campaign_native_slot_overwrite_retires_old_owner",
                    ReadBool(finalizedSlotOwner, "ok", false)
                    && ReadInt(finalizedSlotOwner,
                        "supersededSavePointsDeleted", 0) == 1
                    && ReadInt(finalizedSlotOwner,
                        "supersededCampaignsDeleted", 0) == 1
                    && !ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") ==
                            supersededSlotOwnerId),
                    "Finalizing a native slot removes the same slot from an older campaign and retires that old campaign when the overwritten slot was its final save.");

                PrepareCampaignRetirementTestPoint(
                    immediateRetirementId, "immediate_" + token, "Immediate Save");
                string immediatePortrait = CampaignPortraitCacheDirectory(
                    immediateRetirementId);
                Directory.CreateDirectory(immediatePortrait);
                File.WriteAllText(Path.Combine(immediatePortrait, "portrait.txt"),
                    "campaign-owned", Encoding.UTF8);
                Dictionary<string, object> immediateDeleted =
                    SaveSyncDeleteNativeSaveApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = immediateRetirementId,
                        ["savePointId"] = "immediate_" + token,
                        ["nativeSaveName"] = "Immediate Save",
                        ["campaignLoaded"] = false
                    });
                bool immediateGone = !ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == immediateRetirementId)
                    && !Directory.Exists(StrictCampaignDirectory(immediateRetirementId))
                    && !Directory.Exists(SaveSyncCampaignRoot(immediateRetirementId))
                    && !Directory.Exists(immediatePortrait);
                add("final_save_deletes_unloaded_campaign",
                    ReadBool(immediateDeleted, "campaignDeleted", false)
                    && !ReadBool(immediateDeleted, "campaignDeletionPending", true)
                    && ReadInt(immediateDeleted, "remainingSavePoints", -1) == 0
                    && immediateGone,
                    "Deleting an unloaded campaign's final registered native save atomically retires its database, campaign files, Save Sync storage, and portrait cache.");
                Dictionary<string, object> immediateRetry =
                    SaveSyncDeleteNativeSaveApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = immediateRetirementId,
                        ["savePointId"] = "immediate_" + token,
                        ["nativeSaveName"] = "Immediate Save",
                        ["campaignLoaded"] = false
                    });
                add("final_save_deletion_is_idempotent",
                    ReadString(immediateRetry, "result", "") == "not_found"
                    && !ReadBool(immediateRetry, "campaignDeleted", true),
                    "A retried native deletion receipt cannot recreate or re-delete a retired campaign.");

                using (ReignDbConnection databaseOnlyConnection =
                    OpenCampaignConnection(databaseOnlyRetirementId)) { }
                TryDeleteDirectory(StrictCampaignDirectory(
                    databaseOnlyRetirementId));
                Dictionary<string, object> databaseOnlyDeleted;
                SaveSyncSkipSemanticForTests = false;
                try
                {
                    databaseOnlyDeleted = DeleteCampaignOwnedDataCore(
                        databaseOnlyRetirementId, "Database Only", true,
                        "test_database_only");
                }
                finally { SaveSyncSkipSemanticForTests = true; }
                add("database_only_retirement_does_not_recreate_campaign",
                    ReadBool(databaseOnlyDeleted, "campaignDeleted", false)
                    && !Directory.Exists(StrictCampaignDirectory(
                        databaseOnlyRetirementId))
                    && !ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "").Equals(
                            databaseOnlyRetirementId,
                            StringComparison.OrdinalIgnoreCase)),
                    "Deleting a database-only legacy campaign does not recreate an empty campaign directory or PostgreSQL schema during semantic cleanup.");

                PrepareCampaignRetirementTestPoint(
                    activeRetirementId, "active_" + token, "Active Save");
                Dictionary<string, object> activeDeferred =
                    SaveSyncDeleteNativeSaveApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = activeRetirementId,
                        ["savePointId"] = "active_" + token,
                        ["nativeSaveName"] = "Active Save",
                        ["campaignLoaded"] = true
                    });
                add("active_campaign_final_save_deletion_is_deferred",
                    ReadBool(activeDeferred, "campaignDeletionPending", false)
                    && !ReadBool(activeDeferred, "campaignDeleted", true)
                    && IsCampaignRetirementPending(activeRetirementId)
                    && ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == activeRetirementId),
                    "The active campaign records durable pending_final_save_deletion instead of deleting live state.");

                string replacementPoint = "replacement_" + token;
                Dictionary<string, object> replacementLedger =
                    ReadSaveSyncLedger(activeRetirementId);
                replacementLedger["points"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = activeRetirementId,
                        ["savePointId"] = replacementPoint,
                        ["nativeSaveName"] = "Replacement Save",
                        ["snapshotStorageId"] = "",
                        ["status"] = "registered"
                    }
                };
                WriteSaveSyncLedger(activeRetirementId, replacementLedger);
                Dictionary<string, object> replacementFinalized =
                    SaveSyncFinalizeApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = activeRetirementId,
                        ["savePointId"] = replacementPoint,
                        ["nativeSaveName"] = "Replacement Save",
                        ["successful"] = true
                    });
                add("new_confirmed_save_cancels_pending_retirement",
                    ReadBool(replacementFinalized, "ok", false)
                    && !IsCampaignRetirementPending(activeRetirementId),
                    "A new confirmed native save cancels pending final-save retirement before unload.");
                Dictionary<string, object> deferredAgain =
                    SaveSyncDeleteNativeSaveApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = activeRetirementId,
                        ["savePointId"] = replacementPoint,
                        ["nativeSaveName"] = "Replacement Save",
                        ["campaignLoaded"] = true
                    });
                Dictionary<string, object> unloaded = CampaignUnloadedApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = activeRetirementId
                    });
                add("pending_retirement_executes_on_unload",
                    ReadBool(deferredAgain, "campaignDeletionPending", false)
                    && ReadBool(unloaded, "campaignDeleted", false)
                    && !IsCampaignRetirementPending(activeRetirementId),
                    "A durable final-save retirement executes at the explicit campaign-unload boundary.");

                PrepareSaveSyncTestCampaign(unmatchedRetirementId, "unmatched");
                Dictionary<string, object> unmatchedDeleted =
                    SaveSyncDeleteNativeSaveApi(new Dictionary<string, object>
                    {
                        ["campaignId"] = unmatchedRetirementId,
                        ["savePointId"] = "never_registered_" + token,
                        ["nativeSaveName"] = "Never Registered",
                        ["campaignLoaded"] = false
                    });
                add("unmatched_notification_never_retires_campaign",
                    ReadString(unmatchedDeleted, "result", "") == "not_found"
                    && ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == unmatchedRetirementId),
                    "An unmatched deletion notification cannot retire a campaign that never registered the named Save Sync point.");
                MarkCampaignSaveSyncLifecycle(unmatchedRetirementId,
                    "imported_awaiting_first_load", false);
                Dictionary<string, object> protectedImport =
                    ReadDictionaryList(CampaignStorageAuditCore(), "campaigns")
                        .FirstOrDefault(row => ReadString(row, "campaignId", "")
                            == unmatchedRetirementId);
                add("imported_zero_point_campaign_is_protected",
                    protectedImport != null
                    && !ReadBool(protectedImport,
                        "eligibleForPermanentDeletion", true),
                    "An imported campaign awaiting its first confirmed load is distinguished from manually cleanable legacy zero-save data.");

                bool traversalRejected = false;
                bool sharedRejected = false;
                try { ValidateCampaignOwnedDirectory(
                    Path.Combine(CampaignsRoot(), "..", "escape"),
                    CampaignsRoot(), false); }
                catch (InvalidDataException) { traversalRejected = true; }
                try { ValidateCampaignOwnedDirectory(
                    Path.Combine(CampaignPortraitCacheRoot(), "_shared"),
                    CampaignPortraitCacheRoot(), true); }
                catch (InvalidDataException) { sharedRejected = true; }
                add("retirement_rejects_path_escape_and_shared_root",
                    traversalRejected && sharedRejected,
                    "Campaign retirement rejects path traversal and can never stage the _shared portrait library.");

                string orphanCampaignRoot = StrictCampaignDirectory(orphanRootId);
                string orphanSaveSyncRoot = SaveSyncCampaignRoot(orphanRootId);
                string orphanPortraitRoot = CampaignPortraitCacheDirectory(orphanRootId);
                Directory.CreateDirectory(orphanCampaignRoot);
                Directory.CreateDirectory(orphanSaveSyncRoot);
                Directory.CreateDirectory(orphanPortraitRoot);
                File.WriteAllText(Path.Combine(orphanCampaignRoot, "orphan.txt"),
                    "orphan", Encoding.UTF8);
                List<Dictionary<string, object>> orphanCleanupResults =
                    new List<Dictionary<string, object>>();
                DeleteAuditedOrphanRoots(new[]
                {
                    new Dictionary<string, object>
                    {
                        ["category"] = "orphan_campaign_root",
                        ["name"] = Path.GetFileName(orphanCampaignRoot),
                        ["path"] = orphanCampaignRoot,
                        ["fileCount"] = 1,
                        ["bytes"] = 6
                    }
                }, CampaignsRoot(), false, orphanCleanupResults);
                DeleteAuditedOrphanRoots(new[]
                {
                    new Dictionary<string, object>
                    {
                        ["category"] = "orphan_save_sync_root",
                        ["name"] = Path.GetFileName(orphanSaveSyncRoot),
                        ["path"] = orphanSaveSyncRoot,
                        ["fileCount"] = 0,
                        ["bytes"] = 0
                    }
                }, SaveSyncRoot(), false, orphanCleanupResults);
                DeleteAuditedOrphanRoots(new[]
                {
                    new Dictionary<string, object>
                    {
                        ["category"] = "orphan_portrait_root",
                        ["name"] = Path.GetFileName(orphanPortraitRoot),
                        ["path"] = orphanPortraitRoot,
                        ["fileCount"] = 0,
                        ["bytes"] = 0
                    }
                }, CampaignPortraitCacheRoot(), true, orphanCleanupResults);
                add("confirmed_cleanup_removes_audited_orphan_roots",
                    orphanCleanupResults.Count == 3
                    && orphanCleanupResults.All(row =>
                        ReadBool(row, "deleted", false))
                    && !Directory.Exists(orphanCampaignRoot)
                    && !Directory.Exists(orphanSaveSyncRoot)
                    && !Directory.Exists(orphanPortraitRoot),
                    "The confirmed zero-save cleanup also removes verified orphan campaign, Save Sync, and portrait roots while preserving the shared library.");

                PrepareSaveSyncTestCampaign(recoveredFailureId, "recover");
                CampaignRetirementFailureInjectionForTests = "after_stage";
                Dictionary<string, object> recoveredFailure =
                    DeleteCampaignOwnedDataCore(recoveredFailureId,
                        "Recovered Failure", true, "test_failure");
                CampaignRetirementFailureInjectionForTests = "";
                add("interrupted_retirement_restores_active_roots",
                    !ReadBool(recoveredFailure, "campaignDeleted", true)
                    && Directory.Exists(StrictCampaignDirectory(recoveredFailureId))
                    && ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == recoveredFailureId),
                    "A pre-commit staging failure rolls campaign-owned roots back and leaves the PostgreSQL campaign active.");

                PrepareSaveSyncTestCampaign(residualCleanupId, "residual");
                string longResidualDirectory =
                    StrictCampaignDirectory(residualCleanupId);
                for (int index = 0; index < 5; index++)
                    longResidualDirectory = Path.Combine(
                        longResidualDirectory,
                        "long-retirement-segment-" + index.ToString(
                            CultureInfo.InvariantCulture)
                        + "-abcdefghijklmnopqrstuvwxyz");
                string longResidualFile = Path.Combine(
                    ExtendedLengthPath(longResidualDirectory),
                    "long-retirement-remnant.json");
                Directory.CreateDirectory(
                    ExtendedLengthPath(longResidualDirectory));
                File.WriteAllText(longResidualFile, "long", Encoding.UTF8);
                bool longResidualFixtureCreated =
                    longResidualDirectory.Length > 260
                    && File.Exists(longResidualFile);
                CampaignRetirementFailureInjectionForTests = "after_database_drop";
                Dictionary<string, object> residual = DeleteCampaignOwnedDataCore(
                    residualCleanupId, "Residual Cleanup", true, "test_residual");
                CampaignRetirementFailureInjectionForTests = "";
                bool residualReported = ReadBool(residual, "campaignDeleted", false)
                    && !ReadBool(residual, "cleanupComplete", true)
                    && ReadStringList(residual, "residualCleanup").Count > 0;
                foreach (string residualPath in ReadStringList(
                    residual, "residualCleanup"))
                    TryDeleteFile(Path.Combine(residualPath, "transaction.json"));
                CleanupCampaignRetirementStagingAtStartup();
                bool residualRemoved = CampaignRetirementStagingRoots().All(root =>
                    !Directory.Exists(root)
                    || !Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                        .Any(path => Path.GetFileName(path).StartsWith(
                            residualCleanupId + "_", StringComparison.OrdinalIgnoreCase)));
                add("partial_cleanup_is_reported_and_restart_recovered",
                    longResidualFixtureCreated
                    && residualReported && residualRemoved,
                    "Post-commit cleanup failures return explicit residual paths, and the startup reconciler removes verified transaction remnants even when an interrupted recursive delete already removed the receipt.");
                PrepareSaveSyncTestCampaign(lockedCleanupId, "locked");
                string lockedPath = Path.Combine(
                    StrictCampaignDirectory(lockedCleanupId), "locked.bin");
                File.WriteAllText(lockedPath, "locked", Encoding.UTF8);
                Dictionary<string, object> lockedResult;
                using (FileStream locked = new FileStream(lockedPath,
                    FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    lockedResult = DeleteCampaignOwnedDataCore(
                        lockedCleanupId, "Locked Cleanup", true, "test_locked");
                }
                bool lockedFailureVisible =
                    (!ReadBool(lockedResult, "campaignDeleted", false)
                        && Directory.Exists(StrictCampaignDirectory(lockedCleanupId))
                        && !string.IsNullOrWhiteSpace(
                            ReadString(lockedResult, "error", "")))
                    || (ReadBool(lockedResult, "campaignDeleted", false)
                        && !ReadBool(lockedResult, "cleanupComplete", true)
                        && ReadStringList(lockedResult,
                            "residualCleanup").Count > 0);
                add("locked_files_never_fail_silently",
                    lockedFailureVisible,
                    "A locked campaign file either leaves the active campaign recovered or produces explicit post-commit residual cleanup; it is never silently ignored.");
                CleanupCampaignRetirementStagingAtStartup();
                Dictionary<string, object> badCleanupConfirmation =
                    CleanupZeroSaveCampaignsApi(new Dictionary<string, object>
                    {
                        ["confirmation"] = "DELETE"
                    });
                add("zero_save_cleanup_requires_exact_confirmation",
                    !ReadBool(badCleanupConfirmation, "ok", true),
                    "Bulk zero-save cleanup requires its exact confirmation phrase.");
                add("campaign_storage_audit_and_control_center_contract",
                    html.Contains("/api/campaigns/storage-audit")
                    && html.Contains("delete Reign campaigns with no saves")
                    && html.Contains("No matching Bannerlord save")
                    && html.Contains("_shared"),
                    "The Control Center exposes the storage audit, explicit zero-save label, protected shared library, and confirmation-gated cleanup.");

                string auditableDirectory;
                add("campaign_storage_audit_filters_internal_and_invalid_records",
                    TryResolveAuditableCampaignDirectory(
                        "AuditCampaign_123", out auditableDirectory)
                    && auditableDirectory.EndsWith(
                        "AuditCampaign_123", StringComparison.OrdinalIgnoreCase)
                    && !TryResolveAuditableCampaignDirectory(
                        "default", out auditableDirectory)
                    && !TryResolveAuditableCampaignDirectory(
                        "../escape", out auditableDirectory),
                    "Storage audits ignore internal technical schemas and invalid database identifiers instead of failing or treating them as player campaigns.");
            }
            catch (Exception ex) { add("save_sync_test_exception", false, ex.ToString()); }
            finally
            {
                SaveSyncFailureInjectionForTests = "";
                SaveSyncEnabledOverrideForTests = null;
                SaveSyncSkipSemanticForTests = false;
                SaveSyncDisableBackgroundOptimizationForTests = false;
                CampaignMutationAllowDuringTests = false;
                CampaignRetirementFailureInjectionForTests = "";
                CampaignNativeSaveRootOverrideForTests = "";
                TryDeleteFile(archivePath);
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                try { ReignPostgreSqlStorage.DropCampaign(siblingId); } catch { }
                try { ReignPostgreSqlStorage.DropCampaign(preexistingId); } catch { }
                foreach (string retirementId in new[]
                {
                    immediateRetirementId, activeRetirementId,
                    unmatchedRetirementId, recoveredFailureId,
                    residualCleanupId, lockedCleanupId,
                    databaseOnlyRetirementId, currentSlotOwnerId,
                    supersededSlotOwnerId
                })
                {
                    try { ReignPostgreSqlStorage.DropCampaign(retirementId); } catch { }
                    CancelPendingCampaignRetirement(retirementId, "test_cleanup");
                    TryDeleteDirectory(StrictCampaignDirectory(retirementId));
                    TryDeleteDirectory(SaveSyncCampaignRoot(retirementId));
                    TryDeleteDirectory(CampaignPortraitCacheDirectory(retirementId));
                }
                CleanupCampaignRetirementStagingAtStartup();
                TryDeleteDirectory(StrictCampaignDirectory(campaignId));
                TryDeleteDirectory(StrictCampaignDirectory(siblingId));
                TryDeleteDirectory(StrictCampaignDirectory(preexistingId));
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
                TryDeleteDirectory(SaveSyncCampaignRoot(siblingId));
                TryDeleteDirectory(SaveSyncCampaignRoot(preexistingId));
                TryDeleteDirectory(StrictCampaignDirectory(orphanRootId));
                TryDeleteDirectory(SaveSyncCampaignRoot(orphanRootId));
                TryDeleteDirectory(CampaignPortraitCacheDirectory(orphanRootId));
                TryDeleteDirectory(nativeSaveFixture);
            }
            return results;
        }

        private static void PrepareCampaignRetirementTestPoint(
            string campaignId,
            string savePointId,
            string nativeSaveName)
        {
            PrepareSaveSyncTestCampaign(campaignId, "retirement");
            Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
            ledger["points"] = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["savePointId"] = savePointId,
                    ["nativeSaveName"] = nativeSaveName,
                    ["snapshotStorageId"] = "",
                    ["status"] = "ready"
                }
            };
            ledger["lastRegisteredPointId"] = savePointId;
            ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
            WriteSaveSyncLedger(campaignId, ledger);
            MarkCampaignSaveSyncLifecycle(campaignId, "registered", true);
        }

        private static void PrepareSaveSyncTestCampaign(string campaignId, string marker)
        {
            string campaign = StrictCampaignDirectory(campaignId);
            Directory.CreateDirectory(Path.Combine(campaign, "characters", "hero_a", "portraits"));
            WriteJsonObject(Path.Combine(campaign, "campaign.json"), new Dictionary<string, object> { ["campaignId"] = campaignId, ["campaignLabel"] = "Save Sync Test" });
            WriteJsonObject(Path.Combine(campaign, "portrait_roster.json"), new Dictionary<string, object>
            {
                ["version"] = 2,
                ["campaignId"] = campaignId,
                ["rosterFingerprint"] = marker,
                ["characters"] = new List<object>()
            });
            WriteJsonObject(Path.Combine(campaign, "characters", "hero_a", "background.json"), new Dictionary<string, object> { ["text"] = "old static definition" });
            File.WriteAllBytes(Path.Combine(campaign, "characters", "hero_a", "portraits", "portrait.png"), new byte[] { 1, 2, 3, 4 });
            WriteJsonObject(Path.Combine(campaign, "characters", "hero_a", "state.json"), new Dictionary<string, object> { ["knowledge"] = marker });
            AppendSaveSyncTestLine(campaignId, "conversations", marker);
            AppendSaveSyncTestLine(campaignId, "events", marker);
            AppendSaveSyncTestLine(campaignId, "memories", marker);
            AppendSaveSyncTestLine(campaignId, "worldHistory", marker);
            AppendSaveSyncTestLine(campaignId, "rumors", marker);
            AppendSaveSyncTestLine(campaignId, "actions", marker);
            AppendSaveSyncTestLine(campaignId, "diplomacy", marker);
            AppendSaveSyncTestLine(campaignId, "rebellions", marker);
            AppendSaveSyncTestLine(campaignId, "court", marker);
            AppendSaveSyncTestPath(campaignId, Path.Combine("world", "events.jsonl"), marker);
            AppendSaveSyncTestPath(campaignId, Path.Combine("world", "memories.jsonl"), marker);
            AppendSaveSyncTestPath(campaignId, Path.Combine("actions", "results.jsonl"), marker);
            AppendSaveSyncTestPath(campaignId, Path.Combine("diplomacy", "consequences.jsonl"), marker);
            AppendSaveSyncTestPath(campaignId, Path.Combine("negotiations", "events.jsonl"), marker);
            WriteJsonObject(Path.Combine(campaign, "actions", "action-queue.json"), new Dictionary<string, object> { ["marker"] = marker, ["items"] = new List<object> { marker + "_queued_action" } });
            WriteJsonObject(Path.Combine(campaign, "world", "calendar.json"), new Dictionary<string, object> { ["marker"] = marker, ["nativeDayAnchor"] = marker == "old" ? 10d : 20d });
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                EnsureMbtiRelationshipSchema(connection);
                EnsureCourtSchema(connection);
                EnsureSocialReputationSchema(connection);
                long ts = marker == "old" ? 10 : 1;
                ExecuteSql(connection, "INSERT OR REPLACE INTO events(event_id,campaign_id,ts,world_day,event_type,summary,created_utc) VALUES($id,$campaign,$ts,$day,'test',$id,$utc);", new Dictionary<string, object> { ["id"] = marker + "_event", ["campaign"] = campaignId, ["ts"] = ts, ["day"] = (double)ts, ["utc"] = DateTime.UtcNow.ToString("o") });
                ExecuteSql(connection, "INSERT OR REPLACE INTO memories(memory_id,event_id,owner_id,ts,world_day,summary) VALUES($id,$event,'hero_a',$ts,$day,$id);", new Dictionary<string, object> { ["id"] = marker + "_memory", ["event"] = marker + "_event", ["ts"] = ts, ["day"] = (double)ts });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,projected_native_relation,last_decay_period,updated_ts)
VALUES('hero_a|hero_b','hero_a','hero_b',$affinity,$affinity,'neutral','neutral',
$day,$day,$affinity,0,$ts);", new Dictionary<string, object>
                {
                    ["affinity"] = marker == "future" ? 99 : 1, ["day"] = (double)ts, ["ts"] = ts
                });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,last_processed_day,updated_ts)
VALUES('hero_a|hero_b','hero_a','hero_b',$day,$ts);",
                    new Dictionary<string, object> { ["day"] = (int)ts, ["ts"] = ts });
                ExecuteSql(connection, "INSERT OR REPLACE INTO conversation_sessions(session_id,campaign_id,npc_id,start_ts) VALUES($id,$campaign,'hero_a',$ts);", new Dictionary<string, object> { ["id"] = marker + "_session", ["campaign"] = campaignId, ["ts"] = ts });
                ExecuteSql(connection, "INSERT OR REPLACE INTO conversation_turns(turn_id,session_id,turn_order,role,text,ts) VALUES($id,$session,1,'npc',$id,$ts);", new Dictionary<string, object> { ["id"] = marker + "_turn", ["session"] = marker + "_session", ["ts"] = ts });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_occurrences(
occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,source_event_id,world_day,expires_day,status,
exposure_chance,exposure_roll,catalog_revision,participants_json,provenance_summary,snapshot_json,created_ts,updated_ts)
VALUES($id,$campaign,'timeline_test','marital_strife','hero_a|hero_b',$event,$day,999999,'active',
1,0,1,'[""hero_a"",""hero_b""]',$summary,'{}',$ts,$ts);",
                    new Dictionary<string, object>
                    {
                        ["id"] = marker + "_rumor_occurrence", ["campaign"] = campaignId,
                        ["event"] = marker + "_event", ["day"] = (double)ts,
                        ["summary"] = marker + "_rumor_summary", ["ts"] = ts
                    });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_subject_tags(
occurrence_id,subject_id,tag_id,subject_role,description,rumor_value,reputation_value,status,
co_participants_json,snapshot_json,updated_ts)
VALUES($occurrence,'hero_a','marital_strife','spouse',$description,-5,-10,'active','[""hero_b""]','{}',$ts);",
                    new Dictionary<string, object>
                    {
                        ["occurrence"] = marker + "_rumor_occurrence",
                        ["description"] = marker + "_rumor_description", ["ts"] = ts
                    });
                ExecuteSql(connection, "INSERT OR REPLACE INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,created_utc) VALUES($id,$campaign,'timeline_test',$ts,$day,'test',$id,$utc);", new Dictionary<string, object> { ["id"] = marker + "_history", ["campaign"] = campaignId, ["ts"] = ts, ["day"] = (double)ts, ["utc"] = DateTime.UtcNow.ToString("o") });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO rebellion_movements(movement_id,campaign_id,timeline_id,parent_kingdom_id,leader_clan_id,leader_hero_id,ruler_hero_id,objective,stage,pressure,relationship_pressure,trait_pressure,factual_pressure,viability,readiness,correlation_id,created_day,updated_day)
VALUES($id,$campaign,'timeline_test','kingdom_a','clan_a','hero_a','hero_b','claimant','organizing',1,1,1,1,1,1,$id,$day,$day);", new Dictionary<string, object> { ["id"] = marker + "_rebellion", ["campaign"] = campaignId, ["day"] = (double)ts });
                ExecuteSql(connection, "INSERT OR REPLACE INTO court_sessions(session_id,campaign_id,timeline_id,authority,host_settlement_id,created_ts,updated_ts) VALUES($id,$campaign,'timeline_test','royal','town_a',$ts,$ts);", new Dictionary<string, object> { ["id"] = marker + "_court", ["campaign"] = campaignId, ["ts"] = ts });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO character_reputations(
campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,archetype_id,subject_role,description,
reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,'timeline_test','hero_a','test_reputation',$occurrence,'test','subject',$description,1,$day,1,'{}','active',$ts);",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["occurrence"] = marker + "_rumor_occurrence",
                        ["description"] = marker + "_reputation", ["day"] = (double)ts, ["ts"] = ts
                    });
                ExecuteSql(connection, "INSERT OR REPLACE INTO director_outbox(outbox_id,kind,status,world_day,subject_ids_json,payload_json,created_ts) VALUES($id,'test','pending',$day,'[]',$payload,$ts);", new Dictionary<string, object> { ["id"] = marker + "_outbox", ["day"] = (double)ts, ["payload"] = "{\"marker\":\"" + marker + "\"}", ["ts"] = ts });
            }
        }

        private static void AppendSaveSyncTestFutureState(string campaignId)
        {
            PrepareSaveSyncTestCampaign(campaignId, "future");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                ExecuteSql(connection, "UPDATE character_reputations SET reputation_value=99,updated_ts=20 WHERE campaign_id=$campaign AND subject_id='hero_a' AND tag_id='test_reputation';", new Dictionary<string, object> { ["campaign"] = campaignId });
            }
            WriteJsonObject(Path.Combine(StrictCampaignDirectory(campaignId), "characters", "hero_a", "background.json"), new Dictionary<string, object> { ["text"] = "later static definition" });
            File.WriteAllBytes(Path.Combine(StrictCampaignDirectory(campaignId), "characters", "hero_a", "portraits", "portrait.png"), new byte[] { 9, 8, 7, 6 });
            string audit = Path.Combine(StrictCampaignDirectory(campaignId), "audit", "audit.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(audit));
            File.AppendAllText(audit, "{\"message\":\"future technical audit\"}" + Environment.NewLine, Encoding.UTF8);
            string liveTestEvidence = Path.Combine(StrictCampaignDirectory(campaignId), "tests", "live-interaction", "runs", "future.json");
            Directory.CreateDirectory(Path.GetDirectoryName(liveTestEvidence));
            File.WriteAllText(liveTestEvidence, "{\"message\":\"future qualification evidence\"}", Encoding.UTF8);
        }

        private static void AppendSaveSyncTestLine(string campaignId, string subsystem, string marker)
        {
            Dictionary<string, string> paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["conversations"] = Path.Combine("characters", "hero_a", "history", "dialogue.jsonl"),
                ["events"] = Path.Combine("events", "social", "test_event", "transcript.jsonl"),
                ["memories"] = Path.Combine("characters", "hero_a", "memory", "memories.jsonl"),
                ["worldHistory"] = Path.Combine("world", "history.jsonl"), ["rumors"] = Path.Combine("rumors", "rumors.jsonl"),
                ["actions"] = Path.Combine("actions", "actions.jsonl"), ["diplomacy"] = Path.Combine("diplomacy", "events.jsonl"),
                ["rebellions"] = Path.Combine("rebellions", "events.jsonl"), ["court"] = Path.Combine("court", "events.jsonl")
            };
            string path = Path.Combine(StrictCampaignDirectory(campaignId), paths[subsystem]);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, Json.Serialize(new Dictionary<string, object> { ["marker"] = marker, ["subsystem"] = subsystem }) + Environment.NewLine, Encoding.UTF8);
        }

        private static void AppendSaveSyncTestPath(string campaignId, string relativePath, string marker)
        {
            string path = Path.Combine(StrictCampaignDirectory(campaignId), relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.AppendAllText(path, Json.Serialize(new Dictionary<string, object> { ["marker"] = marker, ["path"] = relativePath.Replace('\\', '/') }) + Environment.NewLine, Encoding.UTF8);
        }

        private static bool SaveSyncTestMarkerExists(string campaignId, string marker)
        {
            string campaign = StrictCampaignDirectory(campaignId);
            string[] gameplayPaths =
            {
                Path.Combine("characters", "hero_a", "state.json"),
                Path.Combine("characters", "hero_a", "history", "dialogue.jsonl"),
                Path.Combine("characters", "hero_a", "memory", "memories.jsonl"),
                Path.Combine("events", "social", "test_event", "transcript.jsonl"),
                Path.Combine("world", "history.jsonl"), Path.Combine("world", "events.jsonl"), Path.Combine("world", "memories.jsonl"), Path.Combine("world", "calendar.json"),
                Path.Combine("rumors", "rumors.jsonl"), Path.Combine("actions", "actions.jsonl"), Path.Combine("actions", "results.jsonl"), Path.Combine("actions", "action-queue.json"),
                Path.Combine("diplomacy", "events.jsonl"), Path.Combine("diplomacy", "consequences.jsonl"), Path.Combine("rebellions", "events.jsonl"),
                Path.Combine("negotiations", "events.jsonl"), Path.Combine("court", "events.jsonl")
            };
            bool fileMarker = gameplayPaths.Select(path => Path.Combine(campaign, path)).Where(File.Exists)
                .Any(path => { try { return File.ReadAllText(path).Contains(marker); } catch { return false; } });
            bool sqlMarker;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                sqlMarker = ReadLong(QuerySql(connection, "SELECT COUNT(*) count FROM events WHERE event_id=$id;", new Dictionary<string, object> { ["id"] = marker + "_event" }).FirstOrDefault(), "count", 0) > 0;
            return fileMarker || sqlMarker;
        }

        private static long SaveSyncTestFutureSqlRows(string campaignId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                string[] queries =
                {
                    "SELECT COUNT(*) count FROM events WHERE event_id='future_event';",
                    "SELECT COUNT(*) count FROM memories WHERE memory_id='future_memory';",
                    "SELECT COUNT(*) count FROM conversation_turns WHERE turn_id='future_turn';",
                    "SELECT COUNT(*) count FROM rumor_occurrences WHERE occurrence_id='future_rumor_occurrence';",
                    "SELECT COUNT(*) count FROM rumor_subject_tags WHERE occurrence_id='future_rumor_occurrence' AND subject_id='hero_a';",
                    "SELECT COUNT(*) count FROM character_reputations WHERE subject_id='hero_a' AND tag_id='test_reputation' AND reputation_value=99;",
                    "SELECT COUNT(*) count FROM world_history_events WHERE event_id='future_history';",
                    "SELECT COUNT(*) count FROM rebellion_movements WHERE movement_id='future_rebellion';",
                    "SELECT COUNT(*) count FROM court_sessions WHERE session_id='future_court';",
                    "SELECT COUNT(*) count FROM director_outbox WHERE outbox_id='future_outbox';"
                };
                return queries.Sum(sql => ReadLong(QuerySql(connection, sql).FirstOrDefault(), "count", 0));
            }
        }

        private static Dictionary<string, object> SaveSyncTestPointPayload(string campaignId, string pointId, double day, string name)
        {
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["campaignLabel"] = "Save Sync Test", ["savePointId"] = pointId,
                ["savePointKind"] = "native_save", ["campaignTimeDays"] = day, ["campaignTimeMilliseconds"] = day * 86400000d,
                ["capturedUtc"] = new DateTime(2026, 7, 14, 12, 0, 0, DateTimeKind.Utc).AddDays(day).ToString("o"),
                ["nativeSaveName"] = name, ["timelineId"] = "timeline_test", ["worldHistorySequence"] = (long)day
            };
        }

        private static string SaveSyncTestSqlValue(string campaignId, string sql, string field, string parameterCampaign = "")
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> parameters = string.IsNullOrWhiteSpace(parameterCampaign) ? null : new Dictionary<string, object> { ["campaign"] = parameterCampaign };
                Dictionary<string, object> row = QuerySql(connection, sql, parameters).FirstOrDefault();
                object value = row != null && row.ContainsKey(field) ? row[field] : null;
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }

        private static string SaveSyncActiveFingerprintForTests(string campaignId)
        {
            string campaignRoot = StrictCampaignDirectory(campaignId);
            List<Dictionary<string, object>> files =
                Directory.Exists(campaignRoot)
                    ? BackupFileManifest(campaignRoot)
                    : new List<Dictionary<string, object>>();
            return ReignPostgreSqlStorage.CampaignStateToken(campaignId)
                + ":" + SaveSyncManifestFingerprint(files);
        }
    }
}
