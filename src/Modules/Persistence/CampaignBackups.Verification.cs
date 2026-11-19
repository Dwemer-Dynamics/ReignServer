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
            string campaignDir = Program.CampaignDirectory(campaignId), portraitDir = CampaignPortraitCacheDirectory(campaignId), archivePath = "";
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
                Program.WriteJsonObject(Path.Combine(campaignDir, "campaign.json"), new Dictionary<string, object> { ["campaignId"] = campaignId, ["campaignLabel"] = "Backup Test", ["mainHeroName"] = "Tester" });
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
                string uploadCopy = Path.Combine(Program.DataDir, "backups", "importing", Guid.NewGuid().ToString("N") + ".reignbackup");
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
