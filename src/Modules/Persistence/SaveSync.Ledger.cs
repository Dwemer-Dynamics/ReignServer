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
        private static Dictionary<string, object> NormalizeSaveSyncPoint(Dictionary<string, object> payload)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["campaignId"] = ReadString(payload, "campaignId", ""), ["savePointId"] = ReadString(payload, "savePointId", ""),
                ["savePointKind"] = ReadString(payload, "savePointKind", "native_save"),
                ["campaignLabel"] = DialogueService.LimitText(ReadString(payload, "campaignLabel", ""), 500),
                ["campaignTimeDays"] = ReadDouble(payload, "campaignTimeDays", 0d),
                ["campaignTimeMilliseconds"] = ReadDouble(payload, "campaignTimeMilliseconds", 0d),
                ["capturedUtc"] = ReadString(payload, "capturedUtc", DateTime.UtcNow.ToString("o")),
                ["nativeSaveName"] = DialogueService.LimitText(ReadString(payload, "nativeSaveName", ""), 240),
                ["nativeCreationUtc"] = DialogueService.LimitText(ReadString(payload, "nativeCreationUtc", ""), 100),
                ["timelineId"] = DialogueService.LimitText(ReadString(payload, "timelineId", ""), 160),
                ["worldHistorySequence"] = ReadLong(payload, "worldHistorySequence", 0),
                ["worldHistoryHeadEventId"] = DialogueService.LimitText(ReadString(payload, "worldHistoryHeadEventId", ""), 200),
                ["clientVersion"] = DialogueService.LimitText(ReadString(payload, "clientVersion", ""), 100)
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

        internal static Dictionary<string, object> ReadSaveSyncLedger(string campaignId)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            SaveSyncRecoveryObserver?.Invoke("read_ledger." + campaignId);
#endif
            RequireSaveSyncCampaignAvailable(campaignId);
            lock (SaveSyncLedgerLock)
            {
                Dictionary<string, object> ledger = CampaignFileService.ReadJsonObject(Path.Combine(SaveSyncCampaignRoot(campaignId), "ledger.json"));
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

        internal static void WriteSaveSyncLedger(string campaignId, Dictionary<string, object> ledger)
        {
            RequireSaveSyncCampaignAvailable(campaignId);
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

        internal static Dictionary<string, object> SaveSyncPoint(Dictionary<string, object> ledger, string savePointId)
        {
            return ReadDictionaryList(ledger, "points").FirstOrDefault(x => ReadString(x, "savePointId", "").Equals(savePointId, StringComparison.OrdinalIgnoreCase));
        }

        // The caller first verifies that the active restore context names this save.
        // Read the exact selected queue file, including raw load-ready snapshots.
        internal static Dictionary<string, object> ReadSaveSyncActionQueueRow(string campaignId, string savePointId,
            string sourceTimeline, long sourceSequence, string sourceHead, string actionId)
        {
            var point = SaveSyncPoint(ReadSaveSyncLedger(campaignId), savePointId);
            if (point == null || ReadString(point, "timelineId", "") != sourceTimeline
                || ReadLong(point, "worldHistorySequence", 0) != sourceSequence
                || ReadString(point, "worldHistoryHeadEventId", "") != sourceHead) return null;
            SaveSyncStorageGate.EnterReadLock();
            try
            {
                string root = SaveSyncSnapshotRoot(campaignId, point);
                var manifest = CampaignFileService.ReadJsonObject(Path.Combine(root, "manifest.json"));
                if (ReadString(manifest, "format", "") != "bannerlord-reign-save-sync-snapshot"
                    || ReadString(manifest, "campaignId", "") != campaignId) return null;
                const string relative = "actions/action-queue.json";
                string queuePath = Path.Combine(root, "campaign", "actions", "action-queue.json");
                if (!File.Exists(queuePath)) return null;
                var inventory = ReadDictionaryList(manifest, "files").FirstOrDefault(file => ReadString(file, "path", "") == relative);
                if (inventory == null ? !ReadBool(manifest, "integrityDeferred", false)
                    : ReadLong(inventory, "bytes", -1) != new FileInfo(queuePath).Length
                        || !string.Equals(ReadString(inventory, "sha256", ""), FileSha256(queuePath), StringComparison.OrdinalIgnoreCase))
                    return null;
                var rows = CampaignFileService.ReadJsonArray(queuePath)
                    .OfType<Dictionary<string, object>>()
                    .Where(row => ReadString(row, "id", "") == actionId).Take(2).ToList();
                return rows.Count == 1 ? rows[0] : null;
            }
            finally { SaveSyncStorageGate.ExitReadLock(); }
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
                        Dictionary<string, object> metadata = CampaignFileService.ReadJsonObject(
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

        internal static string SaveSyncPostgreSqlSnapshotPointId(
            Dictionary<string, object> point)
        {
            return ReadString(point, "postgresqlSnapshotPointId",
                ReadString(point, "savePointId", ""));
        }

        private static void UpdateSaveSyncLastResult(Dictionary<string, object> ledger, Dictionary<string, object> point, Dictionary<string, object> result, string backupId)
        {
            ledger["lastLoadedSavePoint"] = PublicSaveSyncPoint(point);
            var lastResult = new Dictionary<string, object>
            {
                ["result"] = ReadString(result, "result", ""), ["ok"] = ReadBool(result, "ok", false),
                ["completedUtc"] = DateTime.UtcNow.ToString("o"), ["durationMs"] = ReadLong(result, "durationMs", 0),
                ["error"] = DialogueService.LimitText(ReadString(result, "error", ""), 2000), ["backupId"] = backupId ?? ""
            };
            RecordSaveSyncComparisonDiagnostics(lastResult, result);
            ledger["lastSyncResult"] = lastResult;
            ledger["lastBackupId"] = backupId ?? "";
            ledger["updatedUtc"] = DateTime.UtcNow.ToString("o");
        }

        private static void RecordSaveSyncComparisonDiagnostics(Dictionary<string, object> record, Dictionary<string, object> result)
        {
            if (ReadBool(result, "comparisonDiagnosticsOmitted", false))
            {
                record["comparisonDiagnosticsOmitted"] = true;
                return;
            }
            record["recordsRolledBack"] = ReadLong(result, "recordsRolledBack", 0);
            record["recordsRestored"] = ReadLong(result, "recordsRestored", 0);
            record["changedFiles"] = ReadInt(result, "changedFiles", 0);
            record["countsBySubsystem"] = ReadDictionary(result, "countsBySubsystem") ?? new Dictionary<string, object>();
        }

        private static void AppendSaveSyncAudit(string campaignId, string eventType, Dictionary<string, object> data)
        {
            try
            {
                SaveSyncRecoveryBoundary("audit." + eventType);
                string path = Path.Combine(SaveSyncCampaignRoot(campaignId), "audit.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                Dictionary<string, object> row = new Dictionary<string, object>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["utc"] = DateTime.UtcNow.ToString("o"),
                    ["eventType"] = eventType, ["campaignId"] = campaignId,
                    ["savePointId"] = ReadString(data, "savePointId", ""), ["result"] = ReadString(data, "result", ""),
                    ["durationMs"] = ReadLong(data, "durationMs", 0), ["backupId"] = ReadString(data, "backupId", ""),
                    ["error"] = DialogueService.LimitText(ReadString(data, "error", ""), 4000)
                };
                RecordSaveSyncComparisonDiagnostics(row, data);
                CampaignFileService.AppendBoundedJsonLineToPath(path, row, 4L * 1024L * 1024L, 2L * 1024L * 1024L);
                OperationsService.LogOperational("save_sync." + eventType, row);
            }
            catch { }
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
                WriteSaveSyncActiveState(campaignId, state, dirty: false);
                SaveSyncActiveStates[campaignId] = state;
            }
        }

        internal static void MarkSaveSyncActiveStateDirtyForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            string campaigns;
            try
            {
                full = Path.GetFullPath(path);
                campaigns = Path.GetFullPath(CampaignFileService.CampaignsRoot())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
            }
            catch { return; }
            if (!full.StartsWith(campaigns, StringComparison.OrdinalIgnoreCase)) return;
            string relative = full.Substring(campaigns.Length);
            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator <= 0) return;
            MarkSaveSyncActiveStateDirty(relative.Substring(0, separator));
        }

        internal static void MarkSaveSyncActiveStateDirty(string campaignId)
        {
            if (string.IsNullOrWhiteSpace(campaignId)) return;
            lock (SaveSyncActiveStateLock)
            {
                SaveSyncActiveState state =
                    ReadSaveSyncActiveStateLocked(campaignId);
                if (state.Dirty) return;
                // Keep this attempt retryable until the durable sidecar is published.
                WriteSaveSyncActiveState(campaignId, state, dirty: true);
                state.Dirty = true;
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
            // This sidecar is atomically replaced under SaveSyncActiveStateLock.
            // Taking FileLock here would invert queue writers' FileLock → dirty-state order.
            Dictionary<string, object> json = CampaignFileService.ReadAtomicallyReplacedJsonObject(SaveSyncActiveStatePath(campaignId));
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

        private static void WriteSaveSyncActiveState(string campaignId, SaveSyncActiveState state, bool dirty)
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
                ["dirty"] = dirty,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            }), new UTF8Encoding(false));
            ReplaceSaveSyncFileWithRetry(temp, path);
        }

        internal static void ResumePendingSaveSyncOptimizations()
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

        internal static void PurgeLegacySaveSyncSnapshotsAtStartup()
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
                Dictionary<string, object> ledger = CampaignFileService.ReadJsonObject(ledgerPath);
                string campaignId = ReadString(ledger, "campaignId", "");
                foreach (Dictionary<string, object> point in ReadDictionaryList(ledger, "points"))
                {
                    string pointId = SaveSyncPostgreSqlSnapshotPointId(point);
                    try
                    {
                        if (snapshotExists(campaignId, pointId)) continue;
                        OperationsService.LogOperational("save_sync.startup_snapshot_unavailable", new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId, ["savePointId"] = pointId,
                            ["preserved"] = true
                        });
                    }
                    catch (Exception ex)
                    {
                        OperationsService.LogOperational("save_sync.startup_snapshot_check_deferred", new Dictionary<string, object>
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
            return SaveSyncEnabledOverrideForTests ?? ReadBool(SettingsService.LoadSettings(), "saveSyncEnabled", true);
        }

        private static void ValidateSaveSyncId(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 180 || !Regex.IsMatch(value, "^[A-Za-z0-9_-]+$") || value == "." || value == "..")
                throw new InvalidDataException(name + " must contain only letters, numbers, underscores, or hyphens.");
        }

        internal static string StrictCampaignDirectory(string campaignId)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            RequireSaveSyncCampaignAvailable(campaignId);
            string root = Path.GetFullPath(CampaignFileService.CampaignsRoot());
            string path = Path.GetFullPath(Path.Combine(root, campaignId));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Campaign path escaped the campaign storage root.");
            return path;
        }

        internal static string SaveSyncRoot()
        {
            return ResolveSaveSyncRoot(
                Environment.GetEnvironmentVariable("REIGN_SAVE_SYNC_ROOT"),
                Path.Combine(Program.DataDir, "ss"));
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

        internal static string SaveSyncCampaignRoot(string campaignId)
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

        internal static string SaveSyncSnapshotRoot(string campaignId, Dictionary<string, object> point)
        {
            string storageId = ReadString(point, "snapshotStorageId", "");
            return string.IsNullOrWhiteSpace(storageId)
                ? SaveSyncPointRoot(campaignId, ReadString(point, "savePointId", ""))
                : SaveSyncContentRoot(campaignId, storageId);
        }
    }
}
