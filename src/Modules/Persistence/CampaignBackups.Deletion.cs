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
        internal static Dictionary<string, object> DeleteCampaignBackupData(Dictionary<string, object> payload)
        {
            Program.CampaignDataGate.EnterWriteLock();
            try { return DeleteCampaignBackupDataCore(payload); }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        private static Dictionary<string, object> DeleteCampaignBackupDataCore(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            string confirmation = ReadString(payload, "confirmation", "");
            string campaignDir = CampaignFileService.CampaignDirectory(campaignId);
            bool databaseExists =
                ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                    ReadString(row, "campaignId", "")
                        .Equals(campaignId, StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(campaignId) || !databaseExists)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Campaign was not found."
                };
            Dictionary<string, object> meta = CampaignFileService.ReadJsonObject(Path.Combine(campaignDir, "campaign.json"));
            string label = ReadString(meta, "campaignLabel", ReadString(meta, "mainHeroName", campaignId));
            if (!confirmation.Equals(campaignId, StringComparison.OrdinalIgnoreCase) && !confirmation.Equals(label, StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Confirmation must exactly match the campaign ID or label." };
            return DeleteCampaignOwnedDataCore(
                campaignId, label, false, "confirmed_campaign_delete");
        }

        private static Dictionary<string, object> DeleteCampaignOwnedDataCore(
            string campaignId,
            string label,
            bool allowBannerlordRunning,
            string reason)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            if (campaignId.Equals("_shared", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["campaignDeleted"] = false,
                    ["error"] = "The shared portrait library cannot be deleted as a campaign."
                };
            if (!allowBannerlordRunning)
            {
                string running = CampaignMutationBlockReason();
                if (!string.IsNullOrWhiteSpace(running))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["campaignDeleted"] = false,
                        ["error"] = running
                    };
            }

            lock (CampaignBackupMutationLock)
            {
                string campaignDir = StrictCampaignDirectory(campaignId);
                string portraitDir = CampaignPortraitCacheDirectory(campaignId);
                string saveSyncDir = SaveSyncCampaignRoot(campaignId);
                string campaignRoot = CampaignFileService.CampaignsRoot();
                string portraitRoot = CampaignPortraitCacheRoot();
                string saveSyncRoot = SaveSyncRoot();
                ValidateCampaignOwnedDirectory(campaignDir, campaignRoot, false);
                ValidateCampaignOwnedDirectory(portraitDir, portraitRoot, true);
                ValidateCampaignOwnedDirectory(saveSyncDir, saveSyncRoot, false);

                bool databaseExists = ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                    ReadString(row, "campaignId", "").Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase));
                bool campaignExists = Directory.Exists(campaignDir);
                bool portraitExists = Directory.Exists(portraitDir);
                bool saveSyncExists = Directory.Exists(saveSyncDir);
                if (!databaseExists && !campaignExists && !portraitExists && !saveSyncExists)
                {
                    CancelPendingCampaignRetirement(campaignId, "already_deleted");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "already_deleted",
                        ["campaignId"] = campaignId,
                        ["campaignDeleted"] = true,
                        ["cleanupComplete"] = true,
                        ["countsBySubsystem"] = EmptyCampaignDeletionCounts()
                    };
                }

                Dictionary<string, object> counts = EmptyCampaignDeletionCounts();
                counts["campaignFiles"] = CampaignDeletionCount(campaignDir);
                counts["portraitCache"] = CampaignDeletionCount(portraitDir);
                counts["saveSync"] = CampaignDeletionCount(saveSyncDir);
                Dictionary<string, object> databaseCounts =
                    ReadDictionary(counts, "postgresql") ?? new Dictionary<string, object>();
                databaseCounts["schemas"] = databaseExists ? 1 : 0;
                databaseCounts["snapshots"] = databaseExists
                    ? ReignPostgreSqlStorage.ListSnapshots(campaignId).Count
                    : 0;

                string operationId = CampaignFileService.SafePathSegment(campaignId, "campaign") + "_" +
                    Guid.NewGuid().ToString("N");
                string dataStageRoot = Path.Combine(
                    Program.DataDir, "campaign-retirement-staging", operationId);
                string portraitParent = Directory.GetParent(
                    Path.GetFullPath(portraitRoot).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar))?.FullName ?? Program.DataDir;
                string portraitStageRoot = Path.Combine(
                    portraitParent, ".reign-campaign-retirement-staging", operationId);
                string movedCampaign = Path.Combine(dataStageRoot, "campaign");
                string movedSaveSync = Path.Combine(dataStageRoot, "save-sync");
                string movedPortrait = Path.Combine(portraitStageRoot, "portrait-cache");
                bool movedCampaignReady = false;
                bool movedPortraitReady = false;
                bool movedSaveSyncReady = false;
                bool databaseDropped = false;
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    int vectorsDeleted = databaseExists
                        ? DeleteCampaignSemanticVectors(campaignId)
                        : 0;
                    Dictionary<string, object> semanticCounts =
                        ReadDictionary(counts, "semanticVectors") ?? new Dictionary<string, object>();
                    semanticCounts["records"] = vectorsDeleted;

                    Directory.CreateDirectory(dataStageRoot);
                    Directory.CreateDirectory(portraitStageRoot);
                    WriteCampaignRetirementTransaction(
                        dataStageRoot, campaignId, "staging");
                    WriteCampaignRetirementTransaction(
                        portraitStageRoot, campaignId, "staging");
                    if (campaignExists)
                    {
                        MoveDirectoryWithRetries(campaignDir, movedCampaign);
                        VerifyStagedCampaignDirectory(campaignDir, movedCampaign);
                        movedCampaignReady = true;
                    }
                    if (saveSyncExists)
                    {
                        MoveDirectoryWithRetries(saveSyncDir, movedSaveSync);
                        VerifyStagedCampaignDirectory(saveSyncDir, movedSaveSync);
                        movedSaveSyncReady = true;
                    }
                    if (portraitExists)
                    {
                        MoveDirectoryWithRetries(portraitDir, movedPortrait);
                        VerifyStagedCampaignDirectory(portraitDir, movedPortrait);
                        movedPortraitReady = true;
                    }
                    if (CampaignRetirementFailureInjectionForTests == "after_stage")
                        throw new IOException("Injected campaign-retirement staging failure.");

                    if (databaseExists)
                    {
                        ReignPostgreSqlStorage.DropCampaign(campaignId);
                        databaseDropped = true;
                    }
                    WriteCampaignRetirementTransaction(
                        dataStageRoot, campaignId, "committed");
                    WriteCampaignRetirementTransaction(
                        portraitStageRoot, campaignId, "committed");
                    ClearSaveSyncCampaignRuntime(campaignId);
                    CancelPendingCampaignRetirement(campaignId, "campaign_deleted");

                    List<string> residuals = new List<string>();
                    if (CampaignRetirementFailureInjectionForTests == "after_database_drop")
                    {
                        if (Directory.Exists(dataStageRoot)) residuals.Add(dataStageRoot);
                        if (Directory.Exists(portraitStageRoot)) residuals.Add(portraitStageRoot);
                    }
                    else
                    {
                        DeleteCampaignStagingRoot(dataStageRoot, residuals);
                        DeleteCampaignStagingRoot(portraitStageRoot, residuals);
                    }
                    bool rootsGone = !Directory.Exists(campaignDir)
                        && !Directory.Exists(saveSyncDir)
                        && !Directory.Exists(portraitDir);
                    bool databaseGone = !ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                        ReadString(row, "campaignId", "").Equals(
                            campaignId, StringComparison.OrdinalIgnoreCase));
                    bool campaignDeleted = rootsGone && databaseGone;
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = campaignDeleted,
                        ["result"] = campaignDeleted
                            ? residuals.Count == 0 ? "deleted" : "deleted_cleanup_pending"
                            : "delete_verification_failed",
                        ["campaignId"] = campaignId,
                        ["label"] = label ?? campaignId,
                        ["reason"] = reason ?? "",
                        ["campaignDeleted"] = campaignDeleted,
                        ["campaignDeletionPending"] = false,
                        ["cleanupComplete"] = residuals.Count == 0,
                        ["residualCleanup"] = residuals,
                        ["countsBySubsystem"] = counts,
                        ["sharedPortraitLibraryPreserved"] = true,
                        ["message"] = residuals.Count == 0
                            ? "Campaign data, Save Sync storage, semantic vectors, and its portrait cache were permanently deleted."
                            : "The campaign is no longer active, but staged files remain for startup cleanup."
                    };
                    OperationsService.LogOperational("campaign.deleted", result);
                    return result;
                }
                catch (Exception ex)
                {
                    List<string> rollbackErrors = new List<string>();
                    if (!databaseDropped)
                    {
                        RestoreStagedCampaignDirectory(
                            movedCampaign, campaignDir, movedCampaignReady, rollbackErrors);
                        RestoreStagedCampaignDirectory(
                            movedSaveSync, saveSyncDir, movedSaveSyncReady, rollbackErrors);
                        RestoreStagedCampaignDirectory(
                            movedPortrait, portraitDir, movedPortraitReady, rollbackErrors);
                    }
                    Dictionary<string, object> failed = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["result"] = databaseDropped
                            ? "deleted_cleanup_pending"
                            : "delete_failed_recovered",
                        ["campaignId"] = campaignId,
                        ["campaignDeleted"] = databaseDropped,
                        ["campaignDeletionPending"] = !databaseDropped,
                        ["cleanupComplete"] = false,
                        ["countsBySubsystem"] = counts,
                        ["residualCleanup"] = new[] { dataStageRoot, portraitStageRoot }
                            .Where(Directory.Exists).ToList(),
                        ["rollbackErrors"] = rollbackErrors,
                        ["error"] = ex.Message
                    };
                    OperationsService.LogOperational("campaign.delete_failed", failed);
                    return failed;
                }
            }
        }

        internal static Dictionary<string, object> DeleteAllCampaignBackupData(Dictionary<string, object> payload)
        {
            return DeleteAllCampaignBackupData(payload, out _);
        }

        // Validates and schedules only; it makes no destructive call. scheduled is true only for the
        // one request that published the pending owner, which then owns the response-sent signal.
        internal static Dictionary<string, object> DeleteAllCampaignBackupData(Dictionary<string, object> payload, out bool scheduled)
        {
            scheduled = false;
            Program.CampaignDataGate.EnterWriteLock();
            try { return DeleteAllCampaignBackupDataCore(payload, ref scheduled); }
            finally
            {
                Program.CampaignDataGate.ExitWriteLock();
                // After both campaign locks are released: the first scheduler stop settles queued
                // never-started releases synchronously on this thread.
                if (scheduled) Program.RequestServerShutdown("campaign_delete_all");
            }
        }

        private static Dictionary<string, object> DeleteAllCampaignBackupDataCore(Dictionary<string, object> payload, ref bool scheduled)
        {
            payload = payload ?? new Dictionary<string, object>();
            if (!ReadString(payload, "confirmation", "").Equals("delete", StringComparison.Ordinal))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Type delete exactly to confirm permanent removal of every Reign campaign." };

            string running = CampaignMutationBlockReason();
            if (!string.IsNullOrWhiteSpace(running)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = running };
#if !REIGN_EXCLUDE_VERIFICATION
            if (VerificationService.VerificationTask != null && !VerificationService.VerificationTask.IsCompleted)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Wait for the active verification run to finish before deleting all campaign and test data." };
#endif

            lock (CampaignBackupMutationLock)
            {
                string campaignsRoot = CampaignFileService.CampaignsRoot();
                string backupsRoot = Path.Combine(Program.DataDir, "backups");
                string vectorsRoot = Path.Combine(Program.DataDir, "vectors");
                string portraitsRoot = CampaignPortraitCacheRoot();
                string saveSyncRoot = SaveSyncRoot();
                string legacySaveSyncRoot = Path.Combine(Program.DataDir, "save-sync");
                string testRunsRoot = Path.Combine(Program.TestsDir, "runs");
                string testSnapshotsRoot = Path.Combine(Program.TestsDir, "snapshots");
                string verificationRoot = Path.Combine(Program.TestsDir, "verification");
                Dictionary<string, object> settings = SettingsService.LoadSettings();
                bool localVectors = !ReadString(settings, "vectorProvider", "local").Equals("qdrant", StringComparison.OrdinalIgnoreCase);
                int campaignCount = SafeDirectoryCount(campaignsRoot);
                int backupCount = SafeFileCount(backupsRoot, "*.reignbackup");
                long bytesDeleted = SafeDirectorySize(campaignsRoot) + SafeDirectorySize(backupsRoot) + CampaignPortraitDataSizeExcludingShared(portraitsRoot)
                    + SafeDirectorySize(saveSyncRoot) + SafeDirectorySize(legacySaveSyncRoot)
                    + SafeDirectorySize(testRunsRoot) + SafeDirectorySize(testSnapshotsRoot) + SafeDirectorySize(verificationRoot);
                if (localVectors) bytesDeleted += SafeDirectorySize(vectorsRoot);
                string legacyActionQueue = Path.Combine(Program.DataDir, "action-queue.json");
                try { if (File.Exists(legacyActionQueue)) bytesDeleted += new FileInfo(legacyActionQueue).Length; } catch { }
                string refusal = "";
                // Validate the trusted configured external target without contacting it.
                if (!localVectors && string.IsNullOrWhiteSpace(ReadString(settings, "qdrantUrl", "")))
                    refusal = "External Qdrant is selected, but its URL is not configured. No campaign data was deleted.";
                else
                {
                    lock (Program.DeleteAllScheduleLock)
                    {
                        if (Program.DeleteAllSchedulingClosed)
                            refusal = "Reign is already shutting down, so campaign deletion was not scheduled. No campaign data was deleted. Restart the Reign server and try again.";
                        else if (Program.DeleteAllCampaignDataPending)
                            refusal = "Campaign deletion is already scheduled and Reign is shutting down.";
                        else
                        {
                            // Exactly this request's selection is carried to teardown; never overwritten.
                            Program.DeleteAllScheduledSettings = settings;
                            Program.DeleteAllCampaignResponseSent.Reset();
                            Program.DeleteAllCampaignDataPending = true;
                            scheduled = true;
                        }
                    }
                }
                if (!scheduled)
                {
                    OperationsService.LogOperational("campaign.all_delete_failed", new Dictionary<string, object> { ["error"] = refusal });
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = refusal };
                }
                Dictionary<string, object> result = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["serverStopping"] = true,
                    ["campaignsScheduled"] = campaignCount,
                    ["backupArchivesScheduled"] = backupCount,
                    ["bytesScheduled"] = bytesDeleted,
                    ["semanticProvider"] = localVectors ? "local" : "qdrant",
                    ["cleanupMode"] = "server_shutdown",
                    ["message"] = "Campaign deletion is scheduled. Reign is shutting down; once background work has stopped, this same server process will remove all campaign data, campaign-specific portraits, backups, queued actions, semantic indexes, and accumulated test results. If cleanup cannot start safely, nothing is deleted; logs/delete-all-last.json records the outcome and how to retry after restarting. The pregenerated shared portrait library, test definitions, global settings, API key, and native Bannerlord saves are preserved. Restart the Reign server after this page disconnects."
                };
                OperationsService.LogOperational("campaign.all_delete_scheduled", result);
                return result;
            }
        }

        // Re-evaluated after quiescence, before any delete phase. Local teardown rechecks Bannerlord itself.
        internal static string DeleteAllPreDeleteBlockReason()
        {
            string running = CampaignMutationBlockReason();
            if (!string.IsNullOrWhiteSpace(running)) return running;
#if !REIGN_EXCLUDE_VERIFICATION
            if (VerificationService.VerificationTask != null && !VerificationService.VerificationTask.IsCompleted)
                return "A verification run was still active.";
#endif
            return "";
        }

        internal static void WriteDeleteAllFailureReceipt(Dictionary<string, object> result)
        {
            try
            {
                Directory.CreateDirectory(Program.LogsDir);
                CampaignFileService.WriteJsonObject(Path.Combine(Program.LogsDir, "delete-all-last.json"), result);
                OperationsService.LogOperational("campaign.all_delete_failed", result);
            }
            catch { }
        }

        /// <summary>
        /// API teardown for a scheduled delete-all, after the listener claimed it. Runs with no
        /// campaign/backup lock held. Unless quiescence settled and the guard passes, it makes no
        /// external or local delete call. The captured external collection is erased first, while the
        /// vector worker can serve; semantic memory is then stopped on every path; local teardown runs
        /// only after external success and a clean semantic stop. Failures write the existing
        /// delete-all-last.json receipt; the first known failure is kept and a stop fault is added.
        /// </summary>
        internal static int CompleteScheduledDeleteAllCampaignData(Dictionary<string, object> settings, Dictionary<string, object> quiescence,
            Func<string> preDeleteBlockReason, Action<Dictionary<string, object>> deleteExternal, Action stopSemantic,
            Func<int> deleteLocal, Action<Dictionary<string, object>> recordFailure)
        {
            const string retry = " No campaign data was deleted. Restart the Reign server and run Delete all again.";
            // Once the external call has been made, the external index may be erased (fully or partly).
            const string externalRetry = " Local campaign data was not deleted, but the external semantic index may already be erased. Restart the Reign server and run Delete all again.";
            settings = settings ?? new Dictionary<string, object>();
            string failure = "";
            bool externalAttempted = false;
            try
            {
                if (!ReadBool(quiescence, "settled", false))
                    failure = "Background work, in-flight requests or semantic indexing did not stop within the shutdown limit." + retry;
                else
                {
                    string blocked = preDeleteBlockReason();
                    if (!string.IsNullOrWhiteSpace(blocked)) failure = blocked + retry;
                    else if (ReadString(settings, "vectorProvider", "local").Equals("qdrant", StringComparison.OrdinalIgnoreCase))
                    {
                        externalAttempted = true;
                        deleteExternal(settings);
                    }
                }
            }
            catch (Exception ex)
            {
                string message = string.IsNullOrWhiteSpace(ex.Message) ? "Campaign deletion failed." : ex.Message.Trim();
                failure = message + (externalAttempted ? externalRetry : retry);
            }
            string stopFailure = "";
            try { stopSemantic(); }
            catch (Exception ex) { stopFailure = string.IsNullOrWhiteSpace(ex.Message) ? "unknown error" : ex.Message.Trim(); }
            if (failure.Length == 0 && stopFailure.Length != 0)
                failure = "Semantic memory did not stop cleanly (" + stopFailure + ")." + (externalAttempted ? externalRetry : retry);
            if (failure.Length == 0) return deleteLocal();
            Dictionary<string, object> receipt = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["error"] = failure,
                ["externalDeleteAttempted"] = externalAttempted,
                ["quiescence"] = quiescence ?? new Dictionary<string, object>()
            };
            if (stopFailure.Length != 0) receipt["semanticStopError"] = stopFailure;
            recordFailure(receipt);
            return 1;
        }

        internal static int CompleteDeferredDeleteAllCampaignData(string[] args)
        {
            int parentPid = Program.ReadIntArgument(args ?? new string[0], "--wait-pid", 0);
            try
            {
                if (parentPid > 0)
                {
                    try
                    {
                        using (Process parent = Process.GetProcessById(parentPid))
                        {
                            if (!parent.WaitForExit(60000)) throw new IOException("The Reign server did not release its campaign files within 60 seconds.");
                        }
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                try
                {
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        ["error"] = ex.Message
                    };
                    Directory.CreateDirectory(Program.LogsDir);
                    CampaignFileService.WriteJsonObject(Path.Combine(Program.LogsDir, "delete-all-last.json"), result);
                    OperationsService.LogOperational("campaign.all_delete_failed", result);
                }
                catch { }
                return 1;
            }
            return CompleteDeleteAllCampaignData();
        }

        internal static int CompleteDeleteAllCampaignData()
        {
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = false,
                ["completedUtc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            try
            {
                string running = CampaignMutationBlockReason();
                if (!string.IsNullOrWhiteSpace(running)) throw new InvalidOperationException(running);
                ReignPostgreSqlStorage.ClearAllPools();
                ReignPostgreSqlStorage.DropAllCampaigns();
                List<string> directoryTargets = new List<string>
                {
                    CampaignFileService.CampaignsRoot(),
                    Path.Combine(Program.DataDir, "backups"),
                    SaveSyncRoot(),
                    Path.Combine(Program.DataDir, "save-sync"),
                    Path.Combine(Program.DataDir, "vectors"),
                    Path.Combine(Program.DataDir, ".i"),
                    Path.Combine(Program.TestsDir, "runs"),
                    Path.Combine(Program.TestsDir, "snapshots"),
                    Path.Combine(Program.TestsDir, "verification")
                };
                int rootsDeleted = 0;
                foreach (string target in directoryTargets.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(target)) continue;
                    DeleteDirectoryCompletely(target);
                    rootsDeleted++;
                }
                int portraitCachesDeleted = DeleteCampaignPortraitDataPreservingShared(CampaignPortraitCacheRoot());
                TryDeleteFile(Path.Combine(Program.DataDir, "action-queue.json"));
                TryDeleteFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification-latest.json"));
                lock (WorldSimulationService.WorldHistoryCacheLock) WorldSimulationService.WorldHistoryClaimCache.Clear();
                lock (CharactersService.CharacterEditorAuthorityCacheLock) CharactersService.CharacterEditorAuthorityByCampaign.Clear();
                result["ok"] = true;
                result["rootsDeleted"] = rootsDeleted;
                result["portraitCachesDeleted"] = portraitCachesDeleted;
                result["sharedPortraitLibraryPreserved"] = Directory.Exists(Path.Combine(CampaignPortraitCacheRoot(), "_shared"));
                result["testDefinitionsPreserved"] = Directory.Exists(Path.Combine(Program.TestsDir, "cases"));
                result["settingsPreserved"] = File.Exists(Program.SettingsPath);
                result["message"] = "All Reign campaign and accumulated test data was deleted after the server released its files. The pregenerated shared portrait library, test definitions, global settings, API key, and native Bannerlord saves were preserved.";
                Directory.CreateDirectory(Program.LogsDir);
                CampaignFileService.WriteJsonObject(Path.Combine(Program.LogsDir, "delete-all-last.json"), result);
                OperationsService.LogOperational("campaign.all_deleted", result);
                return 0;
            }
            catch (Exception ex)
            {
                result["error"] = ex.Message;
                try
                {
                    Directory.CreateDirectory(Program.LogsDir);
                    CampaignFileService.WriteJsonObject(Path.Combine(Program.LogsDir, "delete-all-last.json"), result);
                    OperationsService.LogOperational("campaign.all_delete_failed", result);
                }
                catch { }
                return 1;
            }
        }

        internal static void DeleteExternalSemanticCollection(Dictionary<string, object> settings)
        {
            if (string.IsNullOrWhiteSpace(ReadString(settings, "qdrantUrl", "")))
                throw new InvalidOperationException("External Qdrant is selected, but its URL is not configured. No campaign data was deleted.");

            DialogueService.StartManagedSemanticWorker(settings);
            Dictionary<string, object> request = DialogueService.SemanticProviderRequest(settings);
            request["collection"] = DialogueService.SemanticCollection;
            request["deleteCollection"] = true;
            Exception last = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Dictionary<string, object> response = TryParseJsonObject(DialogueService.PostJsonToUrl(DialogueService.SemanticWorkerUrl(settings, "/vectors/delete"), Json.Serialize(request), 15000)) ?? new Dictionary<string, object>();
                    if (!ReadBool(response, "ok", false)) throw new InvalidOperationException(ReadString(response, "error", "Qdrant collection deletion failed."));
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt < 19) Thread.Sleep(250);
                }
            }
            throw new InvalidOperationException("Reign could not remove its external Qdrant campaign index. No local campaign data was deleted. " + (last?.Message ?? ""));
        }

        private static void DeleteAllCampaignStorageCore(IEnumerable<string> directoryTargets, IEnumerable<string> fileTargets, string stagingRoot)
        {
            List<KeyValuePair<string, string>> movedDirectories = new List<KeyValuePair<string, string>>();
            List<KeyValuePair<string, string>> movedFiles = new List<KeyValuePair<string, string>>();
            Directory.CreateDirectory(stagingRoot);
            try
            {
                int rootSerial = 0;
                foreach (string root in (directoryTargets ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!Directory.Exists(root)) continue;
                    string stagedRoot = Path.Combine(stagingRoot, "root_" + (++rootSerial).ToString("D2", CultureInfo.InvariantCulture) + "_" + CampaignFileService.SafePathSegment(Path.GetFileName(root), "data"));
                    Directory.CreateDirectory(stagedRoot);
                    int entrySerial = 0;
                    foreach (string source in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        string destination = Path.Combine(stagedRoot, "directory_" + (++entrySerial).ToString("D4", CultureInfo.InvariantCulture));
                        MoveDirectoryWithRetries(source, destination);
                        movedDirectories.Add(new KeyValuePair<string, string>(source, destination));
                    }
                    foreach (string source in Directory.GetFiles(root, "*", SearchOption.TopDirectoryOnly))
                    {
                        string destination = Path.Combine(stagedRoot, "file_" + (++entrySerial).ToString("D4", CultureInfo.InvariantCulture));
                        MoveFileWithRetries(source, destination);
                        movedFiles.Add(new KeyValuePair<string, string>(source, destination));
                    }
                }
                int serial = 0;
                foreach (string source in (fileTargets ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(source)) continue;
                    string destination = Path.Combine(stagingRoot, "file_" + (++serial).ToString("D2", CultureInfo.InvariantCulture) + "_" + CampaignFileService.SafePathSegment(Path.GetFileName(source), "data"));
                    MoveFileWithRetries(source, destination);
                    movedFiles.Add(new KeyValuePair<string, string>(source, destination));
                }
            }
            catch
            {
                foreach (KeyValuePair<string, string> item in movedFiles.AsEnumerable().Reverse())
                    try { if (!File.Exists(item.Key) && File.Exists(item.Value)) MoveFileWithRetries(item.Value, item.Key); } catch { }
                foreach (KeyValuePair<string, string> item in movedDirectories.AsEnumerable().Reverse())
                    try { if (!Directory.Exists(item.Key) && Directory.Exists(item.Value)) MoveDirectoryWithRetries(item.Value, item.Key); } catch { }
                TryDeleteDirectory(stagingRoot);
                throw;
            }
            DeleteDirectoryCompletely(stagingRoot);
        }

        private static long CampaignPortraitDataSizeExcludingShared(string portraitRoot)
        {
            if (string.IsNullOrWhiteSpace(portraitRoot) || !Directory.Exists(portraitRoot)) return 0;
            long bytes = 0;
            foreach (string directory in Directory.GetDirectories(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals("_shared", StringComparison.OrdinalIgnoreCase)) continue;
                bytes += SafeDirectorySize(directory);
            }
            foreach (string file in Directory.GetFiles(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                try { bytes += new FileInfo(file).Length; } catch { }
            }
            return bytes;
        }

        private static int DeleteCampaignPortraitDataPreservingShared(string portraitRoot)
        {
            if (string.IsNullOrWhiteSpace(portraitRoot) || !Directory.Exists(portraitRoot)) return 0;
            int deleted = 0;
            foreach (string directory in Directory.GetDirectories(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileName(directory).Equals("_shared", StringComparison.OrdinalIgnoreCase)) continue;
                DeleteDirectoryCompletely(directory);
                deleted++;
            }
            foreach (string file in Directory.GetFiles(portraitRoot, "*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteFile(file);
            }
            return deleted;
        }
    }
}
