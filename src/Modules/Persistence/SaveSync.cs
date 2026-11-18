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
    internal static partial class PersistenceService
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
        internal static bool SaveSyncDisableBackgroundOptimizationForTests;


        // The same-directory Unix rename atomically replaces JSON and Save Sync files.
        internal static bool MoveFileEx(string existingFileName, string newFileName, uint flags)
        {
            File.Move(existingFileName, newFileName, (flags & 0x1) != 0);
            return true;
        }

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

        internal static Dictionary<string, object> SaveSyncRegisterApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsSaveSyncEnabled()) return SaveSyncDisabledResult(payload);
            Stopwatch requestTimer = Stopwatch.StartNew();
            Program.CampaignDataGate.EnterWriteLock();
            long gateWaitMs = requestTimer.ElapsedMilliseconds;
            try
            {
                var result = SaveSyncRegisterCore(payload);
                result["gateWaitMs"] = gateWaitMs;
                result["requestDurationMs"] = requestTimer.ElapsedMilliseconds;
                Program.LogOperational("save_sync.register_completed", new Dictionary<string, object>
                {
                    ["campaignId"] = ReadString(payload, "campaignId", ""),
                    ["gateWaitMs"] = gateWaitMs, ["requestDurationMs"] = requestTimer.ElapsedMilliseconds,
                    ["result"] = ReadString(result, "result", "")
                });
                return result;
            }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        internal static Dictionary<string, object> SaveSyncRegisterCore(Dictionary<string, object> payload)
        {
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "");
            string savePointId = ReadString(payload, "savePointId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            ValidateSaveSyncId(savePointId, "savePointId");
            if (PortraitsService.ChatViewPending(campaignId)) return TestDict("ok", false, "result", "chat_view_pending", "retryable", true,
                "error", "Save Sync is waiting for scene images to finish; no inconsistent image checkpoint was captured.");
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
                Program.WriteJsonObject(campaignMetadataPath, new Dictionary<string, object>
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
                Dictionary<string, object> existingManifest = Program.ReadJsonObject(Path.Combine(pointRoot, "manifest.json"));
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

        internal static Dictionary<string, object> SaveSyncFinalizeApi(Dictionary<string, object> payload)
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
            Program.CampaignDataGate.EnterWriteLock();
            long gateWaitMs = finalizeTimer.ElapsedMilliseconds - preflightMs;
            try
            {
                Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
                Dictionary<string, object> point = SaveSyncPoint(ledger, savePointId);
                if (point == null) return SaveSyncMissingFinalizeResult(campaignId, savePointId);
                bool successful = ReadBool(payload, "successful", false);
                point["nativeSaveName"] = Program.LimitText(ReadString(payload, "nativeSaveName", ReadString(point, "nativeSaveName", "")), 240);
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
            finally { Program.CampaignDataGate.ExitWriteLock(); }
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

        internal static Dictionary<string, object> SaveSyncLoadApi(Dictionary<string, object> payload, bool forcePreview)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!IsSaveSyncEnabled()) return SaveSyncDisabledResult(payload);
            Program.CampaignDataGate.EnterWriteLock();
            try { return SaveSyncLoadCore(payload, forcePreview || ReadBool(payload, "dryRun", false)); }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> SaveSyncLoadCore(Dictionary<string, object> payload, bool dryRun)
        {
            if (!dryRun) Program.InvalidateCodexConversationTimeline(ReadString(payload, "campaignId", ""));
            return SaveSyncLoadPostgreSql(payload, dryRun);
        }


        internal static Dictionary<string, object> SaveSyncStatusApi(Dictionary<string, string> query)
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
                ["lastLoadedSavePoint"] = PublicLoadedSaveSyncPoint(campaignId, ledger, allPoints),
                ["lastSyncResult"] = ReadDictionary(ledger, "lastSyncResult") ?? new Dictionary<string, object>(),
                ["lastBackupId"] = ReadString(ledger, "lastBackupId", ""),
                ["pendingFinalSaveDeletion"] = IsCampaignRetirementPending(campaignId),
                ["updatedUtc"] = ReadString(ledger, "updatedUtc", "")
            };
        }

        private static Dictionary<string, object> PublicLoadedSaveSyncPoint(string campaignId,
            Dictionary<string, object> ledger, List<Dictionary<string, object>> points)
        {
            var loaded = ReadDictionary(ledger, "lastLoadedSavePoint");
            string id = ReadString(loaded, "savePointId", "");
            if (id.Length == 0) return new Dictionary<string, object>();
            // The cached public receipt omits private storage identifiers. Resolve
            // the registered point before checking its actual snapshot location.
            var current = ResolveLoadedSaveSyncPoint(ledger, points);
            if (current != null) return PublicSaveSyncPoint(campaignId, current);
            var missing = PublicSaveSyncPoint(loaded);
            missing["status"] = "unregistered_loaded_point";
            missing["snapshotAvailable"] = false;
            missing["availabilityReason"] = "The last-loaded point is no longer registered in this campaign ledger.";
            return missing;
        }

        private static Dictionary<string, object> ResolveLoadedSaveSyncPoint(Dictionary<string, object> ledger,
            List<Dictionary<string, object>> points)
        {
            string id = ReadString(ReadDictionary(ledger, "lastLoadedSavePoint"), "savePointId", "");
            return id.Length == 0 ? null : points.FirstOrDefault(point => ReadString(point, "savePointId", "")
                .Equals(id, StringComparison.OrdinalIgnoreCase));
        }

        internal static Dictionary<string, object> SaveSyncDeleteNativeSaveApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string requestedCampaignId = ReadString(payload, "campaignId", "");
            string savePointId = ReadString(payload, "savePointId", "");
            string nativeSaveName = ReadString(payload, "nativeSaveName", "");
            if (string.IsNullOrWhiteSpace(savePointId) && string.IsNullOrWhiteSpace(nativeSaveName))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "savePointId or nativeSaveName is required." };

            Program.CampaignDataGate.EnterWriteLock();
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
                                Dictionary<string, object> metadata = Program.ReadJsonObject(
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
            finally { Program.CampaignDataGate.ExitWriteLock(); }
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
                .Select(path => ReadString(Program.ReadJsonObject(Path.Combine(path, "ledger.json")), "campaignId", ""))
                .Where(id => Program.HasLocalCampaignMetadata(id))
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
                Program.WriteJsonObject(Path.Combine(staging, "manifest.json"), manifest);
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

        private static void CopySaveSyncTreeForOptimization(string sourceRoot, string destinationRoot)
        {
            foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                string relative = RelativeBackupPath(sourceRoot, source);
                string target = Path.Combine(destinationRoot, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, false);
            }
        }

        private static void QueueSaveSyncOptimization(string campaignId, string savePointId)
        {
            if (SaveSyncDisableBackgroundOptimizationForTests || Program.ShutdownRequested) return;
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
                Dictionary<string, object> oldManifest = Program.ReadJsonObject(Path.Combine(temp, "manifest.json"));
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
                Program.WriteJsonObject(Path.Combine(temp, "manifest.json"), optimizedManifest);
                string optimizedStorageId = SaveSyncStorageKey("content|" + fingerprint);
                string optimizedRoot = SaveSyncContentRoot(campaignId, optimizedStorageId);

                Program.CampaignDataGate.EnterWriteLock();
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
                                Program.ReadJsonObject(Path.Combine(optimizedRoot, "manifest.json"));
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
                finally { Program.CampaignDataGate.ExitWriteLock(); }
                timer.Stop();
                AppendSaveSyncAudit(campaignId, "snapshot.optimized", new Dictionary<string, object>
                {
                    ["result"] = "optimized", ["savePointId"] = savePointId,
                    ["durationMs"] = timer.ElapsedMilliseconds
                });
            }
            finally { TryDeleteDirectory(temp); }
        }


        private static Dictionary<string, object> BuildSaveSyncInventory(string campaignRoot)
        {
            Dictionary<string, long> counts = SaveSyncSubsystemNames().ToDictionary(x => x, x => 0L, StringComparer.OrdinalIgnoreCase);
            string manifestPath = Path.Combine(
                Directory.GetParent(campaignRoot)?.FullName ?? string.Empty,
                "manifest.json");
            if (File.Exists(manifestPath))
            {
                Dictionary<string, object> manifest = Program.ReadJsonObject(manifestPath);
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

        internal static string SaveSyncSubsystemForTable(string table)
        {
            string value = (table ?? "").ToLowerInvariant();
            if (value.StartsWith("conversation_") || value == "letters" || value == "correspondence_threads") return "conversations";
            if (value == "events") return "events";
            if (value == "memories" || value == "summaries" || value == "comprehension" || value.StartsWith("memory_") || value.StartsWith("temporal_knowledge_") || value == "knowledge_receipts" || value == "beliefs") return "memories";
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
            if (value.StartsWith("character_editor_") || value == "character_npc_lives_state") return "characterState";
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

        internal static bool IsSaveSyncFingerprintExcluded(string relativePath)
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

        internal static bool ShouldIncludeSaveSyncSnapshotFile(string campaignRoot, string path)
        {
            if (IsLiveSqliteSidecar(path)) return false;
            string relative = RelativeBackupPath(campaignRoot, path);
            // These assets belong to the campaign but are not temporal save state. They remain
            // in the live campaign and are overlaid after rollback instead of being recopied into
            // every native-save snapshot.
            return !IsSaveSyncFingerprintExcluded(relative);
        }

        internal static DialogueService.SaveSyncSemanticReconciliationPlan BuildSaveSyncSemanticReconciliationPlan(
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

        internal static bool IsSaveSyncSemanticBlocked(string campaignId)
        {
            lock (SaveSyncSemanticBlockedCampaigns)
                return SaveSyncSemanticBlockedCampaigns.Contains(campaignId ?? "");
        }

        internal static DialogueService.SaveSyncSemanticReconciliationPlan BuildSaveSyncSemanticReconciliationPlan(
            Dictionary<string, Dictionary<string, object>> current,
            Dictionary<string, Dictionary<string, object>> target)
        {
            DialogueService.SaveSyncSemanticReconciliationPlan plan = new DialogueService.SaveSyncSemanticReconciliationPlan();
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
                    plan.InvalidatedCurrentSources.Add(new DialogueService.SemanticSourceIdentity
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
                    plan.RequeueSources.Add(new DialogueService.SemanticSourceIdentity
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
            Dictionary<string, object> manifest = Program.ReadJsonObject(
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
                foreach (Dictionary<string, object> row in Program.QuerySql(connection, sql))
                {
                    rows[SaveSyncEmbeddingDocumentKey(row)] = row;
                }
            }
            return rows;
        }

        internal static string SaveSyncEmbeddingDocumentKey(Dictionary<string, object> row)
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
            return DialogueService.WorldHistoryEventEmbeddingEligible(row);
        }

        internal static void ClearSaveSyncCaches(string campaignId)
        {
            InvalidateWaitingCampaignRequests();
            lock (WorldSimulationService.WorldHistoryCacheLock) WorldSimulationService.WorldHistoryClaimCache.Clear();
            lock (CharactersService.CharacterEditorAuthorityCacheLock) CharactersService.CharacterEditorAuthorityByCampaign.Remove(campaignId);
            RelationshipsService.InvalidateRelationshipPairStateCache(campaignId);
            ReignPostgreSqlStorage.ClearAllPools();
            RelationshipsService.ResetRelationshipCampaignScheduling(campaignId);
        }
    }
}
