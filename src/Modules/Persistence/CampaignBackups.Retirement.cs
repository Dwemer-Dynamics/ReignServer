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
        private static string CampaignRetirementLedgerPath()
        {
            return Path.Combine(Program.DataDir, "campaign-retirements.json");
        }

        private static Dictionary<string, object> ReadCampaignRetirementLedger()
        {
            lock (CampaignRetirementLedgerLock)
            {
                Dictionary<string, object> ledger =
                    Program.ReadJsonObject(CampaignRetirementLedgerPath());
                ledger["version"] = CampaignRetirementVersion;
                if (!ledger.ContainsKey("pending"))
                    ledger["pending"] = new List<Dictionary<string, object>>();
                return ledger;
            }
        }

        private static void WriteCampaignRetirementLedger(
            Dictionary<string, object> ledger)
        {
            lock (CampaignRetirementLedgerLock)
            {
                ledger = ledger ?? new Dictionary<string, object>();
                ledger["version"] = CampaignRetirementVersion;
                ledger["updatedUtc"] = DateTime.UtcNow.ToString(
                    "o", CultureInfo.InvariantCulture);
                if (!ledger.ContainsKey("pending"))
                    ledger["pending"] = new List<Dictionary<string, object>>();
                Program.WriteJsonObject(CampaignRetirementLedgerPath(), ledger);
            }
        }

        private static Dictionary<string, object> SchedulePendingCampaignRetirement(
            string campaignId,
            string savePointId,
            string nativeSaveName)
        {
            ValidateSaveSyncId(campaignId, "campaignId");
            lock (CampaignRetirementLedgerLock)
            {
                Dictionary<string, object> ledger = ReadCampaignRetirementLedger();
                List<Dictionary<string, object>> pending =
                    ReadDictionaryList(ledger, "pending");
                Dictionary<string, object> entry = pending.FirstOrDefault(row =>
                    ReadString(row, "campaignId", "").Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase));
                if (entry == null)
                {
                    entry = new Dictionary<string, object>();
                    pending.Add(entry);
                }
                entry["campaignId"] = campaignId;
                entry["state"] = "pending_final_save_deletion";
                entry["savePointId"] = savePointId ?? "";
                entry["nativeSaveName"] = nativeSaveName ?? "";
                entry["requestedUtc"] = DateTime.UtcNow.ToString(
                    "o", CultureInfo.InvariantCulture);
                entry["reason"] = "confirmed_final_native_save_deleted";
                ledger["pending"] = pending;
                WriteCampaignRetirementLedger(ledger);
                Program.LogOperational("campaign.retirement_pending", entry);
                return entry;
            }
        }

        private static bool CancelPendingCampaignRetirement(
            string campaignId,
            string reason)
        {
            if (string.IsNullOrWhiteSpace(campaignId)) return false;
            lock (CampaignRetirementLedgerLock)
            {
                Dictionary<string, object> ledger = ReadCampaignRetirementLedger();
                List<Dictionary<string, object>> pending =
                    ReadDictionaryList(ledger, "pending");
                int removed = pending.RemoveAll(row =>
                    ReadString(row, "campaignId", "").Equals(
                        campaignId, StringComparison.OrdinalIgnoreCase));
                if (removed == 0) return false;
                ledger["pending"] = pending;
                WriteCampaignRetirementLedger(ledger);
                Program.LogOperational("campaign.retirement_cancelled", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["reason"] = reason ?? ""
                });
                return true;
            }
        }

        private static bool IsCampaignRetirementPending(string campaignId)
        {
            return ReadDictionaryList(ReadCampaignRetirementLedger(), "pending")
                .Any(row => ReadString(row, "campaignId", "").Equals(
                    campaignId ?? "", StringComparison.OrdinalIgnoreCase));
        }

        internal static Dictionary<string, object> CampaignUnloadedApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            Program.CampaignDataGate.EnterWriteLock();
            try
            {
                if (!IsCampaignRetirementPending(campaignId))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "no_pending_retirement",
                        ["campaignId"] = campaignId,
                        ["campaignDeleted"] = false,
                        ["campaignDeletionPending"] = false,
                        ["noOp"] = true
                    };
                int remaining = ReadDictionaryList(
                    ReadSaveSyncLedger(campaignId), "points").Count;
                if (remaining > 0)
                {
                    CancelPendingCampaignRetirement(
                        campaignId, "confirmed_save_exists_at_unload");
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "retirement_cancelled",
                        ["campaignId"] = campaignId,
                        ["remainingSavePoints"] = remaining,
                        ["campaignDeleted"] = false,
                        ["campaignDeletionPending"] = false
                    };
                }
                Dictionary<string, object> metadata = Program.ReadJsonObject(
                    Path.Combine(StrictCampaignDirectory(campaignId), "campaign.json"));
                return DeleteCampaignOwnedDataCore(
                    campaignId,
                    ReadString(metadata, "campaignLabel",
                        ReadString(metadata, "mainHeroName", campaignId)),
                    true,
                    "active_campaign_unloaded_after_final_save_deletion");
            }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        internal static void ProcessPendingCampaignRetirementsAtStartup()
        {
            CleanupCampaignRetirementStagingAtStartup();
            // Recover interrupted filesystem transactions before accepting campaign
            // requests, but leave pending retirement to the monitor. Semantic
            // deletion can outlast systemd's startup deadline; running it here
            // prevents even /health from reaching the HTTP accept loop and
            // makes systemd kill and restart the same unfinished deletion.
        }

        internal static void StartCampaignRetirementMonitor()
        {
            Task.Run(async () =>
            {
                while (!Program.ShutdownRequested)
                {
                    await Task.Delay(2000).ConfigureAwait(false);
                    if (Program.ShutdownRequested || !string.IsNullOrWhiteSpace(
                        CampaignMutationBlockReason())) continue;
                    if (ReadDictionaryList(
                        ReadCampaignRetirementLedger(), "pending").Count == 0) continue;
                    ProcessPendingCampaignRetirements(false);
                }
            });
        }

        private static void ProcessPendingCampaignRetirements(
            bool allowBannerlordRunning)
        {
            if (!allowBannerlordRunning
                && !string.IsNullOrWhiteSpace(CampaignMutationBlockReason())) return;
            Program.CampaignDataGate.EnterWriteLock();
            try
            {
                List<Dictionary<string, object>> pending = ReadDictionaryList(
                    ReadCampaignRetirementLedger(), "pending").ToList();
                foreach (Dictionary<string, object> entry in pending)
                {
                    string campaignId = ReadString(entry, "campaignId", "");
                    try
                    {
                        ValidateSaveSyncId(campaignId, "campaignId");
                        int remaining = ReadDictionaryList(
                            ReadSaveSyncLedger(campaignId), "points").Count;
                        if (remaining > 0)
                        {
                            CancelPendingCampaignRetirement(
                                campaignId, "confirmed_save_created_before_retirement");
                            continue;
                        }
                        Dictionary<string, object> metadata = Program.ReadJsonObject(
                            Path.Combine(StrictCampaignDirectory(campaignId), "campaign.json"));
                        DeleteCampaignOwnedDataCore(
                            campaignId,
                            ReadString(metadata, "campaignLabel",
                                ReadString(metadata, "mainHeroName", campaignId)),
                            allowBannerlordRunning,
                            "pending_final_save_deletion_recovery");
                    }
                    catch (Exception ex)
                    {
                        Program.LogOperational("campaign.retirement_retry_failed", new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["error"] = ex.Message
                        });
                    }
                }
            }
            finally { Program.CampaignDataGate.ExitWriteLock(); }
        }

        private static void MarkCampaignSaveSyncLifecycle(
            string campaignId,
            string lifecycle,
            bool everRegistered)
        {
            string directory = StrictCampaignDirectory(campaignId);
            string path = Path.Combine(directory, "campaign.json");
            Dictionary<string, object> metadata = Program.ReadJsonObject(path);
            metadata["campaignId"] = campaignId;
            metadata["saveSyncLifecycle"] = lifecycle ?? "";
            metadata["everRegisteredSaveSyncPoint"] = everRegistered;
            metadata["updatedUtc"] = DateTime.UtcNow.ToString(
                "o", CultureInfo.InvariantCulture);
            Program.WriteJsonObject(path, metadata);
        }

        private static Dictionary<string, object> EmptyCampaignDeletionCounts()
        {
            return new Dictionary<string, object>
            {
                ["postgresql"] = new Dictionary<string, object>
                    { ["schemas"] = 0, ["snapshots"] = 0 },
                ["campaignFiles"] = new Dictionary<string, object>
                    { ["roots"] = 0, ["files"] = 0, ["bytes"] = 0L },
                ["saveSync"] = new Dictionary<string, object>
                    { ["roots"] = 0, ["files"] = 0, ["bytes"] = 0L },
                ["portraitCache"] = new Dictionary<string, object>
                    { ["roots"] = 0, ["files"] = 0, ["bytes"] = 0L },
                ["semanticVectors"] = new Dictionary<string, object>
                    { ["records"] = 0 }
            };
        }

        private static Dictionary<string, object> CampaignDeletionCount(string path)
        {
            return new Dictionary<string, object>
            {
                ["roots"] = Directory.Exists(path) ? 1 : 0,
                ["files"] = SafeFileCount(path, "*"),
                ["bytes"] = SafeDirectorySize(path)
            };
        }

        private static int DeleteCampaignSemanticVectors(string campaignId)
        {
            if (SaveSyncSkipSemanticForTests) return 0;
            List<string> ids;
            using (ReignDbConnection connection =
                ReignPostgreSqlStorage.OpenCampaignConnection(campaignId))
            {
                bool hasEmbeddingDocuments = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_name='embedding_documents';").FirstOrDefault(), "count", 0) > 0;
                ids = hasEmbeddingDocuments
                    ? Program.QuerySql(connection,
                            "SELECT vector_id FROM embedding_documents WHERE vector_id<>'' AND status<>'deleted';")
                        .Select(row => ReadString(row, "vector_id", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList()
                    : new List<string>();
            }
            if (ids.Count == 0) return 0;
            Dictionary<string, object> settings = Program.LoadSettings();
            DialogueService.StartManagedSemanticWorker(settings);
            for (int offset = 0; offset < ids.Count;
                offset += CampaignSemanticDeleteBatchSize)
            {
                Dictionary<string, object> request = DialogueService.SemanticProviderRequest(settings);
                request["collection"] = DialogueService.SemanticCollection;
                request["ids"] = ids.Skip(offset)
                    .Take(CampaignSemanticDeleteBatchSize).ToList();
                Dictionary<string, object> response = TryParseJsonObject(
                    Program.PostJsonToUrl(DialogueService.SemanticWorkerUrl(settings, "/vectors/delete"),
                        Json.Serialize(request), CampaignSemanticDeleteTimeoutMs))
                    ?? new Dictionary<string, object>();
                if (!ReadBool(response, "ok", false))
                    throw new IOException(ReadString(response, "error",
                        "Campaign semantic vector deletion failed."));
            }
            return ids.Count;
        }

        private static void ValidateCampaignOwnedDirectory(
            string path,
            string root,
            bool rejectShared)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
                throw new InvalidDataException("Campaign storage root is unavailable.");
            string fullRoot = Path.GetFullPath(root).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullPath = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string parent = Path.GetDirectoryName(fullPath) ?? "";
            if (!parent.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Campaign deletion target escaped its owned storage root.");
            if (rejectShared && Path.GetFileName(fullPath).Equals(
                "_shared", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The shared portrait library cannot be staged for campaign deletion.");
            if (Directory.Exists(fullPath)
                && (new DirectoryInfo(fullPath).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Campaign deletion refused a reparse-point storage root: " + fullPath);
        }

        private static void VerifyStagedCampaignDirectory(
            string source,
            string staged)
        {
            if (Directory.Exists(source) || !Directory.Exists(staged))
                throw new IOException(
                    "Campaign deletion staging could not be verified for " + source + ".");
        }

        private static void RestoreStagedCampaignDirectory(
            string staged,
            string destination,
            bool wasMoved,
            List<string> errors)
        {
            if (!wasMoved || !Directory.Exists(staged)) return;
            try
            {
                if (Directory.Exists(destination))
                    throw new IOException("Rollback destination already exists: " + destination);
                MoveDirectoryWithRetries(staged, destination);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        private static void DeleteCampaignStagingRoot(
            string path,
            List<string> residuals)
        {
            if (!Directory.Exists(path)) return;
            try
            {
                ValidateCampaignRetirementStagingRoot(path);
                DeleteDirectoryCompletely(path);
            }
            catch
            {
                residuals.Add(path);
            }
        }

        private static void WriteCampaignRetirementTransaction(
            string stagingRoot,
            string campaignId,
            string state)
        {
            Program.WriteJsonObject(Path.Combine(stagingRoot, "transaction.json"),
                new Dictionary<string, object>
                {
                    ["version"] = CampaignRetirementVersion,
                    ["campaignId"] = campaignId,
                    ["state"] = state,
                    ["updatedUtc"] = DateTime.UtcNow.ToString(
                        "o", CultureInfo.InvariantCulture)
                });
        }

        private static void ValidateCampaignRetirementStagingRoot(string path)
        {
            string full = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            bool owned = CampaignRetirementStagingRoots().Any(root =>
                (Path.GetDirectoryName(full) ?? "").Equals(
                    Path.GetFullPath(root).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase));
            if (!owned) throw new InvalidDataException(
                "Campaign retirement staging path escaped its owned root.");
            if ((new DirectoryInfo(full).Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    "Campaign retirement cleanup refused a reparse point.");
        }

        private static List<string> CampaignRetirementStagingRoots()
        {
            string portraitRoot = CampaignPortraitCacheRoot();
            string portraitParent = string.IsNullOrWhiteSpace(portraitRoot)
                ? Program.DataDir
                : Directory.GetParent(Path.GetFullPath(portraitRoot).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar))?.FullName ?? Program.DataDir;
            return new[]
            {
                Path.Combine(Program.DataDir, "campaign-retirement-staging"),
                Path.Combine(portraitParent, ".reign-campaign-retirement-staging")
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void CleanupCampaignRetirementStagingAtStartup()
        {
            foreach (string root in CampaignRetirementStagingRoots())
            {
                if (!Directory.Exists(root)) continue;
                foreach (string child in Directory.GetDirectories(
                    root, "*", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        ValidateCampaignRetirementStagingRoot(child);
                        Dictionary<string, object> transaction = Program.ReadJsonObject(
                            Path.Combine(child, "transaction.json"));
                        string campaignId = ReadString(
                            transaction, "campaignId", "");
                        if (string.IsNullOrWhiteSpace(campaignId))
                            campaignId = CampaignIdFromRetirementStagingName(child);
                        ValidateSaveSyncId(campaignId, "campaignId");
                        bool databaseExists = ReignPostgreSqlStorage.ListCampaignMetadata().Any(row =>
                            ReadString(row, "campaignId", "").Equals(
                                campaignId, StringComparison.OrdinalIgnoreCase));
                        if (databaseExists)
                        {
                            RestoreRetirementTransactionRoot(child, campaignId);
                        }
                        else
                        {
                            DeleteDirectoryCompletely(child);
                        }
                    }
                    catch (Exception ex)
                    {
                        Program.LogOperational("campaign.retirement_staging_cleanup_failed",
                            new Dictionary<string, object>
                            {
                                ["path"] = child,
                                ["error"] = ex.Message
                            });
                    }
                }
            }
        }

        private static string CampaignIdFromRetirementStagingName(string path)
        {
            string name = Path.GetFileName(Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            const int operationTokenLength = 32;
            int separator = name.Length - operationTokenLength - 1;
            if (separator <= 0 || name[separator] != '_')
                throw new InvalidDataException(
                    "Campaign retirement staging receipt is missing or invalid.");
            string operationToken = name.Substring(separator + 1);
            if (operationToken.Length != operationTokenLength
                || operationToken.Any(ch => !Uri.IsHexDigit(ch)))
                throw new InvalidDataException(
                    "Campaign retirement staging receipt is missing or invalid.");
            string campaignId = name.Substring(0, separator);
            ValidateSaveSyncId(campaignId, "campaignId");
            HashSet<string> allowedEntries = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase)
            {
                "campaign", "save-sync", "portrait-cache", "transaction.json"
            };
            if (Directory.GetFileSystemEntries(path).Any(entry =>
                !allowedEntries.Contains(Path.GetFileName(entry))))
                throw new InvalidDataException(
                    "Campaign retirement staging receipt is missing and its contents are not recognized.");
            return campaignId;
        }

        private static void RestoreRetirementTransactionRoot(
            string stagingRoot,
            string campaignId)
        {
            Dictionary<string, string> candidates = new Dictionary<string, string>(
                StringComparer.OrdinalIgnoreCase)
            {
                [Path.Combine(stagingRoot, "campaign")] =
                    StrictCampaignDirectory(campaignId),
                [Path.Combine(stagingRoot, "save-sync")] =
                    SaveSyncCampaignRoot(campaignId),
                [Path.Combine(stagingRoot, "portrait-cache")] =
                    CampaignPortraitCacheDirectory(campaignId)
            };
            foreach (KeyValuePair<string, string> candidate in candidates)
            {
                if (!Directory.Exists(candidate.Key)) continue;
                if (Directory.Exists(candidate.Value))
                    throw new IOException(
                        "Campaign retirement recovery found both staged and active roots for "
                        + campaignId + ". Manual review is required.");
                if (candidate.Key.EndsWith("portrait-cache",
                    StringComparison.OrdinalIgnoreCase))
                    ValidateCampaignOwnedDirectory(candidate.Value,
                        CampaignPortraitCacheRoot(), true);
                else if (candidate.Key.EndsWith("save-sync",
                    StringComparison.OrdinalIgnoreCase))
                    ValidateCampaignOwnedDirectory(candidate.Value,
                        SaveSyncRoot(), false);
                else
                    ValidateCampaignOwnedDirectory(candidate.Value,
                        Program.CampaignsRoot(), false);
                MoveDirectoryWithRetries(candidate.Key, candidate.Value);
            }
            DeleteDirectoryCompletely(stagingRoot);
            Program.LogOperational("campaign.retirement_staging_recovered",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["stagingRoot"] = stagingRoot
                });
        }

        private static List<Dictionary<string, object>> EnumerateOrphanPortraitRoots(
            HashSet<string> campaignIds)
        {
            string root = CampaignPortraitCacheRoot();
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return new List<Dictionary<string, object>>();
            return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals(
                    "_shared", StringComparison.OrdinalIgnoreCase))
                .Where(path => !campaignIds.Contains(Path.GetFileName(path)))
                .Select(path => OrphanStorageRoot("orphan_portrait_root", path))
                .ToList();
        }

        private static List<Dictionary<string, object>> EnumerateOrphanCampaignRoots(
            HashSet<string> campaignIds)
        {
            string root = Program.CampaignsRoot();
            if (!Directory.Exists(root))
                return new List<Dictionary<string, object>>();
            return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).StartsWith(".", StringComparison.Ordinal))
                .Select(path => new
                {
                    Path = path,
                    Id = ReadString(Program.ReadJsonObject(Path.Combine(path, "campaign.json")),
                        "campaignId", Path.GetFileName(path))
                })
                .Where(item => !campaignIds.Contains(item.Id))
                .Select(item => OrphanStorageRoot("orphan_campaign_root", item.Path))
                .ToList();
        }

        private static List<Dictionary<string, object>> EnumerateOrphanSaveSyncRoots(
            HashSet<string> campaignIds)
        {
            string root = SaveSyncRoot();
            if (!Directory.Exists(root))
                return new List<Dictionary<string, object>>();
            return Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly)
                .Where(path => !Path.GetFileName(path).Equals(
                    "t", StringComparison.OrdinalIgnoreCase))
                .Select(path => new
                {
                    Path = path,
                    Id = ReadString(Program.ReadJsonObject(Path.Combine(path, "ledger.json")),
                        "campaignId", "")
                })
                .Where(item => string.IsNullOrWhiteSpace(item.Id)
                    || !campaignIds.Contains(item.Id))
                .Select(item => OrphanStorageRoot("orphan_save_sync_root", item.Path))
                .ToList();
        }

        private static Dictionary<string, object> OrphanStorageRoot(
            string category,
            string path)
        {
            bool reparse = Directory.Exists(path)
                && (new DirectoryInfo(path).Attributes & FileAttributes.ReparsePoint) != 0;
            return new Dictionary<string, object>
            {
                ["category"] = category,
                ["name"] = Path.GetFileName(path),
                ["path"] = path,
                ["reparsePoint"] = reparse,
                ["fileCount"] = reparse ? 0 : SafeFileCount(path, "*"),
                ["bytes"] = reparse ? 0L : SafeDirectorySize(path),
                ["eligibleForPermanentDeletion"] = !reparse
            };
        }

        private static string CampaignMutationBlockReason()
        {
            if (CampaignMutationAllowDuringTests) return "";
            try
            {
                bool running = Process.GetProcesses().Any(x => x.ProcessName.IndexOf("Bannerlord", StringComparison.OrdinalIgnoreCase) >= 0);
                return running ? "Close Bannerlord before importing or deleting campaign data." : "";
            }
            catch { return ""; }
        }

        private static void CopyDirectoryTree(string source, string destination, Func<string, bool> include)
        {
            Directory.CreateDirectory(destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destination, RelativeBackupPath(source, directory).Replace('/', Path.DirectorySeparatorChar)));
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                if (include != null && !include(file)) continue;
                string target = Path.Combine(destination, RelativeBackupPath(source, file).Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(file, target, true);
            }
        }

        private static bool IsLiveSqliteSidecar(string path)
        {
            string name = Path.GetFileName(path);
            return name.Equals("world_memory.sqlite", StringComparison.OrdinalIgnoreCase) || name.Equals("world_memory.sqlite-wal", StringComparison.OrdinalIgnoreCase) || name.Equals("world_memory.sqlite-shm", StringComparison.OrdinalIgnoreCase);
        }

        private static string RelativeBackupPath(string root, string path)
        {
            Uri rootUri = new Uri(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(path))).ToString()).Replace('\\', '/');
        }

        private static string SafeBackupPath(string root, string relative)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, relative ?? ""));
            if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) && !full.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Archive contains an unsafe path.");
            return full;
        }

        internal static string FileSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        private static long SafeDirectorySize(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? 0 : Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length); }
            catch { return 0; }
        }

        private static int SafeDirectoryCount(string path) { try { return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0; } catch { return 0; } }
        private static int SafeFileCount(string path, string pattern) { try { return string.IsNullOrWhiteSpace(path) || !Directory.Exists(path) ? 0 : Directory.EnumerateFiles(path, pattern, SearchOption.AllDirectories).Count(); } catch { return 0; } }
        internal static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            ReignPostgreSqlStorage.ClearAllPools();
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
                catch { if (attempt < 3) Thread.Sleep(100 * (attempt + 1)); }
            }
        }
        private static void MoveDirectoryWithRetries(string source, string destination)
        {
            ReignPostgreSqlStorage.ClearAllPools();
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { Directory.Move(source, destination); return; }
                catch (Exception ex) { last = ex; if (attempt < 4) Thread.Sleep(120 * (attempt + 1)); }
            }
            throw last ?? new IOException("Directory move failed.");
        }
        private static void MoveFileWithRetries(string source, string destination)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            Exception last = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try { File.Move(source, destination); return; }
                catch (Exception ex) { last = ex; if (attempt < 4) Thread.Sleep(120 * (attempt + 1)); }
            }
            throw last ?? new IOException("File move failed.");
        }
        private static void DeleteDirectoryCompletely(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            ReignPostgreSqlStorage.ClearAllPools();
            Exception last = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (!Directory.Exists(path)) return;
                    DeleteDirectoryTreePass(path);
                    if (!Directory.Exists(path)) return;
                }
                catch (Exception ex) { last = ex; }
                if (attempt < 7) Thread.Sleep(200 * (attempt + 1));
            }
            throw new IOException("Reign staged the campaign data but could not finish deleting it. " + (last?.Message ?? ""));
        }

        private static void DeleteDirectoryTreePass(string path)
        {
            Stack<string> pending = new Stack<string>();
            List<string> directories = new List<string>();
            pending.Push(ExtendedLengthPath(path));
            while (pending.Count > 0)
            {
                string current = pending.Pop();
                if (!Directory.Exists(current)) continue;
                directories.Add(current);
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(current); }
                catch (DirectoryNotFoundException) { continue; }
                foreach (string entry in entries)
                {
                    FileAttributes attributes;
                    try { attributes = File.GetAttributes(entry); }
                    catch (FileNotFoundException) { continue; }
                    catch (DirectoryNotFoundException) { continue; }
                    bool directory = (attributes & FileAttributes.Directory) != 0;
                    bool reparse = (attributes & FileAttributes.ReparsePoint) != 0;
                    if (directory && !reparse)
                    {
                        pending.Push(entry);
                        continue;
                    }
                    try
                    {
                        File.SetAttributes(entry,
                            attributes & ~FileAttributes.ReadOnly);
                        if (directory) Directory.Delete(entry, false);
                        else File.Delete(entry);
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
            }

            foreach (string directory in directories
                .OrderByDescending(value => value.Length))
            {
                if (!Directory.Exists(directory)) continue;
                try
                {
                    FileAttributes attributes = File.GetAttributes(directory);
                    File.SetAttributes(directory,
                        attributes & ~FileAttributes.ReadOnly);
                }
                catch (FileNotFoundException) { continue; }
                catch (DirectoryNotFoundException) { continue; }
                try { Directory.Delete(directory, false); }
                catch (DirectoryNotFoundException) { }
            }
        }

        private static string ExtendedLengthPath(string path)
        {
            string full = Path.GetFullPath(path);
            if (Path.DirectorySeparatorChar != '\\'
                || full.StartsWith("\\\\?\\", StringComparison.Ordinal))
                return full;
            if (full.StartsWith("\\\\", StringComparison.Ordinal))
                return "\\\\?\\UNC\\" + full.Substring(2);
            return "\\\\?\\" + full;
        }
        internal static void TryDeleteFile(string path) { try { if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) File.Delete(path); } catch { } }

        private static void WriteDownloadFile(NetworkStream stream, string path, string contentType, string downloadName)
        {
            if (!File.Exists(path)) { Program.WriteJson(stream, 404, new Dictionary<string, object> { ["ok"] = false, ["error"] = "File not found." }); return; }
            FileInfo info = new FileInfo(path);
            string header = "HTTP/1.1 200 OK\r\nContent-Type: " + contentType + "\r\nContent-Length: " + info.Length.ToString(CultureInfo.InvariantCulture) + "\r\nContent-Disposition: attachment; filename=\"" + (downloadName ?? "campaign.reignbackup").Replace("\"", "") + "\"\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(header);
            stream.Write(headerBytes, 0, headerBytes.Length);
            byte[] buffer = new byte[1024 * 1024];
            using (FileStream input = File.OpenRead(path))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) stream.Write(buffer, 0, read);
            }
        }
    }
}
