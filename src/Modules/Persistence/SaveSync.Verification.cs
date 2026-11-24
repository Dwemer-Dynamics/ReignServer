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
#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunSaveSyncSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "save_sync", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            string token = Guid.NewGuid().ToString("N").Substring(0, 8);
            // Save Sync restores older schemas; they must reach the latest campaign migration.
            RunCampaignMigrationSelfTests(add, token);
            VerifyRegisteredPartyAgencyAcceptedReceiptProjection(add, token);
            VerifyActiveStatePublicationRetry(add);
            var loadedReceiptLedger = TestDict("lastLoadedSavePoint", TestDict("savePointId", "cached_point", "status", "ready"));
            var registeredLoadedPoint = TestDict("savePointId", "cached_point", "snapshotStorageId", "actual_retained_storage");
            add("loaded_receipt_uses_registered_storage_metadata",
                ReadString(ResolveLoadedSaveSyncPoint(loadedReceiptLedger,
                    new List<Dictionary<string, object>> { registeredLoadedPoint }), "snapshotStorageId", "") == "actual_retained_storage",
                "Cached public receipts resolve the current full point before snapshot availability checks.");
            var missingLoaded = PublicLoadedSaveSyncPoint("isolated_missing_point", loadedReceiptLedger, new List<Dictionary<string, object>>());
            add("unregistered_loaded_receipt_has_explicit_coverage_gap",
                ReadString(missingLoaded, "status", "") == "unregistered_loaded_point" && !ReadBool(missingLoaded, "snapshotAvailable", true)
                && PublicLoadedSaveSyncPoint("isolated_missing_point", TestDict(), new List<Dictionary<string, object>>()).Count == 0,
                "Absent loaded points do not invent legacy-snapshot failures or access another campaign.");
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
                Program.TestsDir, "native_save_inventory_" + token);
            SaveSyncEnabledOverrideForTests = true;
            SaveSyncSkipSemanticForTests = true;
            SaveSyncDisableBackgroundOptimizationForTests = true;
            CampaignMutationAllowDuringTests = true;
            try
            {
                VerifyTypedArrestEffectOwner(add);
                VerifyOwnedWorldHistoryEventProof(add);
                string fallbackRoot = Path.Combine(Program.DataDir, "ss");
                string stableRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bannerlord Reign",
                    "save-sync");
                string stableRuntimeRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Bannerlord Reign");
                add("configured_runtime_root_is_deployment_independent",
                    Program.ResolveRuntimeDataDirectory(stableRuntimeRoot, fallbackRoot)
                        == Path.GetFullPath(stableRuntimeRoot)
                    && Program.ResolveRuntimeDataDirectory("", fallbackRoot)
                        == Path.GetFullPath(fallbackRoot),
                    "Packaged binaries can move or update without relocating settings, campaign sidecars, logs, backups, or Save Sync state.");
                string migrationTestRoot = Path.Combine(
                    Program.TestsDir,
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
                    SettingsService.CopyDirectoryMissingOnly(migrationSource, migrationTarget);
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
                    "/var/lib/reign-test-runtime", "campaigns", campaignId);
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
                    ReignDatabase.OpenCampaignConnection(preexistingId))
                {
                    PersistenceStore.UpsertSchemaMeta2(preexistingConnection);
                }
                Dictionary<string, object> preexistingPayload = SaveSyncTestPointPayload(preexistingId, "pre_" + token, 1d, "Preexisting World History");
                preexistingPayload["campaignLabel"] = "Preexisting World History";
                Dictionary<string, object> preexistingRegistered = SaveSyncRegisterApi(preexistingPayload);
                Dictionary<string, object> preexistingMetadata = CampaignFileService.ReadJsonObject(Path.Combine(preexistingCampaign, "campaign.json"));
                add("preexisting_campaign_directory_gets_metadata", ReadBool(preexistingRegistered, "ok", false)
                    && ReadString(preexistingMetadata, "campaignId", "") == preexistingId
                    && ReadString(preexistingMetadata, "campaignLabel", "") == "Preexisting World History",
                    "Save registration creates campaign metadata even when world-history traffic created the campaign directory first.");
                VerifyLegacySaveSyncPreview(preexistingId, token, add);
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
                    Program.CampaignDataGate.EnterReadLock();
                    try
                    {
                        finalizeReaderReady.Set();
                        Thread.Sleep(1500);
                    }
                    finally { Program.CampaignDataGate.ExitReadLock(); }
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
                VerifySaveSyncCheckpointSlot(campaignId, siblingId, oldStoredPoint, add);
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
                VerifyLegacySaveSyncCapacity(preexistingId, token, capacityLedger, add);

                AppendSaveSyncTestFutureState(campaignId);
                Dictionary<string, object> newPayload = SaveSyncTestPointPayload(campaignId, newPoint, 20d, "Newest Save");
                Dictionary<string, object> newRegistered = SaveSyncRegisterApi(newPayload);
                add("register_divergent_newer_save", ReadBool(newRegistered, "ok", false), "A second divergent native save from the same campaign receives its own snapshot.");

                Dictionary<string, object> newest = SaveSyncLoadApi(newPayload, false);
                add("newest_save_no_op", ReadBool(newest, "ok", false) && ReadBool(newest, "noOp", false) && ReadString(newest, "result", "") == "no_op", "Loading the newest unchanged save is a zero-write no-op.");

                VerifyD60RawChangedFileCount(token, add);
                RunSaveSyncFencedRecoveryTests(add, token, campaignId);
                RunSaveSyncCommitJournalTest(add, token);
                string legacyRecovery = Path.Combine(SaveSyncCampaignRoot(campaignId), "r", "legacy-unowned");
                Directory.CreateDirectory(legacyRecovery);
                File.WriteAllText(Path.Combine(legacyRecovery, "original"), "legacy");
                SaveSyncRecoveryObserver = boundary =>
                {
                    if (boundary == "journal.restoring_target_database") throw new IOException("fixture journal write");
                };
                var unchanged = SaveSyncLoadApi(oldPayload, false);
                SaveSyncRecoveryObserver = null;
                add("prepare_journal_failure_preserves_current_state", ReadString(unchanged, "outcome", "") == "unchanged"
                    && SaveSyncTestMarkerExists(campaignId, "future") && !IsSaveSyncSemanticBlocked(campaignId),
                    "Failure to publish target-restore intent leaves the current campaign in place.");

                SaveSyncRecoveryObserver = boundary =>
                {
                    if (boundary == "target_restored") throw new IOException("fixture target acknowledgement");
                };
                var failedTarget = SaveSyncLoadApi(oldPayload, false);
                SaveSyncRecoveryObserver = null;
                add("target_sql_failure_recovers_before_file_swap", ReadString(failedTarget, "outcome", "") == "recovered"
                    && SaveSyncTestMarkerExists(campaignId, "future") && SaveSyncTestFutureSqlRows(campaignId) > 0,
                    "A failure immediately after target SQL restore recovers the pre-load database and files.");

                SaveSyncFailureInjectionForTests = "after_install";
                Dictionary<string, object> failed = SaveSyncLoadApi(oldPayload, false);
                SaveSyncFailureInjectionForTests = "";
                add("failed_rollback_recovers_active_state",
                    !ReadBool(failed, "ok", true) && ReadString(failed, "result", "") == "failed_recovered"
                    && ReadString(failed, "failedPhase", "") == "installing_target_files"
                    && SaveSyncTestMarkerExists(campaignId, "future"),
                    "An injected mid-swap failure reports the exact journal phase, restores the pre-sync campaign and retains a recovery snapshot.");

                string loadSessionId = "load_session_" + token;
                Dictionary<string, object> loadSessionPayload =
                    new Dictionary<string, object>(oldPayload)
                    {
                        ["loadSessionId"] = loadSessionId,
                        ["skipCleanLoadComparison"] = true
                    };
                // Terminal report recovery authority: the old snapshot's own restore generation, read
                // directly from its immutable schema. The live generation is advanced first, so the
                // context can only record the snapshot's value if the real restore brings back
                // memory_precision_state before the context captures it.
                string oldSnapshotGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryPrecisionStateSql(ReignPostgreSqlStorage.SnapshotSchemaName(campaignId, SaveSyncPostgreSqlSnapshotPointId(oldStoredPoint))), "restore_generation");
                DialogueService.AdvanceMemoryRestoreGeneration(campaignId);
                string preRollbackGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryPrecisionStateSql2, "restore_generation");
                bool partialCleanup = false;
                SaveSyncRecoveryObserver = boundary =>
                {
                    if (boundary == "audit.load.rolled_back") throw new IOException("fixture audit write");
                    if (boundary != "cleanup_deleting_work") return;
                    string id = ReadString(ReadSaveSyncLedger(campaignId), "lastBackupId", "");
                    var operation = ReadSaveSyncOperation(campaignId, Path.Combine(SaveSyncJournalDirectory(campaignId), id + ".json"));
                    string file = Path.Combine(operation.DisplacedPath, "characters", "hero_a", "state.json");
                    partialCleanup = operation.WorkReleaseStarted && File.Exists(file);
                    File.Delete(file);
                    throw new IOException("fixture interrupted recursive cleanup");
                };
                Dictionary<string, object> rolledBack = SaveSyncLoadApi(loadSessionPayload, false);
                Dictionary<string, object> previewAfterLoad = SaveSyncLoadApi(loadSessionPayload, true);
                add("preview_does_not_replay_a_completed_load",
                    ReadString(previewAfterLoad, "result", "") == "preview"
                    && ReadBool(previewAfterLoad, "dryRun", false)
                    && !previewAfterLoad.ContainsKey("idempotentReplay")
                    && !previewAfterLoad.ContainsKey("restoreGeneration"),
                    "A preview using a completed load session compares the current state without returning the applied load receipt.");
                add("d60_dirty_opt_in_keeps_rollback_comparison",
                    ReadString(rolledBack, "result", "") == "rolled_back"
                    && !rolledBack.ContainsKey("comparisonDiagnosticsOmitted")
                    && ReadString(rolledBack, "currentStateToken", "").Length > 0
                    && ReadString(rolledBack, "targetStateToken", "").Length > 0
                    && ReadDictionary(rolledBack, "countsBySubsystem") != null
                    && ReadInt(rolledBack, "changedFiles", 0) > 0,
                    "A dirty native load requesting lightweight comparison still reports its measured differences and rolls back.");
                add("d60_preview_opt_in_keeps_full_comparison",
                    ReadString(previewAfterLoad, "result", "") == "preview"
                    && !previewAfterLoad.ContainsKey("comparisonDiagnosticsOmitted")
                    && ReadDictionary(previewAfterLoad, "countsBySubsystem") != null
                    && previewAfterLoad.ContainsKey("changedFiles"),
                    "An opted-in preview, even after a completed load with the same session, still measures diagnostic differences.");
                string rollbackSourceGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryRestoredSourceContextSql, "source_generation");
                string rollbackLoadSession = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryRestoredSourceContextSql2, "load_session_id");
                SaveSyncRecoveryObserver = null;
                var pendingReplay = SaveSyncLoadApi(loadSessionPayload, false);
                add("verified_commit_survives_audit_and_cleanup_failure",
                    ReadString(rolledBack, "outcome", "") == "committed" && ReadBool(rolledBack, "cleanupPending", false)
                    && ReadBool(pendingReplay, "idempotentReplay", false) && ReadBool(pendingReplay, "cleanupPending", false)
                    && partialCleanup && !IsSaveSyncSemanticBlocked(campaignId),
                    "The committed load replays after interrupted removal of its verified displaced tree.");
                string committedJournal = Path.Combine(SaveSyncJournalDirectory(campaignId), ReadString(rolledBack, "backupId", "") + ".json");
                byte[] committedReceipt = File.ReadAllBytes(committedJournal);
                RequireOfflineSaveSyncCampaignAvailable(campaignId, CampaignFileService.CampaignsRoot());
                add("offline_maintenance_allows_terminal_pending_cleanup",
                    committedReceipt.SequenceEqual(File.ReadAllBytes(committedJournal))
                    && Directory.Exists(ReadSaveSyncOperation(campaignId, committedJournal).WorkPath),
                    "A settled campaign stays eligible for maintenance while its terminal receipt and pending cleanup remain intact.");
                CleanupSaveSyncRecovery(campaignId);
                add("retention_releases_only_owned_terminal_pairs",
                    !Directory.Exists(ReadString(unchanged, "backupPath", "")) && Directory.Exists(ReadString(failed, "backupPath", ""))
                    && !ReignPostgreSqlStorage.SnapshotExists(campaignId, "recovery:" + ReadString(unchanged, "backupId", ""))
                    && ReignPostgreSqlStorage.SnapshotExists(campaignId, "recovery:" + ReadString(failed, "backupId", ""))
                    && File.ReadAllText(Path.Combine(legacyRecovery, "original")) == "legacy",
                    "Two verified recovery pairs survive pruning together with unjournaled historical evidence.");
                add("rollback_context_records_snapshot_generation",
                    ReadString(rolledBack, "result", "") == "rolled_back"
                    && oldSnapshotGeneration.Length > 0 && preRollbackGeneration.Length > 0
                    && preRollbackGeneration != oldSnapshotGeneration
                    && rollbackSourceGeneration == oldSnapshotGeneration
                    && rollbackLoadSession == loadSessionId,
                    "A real rollback restores memory_precision_state with the snapshot, so the restore context records the target snapshot's own generation (not the pre-load live one) and the load session that bound it. snapshot="
                    + oldSnapshotGeneration + " preLoad=" + preRollbackGeneration + " context=" + rollbackSourceGeneration);
                Dictionary<string, object> rollbackCounts = ReadDictionary(rolledBack, "countsBySubsystem") ?? new Dictionary<string, object>();
                bool affectedCountsAuditable = new[] { "conversations", "events", "memories", "relationships", "worldHistory", "rumors", "actions", "diplomacy", "rebellions", "court", "reputation", "characterState" }
                    .All(subsystem => ReadLong(ReadDictionary(rollbackCounts, subsystem), "removed", 0) > 0);
                bool storesClean = !SaveSyncTestMarkerExists(campaignId, "future")
                    && SaveSyncTestFutureSqlRows(campaignId) == 0
                    && SaveSyncTestSqlValue(campaignId, PersistenceStore.SelectRelationshipPairChemistrySql, "affinity_a_to_b") == "1"
                    && SaveSyncTestSqlValue(campaignId, PersistenceStore.SelectCharacterReputationsSql, "reputation_value", campaignId) == "1"
                    && affectedCountsAuditable;
                add("older_save_rolls_back_all_temporal_stores", ReadBool(rolledBack, "ok", false) && ReadString(rolledBack, "result", "") == "rolled_back" && storesClean, "Older-save apply removes future conversations, events, memories, relationships, world history, rumors, actions, diplomacy, rebellions, court/reputation state, and character runtime state.");
                add("original_input_and_receipt_round_trip_exactly", SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectConversationTurnsSql, "text") == "  *I trace a 🌿.*\r\nHello.\t "
                    && SaveSyncTestSqlValue(campaignId, PersistenceStore.SelectConversationInputReceiptsSql, "source_hash")
                        == DialogueService.ConversationExactTextHash("  *I trace a 🌿.*\r\nHello.\t "),
                    "Author-only originals and receipt hashes survive snapshot restore byte-for-byte; future receipts and originals are removed with their branch.");
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
                Dictionary<string, object> retainedPortraitRoster = CampaignFileService.ReadJsonObject(
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
                // Terminal report load proof: binding each verified no-op load session leaves the
                // state clean (the next load of the same save stays a no-op), and both a rollback
                // and a no-op echo the restore generation bound to their own session.
                Dictionary<string, object> boundNoOp = SaveSyncLoadApi(new Dictionary<string, object>(newPayload) { ["loadSessionId"] = "noop_a_" + token }, false);
                var lightPayload = new Dictionary<string, object>(newPayload)
                {
                    ["loadSessionId"] = "noop_b_" + token,
                    ["skipCleanLoadComparison"] = true
                };
                Dictionary<string, object> repeatNoOp = SaveSyncLoadApi(lightPayload, false);
                string boundSession = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryRestoredSourceContextSql2, "load_session_id");
                string liveRestoreGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryPrecisionStateSql2, "restore_generation");
                add("no_op_load_proof_bind_keeps_no_op",
                    ReadString(boundNoOp, "result", "") == "no_op" && ReadString(repeatNoOp, "result", "") == "no_op"
                    && boundSession == "noop_b_" + token && liveRestoreGeneration.Length > 0
                    && ReadString(boundNoOp, "restoreGeneration", "") == liveRestoreGeneration
                    && ReadString(repeatNoOp, "restoreGeneration", "") == liveRestoreGeneration
                    && ReadString(rolledBack, "restoreGeneration", "").Length > 0,
                    "A verified no-op load binds its session without dirtying Save Sync state, and each verified load echoes the restore generation bound to it.");
                add("d60_clean_legacy_request_keeps_full_comparison",
                    ReadString(boundNoOp, "result", "") == "no_op"
                    && ReadString(boundNoOp, "loadSessionId", "") == "noop_a_" + token
                    && ReadString(boundNoOp, "restoreGeneration", "") == liveRestoreGeneration
                    && !boundNoOp.ContainsKey("comparisonDiagnosticsOmitted")
                    && ReadDictionary(boundNoOp, "countsBySubsystem") != null
                    && boundNoOp.ContainsKey("changedFiles"),
                    "A clean load with a valid session and no comparison preference keeps the original diagnostics and verified owner.");
                string[] omittedDiagnostics = { "currentStateToken", "targetStateToken", "currentDatabaseBytes",
                    "targetDatabaseBytes", "recordsRolledBack", "recordsRestored", "changedFiles", "countsBySubsystem" };
                bool LightResult(Dictionary<string, object> value) => value != null
                    && ReadBool(value, "comparisonDiagnosticsOmitted", false)
                    && omittedDiagnostics.All(key => !value.ContainsKey(key));
                Dictionary<string, object> lightLedger = ReadSaveSyncLedger(campaignId);
                Dictionary<string, object> lightReceipt = ReadDictionary(ReadDictionary(lightLedger, "lastLoadReceipt"), "response");
                Dictionary<string, object> lightAudit = Program.ReadJsonLinesFromPath(Path.Combine(
                    SaveSyncCampaignRoot(campaignId), "audit.jsonl")).LastOrDefault(
                        row => ReadString(row, "eventType", "") == "load.no_op");
                Dictionary<string, object> lightReplay = SaveSyncLoadApi(lightPayload, false);
                add("d60_clean_opt_in_retains_owner_and_light_receipt",
                    ReadString(repeatNoOp, "result", "") == "no_op"
                    && ReadString(repeatNoOp, "campaignId", "") == campaignId
                    && ReadString(repeatNoOp, "savePointId", "") == newPoint
                    && ReadString(repeatNoOp, "loadSessionId", "") == "noop_b_" + token
                    && ReadString(repeatNoOp, "restoreGeneration", "") == liveRestoreGeneration
                    && LightResult(repeatNoOp) && LightResult(lightReceipt)
                    && LightResult(ReadDictionary(lightLedger, "lastSyncResult")) && LightResult(lightAudit)
                    && LightResult(lightReplay) && ReadBool(lightReplay, "idempotentReplay", false)
                    && ReadString(lightReplay, "restoreGeneration", "") == liveRestoreGeneration,
                    "Verified clean native alignment and its replay preserve exact ownership and explicitly omit unmeasured diagnostics.");
                add("cross_campaign_isolation", FileSha256(siblingFile) == siblingHash, "Save-point registration and backward/forward restores never write to a sibling campaign.");
                VerifyCalendarCheckpointInstallation("ssc_" + token, add);

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
                string upload = Path.Combine(Program.DataDir, "backups", "uploads", Guid.NewGuid().ToString("N") + ".reignbackup");
                Dictionary<string, object> imported = TestDict("ok", false, "error", "Fixture backup export failed.");
                if (archiveReady)
                {
                    TryDeleteFile(oldNativeSavePath);
                    TryDeleteFile(newestNativeSavePath);
                    // Retire the fixture through the same transaction that settles its local journals.
                    Dictionary<string, object> removed = DeleteCampaignBackupData(
                        TestDict("campaignId", campaignId, "confirmation", campaignId));
                    bool destinationCleared = ReadBool(removed, "ok", false)
                        && ReadBool(removed, "campaignDeleted", false)
                        && ReadBool(removed, "cleanupComplete", false)
                        && !Path.Exists(SaveSyncJournalDirectory(campaignId));
                    if (destinationCleared)
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(upload));
                        File.Copy(archivePath, upload, true);
                        imported = ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "false" });
                    }
                    else
                    {
                        imported = TestDict("ok", false, "error",
                            "Fixture campaign retirement did not clear the destination.", "removal", removed);
                    }
                }
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
                // The real ImportCampaignArchive stamp (not a simulated update): the exported context
                // was bound to a verified no-op session, and the imported one names no load at all.
                string importedLoadSession = ReadBool(imported, "ok", false) ? SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryRestoredSourceContextSql2, "load_session_id") : "";
                add("import_stamps_restore_context_load_session",
                    boundSession == "noop_b_" + token
                    && importedLoadSession == Reign.Core.Contracts.Actions.TerminalActionReportProtocol.ImportedLoadSession,
                    "A backup import stamps the restored context with the imported-load marker, so no earlier load proof matches it. imported=" + importedLoadSession);
                add("export_import_preserves_save_sync", roundTrip, roundTrip ? "Campaign export/import round-trips the ledger, immutable save snapshots, status, recovery metadata, and exact native Bannerlord saves." : "Save Sync backup round trip failed. Export=" + Json.Serialize(export) + " Import=" + Json.Serialize(imported));
                TryDeleteFile(oldNativeSavePath);
                TryDeleteFile(newestNativeSavePath);

                bool defaultEnabled = ReadBool(SettingsService.DefaultSettings(), "saveSyncEnabled", false);
                string html = Program.ControlCenterHtml();
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
                    ReignDatabase.OpenCampaignConnection(databaseOnlyRetirementId)) { }
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

                ProcessPendingCampaignRetirementsAtStartup();
                add("startup_preserves_pending_retirement_for_background_monitor",
                    IsCampaignRetirementPending(activeRetirementId)
                    && ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == activeRetirementId)
                    && Directory.Exists(StrictCampaignDirectory(activeRetirementId)),
                    "Startup recovers staged transactions without executing pending campaign/vector deletion before HTTP health can answer; the existing monitor retains the durable retirement request.");

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
                    Path.Combine(CampaignFileService.CampaignsRoot(), "..", "escape"),
                    CampaignFileService.CampaignsRoot(), false); }
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
                }, CampaignFileService.CampaignsRoot(), false, orphanCleanupResults);
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
                var retirementPoint = SaveSyncTestPointPayload(recoveredFailureId, "retire_" + token, 1d, "Retirement " + token);
                SaveSyncRegisterApi(retirementPoint);
                AppendSaveSyncTestFutureState(recoveredFailureId);
                SaveSyncRecoveryObserver = phase =>
                {
                    if (phase == "cleanup_deleting_work") throw new IOException("fixture retirement cleanup");
                };
                var pendingRetirementLoad = SaveSyncLoadApi(retirementPoint, false);
                string terminalId = ReadString(pendingRetirementLoad, "backupId", "");
                var terminal = ReadSaveSyncOperation(recoveredFailureId, Path.Combine(SaveSyncJournalDirectory(recoveredFailureId), terminalId + ".json"));
                var deferred = DeleteCampaignOwnedDataCore(recoveredFailureId, "Deferred Cleanup", true, "test_deferred");
                SaveSyncRecoveryObserver = null;
                add("retirement_waits_for_verified_terminal_work",
                    ReadString(deferred, "result", "") == "delete_deferred" && terminal.WorkReleaseStarted
                    && Directory.Exists(terminal.DisplacedPath) && Directory.Exists(terminal.BackupPath)
                    && ReignPostgreSqlStorage.SnapshotExists(recoveredFailureId, terminal.RecoverySnapshotId),
                    "A failed terminal cleanup defers retirement while its SQL and file recovery resources remain verifiable.");
                CampaignRetirementFailureInjectionForTests = "after_stage";
                Dictionary<string, object> recoveredFailure =
                    DeleteCampaignOwnedDataCore(recoveredFailureId,
                        "Recovered Failure", true, "test_failure");
                CampaignRetirementFailureInjectionForTests = "";
                add("interrupted_retirement_restores_active_roots",
                    !ReadBool(recoveredFailure, "campaignDeleted", true)
                    && Directory.Exists(StrictCampaignDirectory(recoveredFailureId))
                    && File.Exists(terminal.JournalPath) && !Directory.Exists(terminal.WorkPath)
                    && ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == recoveredFailureId),
                    "A pre-commit staging failure rolls campaign-owned roots back and leaves the PostgreSQL campaign active.");
                var retired = VerifyCaseDistinctSaveSyncRetirement(add, terminal);
                add("retirement_releases_owned_terminal_journals_and_work",
                    ReadBool(retired, "campaignDeleted", false) && ReadBool(retired, "cleanupComplete", false)
                    && !Directory.Exists(SaveSyncJournalDirectory(recoveredFailureId)) && !Directory.Exists(terminal.WorkPath)
                    && !Directory.Exists(terminal.BackupPath)
                    && !ReignPostgreSqlStorage.SnapshotExists(recoveredFailureId, terminal.RecoverySnapshotId),
                    "Confirmed campaign retirement releases the previously settled work and its terminal journal, SQL and file backup.");

                VerifyCampaignRetirementVectors(add, "ssvec_" + token);

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
                // Unix permits unlinking an open file; verify the successful cleanup as well as failure reporting.
                lockedFailureVisible |= ReadBool(lockedResult, "campaignDeleted", false)
                    && ReadBool(lockedResult, "cleanupComplete", false)
                    && !File.Exists(lockedPath)
                    && !Directory.Exists(StrictCampaignDirectory(lockedCleanupId))
                    && !ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "") == lockedCleanupId);
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
                    && html.Contains("if(!confirm(`Are you sure you want to permanently delete all Reign data")
                    && html.Contains("if(!confirm('Are you sure you want to permanently delete Reign campaigns")
                    && html.Contains("if(!confirm('Are you sure you want to permanently delete every Reign campaign")
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
                SaveSyncRecoveryObserver = null;
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
                TryDeleteDirectory(SaveSyncJournalDirectory(campaignId));
                TryDeleteDirectory(StrictCampaignDirectory(orphanRootId));
                TryDeleteDirectory(SaveSyncCampaignRoot(orphanRootId));
                TryDeleteDirectory(CampaignPortraitCacheDirectory(orphanRootId));
                TryDeleteDirectory(nativeSaveFixture);
            }
            return results;
        }

        private static void VerifyD60RawChangedFileCount(string token, Action<string, bool, string> add)
        {
            string campaignId = "ssd60_" + token;
            string current = StrictCampaignDirectory(campaignId);
            string snapshot = Path.Combine(Path.GetTempPath(), "ReignSaveSyncD60_" + token);
            string target = Path.Combine(snapshot, "campaign");
            try
            {
                Directory.CreateDirectory(current);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(current, "added.txt"), "added");
                File.WriteAllText(Path.Combine(target, "removed.txt"), "removed");
                File.WriteAllText(Path.Combine(current, "changed.txt"), "after");
                File.WriteAllText(Path.Combine(target, "changed.txt"), "before");
                File.WriteAllText(Path.Combine(current, "unchanged.txt"), "stable");
                File.WriteAllText(Path.Combine(target, "unchanged.txt"), "stable");
                File.WriteAllText(Path.Combine(current, "campaign.json"), Json.Serialize(TestDict("retained", true)));
                var counts = TestDict("other", TestDict());
                int rawChanges = ComparePostgreSqlSaveSyncFiles(campaignId, snapshot,
                    TestDict("files", new List<Dictionary<string, object>>(), "integrityDeferred", true), counts);
                var rawCounts = ReadDictionary(counts, "other");
                int declaredChanges = ComparePostgreSqlSaveSyncFiles(campaignId, snapshot,
                    TestDict("files", BackupFileManifest(target), "integrityDeferred", false), TestDict("other", TestDict()));
                add("d60_changed_files_counts_distinct_measured_paths",
                    rawChanges == 3 && declaredChanges == 3
                    && ReadLong(rawCounts, "removed", 0) == 2 && ReadLong(rawCounts, "restored", 0) == 2,
                    "Both deferred raw and declared inventories count one added, one removed, and one content-changed path, excluding unchanged and retained files.");
            }
            finally
            {
                TryDeleteDirectory(current);
                TryDeleteDirectory(snapshot);
            }
        }
#endif

        private static void VerifyLegacySaveSyncPreview(
            string campaignId, string token, Action<string, bool, string> add)
        {
            const string previewPath = "/api/save-sync/preview";
            const string loadPath = "/save-sync/load";
            string pointId = "legacy_" + token;
            var legacy = SaveSyncTestPointPayload(campaignId, pointId, 2d, "Legacy Save");
            legacy["savePointKind"] = "legacy_baseline";

            Dictionary<string, object> Post(string path, Dictionary<string, object> payload)
            {
                var request = new Program.HttpRequest { Method = "POST", Path = path, JsonBody = payload };
                return (Dictionary<string, object>)Program.ServerRoutes.Value.Resolve(request.Method, path).Handler(request, null);
            }

            string[] QueuedOptimizations()
            {
                lock (SaveSyncOptimizationQueued)
                    return SaveSyncOptimizationQueued.Where(key => key.StartsWith(campaignId + "|", StringComparison.Ordinal))
                        .OrderBy(key => key, StringComparer.Ordinal).ToArray();
            }

            string State()
            {
                return Json.Serialize(TestDict(
                    "campaignFiles", BackupFileManifest(StrictCampaignDirectory(campaignId)),
                    "saveSyncFiles", BackupFileManifest(SaveSyncCampaignRoot(campaignId)),
                    "snapshotSchemas", ReignPostgreSqlStorage.ListSnapshots(campaignId),
                    "uniqueStates", SaveSyncUniqueStateCount(ReadSaveSyncLedger(campaignId)),
                    "queued", QueuedOptimizations()));
            }

            void CheckPreview(string id, string path, Dictionary<string, object> payload, string expectedResult, bool expectedOk = true)
            {
                string before = State();
                var response = Post(path, payload);
                add(id, ReadBool(response, "ok", !expectedOk) == expectedOk
                    && ReadBool(response, "dryRun", false)
                    && ReadString(response, "result", "") == expectedResult
                    && ReadString(response, "campaignId", "") == campaignId
                    && ReadString(response, "savePointId", "") == ReadString(payload, "savePointId", "")
                    && State() == before,
                    "Preview preserves campaign and Save Sync file hashes, snapshot schemas, capacity and queued optimizations. Expected "
                    + expectedResult + ", received " + ReadString(response, "result", "") + ".");
            }

            // Keep a scheduled optimization observable even if its worker runs immediately.
            bool previousDisable = SaveSyncDisableBackgroundOptimizationForTests;
            SaveSyncOptimizationSemaphore.Wait();
            try
            {
                SaveSyncDisableBackgroundOptimizationForTests = false;
                legacy["dryRun"] = false;
                CheckPreview("legacy_preview_does_not_share_clean_generation", previewPath, legacy, "legacy_baseline_registration_required");

                MarkSaveSyncActiveStateDirty(campaignId);
                legacy["dryRun"] = true;
                CheckPreview("legacy_dry_load_does_not_capture_dirty_generation", loadPath, legacy, "legacy_baseline_registration_required");

                var missing = SaveSyncTestPointPayload(campaignId, "missing_" + token, 3d, "Missing Save");
                missing["registrationConfirmed"] = false;
                CheckPreview("unregistered_preview_does_not_append_load_audit", previewPath, missing, "unregistered_save_baseline");
                missing["registrationConfirmed"] = true;
                CheckPreview("missing_preview_does_not_append_load_audit", previewPath, missing, "snapshot_missing", false);

                legacy["dryRun"] = false;
                legacy["worldHistoryOutboxDrained"] = false;
                string beforeFailure = State();
                var failed = Post(loadPath, legacy);
                add("legacy_load_preserves_registration_refusal",
                    !ReadBool(failed, "ok", true) && ReadString(failed, "result", "") == "critical_history_pending"
                    && ReadString(failed, "error", "").Length > 0 && !failed.ContainsKey("noOp")
                    && State() == beforeFailure,
                    "A refused legacy registration reports pending history and allows a later retry.");

                legacy["worldHistoryOutboxDrained"] = true;
                int previousCount = SaveSyncUniqueStateCount(ReadSaveSyncLedger(campaignId));
                var registered = Post(loadPath, legacy);
                var ledger = ReadSaveSyncLedger(campaignId);
                var point = SaveSyncPoint(ledger, pointId);
                add("legacy_load_captures_baseline_after_refusal",
                    ReadBool(registered, "ok", false) && ReadString(registered, "result", "") == "legacy_baseline_registered"
                    && !ReadBool(registered, "dryRun", true) && ReadBool(registered, "noOp", false) && point != null
                    && QueuedOptimizations().Contains(campaignId + "|" + pointId, StringComparer.OrdinalIgnoreCase)
                    && ReignPostgreSqlStorage.SnapshotExists(campaignId, pointId)
                    && File.Exists(Path.Combine(SaveSyncSnapshotRoot(campaignId, point), "manifest.json"))
                    && SaveSyncUniqueStateCount(ledger) == previousCount + 1,
                    "The real load captures a load-ready baseline, consumes one unique state and schedules its optimization.");
                CheckPreview("registered_legacy_point_previews_existing_snapshot", previewPath, legacy, "preview");

                string aliasId = "legacy_shared_" + token;
                var alias = new Dictionary<string, object>(legacy) { ["savePointId"] = aliasId };
                var shared = Post(loadPath, alias);
                var sharedLedger = ReadSaveSyncLedger(campaignId);
                var sharedPoint = SaveSyncPoint(sharedLedger, aliasId);
                add("legacy_load_shares_clean_baseline",
                    ReadBool(shared, "ok", false) && ReadString(shared, "result", "") == "legacy_baseline_registered"
                    && ReadBool(shared, "noOp", false) && point != null && sharedPoint != null
                    && SaveSyncPostgreSqlSnapshotPointId(sharedPoint) == pointId
                    && SaveSyncSnapshotRoot(campaignId, sharedPoint) == SaveSyncSnapshotRoot(campaignId, point)
                    && SaveSyncUniqueStateCount(sharedLedger) == previousCount + 1
                    && !ReignPostgreSqlStorage.SnapshotExists(campaignId, aliasId)
                    && !QueuedOptimizations().Contains(campaignId + "|" + aliasId, StringComparer.OrdinalIgnoreCase),
                    "A real legacy load can register an alias of the clean baseline without consuming a slot or scheduling another optimization.");
            }
            finally
            {
                SaveSyncDisableBackgroundOptimizationForTests = previousDisable;
                SaveSyncOptimizationSemaphore.Release();
                if (!SpinWait.SpinUntil(() => QueuedOptimizations().Length == 0, TimeSpan.FromSeconds(30)))
                    throw new TimeoutException("The legacy Save Sync fixture did not finish its queued optimization.");
            }
        }

        private static void VerifyLegacySaveSyncCapacity(
            string campaignId, string token, Dictionary<string, object> capacityLedger, Action<string, bool, string> add)
        {
            var originalLedger = ReadSaveSyncLedger(campaignId);
            string root = SaveSyncCampaignRoot(campaignId);
            string ledgerPath = Path.Combine(root, "ledger.json");
            string auditPath = Path.Combine(root, "audit.jsonl");
            try
            {
                WriteSaveSyncLedger(campaignId, capacityLedger);
                string ledgerHash = FileSha256(ledgerPath);
                int auditLines = File.ReadLines(auditPath).Count();
                string pointId = "legacy_at_limit_" + token;
                var payload = SaveSyncTestPointPayload(campaignId, pointId, 3d, "Unrelated Legacy Save");
                payload["savePointKind"] = "legacy_baseline";
                var refused = SaveSyncLoadApi(payload, false);
                var addedAudit = File.ReadLines(auditPath).Skip(auditLines)
                    .Select(line => Json.Deserialize<Dictionary<string, object>>(line)).ToArray();
                add("legacy_load_preserves_capacity_refusal",
                    !ReadBool(refused, "ok", true) && ReadString(refused, "result", "") == "snapshot_limit_reached"
                    && ReadString(refused, "error", "").Length > 0 && !refused.ContainsKey("noOp")
                    && ReadInt(refused, "remainingUniqueStates", -1) == 0 && FileSha256(ledgerPath) == ledgerHash
                    && !ReignPostgreSqlStorage.SnapshotExists(campaignId, pointId)
                    && addedAudit.Length == 1 && ReadString(addedAudit[0], "eventType", "") == "save_point.limit_reached"
                    && ReadString(addedAudit[0], "savePointId", "") == pointId,
                    "At the unique-state limit the real legacy load preserves the refusal, records the limit audit and leaves the ledger intact.");
            }
            finally { WriteSaveSyncLedger(campaignId, originalLedger); }
        }

#if !REIGN_EXCLUDE_VERIFICATION
        private static void VerifyCampaignRetirementVectors(Action<string, bool, string> add,
            string campaignId)
        {
            string vectorId = Guid.NewGuid().ToString();
            string pendingRoot = "";
            string crashRoot = "";
            bool previousSkip = SaveSyncSkipSemanticForTests;
            string previousFailure = CampaignRetirementFailureInjectionForTests;
            Action<IReadOnlyList<string>> previousTransport = CampaignRetirementDeleteVectorsForTests;
            try
            {
                PrepareSaveSyncTestCampaign(campaignId, "semantic");
                SeedRetirementVectorFixture(campaignId, new[] { vectorId });
                SaveSyncSkipSemanticForTests = false;
                List<string> attempts = new List<string>();
                bool transportSawRetiredSql = true;
                bool rejectVectorDelete = true;
                CampaignRetirementDeleteVectorsForTests = ids =>
                {
                    attempts.AddRange(ids);
                    transportSawRetiredSql &= !ReignPostgreSqlStorage.CampaignExists(campaignId);
                    if (rejectVectorDelete) throw new IOException("Injected vector worker failure.");
                };

                CampaignRetirementFailureInjectionForTests = "after_stage";
                Dictionary<string, object> rollback = DeleteCampaignOwnedDataCore(
                    campaignId, "Semantic Retirement", true, "test_vector_rollback");
                CampaignRetirementFailureInjectionForTests = "";
                add("retirement_rollback_never_deletes_semantic_vectors",
                    ReadString(rollback, "result", "") == "delete_failed_recovered"
                    && attempts.Count == 0
                    && ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && Directory.Exists(StrictCampaignDirectory(campaignId))
                    && ReadCampaignRetirementVectorIds(campaignId).SequenceEqual(new[] { vectorId }),
                    "The staged failure restores the active campaign and preserves indexed vector IDs without calling the vector worker.");

                Dictionary<string, object> retired = DeleteCampaignOwnedDataCore(
                    campaignId, "Semantic Retirement", true, "test_vector_retry");
                pendingRoot = ReadStringList(retired, "residualCleanup").FirstOrDefault(path =>
                    File.Exists(Path.Combine(path, CampaignRetirementVectorCleanupReceipt))) ?? "";
                CleanupCampaignRetirementStagingAtStartup();
                string receipt = Path.Combine(pendingRoot, CampaignRetirementVectorCleanupReceipt);
                add("retirement_vector_cleanup_is_durable_after_commit",
                    ReadBool(retired, "ok", false)
                    && ReadString(retired, "result", "") == "deleted_cleanup_pending"
                    && !ReadBool(retired, "cleanupComplete", true)
                    && attempts.SequenceEqual(new[] { vectorId }) && transportSawRetiredSql
                    && !ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && !Directory.Exists(StrictCampaignDirectory(campaignId))
                    && pendingRoot.Length > 0
                    && ReadStringList(CampaignFileService.ReadJsonObject(receipt), "ids").SequenceEqual(new[] { vectorId }),
                    "Worker failure leaves a committed cleanup receipt with the original IDs. Startup preserves it and does not contact the worker.");

                rejectVectorDelete = false;
                PrepareSaveSyncTestCampaign(campaignId, "replacement");
                CleanupCampaignRetirementStagingAtStartup();
                RetryCampaignRetirementVectorCleanup("");
                bool replacementProtected = attempts.Count == 1 && File.Exists(receipt)
                    && Directory.Exists(StrictCampaignDirectory(campaignId));
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                RetryCampaignRetirementVectorCleanup("");
                add("retirement_vector_retry_respects_recreated_campaign",
                    replacementProtected && transportSawRetiredSql
                    && attempts.SequenceEqual(new[] { vectorId, vectorId })
                    && pendingRoot.Length > 0 && !Directory.Exists(pendingRoot),
                    "A recreated SQL campaign blocks old vector cleanup. Once it is absent, the monitor retries the captured IDs and clears the staged receipt.");

                PrepareSaveSyncTestCampaign(campaignId, "retirement_receipt_crash");
                string[] seededIds = Enumerable.Range(0, CampaignSemanticDeleteBatchSize + 1)
                    .Select(_ => Guid.NewGuid().ToString()).ToArray();
                SeedRetirementVectorFixture(campaignId, seededIds);
                attempts.Clear();
                CampaignRetirementFailureInjectionForTests = "after_sql_drop_before_commit_receipt";
                Dictionary<string, object> interrupted = DeleteCampaignOwnedDataCore(
                    campaignId, "Semantic Retirement", true, "test_vector_commit_receipt_crash");
                CampaignRetirementFailureInjectionForTests = "";
                crashRoot = ReadStringList(interrupted, "residualCleanup").FirstOrDefault(path =>
                    File.Exists(Path.Combine(path, CampaignRetirementVectorCleanupReceipt))) ?? "";
                if (crashRoot.Length == 0)
                {
                    add("retirement_vector_retry_resumes_after_interrupted_receipt", false,
                        "The SQL-drop boundary did not retain a vector cleanup receipt.");
                    return;
                }
                string transactionPath = Path.Combine(crashRoot, "transaction.json");
                string crashReceiptPath = Path.Combine(crashRoot, CampaignRetirementVectorCleanupReceipt);
                List<string> captured = ReadStringList(CampaignFileService.ReadJsonObject(crashReceiptPath), "ids");
                bool sqlCommitted = ReadBool(interrupted, "campaignDeleted", false)
                    && !ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && ReadString(CampaignFileService.ReadJsonObject(transactionPath), "state", "") == "staging"
                    && captured.Count == seededIds.Length && seededIds.ToHashSet().SetEquals(captured);
                string temporaryFile = Path.Combine(crashRoot, "~0123abcd");
                File.WriteAllText(temporaryFile, "interrupted atomic replacement");
                CleanupCampaignRetirementStagingAtStartup();
                bool startupSealedCommit = attempts.Count == 0 && File.Exists(temporaryFile) &&
                    ReadString(CampaignFileService.ReadJsonObject(transactionPath), "state", "") == "committed";
                string unexpectedDirectory = Path.Combine(crashRoot, "~fedcba98");
                Directory.CreateDirectory(unexpectedDirectory);
                RetryCampaignRetirementVectorCleanup("");
                bool rejectsUnexpectedDirectory = attempts.Count == 0 && File.Exists(crashReceiptPath);
                Directory.Delete(unexpectedDirectory);
                RetryCampaignRetirementVectorCleanup("");
                bool firstBatchRecorded = attempts.SequenceEqual(captured.Take(CampaignSemanticDeleteBatchSize))
                    && ReadStringList(CampaignFileService.ReadJsonObject(crashReceiptPath), "ids")
                        .SequenceEqual(captured.Skip(CampaignSemanticDeleteBatchSize));
                RetryCampaignRetirementVectorCleanup("");
                add("retirement_vector_retry_resumes_after_interrupted_receipt",
                    sqlCommitted && startupSealedCommit && rejectsUnexpectedDirectory
                    && firstBatchRecorded && attempts.SequenceEqual(captured) && transportSawRetiredSql
                    && !Directory.Exists(crashRoot),
                    "Startup seals the witnessed SQL commit without worker access, and the monitor advances the remaining IDs despite the interrupted atomic write file.");
            }
            finally
            {
                CampaignRetirementDeleteVectorsForTests = previousTransport;
                CampaignRetirementFailureInjectionForTests = previousFailure;
                SaveSyncSkipSemanticForTests = previousSkip;
                if (pendingRoot.Length > 0)
                    DeleteCampaignStagingRoot(pendingRoot, new List<string>());
                if (crashRoot.Length > 0)
                    DeleteCampaignStagingRoot(crashRoot, new List<string>());
                if (ReignPostgreSqlStorage.CampaignExists(campaignId))
                    ReignPostgreSqlStorage.DropCampaign(campaignId);
                TryDeleteDirectory(StrictCampaignDirectory(campaignId));
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
                TryDeleteDirectory(CampaignPortraitCacheDirectory(campaignId));
            }
        }
        private static void SeedRetirementVectorFixture(string campaignId, IEnumerable<string> vectorIds)
        {
            using (ReignDbConnection connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId))
            {
                DialogueSchema.CreateEmbeddingDocuments(connection);
                foreach (string vectorId in vectorIds)
                    DialogueStore.UpsertEmbeddingDocuments(connection, TestDict("type", "memory",
                        "id", "retirement_fixture_" + vectorId, "model", "fixture", "provider", "local",
                        "campaign", campaignId, "timeline", "fixture", "hash", "fixture",
                        "vector", vectorId, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "payload", "{}"));
            }
        }
#endif

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

        private static void VerifySaveSyncCheckpointSlot(string campaignId, string siblingId,
            Dictionary<string, object> oldStoredPoint, Action<string, bool, string> add)
        {
            const string slot = "ReignTest_Inventory_Current";
            Dictionary<string, object> original = ReadSaveSyncLedger(campaignId);
            var archivedPoint = new Dictionary<string, object>(oldStoredPoint)
            {
                ["nativeSaveName"] = " reigntest_inventory_current.SAV ",
                ["capturedUtc"] = "2025-01-01T00:00:00Z",
                ["status"] = "ready",
                ["saveFinalizedUtc"] = "2025-01-01T00:00:01Z"
            };
            var points = Enumerable.Range(0, 50).Select(index => new Dictionary<string, object>
            {
                ["savePointId"] = "inventory_" + index.ToString(CultureInfo.InvariantCulture),
                ["nativeSaveName"] = "Inventory " + index.ToString(CultureInfo.InvariantCulture),
                ["capturedUtc"] = "2026-01-01T00:00:00Z",
                ["snapshotFingerprint"] = "unavailable-inventory-generation"
            }).ToList();
            points.Add(archivedPoint);

            Dictionary<string, object> Lookup(string id, string name) =>
                SaveSyncStatusRequestApi(new Dictionary<string, string>
                {
                    ["campaignId"] = id, ["checkpointSlot"] = name
                });

            try
            {
                var expanded = new Dictionary<string, object>(original) { ["points"] = points };
                WriteSaveSyncLedger(campaignId, expanded);
                var summary = SaveSyncStatusRequestApi(new Dictionary<string, string> { ["campaignId"] = campaignId });
                var projected = ReadDictionaryList(ReadDictionary(summary, "saveSync"), "points");
                var owned = Lookup(campaignId, slot);
                var ownedPoint = ReadDictionary(owned, "point");
                add("guarded_checkpoint_slot_reads_beyond_status_cap",
                    projected.Count == 50 && !projected.Any(point => MatchesCheckpointSlot(point, slot))
                    && ReadInt(owned, "registrationCount", -1) == 1
                    && ReadInt(owned, "registrationUniqueStateCount", -1) == 2
                    && ReadInt(ReadDictionary(summary, "saveSync"), "uniqueStateCount", -1) == 1
                    && !ReadBool(owned, "foreignRegistration", true) && ownedPoint != null
                    && ReadString(ownedPoint, "savePointId", "") == ReadString(oldStoredPoint, "savePointId", "")
                    && ReadString(ownedPoint, "status", "") == "ready"
                    && ReadBool(ownedPoint, "snapshotAvailable", false),
                    "An exact lookup returns the finalized checkpoint beyond the status cap and counts unavailable generations still charged at registration.");

                var foreign = Lookup(siblingId, slot);
                var vacant = Lookup(siblingId, "ReignTest_Vacant_Current");
                add("guarded_checkpoint_slot_distinguishes_hidden_foreign_and_vacant",
                    ReadBool(foreign, "foreignRegistration", false)
                    && ReadInt(foreign, "registrationCount", -1) == 0 && !foreign.ContainsKey("point")
                    && !ReadBool(vacant, "foreignRegistration", true)
                    && ReadInt(vacant, "registrationCount", -1) == 0 && !vacant.ContainsKey("point"),
                    "A case-insensitive foreign native slot remains occupied beyond the public summary cap, while an unrelated slot is vacant.");

                var distinctOwner = Lookup(campaignId.ToUpperInvariant(), slot);
                add("guarded_checkpoint_slot_recognizes_case_distinct_campaign",
                    ReadBool(distinctOwner, "ok", false) && ReadBool(distinctOwner, "foreignRegistration", false)
                    && ReadInt(distinctOwner, "registrationCount", -1) == 0 && !distinctOwner.ContainsKey("point"),
                    "The case-distinct registration owner is foreign even though native slot names are case-insensitive.");

                string journalRoot = SaveSyncJournalDirectory(campaignId);
                string operation = Guid.NewGuid().ToString("N");
                Dictionary<string, object> unresolved;
                FenceSaveSyncRecovery(journalRoot, operation);
                try { unresolved = Lookup(siblingId, slot); }
                finally { ReleaseSaveSyncFence(journalRoot, operation); }
                var restored = Lookup(siblingId, slot);
                add("guarded_checkpoint_slot_requires_unfenced_occupancy",
                    !ReadBool(unresolved, "ok", true)
                    && ReadString(unresolved, "result", "") == "checkpoint_occupancy_unverified"
                    && !unresolved.ContainsKey("foreignRegistration") && !unresolved.ContainsKey("backupId")
                    && ReadBool(restored, "ok", false) && ReadBool(restored, "foreignRegistration", false),
                    "A hidden foreign registration cannot prove vacancy, and settlement restores exact occupancy lookup.");

                points.Add(new Dictionary<string, object>(archivedPoint) { ["savePointId"] = "duplicate_inventory" });
                WriteSaveSyncLedger(campaignId, expanded);
                var ambiguous = Lookup(campaignId, slot);
                add("guarded_checkpoint_slot_exposes_duplicate_occupancy",
                    ReadInt(ambiguous, "registrationCount", -1) == 2 && !ambiguous.ContainsKey("point"),
                    "An ambiguous slot exposes its occupancy count and cannot stand in for one exact owned point.");
            }
            finally { WriteSaveSyncLedger(campaignId, original); }
        }

        private static void PrepareSaveSyncTestCampaign(string campaignId, string marker)
        {
            string campaign = StrictCampaignDirectory(campaignId);
            Directory.CreateDirectory(Path.Combine(campaign, "characters", "hero_a", "portraits"));
            CampaignFileService.WriteJsonObject(Path.Combine(campaign, "campaign.json"), new Dictionary<string, object> { ["campaignId"] = campaignId, ["campaignLabel"] = "Save Sync Test" });
            CampaignFileService.WriteJsonObject(Path.Combine(campaign, "portrait_roster.json"), new Dictionary<string, object>
            {
                ["version"] = 2,
                ["campaignId"] = campaignId,
                ["rosterFingerprint"] = marker,
                ["characters"] = new List<object>()
            });
            CampaignFileService.WriteJsonObject(Path.Combine(campaign, "characters", "hero_a", "background.json"), new Dictionary<string, object> { ["text"] = "old static definition" });
            File.WriteAllBytes(Path.Combine(campaign, "characters", "hero_a", "portraits", "portrait.png"), new byte[] { 1, 2, 3, 4 });
            CampaignFileService.WriteJsonObject(Path.Combine(campaign, "characters", "hero_a", "state.json"), new Dictionary<string, object> { ["knowledge"] = marker });
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
            CampaignFileService.WriteJsonObject(Path.Combine(campaign, "actions", "action-queue.json"), new Dictionary<string, object> { ["marker"] = marker, ["items"] = new List<object> { marker + "_queued_action" } });
            CampaignFileService.WriteJsonObject(Path.Combine(campaign, "world", "calendar.json"), new Dictionary<string, object>
            {
                ["marker"] = marker, ["Version"] = 1, ["CampaignId"] = campaignId,
                ["NativeDayAnchor"] = marker == "old" ? 10d : 20d,
                ["CalendarDayAnchor"] = marker == "old" ? 15d : 25d,
                ["AgeAnchors"] = TestDict("hero_a", TestDict("Day", 10d, "Age", 17.5d))
            });
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                RebellionService.EnsureRebellionSchema(connection);
                RelationshipsService.EnsureRelationshipDirectorSchema(connection);
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                CourtService.EnsureCourtSchema(connection);
                ReputationService.EnsureSocialReputationSchema(connection);
                long ts = marker == "old" ? 10 : 1;
                PersistenceStore.UpsertEvents(connection, new Dictionary<string, object> { ["id"] = marker + "_event", ["campaign"] = campaignId, ["ts"] = ts, ["day"] = (double)ts, ["utc"] = DateTime.UtcNow.ToString("o") });
                PersistenceStore.UpsertMemories(connection, new Dictionary<string, object> { ["id"] = marker + "_memory", ["event"] = marker + "_event", ["ts"] = ts, ["day"] = (double)ts });
                PersistenceStore.UpsertRelationshipPairChemistry(connection, new Dictionary<string, object>
                {
                    ["affinity"] = marker == "future" ? 99 : 1, ["day"] = (double)ts, ["ts"] = ts
                });
                PersistenceStore.UpsertRelationshipPairLifecycle(connection, new Dictionary<string, object> { ["day"] = (int)ts, ["ts"] = ts });
                PersistenceStore.UpsertConversationSessions(connection, new Dictionary<string, object> { ["id"] = marker + "_session", ["campaign"] = campaignId, ["ts"] = ts });
                PersistenceStore.UpsertConversationTurns(connection, new Dictionary<string, object> { ["id"] = marker + "_turn", ["session"] = marker + "_session", ["ts"] = ts });
                DialogueService.EnsureConversationInputSchema(connection);
                const string exactOriginal = "  *I trace a 🌿.*\r\nHello.\t ";
                PersistenceStore.UpsertConversationTurns2(connection, TestDict("id", marker + "_original", "session", marker + "_session", "text", exactOriginal, "ts", ts));
                PersistenceStore.InsertOrIgnoreConversationInputReceipts(connection, TestDict("id", marker + "_input", "session", marker + "_session", "source", marker + "_original", "hash", DialogueService.ConversationExactTextHash(exactOriginal), "ts", ts));
                PersistenceStore.UpsertRumorOccurrences(connection, new Dictionary<string, object>
                    {
                        ["id"] = marker + "_rumor_occurrence", ["campaign"] = campaignId,
                        ["event"] = marker + "_event", ["day"] = (double)ts,
                        ["summary"] = marker + "_rumor_summary", ["ts"] = ts
                    });
                PersistenceStore.UpsertRumorSubjectTags(connection, new Dictionary<string, object>
                    {
                        ["occurrence"] = marker + "_rumor_occurrence",
                        ["description"] = marker + "_rumor_description", ["ts"] = ts
                    });
                PersistenceStore.UpsertWorldHistoryEvents(connection, new Dictionary<string, object> { ["id"] = marker + "_history", ["campaign"] = campaignId, ["ts"] = ts, ["day"] = (double)ts, ["utc"] = DateTime.UtcNow.ToString("o") });
                PersistenceStore.UpsertRebellionMovements(connection, new Dictionary<string, object> { ["id"] = marker + "_rebellion", ["campaign"] = campaignId, ["day"] = (double)ts });
                PersistenceStore.UpsertCourtSessions(connection, new Dictionary<string, object> { ["id"] = marker + "_court", ["campaign"] = campaignId, ["ts"] = ts });
                PersistenceStore.UpsertCharacterReputations(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["occurrence"] = marker + "_rumor_occurrence",
                        ["description"] = marker + "_reputation", ["day"] = (double)ts, ["ts"] = ts
                    });
                PersistenceStore.UpsertDirectorOutbox(connection, new Dictionary<string, object> { ["id"] = marker + "_outbox", ["day"] = (double)ts, ["payload"] = "{\"marker\":\"" + marker + "\"}", ["ts"] = ts });
            }
        }

        private static void AppendSaveSyncTestFutureState(string campaignId)
        {
            PrepareSaveSyncTestCampaign(campaignId, "future");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                PersistenceStore.UpdateCharacterReputations(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
            }
            CampaignFileService.WriteJsonObject(Path.Combine(StrictCampaignDirectory(campaignId), "characters", "hero_a", "background.json"), new Dictionary<string, object> { ["text"] = "later static definition" });
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                sqlMarker = ReadLong(PersistenceStore.CountEvents(connection, new Dictionary<string, object> { ["id"] = marker + "_event" }).FirstOrDefault(), "count", 0) > 0;
            return fileMarker || sqlMarker;
        }

        private static long SaveSyncTestFutureSqlRows(string campaignId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                string[] queries =
                {
                    PersistenceStore.CountEventsSql,
                    PersistenceStore.CountMemoriesSql,
                    PersistenceStore.CountConversationTurnsSql,
                    PersistenceStore.CountConversationTurnsSql2,
                    PersistenceStore.CountConversationInputReceiptsSql,
                    PersistenceStore.CountRumorOccurrencesSql,
                    PersistenceStore.CountRumorSubjectTagsSql,
                    PersistenceStore.CountCharacterReputationsSql,
                    PersistenceStore.CountWorldHistoryEventsSql,
                    PersistenceStore.CountRebellionMovementsSql,
                    PersistenceStore.CountCourtSessionsSql,
                    PersistenceStore.CountDirectorOutboxSql
                };
                return queries.Sum(sql => ReadLong(PersistenceStore.QuerySql(connection, sql).FirstOrDefault(), "count", 0));
            }
        }

        internal static Dictionary<string, object> SaveSyncTestPointPayload(string campaignId, string pointId, double day, string name)
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
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> parameters = string.IsNullOrWhiteSpace(parameterCampaign) ? null : new Dictionary<string, object> { ["campaign"] = parameterCampaign };
                Dictionary<string, object> row = PersistenceStore.QuerySql(connection, sql, parameters).FirstOrDefault();
                object value = row != null && row.ContainsKey(field) ? row[field] : null;
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            }
        }

#if !REIGN_EXCLUDE_VERIFICATION
        private static void VerifyRegisteredPartyAgencyAcceptedReceiptProjection(Action<string, bool, string> add, string token)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
            {
                add("party_agency_postgres_requires_isolation", false, "The fixture requires the isolated ReignValidation database.");
                return;
            }

            const string generation = "fixture-current";
            const string oldGeneration = "fixture-prior";
            string ownedCampaign = "__postgres_pa_owned_" + token;
            string otherCampaign = "__postgres_pa_other_" + token;
            string bareCampaign = "__postgres_pa_bare_" + token;
            string missingCampaign = "__postgres_pa_missing_" + token;
            const string selectedUnit = "accepted_selected";
            const string otherSpeakerUnit = "accepted_other_speaker";
            const string oldUnit = "accepted_old_generation";
            const string otherCampaignUnit = "accepted_other_campaign";
            const string missingUnit = "accepted_missing";
            const string malformedUnit = "accepted_malformed";
            const string oversizedUnit = "accepted_oversized";
            const string oversizedProofUnit = "accepted_oversized_proof";
            string responseHash = DialogueService.PromptHash("synthetic response");
            string speechHash = DialogueService.ConversationExactTextHash("synthetic speech");
            var claim = new WorldSimulationService.PartyAgencySendClaim(
                "fixture-live", "fixture-command", "fixture-game", "fixture-process", "fixture-lease", 1);

            void Insert(ReignDbConnection connection, string campaign, string unit, string speaker, string restored,
                string receiptPadding = "", string originPadding = "")
            {
                var origin = new WorldSimulationService.PartyAgencySendOrigin(claim, campaign, "fixture-test",
                    "fixture-certification", "party_chat", speaker, "fixture-party-beat", speechHash)
                    .AcceptedReceipt(unit, responseHash);
                if (originPadding.Length > 0) origin["fixturePadding"] = originPadding;
                var provider = TestDict("schema", "reign-party-agency-serving-provider-attempt-v1",
                    "status", "serving_transport_matches_accepted_speech", "acceptedUnitId", unit,
                    "acceptedResponseHash", responseHash, "acceptedSpeechSha256", speechHash,
                    "providerAuthoredSpeechSha256", speechHash, "adapter", LlmProviderService.OpenRouterProvider,
                    "requestedModel", "fixture/requested", "servedModel", "fixture/served",
                    "servedModelEvidence", "http_response_model", "requestKind", "http_post_body",
                    "requestSha256", speechHash, "responseSha256", speechHash, "responseIdentitySha256", speechHash,
                    "providerAssistantContentSha256", speechHash, "fallbackDisposition", "none",
                    "httpRetriesInServingRound", 0, "codexTerminalAttempts", 0);
                var receipt = TestDict("ok", true, "status", "committed", "unitId", unit,
                    "responseHash", responseHash, "acceptedReplyHash", speechHash, "restoreGeneration", restored,
                    WorldSimulationService.PartyAgencySendOriginField, origin,
                    LlmProviderService.ServingProviderAttempt.ReceiptField, provider,
                    "fixtureUnprojectedSpeech", "fixture raw speech", "fixturePadding", receiptPadding);
                DialogueStore.InsertConversationAcceptedUnits(connection, TestDict("unit", unit, "hash", responseHash,
                    "generation", restored, "receipt", Json.Serialize(receipt), "ts", 1L));
            }

            void Initialize(ReignDbConnection connection)
            {
                DialogueSchema.CreateMemoryPrecisionState(connection);
                DialogueStore.InsertOrIgnoreMemoryPrecisionState(connection, TestDict("generation", generation, "ts", 1L));
                DialogueService.EnsureAcceptedConversationUnitSchema(connection);
            }

            bool RegistryHasSchema(ReignDbConnection connection, string campaign)
            {
                var row = ReignDatabase.QuerySql(connection, @"
SELECT EXISTS (SELECT 1 FROM reign_meta.campaign_registry WHERE campaign_id = $campaign)
    OR EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = $schema) AS present;",
                    TestDict("campaign", campaign, "schema", ReignPostgreSqlStorage.CampaignSchemaName(campaign))).Single();
                return ReadBool(row, "present", false);
            }

            string Snapshot(ReignDbConnection connection, string campaign) => Json.Serialize(ReignDatabase.QuerySql(connection, @"
SELECT r.xmin::text AS registry_version, s.xmin::text AS generation_version, s.restore_generation,
    (SELECT jsonb_agg(jsonb_build_array(u.xmin::text, to_jsonb(u)) ORDER BY u.unit_id)::text
        FROM conversation_accepted_units u) AS accepted_state
FROM reign_meta.campaign_registry r CROSS JOIN memory_precision_state s
WHERE r.campaign_id = $campaign AND s.state_id = 'current';", TestDict("campaign", campaign)).Single());

            try
            {
                using var owned = ReignPostgreSqlStorage.OpenCampaignConnection(ownedCampaign);
                using var other = ReignPostgreSqlStorage.OpenCampaignConnection(otherCampaign);
                using var bare = ReignPostgreSqlStorage.OpenCampaignConnection(bareCampaign);
                Initialize(owned);
                Initialize(other);
                Insert(owned, ownedCampaign, selectedUnit, "fixture-selected-speaker", generation);
                Insert(owned, ownedCampaign, otherSpeakerUnit, "fixture-other-speaker", generation);
                Insert(owned, ownedCampaign, oldUnit, "fixture-selected-speaker", oldGeneration);
                Insert(owned, ownedCampaign, oversizedUnit, "fixture-selected-speaker", generation,
                    receiptPadding: new string('x', 262144));
                Insert(owned, ownedCampaign, oversizedProofUnit, "fixture-selected-speaker", generation,
                    originPadding: new string('x', 8192));
                Insert(other, otherCampaign, otherCampaignUnit, "fixture-other-campaign-speaker", generation);
                DialogueStore.InsertConversationAcceptedUnits(owned, TestDict("unit", malformedUnit, "hash", responseHash,
                    "generation", generation, "receipt", "fixture malformed JSON", "ts", 1L));
                string ownedBefore = Snapshot(owned, ownedCampaign);
                string otherBefore = Snapshot(other, otherCampaign);

                var selected = ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(ownedCampaign,
                    new[] { selectedUnit, oldUnit, missingUnit, "invalid-fixture-selector" }).SingleOrDefault();
                var proof = selected == null ? TestDict() : Json.Deserialize<Dictionary<string, object>>(selected.ProofJson);
                var origin = ReadDictionary(proof, WorldSimulationService.PartyAgencySendOriginField);
                var provider = ReadDictionary(proof, LlmProviderService.ServingProviderAttempt.ReceiptField);
                add("party_agency_postgres_projects_exact_saved_receipt", selected?.UnitId == selectedUnit
                    && selected.ResponseHash == responseHash && selected.RestoreGeneration == generation
                    && ReadString(proof, "unitId", "") == selectedUnit && ReadString(proof, "restoreGeneration", "") == generation
                    && ReadString(origin, "campaignId", "") == ownedCampaign
                    && ReadString(origin, "speakerHeroStringId", "") == "fixture-selected-speaker"
                    && ReadString(origin, "correlationId", "") == "fixture-party-beat"
                    && ReadString(provider, "acceptedUnitId", "") == selectedUnit
                    && ReadString(provider, "servedModel", "") == "fixture/served"
                    && !proof.ContainsKey("fixtureUnprojectedSpeech") && !proof.ContainsKey("fixturePadding"),
                    "The exact selected speaker's current receipt is projected without extra saved fields or another speaker's receipt.");

                add("party_agency_postgres_excludes_foreign_or_missing_authority",
                    ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(ownedCampaign,
                        new[] { oldUnit, missingUnit, otherCampaignUnit }).Count == 0
                    && ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(otherCampaign,
                        new[] { selectedUnit }).Count == 0
                    && ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(otherCampaign,
                        new[] { otherCampaignUnit }).SingleOrDefault()?.UnitId == otherCampaignUnit,
                    "An old restore generation, an absent unit and a different registered campaign cannot supply a matching receipt.");

                add("party_agency_postgres_bounds_selector_and_projection",
                    ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(ownedCampaign,
                        new[] { oversizedUnit, oversizedProofUnit }).Count == 0
                    && ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(ownedCampaign,
                        new[] { selectedUnit }.Concat(Enumerable.Range(0, ReignPostgreSqlStorage.PartyAgencyAcceptedReceiptReadLimit)
                            .Select(number => "accepted_unused_" + number)).ToArray()).Count == 0
                    && ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(ownedCampaign + " ",
                        new[] { selectedUnit }).Count == 0,
                    "Over-limit selectors, a non-exact campaign identity and oversized receipt or proof JSON cannot contribute evidence.");

                bool malformedRejected = false;
                try { ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(ownedCampaign, new[] { malformedUnit }); }
                catch (Npgsql.PostgresException exception) when (exception.SqlState == "22P02") { malformedRejected = true; }
                add("party_agency_postgres_malformed_saved_receipt_fails_closed", malformedRejected,
                    "Invalid selected saved JSON fails the database projection before any receipt can be returned.");

                bool MissingReceiptTable() => ReignDatabase.QuerySql(bare,
                    @"SELECT NOT EXISTS (SELECT 1 FROM pg_catalog.pg_class relation
INNER JOIN pg_catalog.pg_namespace ns ON ns.oid = relation.relnamespace
WHERE ns.nspname = $schema AND relation.relname = 'conversation_accepted_units') AS missing;",
                    TestDict("schema", ReignPostgreSqlStorage.CampaignSchemaName(bareCampaign)))
                    .Any(row => ReadBool(row, "missing", false));
                bool tableMissingBefore = MissingReceiptTable();
                bool noSetup = false;
                try { ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(bareCampaign, new[] { selectedUnit }); }
                catch (Npgsql.PostgresException exception) when (exception.SqlState == "42P01") { noSetup = true; }
                add("party_agency_postgres_reader_never_sets_up_or_updates_campaign",
                    !RegistryHasSchema(owned, missingCampaign)
                    && ReignPostgreSqlStorage.ReadRegisteredPartyAgencyAcceptedReceipts(missingCampaign, new[] { selectedUnit }).Count == 0
                    && !RegistryHasSchema(owned, missingCampaign)
                    && tableMissingBefore && noSetup && MissingReceiptTable()
                    && Snapshot(owned, ownedCampaign) == ownedBefore && Snapshot(other, otherCampaign) == otherBefore,
                    "The reader leaves both registered row versions and saved evidence intact and does not register or initialize a schema.");
            }
            catch (Exception exception)
            {
                add("party_agency_postgres_fixture_exception", false,
                    "The disposable PostgreSQL fixture failed with " + exception.GetType().Name + ".");
            }
            finally
            {
                foreach (string campaign in new[] { bareCampaign, otherCampaign, ownedCampaign })
                {
                    try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                    catch (Exception exception)
                    {
                        add("party_agency_postgres_fixture_cleanup", false,
                            "Could not retire disposable fixture " + campaign + " (" + exception.GetType().Name + ").");
                    }
                }
            }
        }

        private static void VerifyActiveStatePublicationRetry(Action<string, bool, string> add)
        {
            string campaignId = "save_state_" + Guid.NewGuid().ToString("N");
            string fixtureRoot = SaveSyncCampaignRoot(campaignId);
            if (File.Exists(fixtureRoot) || Directory.Exists(fixtureRoot))
            {
                add("active_state_publication_fixture_isolated", false, "The disposable active-state root is already occupied.");
                return;
            }

            string path = SaveSyncActiveStatePath(campaignId);
            string campaignPath = Path.Combine(StrictCampaignDirectory(campaignId), "world", "fixture.json");
            const string oldPoint = "before_write", newPoint = "after_write";
            bool FailPublication(Action publish)
            {
                string savedSidecar = path + ".fixture_previous";
                File.Move(path, savedSidecar);
                try
                {
                    Directory.CreateDirectory(path);
                    try { publish(); }
                    catch (IOException) { return true; }
                    catch (UnauthorizedAccessException) { return true; }
                    return false;
                }
                finally
                {
                    if (Directory.Exists(path))
                        Directory.Move(path, path + ".fixture_obstruction_" + Guid.NewGuid().ToString("N"));
                    File.Move(savedSidecar, path);
                }
            }

            bool CachedStateMatches(string point, bool dirty)
            {
                lock (SaveSyncActiveStateLock)
                {
                    SaveSyncActiveState cached = ReadSaveSyncActiveStateLocked(campaignId);
                    return cached.SavePointId == point && cached.Dirty == dirty;
                }
            }

            try
            {
                MarkSaveSyncActiveStateClean(campaignId, TestDict("savePointId", oldPoint));
                string unrelatedTemporary = path + ".tmp_unrelated_fixture";
                const string unrelatedContent = "Retain the unrelated temporary";
                File.WriteAllText(unrelatedTemporary, unrelatedContent);
                byte[] clean = File.ReadAllBytes(path);
                bool directFailed = FailPublication(() => MarkSaveSyncActiveStateDirty(campaignId));
                bool pathFailed = FailPublication(() => MarkSaveSyncActiveStateDirtyForPath(campaignPath));
                bool cleanSurvived = CachedStateMatches(oldPoint, false) && clean.SequenceEqual(File.ReadAllBytes(path));
                MarkSaveSyncActiveStateDirtyForPath(campaignPath);
                var dirtySidecar = CampaignFileService.ReadAtomicallyReplacedJsonObject(path);
                add("failed_active_state_dirty_publication_can_retry",
                    directFailed && pathFailed && cleanSurvived && CachedStateMatches(oldPoint, true)
                    && ReadBool(dirtySidecar, "dirty", false) && ReadString(dirtySidecar, "savePointId", "") == oldPoint,
                    "Both dirty entry points propagate a real sidecar write failure, preserve the last published state, and can persist the retry.");

                byte[] dirty = File.ReadAllBytes(path);
                var nextPoint = TestDict("savePointId", newPoint);
                bool cleanFailed = FailPublication(() => MarkSaveSyncActiveStateClean(campaignId, nextPoint));
                bool dirtySurvived = CachedStateMatches(oldPoint, true) && dirty.SequenceEqual(File.ReadAllBytes(path));
                MarkSaveSyncActiveStateClean(campaignId, nextPoint);
                var cleanSidecar = CampaignFileService.ReadAtomicallyReplacedJsonObject(path);
                add("failed_active_state_clean_publication_preserves_cache",
                    cleanFailed && dirtySurvived && CachedStateMatches(newPoint, false)
                    && !ReadBool(cleanSidecar, "dirty", true) && ReadString(cleanSidecar, "savePointId", "") == newPoint,
                    "A failed clean publication retains the last persisted dirty point until the clean retry is written.");

                add("failed_active_state_publication_cleans_only_its_temporary", directFailed && pathFailed && cleanFailed
                    && Directory.EnumerateFiles(fixtureRoot, "active-state.json.tmp_*").SequenceEqual(new[] { unrelatedTemporary })
                    && File.ReadAllText(unrelatedTemporary) == unrelatedContent,
                    "Failed active-state attempts remove their own scratch files while preserving an unrelated sibling.");
                VerifyDirtyPublicationBeforeMetadataCommit(campaignId, path, FailPublication, add);
            }
            finally
            {
                lock (SaveSyncActiveStateLock) SaveSyncActiveStates.Remove(campaignId);
                if (Directory.Exists(fixtureRoot)) Directory.Delete(fixtureRoot, true);
            }
        }

        private static void VerifyDirtyPublicationBeforeMetadataCommit(string campaign, string path,
            Func<Action, bool> failPublication, Action<string, bool, string> add)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation)
            {
                add("dirty_before_commit_fixture_isolation", false, "The metadata fixture requires the isolated validation database.");
                return;
            }

            const string point = "metadata_fixture_point";
            try
            {
                using var writer = ReignPostgreSqlStorage.OpenCampaignConnection(campaign);
                using var reader = ReignPostgreSqlStorage.OpenCampaignConnection(campaign);
                ReignDatabase.ExecuteSql(writer, @"CREATE TABLE dirty_publication_fixture (
marker INTEGER PRIMARY KEY CHECK (marker > 0));");
                var activePoint = TestDict("savePointId", point);
                MarkSaveSyncActiveStateClean(campaign, activePoint);

                int Count(ReignDbConnection connection, ReignDbTransaction transaction = null) =>
                    ReadInt(ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM dirty_publication_fixture;",
                        transaction: transaction).Single(), "count", -1);
                // The raw-only maintenance candidate would use the returning query seam and explicitly publish dirty before commit.
                void Stage(int marker, ReignDbTransaction transaction) =>
                    ReignDatabase.QuerySql(writer, "INSERT INTO dirty_publication_fixture(marker) VALUES($marker) RETURNING marker;",
                        TestDict("marker", marker), transaction).Single();
                bool DiskIsDirty()
                {
                    var sidecar = CampaignFileService.ReadAtomicallyReplacedJsonObject(path);
                    return ReadString(sidecar, "savePointId", "") == point && ReadBool(sidecar, "dirty", false);
                }
                bool ReloadedStateIsDirty()
                {
                    lock (SaveSyncActiveStateLock)
                    {
                        SaveSyncActiveStates.Remove(campaign);
                        var reloaded = ReadSaveSyncActiveStateLocked(campaign);
                        return reloaded.SavePointId == point && reloaded.Dirty;
                    }
                }

                byte[] clean = File.ReadAllBytes(path);
                bool stagedBeforeFailure = false;
                bool failed = failPublication(() =>
                {
                    using var transaction = writer.BeginTransaction();
                    Stage(1, transaction);
                    stagedBeforeFailure = Count(writer, transaction) == 1 && Count(reader) == 0;
                    MarkSaveSyncActiveStateDirty(campaign);
                    transaction.Commit();
                });
                add("dirty_publication_failure_rolls_back_staged_metadata",
                    failed && stagedBeforeFailure && Count(reader) == 0 && clean.SequenceEqual(File.ReadAllBytes(path)),
                    "A real sidecar replace failure rolls back staged PostgreSQL metadata and leaves the last clean marker intact.");

                bool publishedBeforeCommit;
                using (var transaction = writer.BeginTransaction())
                {
                    Stage(1, transaction);
                    MarkSaveSyncActiveStateDirty(campaign);
                    publishedBeforeCommit = DiskIsDirty() && Count(writer, transaction) == 1 && Count(reader) == 0;
                    transaction.Commit();
                }
                add("dirty_publication_retry_is_visible_before_metadata_commit",
                    publishedBeforeCommit && Count(reader) == 1 && ReloadedStateIsDirty(),
                    "The same-process retry publishes the dirty checkpoint marker before PostgreSQL commit, and a fresh cache read observes it.");

                MarkSaveSyncActiveStateClean(campaign, activePoint);
                bool publishedBeforeSqlFailure = false, constraintRejected = false;
                try
                {
                    using var transaction = writer.BeginTransaction();
                    Stage(2, transaction);
                    MarkSaveSyncActiveStateDirty(campaign);
                    publishedBeforeSqlFailure = DiskIsDirty() && Count(writer, transaction) == 2 && Count(reader) == 1;
                    Stage(0, transaction);
                }
                catch (Npgsql.PostgresException exception) when (exception.SqlState == "23514")
                {
                    constraintRejected = true;
                }
                add("dirty_publication_remains_conservative_after_metadata_rollback",
                    publishedBeforeSqlFailure && constraintRejected && Count(reader) == 1
                    && DiskIsDirty() && ReloadedStateIsDirty(),
                    "A later PostgreSQL failure rolls back its staged row and leaves the already published dirty marker conservative.");
            }
            catch (Exception exception)
            {
                add("dirty_before_commit_fixture_exception", false, "The isolated metadata fixture failed with " + exception.GetType().Name + ".");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
            }
        }

        private static void VerifyOwnedWorldHistoryEventProof(Action<string, bool, string> add)
        {
            string campaign = "world_history_selftest_" + Guid.NewGuid().ToString("N");
            string absent = "world_history_selftest_absent_" + Guid.NewGuid().ToString("N");
            // Ordinary ingest queues reputation work unless this uses a recognized synchronous fixture ID.
            if (!CampaignFileService.IsInternalCampaignId(campaign) || !CampaignFileService.IsInternalCampaignId(absent))
                throw new InvalidOperationException("History proof fixtures must remain isolated from ordinary background work.");
            string[] roots = { StrictCampaignDirectory(campaign), SaveSyncCampaignRoot(campaign),
                StrictCampaignDirectory(absent), SaveSyncCampaignRoot(absent) };
            const string timeline = "main", eventId = "history_proof_evidence", ephemeralId = "history_proof_ephemeral";
            const string summary = "A witness established evidence for a captured noble.";
            string sidecar = SaveSyncActiveStatePath(campaign);

            bool Registered(string id)
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(id);
                return connection != null;
            }
            if (roots.Any(path => Directory.Exists(path) || File.Exists(path)) || Registered(campaign) || Registered(absent))
                throw new IOException("A disposable history event proof fixture root is already occupied.");

            string Generation()
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                return ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "<missing>");
            }

            string TimelineState()
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                return Json.Serialize(WorldSimulationStore.SelectWorldHistoryTimelines(connection, TestDict("id", timeline)).Single());
            }

            Dictionary<string, object> Upload(params Dictionary<string, object>[] events) =>
                WorldSimulationService.WorldHistoryIngestBatchApi(TestDict("campaignId", campaign,
                    "timelineId", timeline, "events", events.ToList(), "waitForRetention", true));

            try
            {
                var original = TestDict("eventId", eventId, "eventType", "arrest_evidence_reclassified", "sequence", 1L,
                    "worldDay", 18.25d, "locationId", "history_proof_town", "locationName", "Proof Town",
                    "source", "reign_system", "category", "justice", "phase", "evidence_established",
                    "summary", summary, "checksum", "fixture-checksum",
                    "payload", TestDict("caseId", "fixture_case", "priorStatus", "suspicion"),
                    "entities", new List<Dictionary<string, object>>
                    {
                        TestDict("entityId", "fixture_accused", "entityType", "hero", "role", "accused"),
                        TestDict("entityId", "fixture_witness", "entityType", "hero", "role", "witness")
                    });
                var ephemeral = new Dictionary<string, object>(original)
                {
                    ["eventId"] = ephemeralId, ["sequence"] = 2L, ["summary"] = "Disposable polling receipt"
                };
                const string unrelatedId = "history_proof_unrelated";
                var unrelated = new Dictionary<string, object>(original)
                {
                    ["eventId"] = unrelatedId, ["sequence"] = 3L, ["category"] = "diplomacy"
                };
                var initial = Upload(original, ephemeral, unrelated);
                string beforeRestore = Generation();
                DialogueService.AdvanceMemoryRestoreGeneration(campaign);
                string generation = Generation();
                if (beforeRestore == generation || generation == "<missing>" || !ReadBool(initial, "ok", false)
                    || ReadInt(initial, "accepted", -1) != 2 || ReadInt(initial, "ephemeral", -1) != 1)
                    throw new InvalidOperationException("The proof fixture could not establish its owner and two upload outcomes.");

                var conflicting = new Dictionary<string, object>(original)
                {
                    ["summary"] = "Content that did not win", ["payload"] = TestDict("caseId", "different_case"),
                    ["entities"] = new List<Dictionary<string, object>>()
                };
                var duplicate = Upload(conflicting);
                MarkSaveSyncActiveStateClean(campaign, TestDict("savePointId", "history_proof_active"));
                byte[] cleanSidecar = File.ReadAllBytes(sidecar);
                string timelineBefore = TimelineState();

                Dictionary<string, object> Proof(string id = eventId, string ownerCampaign = null,
                    string ownerTimeline = timeline, string ownerGeneration = null, string version = "1")
                {
                    var scope = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["eventProofVersion"] = version, ["campaignId"] = ownerCampaign ?? campaign,
                        ["timelineId"] = ownerTimeline
                    };
                    if (ownerGeneration != null) scope["sharedActivityGeneration"] = ownerGeneration;
                    return WorldSimulationService.WorldHistoryEventApi(scope, id);
                }

                var observed = Proof(ownerGeneration: generation);
                var row = ReadDictionary(observed, "event");
                var entities = ReadDictionaryList(row, "entities");
                var owner = ReadDictionary(observed, "ownership");
                add("owned_history_event_proof_observes_actual_persisted_material",
                    ReadBool(observed, "ok", false) && ReadString(observed, "result", "") == "observed"
                    && ReadInt(observed, "eventProofVersion", -1) == 1 && row != null && !row.ContainsKey("checksum")
                    && !row.ContainsKey("knowledgeRules") && !row.ContainsKey("semantic_text") && !row.ContainsKey("entities_json")
                    && ReadString(owner, "campaignId", "") == campaign && ReadString(owner, "timelineId", "") == timeline
                    && ReadString(owner, "sharedActivityGeneration", "") == generation
                    && ReadString(row, "event_id", "") == eventId && ReadLong(row, "sequence", -1) == 1
                    && ReadDouble(row, "world_day", -1) == 18.25d && ReadString(row, "location_id", "") == "history_proof_town"
                    && ReadString(row, "summary", "") == summary
                    && ReadString(TryParseJsonObject(ReadString(row, "payload_json", "{}")), "caseId", "") == "fixture_case"
                    && entities.Select(entity => ReadString(entity, "entity_id", ""))
                        .SequenceEqual(new[] { "fixture_accused", "fixture_witness" })
                    && ReadBool(duplicate, "ok", false) && ReadInt(duplicate, "duplicates", -1) == 1
                    && ReadInt(duplicate, "accepted", -1) == 0,
                    "A successful same-ID upload with a matching producer checksum and different content does not change the observed event.");

                var discarded = Proof(ephemeralId, ownerGeneration: generation);
                var wrongId = Proof(eventId.ToUpperInvariant(), ownerGeneration: generation);
                var unrelatedProof = Proof(unrelatedId, ownerGeneration: generation);
                add("owned_history_event_proof_requires_an_exact_durable_row",
                    !ReadBool(discarded, "ok", true) && ReadString(discarded, "result", "") == "not_found"
                    && !discarded.ContainsKey("event") && ReadString(wrongId, "result", "") == "not_found"
                    && ReadString(unrelatedProof, "result", "") == "not_found" && !unrelatedProof.ContainsKey("event"),
                    "Ephemeral ingest, a different spelling and an unrelated category cannot prove the exact justice event.");

                var stale = Proof(ownerGeneration: beforeRestore);
                var wrongTimeline = Proof(ownerTimeline: "elsewhere", ownerGeneration: generation);
                var unregistered = Proof(ownerCampaign: absent, ownerGeneration: generation);
                var missingGeneration = Proof();
                var unknownVersion = Proof(ownerGeneration: generation, version: "2");
                add("owned_history_event_proof_requires_current_explicit_scope",
                    new[] { stale, wrongTimeline, unregistered, missingGeneration }.All(result =>
                        ReadString(result, "result", "") == "owner_mismatch" && !result.ContainsKey("event")
                        && !result.ContainsKey("ownership"))
                    && ReadString(unknownVersion, "result", "") == "invalid_request"
                    && !unknownVersion.ContainsKey("event") && !Registered(absent)
                    && !Directory.Exists(roots[2]) && !Directory.Exists(roots[3]),
                    "Stale, missing, foreign and unsupported scope requests return no event and create no campaign registration.");

                var repeated = Proof(ownerGeneration: generation);
                bool clean;
                lock (SaveSyncActiveStateLock)
                {
                    var active = ReadSaveSyncActiveStateLocked(campaign);
                    clean = active?.SavePointId == "history_proof_active" && !active.Dirty;
                }
                bool sidecarUnchanged = File.ReadAllBytes(sidecar).SequenceEqual(cleanSidecar);
                bool timelineUnchanged = TimelineState() == timelineBefore;
                bool repeatedProofSucceeded = ReadBool(repeated, "ok", false);
                bool eventUnchanged = Json.Serialize(ReadDictionary(repeated, "event")) == Json.Serialize(row);
                bool proofReadOnly = clean && sidecarUnchanged && timelineUnchanged && repeatedProofSucceeded && eventUnchanged;
                const string readOnlySummary = "Repeated exact proof leaves the timeline head, persisted event and active checkpoint sidecar unchanged.";
                add("owned_history_event_proof_does_not_advance_or_dirty_history", proofReadOnly,
                    proofReadOnly ? readOnlySummary : $"{readOnlySummary} Clean checkpoint {clean}, sidecar unchanged {sidecarUnchanged}, "
                        + $"timeline unchanged {timelineUnchanged}, repeated proof succeeded {repeatedProofSucceeded}, event unchanged {eventUnchanged}.");

                var legacy = WorldSimulationService.WorldHistoryEventApi(
                    new Dictionary<string, string> { ["campaignId"] = campaign }, eventId);
                add("owned_history_event_proof_preserves_legacy_event_read",
                    ReadBool(legacy, "ok", false) && legacy.Count == 2
                    && ReadString(ReadDictionary(legacy, "event"), "checksum", "") == "fixture-checksum"
                    && ReadDictionary(legacy, "event").ContainsKey("knowledgeRules"),
                    "The unversioned event read retains its legacy result shape, checksum and knowledge rules.");

                // Queue the proof behind a different PostgreSQL backend holding the campaign advisory lock.
                // Commit only after PostgreSQL confirms the reader is actually waiting.
                const string nextGeneration = "fixture_predecessor_committed";
                using var predecessor = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                using var predecessorTransaction = predecessor.BeginTransaction();
                AcquirePostgreSqlTransactionMutationLock(predecessor, predecessorTransaction);
                DialogueStore.UpdateMemoryPrecisionState2(predecessor,
                    TestDict("generation", nextGeneration, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()), predecessorTransaction);
                var reading = Task.Run(() => Proof(ownerGeneration: generation));
                bool queued;
                try
                {
                    queued = SpinWait.SpinUntil(() =>
                        ReignDatabase.QuerySql(predecessor, @"SELECT pid FROM pg_locks
WHERE locktype='advisory' AND NOT granted AND pg_backend_pid()=ANY(pg_blocking_pids(pid));",
                            transaction: predecessorTransaction).Count > 0, TimeSpan.FromSeconds(5));
                }
                finally
                {
                    predecessorTransaction.Commit();
                }
                var superseded = reading.GetAwaiter().GetResult();
                var newOwner = Proof(ownerGeneration: nextGeneration);
                add("owned_history_event_proof_observes_predecessor_commit_after_lock_wait",
                    queued && ReadString(superseded, "result", "") == "owner_mismatch"
                    && !superseded.ContainsKey("event") && ReadString(newOwner, "result", "") == "observed",
                    "A proof blocked by a prior PostgreSQL connection refuses the obsolete generation after that connection commits.");

                Dictionary<string, object> CursorEvent(string id, long sequence) =>
                    new Dictionary<string, object>(original) { ["eventId"] = id, ["sequence"] = sequence };

                bool CursorsAt(long sequence, string id)
                {
                    using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                    var head = WorldSimulationStore.SelectWorldHistoryTimelines(connection, TestDict("id", timeline)).SingleOrDefault();
                    var ingest = ReignDatabase.QuerySql(connection, @"SELECT last_sequence,last_event_id
FROM world_history_ingest_state WHERE client_id='bannerlord' AND timeline_id=$timeline;",
                        TestDict("timeline", timeline)).SingleOrDefault();
                    return ReadLong(head, "head_sequence", -1) == sequence && ReadString(head, "head_event_id", "") == id
                        && ReadLong(ingest, "last_sequence", -1) == sequence && ReadString(ingest, "last_event_id", "") == id;
                }

                bool UploadStoresEvent(string id, long sequence)
                {
                    var uploaded = Upload(CursorEvent(id, sequence));
                    using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                    var stored = WorldSimulationStore.SelectWorldHistoryEvents4(connection,
                        TestDict("id", id, "timeline", timeline)).SingleOrDefault();
                    return ReadBool(uploaded, "ok", false) && ReadInt(uploaded, "accepted", -1) == 1
                        && ReadString(stored, "event_id", "") == id && ReadLong(stored, "sequence", -1) == sequence;
                }

                add("world_history_cursors_keep_matching_pair_after_late_accepted_event",
                    CursorsAt(3L, unrelatedId) && UploadStoresEvent("history_proof_late", 2L)
                    && CursorsAt(3L, unrelatedId),
                    "The ordinary wire still accepts the late row while both persisted cursors keep the newer pair.");

                add("world_history_cursors_keep_established_id_on_equal_sequence_conflict",
                    UploadStoresEvent("history_proof_equal_conflict", 3L) && CursorsAt(3L, unrelatedId),
                    "An independently persisted equal-sequence event cannot replace an established cursor ID.");

                const string nextEventId = "history_proof_newer";
                add("world_history_cursors_advance_both_fields_to_newer_event",
                    UploadStoresEvent(nextEventId, 4L) && CursorsAt(4L, nextEventId),
                    "A higher sequence advances the sequence and event ID together in both cursors.");

                using (var legacy = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                {
                    ReignDatabase.ExecuteSql(legacy, "UPDATE world_history_timelines SET head_event_id='' WHERE timeline_id=$timeline;",
                        TestDict("timeline", timeline));
                    ReignDatabase.ExecuteSql(legacy, @"UPDATE world_history_ingest_state SET last_event_id=''
WHERE client_id='bannerlord' AND timeline_id=$timeline;", TestDict("timeline", timeline));
                }
                const string legacyEventId = "history_proof_legacy_fill";
                add("world_history_cursors_initialize_empty_legacy_id_at_matching_sequence",
                    CursorsAt(4L, "") && UploadStoresEvent("history_proof_legacy_older", 3L) && CursorsAt(4L, "")
                    && UploadStoresEvent(legacyEventId, 4L) && CursorsAt(4L, legacyEventId),
                    "A blank legacy ID stays blank for earlier events and can be filled at its existing sequence.");
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                finally
                {
                    try { if (Registered(absent)) ReignPostgreSqlStorage.DropCampaign(absent); }
                    finally
                    {
                        lock (SaveSyncActiveStateLock)
                        {
                            SaveSyncActiveStates.Remove(campaign);
                            SaveSyncActiveStates.Remove(absent);
                        }
                        foreach (string root in roots)
                            if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }
            }
        }

        private static void VerifyTypedArrestEffectOwner(Action<string, bool, string> add)
        {
            string campaign = "arrest_owner_" + Guid.NewGuid().ToString("N");
            string missing = "arrest_absent_" + Guid.NewGuid().ToString("N");
            const string timeline = "main", accused = "arrest_a", player = "arrest_b";
            const string legacyCase = "legacy_case", legacyTag = "legacy_tag";
            const string typedCase = "typed_case", typedTag = "typed_tag";
            string pair = RelationshipsService.AmbientPairKey(accused, player);
            string campaignRoot = StrictCampaignDirectory(campaign), fixtureRoot = SaveSyncCampaignRoot(campaign);
            string sidecar = SaveSyncActiveStatePath(campaign);
            string missingCampaignRoot = StrictCampaignDirectory(missing), missingFixtureRoot = SaveSyncCampaignRoot(missing);
            string[] roots = { campaignRoot, fixtureRoot, missingCampaignRoot, missingFixtureRoot };
            if (roots.Any(path => Directory.Exists(path) || File.Exists(path)))
                throw new IOException("The disposable arrest owner fixture root is already occupied.");

            Dictionary<string, object> Apply(Dictionary<string, object> request)
            {
                var access = Program.EnterCampaignRequestAccess("/arrests/case/apply");
                try { return WorldSimulationService.ArrestCaseApplyApi(request); }
                finally { Program.ExitCampaignRequestAccess(access); }
            }

            Dictionary<string, object> Request(string caseId, string tag, int delta) =>
                TestDict("campaignId", campaign, "timelineId", timeline, "operation", "apply",
                    "caseId", caseId, "accusedHeroId", accused, "playerHeroId", player,
                    "reputationTagId", tag, "relationshipDelta", delta, "reputationValue", -8,
                    "worldDay", 17d, "description", "Disposable arrest owner fixture.");

            Dictionary<string, object> CaseKey(string caseId, string tag) =>
                TestDict("campaign", campaign, "timeline", timeline, "case", caseId,
                    "subject", accused, "player", player, "tag", tag);

            (int Affinity, int Cases, int Reputations, string Evidence) State(string caseId, string tag)
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                var chemistry = RelationshipsStore.SelectRelationshipPairChemistry8(connection, TestDict("pair", pair)).Single();
                var key = CaseKey(caseId, tag);
                var cases = WorldSimulationStore.SelectArrestCaseEffects(connection, key);
                var reputations = WorldSimulationStore.SelectCharacterReputations(connection, key);
                return (ReadInt(chemistry, "affinity_a_to_b", int.MinValue), cases.Count, reputations.Count,
                    Json.Serialize(TestDict("pair", chemistry, "cases", cases, "reputation", reputations)));
            }

            bool CommittedCase(string caseId, string tag, int delta)
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                var key = CaseKey(caseId, tag);
                var state = WorldSimulationStore.SelectArrestCaseEffects(connection, key).SingleOrDefault();
                var reputation = WorldSimulationStore.SelectCharacterReputations(connection, key).SingleOrDefault();
                var snapshot = TryParseJsonObject(ReadString(reputation, "snapshot_json", "{}"));
                return ReadString(state, "case_id", "") == caseId && ReadString(state, "tag_id", "") == tag
                    && ReadString(state, "accused_id", "") == accused && ReadString(state, "player_id", "") == player
                    && !ReadBool(state, "rescinded", true) && ReadString(snapshot, "arrestCaseId", "") == caseId
                    && ReadInt(snapshot, "arrestAppliedRelationshipDelta", int.MinValue) == delta;
            }

            string Generation()
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                return ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "<missing>");
            }

            string HistoryHead()
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                var row = WorldSimulationStore.SelectWorldHistoryTimelines(connection, TestDict("id", timeline)).Single();
                return Json.Serialize(TestDict("active", WorldSimulationService.ActiveWorldHistoryTimeline(connection),
                    "sequence", ReadLong(row, "head_sequence", -1), "event", ReadString(row, "head_event_id", "")));
            }

            bool Occupied(string id)
            {
                using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(id);
                return connection != null;
            }

            if (Occupied(campaign) || Occupied(missing))
                throw new IOException("The disposable arrest owner fixture campaign is already registered.");

            try
            {
                var opened = WorldSimulationService.WorldHistoryOpenTimelineApi(TestDict("campaignId", campaign, "timelineId", timeline));
                if (!ReadBool(opened, "ok", false) || ReadString(opened, "timelineId", "") != timeline)
                    throw new InvalidOperationException("The fixture did not open the expected World History timeline.");
                using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                {
                    RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                    var seeded = RelationshipsStore.InsertOrIgnoreRelationshipPairChemistry(connection,
                        TestDict("pair", pair, "a", accused, "b", player, "typeA", "ISTJ", "typeB", "ISTJ",
                            "baseAB", 50, "baseBA", 50, "chanceAB", 50, "chanceBA", 50, "day", 17,
                            "shard", 0, "version", 1, "ts", 0L));
                    if (seeded.Count != 1) throw new InvalidOperationException("The disposable arrest affinity row was not seeded.");
                }

                var legacy = Apply(Request(legacyCase, legacyTag, -4));
                add("arrest_effect_owner_legacy_sync_path_commits",
                    ReadBool(legacy, "ok", false) && ReadString(legacy, "operation", "") == "apply"
                    && ReadInt(legacy, "appliedRelationshipDelta", int.MinValue) == -4
                    && State(legacyCase, legacyTag).Affinity == -4 && CommittedCase(legacyCase, legacyTag, -4),
                    "The untyped synchronous caller still initializes storage and commits its exact affinity and reputation.");

                string oldGeneration = Generation();
                var typedRequest = Request(typedCase, typedTag, -6);
                typedRequest["sharedActivityGeneration"] = oldGeneration;
                DialogueService.AdvanceMemoryRestoreGeneration(campaign);
                string currentGeneration = Generation();
                using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                {
                    if (currentGeneration == "<missing>" || oldGeneration == "<missing>" || currentGeneration == oldGeneration
                        || WorldSimulationService.ActiveWorldHistoryTimeline(connection) != timeline)
                        throw new InvalidOperationException("The fixture did not advance the persisted restore owner on the same timeline.");
                }

                const string pointId = "arrest_fixture_active";
                MarkSaveSyncActiveStateClean(campaign, TestDict("savePointId", pointId));
                byte[] cleanSidecar = File.ReadAllBytes(sidecar);
                var untouched = State(typedCase, typedTag);
                if (untouched.Cases != 0 || untouched.Reputations != 0 || untouched.Affinity != -4)
                    throw new InvalidOperationException("The dormant typed case did not have the expected initial state.");
                bool StillClean()
                {
                    lock (SaveSyncActiveStateLock)
                    {
                        var cached = ReadSaveSyncActiveStateLocked(campaign);
                        return !cached.Dirty && cached.SavePointId == pointId && cleanSidecar.SequenceEqual(File.ReadAllBytes(sidecar));
                    }
                }

                var stale = Apply(typedRequest);
                add("arrest_effect_owner_stale_same_timeline_preserves_state",
                    !ReadBool(stale, "ok", true) && ReadString(stale, "status", "") == "ownership_mismatch"
                    && State(typedCase, typedTag) == untouched && !CommittedCase(typedCase, typedTag, -6) && StillClean(),
                    "The older restore request creates no case or reputation row, leaves the affinity row unchanged, and preserves the clean sidecar bytes and cache.");

                var invalid = new Dictionary<string, object>(typedRequest) { ["sharedActivityGeneration"] = true };
                var absent = new Dictionary<string, object>(typedRequest)
                { ["campaignId"] = missing, ["sharedActivityGeneration"] = currentGeneration };
                var malformedResult = Apply(invalid);
                var absentResult = Apply(absent);
                add("arrest_effect_owner_denies_malformed_and_unregistered",
                    !ReadBool(malformedResult, "ok", true) && ReadString(malformedResult, "status", "") == "ownership_mismatch"
                    && !ReadBool(absentResult, "ok", true) && ReadString(absentResult, "status", "") == "ownership_mismatch"
                    && !Occupied(missing) && !Directory.Exists(missingFixtureRoot)
                    && !Directory.Exists(missingCampaignRoot)
                    && State(typedCase, typedTag) == untouched && StillClean(),
                    "Malformed and unregistered typed requests perform no fallback registration or case, affinity, or active-state write.");

                const string aliasCase = "owner_alias_case", aliasTag = "owner_alias_tag";
                var aliasBefore = State(aliasCase, aliasTag);
                var alias = Request(aliasCase, aliasTag, -5);
                alias["SharedActivityGeneration"] = currentGeneration;
                var aliasBody = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(alias));
                if (aliasBody == null || !aliasBody.ContainsKey("SharedActivityGeneration") || aliasBody.ContainsKey("sharedActivityGeneration"))
                    throw new InvalidOperationException("The alternate-case fixture did not retain the wire discriminator.");
                var aliasResult = Apply(aliasBody);
                add("arrest_effect_owner_rejects_alternate_case_discriminator",
                    aliasBefore.Cases == 0 && aliasBefore.Reputations == 0
                    && !ReadBool(aliasResult, "ok", true) && ReadString(aliasResult, "status", "") == "ownership_mismatch"
                    && State(aliasCase, aliasTag) == aliasBefore && StillClean(),
                    "An alternate-case discriminator cannot fall back to the untyped route or change case, affinity, reputation, or sidecar state.");

                const string duplicateCase = "owner_duplicate_case", duplicateTag = "owner_duplicate_tag";
                var duplicateBefore = State(duplicateCase, duplicateTag);
                var duplicate = Request(duplicateCase, duplicateTag, -5);
                duplicate["sharedActivityGeneration"] = currentGeneration;
                duplicate["SharedActivityGeneration"] = currentGeneration;
                var duplicateBody = Json.Deserialize<Dictionary<string, object>>(Json.Serialize(duplicate));
                if (duplicateBody == null || duplicateBody.Keys.Count(key => string.Equals(key,
                    "sharedActivityGeneration", StringComparison.OrdinalIgnoreCase)) != 2)
                    throw new InvalidOperationException("The duplicate fixture did not retain both wire discriminator keys.");
                var duplicateResult = Apply(duplicateBody);
                add("arrest_effect_owner_rejects_multiple_equivalent_discriminators",
                    duplicateBefore.Cases == 0 && duplicateBefore.Reputations == 0
                    && !ReadBool(duplicateResult, "ok", true) && ReadString(duplicateResult, "status", "") == "ownership_mismatch"
                    && State(duplicateCase, duplicateTag) == duplicateBefore && StillClean(),
                    "Two case-equivalent discriminator keys are denied before any case, affinity, reputation, or sidecar change.");

                var current = Apply(new Dictionary<string, object>(typedRequest) { ["sharedActivityGeneration"] = currentGeneration });
                add("arrest_effect_owner_current_exact_scope_commits",
                    ReadBool(current, "ok", false) && ReadString(current, "caseId", "") == typedCase
                    && ReadString(current, "operation", "") == "apply" && ReadString(current, "reputationTagId", "") == typedTag
                    && ReadInt(current, "appliedRelationshipDelta", int.MinValue) == -6
                    && State(typedCase, typedTag).Affinity == -10 && CommittedCase(typedCase, typedTag, -6)
                    && ReadBool(CampaignFileService.ReadAtomicallyReplacedJsonObject(sidecar), "dirty", false),
                    "The exact registered active restore owner applies its own measured effect and marks the published sidecar dirty.");

                const string rollbackCase = "rollback_case", rollbackTag = "rollback_tag";
                const string addConstraint = "ALTER TABLE arrest_case_effect_state ADD CONSTRAINT d23_fixture_reject_case CHECK (case_id <> 'rollback_case') NOT VALID;";
                const string dropConstraint = "ALTER TABLE arrest_case_effect_state DROP CONSTRAINT d23_fixture_reject_case;";
                var retry = Request(rollbackCase, rollbackTag, -3);
                retry["sharedActivityGeneration"] = currentGeneration;
                var previousCase = State(rollbackCase, rollbackTag);
                var previousLegacy = State(legacyCase, legacyTag);
                var previousTyped = State(typedCase, typedTag);
                string previousHead = HistoryHead();
                if (previousCase.Cases != 0 || previousCase.Reputations != 0 || previousCase.Affinity != -10)
                    throw new InvalidOperationException("The disposable rollback case was not new.");
                MarkSaveSyncActiveStateClean(campaign, TestDict("savePointId", pointId));

                using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                {
                    ReignDatabase.ExecuteSql(connection, addConstraint);
                    try
                    {
                        // The new nonzero effect writes affinity and reputation before the final case INSERT.
                        var rejected = Apply(retry);
                        string error = ReadString(rejected, "error", "");
                        var persisted = CampaignFileService.ReadAtomicallyReplacedJsonObject(sidecar);
                        add("arrest_effect_owner_final_insert_rolls_back",
                            !ReadBool(rejected, "ok", true) && ReadString(rejected, "caseId", "") == rollbackCase
                            && error.Contains(Npgsql.PostgresErrorCodes.CheckViolation, StringComparison.Ordinal)
                            && error.Contains("d23_fixture_reject_case", StringComparison.Ordinal)
                            && State(rollbackCase, rollbackTag) == previousCase
                            && State(legacyCase, legacyTag) == previousLegacy && State(typedCase, typedTag) == previousTyped
                            && HistoryHead() == previousHead && Generation() == currentGeneration
                            && ReadString(persisted, "savePointId", "") == pointId,
                            "Rejecting the final PostgreSQL case insert rolls back the earlier affinity and reputation changes and preserves the case and history head.");
                    }
                    finally { ReignDatabase.ExecuteSql(connection, dropConstraint); }
                }

                var recovered = Apply(retry);
                var replayed = Apply(retry);
                var recoveredState = State(rollbackCase, rollbackTag);
                add("arrest_effect_owner_retry_after_rollback_commits_once",
                    ReadBool(recovered, "ok", false) && !ReadBool(recovered, "idempotent", true)
                    && ReadString(recovered, "caseId", "") == rollbackCase && ReadString(recovered, "reputationTagId", "") == rollbackTag
                    && ReadInt(recovered, "appliedRelationshipDelta", int.MinValue) == -3
                    && ReadBool(replayed, "ok", false) && ReadBool(replayed, "idempotent", false)
                    && ReadInt(replayed, "appliedRelationshipDelta", int.MinValue) == -3
                    && recoveredState.Affinity == -13 && recoveredState.Cases == 1 && recoveredState.Reputations == 1
                    && CommittedCase(rollbackCase, rollbackTag, -3) && HistoryHead() == previousHead,
                    "The same typed request commits once when the constraint is removed. Repeating it does not duplicate the affinity movement or case.");

                const string receiptCase = "versioned_case", receiptTag = "versioned_tag";
                string preReceiptPoint = "arrest_pre_receipt_" + Guid.NewGuid().ToString("N");
                string committedReceiptPoint = "arrest_committed_receipt_" + Guid.NewGuid().ToString("N");
                Dictionary<string, object> Versioned(long revision, int delta, string caseId = receiptCase, string tag = receiptTag)
                {
                    var request = Request(caseId, tag, delta);
                    request["arrestEffectVersion"] = 2;
                    request["expectedMaterialRevision"] = revision;
                    request["effectIntentId"] = revision.ToString(CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
                    request["sharedActivityGeneration"] = Generation();
                    request["chargeCategory"] = "espionage";
                    request["severity"] = "serious";
                    request["causeEstablished"] = true;
                    request["evidenceStatus"] = "established_after_arrest";
                    request["evidenceIds"] = "fixture_disclosed_evidence";
                    return request;
                }

                Dictionary<string, object> ReceiptState(string caseId = receiptCase, string tag = receiptTag)
                {
                    using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign);
                    return WorldSimulationStore.SelectArrestCaseEffects(connection, CaseKey(caseId, tag), includeReceipt: true).SingleOrDefault();
                }

                byte[] CleanBytes()
                {
                    MarkSaveSyncActiveStateClean(campaign, TestDict("savePointId", pointId));
                    return File.ReadAllBytes(sidecar);
                }

                bool StillCleanBytes(byte[] bytes)
                {
                    lock (SaveSyncActiveStateLock)
                    {
                        var cached = ReadSaveSyncActiveStateLocked(campaign);
                        return !cached.Dirty && cached.SavePointId == pointId && bytes.SequenceEqual(File.ReadAllBytes(sidecar));
                    }
                }

                Dictionary<string, object> Inspection(Dictionary<string, object> material)
                {
                    var query = new Dictionary<string, object>(material) { ["operation"] = "inspect" };
                    query.Remove("expectedMaterialRevision");
                    query.Remove("effectIntentId");
                    return Json.Deserialize<Dictionary<string, object>>(Json.Serialize(query));
                }

                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, preReceiptPoint);
                var firstIntent = Versioned(0, -7);
                var inspection = Inspection(firstIntent);
                var cleanInspection = CleanBytes();
                var beforeInspection = State(receiptCase, receiptTag);
                var ready = Apply(inspection);
                var forbiddenInspection = new Dictionary<string, object>(inspection) { ["effectIntentId"] = firstIntent["effectIntentId"] };
                var foreignInspection = new Dictionary<string, object>(inspection) { ["sharedActivityGeneration"] = oldGeneration };
                add("arrest_captured_repair_inspection_ready_is_read_only",
                    ReadBool(ready, "ok", false) && ReadString(ready, "status", "") == "ready"
                    && ReadString(ready, "operation", "") == "inspect" && ReadInt(ready, "arrestEffectVersion", 0) == 2
                    && ReadLong(ready, "materialRevision", -1) == 0
                    && ReadString(ready, "campaignId", "") == campaign && ReadString(ready, "timelineId", "") == timeline
                    && ReadString(ready, "caseId", "") == receiptCase && ReadString(ready, "reputationTagId", "") == receiptTag
                    && ReadString(ready, "playerHeroId", "") == player && ReadString(ready, "accusedHeroId", "") == accused
                    && ReadString(ready, "sharedActivityGeneration", "<missing>") == Generation()
                    && ReadString(Apply(forbiddenInspection), "status", "") == "invalid_effect_intent"
                    && ReadString(Apply(foreignInspection), "status", "") == "ownership_mismatch"
                    && State(receiptCase, receiptTag) == beforeInspection && ReceiptState() == null
                    && StillCleanBytes(cleanInspection),
                    "An exact registered inspection discovers the empty material revision. An injected intent or old owner cannot read or mutate it.");
                var firstReceipt = Apply(Json.Deserialize<Dictionary<string, object>>(Json.Serialize(firstIntent)));
                string committed = Json.Serialize(firstReceipt);
                var firstState = State(receiptCase, receiptTag);
                var firstStored = ReceiptState();
                add("arrest_effect_v2_commit_has_exact_measured_receipt",
                    ReadBool(firstReceipt, "ok", false) && ReadString(firstReceipt, "status", "") == "committed"
                    && ReadInt(firstReceipt, "arrestEffectVersion", 0) == 2
                    && ReadString(firstReceipt, "effectIntentId", "") == ReadString(firstIntent, "effectIntentId", "-")
                    && ReadLong(firstReceipt, "materialRevision", -1) == 1
                    && ReadLong(firstStored, "material_revision", -1) == 1 && ReadString(firstStored, "effect_receipt_json", "") == committed
                    && ReadInt(firstReceipt, "appliedRelationshipDelta", int.MinValue) == -7
                    && firstState.Affinity == -20 && CommittedCase(receiptCase, receiptTag, -7)
                    && ReadBool(CampaignFileService.ReadAtomicallyReplacedJsonObject(sidecar), "dirty", false),
                    "The exact successful measured response, revision, affinity and reputation commit together and dirty the active checkpoint.");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, committedReceiptPoint);

                var cleanReplay = CleanBytes();
                var changedMaterial = new Dictionary<string, object>(firstIntent) { ["worldDay"] = 18d };
                var malformedVersion = new Dictionary<string, object>(firstIntent);
                malformedVersion.Remove("arrestEffectVersion");
                malformedVersion["ArrestEffectVersion"] = 2;
                var changedRevision = new Dictionary<string, object>(firstIntent) { ["expectedMaterialRevision"] = 1 };
                bool replayIsReadOnly = Json.Serialize(Apply(firstIntent)) == committed
                    && ReadString(Apply(changedMaterial), "status", "") == "intent_conflict"
                    && ReadString(Apply(Json.Deserialize<Dictionary<string, object>>(Json.Serialize(malformedVersion))), "status", "") == "invalid_effect_intent"
                    && ReadString(Apply(changedRevision), "status", "") == "invalid_effect_intent";
                add("arrest_effect_v2_replay_and_conflicts_are_read_only",
                    replayIsReadOnly && State(receiptCase, receiptTag) == firstState
                    && Json.Serialize(ReceiptState()) == Json.Serialize(firstStored) && StillCleanBytes(cleanReplay),
                    "An exact replay returns the same committed object without rewriting the reputation or sidecar. Malformed or altered intents do not mutate.");

                var settledInspection = new Dictionary<string, object>(inspection) { ["worldDay"] = 22d };
                var settled = Apply(settledInspection);
                var changedEvidence = new Dictionary<string, object>(settledInspection) { ["evidenceIds"] = "later_material" };
                var mismatched = Apply(changedEvidence);
                add("arrest_captured_repair_inspection_uses_exact_active_material",
                    ReadBool(settled, "ok", false) && ReadString(settled, "status", "") == "settled"
                    && ReadBool(settled, "relationshipEffectResolved", false)
                    && ReadInt(settled, "appliedRelationshipDelta", int.MinValue) == -7
                    && ReadLong(settled, "materialRevision", -1) == 1
                    && !ReadBool(mismatched, "ok", true) && ReadString(mismatched, "status", "") == "material_mismatch"
                    && !ReadBool(mismatched, "relationshipEffectResolved", true)
                    && State(receiptCase, receiptTag) == firstState && Json.Serialize(ReceiptState()) == Json.Serialize(firstStored)
                    && StillCleanBytes(cleanReplay),
                    "An identical established case returns the measured effect at the existing revision. A conflicting disclosure cannot acquire permission to rebase it.");

                var secondIntent = Versioned(1, -9);
                var secondReceipt = Apply(secondIntent);
                var secondState = State(receiptCase, receiptTag);
                var cleanTyped = CleanBytes();
                var older = Apply(firstIntent);
                add("arrest_effect_v2_newer_typed_effect_fences_older",
                    ReadString(secondReceipt, "status", "") == "committed" && ReadLong(secondReceipt, "materialRevision", -1) == 2
                    && ReadString(older, "status", "") == "stale_material" && ReadLong(older, "materialRevision", -1) == 2
                    && secondState.Affinity == -22 && State(receiptCase, receiptTag) == secondState && StillCleanBytes(cleanTyped),
                    "A previous committed intent cannot replay success or overwrite a later typed material revision.");

                var legacyReplacement = Apply(Request(receiptCase, receiptTag, -4));
                var legacyState = State(receiptCase, receiptTag);
                var cleanLegacy = CleanBytes();
                var beforeLegacyIntent = Apply(secondIntent);
                var legacyReceiptRow = ReceiptState();
                add("arrest_effect_v2_ordinary_and_generation_only_writes_fence_older",
                    ReadBool(legacyReplacement, "ok", false) && !legacyReplacement.ContainsKey("arrestEffectVersion")
                    && ReadString(beforeLegacyIntent, "status", "") == "stale_material" && ReadLong(beforeLegacyIntent, "materialRevision", -1) == 3
                    && ReadString(legacyReceiptRow, "effect_intent_id", "<missing>") == ""
                    && legacyState.Affinity == -17 && State(receiptCase, receiptTag) == legacyState && StillCleanBytes(cleanLegacy),
                    "An ordinary writer updates the revision and clears the old receipt without exposing v2 fields on the legacy response.");

                var ownedRescind = Request(receiptCase, receiptTag, 0);
                ownedRescind["operation"] = "rescind";
                ownedRescind["sharedActivityGeneration"] = Generation();
                var revoked = Apply(ownedRescind);
                var terminalState = State(receiptCase, receiptTag);
                var cleanTerminal = CleanBytes();
                var lateApply = Apply(secondIntent);
                add("arrest_effect_v2_late_apply_cannot_revive_rescission",
                    ReadBool(revoked, "ok", false) && !revoked.ContainsKey("arrestEffectVersion")
                    && ReadString(lateApply, "status", "") == "terminal_rescinded" && ReadString(lateApply, "operation", "") == "apply"
                    && ReadLong(lateApply, "materialRevision", -1) == 4 && terminalState.Affinity == -13
                    && State(receiptCase, receiptTag) == terminalState && StillCleanBytes(cleanTerminal),
                    "The generation-only rescission keeps its original shape and defeats a stale typed apply with an explicit terminal result.");

                var terminalInspection = Apply(inspection);
                add("arrest_captured_repair_terminal_inspection_never_reapplies",
                    !ReadBool(terminalInspection, "ok", true) && ReadString(terminalInspection, "operation", "") == "inspect"
                    && ReadString(terminalInspection, "status", "") == "terminal_rescinded"
                    && ReadLong(terminalInspection, "materialRevision", -1) == 4
                    && !ReadBool(terminalInspection, "relationshipEffectResolved", true)
                    && State(receiptCase, receiptTag) == terminalState && StillCleanBytes(cleanTerminal),
                    "A retained tombstone settles a daily attempt without a new effect, measured-success claim, or sidecar write.");

                const string rejectedIntentCase = "failed_receipt_case", rejectedIntentTag = "failed_receipt_tag";
                var rejectedIntent = Versioned(0, -12, rejectedIntentCase, rejectedIntentTag);
                var beforeRejectedReceipt = State(rejectedIntentCase, rejectedIntentTag);
                using (var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                {
                    ReignDatabase.ExecuteSql(connection, "ALTER TABLE arrest_case_effect_state ADD CONSTRAINT d23_reject_receipt CHECK (case_id <> 'failed_receipt_case' OR effect_intent_id = '') NOT VALID;");
                    try
                    {
                        var denied = Apply(rejectedIntent);
                        add("arrest_effect_v2_failed_receipt_rolls_back_effect",
                            !ReadBool(denied, "ok", true) && ReadString(denied, "error", "").Contains("d23_reject_receipt", StringComparison.Ordinal)
                            && State(rejectedIntentCase, rejectedIntentTag) == beforeRejectedReceipt
                            && ReceiptState(rejectedIntentCase, rejectedIntentTag) == null,
                            "Rejecting the versioned case-row receipt rolls back both prior effect writes and leaves no receipt.");
                    }
                    finally { ReignDatabase.ExecuteSql(connection, "ALTER TABLE arrest_case_effect_state DROP CONSTRAINT d23_reject_receipt;"); }
                }

                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, committedReceiptPoint);
                DialogueService.AdvanceMemoryRestoreGeneration(campaign);
                var postRestoreClean = CleanBytes();
                var pendingAfterRestore = new Dictionary<string, object>(firstIntent) { ["sharedActivityGeneration"] = Generation() };
                bool restoredReceipt = ReadString(Apply(firstIntent), "status", "") == "ownership_mismatch"
                    && Json.Serialize(Apply(pendingAfterRestore)) == committed
                    && State(receiptCase, receiptTag) == firstState && StillCleanBytes(postRestoreClean);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, preReceiptPoint);
                DialogueService.AdvanceMemoryRestoreGeneration(campaign);
                var pendingBeforeCommit = State(receiptCase, receiptTag);
                var activeIntent = new Dictionary<string, object>(firstIntent) { ["sharedActivityGeneration"] = Generation() };
                add("arrest_effect_v2_checkpoint_replays_only_under_current_owner",
                    restoredReceipt && pendingBeforeCommit.Cases == 0 && pendingBeforeCommit.Reputations == 0 && pendingBeforeCommit.Affinity == -13
                    && Json.Serialize(Apply(activeIntent)) == committed && State(receiptCase, receiptTag).Affinity == -20,
                    "A restored committed schema replays the exact receipt only for its current owner. A restored pre-commit schema accepts the still-matching pending intent once.");

                using (var olderSchema = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                {
                    ReignDatabase.ExecuteSql(olderSchema, @"ALTER TABLE arrest_case_effect_state
DROP COLUMN effect_receipt_json,DROP COLUMN effect_intent_material,DROP COLUMN effect_intent_id,DROP COLUMN material_revision;");
                    ReignDatabase.ExecuteSql(olderSchema, "UPDATE schema_meta SET value='5' WHERE key=$key;",
                        TestDict("key", CampaignMigrationVersionKey));
                }
                var beforeMigration = State(receiptCase, receiptTag);
                var cleanOld = CleanBytes();
                var unavailable = Apply(activeIntent);
                bool heldWithoutDdl;
                using (var olderSchema = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign))
                    heldWithoutDdl = !WorldSimulationStore.ArrestEffectReceiptSchemaReady(olderSchema);
                bool versionedDidNotUpgrade = ReadString(unavailable, "status", "") == "arrest_effect_schema_unavailable"
                    && beforeMigration == State(receiptCase, receiptTag) && StillCleanBytes(cleanOld) && heldWithoutDdl;
                var oldOwner = Request(receiptCase, receiptTag, -7);
                oldOwner["sharedActivityGeneration"] = Generation();
                var stillLegacy = Apply(oldOwner);
                using var migrated = ReignDatabase.OpenCampaignConnection(campaign);
                add("arrest_effect_v2_registered_old_schema_requires_normal_migration",
                    versionedDidNotUpgrade && ReadBool(stillLegacy, "ok", false) && !stillLegacy.ContainsKey("arrestEffectVersion")
                    && WorldSimulationStore.ArrestEffectReceiptSchemaReady(migrated)
                    && ReadLong(ReceiptState(), "material_revision", -1) == 0 && CommittedCase(receiptCase, receiptTag, -7),
                    "A registered v5 schema keeps its owner-only compatibility and stays unmodified by v2 admission. The normal campaign open migrates without losing existing material.");
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaign); }
                finally
                {
                    try { if (Occupied(missing)) ReignPostgreSqlStorage.DropCampaign(missing); }
                    finally
                    {
                        lock (SaveSyncActiveStateLock)
                        {
                            SaveSyncActiveStates.Remove(campaign);
                            SaveSyncActiveStates.Remove(missing);
                        }
                        foreach (string root in roots)
                            if (Directory.Exists(root)) Directory.Delete(root, true);
                    }
                }
            }
        }

        private static void RunSaveSyncFencedRecoveryTests(Action<string, bool, string> add, string token, string sibling)
        {
            string campaign = "save_recovery_" + token;
            string parentRoot = Program.CampaignsRootOverride.Value;
            string previousNativeRoot = CampaignNativeSaveRootOverrideForTests;
            string siblingMetadata = Path.Combine(StrictCampaignDirectory(sibling), "campaign.json");
            string fixtureRoot = Path.Combine(Path.GetTempPath(), "ReignSaveSyncRecovery_" + Guid.NewGuid().ToString("N"));
            Program.CampaignsRootOverride.Value = Path.Combine(fixtureRoot, "campaigns");
            try
            {
                CampaignNativeSaveRootOverrideForTests = Directory.CreateDirectory(Path.Combine(fixtureRoot, "native-saves")).FullName;
                string siblingDirectory = StrictCampaignDirectory(sibling);
                Directory.CreateDirectory(siblingDirectory);
                File.Copy(siblingMetadata, Path.Combine(siblingDirectory, "campaign.json"));
                CampaignFileService.WriteJsonObject(Path.Combine(siblingDirectory, "characters", "hero_a", "enrichment.json"),
                    TestDict("status", "pending"));
                VerifySaveSyncFencedRecovery(add, token, campaign, sibling);
            }
            finally
            {
                CampaignNativeSaveRootOverrideForTests = previousNativeRoot;
                Program.CampaignsRootOverride.Value = parentRoot;
                TryDeleteDirectory(fixtureRoot);
            }
        }

        private static void VerifySaveSyncFencedRecovery(Action<string, bool, string> add, string token, string campaign, string sibling)
        {
            string directory = StrictCampaignDirectory(campaign);
            string sync = SaveSyncCampaignRoot(campaign);
            string journals = SaveSyncJournalDirectory(campaign);
            SaveSyncOperation op = null;
            try
            {
                bool identityRejected = false;
                try { RequireSaveSyncJournalCampaign(campaign + "\n"); }
                catch (InvalidDataException) { identityRejected = true; }
                add("journal_campaign_uses_exact_sql_identity", identityRejected,
                    "A recovery journal rejects a campaign identifier that PostgreSQL would normalize to another spelling.");
                PrepareSaveSyncTestCampaign(campaign, "target");
                string enrichment = Path.Combine("characters", "hero_a", "enrichment.json");
                CampaignFileService.WriteJsonObject(Path.Combine(directory, enrichment), TestDict("status", "pending"));
                var payload = SaveSyncTestPointPayload(campaign, "point_" + token, 1d, "Recovery fixture " + token);
                SaveSyncRegisterApi(payload);
                AppendSaveSyncTestFutureState(campaign);
                using var heldConnection = ReignPostgreSqlStorage.OpenCampaignConnection(campaign);
                string sourceHash = InventoryHash(SaveSyncTreeInventory(directory));
                string sqlHash = SaveSyncDatabaseHash(ReignPostgreSqlStorage.CampaignSchemaName(campaign));
                string missingSchema = ReignPostgreSqlStorage.SnapshotSchemaName(campaign, "absent_" + token);
                bool missingDenied = false;
                try { SaveSyncDatabaseHash(missingSchema); }
                catch (InvalidDataException) { missingDenied = true; }
                using (var metadata = ReignPostgreSqlStorage.OpenMetadataConnection())
                {
                    var count = ReignDatabase.QuerySql(metadata, "SELECT count(*) AS total FROM pg_namespace WHERE nspname=@schema;",
                        TestDict("schema", missingSchema)).Single();
                    add("sql_receipt_never_creates_missing_schema", missingDenied && ReadLong(count, "total", -1) == 0,
                        "A missing SQL schema cannot acquire a recovery hash or be created by verification.");
                }
                SaveSyncRecoveryObserver = boundary =>
                {
                    if (boundary is "target_installed" or "rollback_database" or "journal.recovery_required")
                        throw new IOException("fixture " + boundary);
                };
                var failed = SaveSyncLoadApi(payload, false);
                SaveSyncRecoveryObserver = null;
                string id = ReadString(failed, "backupId", "");
                op = ReadSaveSyncOperation(campaign, Path.Combine(journals, id + ".json"));
                string backup = Path.Combine(op.BackupPath, "campaign");
                add("failed_compensation_retains_sql_and_both_originals",
                    ReadString(failed, "result", "") == "recovery_required" && op.Phase == "restoring_original_database"
                    && ReignPostgreSqlStorage.SnapshotExists(campaign, op.RecoverySnapshotId)
                    && op.SourceDatabaseSha256 == sqlHash
                    && SaveSyncDatabaseHash(ReignPostgreSqlStorage.SnapshotSchemaName(campaign, op.RecoverySnapshotId)) == sqlHash
                    && InventoryHash(SaveSyncTreeInventory(backup)) == sourceHash
                    && InventoryHash(SaveSyncTreeInventory(op.DisplacedPath)) == sourceHash,
                    "Both original file trees and the exact recovery schema survive failed SQL compensation and journal update.");

                ReleaseSaveSyncFence(journals, id);
                VerifyOfflineSaveSyncRecoveryFence(add, op, sibling);
                File.Move(Path.Combine(sync, "ledger.json"), Path.Combine(sync, "fixture-ledger"));
                bool blockedAtEveryPhase = true;
                foreach (string phase in new[] { "preparing", "restoring_target_database", "moving_active_files",
                    "installing_target_files", "publishing_receipt", "restoring_original_database", "restoring_original_files", "recovery_required" })
                {
                    PublishSaveSyncPhase(op, phase);
                    ReleaseSaveSyncFence(journals, id);
                    CleanupAllSaveSyncRecoveryAtStartup();
                    blockedAtEveryPhase &= FencedSaveSyncOperation(campaign) == id
                        && ReadString(SaveSyncStatusSummary(campaign), "result", "") == "recovery_required"
                        && ReadString(SaveSyncLoadApi(payload, false), "result", "") == "recovery_required";
                }
                bool sqlDenied = false, heldSqlDenied = false, fileDenied = false;
                try { using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaign); }
                catch (InvalidOperationException e) when (e.Message.Contains(id)) { sqlDenied = true; }
                try { ReignDatabase.QuerySql(heldConnection, "SELECT 1"); }
                catch (InvalidOperationException e) when (e.Message.Contains(id)) { heldSqlDenied = true; }
                try { CampaignFileService.WriteJsonObject(Path.Combine(directory, "blocked.json"), TestDict()); }
                catch (InvalidOperationException e) when (e.Message.Contains(id)) { fileDenied = true; }
                add("startup_reconstructs_campaign_fence_without_ledger", blockedAtEveryPhase && sqlDenied && heldSqlDenied && fileDenied
                    && !File.Exists(Path.Combine(directory, "blocked.json")) && !IsSaveSyncSemanticBlocked(sibling)
                    && Directory.Exists(CampaignFileService.CampaignDirectory(sibling)),
                    "Reloading each nonterminal journal phase fences SQL, campaign files, indexing and retry for this campaign alone.");
                VerifyCaseDistinctSaveSyncFences(add, op);
                CharactersService.InitializeCharacterProfileLibrary();
                add("startup_enrichment_preserves_displaced_and_fenced_files",
                    InventoryHash(SaveSyncTreeInventory(op.DisplacedPath)) == sourceHash
                    && ReadString(CampaignFileService.ReadJsonObject(Path.Combine(directory, enrichment)), "status", "") == "pending"
                    && ReadString(CampaignFileService.ReadJsonObject(Path.Combine(StrictCampaignDirectory(sibling), enrichment)),
                        "status", "") == "deferred_until_interaction"
                    && !CampaignFileService.HasLocalCampaignMetadata(campaign) && CampaignFileService.HasLocalCampaignMetadata(sibling),
                    "Startup migrates eligible sibling enrichment while the fenced campaign and its displaced original remain intact.");
                var listed = ReadDictionaryList(CampaignListApi(), "campaigns");
                var statuses = ReadDictionaryList(SaveSyncStatusApi(null), "campaigns");
                add("fenced_campaign_keeps_sibling_listings_available",
                    new[] { listed, statuses }.All(rows => rows.Any(row => ReadString(row, "campaignId", "") == sibling)
                        && rows.Any(row => ReadString(row, "campaignId", "") == campaign
                            && ReadString(row, "result", "") == "recovery_required")),
                    "Both campaign and Save Sync listings expose the fenced operation alongside the healthy sibling.");
                var applying = new SaveSyncFence(Guid.NewGuid().ToString("N"), Applying: true);
                string siblingJournals = SaveSyncJournalDirectory(sibling);
                try
                {
                    SaveSyncRecoveryObserver = boundary =>
                    {
                        if (boundary == "read_ledger." + sibling) GetOrAddSaveSyncFence(siblingJournals, applying);
                    };
                    var concurrent = ReadDictionaryList(SaveSyncStatusApi(null), "campaigns");
                    add("status_ledger_fence_preserves_other_rows",
                        concurrent.Any(row => ReadString(row, "backupId", "") == applying.Id && ReadString(row, "result", "") == "apply_in_progress")
                        && concurrent.Any(row => ReadString(row, "backupId", "") == id && ReadString(row, "result", "") == "recovery_required"),
                        "A fence acquired during a status ledger read reports its exact operation alongside other campaigns.");
                }
                finally { SaveSyncRecoveryObserver = null; ReleaseSaveSyncFence(siblingJournals, applying.Id); }
                bool Denied(Action action)
                {
                    try { action(); return false; }
                    catch (InvalidOperationException ex) when (ex.Message.Contains(id)) { return true; }
                }
                bool deletionsDenied = Denied(() => ReignPostgreSqlStorage.DropSnapshot(campaign, op.RecoverySnapshotId))
                    && Denied(() => ReignPostgreSqlStorage.DropCampaign(campaign))
                    && Denied(() => DeleteCampaignBackupData(TestDict("campaignId", campaign, "confirmation", campaign)))
                    && Denied(() => DeleteCampaignOwnedDataCore(campaign, campaign, true, "recovery_test"));
                ReleaseSaveSyncFence(journals, id);
                string coldDeleteBlock = SaveSyncDeleteAllBlockReason();
                add("recovery_fence_blocks_retirement_and_cold_delete_all", deletionsDenied && coldDeleteBlock.Contains(id)
                    && DeleteAllPreDeleteBlockReason().Contains(id)
                    && ReignPostgreSqlStorage.SnapshotExists(campaign, op.RecoverySnapshotId)
                    && File.Exists(op.JournalPath) && Directory.Exists(op.DisplacedPath) && Directory.Exists(backup),
                    "Snapshot drop, campaign deletion, retirement and cold Delete All preflight preserve every original.");
                string metadataPath = Path.Combine(directory, "campaign.json");
                byte[] metadataBytes = File.ReadAllBytes(metadataPath);
                Dictionary<string, object> audit;
                try
                {
                    File.WriteAllText(metadataPath, Json.Serialize(TestDict("campaignId", "unregistered_" + token)));
                    audit = CampaignStorageAuditCore();
                }
                finally { File.WriteAllBytes(metadataPath, metadataBytes); }
                add("fenced_storage_ineligible_for_orphan_cleanup",
                    ReadDictionaryList(audit, "campaigns").Any(row => ReadString(row, "campaignId", "") == campaign
                        && ReadString(row, "storageCategory", "") == "recovery_required" && !ReadBool(row, "eligibleForPermanentDeletion", true))
                    && !ReadDictionaryList(audit, "orphanCampaignRoots").Any(row => ReadString(row, "path", "") == directory)
                    && !ReadDictionaryList(audit, "orphanSaveSyncRoots").Any(row => ReadString(row, "path", "") == sync),
                    "The storage audit protects operation-owned directories even with inconsistent metadata and a missing ledger.");

                byte[] lastPhase = File.ReadAllBytes(op.JournalPath);
                WriteRecoveryFile(op.JournalPath, Encoding.UTF8.GetBytes("{"));
                ReleaseSaveSyncFence(journals, id);
                string ledgerPath = Path.Combine(sync, "ledger.json");
                CampaignFileService.WriteJsonObject(ledgerPath, TestDict("version", SaveSyncFormatVersion,
                    "campaignId", campaign, "lastBackupId", id));
                var degraded = ReadDictionaryList(SaveSyncStatusApi(null), "campaigns");
                add("unreadable_status_journal_is_campaign_local",
                    degraded.Any(row => ReadString(row, "campaignId", "") == sibling)
                    && degraded.Any(row => ReadString(row, "backupId", "") == id
                        && ReadString(row, "result", "") == "recovery_required"),
                    "A journal damaged after startup reports recovery for its campaign while other Save Sync statuses remain readable.");
                ReleaseSaveSyncFence(journals, id);
                try
                {
                    SaveSyncRecoveryObserver = boundary =>
                    {
                        if (boundary == "status_journal") GetOrAddSaveSyncFence(journals, applying);
                    };
                    var status = SaveSyncStatusSummary(campaign);
                    add("stale_status_preserves_newer_applying_fence", ReadString(status, "backupId", "") == applying.Id
                        && ReadString(status, "result", "") == "apply_in_progress" && !status.ContainsKey("journalError")
                        && FindSaveSyncFence(campaign) == applying,
                        "Reading an older damaged receipt cannot replace an applying operation's fence.");
                }
                finally { SaveSyncRecoveryObserver = null; ReleaseSaveSyncFence(journals, applying.Id); }
                File.Delete(ledgerPath);
                ReleaseSaveSyncFence(journals, id);
                bool unreadable = false;
                try { CleanupSaveSyncRecovery(campaign); }
                catch (System.Text.Json.JsonException) { unreadable = true; }
                bool offlineUnreadable = false;
                try { RequireOfflineSaveSyncCampaignAvailable(campaign, CampaignFileService.CampaignsRoot()); }
                catch (InvalidDataException ex) when (ex.Message.Contains(op.JournalPath)) { offlineUnreadable = true; }
                add("offline_maintenance_rejects_unreadable_journal", offlineUnreadable && File.ReadAllText(op.JournalPath) == "{",
                    "Maintenance refuses the damaged journal and leaves its bytes intact.");
                add("unreadable_journal_preserves_fence_and_originals", unreadable && FencedSaveSyncOperation(campaign) == id
                    && Directory.Exists(op.DisplacedPath) && ReignPostgreSqlStorage.SnapshotExists(campaign, op.RecoverySnapshotId),
                    "An unreadable operation journal blocks its campaign and all recovery cleanup.");
                WriteRecoveryFile(op.JournalPath, lastPhase);

                foreach (string failure in new[] { "rollback_copy", "rollback_move" })
                {
                    bool injected = false;
                    SaveSyncRecoveryObserver = boundary =>
                    {
                        if (boundary == failure) { injected = true; throw new IOException("fixture file compensation"); }
                    };
                    try { RecoverSaveSyncOriginalFiles(op); }
                    catch (IOException) when (injected) { }
                    finally { SaveSyncRecoveryObserver = null; }
                    add(failure + "_preserves_original_evidence", injected && !Directory.Exists(directory)
                        && InventoryHash(SaveSyncTreeInventory(op.DisplacedPath)) == sourceHash
                        && InventoryHash(SaveSyncTreeInventory(backup)) == sourceHash
                        && ReignPostgreSqlStorage.SnapshotExists(campaign, op.RecoverySnapshotId),
                        "A failed file compensation retains the displaced tree, independent bytes and SQL preimage.");
                }
                RecoverSaveSyncOriginalFiles(op);
                add("rollback_file_copy_reconciles_inventory", InventoryHash(SaveSyncTreeInventory(directory)) == sourceHash,
                    "Completing the isolated file leg reproduces the source inventory exactly.");
            }
            finally
            {
                SaveSyncRecoveryObserver = null;
                ReleaseSaveSyncFence(journals, FencedSaveSyncOperation(campaign));
                ReignPostgreSqlStorage.DropCampaign(campaign);
                if (op != null) TryDeleteDirectory(op.WorkPath);
                TryDeleteDirectory(directory);
                TryDeleteDirectory(sync);
                TryDeleteDirectory(journals);
            }
        }

        private static void VerifyOfflineSaveSyncRecoveryFence(Action<string, bool, string> add, SaveSyncOperation op, string sibling)
        {
            string campaigns = CampaignFileService.CampaignsRoot();
            string previousRoot = Program.CampaignsRootOverride.Value;
            if (!ReignPostgreSqlStorage.Options.IsValidation || string.IsNullOrEmpty(previousRoot))
                throw new InvalidOperationException("Offline maintenance fence checks require isolated campaign storage.");
            string evidence = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(campaigns), "maintenance-evidence")).FullName;
            string manifest = Path.Combine(evidence, "request.json"), backup = Path.Combine(evidence, "backup-fixture");
            string report = Path.Combine(evidence, "report.json");
            File.WriteAllText(manifest, Json.Serialize(TestDict("schema", "reign-continuity-repair-v1",
                "campaignId", op.CampaignId, "timelineId", "absent_" + op.OperationId)));
            File.WriteAllText(backup, "synthetic maintenance backup hash input");
            byte[] before = File.ReadAllBytes(op.JournalPath);
            string expected = "Save Sync recovery_required for campaign " + op.CampaignId + ", operation " + op.OperationId + ".";
            string previousMode = Environment.GetEnvironmentVariable("REIGN_CONTINUITY_MAINTENANCE");
            bool cliBlocked = false, explicitRootBlocked = false;
            try
            {
                Environment.SetEnvironmentVariable("REIGN_CONTINUITY_MAINTENANCE", "1");
                try
                {
                    DialogueService.RunConversationContinuityRepairCli(new[] { "--apply", "--manifest", manifest, "--report", report,
                        "--confirmed-manifest-sha256", FileSha256(manifest), "--backup", backup, "--backup-sha256", FileSha256(backup) });
                }
                catch (InvalidOperationException ex) when (ex.Message == expected) { cliBlocked = true; }
                Program.CampaignsRootOverride.Value = Path.Combine(evidence, "campaigns");
                try { RequireOfflineSaveSyncCampaignAvailable(op.CampaignId, campaigns); }
                catch (InvalidOperationException ex) when (ex.Message == expected) { explicitRootBlocked = true; }
                RequireOfflineSaveSyncCampaignAvailable(sibling, campaigns);
            }
            finally
            {
                Program.CampaignsRootOverride.Value = previousRoot;
                Environment.SetEnvironmentVariable("REIGN_CONTINUITY_MAINTENANCE", previousMode);
            }
            add("cold_maintenance_apply_reads_exact_managed_journal", cliBlocked && explicitRootBlocked && !File.Exists(report)
                && FencedSaveSyncOperation(op.CampaignId) == null && before.SequenceEqual(File.ReadAllBytes(op.JournalPath)),
                "An offline apply with no cached fence reports the exact unresolved operation. A separate evidence root leaves sibling eligibility and journal bytes intact.");
        }

        private static void VerifyCaseDistinctSaveSyncFences(Action<string, bool, string> add, SaveSyncOperation op)
        {
            string sibling = op.CampaignId.ToUpperInvariant();
            string journals = SaveSyncJournalDirectory(sibling);
            string siblingId = Guid.NewGuid().ToString("N");
            try
            {
                using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(sibling);
                var row = ReignDatabase.QuerySql(connection, "SELECT current_schema() AS schema_name;").Single();
                string marker = Path.Combine(CampaignFileService.CampaignDirectory(sibling), "case-test.json");
                CampaignFileService.WriteJsonObject(marker, TestDict("operation", siblingId));
                var blockedMigration = CharactersService.MigrateCharacterProfilesToCurrentModels(new[] { "--campaign-id", op.CampaignId });
                var healthyMigration = CharactersService.MigrateCharacterProfilesToCurrentModels(new[] { "--campaign-id", sibling });
                string absentSpelling = char.ToUpperInvariant(op.CampaignId[0]) + op.CampaignId.Substring(1);
                var absentMigration = CharactersService.MigrateCharacterProfilesToCurrentModels(new[] { "--campaign-id", absentSpelling });
                add("profile_migration_selects_only_exact_unfenced_campaign",
                    ReadString(blockedMigration, "result", "") == "recovery_required" && ReadString(blockedMigration, "backupId", "") == op.OperationId
                    && ReadInt(healthyMigration, "campaignCount", -1) == 1 && ReadInt(absentMigration, "campaignCount", -1) == 0,
                    "An explicitly targeted profile migration returns the exact recovery fence, visits a named healthy sibling, and ignores a missing spelling.");
                add("relationship_queue_keeps_case_distinct_healthy_campaign",
                    RelationshipsService.VerifyCaseDistinctSaveSyncScheduling(CampaignFileService.CampaignsRoot(), op.CampaignId, sibling),
                    "Queueing the fenced campaign first keeps the healthy sibling eligible and preserves both worker generations.");
                bool distinctActiveState;
                lock (SaveSyncActiveStateLock)
                    distinctActiveState = !ReferenceEquals(ReadSaveSyncActiveStateLocked(op.CampaignId), ReadSaveSyncActiveStateLocked(sibling));
                GetOrAddSaveSyncFence(journals, new SaveSyncFence(siblingId, Applying: true));
                var statuses = ReadDictionaryList(SaveSyncStatusApi(null), "campaigns");
                add("case_distinct_campaigns_keep_separate_fences", ReadString(row, "schema_name", "") == ReignPostgreSqlStorage.CampaignSchemaName(sibling)
                    && distinctActiveState && File.Exists(marker) && !File.Exists(Path.Combine(op.ActivePath, "case-test.json"))
                    && statuses.Any(status => ReadString(status, "campaignId", "") == op.CampaignId && ReadString(status, "backupId", "") == op.OperationId)
                    && statuses.Any(status => ReadString(status, "campaignId", "") == sibling && ReadString(status, "backupId", "") == siblingId),
                    "A case-distinct sibling uses its own PostgreSQL schema and files. Both simultaneous fences retain their exact operation IDs in status.");
            }
            finally
            {
                ReleaseSaveSyncFence(journals, siblingId);
                ReignPostgreSqlStorage.DropCampaign(sibling);
                lock (SaveSyncActiveStateLock) SaveSyncActiveStates.Remove(sibling);
                TryDeleteDirectory(SaveSyncCampaignRoot(sibling));
            }
        }

        private static Dictionary<string, object> VerifyCaseDistinctSaveSyncRetirement(Action<string, bool, string> add, SaveSyncOperation op)
        {
            string campaign = op.CampaignId, sibling = campaign.ToUpperInvariant();
            string siblingDirectory = StrictCampaignDirectory(sibling);
            string marker = Path.Combine(siblingDirectory, "retirement-sibling.json");
            string queuedSibling = sibling + "|" + op.OperationId.ToUpperInvariant();
            string previousFailure = CampaignRetirementFailureInjectionForTests;
            try
            {
                using (var connection = ReignPostgreSqlStorage.OpenCampaignConnection(sibling))
                    CampaignFileService.WriteJsonObject(marker, TestDict("campaignId", ReignPostgreSqlStorage.CampaignIdForConnection(connection)));
                SchedulePendingCampaignRetirement(sibling, "sibling", "Sibling fixture");
                var unrelatedUnload = CampaignUnloadedApi(TestDict("campaignId", campaign));
                SchedulePendingCampaignRetirement(campaign, op.SavePointId, "Target fixture");
                add("case_distinct_pending_retirement_keeps_both_ids",
                    ReadString(unrelatedUnload, "result", "") == "no_pending_retirement" && IsCampaignRetirementPending(campaign)
                    && IsCampaignRetirementPending(sibling),
                    "A sibling's pending retirement cannot authorize an unload. Both case-distinct requests retain their ownership.");

                byte[] journal = File.ReadAllBytes(op.JournalPath);
                lock (SaveSyncOptimizationQueued) SaveSyncOptimizationQueued.Add(queuedSibling);
                CampaignRetirementFailureInjectionForTests = "after_database_drop";
                var interrupted = DeleteCampaignOwnedDataCore(campaign, "Verified Retirement", true, "test_terminal");
                CampaignRetirementFailureInjectionForTests = previousFailure;
                var residuals = ReadStringList(interrupted, "residualCleanup");
                bool staged = residuals.Select(root => Path.Combine(root, "save-sync-journals", op.OperationId + ".json"))
                    .Any(path => File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(journal));
                CleanupCampaignRetirementStagingAtStartup();
                var retry = DeleteCampaignOwnedDataCore(campaign, "Verified Retirement", true, "test_terminal_retry");
                bool queued;
                lock (SaveSyncOptimizationQueued) queued = SaveSyncOptimizationQueued.Contains(queuedSibling);
                add("case_distinct_retirement_discards_only_committed_staging",
                    ReadBool(interrupted, "campaignDeleted", false) && !ReadBool(interrupted, "cleanupComplete", true)
                    && staged && residuals.All(root => !Directory.Exists(root)) && !Directory.Exists(op.ActivePath)
                    && !File.Exists(op.JournalPath) && ReadString(retry, "result", "") == "already_deleted"
                    && !IsCampaignRetirementPending(campaign) && IsCampaignRetirementPending(sibling) && queued
                    && ReignPostgreSqlStorage.CampaignExists(sibling)
                    && ReadString(CampaignFileService.ReadJsonObject(marker), "campaignId", "") == sibling,
                    "After the target SQL drop, restart removes its staged terminal journal and files. The sibling keeps its data, pending retirement and queued work.");
                return retry;
            }
            finally
            {
                CampaignRetirementFailureInjectionForTests = previousFailure;
                lock (SaveSyncOptimizationQueued) SaveSyncOptimizationQueued.Remove(queuedSibling);
                CancelPendingCampaignRetirement(sibling, "fixture_cleanup");
                ReignPostgreSqlStorage.DropCampaign(sibling);
                TryDeleteDirectory(siblingDirectory);
                ClearSaveSyncCampaignRuntime(sibling);
                TryDeleteDirectory(SaveSyncCampaignRoot(sibling));
            }
        }

        private static void RunSaveSyncCommitJournalTest(Action<string, bool, string> add, string token)
        {
            string campaign = "sst_publish_" + token;
            string active = StrictCampaignDirectory(campaign);
            string journals = SaveSyncJournalDirectory(campaign);
            string sync = SaveSyncCampaignRoot(campaign);
            SaveSyncOperation op = null;
            try
            {
                PrepareSaveSyncTestCampaign(campaign, "target");
                var payload = SaveSyncTestPointPayload(campaign, "point_" + token, 1d, "Publication fixture " + token);
                SaveSyncRegisterApi(payload);
                AppendSaveSyncTestFutureState(campaign);
                payload["loadSessionId"] = "session_" + token;
                SaveSyncRecoveryObserver = boundary =>
                {
                    if (boundary is "journal.committed" or "audit.load.recovery_required")
                        throw new IOException("fixture terminal publication failure");
                };
                var uncertain = SaveSyncLoadApi(payload, false);
                SaveSyncRecoveryObserver = null;
                string id = ReadString(uncertain, "backupId", "");
                op = ReadSaveSyncOperation(campaign, Path.Combine(journals, id + ".json"));
                var ledger = CampaignFileService.ReadJsonObject(Path.Combine(sync, "ledger.json"));
                ReleaseSaveSyncFence(journals, id);
                CleanupSaveSyncRecovery(campaign);
                add("published_ledger_cannot_replay_indeterminate_commit",
                    op.Outcome == "recovery_required" && ReadString(uncertain, "result", "") == op.Outcome
                    && ReadString(ReadDictionary(ledger, "lastLoadReceipt"), "loadSessionId", "") == "session_" + token
                    && ReadString(SaveSyncLoadApi(payload, false), "result", "") == op.Outcome
                    && Directory.Exists(op.DisplacedPath) && ReignPostgreSqlStorage.SnapshotExists(campaign, op.RecoverySnapshotId),
                    "The durable fence takes precedence over an already-published success receipt after a terminal journal failure.");
            }
            finally
            {
                SaveSyncRecoveryObserver = null;
                ReleaseSaveSyncFence(journals, FencedSaveSyncOperation(campaign));
                ReignPostgreSqlStorage.DropCampaign(campaign);
                if (op != null) TryDeleteDirectory(op.WorkPath);
                TryDeleteDirectory(active);
                TryDeleteDirectory(sync);
                TryDeleteDirectory(journals);
            }
        }

#endif
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

#if !REIGN_EXCLUDE_VERIFICATION
    internal static partial class RelationshipsService
    {
        internal static bool VerifyCaseDistinctSaveSyncScheduling(string root, string fenced, string healthy)
        {
            // The real rescan can also visit other fixture directories. Restore the
            // scheduler under its own lock before returning to the persistence suite.
            lock (RelationshipReadyGate)
            {
                static Action Capture<T>(Dictionary<string, T> state)
                {
                    var entries = state.ToArray();
                    return () =>
                    {
                        state.Clear();
                        foreach (var entry in entries) state.Add(entry.Key, entry.Value);
                    };
                }
                var restoreReady = Capture(RelationshipReadyCampaigns);
                var restoreVersions = Capture(RelationshipReadyVersions);
                var restoreGenerations = Capture(RelationshipCampaignGenerations);
                var recovered = RelationshipRecoveredCampaigns.ToArray();
                DateTime reconciled = RelationshipReadyReconciledUtc;
                try
                {
                    RelationshipReadyReconciledUtc = DateTime.MinValue;
                    ResetRelationshipCampaignScheduling(fenced);
                    string generation = RelationshipCampaignGeneration(fenced);
                    ResetRelationshipCampaignScheduling(healthy);
                    var candidates = RelationshipCampaignCandidates(root);
                    return candidates.Contains(Path.Combine(root, healthy), StringComparer.Ordinal)
                        && !candidates.Contains(Path.Combine(root, fenced), StringComparer.Ordinal)
                        && RelationshipCampaignGeneration(fenced) == generation && RelationshipCampaignGeneration(healthy) != generation;
                }
                finally
                {
                    restoreReady();
                    restoreVersions();
                    restoreGenerations();
                    RelationshipRecoveredCampaigns.Clear();
                    RelationshipRecoveredCampaigns.UnionWith(recovered);
                    RelationshipReadyReconciledUtc = reconciled;
                }
            }
        }
    }
#endif
}
