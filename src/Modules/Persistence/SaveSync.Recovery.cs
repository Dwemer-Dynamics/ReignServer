using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
    {
        // Kept outside both replaceable trees. A nonterminal journal is a fence even if
        // the campaign ledger, database, or independent backup cannot be opened.
        private sealed class SaveSyncOperation
        {
            public int Version { get; set; } = 1;
            public string CampaignId { get; set; }
            public string OperationId { get; set; }
            public string SavePointId { get; set; }
            public string LoadSessionId { get; set; }
            public string TargetSnapshotId { get; set; }
            public string SourceStateToken { get; set; }
            public string TargetStateToken { get; set; }
            public string ActivePath { get; set; }
            public string TargetPath { get; set; }
            public string BackupPath { get; set; }
            public string DisplacedPath { get; set; }
            public string RecoverySnapshotId => "recovery:" + OperationId;
            public bool HadActiveDirectory { get; set; }
            public string SourceInventorySha256 { get; set; }
            public string TargetInventorySha256 { get; set; }
            public string SourceDatabaseSha256 { get; set; }
            public string TargetDatabaseSha256 { get; set; }
            public string Phase { get; set; } = "preparing";
            public string Outcome { get; set; } = "recovery_required";
            public DateTime CompletedUtc { get; set; }
            public bool CleanupPending { get; set; }
            public bool WorkReleaseStarted { get; set; }
            public bool RetiringBackup { get; set; }
            public string FailedPhase { get; set; }
            public string RecoveryFailedPhase { get; set; }
            public string Error { get; set; }

            internal string WorkPath => Path.GetDirectoryName(DisplacedPath);
            internal string JournalPath => Path.Combine(SaveSyncJournalDirectory(CampaignId), OperationId + ".json");
            internal bool Terminal => Outcome is "committed" or "recovered" or "unchanged";
        }

        private sealed record SaveSyncFence(string Id, bool Applying = false);
        internal sealed class SaveSyncCampaignFencedException : InvalidOperationException
        {
            internal readonly Dictionary<string, object> Status;

            internal SaveSyncCampaignFencedException(string campaignId, string operation, bool applying)
                : base("Save Sync recovery_required for campaign " + campaignId + ", operation " + operation + ". The campaign is fenced.")
            {
                Status = SaveSyncFencedResult(campaignId, operation, applying);
            }

            internal SaveSyncCampaignFencedException(Dictionary<string, object> status)
                : base(ReadString(status, "error", "The campaign is fenced."))
            {
                Status = status;
            }
        }

        private static ImmutableDictionary<string, SaveSyncFence> recoveryFences =
            ImmutableDictionary.Create<string, SaveSyncFence>(StringComparer.Ordinal);
        private static ImmutableDictionary<string, SaveSyncFence> SaveSyncRecoveryFences =>
            System.Threading.Volatile.Read(ref recoveryFences);
        [ThreadStatic] private static string SaveSyncOperationOwner;
#if !REIGN_EXCLUDE_VERIFICATION
        [ThreadStatic] private static Action<string> SaveSyncRecoveryObserver;
#endif
        private static void SaveSyncRecoveryBoundary(string phase)
        {
#if !REIGN_EXCLUDE_VERIFICATION
            SaveSyncRecoveryObserver?.Invoke(phase);
#endif
        }

        private static string SaveSyncJournalDirectory(string campaignId, string campaignsRoot = null) =>
            Path.Combine(campaignsRoot ?? CampaignFileService.CampaignsRoot(), ".save-sync-work", "journals", campaignId);

        private static void RequireSaveSyncJournalCampaign(string campaignId)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            if (campaignId != campaignId.Trim())
                throw new InvalidDataException("The recovery campaign ID must exactly match its PostgreSQL identity.");
        }

        private static List<string> FencedSaveSyncCampaignIds() => SaveSyncRecoveryFences.Keys
            .Where(path => Path.GetDirectoryName(path) == SaveSyncJournalDirectory(""))
            .Select(Path.GetFileName).Concat(PortraitsService.FencedOutfitCampaigns()).Concat(CharactersService.FencedEditorCampaigns())
            .Distinct(StringComparer.Ordinal).ToList();

        private static SaveSyncFence FindSaveSyncFence(string campaignId)
        {
            var fences = SaveSyncRecoveryFences;
            if (fences.IsEmpty || string.IsNullOrWhiteSpace(campaignId)) return null;
            return fences.TryGetValue(SaveSyncJournalDirectory(campaignId), out var fence) ? fence : null;
        }

        internal static bool HasSaveSyncJournalFence(string campaignId) => FindSaveSyncFence(campaignId) != null;

        private static string FencedSaveSyncOperation(string campaignId) => FindSaveSyncFence(campaignId)?.Id
            ?? ReadString(PortraitsService.OutfitRecoveryStatus(campaignId), "operationId", null)
            ?? ReadString(CharactersService.EditorRecoveryStatus(campaignId), "operationId", null);

        internal static void RequireSaveSyncCampaignAvailable(string campaignId, bool allowEditorRecoveryWitness = false)
        {
            var fence = FindSaveSyncFence(campaignId);
            if (fence != null && fence.Id != SaveSyncOperationOwner)
                throw new SaveSyncCampaignFencedException(campaignId, fence.Id, fence.Applying);
            var outfit = PortraitsService.OutfitRecoveryStatus(campaignId);
            if (outfit != null) throw new SaveSyncCampaignFencedException(outfit);
            var editor = allowEditorRecoveryWitness ? null : CharactersService.EditorRecoveryStatus(campaignId);
            if (editor != null) throw new SaveSyncCampaignFencedException(editor);
        }

        internal static void RequireSaveSyncConnectionAvailable(System.Data.Common.DbConnection connection)
        {
            if (!SaveSyncRecoveryFences.IsEmpty || PortraitsService.HasOutfitRecoveryFences || CharactersService.HasEditorRecoveryFences)
                RequireSaveSyncCampaignAvailable(ReignPostgreSqlStorage.CampaignIdForConnection(connection));
        }

        internal static void RequireSaveSyncPathAvailable(string path)
        {
            if ((SaveSyncRecoveryFences.IsEmpty && !PortraitsService.HasOutfitRecoveryFences && !CharactersService.HasEditorRecoveryFences)
                || string.IsNullOrEmpty(path)) return;
            string relative = Path.GetRelativePath(CampaignFileService.CampaignsRoot(), path);
            int separator = relative.IndexOf(Path.DirectorySeparatorChar);
            if (separator > 0) RequireSaveSyncCampaignAvailable(relative.Substring(0, separator));
        }

        private static Dictionary<string, object> SaveSyncFencedResult(string campaignId, string operation, bool? applyingAtRead = null)
        {
            if (FindSaveSyncFence(campaignId) is not { } save || save.Id != operation)
            {
                var outfit = PortraitsService.OutfitRecoveryStatus(campaignId);
                if (outfit != null && ReadString(outfit, "operationId", "") == operation) return outfit;
                var editor = CharactersService.EditorRecoveryStatus(campaignId);
                if (editor != null && ReadString(editor, "operationId", "") == operation) return editor;
            }
            bool applying = applyingAtRead ?? (FindSaveSyncFence(campaignId) is { Applying: true } fence && fence.Id == operation);
            string outcome = applying ? "apply_in_progress" : "recovery_required";
            return new Dictionary<string, object>
            {
                ["ok"] = false, ["result"] = outcome, ["outcome"] = outcome, ["storageCategory"] = outcome,
                ["eligibleForPermanentDeletion"] = false, ["campaignId"] = campaignId, ["backupId"] = operation,
                ["journalPath"] = Path.Combine(SaveSyncJournalDirectory(campaignId), operation + ".json"),
                ["error"] = applying ? "A Save Sync load is in progress." : "The recorded Save Sync operation requires recovery."
            };
        }

        private static void FenceSaveSyncRecovery(string key, string operation) =>
            ImmutableInterlocked.Update(ref recoveryFences, current => current.SetItem(key, new SaveSyncFence(operation)));

        private static SaveSyncFence GetOrAddSaveSyncFence(string key, SaveSyncFence fence) =>
            ImmutableInterlocked.GetOrAdd(ref recoveryFences, key, fence);

        private static void ReleaseSaveSyncFence(string key, string operation) =>
            ImmutableInterlocked.Update(ref recoveryFences, current =>
                current.TryGetValue(key, out var fence) && fence.Id == operation ? current.Remove(key) : current);

        internal static void WriteRecoveryFile(string path, byte[] bytes, bool replace = true, UnixFileMode? mode = null,
            string temporaryDirectory = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            temporaryDirectory ??= Path.GetDirectoryName(path);
            Directory.CreateDirectory(temporaryDirectory);
            string temp = Path.Combine(temporaryDirectory, Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes);
                    stream.Flush(true);
                }
                if (mode.HasValue && OperatingSystem.IsLinux()) File.SetUnixFileMode(temp, mode.Value);
                File.Move(temp, path, replace);
            }
            finally { TryDeleteFile(temp); }
        }

        private static void PublishSaveSyncPhase(SaveSyncOperation op, string phase, bool first = false)
        {
            op.Phase = phase;
            SaveSyncRecoveryBoundary("journal." + phase);
            WriteRecoveryFile(op.JournalPath, JsonSerializer.SerializeToUtf8Bytes(op), !first);
        }

        private static byte[] SaveSyncTreeInventory(string root)
        {
            // Include technical assets, empty directories, path case and file bytes.
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(root, path)).OrderBy(path => path, StringComparer.Ordinal);
            var files = BackupFileManifest(root).OrderBy(row => ReadString(row, "path", ""), StringComparer.Ordinal);
            return JsonSerializer.SerializeToUtf8Bytes(new { directories = dirs, files });
        }

        private static string InventoryHash(byte[] inventory) => Convert.ToHexString(SHA256.HashData(inventory));

        private static void VerifySaveSyncTree(string root, string expected)
        {
            if (InventoryHash(SaveSyncTreeInventory(root)) != expected)
                throw new InvalidDataException("Save Sync file inventory differs from the verified operation receipt.");
        }

        private static Dictionary<string, object> ApplyPostgreSqlSaveSync(
            string campaignId, string savePointId, string loadSessionId, string pointRoot, string targetId, string calendarSha,
            Dictionary<string, object> manifest, Dictionary<string, object> ledger, Dictionary<string, object> point,
            Dictionary<string, object> result, Stopwatch timer)
        {
            RequireSaveSyncJournalCampaign(campaignId);
            string id = Guid.NewGuid().ToString("N");
            string active = StrictCampaignDirectory(campaignId);
            string work = SaveSyncCampaignWorkRoot(active, id, SaveSyncStorageKey(campaignId));
            string staged = Path.Combine(work, "staged");
            string liveSchema = ReignPostgreSqlStorage.CampaignSchemaName(campaignId);
            var op = new SaveSyncOperation
            {
                CampaignId = campaignId, OperationId = id, SavePointId = savePointId, LoadSessionId = loadSessionId,
                TargetSnapshotId = targetId, ActivePath = active, TargetPath = Path.Combine(pointRoot, "campaign"),
                SourceStateToken = ReadString(result, "currentStateToken", ""), TargetStateToken = ReadString(result, "targetStateToken", ""),
                HadActiveDirectory = Directory.Exists(active), DisplacedPath = Path.Combine(work, "active"),
                BackupPath = Path.Combine(SaveSyncCampaignRoot(campaignId), "r", id)
            };
            string key = SaveSyncJournalDirectory(campaignId);
            var applying = new SaveSyncFence(id, Applying: true);
            var present = GetOrAddSaveSyncFence(key, applying);
            if (!ReferenceEquals(present, applying))
                return SaveSyncFencedResult(campaignId, present.Id, present.Applying);
            bool started = false, restoreAttempted = false, ledgerAttempted = false;
            string previousOwner = SaveSyncOperationOwner;
            SaveSyncOperationOwner = id;
            try
            {
                CheckSaveSyncResourcePaths(op);
                PublishSaveSyncPhase(op, "preparing", first: true);
                started = true;
                string backup = Path.Combine(op.BackupPath, "campaign");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaignId, op.RecoverySnapshotId);
                Directory.CreateDirectory(backup);
                if (op.HadActiveDirectory) CopyDirectoryTree(active, backup, null);
                byte[] inventory = SaveSyncTreeInventory(backup);
                string sourceHash = InventoryHash(inventory);
                if (op.HadActiveDirectory) VerifySaveSyncTree(active, sourceHash);
                op.SourceDatabaseSha256 = SaveSyncDatabaseHash(ReignPostgreSqlStorage.SnapshotSchemaName(campaignId, op.RecoverySnapshotId));
                VerifySaveSyncDatabase(liveSchema, op.SourceDatabaseSha256);
                WriteRecoveryFile(Path.Combine(op.BackupPath, "inventory.json"), inventory, false);
                op.SourceInventorySha256 = sourceHash;
                CopyDirectoryTree(op.TargetPath, staged, path => !IsLiveSqliteSidecar(path));
                RequireSaveSyncCalendarDigest(calendarSha, SaveSyncCalendarDigest(ReadSaveSyncCalendar(staged)));
                // Raw manifests defer file integrity. The journal records the actual
                // staged inventory after the calendar matches its preflight value.
                if (ReadString(manifest, "snapshotState", "") == "optimized")
                {
                    var stagedFiles = BackupFileManifest(staged);
                    if (stagedFiles.Count != ReadLong(manifest, "fileCount", stagedFiles.Count)
                        || !string.Equals(SaveSyncManifestFingerprint(stagedFiles),
                            ReadString(manifest, "fingerprint", ""), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Staged files differ from the selected Save Sync manifest.");
                }
                int retained = MergeSaveSyncRetainedAssets(active, staged);
                op.TargetInventorySha256 = InventoryHash(SaveSyncTreeInventory(staged));
                op.TargetDatabaseSha256 = SaveSyncDatabaseHash(ReignPostgreSqlStorage.SnapshotSchemaName(campaignId, targetId));

                PublishSaveSyncPhase(op, "restoring_target_database");
                restoreAttempted = true; // A connection error can occur after the SQL transaction commits.
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaignId, targetId);
                SaveSyncRecoveryBoundary("target_restored");
                if (SaveSyncFailureInjectionForTests == "after_stage") throw new IOException("Injected target restore failure.");
                VerifySaveSyncDatabase(liveSchema, op.TargetDatabaseSha256);
                PublishSaveSyncPhase(op, "moving_active_files");
                if (op.HadActiveDirectory)
                {
                    MoveDirectoryWithRetries(active, op.DisplacedPath);
                    VerifySaveSyncTree(op.DisplacedPath, op.SourceInventorySha256);
                }
                PublishSaveSyncPhase(op, "installing_target_files");
                MoveDirectoryWithRetries(staged, active);
                SaveSyncRecoveryBoundary("target_installed");
                if (SaveSyncFailureInjectionForTests == "after_install") throw new IOException("Injected installation failure.");
                VerifySaveSyncTree(active, op.TargetInventorySha256);
                ClearSaveSyncCaches(campaignId);
                DialogueService.AdvanceMemoryRestoreGeneration(campaignId, point, "", loadSessionId);
                MarkSaveSyncActiveStateClean(campaignId, point);
                string generation = DialogueService.TerminalReportLoadProofGeneration(campaignId, savePointId, loadSessionId);
                if (generation.Length > 0) result["restoreGeneration"] = generation;
                result["result"] = "rolled_back";
                result["outcome"] = "committed";
                result["durationMs"] = timer.ElapsedMilliseconds;
                result["backupCreated"] = true;
                result["backupId"] = id;
                result["backupPath"] = op.BackupPath;
                result["databaseRestored"] = true;
                result["retainedStaticAssetFiles"] = retained;
                result["completedPhase"] = "verified";
                PublishSaveSyncPhase(op, "publishing_receipt");
                StoreSaveSyncLoadReceipt(ledger, savePointId, loadSessionId, result);
                UpdateSaveSyncLastResult(ledger, point, result, id);
                ledgerAttempted = true;
                SaveSyncRecoveryBoundary("ledger");
                WriteSaveSyncLedger(campaignId, ledger);
                FinishSaveSyncOperation(op, "committed");
            }
            catch (Exception ex)
            {
                op.FailedPhase = op.Phase;
                op.Error = ex.Message;
                try
                {
                    // If receipt publication or its acknowledgement was lost, a caller may
                    // already hold success. Preserve both sides and prohibit receipt replay.
                    if (ledgerAttempted) throw new IOException("Load receipt publication is indeterminate.");
                    if (restoreAttempted) RecoverSaveSyncOriginal(op);
                    if (started) FinishSaveSyncOperation(op, restoreAttempted ? "recovered" : "unchanged");
                    else op.Outcome = "unchanged";
                }
                catch (Exception recoveryError)
                {
                    op.Outcome = "recovery_required";
                    op.CompletedUtc = default;
                    op.CleanupPending = false;
                    FenceSaveSyncRecovery(key, id);
                    op.RecoveryFailedPhase = op.Phase;
                    op.Error += " Recovery: " + recoveryError.Message;
                    try { PublishSaveSyncPhase(op, "recovery_required"); }
                    catch (Exception journalError) { op.Error += " Journal: " + journalError.Message; }
                    var blocked = SaveSyncFencedResult(campaignId, id);
                    blocked["failedPhase"] = op.FailedPhase;
                    blocked["recoveryFailedPhase"] = op.RecoveryFailedPhase;
                    blocked["error"] = op.Error;
                    blocked["backupPath"] = op.BackupPath;
                    AppendSaveSyncAudit(campaignId, "load.recovery_required", blocked);
                    return blocked;
                }
                result = new Dictionary<string, object>
                {
                    ["ok"] = false, ["result"] = restoreAttempted ? "failed_recovered" : "failed_unchanged",
                    ["outcome"] = op.Outcome, ["campaignId"] = campaignId, ["savePointId"] = savePointId,
                    ["backupId"] = id, ["backupPath"] = op.BackupPath, ["backupCreated"] = op.SourceInventorySha256 != null,
                    ["failedPhase"] = op.FailedPhase, ["error"] = op.Error, ["durationMs"] = timer.ElapsedMilliseconds
                };
            }
            finally { SaveSyncOperationOwner = previousOwner; }

            // Only a successfully published terminal decision can release this campaign.
            // All work below is maintenance. None of its exceptions may enter compensation.
            ReleaseSaveSyncFence(key, id);
            if (started && !ReadBool(result, "ok", false))
            {
                // A recovered load must not publish the failed target as the loaded save.
                // The independent journal remains authoritative if this ledger write fails.
                try
                {
                    var current = ReadSaveSyncLedger(campaignId);
                    current["lastSyncResult"] = result;
                    current["lastBackupId"] = id;
                    current["updatedUtc"] = DateTime.UtcNow.ToString("o");
                    WriteSaveSyncLedger(campaignId, current);
                }
                catch (Exception ex)
                {
                    OperationsService.LogOperational("save_sync.result_ledger_unavailable",
                        TestDict("campaignId", campaignId, "operationId", id, "error", ex.Message));
                }
            }
            AppendSaveSyncAudit(campaignId, "load." + ReadString(result, "result", ""), result);
            if (started)
            {
                try { CleanupSaveSyncRecovery(campaignId); }
                catch (Exception ex)
                {
                    result["cleanupPending"] = true;
                    result["cleanupStatus"] = "cleanup_pending";
                    result["campaignCleanupPending"] = true;
                    // Retention can fail on an older operation after this one's work is clean.
                    try { AddSaveSyncOperationStatus(result, campaignId, id); }
                    catch (Exception receiptError) { result["cleanupReceiptError"] = receiptError.Message; }
                    OperationsService.LogOperational("save_sync.cleanup_pending", TestDict("campaignId", campaignId,
                        "operationId", id, "error", ex.Message));
                }
            }
            return result;
        }

        private static void CheckSaveSyncResourcePaths(SaveSyncOperation op)
        {
            string campaigns = CampaignFileService.CampaignsRoot();
            string active = Path.Combine(campaigns, op.CampaignId);
            string expectedWork = SaveSyncCampaignWorkRoot(active, op.OperationId, SaveSyncStorageKey(op.CampaignId));
            if (op.ActivePath != active || op.DisplacedPath != Path.Combine(expectedWork, "active")
                || op.BackupPath != Path.Combine(SaveSyncCampaignRoot(op.CampaignId), "r", op.OperationId))
                throw new InvalidDataException("Save Sync recovery resources do not match this installation.");
            string storage = SaveSyncCampaignRoot(op.CampaignId);
            if (IsSaveSyncPathWithin(campaigns, storage) || IsSaveSyncPathWithin(storage, campaigns))
                throw new InvalidDataException("Save Sync recovery storage must be independent of the campaigns directory.");
            RequirePhysicalSaveSyncPath(campaigns, op.ActivePath);
            RequirePhysicalSaveSyncPath(campaigns, op.DisplacedPath);
            RequirePhysicalSaveSyncPath(campaigns, op.JournalPath);
            RequirePhysicalSaveSyncPath(SaveSyncRoot(), op.BackupPath);
        }

        private static bool IsSaveSyncPathWithin(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path);
            return !Path.IsPathRooted(relative) && relative != ".."
                && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
        }

        private static void RequirePhysicalSaveSyncPath(string root, string path)
        {
            string relative = Path.GetRelativePath(root, path);
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
            {
                if (segment is ".." or "." || Path.IsPathRooted(segment))
                    throw new InvalidDataException("Save Sync operation path escaped its storage root.");
                root = Path.Combine(root, segment);
                try
                {
                    if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("Save Sync operation path traverses a link.");
                }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
            }
        }

        private static void FinishSaveSyncOperation(SaveSyncOperation op, string outcome)
        {
            op.CompletedUtc = DateTime.UtcNow;
            op.CleanupPending = true;
            op.Outcome = outcome;
            PublishSaveSyncPhase(op, outcome);
        }

        private static void RecoverSaveSyncOriginal(SaveSyncOperation op)
        {
            PublishSaveSyncPhase(op, "restoring_original_database");
            SaveSyncRecoveryBoundary("rollback_database");
            ReignPostgreSqlStorage.RestoreCampaignSnapshot(op.CampaignId, op.RecoverySnapshotId);
            VerifySaveSyncDatabase(ReignPostgreSqlStorage.CampaignSchemaName(op.CampaignId), op.SourceDatabaseSha256);
            PublishSaveSyncPhase(op, "restoring_original_files");
            RecoverSaveSyncOriginalFiles(op);
            ClearSaveSyncCaches(op.CampaignId);
            MarkSaveSyncActiveStateDirty(op.CampaignId);
        }

        private static void RecoverSaveSyncOriginalFiles(SaveSyncOperation op)
        {
            string staged = Path.Combine(op.WorkPath, "staged");
            bool displaced = Directory.Exists(op.DisplacedPath);
            // The staged target ceases to exist only when its atomic install rename succeeds.
            if (!Directory.Exists(staged) && Directory.Exists(op.ActivePath))
            {
                if (op.HadActiveDirectory && !displaced) throw new IOException("Displaced original is missing.");
                MoveDirectoryWithRetries(op.ActivePath, Path.Combine(op.WorkPath, "failed-target"));
            }
            if (op.HadActiveDirectory && !Directory.Exists(op.ActivePath))
            {
                // Leave both originals intact, including through failure of the final move.
                string restore = Path.Combine(op.WorkPath, "restore");
                SaveSyncRecoveryBoundary("rollback_copy");
                CopyDirectoryTree(displaced ? op.DisplacedPath : Path.Combine(op.BackupPath, "campaign"), restore, null);
                VerifySaveSyncTree(restore, op.SourceInventorySha256);
                SaveSyncRecoveryBoundary("rollback_move");
                MoveDirectoryWithRetries(restore, op.ActivePath);
            }
            if (op.HadActiveDirectory) VerifySaveSyncTree(op.ActivePath, op.SourceInventorySha256);
            else if (Directory.Exists(op.ActivePath)) throw new IOException("Unexpected active campaign directory.");
        }

        private static SaveSyncOperation ReadSaveSyncOperation(string campaignId, string path, string campaignsRoot = null)
        {
            RequireSaveSyncJournalCampaign(campaignId);
            campaignsRoot ??= CampaignFileService.CampaignsRoot();
            RequirePhysicalSaveSyncPath(campaignsRoot, path);
            var op = JsonSerializer.Deserialize<SaveSyncOperation>(File.ReadAllBytes(path));
            string id = Path.GetFileNameWithoutExtension(path);
            string expectedPath = Path.Combine(SaveSyncJournalDirectory(campaignId, campaignsRoot), id + ".json");
            if (op == null || op.Version != 1 || op.CampaignId != campaignId || op.OperationId != id
                || !Guid.TryParseExact(id, "N", out _)
                || expectedPath != path || (op.Terminal && (op.Phase != op.Outcome || op.CompletedUtc == default
                    || (op.Outcome != "unchanged" && (op.SourceInventorySha256?.Length != 64 || op.TargetInventorySha256?.Length != 64
                        || op.SourceDatabaseSha256?.Length != 64 || op.TargetDatabaseSha256?.Length != 64)))))
                throw new InvalidDataException("Save Sync recovery journal identity or phase is inconsistent.");
            return op;
        }

        internal static void RequireOfflineSaveSyncCampaignAvailable(string campaignId, string campaignsRoot)
        {
            RequireSaveSyncJournalCampaign(campaignId);
            CharactersService.RequireOfflineEditorCampaignAvailable(campaignsRoot, campaignId);
            PortraitsService.RequireOfflineOutfitCampaignAvailable(campaignsRoot, campaignId);
            string journals = SaveSyncJournalDirectory(campaignId, campaignsRoot);
            string directory = Path.GetDirectoryName(campaignsRoot);
            const FileAttributes directoryMask = FileAttributes.Directory | FileAttributes.ReparsePoint;
            if ((File.GetAttributes(directory) & directoryMask) != FileAttributes.Directory)
                throw new InvalidDataException("The managed campaign data directory is unavailable.");
            foreach (string segment in Path.GetRelativePath(directory, journals).Split(Path.DirectorySeparatorChar))
            {
                directory = Path.Combine(directory, segment);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(directory); }
                catch (FileNotFoundException) { return; }
                catch (DirectoryNotFoundException) { return; }
                if ((attributes & directoryMask) != FileAttributes.Directory)
                    throw new InvalidDataException("Save Sync recovery journal directory is unreadable for campaign " + campaignId + ".");
            }
            foreach (string path in Directory.GetFileSystemEntries(journals, "*.json"))
            {
                SaveSyncOperation op;
                try { op = ReadSaveSyncOperation(campaignId, path, campaignsRoot); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
                {
                    throw new InvalidDataException("Cannot read Save Sync recovery journal " + path + ".", ex);
                }
                if (!op.Terminal)
                    throw new InvalidOperationException("Save Sync recovery_required for campaign " + campaignId + ", operation " + op.OperationId + ".");
            }
        }

        internal static void CleanupAllSaveSyncRecoveryAtStartup()
        {
            foreach (string dir in SaveSyncJournalDirectories())
            {
                string campaignId = Path.GetFileName(dir);
                try { CleanupSaveSyncRecovery(campaignId); }
                catch (Exception ex)
                {
                    OperationsService.LogOperational("save_sync.startup_cleanup_pending",
                        TestDict("campaignId", campaignId, "error", ex.Message));
                }
            }
        }

        private static string[] SaveSyncJournalDirectories()
        {
            try { return Directory.GetDirectories(SaveSyncJournalDirectory("")); }
            catch (DirectoryNotFoundException) { return Array.Empty<string>(); }
        }

        private static string SaveSyncDeleteAllBlockReason()
        {
            // Read only. A deferred Delete All must also see an unresolved campaign
            // whose active directory, SQL campaign registry, or ordinary ledger is absent.
            try
            {
                if (PortraitsService.AnyOfflineOutfitPublication(CampaignFileService.CampaignsRoot()))
                    return "Campaign deletion is blocked by an interrupted outfit publication.";
                if (CharactersService.AnyOfflineEditorPublication(CampaignFileService.CampaignsRoot()))
                    return "Campaign deletion is blocked by an interrupted Character Editor revision.";
                foreach (string directory in SaveSyncJournalDirectories())
                    ReadSaveSyncOperations(Path.GetFileName(directory));
            }
            catch (Exception ex)
            {
                return "Campaign deletion could not verify Save Sync recovery journals. " + ex.Message;
            }
            string campaign = FencedSaveSyncCampaignIds().FirstOrDefault();
            return campaign == null ? "" : "Save Sync recovery_required for campaign " + campaign
                + ", operation " + FencedSaveSyncOperation(campaign) + ".";
        }

        private static List<SaveSyncOperation> ReadSaveSyncOperations(string campaignId)
        {
            string directory = SaveSyncJournalDirectory(campaignId);
            var operations = new List<SaveSyncOperation>();
            string reading = "unreadable";
            try
            {
                foreach (string path in Directory.GetFileSystemEntries(directory, "*.json"))
                {
                    reading = Path.GetFileNameWithoutExtension(path);
                    var op = ReadSaveSyncOperation(campaignId, path);
                    if (!op.Terminal)
                    {
                        FenceSaveSyncRecovery(directory, op.OperationId);
                        break;
                    }
                    operations.Add(op);
                }
            }
            catch
            {
                FenceSaveSyncRecovery(directory, reading);
                throw;
            }
            return operations;
        }

        private static void CleanupSaveSyncRecovery(string campaignId)
        {
            var operations = ReadSaveSyncOperations(campaignId);
            if (FencedSaveSyncOperation(campaignId) != null) return;
            foreach (var op in operations.Where(operation => operation.CleanupPending))
            {
                if (op.RetiringBackup) continue;
                SaveSyncRecoveryBoundary("cleanup_work");
                CheckSaveSyncResourcePaths(op);
                if (Directory.Exists(op.DisplacedPath))
                {
                    if (!op.WorkReleaseStarted) VerifySaveSyncTree(op.DisplacedPath, op.SourceInventorySha256);
                    VerifyRetainedSaveSyncBackup(op);
                }
                if (Directory.Exists(op.WorkPath))
                {
                    if (!op.WorkReleaseStarted)
                    {
                        op.WorkReleaseStarted = true;
                        PublishSaveSyncPhase(op, op.Outcome);
                    }
                    SaveSyncRecoveryBoundary("cleanup_deleting_work");
                    Directory.Delete(op.WorkPath, true);
                }
                if (op.SourceInventorySha256 == null && op.Outcome == "unchanged")
                {
                    ReignPostgreSqlStorage.DropSnapshot(campaignId, op.RecoverySnapshotId);
                    if (Directory.Exists(op.BackupPath)) Directory.Delete(op.BackupPath, true);
                    File.Delete(op.JournalPath);
                    continue;
                }
                op.CleanupPending = false;
                PublishSaveSyncPhase(op, op.Outcome);
            }

            // Legacy folders and unowned snapshots lack proof of eligibility. Count only
            // intact newer SQL/file pairs towards retention before releasing an older pair.
            var retained = operations.Where(op => op.SourceInventorySha256 != null)
                .OrderByDescending(op => op.CompletedUtc).ThenBy(op => op.OperationId, StringComparer.Ordinal).ToList();
            if (retained.Count <= SaveSyncRecoveryRetention) return;
            var older = retained.Skip(SaveSyncRecoveryRetention).ToList();
            foreach (var op in older.Where(operation => !operation.RetiringBackup))
            {
                op.CleanupPending = op.RetiringBackup = true;
                PublishSaveSyncPhase(op, op.Outcome);
            }
            foreach (var op in retained.Take(SaveSyncRecoveryRetention))
            {
                CheckSaveSyncResourcePaths(op);
                VerifyRetainedSaveSyncBackup(op);
            }
            foreach (var op in older)
            {
                CheckSaveSyncResourcePaths(op);
                ReignPostgreSqlStorage.DropSnapshot(campaignId, op.RecoverySnapshotId);
                if (Directory.Exists(op.BackupPath)) Directory.Delete(op.BackupPath, true);
                File.Delete(op.JournalPath);
            }
        }

        // Under CampaignDataGate, settle eligible work while the original SQL
        // and file witnesses still exist. The caller stages the returned local
        // journal directory with the campaign data it is removing or replacing.
        internal static string PrepareSaveSyncJournalsForCampaignRemoval(string campaignId)
        {
            RequireSaveSyncJournalCampaign(campaignId);
            RequireSaveSyncCampaignAvailable(campaignId);
            string directory = SaveSyncJournalDirectory(campaignId);
            RequirePhysicalSaveSyncPath(CampaignFileService.CampaignsRoot(), directory);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(directory); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("Expected a physical Save Sync journal directory for campaign " + campaignId + ".");
            CleanupSaveSyncRecovery(campaignId);
            var operations = ReadSaveSyncOperations(campaignId);
            RequireSaveSyncCampaignAvailable(campaignId);
            foreach (var op in operations)
            {
                CheckSaveSyncResourcePaths(op);
                if (Path.Exists(op.WorkPath))
                    throw new IOException("Campaign removal found outstanding Save Sync work for operation " + op.OperationId + ".");
            }
            return directory;
        }

        private static void VerifyRetainedSaveSyncBackup(SaveSyncOperation op)
        {
            RequirePhysicalSaveSyncPath(SaveSyncRoot(), Path.Combine(op.BackupPath, "inventory.json"));
            RequirePhysicalSaveSyncPath(SaveSyncRoot(), Path.Combine(op.BackupPath, "campaign"));
            if (!ReignPostgreSqlStorage.SnapshotExists(op.CampaignId, op.RecoverySnapshotId)
                || !string.Equals(FileSha256(Path.Combine(op.BackupPath, "inventory.json")),
                    op.SourceInventorySha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Retained Save Sync backup is incomplete.");
            VerifySaveSyncTree(Path.Combine(op.BackupPath, "campaign"), op.SourceInventorySha256);
            VerifySaveSyncDatabase(ReignPostgreSqlStorage.SnapshotSchemaName(op.CampaignId, op.RecoverySnapshotId), op.SourceDatabaseSha256);
        }

        private static void AddSaveSyncOperationStatus(Dictionary<string, object> result, string campaignId, string id)
        {
            if (!Guid.TryParseExact(id, "N", out _)) return;
            string path = Path.Combine(SaveSyncJournalDirectory(campaignId), id + ".json");
            if (!File.Exists(path)) return;
            SaveSyncRecoveryBoundary("status_journal");
            SaveSyncOperation op;
            try { op = ReadSaveSyncOperation(campaignId, path); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            result["outcome"] = op.Outcome;
            result["journalPath"] = path;
            result["cleanupPending"] = op.CleanupPending;
            result["cleanupStatus"] = op.CleanupPending ? "cleanup_pending" : "complete";
        }
    }
}
