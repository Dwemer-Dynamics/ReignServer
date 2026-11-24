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
        internal static List<Dictionary<string, object>> RunCampaignBackupSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = passed, ["suite"] = "campaign_backups", ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0 });
            string campaignId = "cbt_" + Guid.NewGuid().ToString("N");
            string campaignDir = CampaignFileService.CampaignDirectory(campaignId), portraitDir = CampaignPortraitCacheDirectory(campaignId), archivePath = "";
            string purgeFixture = Path.Combine(Program.TestsDir, "campaign_delete_all_" + Guid.NewGuid().ToString("N"));
            string priorNativeSaveRootOverride =
                CampaignNativeSaveRootOverrideForTests;
            try
            {
                CampaignMutationAllowDuringTests = true;
                Dictionary<string, object> rejected = DeleteAllCampaignBackupData(new Dictionary<string, object> { ["confirmation"] = "DELETE" });
                add("delete_all_requires_exact_confirmation", !ReadBool(rejected, "ok", true), "Delete all requires the exact lowercase word delete.");

                // Scheduled teardown phases with inert recording callbacks: no deletion, no receipt write.
                string lastReceiptError = "";
                string RunPhases(string provider, bool settled, bool externalFails, string blocked, bool stopFails = false)
                {
                    List<string> calls = new List<string>();
                    int code = CompleteScheduledDeleteAllCampaignData(
                        new Dictionary<string, object> { ["vectorProvider"] = provider, ["qdrantUrl"] = "http://qdrant.invalid" },
                        new Dictionary<string, object> { ["settled"] = settled },
                        () => { calls.Add("guard"); return blocked; },
                        _ => { calls.Add("external"); if (externalFails) throw new InvalidOperationException("external failed"); },
                        () => { calls.Add("stop"); if (stopFails) throw new InvalidOperationException("stop failed"); },
                        () => { calls.Add("local"); return 0; },
                        receipt =>
                        {
                            lastReceiptError = ReadString(receipt, "error", "");
                            calls.Add(ReadBool(receipt, "ok", true) ? "receipt_ok" : "receipt_failed");
                        });
                    return string.Join(",", calls) + ":" + code;
                }
                add("delete_all_timeout_deletes_nothing", RunPhases("qdrant", false, false, "") == "stop,receipt_failed:1",
                    "Unsettled shutdown quiescence writes a failure receipt and makes no external or local delete call.");
                add("delete_all_guard_recheck_deletes_nothing", RunPhases("qdrant", true, false, "blocked") == "guard,stop,receipt_failed:1",
                    "A failed pre-delete guard recheck makes no external or local delete call.");
                add("delete_all_external_failure_skips_local", RunPhases("qdrant", true, true, "") == "guard,external,stop,receipt_failed:1",
                    "External index failure stops semantic memory and never reaches local teardown.");
                add("delete_all_external_then_local", RunPhases("qdrant", true, false, "") == "guard,external,stop,local:0"
                    && RunPhases("local", true, false, "") == "guard,stop,local:0",
                    "After quiescence the captured external index is erased before semantic memory stops, and local teardown runs last.");
                add("delete_all_stop_fault_skips_local", RunPhases("qdrant", true, false, "", true) == "guard,external,stop,receipt_failed:1"
                    && lastReceiptError.Contains("Restart the Reign server") && !lastReceiptError.Contains("No campaign data was deleted")
                    && RunPhases("qdrant", true, true, "", true) == "guard,external,stop,receipt_failed:1"
                    && lastReceiptError.StartsWith("external failed", StringComparison.Ordinal),
                    "A semantic stop fault is surfaced and skips local teardown, keeping the earlier failure and never claiming nothing was deleted after the external call.");

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
                CampaignFileService.WriteJsonObject(Path.Combine(campaignDir, "campaign.json"), new Dictionary<string, object> { ["campaignId"] = campaignId, ["campaignLabel"] = "Backup Test", ["mainHeroName"] = "Tester" });
                byte[] admittedPortrait = { 1, 3, 5, 7 };
                string admittedMaster = WriteCampaignBackupPortraitFixture(campaignDir, campaignId, "hero_test", admittedPortrait);
                string legacyPortrait = Path.Combine(campaignDir, "characters", "legacy_hero", "portraits", "custom.png");
                Directory.CreateDirectory(Path.GetDirectoryName(legacyPortrait));
                File.WriteAllBytes(legacyPortrait, new byte[] { 2, 4, 6 });
                string outfitMarker = PortraitsService.WriteCampaignBackupOutfitFixture(campaignDir, campaignId, "outfit_hero");
                byte[] originalOutfitMarker = File.ReadAllBytes(outfitMarker);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId)) PersistenceStore.UpsertSchemaMeta(connection);
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
                var aliasedExport = CreateCampaignBackupArchive(campaignId.ToUpperInvariant());
                add("export_requires_exact_portrait_campaign_owner",
                    !ReadBool(aliasedExport, "ok", true) && ReadString(aliasedExport, "path", "") == ""
                    && ReadString(aliasedExport, "error", "").Contains("exact identity"),
                    "A differently spelled campaign request cannot export this campaign's portrait assets.");
                File.WriteAllBytes(admittedMaster, new byte[] { 7, 5, 3, 1 });
                var inconsistentExport = CreateCampaignBackupArchive(campaignId);
                add("export_rejects_unadmitted_campaign_portrait",
                    !ReadBool(inconsistentExport, "ok", true) && ReadString(inconsistentExport, "path", "") == ""
                    && ReadString(inconsistentExport, "error", "").Contains("do not match the stored receipt"),
                    "An archive cannot publish a master that disagrees with its portrait receipt.");
                File.WriteAllBytes(admittedMaster, admittedPortrait);
                File.Delete(outfitMarker);
                var unconfirmedExport = CreateCampaignBackupArchive(campaignId);
                add("export_rejects_unconfirmed_campaign_outfit",
                    !ReadBool(unconfirmedExport, "ok", true) && ReadString(unconfirmedExport, "path", "") == ""
                    && ReadString(unconfirmedExport, "error", "").Contains("no matching outfit confirmation"),
                    "Export cannot publish an outfit-format portrait whose final confirmation is missing.");
                File.WriteAllBytes(outfitMarker, originalOutfitMarker);
                Dictionary<string, object> export = CreateCampaignBackupArchive(campaignId);
                archivePath = ReadString(export, "path", "");
                add("export_archive_created", ReadBool(export, "ok", false) && File.Exists(archivePath), "A complete campaign archive is created.");
                if (!ReadBool(export, "ok", false)) throw new InvalidOperationException("Export failed: " + ReadString(export, "error", "unknown error"));
                VerifyBackupDirectoryRenames(purgeFixture, add);
                VerifyBackupNativeStaging(purgeFixture, add);
                VerifyBackupArchiveInventory(campaignId, add);
                VerifyReservedSharedPortraitImport(purgeFixture, add);
                VerifyBackupImportExactCampaignId(purgeFixture, add);
                VerifyBackupImportFailures(campaignId, archivePath, nativeSavePath, purgeFixture, add);
                string uploadCopy = Path.Combine(Program.DataDir, "backups", "importing", Guid.NewGuid().ToString("N") + ".reignbackup");
                Directory.CreateDirectory(Path.GetDirectoryName(uploadCopy));
                File.Copy(archivePath, uploadCopy, true);
                File.Delete(nativeSavePath);
                TryDeleteDirectory(campaignDir);
                TryDeleteDirectory(portraitDir);
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                if (Directory.Exists(campaignDir) || Directory.Exists(portraitDir)) throw new IOException("The disposable source campaign could not be cleared before the import round trip.");
                string orphan = SaveSyncCampaignRoot(campaignId);
                Directory.CreateDirectory(orphan);
                File.WriteAllText(Path.Combine(orphan, "orphan"), "original");
                string orphanUpload = Path.Combine(purgeFixture, "orphan.reignbackup");
                File.Copy(archivePath, orphanUpload);
                var orphanResult = ImportCampaignBackup(orphanUpload, new Dictionary<string, string>());
                add("import_orphan_conflict", ReadBool(orphanResult, "conflict", false)
                    && File.ReadAllText(Path.Combine(orphan, "orphan")) == "original"
                    && !Directory.Exists(campaignDir) && !Directory.Exists(portraitDir)
                    && !ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "A non-replace conflict preserves the orphan Save Sync tree and never creates the absent campaign.");
                TryDeleteDirectory(orphan);
                string newUpload = Path.Combine(purgeFixture, "new.reignbackup");
                File.Copy(archivePath, newUpload);
                string newOperation = "";
                var failedNew = ImportCampaignBackup(newUpload, new Dictionary<string, string>(), (step, operation) =>
                {
                    if (step != "finalize") return;
                    newOperation = operation;
                    throw new IOException("new install failed");
                });
                bool retainedNew = ReadBool(failedNew, "recoveryRequired", false) && ReadString(failedNew, "recoveryPath", "") == newOperation
                    && ReadString(failedNew, "databaseState", "") == "new_install_retained";
                add("import_new_database_retained", retainedNew
                    && !Directory.Exists(campaignDir) && !Directory.Exists(portraitDir) && !Directory.Exists(orphan)
                    && !File.Exists(nativeSavePath) && ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && File.Exists(Path.Combine(newOperation, "recovery-required.json")),
                    "A failed first import rolls back its files and identifies the retained SQL installation for recovery without guessing what it owns.");
                if (!retainedNew) throw new IOException("Missing isolated new-campaign recovery evidence.");
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                TryDeleteDirectory(newOperation);
                Dictionary<string, object> imported = ImportCampaignBackup(uploadCopy, new Dictionary<string, string> { ["replace"] = "false" });
                bool importRoundTrip = ReadBool(imported, "ok", false) && File.Exists(Path.Combine(campaignDir, "characters", "hero_test", "memory", "memories.jsonl")) && File.Exists(Path.Combine(portraitDir, "hero_test", "custom.png"))
                    && File.Exists(nativeSavePath)
                    && File.ReadAllBytes(nativeSavePath).SequenceEqual(nativeSaveBytes)
                    && ReadInt(imported, "nativeSavesRestored", 0) == 1;
                add("import_round_trip", importRoundTrip, importRoundTrip ? "Import restores campaign files, the external portrait cache, and the exact native Bannerlord save." : "Import round trip failed: " + Json.Serialize(imported));
                add("import_admitted_and_legacy_portrait_round_trip", importRoundTrip
                    && new[] { admittedMaster, legacyPortrait, outfitMarker }.All(File.Exists)
                    && File.ReadAllBytes(admittedMaster).SequenceEqual(admittedPortrait)
                    && File.ReadAllBytes(legacyPortrait).SequenceEqual(new byte[] { 2, 4, 6 })
                    && File.ReadAllBytes(outfitMarker).SequenceEqual(originalOutfitMarker),
                    "A receipt-backed portrait, original markerless custom portrait and owned outfit marker retain their bytes.");
                bool postgresPreserved = false;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId)) postgresPreserved = ReadString(PersistenceStore.SelectSchemaMeta(connection).FirstOrDefault(), "value", "") == "preserved";
                add("postgresql_snapshot_preserved", postgresPreserved, postgresPreserved ? "The consistent PostgreSQL campaign archive survives export and import." : "The imported PostgreSQL schema did not contain the exported backup_test marker. Import result: " + Json.Serialize(imported));
                add("manifest_hashes_present", ReadInt(export, "fileCount", 0) >= 4 && ReadInt(export, "nativeSaveCount", 0) == 1, "The archive manifest covers campaign, database, portrait, Save Sync, and native Bannerlord save files.");

                File.WriteAllBytes(nativeSavePath,
                    new byte[] { 8, 6, 4, 2 });
                string conflictUpload = Path.Combine(Program.DataDir, "backups",
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

                string replaceUpload = Path.Combine(Program.DataDir, "backups",
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
                VerifyVersionTwoBackupImport(campaignId, nativeSavePath, purgeFixture, add);
#if !REIGN_EXCLUDE_VERIFICATION
                VerifyBackupImportWithSaveSyncJournal(purgeFixture, add);
#endif
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
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
                TryDeleteDirectory(purgeFixture);
            }
            return results;
        }

        private static void VerifyBackupArchiveInventory(string campaignId, Action<string, bool, string> add)
        {
            string campaign = ReignPostgreSqlStorage.CampaignSchemaName(campaignId);
            string snapshot = ReignPostgreSqlStorage.SnapshotSchemaName(campaignId, "fixture");
            var expected = new HashSet<string>(StringComparer.Ordinal) { campaign, snapshot };
            string definitions = $"; Selected TOC Entries:\n1; 2615 10 SCHEMA - {campaign} owner\n2; 2615 11 SCHEMA - {snapshot} owner\n";
            string objects = $"3; 0 12 TABLE DATA {campaign} fixture owner\n4; 0 0 SEQUENCE OWNED BY {snapshot} fixture_seq owner\n";
            bool Accepted(string complete, string scoped)
            {
                try { ReignPostgreSqlStorage.VerifyCampaignArchiveInventory(complete, scoped, expected); return true; }
                catch (InvalidDataException) { return false; }
            }
            string schemaMetadata = $"8; 0 0 SECURITY LABEL - SCHEMA {campaign} owner\n9; 0 0 ACL - SCHEMA {snapshot} owner\n";
            add("import_scoped_archive_inventory", Accepted(definitions + schemaMetadata + objects, objects),
                "The inventory admits multiword entries and annotations on the expected campaign and snapshot schemas.");
            string other = "5; 0 13 TABLE DATA separate_fixture sample owner\n";
            add("import_foreign_archive_entry", !Accepted(definitions + objects + other, objects),
                "A table-data entry outside the selected namespaces invalidates an otherwise complete inventory.");
            string unscoped = "6; 0 0 COMMENT - fixture owner\n";
            string emptyScope = "6; 0 0 COMMENT  fixture owner\n";
            string foreignComment = "6; 0 0 COMMENT - SCHEMA separate_fixture owner\n";
            add("import_unscoped_archive_annotation", !Accepted(definitions + objects + unscoped, objects + unscoped)
                && !Accepted(definitions + objects + emptyScope, objects + emptyScope)
                && !Accepted(definitions + objects + foreignComment, objects),
                "An annotation must name an owned schema or be selected in an owned namespace.");
            add("import_duplicate_archive_definition", !Accepted(definitions + objects + $"7; 2615 14 SCHEMA - {campaign} owner\n", objects),
                "A repeated namespace definition invalidates the inventory.");
        }

#if !REIGN_EXCLUDE_VERIFICATION
        private static void VerifyBackupImportWithSaveSyncJournal(string fixture, Action<string, bool, string> add)
        {
            string label = Guid.NewGuid().ToString("N");
            string campaignId = "cbt_journal_" + label;
            string campaign = StrictCampaignDirectory(campaignId);
            string journalDirectory = SaveSyncJournalDirectory(campaignId);
            string marker = Path.Combine(campaign, "backup-import-marker.txt");
            string root = Path.Combine(fixture, "save-sync-journal-backup");
            bool? priorSaveSyncEnabled = SaveSyncEnabledOverrideForTests;
            bool priorDisableOptimization = SaveSyncDisableBackgroundOptimizationForTests;
            bool priorSkipSemantic = SaveSyncSkipSemanticForTests;
            var priorObserver = SaveSyncRecoveryObserver;
            SaveSyncOperation operation = null;
            try
            {
                SaveSyncEnabledOverrideForTests = true;
                SaveSyncDisableBackgroundOptimizationForTests = true;
                SaveSyncSkipSemanticForTests = true;
                PrepareSaveSyncTestCampaign(campaignId, "before_backup_import");
                File.WriteAllText(marker, "archived campaign");
                CopyDirectoryTree(campaign, Path.Combine(root, "campaign"), null);
                ReignPostgreSqlStorage.ExportCampaignArchive(campaignId, Path.Combine(root, "postgresql.dump"));
                CampaignFileService.WriteJsonObject(Path.Combine(root, "manifest.json"), new Dictionary<string, object>
                {
                    ["format"] = CampaignBackupFormat, ["version"] = 2, ["campaignId"] = campaignId,
                    ["databaseProvider"] = "postgresql", ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                    ["databaseEncoding"] = ReignPostgreSqlOptions.RequiredEncoding, ["databaseArchive"] = "postgresql.dump",
                    ["saveSyncSnapshots"] = ReignPostgreSqlStorage.ListSnapshots(campaignId), ["files"] = BackupFileManifest(root)
                });
                var point = SaveSyncTestPointPayload(campaignId, "import_" + label, 1d, "Journal import fixture");
                SaveSyncRegisterApi(point);
                AppendSaveSyncTestFutureState(campaignId);
                SaveSyncRecoveryObserver = phase =>
                {
                    if (phase == "cleanup_deleting_work") throw new IOException("fixture blocks terminal Save Sync cleanup");
                };
                var applied = SaveSyncLoadApi(point, false);
                string id = ReadString(applied, "backupId", "");
                if (!Guid.TryParseExact(id, "N", out _) || ReadString(applied, "outcome", "") != "committed")
                    throw new InvalidOperationException("Save Sync apply did not commit its disposable fixture.");
                operation = ReadSaveSyncOperation(campaignId, Path.Combine(journalDirectory, id + ".json"));
                string journalHash = FileSha256(operation.JournalPath);
                string inventory = Path.Combine(operation.BackupPath, "inventory.json");
                string retainedHash = FileSha256(inventory);
                File.WriteAllText(marker, "active campaign after Save Sync");
                string liveHash = FileSha256(marker);
                bool OriginalIntact() => File.Exists(marker) && FileSha256(marker) == liveHash
                    && File.Exists(inventory) && FileSha256(inventory) == retainedHash
                    && ReignPostgreSqlStorage.SnapshotExists(campaignId, operation.RecoverySnapshotId);
                Dictionary<string, object> Import(Action<string, string> observer = null)
                {
                    string upload = Path.Combine(fixture, "save-sync-journal.reignbackup");
                    ZipFile.CreateFromDirectory(root, upload);
                    return ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "true" }, observer);
                }

                int steps = 0;
                var deferred = Import((_, _) => steps++);
                add("import_defers_pending_save_sync_cleanup", !ReadBool(deferred, "ok", true) && steps == 0
                    && ReadString(deferred, "error", "") == "fixture blocks terminal Save Sync cleanup"
                    && operation.CleanupPending && Directory.Exists(operation.DisplacedPath)
                    && FileSha256(operation.JournalPath) == journalHash && OriginalIntact(),
                    "Backup import waits for failed terminal Save Sync cleanup while the old workspace, journal, files and SQL snapshot remain available.");

                SaveSyncRecoveryObserver = priorObserver;
                string settledJournalHash = null;
                bool staged = false;
                var failed = Import((phase, path) =>
                {
                    if (phase == "move_original" && path == journalDirectory)
                        settledJournalHash = FileSha256(operation.JournalPath);
                    if (phase == "move_original" && path == campaign)
                    {
                        staged = settledJournalHash != null && !Path.Exists(journalDirectory);
                        throw new IOException("fixture fails after staging the terminal journal");
                    }
                });
                add("import_rollback_restores_settled_save_sync_journal", !ReadBool(failed, "ok", true)
                    && !ReadBool(failed, "recoveryRequired", false)
                    && ReadString(failed, "error", "") == "fixture fails after staging the terminal journal"
                    && staged && File.Exists(operation.JournalPath) && FileSha256(operation.JournalPath) == settledJournalHash
                    && !Directory.Exists(operation.WorkPath) && OriginalIntact(),
                    "A failure before SQL restores the settled journal from the import staging directory alongside the original campaign.");

                var imported = Import();
                add("import_releases_settled_save_sync_journal", ReadBool(imported, "ok", false)
                    && File.ReadAllText(marker) == "archived campaign"
                    && !Path.Exists(journalDirectory) && !Path.Exists(operation.WorkPath)
                    && !Path.Exists(operation.BackupPath)
                    && !ReignPostgreSqlStorage.SnapshotExists(campaignId, operation.RecoverySnapshotId),
                    "Successful replacement installs the older archive and retires the settled journals and their superseded recovery data.");
            }
            finally
            {
                SaveSyncRecoveryObserver = priorObserver;
                SaveSyncSkipSemanticForTests = priorSkipSemantic;
                SaveSyncDisableBackgroundOptimizationForTests = priorDisableOptimization;
                SaveSyncEnabledOverrideForTests = priorSaveSyncEnabled;
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                if (operation != null) TryDeleteDirectory(operation.WorkPath);
                foreach (string path in new[] { campaign, journalDirectory, SaveSyncCampaignRoot(campaignId) })
                    TryDeleteDirectory(path);
            }
        }
#endif

        private static void VerifyReservedSharedPortraitImport(string fixture, Action<string, bool, string> add)
        {
            string shared = CampaignPortraitCacheDirectory("_shared");
            bool sharedExisted = Directory.Exists(shared);
            Directory.CreateDirectory(shared);
            string marker = Path.Combine(shared, Path.GetFileName(fixture) + ".png");
            byte[] original = { 3, 5, 8, 13 };
            using (var stream = new FileStream(marker, FileMode.CreateNew, FileAccess.Write))
                stream.Write(original);
            try
            {
                string bundle = Path.Combine(fixture, "reserved-portrait");
                string portraits = Directory.CreateDirectory(Path.Combine(bundle, "portrait_cache")).FullName;
                string campaign = Directory.CreateDirectory(Path.Combine(bundle, "campaign")).FullName;
                File.WriteAllText(Path.Combine(campaign, "campaign.json"), "{}");
                File.WriteAllBytes(Path.Combine(portraits, Path.GetFileName(marker)), new byte[] { 1, 2 });
                // A nonempty placeholder is sufficient for manifest validation. Preflight must reject it before SQL.
                File.WriteAllText(Path.Combine(bundle, "postgresql.dump"), "reserved-portrait preflight fixture");
                var files = BackupFileManifest(bundle);
                bool refused = true;
                foreach (string id in new[] { "_shared", "_ShArEd" })
                {
                    CampaignFileService.WriteJsonObject(Path.Combine(bundle, "manifest.json"), new Dictionary<string, object>
                    {
                        ["format"] = CampaignBackupFormat, ["version"] = 2, ["campaignId"] = id,
                        ["databaseProvider"] = "postgresql", ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                        ["databaseEncoding"] = ReignPostgreSqlOptions.RequiredEncoding, ["databaseArchive"] = "postgresql.dump",
                        ["files"] = files
                    });
                    string upload = Path.Combine(fixture, "reserved-portrait.reignbackup");
                    ZipFile.CreateFromDirectory(bundle, upload);
                    int steps = 0;
                    var result = ImportCampaignBackup(upload, new Dictionary<string, string>
                    {
                        ["replace"] = "true", ["replaceNativeSaves"] = "true"
                    }, (_, _) => steps++);
                    refused &= ReadBool(result, "conflict", false) && !ReadBool(result, "ok", true)
                        && ReadString(result, "error", "").Contains("shared portrait library", StringComparison.Ordinal)
                        && steps == 0;
                }
                add("import_preserves_reserved_shared_portraits", refused && File.Exists(marker) && File.ReadAllBytes(marker).SequenceEqual(original),
                    "Literal and mixed-case spellings of the reserved portrait identity are refused before replacement, preserving the shared-library bytes.");
            }
            finally
            {
                TryDeleteFile(marker);
                if (!sharedExisted && Directory.Exists(shared) && !Directory.EnumerateFileSystemEntries(shared).Any()) Directory.Delete(shared);
            }
        }

        private static void VerifyBackupImportExactCampaignId(string fixture, Action<string, bool, string> add)
        {
            string campaignId = "cbt_identity_" + Guid.NewGuid().ToString("N");
            string malformed = campaignId + "\n";
            string campaign = StrictCampaignDirectory(campaignId), portraits = CampaignPortraitCacheDirectory(campaignId);
            string campaignMarker = Path.Combine(campaign, "original.bin"), portraitMarker = Path.Combine(portraits, "original.bin");
            byte[] campaignBytes = { 4, 9 }, portraitBytes = { 5, 12 };
            string rawCampaign = StrictCampaignDirectory(malformed), rawSaveSync = SaveSyncCampaignRoot(malformed);
            object ReadOid()
            {
                using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT oid FROM pg_namespace WHERE nspname=current_schema();";
                return command.ExecuteScalar();
            }
            try
            {
                var originalOid = ReadOid();
                Directory.CreateDirectory(campaign);
                Directory.CreateDirectory(portraits);
                File.WriteAllBytes(campaignMarker, campaignBytes);
                File.WriteAllBytes(portraitMarker, portraitBytes);
                string bundle = Path.Combine(fixture, "campaign-identity");
                string incoming = Directory.CreateDirectory(Path.Combine(bundle, "campaign")).FullName;
                File.WriteAllText(Path.Combine(incoming, "campaign.json"), "{}");
                ReignPostgreSqlStorage.ExportCampaignArchive(campaignId, Path.Combine(bundle, "postgresql.dump"));
                CampaignFileService.WriteJsonObject(Path.Combine(bundle, "manifest.json"), new Dictionary<string, object>
                {
                    ["format"] = CampaignBackupFormat, ["version"] = 2, ["campaignId"] = malformed,
                    ["databaseProvider"] = "postgresql", ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                    ["databaseEncoding"] = ReignPostgreSqlOptions.RequiredEncoding, ["databaseArchive"] = "postgresql.dump",
                    ["saveSyncSnapshots"] = ReignPostgreSqlStorage.ListSnapshots(campaignId), ["files"] = BackupFileManifest(bundle)
                });
                bool RefusesBeforeMutation()
                {
                    string upload = Path.Combine(fixture, "campaign-identity.reignbackup");
                    ZipFile.CreateFromDirectory(bundle, upload);
                    int steps = 0;
                    var result = ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "true" }, (_, _) => steps++);
                    return !ReadBool(result, "ok", true) && !ReadBool(result, "recoveryRequired", false) && steps == 0
                        && ReadString(result, "error", "") == "The campaign ID in the backup must exactly match its storage identity."
                        && File.Exists(campaignMarker) && File.ReadAllBytes(campaignMarker).SequenceEqual(campaignBytes)
                        && File.Exists(portraitMarker) && File.ReadAllBytes(portraitMarker).SequenceEqual(portraitBytes)
                        && !Path.Exists(rawCampaign) && !Path.Exists(rawSaveSync);
                }
                add("import_rejects_normalized_registered_id", RefusesBeforeMutation() && ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && Equals(originalOid, ReadOid()),
                    "A trailing newline is rejected before replacement while the same-trimmed campaign keeps its files and original PostgreSQL schema.");
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                add("import_rejects_normalized_orphan_id", RefusesBeforeMutation() && !ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "The same rejected backup leaves the orphan campaign and portrait files intact and creates neither raw storage roots nor a database registration.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                foreach (string path in new[] { campaign, portraits, rawCampaign, rawSaveSync })
                    TryDeleteDirectory(path);
            }
        }

        private static void VerifyVersionTwoBackupImport(string campaignId, string nativeSave, string fixture, Action<string, bool, string> add)
        {
            const string point = "diagnostic_backup_point";
            string root = Path.Combine(fixture, "version-two");
            string campaign = Path.Combine(root, "campaign");
            Directory.CreateDirectory(campaign);
            File.Copy(Path.Combine(StrictCampaignDirectory(campaignId), "campaign.json"), Path.Combine(campaign, "campaign.json"));
            File.WriteAllText(Path.Combine(campaign, "v2.txt"), "version two");
            object ExecuteFixtureSql(string sql)
            {
                using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId);
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
            string schemaComment = "COMMENT ON SCHEMA " + ReignPostgreSqlStorage.CampaignSchemaName(campaignId) + " IS ";
            ExecuteFixtureSql(schemaComment + "'version two fixture'");
            ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaignId, point);
            ReignPostgreSqlStorage.ExportCampaignArchive(campaignId, Path.Combine(root, "postgresql.dump"));
            // Version 2 permits an archive without the version 3 native-save inventory.
            CampaignFileService.WriteJsonObject(Path.Combine(root, "manifest.json"), new Dictionary<string, object>
            {
                ["format"] = CampaignBackupFormat, ["version"] = 2, ["campaignId"] = campaignId,
                ["databaseProvider"] = "postgresql", ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                ["databaseEncoding"] = ReignPostgreSqlOptions.RequiredEncoding, ["databaseArchive"] = "postgresql.dump",
                ["saveSyncSnapshots"] = ReignPostgreSqlStorage.ListSnapshots(campaignId), ["files"] = BackupFileManifest(root)
            });
            string upload = Path.Combine(fixture, "v2.reignbackup");
            ZipFile.CreateFromDirectory(root, upload);
            string nativeHash = FileSha256(nativeSave);
            ReignPostgreSqlStorage.DropSnapshot(campaignId, point);
            ExecuteFixtureSql(schemaComment + "NULL");
            var imported = ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "true" });
            add("import_version_two_snapshot", ReadBool(imported, "ok", false) && ReadInt(imported, "nativeSavesRestored", -1) == 0
                && ReignPostgreSqlStorage.SnapshotExists(campaignId, point) && FileSha256(nativeSave) == nativeHash
                && File.ReadAllText(Path.Combine(StrictCampaignDirectory(campaignId), "v2.txt")) == "version two",
                "A version 2 archive restores its campaign files and PostgreSQL snapshot while preserving the existing native save.");
            add("import_version_two_schema_comment", ReadBool(imported, "ok", false)
                && Equals(ExecuteFixtureSql("SELECT obj_description(oid, 'pg_namespace') FROM pg_namespace WHERE nspname=current_schema()"), "version two fixture"),
                "A comment exported on the owned campaign schema is restored after being cleared from the disposable database.");
            VerifyCaseDistinctBackupImport(campaignId, root, Path.Combine(fixture, "case-distinct.reignbackup"), add);
        }

        private static void VerifyCaseDistinctBackupImport(string campaignId, string v2Root, string upload, Action<string, bool, string> add)
        {
            string sibling = campaignId.ToUpperInvariant();
            string campaign = StrictCampaignDirectory(campaignId), portraits = CampaignPortraitCacheDirectory(campaignId);
            string siblingCampaign = StrictCampaignDirectory(sibling), siblingPortraits = CampaignPortraitCacheDirectory(sibling);
            string journals = SaveSyncJournalDirectory(campaignId), siblingJournals = SaveSyncJournalDirectory(sibling);
            string campaignMarker = Path.Combine(siblingCampaign, "sibling.bin"), portraitMarker = Path.Combine(siblingPortraits, "sibling.bin");
            byte[] campaignBytes = { 3, 11, 7 }, portraitBytes = { 18, 5, 21 };
            var steps = new List<string>();
            Dictionary<string, object> Import(bool replace, string foldedParent = null)
            {
                steps.Clear();
                ZipFile.CreateFromDirectory(v2Root, upload);
                var query = new Dictionary<string, string> { ["replace"] = replace ? "true" : "false" };
                if (foldedParent == null) return ImportCampaignBackup(upload, query);
                // Simulate only name lookup on this one root. Enumeration, files, renames and PostgreSQL are real.
                bool Exists(string path) => Path.GetDirectoryName(path) != foldedParent ? Path.Exists(path)
                    : Directory.Exists(foldedParent) && Directory.EnumerateFileSystemEntries(foldedParent)
                        .Any(entry => Path.GetFileName(entry).Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase));
                return ImportCampaignBackup(upload, query, (step, _) => steps.Add(step), Exists);
            }
            bool Alias(Dictionary<string, object> result, string path) => ReadBool(result, "conflict", false)
                && !ReadBool(result, "ok", true) && !ReadBool(result, "recoveryRequired", false)
                && ReadString(result, "error", "").EndsWith(path, StringComparison.Ordinal);
            bool SiblingFilesIntact() => File.Exists(campaignMarker) && File.Exists(portraitMarker)
                && File.ReadAllBytes(campaignMarker).SequenceEqual(campaignBytes) && File.ReadAllBytes(portraitMarker).SequenceEqual(portraitBytes);

            // Remove only the previous v2 fixture's generated destinations before creating any sibling.
            ReignPostgreSqlStorage.DropCampaign(campaignId);
            foreach (string path in new[] { campaign, portraits, SaveSyncCampaignRoot(campaignId) })
                if (Directory.Exists(path)) Directory.Delete(path, true);
            try
            {
                Directory.CreateDirectory(siblingPortraits);
                File.WriteAllBytes(portraitMarker, portraitBytes);
                bool portraitRefused(bool replace) => Alias(Import(replace, Path.GetDirectoryName(portraits)), portraits) && steps.Count == 0;
                add("import_casefold_orphan_portraits", portraitRefused(false) && portraitRefused(true)
                    && File.ReadAllBytes(portraitMarker).SequenceEqual(portraitBytes) && !ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "Both replace settings preserve an unregistered case-aliased portrait tree before any import step.");
                File.Delete(portraitMarker);
                Directory.Delete(siblingPortraits);

                using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(sibling);
                using var command = connection.CreateCommand();
                command.CommandText = @"CREATE TABLE import_case_sibling AS SELECT oid AS original_oid
FROM pg_namespace WHERE nspname=current_schema();
UPDATE reign_meta.campaign_registry SET updated_utc=now()+interval '1 day' WHERE campaign_id=@sibling;";
                command.Parameters.AddWithValue("sibling", sibling);
                command.ExecuteNonQuery();
                bool newAliasRefused = Alias(Import(true, Path.GetDirectoryName(campaign)), campaign);
                add("import_casefold_empty_registered_destination", newAliasRefused && steps.Contains("install_directory")
                    && !steps.Contains("database_mutation") && !Directory.Exists(campaign) && !Directory.Exists(SaveSyncCampaignRoot(campaignId))
                    && !ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "Installing the first folder exposes a sibling alias. The owned rename is reversed before SQL and the new path disappears.");

                string anchor = Path.Combine(campaign, "original.bin");
                Directory.CreateDirectory(campaign);
                File.WriteAllBytes(anchor, campaignBytes);
                add("import_casefold_existing_target_spelling", Alias(Import(true, Path.GetDirectoryName(campaign)), campaign)
                    && steps.Count == 0 && File.ReadAllBytes(anchor).SequenceEqual(campaignBytes),
                    "An existing folder spelled like the target is preserved when the registered sibling resolves to that same entry.");
                File.Delete(anchor);
                Directory.Delete(campaign);

                string journalMarker = Path.Combine(siblingJournals, "sibling.bin");
                Directory.CreateDirectory(siblingJournals);
                File.WriteAllBytes(journalMarker, campaignBytes);
                bool JournalConflict(bool replace) => Alias(Import(replace, Path.GetDirectoryName(journals)), journals) && steps.Count == 0;
                add("import_casefold_save_sync_journal_conflict", JournalConflict(false) && JournalConflict(true)
                    && File.ReadAllBytes(journalMarker).SequenceEqual(campaignBytes) && !ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "A journal-only case alias is refused before settlement and preserves the registered sibling's journal bytes.");
                File.Delete(journalMarker);
                Directory.Delete(siblingJournals);

                Directory.CreateDirectory(siblingCampaign);
                Directory.CreateDirectory(siblingPortraits);
                File.WriteAllBytes(campaignMarker, campaignBytes);
                File.WriteAllBytes(portraitMarker, portraitBytes);
                add("import_casefold_campaign_conflict", Alias(Import(true, Path.GetDirectoryName(campaign)), campaign)
                    && steps.Count == 0 && SiblingFilesIntact() && !ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "A replacement that resolves to the sibling's stored spelling preserves both file trees before any import step.");
                command.CommandText = @"SELECT EXISTS(SELECT 1 FROM import_case_sibling s
JOIN pg_namespace n ON n.oid=s.original_oid
JOIN reign_meta.campaign_registry r ON r.schema_name=n.nspname
WHERE r.campaign_id=@sibling AND n.nspname=current_schema());";
                bool SiblingIntact() => SiblingFilesIntact() && Convert.ToBoolean(command.ExecuteScalar(), CultureInfo.InvariantCulture);
                string nativeAlias = Directory.Exists(campaign) ? campaign : Directory.Exists(portraits) ? portraits : null;
                var created = Import(false);
                bool createdAsExpected = nativeAlias != null ? Alias(created, nativeAlias) : ReadBool(created, "ok", false)
                    && ReignPostgreSqlStorage.ListCampaignMetadata().Select(row => ReadString(row, "campaignId", ""))
                        .Where(id => id == sibling || id == campaignId).SequenceEqual(new[] { sibling, campaignId });
                add("import_case_distinct_nonreplace", createdAsExpected && SiblingIntact(),
                    "The public non-replace import respects the filesystem's case behavior and preserves the sibling's files and PostgreSQL OID.");
                string marker = Path.Combine(campaign, "v2.txt");
                if (nativeAlias == null && createdAsExpected) File.WriteAllText(marker, "before replacement");
                var imported = Import(true);
                bool replacedAsExpected = nativeAlias != null ? Alias(imported, nativeAlias) && !ReignPostgreSqlStorage.CampaignExists(campaignId)
                    : ReadBool(imported, "ok", false) && File.Exists(marker) && File.ReadAllText(marker) == "version two";
                add("import_case_distinct_campaign", createdAsExpected && replacedAsExpected && SiblingIntact(),
                    "The public replacement respects distinct physical entries and preserves the newer sibling's campaign, portraits and original schema OID.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(sibling);
                TryDeleteDirectory(siblingCampaign);
                TryDeleteDirectory(siblingPortraits);
                TryDeleteDirectory(siblingJournals);
            }
        }

        private static void VerifyBackupNativeStaging(string fixture, Action<string, bool, string> add)
        {
            string root = Path.Combine(fixture, "native-staging");
            Directory.CreateDirectory(root);
            string source = Path.Combine(root, "incoming.sav"), target = Path.Combine(root, "existing.sav");
            string original = Path.Combine(root, "original", "existing.sav");
            File.WriteAllText(source, "complete incoming save");
            File.WriteAllText(target, "original save");
            string priorHash = FileSha256(target);
            var install = new ImportNativeSave(target, original, priorHash, FileSha256(source));
            using (var truncated = File.OpenWrite(source)) truncated.SetLength(2);
            bool rejected = false;
            try { install.Install(source); }
            catch (InvalidDataException) { rejected = true; }
            install.Restore();
            add("import_native_staging_hash_mismatch", rejected && FileSha256(target) == priorHash && FileSha256(original) == priorHash
                && !Directory.EnumerateFiles(root, ".ri-*.tmp").Any(),
                "A short staged copy fails hash verification before replacement and leaves the verified original intact.");

            string slot = new string('s', 251) + ".sav";
            string uploads = Directory.CreateDirectory(Path.Combine(root, "upload")).FullName;
            string saves = Directory.CreateDirectory(Path.Combine(root, "saves")).FullName;
            string recovery = Path.Combine(root, "recovery");
            string incoming = Path.Combine(uploads, slot), destination = Path.Combine(saves, slot);
            File.WriteAllText(incoming, "replacement save");
            File.WriteAllText(destination, "existing long save");
            string originalHash = FileSha256(destination);
            var rollback = new List<ImportNativeSave>();
            var result = InstallImportedNativeSaves(new List<string> { incoming }, saves, true, recovery, rollback);
            bool installed = ReadString(result.Single(), "result", "") == "replaced" && FileSha256(destination) == FileSha256(incoming);
            rollback.Single().Restore();
            add("import_long_native_save_replacement", installed && FileSha256(destination) == originalHash
                && FileSha256(Path.Combine(recovery, slot)) == originalHash,
                "A 255-byte native save name can be backed up, replaced and restored at its original name.");
        }

        private static void VerifyBackupDirectoryRenames(string fixture, Action<string, bool, string> add)
        {
            string root = Path.Combine(fixture, "directory-states");
            string destination = Path.Combine(root, "destination"), source = Path.Combine(root, "archive", "optional");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "old"), "old");
            var missingSource = new ImportDirectory(source, destination, root);
            missingSource.Install(true, null);
            bool retired = !Directory.Exists(destination);
            missingSource.Restore();
            add("import_optional_directory_rollback", retired && File.ReadAllText(Path.Combine(destination, "old")) == "old",
                "An absent optional archive tree can retire an old tree and restore it on failure.");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "new"), "new");
            string absent = Path.Combine(root, "absent");
            var newDirectory = new ImportDirectory(source, absent, root);
            newDirectory.Install(false, null);
            newDirectory.Restore();
            add("import_originally_absent_rollback", !Directory.Exists(absent)
                && File.ReadAllText(Path.Combine(root, "incoming", "optional", "new")) == "new",
                "Rolling back a newly installed tree restores the original absence.");
            string moveRoot = Path.Combine(fixture, "move-failure");
            Directory.CreateDirectory(Path.Combine(moveRoot, "original"));
            File.WriteAllText(Path.Combine(moveRoot, "original", "optional"), "occupied");
            var blockedMove = new ImportDirectory(source, destination, moveRoot);
            bool moveFailed = false;
            try { blockedMove.Install(true, null); }
            catch (IOException) { moveFailed = true; }
            blockedMove.Restore();
            add("import_failed_original_rename", moveFailed && File.ReadAllText(Path.Combine(destination, "old")) == "old",
                "An actual failed move-aside never authorizes displacement of the untouched destination.");
            string uncertainRoot = Path.Combine(fixture, "uncertain-rename");
            var uncertain = new ImportDirectory(source, destination, uncertainRoot);
            try
            {
                uncertain.Install(true, (step, path) =>
                {
                    if (step != "install_directory") return;
                    Directory.Move(Path.Combine(uncertainRoot, "incoming", "optional"), path);
                    throw new IOException("rename result unavailable");
                });
            }
            catch (IOException) { }
            bool refused = false;
            try { uncertain.Restore(); }
            catch (IOException) { refused = true; }
            add("import_unconfirmed_rename", refused && File.ReadAllText(Path.Combine(destination, "new")) == "new"
                && File.ReadAllText(Path.Combine(uncertainRoot, "original", "optional", "old")) == "old",
                "An uncertain rename retains both versions and refuses a second move.");
        }

        private static string WriteCampaignBackupPortraitFixture(string campaign, string campaignId, string hero, byte[] bytes)
        {
            string portraits = Path.Combine(campaign, "characters", hero, "portraits");
            Directory.CreateDirectory(portraits);
            string master = Path.Combine(portraits, "portrait.png");
            File.WriteAllBytes(master, bytes);
            CampaignFileService.WriteJsonObject(Path.Combine(portraits, "portrait.json"),
                TestDict("campaignId", campaignId, "heroStringId", hero, "productReceipt",
                    TestDict("schema", "reign-portrait-product-v1", "accepted", true, "master",
                        TestDict("sha256", FileSha256(master)))));
            return master;
        }

        private static void VerifyBackupImportFailures(string campaignId, string archive, string nativeSave, string fixture,
            Action<string, bool, string> add)
        {
            string[] destinations = { StrictCampaignDirectory(campaignId), CampaignPortraitCacheDirectory(campaignId), SaveSyncCampaignRoot(campaignId) };
            foreach (string directory in destinations)
                File.WriteAllText(Path.Combine(directory, "original-only.txt"), Path.GetFileName(directory));
            byte[] originalPortrait = { 8, 7, 6, 5 };
            string originalMaster = WriteCampaignBackupPortraitFixture(destinations[0], campaignId, "hero_test", originalPortrait);
            File.WriteAllText(nativeSave, "original native bytes");
            string nativeHash = FileSha256(nativeSave);
            string[] manifests = destinations.Select(dir => Json.Serialize(BackupFileManifest(dir))).ToArray();
            bool OriginalDirectories() => destinations.All(Directory.Exists) &&
                manifests.SequenceEqual(destinations.Select(dir => Json.Serialize(BackupFileManifest(dir))));
            long DatabaseOid()
            {
                using var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId);
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT oid::bigint FROM pg_namespace WHERE nspname=@schema";
                command.Parameters.AddWithValue("schema", ReignPostgreSqlStorage.CampaignSchemaName(campaignId));
                return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
            long originalOid = DatabaseOid();
            using (var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE import_original_marker AS SELECT 1 AS value";
                command.ExecuteNonQuery();
            }
            Dictionary<string, object> Import(Action<string, string> step, bool replaceNative = true)
            {
                string upload = Path.Combine(fixture, Guid.NewGuid().ToString("N") + ".reignbackup");
                File.Copy(archive, upload);
                return ImportCampaignBackup(upload, new Dictionary<string, string>
                    { ["replace"] = "true", ["replaceNativeSaves"] = replaceNative ? "true" : "false" }, step);
            }
            void CheckEarlyFailure(string id, string phase, string target = null)
            {
                var result = Import((step, path) =>
                {
                    if (step != phase || (target != null && path != target)) return;
                    if (step == "export_recovery") File.WriteAllText(path, "partial export");
                    throw new IOException(id);
                });
                add(id, ReadString(result, "error", "") == id && !ReadBool(result, "recoveryRequired", false)
                    && OriginalDirectories() && FileSha256(nativeSave) == nativeHash && DatabaseOid() == originalOid,
                    "The injected failure preserves every original file and the original PostgreSQL schema OID.");
            }
            void CheckPortraitConflict(string id, bool foreign, string expectedError)
            {
                string root = Path.Combine(fixture, id);
                ExtractCampaignBackupSecurely(archive, root);
                string portraits = Path.Combine(root, "campaign", "characters", "hero_test", "portraits");
                if (foreign)
                {
                    string manifestPath = Path.Combine(root, "manifest.json");
                    var manifest = CampaignFileService.ReadJsonObject(manifestPath);
                    File.Delete(manifestPath);
                    WriteCampaignBackupPortraitFixture(Path.Combine(root, "campaign"), campaignId + "_sibling", "hero_test",
                        File.ReadAllBytes(Path.Combine(portraits, "portrait.png")));
                    manifest["files"] = BackupFileManifest(root);
                    CampaignFileService.WriteJsonObject(manifestPath, manifest);
                }
                else File.WriteAllText(Path.Combine(portraits, "unlisted.txt"), "unlisted portrait entry");
                string upload = Path.Combine(fixture, id + ".reignbackup");
                ZipFile.CreateFromDirectory(root, upload);
                bool started = false;
                var refused = ImportCampaignBackup(upload, new Dictionary<string, string>
                    { ["replace"] = "true", ["replaceNativeSaves"] = "true" }, (_, _) => started = true);
                add(id, !started && !ReadBool(refused, "ok", true) && !ReadBool(refused, "recoveryRequired", false)
                    && ReadString(refused, "error", "").Contains(expectedError)
                    && OriginalDirectories() && FileSha256(nativeSave) == nativeHash && DatabaseOid() == originalOid,
                    "Import refuses the portrait archive before replacing any directory, PostgreSQL state, or native save.");
            }
            CheckPortraitConflict("import_refuses_foreign_portrait_owner", true, "belongs to another owner");
            CheckPortraitConflict("import_refuses_unlisted_portrait_entry", false, "omits a campaign portrait file");
            CheckEarlyFailure("import_list_snapshots_failure", "list_snapshots");
            CheckEarlyFailure("import_partial_export_failure", "export_recovery");
            string savedNative = Path.Combine(fixture, "prior-native.sav");
            string occupant = Path.Combine(nativeSave, "occupant.txt");
            File.Move(nativeSave, savedNative);
            Directory.CreateDirectory(nativeSave);
            File.WriteAllText(occupant, "preserve");
            try
            {
                bool started = false;
                var withoutReplace = Import((_, _) => started = true, false);
                var withReplace = Import((_, _) => started = true);
                // Imported slot names are trimmed. The preflight and the eventual installation must target the same path.
                string paddedRoot = Path.Combine(fixture, "padded-slot");
                ExtractCampaignBackupSecurely(archive, paddedRoot);
                string nativeRoot = Path.Combine(paddedRoot, "native_saves"), name = Path.GetFileName(nativeSave);
                File.Move(Path.Combine(nativeRoot, name), Path.Combine(nativeRoot, " " + name));
                string manifestPath = Path.Combine(paddedRoot, "manifest.json");
                var manifest = CampaignFileService.ReadJsonObject(manifestPath);
                File.Delete(manifestPath);
                manifest["files"] = BackupFileManifest(paddedRoot);
                CampaignFileService.WriteJsonObject(manifestPath, manifest);
                string paddedUpload = Path.Combine(fixture, "padded.reignbackup");
                ZipFile.CreateFromDirectory(paddedRoot, paddedUpload);
                var padded = ImportCampaignBackup(paddedUpload,
                    new Dictionary<string, string> { ["replace"] = "true", ["replaceNativeSaves"] = "true" }, (_, _) => started = true);
                add("import_native_directory_conflict", !started && new[] { withoutReplace, withReplace, padded }.All(result =>
                        !ReadBool(result, "ok", true) && ReadBool(result, "nativeSaveConflict", false) && !ReadBool(result, "recoveryRequired", false))
                    && File.ReadAllText(occupant) == "preserve" && OriginalDirectories() && DatabaseOid() == originalOid,
                    "An occupied native slot refuses either replacement policy and a padded archive name before directory or PostgreSQL mutation.");
            }
            finally
            {
                File.Delete(occupant);
                Directory.Delete(nativeSave);
                File.Move(savedNative, nativeSave);
            }
            using (var connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId))
            using (var command = connection.CreateCommand())
            {
                command.Parameters.AddWithValue("campaign", campaignId);
                command.Parameters.AddWithValue("schema", ReignPostgreSqlStorage.SnapshotSchemaName(campaignId, "missing_fixture"));
                command.CommandText = "INSERT INTO reign_meta.save_sync_snapshots(campaign_id,save_point_id,schema_name) VALUES(@campaign,'missing_fixture',@schema)";
                command.ExecuteNonQuery();
                try
                {
                    var incomplete = Import(null);
                    command.CommandText = "SELECT count(*) FROM reign_meta.save_sync_snapshots WHERE campaign_id=@campaign AND schema_name=@schema";
                    add("import_incomplete_original_registry", ReadString(incomplete, "error", "").Contains("cannot be fully backed up")
                        && Convert.ToInt64(command.ExecuteScalar()) == 1 && OriginalDirectories() && DatabaseOid() == originalOid,
                        "A registered snapshot with no physical schema blocks replacement and preserves the existing registry.");
                }
                finally
                {
                    command.CommandText = "DELETE FROM reign_meta.save_sync_snapshots WHERE campaign_id=@campaign AND schema_name=@schema";
                    command.ExecuteNonQuery();
                }
            }
            string[] names = { "campaign", "portraits", "save_sync" };
            for (int index = 0; index < destinations.Length; index++)
            {
                CheckEarlyFailure("import_move_aside_failure_" + names[index], "move_original", destinations[index]);
                CheckEarlyFailure("import_install_failure_" + names[index], "install_directory", destinations[index]);
            }
            add("import_rolls_back_admitted_portrait_after_staged_replacement",
                File.ReadAllBytes(originalMaster).SequenceEqual(originalPortrait)
                && OriginalDirectories() && DatabaseOid() == originalOid,
                "The injected directory rollback restores the original admitted portrait and its matching metadata.");
            bool truncated = false;
            var invalidRecovery = Import((step, path) =>
            {
                if (step != "verify_recovery") return;
                using var dump = File.OpenWrite(path);
                // The custom archive's TOC precedes the data blocks. Remove the tail of the last block.
                dump.SetLength(dump.Length - 32);
                truncated = true;
            });
            add("import_unreadable_recovery_export", truncated && !ReadBool(invalidRecovery, "ok", true)
                && ReadString(invalidRecovery, "error", "").StartsWith("PostgreSQL could not read all campaign archive data.", StringComparison.Ordinal)
                && !ReadBool(invalidRecovery, "recoveryRequired", false) && OriginalDirectories() && DatabaseOid() == originalOid,
                "A recovery export with a readable TOC and truncated table data is rejected before the existing campaign is touched.");

            string ownedOperation = "";
            var blocked = Import((step, path) =>
            {
                if (step == "export_recovery") ownedOperation = Path.GetDirectoryName(path);
                if (step == "install_directory" && path == destinations[2]) File.WriteAllText(path, "other owner");
            });
            string displaced = Path.Combine(ownedOperation, "original", "save_sync");
            bool retained = ReadBool(blocked, "recoveryRequired", false) && ReadString(blocked, "recoveryPath", "") == ownedOperation;
            add("import_blocked_directory_recovery", retained && File.ReadAllText(destinations[2]) == "other owner"
                && Json.Serialize(BackupFileManifest(displaced)) == manifests[2] && DatabaseOid() == originalOid
                && File.Exists(Path.Combine(ownedOperation, "original.dump")) && File.Exists(Path.Combine(ownedOperation, "recovery-required.json"))
                && manifests.Take(2).SequenceEqual(destinations.Take(2).Select(dir => Json.Serialize(BackupFileManifest(dir)))),
                "A blocked rename keeps the unowned occupant, the displaced original and the operation evidence while other directories recover.");
            if (!retained) throw new IOException("Missing isolated directory recovery evidence.");
            File.Delete(destinations[2]);
            Directory.Move(displaced, destinations[2]);
            TryDeleteDirectory(ownedOperation);

            var rolledBack = Import((step, operation) =>
            {
                if (step != "finalize") return;
                ownedOperation = operation;
                // Let the real lifecycle metadata write fail after all installations finish.
                string metadata = Path.Combine(destinations[0], "campaign.json");
                File.Move(metadata, Path.Combine(operation, "installed-campaign.json"));
                Directory.CreateDirectory(metadata);
            });
            retained = ReadBool(rolledBack, "recoveryRequired", false) && ReadString(rolledBack, "recoveryPath", "") == ownedOperation;
            add("import_installed_resources_rollback", retained && ReadString(rolledBack, "databaseState", "") == "archive_reapplied"
                && File.Exists(Path.Combine(ownedOperation, "original.dump")) && OriginalDirectories()
                && Directory.Exists(Path.Combine(ownedOperation, "incoming", "campaign", "campaign.json"))
                && FileSha256(nativeSave) == nativeHash && ReignPostgreSqlStorage.TableRowCount(campaignId, "import_original_marker") == 1,
                "A failed lifecycle metadata write restores prior campaign contents and retains the dump and displaced incoming tree.");
            if (!retained) throw new IOException("Missing isolated replay evidence.");
            TryDeleteDirectory(ownedOperation);

            var nativeBlocked = Import((step, operation) =>
            {
                if (step != "finalize") return;
                ownedOperation = operation;
                File.Move(nativeSave, Path.Combine(operation, "installed-native.sav"));
                Directory.CreateDirectory(nativeSave);
                throw new IOException("blocked native copy-back");
            });
            string nativeBackup = Path.Combine(ownedOperation, "original-native", Path.GetFileName(nativeSave));
            retained = ReadBool(nativeBlocked, "recoveryRequired", false) && ReadString(nativeBlocked, "recoveryPath", "") == ownedOperation;
            add("import_failed_native_copy_back", retained && Directory.Exists(nativeSave) && FileSha256(nativeBackup) == nativeHash
                && ((List<string>)nativeBlocked["recoveryErrors"]).Any(error => error.StartsWith(nativeSave, StringComparison.Ordinal))
                && ReadDictionaryList(nativeBlocked, "nativeSaves").Any(save => ReadString(save, "installation", "") == "Completed")
                && File.Exists(Path.Combine(ownedOperation, "original.dump")) && File.Exists(Path.Combine(ownedOperation, "inventory.json"))
                && OriginalDirectories() && ReignPostgreSqlStorage.TableRowCount(campaignId, "import_original_marker") == 1,
                "Failed native copy-back preserves its verified original, database recovery export and inventory while independent resources recover.");
            if (!retained) throw new IOException("Missing isolated native recovery evidence.");
            Directory.Delete(nativeSave);
            File.Copy(nativeBackup, nativeSave);
            TryDeleteDirectory(ownedOperation);

            var stoppedDatabase = Import((step, path) =>
            {
                if (step == "export_recovery") ownedOperation = Path.GetDirectoryName(path);
                if (step == "database_mutation") File.Move(path, path + ".unavailable");
            });
            bool indeterminate = ReadBool(stoppedDatabase, "recoveryRequired", false)
                && ReadString(stoppedDatabase, "recoveryPath", "") == ownedOperation
                && ReadString(stoppedDatabase, "databaseState", "") == "recovery_required";
            if (!indeterminate) throw new IOException("Missing isolated database recovery evidence.");
            var inventory = CampaignFileService.ReadJsonObject(Path.Combine(ownedOperation, "inventory.json"));
            string recoveryDump = Path.Combine(ownedOperation, "original.dump");
            bool verified = FileSha256(recoveryDump) == ReadString(inventory, "originalDatabaseSha256", "");
            if (!verified) throw new IOException("Unexpected recovery dump identity in the isolated database fixture.");
            ReignPostgreSqlStorage.ImportCampaignArchive(campaignId, recoveryDump, ReadDictionaryList(inventory, "originalSnapshots"), true);
            add("import_interrupted_database_recovery", OriginalDirectories() && FileSha256(nativeSave) == nativeHash
                && ReignPostgreSqlStorage.TableRowCount(campaignId, "import_original_marker") == 1,
                "A failed database import retains a hash-bound recovery export that restores the prior contents on this disposable database.");
            TryDeleteDirectory(ownedOperation);
        }
    }

    internal static partial class PortraitsService
    {
        internal static string WriteCampaignBackupOutfitFixture(string campaignDirectory, string campaign, string hero)
        {
            string directory = Path.Combine(campaignDirectory, "characters", hero, "portraits");
            Directory.CreateDirectory(directory);
            byte[] image = { 1, 4, 9 };
            string digest = Sha256Hex(image);
            File.WriteAllBytes(Path.Combine(directory, "custom.png"), image);
            var outfit = TestDict(OutfitConfirmationFormatKey, OutfitConfirmationFormat, "description", "Blue coat",
                "operationId", Guid.NewGuid().ToString("N"), "playerHeroId", "player", "timelineId", "main",
                "sourceSha256", digest, "portraitSha256", digest, "updatedUtc", DateTime.UtcNow.ToString("O"));
            CampaignFileService.WriteJsonObject(Path.Combine(directory, "outfit.json"), outfit);
            CampaignFileService.WriteJsonObject(Path.Combine(directory, OutfitPortraitReceiptFileName),
                TestDict("schema", "reign-portrait-product-v1", "accepted", true, "master", TestDict("sha256", digest)));
            string marker = Path.Combine(directory, OutfitConfirmationFileName);
            CampaignFileService.WriteJsonObject(marker, BuildOutfitConfirmation(campaign, hero, outfit));
            return marker;
        }
    }
}
