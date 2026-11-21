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
                // Terminal report recovery authority: the old snapshot's own restore generation, read
                // directly from its immutable schema. The live generation is advanced first, so the
                // context can only record the snapshot's value if the real restore brings back
                // memory_precision_state before the context captures it.
                string oldSnapshotGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryPrecisionStateSql(ReignPostgreSqlStorage.SnapshotSchemaName(campaignId, SaveSyncPostgreSqlSnapshotPointId(oldStoredPoint))), "restore_generation");
                DialogueService.AdvanceMemoryRestoreGeneration(campaignId);
                string preRollbackGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryPrecisionStateSql2, "restore_generation");
                Dictionary<string, object> rolledBack = SaveSyncLoadApi(loadSessionPayload, false);
                Dictionary<string, object> previewAfterLoad = SaveSyncLoadApi(loadSessionPayload, true);
                add("preview_does_not_replay_a_completed_load",
                    ReadString(previewAfterLoad, "result", "") == "preview"
                    && ReadBool(previewAfterLoad, "dryRun", false)
                    && !previewAfterLoad.ContainsKey("idempotentReplay")
                    && !previewAfterLoad.ContainsKey("restoreGeneration"),
                    "A preview using a completed load session compares the current state without returning the applied load receipt.");
                string rollbackSourceGeneration = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryRestoredSourceContextSql, "source_generation");
                string rollbackLoadSession = SaveSyncTestSqlValue(campaignId,
                    PersistenceStore.SelectMemoryRestoredSourceContextSql2, "load_session_id");
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
                Dictionary<string, object> repeatNoOp = SaveSyncLoadApi(new Dictionary<string, object>(newPayload) { ["loadSessionId"] = "noop_b_" + token }, false);
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
                    && QueuedOptimizations().Contains(campaignId + "|" + pointId)
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
                    && !QueuedOptimizations().Contains(campaignId + "|" + aliasId),
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
