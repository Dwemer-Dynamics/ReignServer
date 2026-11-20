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
                Dictionary<string, object> export = CreateCampaignBackupArchive(campaignId);
                archivePath = ReadString(export, "path", "");
                add("export_archive_created", ReadBool(export, "ok", false) && File.Exists(archivePath), "A complete campaign archive is created.");
                if (!ReadBool(export, "ok", false)) throw new InvalidOperationException("Export failed: " + ReadString(export, "error", "unknown error"));
                VerifyBackupDirectoryRenames(purgeFixture, add);
                VerifyBackupNativeStaging(purgeFixture, add);
                VerifyBackupArchiveInventory(campaignId, add);
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
            add("import_scoped_archive_inventory", Accepted(definitions + objects, objects),
                "The inventory admits multiword entries selected by PostgreSQL for both the campaign and its snapshot.");
            string other = "5; 0 13 TABLE DATA separate_fixture sample owner\n";
            add("import_foreign_archive_entry", !Accepted(definitions + objects + other, objects),
                "A table-data entry outside the selected namespaces invalidates an otherwise complete inventory.");
            string unscoped = "6; 0 0 COMMENT - fixture owner\n";
            string emptyScope = "6; 0 0 COMMENT  fixture owner\n";
            add("import_unscoped_archive_annotation", !Accepted(definitions + objects + unscoped, objects + unscoped)
                && !Accepted(definitions + objects + emptyScope, objects + emptyScope),
                "An annotation selected by a parent still needs an owned namespace of its own.");
            add("import_duplicate_archive_definition", !Accepted(definitions + objects + $"7; 2615 14 SCHEMA - {campaign} owner\n", objects),
                "A repeated namespace definition invalidates the inventory.");
        }

        private static void VerifyVersionTwoBackupImport(string campaignId, string nativeSave, string fixture, Action<string, bool, string> add)
        {
            const string point = "diagnostic_backup_point";
            string root = Path.Combine(fixture, "version-two");
            string campaign = Path.Combine(root, "campaign");
            Directory.CreateDirectory(campaign);
            File.Copy(Path.Combine(StrictCampaignDirectory(campaignId), "campaign.json"), Path.Combine(campaign, "campaign.json"));
            File.WriteAllText(Path.Combine(campaign, "v2.txt"), "version two");
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
            var imported = ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "true" });
            add("import_version_two_snapshot", ReadBool(imported, "ok", false) && ReadInt(imported, "nativeSavesRestored", -1) == 0
                && ReignPostgreSqlStorage.SnapshotExists(campaignId, point) && FileSha256(nativeSave) == nativeHash
                && File.ReadAllText(Path.Combine(StrictCampaignDirectory(campaignId), "v2.txt")) == "version two",
                "A version 2 archive restores its campaign files and PostgreSQL snapshot while preserving the existing native save.");
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

        private static void VerifyBackupImportFailures(string campaignId, string archive, string nativeSave, string fixture,
            Action<string, bool, string> add)
        {
            string[] destinations = { StrictCampaignDirectory(campaignId), CampaignPortraitCacheDirectory(campaignId), SaveSyncCampaignRoot(campaignId) };
            foreach (string directory in destinations)
                File.WriteAllText(Path.Combine(directory, "original-only.txt"), Path.GetFileName(directory));
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
            Dictionary<string, object> Import(Action<string, string> step)
            {
                string upload = Path.Combine(fixture, Guid.NewGuid().ToString("N") + ".reignbackup");
                File.Copy(archive, upload);
                return ImportCampaignBackup(upload, new Dictionary<string, string> { ["replace"] = "true", ["replaceNativeSaves"] = "true" }, step);
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
            CheckEarlyFailure("import_list_snapshots_failure", "list_snapshots");
            CheckEarlyFailure("import_partial_export_failure", "export_recovery");
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
}
