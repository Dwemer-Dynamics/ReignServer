using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> SaveSyncLoadPostgreSql(
            Dictionary<string, object> payload,
            bool dryRun)
        {
            payload = payload ?? new Dictionary<string, object>();
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "");
            string savePointId = ReadString(payload, "savePointId", "");
            string loadSessionId = ReadString(payload, "loadSessionId", "");
            ValidateSaveSyncId(campaignId, "campaignId");
            ValidateSaveSyncId(savePointId, "savePointId");
            if (!string.IsNullOrWhiteSpace(loadSessionId))
                ValidateSaveSyncId(loadSessionId, "loadSessionId");

            Dictionary<string, object> ledger = ReadSaveSyncLedger(campaignId);
            Dictionary<string, object> point = SaveSyncPoint(ledger, savePointId);
            if (point == null
                && ReadString(payload, "savePointKind", "")
                    .Equals("legacy_baseline", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> registered = SaveSyncRegisterCore(payload);
                registered["result"] = "legacy_baseline_registered";
                registered["noOp"] = true;
                registered["dryRun"] = dryRun;
                return registered;
            }

            string pointRoot = point == null
                ? SaveSyncPointRoot(campaignId, savePointId)
                : SaveSyncSnapshotRoot(campaignId, point);
            string postgresSnapshotPointId = point == null
                ? savePointId
                : SaveSyncPostgreSqlSnapshotPointId(point);
            bool registeredSnapshot = point != null
                && ReignPostgreSqlStorage.SnapshotExists(campaignId,
                    postgresSnapshotPointId)
                && Directory.Exists(Path.Combine(pointRoot, "campaign"))
                && File.Exists(Path.Combine(pointRoot, "manifest.json"));
            if (!registeredSnapshot)
            {
                if (!ReadBool(payload, "registrationConfirmed", true))
                {
                    Dictionary<string, object> baseline =
                        new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["result"] = "unregistered_save_baseline",
                            ["campaignId"] = campaignId,
                            ["savePointId"] = savePointId,
                            ["noOp"] = true,
                            ["degraded"] = true,
                            ["warning"] =
                                "This native save was not registered with Save Sync. Reign kept the current campaign state and released the alignment gate."
                        };
                    AppendSaveSyncAudit(
                        campaignId, "load.unregistered_save_baseline", baseline);
                    return baseline;
                }
                Dictionary<string, object> missing =
                    new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["result"] = "snapshot_missing",
                        ["campaignId"] = campaignId,
                        ["savePointId"] = savePointId,
                        ["error"] =
                            "Reign has no PostgreSQL snapshot for this native save. Campaign data was left untouched."
                    };
                AppendSaveSyncAudit(campaignId, "load.snapshot_missing", missing);
                return missing;
            }

            Dictionary<string, object> manifest =
                ReadJsonObject(Path.Combine(pointRoot, "manifest.json"));
            if (!ReadString(manifest, "campaignId", "")
                    .Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                || !ReadString(manifest, "fingerprint", "")
                    .Equals(
                        ReadString(point, "snapshotFingerprint", ""),
                        StringComparison.OrdinalIgnoreCase)
                || !ReadString(manifest, "databaseProvider", "")
                    .Equals("postgresql", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Save Sync snapshot identity is inconsistent; campaign data was left untouched.");
            }

            Dictionary<string, object> replay =
                TryReplaySaveSyncLoadReceipt(
                    ledger, savePointId, loadSessionId, timer);
            if (replay != null)
                return replay;

            bool activeClean =
                IsSaveSyncActiveStateCleanForPoint(campaignId, savePointId);
            string currentToken =
                ReignPostgreSqlStorage.CampaignStateToken(campaignId);
            string targetToken =
                ReignPostgreSqlStorage.SnapshotStateToken(
                    campaignId, postgresSnapshotPointId);
            Dictionary<string, object> countsBySubsystem =
                ComparePostgreSqlSaveSyncRows(
                    campaignId, postgresSnapshotPointId, pointRoot, manifest);
            Dictionary<string, object> result =
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["result"] = dryRun
                        ? "preview"
                        : activeClean ? "no_op" : "rollback_required",
                    ["dryRun"] = dryRun,
                    ["noOp"] = activeClean,
                    ["wouldChange"] = !activeClean,
                    ["campaignId"] = campaignId,
                    ["savePointId"] = savePointId,
                    ["savePoint"] = PublicSaveSyncPoint(point),
                    ["databaseProvider"] = "postgresql",
                    ["databaseName"] = ReignPostgreSqlOptions.RequiredDatabaseName,
                    ["currentStateToken"] = currentToken,
                    ["targetStateToken"] = targetToken,
                    ["currentDatabaseBytes"] =
                        ReignPostgreSqlStorage.CampaignSizeBytes(campaignId),
                    ["targetDatabaseBytes"] =
                        ReignPostgreSqlStorage.SnapshotSizeBytes(
                            campaignId, postgresSnapshotPointId),
                    ["countsBySubsystem"] = countsBySubsystem,
                    ["recordsRolledBack"] =
                        SumSaveSyncCount(countsBySubsystem, "removed"),
                    ["recordsRestored"] =
                        SumSaveSyncCount(countsBySubsystem, "restored"),
                    ["changedFiles"] = activeClean ? 0 : 1,
                    ["backupPlanned"] = !activeClean
                };
            if (!string.IsNullOrWhiteSpace(loadSessionId))
                result["loadSessionId"] = loadSessionId;
            if (dryRun)
            {
                timer.Stop();
                result["durationMs"] = timer.ElapsedMilliseconds;
                return result;
            }
            if (activeClean)
            {
                timer.Stop();
                result["durationMs"] = timer.ElapsedMilliseconds;
                StoreSaveSyncLoadReceipt(
                    ledger, savePointId, loadSessionId, result);
                UpdateSaveSyncLastResult(ledger, point, result, "");
                WriteSaveSyncLedger(campaignId, ledger);
                AppendSaveSyncAudit(campaignId, "load.no_op", result);
                return result;
            }

            string operationId = DateTime.UtcNow.ToString(
                "yyyyMMddHHmmss", CultureInfo.InvariantCulture)
                + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string recoveryPointId = "__recovery_" + operationId;
            string campaignDir = StrictCampaignDirectory(campaignId);
            string recoveryRoot = Path.Combine(
                SaveSyncCampaignRoot(campaignId), "r", operationId);
            string recoveryCampaign = Path.Combine(recoveryRoot, "campaign");
            // The durable Save Sync root may live on a different volume from the
            // installed server. Keep the immutable snapshot and recovery copy in
            // that stable root, but stage the directory swap beside the active
            // campaign so Directory.Move remains an atomic same-volume rename.
            string campaignWorkRoot = SaveSyncCampaignWorkRoot(
                campaignDir,
                operationId,
                Guid.NewGuid().ToString("N").Substring(0, 8));
            string stagedCampaign = Path.Combine(campaignWorkRoot, "staged");
            bool databaseRestored = false;
            bool filesInstalled = false;
            string phase = "prepare_recovery";
            string displacedCampaign = Path.Combine(campaignWorkRoot, "active");
            try
            {
                phase = "snapshot_active_database";
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(
                    campaignId, recoveryPointId);
                phase = "snapshot_active_campaign";
                Directory.CreateDirectory(recoveryCampaign);
                if (Directory.Exists(campaignDir))
                    CopyDirectoryTree(
                        campaignDir,
                        recoveryCampaign,
                        path => !IsLiveSqliteSidecar(path));

                phase = "stage_target_campaign";
                CopyDirectoryTree(
                    Path.Combine(pointRoot, "campaign"),
                    stagedCampaign,
                    path => !IsLiveSqliteSidecar(path));
                int retainedAssets = MergeSaveSyncRetainedAssets(
                    campaignDir, stagedCampaign);

                SetSaveSyncSemanticBlocked(campaignId, true);
                phase = "restore_target_database";
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(
                    campaignId, postgresSnapshotPointId);
                databaseRestored = true;
                if (SaveSyncFailureInjectionForTests == "after_stage")
                    throw new InvalidOperationException(
                        "Injected Save Sync failure after PostgreSQL restore.");

                phase = "move_active_campaign";
                if (Directory.Exists(campaignDir))
                    MoveDirectoryWithRetries(campaignDir, displacedCampaign);
                phase = "install_target_campaign";
                MoveDirectoryWithRetries(stagedCampaign, campaignDir);
                filesInstalled = true;
                if (SaveSyncFailureInjectionForTests == "after_install")
                    throw new InvalidOperationException(
                        "Injected Save Sync failure after installing campaign assets.");

                ClearSaveSyncCaches(campaignId);
                MarkSaveSyncActiveStateClean(campaignId, point);
                timer.Stop();
                result["result"] = "rolled_back";
                result["noOp"] = false;
                result["wouldChange"] = true;
                result["durationMs"] = timer.ElapsedMilliseconds;
                result["backupCreated"] = true;
                result["backupId"] = operationId;
                result["backupPath"] = recoveryRoot;
                result["retainedStaticAssetFiles"] = retainedAssets;
                result["databaseRestored"] = true;
                result["completedPhase"] = phase;
                StoreSaveSyncLoadReceipt(
                    ledger, savePointId, loadSessionId, result);
                UpdateSaveSyncLastResult(
                    ledger, point, result, operationId);
                WriteSaveSyncLedger(campaignId, ledger);
                AppendSaveSyncAudit(
                    campaignId, "load.rolled_back", result);
                TryDeleteDirectory(displacedCampaign);
                ReignPostgreSqlStorage.DropSnapshot(
                    campaignId, recoveryPointId);
                SetSaveSyncSemanticBlocked(campaignId, false);
                CleanupSaveSyncRecovery(campaignId);
                return result;
            }
            catch (Exception ex)
            {
                try
                {
                    if (filesInstalled && Directory.Exists(campaignDir))
                        TryDeleteDirectory(campaignDir);
                    if (Directory.Exists(displacedCampaign))
                        MoveDirectoryWithRetries(
                            displacedCampaign, campaignDir);
                    else if (!Directory.Exists(campaignDir)
                        && Directory.Exists(recoveryCampaign))
                        CopyDirectoryTree(
                            recoveryCampaign,
                            campaignDir,
                            path => !IsLiveSqliteSidecar(path));
                    if (databaseRestored)
                        ReignPostgreSqlStorage.RestoreCampaignSnapshot(
                            campaignId, recoveryPointId);
                    ClearSaveSyncCaches(campaignId);
                }
                catch (Exception recoveryEx)
                {
                    throw new InvalidOperationException(
                        "Save Sync failed and PostgreSQL recovery also failed. "
                        + "Original error: " + ex
                        + " Recovery error: " + recoveryEx,
                        recoveryEx);
                }
                finally
                {
                    try
                    {
                        ReignPostgreSqlStorage.DropSnapshot(
                            campaignId, recoveryPointId);
                    }
                    catch { }
                    SetSaveSyncSemanticBlocked(campaignId, false);
                }
                timer.Stop();
                Dictionary<string, object> failed =
                    new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["result"] = "failed_recovered",
                        ["campaignId"] = campaignId,
                        ["savePointId"] = savePointId,
                        ["backupCreated"] = Directory.Exists(recoveryRoot),
                        ["backupId"] = operationId,
                        ["backupPath"] = recoveryRoot,
                        ["failedPhase"] = phase,
                        ["error"] = ex.ToString(),
                        ["durationMs"] = timer.ElapsedMilliseconds
                    };
                UpdateSaveSyncLastResult(
                    ledger, point, failed, operationId);
                try { WriteSaveSyncLedger(campaignId, ledger); } catch { }
                AppendSaveSyncAudit(
                    campaignId, "load.failed_recovered", failed);
                return failed;
            }
            finally
            {
                TryDeleteDirectory(campaignWorkRoot);
            }
        }

        private static Dictionary<string, object> TryReplaySaveSyncLoadReceipt(
            Dictionary<string, object> ledger,
            string savePointId,
            string loadSessionId,
            Stopwatch timer)
        {
            if (string.IsNullOrWhiteSpace(loadSessionId))
                return null;
            Dictionary<string, object> receipt =
                ReadDictionary(ledger, "lastLoadReceipt");
            if (receipt == null
                || !ReadString(receipt, "loadSessionId", "")
                    .Equals(loadSessionId, StringComparison.OrdinalIgnoreCase)
                || !ReadString(receipt, "savePointId", "")
                    .Equals(savePointId, StringComparison.OrdinalIgnoreCase))
                return null;
            Dictionary<string, object> stored =
                ReadDictionary(receipt, "response");
            if (stored == null || !ReadBool(stored, "ok", false))
                return null;
            Dictionary<string, object> replay =
                new Dictionary<string, object>(
                    stored, StringComparer.OrdinalIgnoreCase);
            timer.Stop();
            replay["idempotentReplay"] = true;
            replay["loadSessionId"] = loadSessionId;
            replay["replayDurationMs"] = timer.ElapsedMilliseconds;
            return replay;
        }

        private static void StoreSaveSyncLoadReceipt(
            Dictionary<string, object> ledger,
            string savePointId,
            string loadSessionId,
            Dictionary<string, object> response)
        {
            if (string.IsNullOrWhiteSpace(loadSessionId)
                || response == null
                || !ReadBool(response, "ok", false))
                return;
            ledger["lastLoadReceipt"] = new Dictionary<string, object>
            {
                ["loadSessionId"] = loadSessionId,
                ["savePointId"] = savePointId,
                ["completedUtc"] = DateTime.UtcNow.ToString("o"),
                ["response"] = new Dictionary<string, object>(
                    response, StringComparer.OrdinalIgnoreCase)
            };
        }

        private static Dictionary<string, object>
            ComparePostgreSqlSaveSyncRows(
                string campaignId,
                string savePointId,
                string targetSnapshotRoot,
                Dictionary<string, object> targetManifest)
        {
            Dictionary<string, object> counts =
                new Dictionary<string, object>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (string subsystem in SaveSyncSubsystemNames())
            {
                counts[subsystem] = new Dictionary<string, object>
                {
                    ["current"] = 0L,
                    ["target"] = 0L,
                    ["removed"] = 0L,
                    ["restored"] = 0L,
                    ["affected"] = 0L,
                    ["netChange"] = 0L
                };
            }

            string currentSchema =
                ReignPostgreSqlStorage.CampaignSchemaName(campaignId);
            string targetSchema =
                ReignPostgreSqlStorage.SnapshotSchemaName(
                    campaignId, savePointId);
            using (NpgsqlConnection connection =
                ReignPostgreSqlStorage.OpenUtilityConnection(
                    currentSchema))
            {
                HashSet<string> currentTables =
                    ReadPostgreSqlSchemaTables(
                        connection, currentSchema);
                HashSet<string> targetTables =
                    ReadPostgreSqlSchemaTables(
                        connection, targetSchema);
                foreach (string table in currentTables.Union(
                    targetTables, StringComparer.OrdinalIgnoreCase))
                {
                    string subsystem =
                        SaveSyncSubsystemForTable(table);
                    Dictionary<string, object> row =
                        ReadDictionary(counts, subsystem)
                        ?? ReadDictionary(counts, "other");
                    bool hasCurrent = currentTables.Contains(table);
                    bool hasTarget = targetTables.Contains(table);
                    long current = hasCurrent
                        ? CountPostgreSqlTableRows(
                            connection, currentSchema, table)
                        : 0L;
                    long target = hasTarget
                        ? CountPostgreSqlTableRows(
                            connection, targetSchema, table)
                        : 0L;
                    long removed;
                    long restored;
                    if (hasCurrent && hasTarget
                        && PostgreSqlTableColumnsMatch(
                            connection, currentSchema,
                            targetSchema, table))
                    {
                        removed = CountPostgreSqlRowDifference(
                            connection, currentSchema,
                            targetSchema, table);
                        restored = CountPostgreSqlRowDifference(
                            connection, targetSchema,
                            currentSchema, table);
                    }
                    else
                    {
                        removed = current;
                        restored = target;
                    }

                    row["current"] =
                        ReadLong(row, "current", 0) + current;
                    row["target"] =
                        ReadLong(row, "target", 0) + target;
                    row["removed"] =
                        ReadLong(row, "removed", 0) + removed;
                    row["restored"] =
                        ReadLong(row, "restored", 0) + restored;
                }
            }

            ComparePostgreSqlSaveSyncFiles(
                campaignId, targetSnapshotRoot,
                targetManifest, counts);
            foreach (Dictionary<string, object> row
                in counts.Values.OfType<Dictionary<string, object>>())
            {
                row["affected"] =
                    ReadLong(row, "removed", 0)
                    + ReadLong(row, "restored", 0);
                row["netChange"] =
                    ReadLong(row, "target", 0)
                    - ReadLong(row, "current", 0);
            }
            return counts;
        }

        private static void ComparePostgreSqlSaveSyncFiles(
            string campaignId,
            string targetSnapshotRoot,
            Dictionary<string, object> targetManifest,
            Dictionary<string, object> counts)
        {
            string currentRoot = StrictCampaignDirectory(campaignId);
            Dictionary<string, Dictionary<string, object>> currentFiles =
                BackupFileManifest(currentRoot)
                    .Where(row => !IsSaveSyncFingerprintExcluded(
                        ReadString(row, "path", "")))
                    .ToDictionary(
                        row => ReadString(row, "path", ""),
                        row => row,
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> targetFiles =
                ReadDictionaryList(targetManifest, "files")
                    .Where(row => !IsSaveSyncFingerprintExcluded(
                        ReadString(row, "path", "")))
                    .ToDictionary(
                        row => ReadString(row, "path", ""),
                        row => row,
                        StringComparer.OrdinalIgnoreCase);
            foreach (string relative in currentFiles.Keys.Union(
                targetFiles.Keys, StringComparer.OrdinalIgnoreCase))
            {
                bool hasCurrent = currentFiles.TryGetValue(
                    relative,
                    out Dictionary<string, object> currentFile);
                bool hasTarget = targetFiles.TryGetValue(
                    relative,
                    out Dictionary<string, object> targetFile);
                if (hasCurrent && hasTarget
                    && ReadString(currentFile, "sha256", "")
                        .Equals(
                            ReadString(targetFile, "sha256", ""),
                            StringComparison.OrdinalIgnoreCase))
                    continue;

                string currentPath = hasCurrent
                    ? Path.Combine(
                        currentRoot,
                        relative.Replace(
                            '/',
                            Path.DirectorySeparatorChar))
                    : string.Empty;
                string targetPath = hasTarget
                    ? SaveSyncSnapshotFile(
                        targetSnapshotRoot, relative)
                    : string.Empty;
                long removed = 0L;
                long restored = 0L;
                if (hasCurrent && hasTarget
                    && relative.EndsWith(
                        ".jsonl",
                        StringComparison.OrdinalIgnoreCase))
                {
                    CompareSaveSyncJsonLines(
                        currentPath, targetPath,
                        out removed, out restored);
                }
                else
                {
                    if (hasCurrent)
                        removed = SaveSyncFileRecordCount(
                            currentPath);
                    if (hasTarget)
                        restored = SaveSyncFileRecordCount(
                            targetPath);
                }
                AddSaveSyncAffectedCounts(
                    counts,
                    SaveSyncSubsystemForPath(relative),
                    removed,
                    restored);
            }
        }

        private static HashSet<string> ReadPostgreSqlSchemaTables(
            NpgsqlConnection connection,
            string schema)
        {
            HashSet<string> tables =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT table_name
FROM information_schema.tables
WHERE table_schema=@schema
  AND table_type='BASE TABLE'
ORDER BY table_name;", connection))
            {
                command.Parameters.AddWithValue("schema", schema);
                using (NpgsqlDataReader reader =
                    command.ExecuteReader())
                {
                    while (reader.Read())
                        tables.Add(reader.GetString(0));
                }
            }
            return tables;
        }

        private static long CountPostgreSqlTableRows(
            NpgsqlConnection connection,
            string schema,
            string table)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM "
                + QuotePostgreSqlIdentifier(schema)
                + "." + QuotePostgreSqlIdentifier(table) + ";",
                connection))
            {
                return Convert.ToInt64(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        private static bool PostgreSqlTableColumnsMatch(
            NpgsqlConnection connection,
            string currentSchema,
            string targetSchema,
            string table)
        {
            using (NpgsqlCommand command = new NpgsqlCommand(@"
SELECT (
    SELECT array_agg(column_name || ':' || data_type
                     ORDER BY ordinal_position)
    FROM information_schema.columns
    WHERE table_schema=@current_schema
      AND table_name=@table
) IS NOT DISTINCT FROM (
    SELECT array_agg(column_name || ':' || data_type
                     ORDER BY ordinal_position)
    FROM information_schema.columns
    WHERE table_schema=@target_schema
      AND table_name=@table
);", connection))
            {
                command.Parameters.AddWithValue(
                    "current_schema", currentSchema);
                command.Parameters.AddWithValue(
                    "target_schema", targetSchema);
                command.Parameters.AddWithValue("table", table);
                return Convert.ToBoolean(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        private static long CountPostgreSqlRowDifference(
            NpgsqlConnection connection,
            string sourceSchema,
            string targetSchema,
            string table)
        {
            string source = QuotePostgreSqlIdentifier(sourceSchema)
                + "." + QuotePostgreSqlIdentifier(table);
            string target = QuotePostgreSqlIdentifier(targetSchema)
                + "." + QuotePostgreSqlIdentifier(table);
            using (NpgsqlCommand command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM ("
                + "SELECT row_to_json(source_row)::text AS row_value FROM "
                + source + " source_row EXCEPT ALL "
                + "SELECT row_to_json(target_row)::text AS row_value FROM "
                + target + " target_row) difference;",
                connection))
            {
                command.CommandTimeout = 180;
                return Convert.ToInt64(
                    command.ExecuteScalar(),
                    CultureInfo.InvariantCulture);
            }
        }

        private static string QuotePostgreSqlIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)
                || !System.Text.RegularExpressions.Regex.IsMatch(
                    identifier,
                    "^[A-Za-z_][A-Za-z0-9_]{0,62}$",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidOperationException(
                    "Unsafe PostgreSQL identifier.");
            return "\"" + identifier.Replace("\"", "\"\"") + "\"";
        }
    }
}
