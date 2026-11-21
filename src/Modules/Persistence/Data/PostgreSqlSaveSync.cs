using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class PersistenceService
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
                if (dryRun)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["result"] = "legacy_baseline_registration_required",
                        ["dryRun"] = true,
                        ["campaignId"] = campaignId,
                        ["savePointId"] = savePointId
                    };

                Dictionary<string, object> registered = SaveSyncRegisterCore(payload);
                registered["dryRun"] = false;
                if (ReadBool(registered, "ok", false))
                {
                    registered["result"] = "legacy_baseline_registered";
                    registered["noOp"] = true;
                }
                return registered;
            }

            string pointRoot = point == null
                ? SaveSyncPointRoot(campaignId, savePointId)
                : SaveSyncSnapshotRoot(campaignId, point);
            string postgresSnapshotPointId = point == null
                ? savePointId
                : SaveSyncPostgreSqlSnapshotPointId(point);
            string manifestPath = Path.Combine(pointRoot, "manifest.json");
            var manifest = point != null && Directory.Exists(Path.Combine(pointRoot, "campaign")) && File.Exists(manifestPath)
                ? CampaignFileService.ReadJsonObject(manifestPath) : null;
            if (manifest != null && (!ReadString(manifest, "campaignId", "").Equals(campaignId, StringComparison.OrdinalIgnoreCase)
                || !ReadString(manifest, "fingerprint", "").Equals(ReadString(point, "snapshotFingerprint", ""), StringComparison.OrdinalIgnoreCase)
                || !ReadString(manifest, "databaseProvider", "").Equals("postgresql", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Save Sync snapshot identity is inconsistent; campaign data was left untouched.");

            // Reject a damaged or unbound calendar before any SQL initialization, recovery copy, or active-state change.
            string calendarSha = manifest == null || dryRun ? null : VerifySaveSyncCalendarCheckpoint(campaignId, pointRoot, point, manifest, payload);
            bool registeredSnapshot = manifest != null && ReignPostgreSqlStorage.SnapshotExists(campaignId, postgresSnapshotPointId);
            if (!registeredSnapshot)
            {
                if (!ReadBool(payload, "registrationConfirmed", true))
                {
                    Dictionary<string, object> baseline =
                        new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["result"] = "unregistered_save_baseline",
                            ["dryRun"] = dryRun,
                            ["campaignId"] = campaignId,
                            ["savePointId"] = savePointId,
                            ["noOp"] = true,
                            ["degraded"] = true,
                            ["warning"] =
                                "This native save was not registered with Save Sync. Reign kept the current campaign state and released the alignment gate."
                        };
                    if (!dryRun)
                        AppendSaveSyncAudit(campaignId, "load.unregistered_save_baseline", baseline);
                    return baseline;
                }
                Dictionary<string, object> missing =
                    new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["result"] = "snapshot_missing",
                        ["dryRun"] = dryRun,
                        ["campaignId"] = campaignId,
                        ["savePointId"] = savePointId,
                        ["error"] =
                            "Reign has no PostgreSQL snapshot for this native save. Campaign data was left untouched."
                    };
                if (!dryRun)
                    AppendSaveSyncAudit(campaignId, "load.snapshot_missing", missing);
                return missing;
            }

            if (!dryRun)
            {
                Dictionary<string, object> replay =
                    TryReplaySaveSyncLoadReceipt(ledger, savePointId, loadSessionId, timer);
                if (replay != null)
                {
                    RequireSaveSyncCalendarDigest(calendarSha, SaveSyncCalendarDigest(ReadSaveSyncCalendar(StrictCampaignDirectory(campaignId))));
                    return replay;
                }
            }

            bool activeClean =
                IsSaveSyncActiveStateCleanForPoint(campaignId, savePointId)
                && (dryRun || calendarSha == SaveSyncCalendarDigest(ReadSaveSyncCalendar(StrictCampaignDirectory(campaignId))));
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
                result["outcome"] = "unchanged";
                // Initialize only from this identity-verified native save. Every
                // source is compared with its snapshot row; the no-op marker is
                // not permission to include arbitrary current/foreign history.
                bool memorySourcesInitialized = false;
                using (var memoryConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    var memoryContext = DialogueService.RestoredMemorySourceContext(memoryConnection, ReadString(point, "timelineId", ""));
                    if (ReadString(memoryContext, "save_point_id", "") != savePointId
                        || ReadString(memoryContext, "legacy_recovery_version", "") != DialogueService.LegacyMemoryRecoveryVersion)
                    {
                        DialogueService.InitializeVerifiedSnapshotMemorySources(campaignId, point, postgresSnapshotPointId, loadSessionId);
                        result["memorySourceAccess"] = "verified_snapshot_membership_initialized";
                        memorySourcesInitialized = true;
                    }
                }
                // Bind this load session to the unchanged context. Every campaign SQL write marks
                // the active state dirty, which would turn the next load of this same save into a
                // full rollback; the bind changes no campaign content (CampaignDataGate write lock
                // held, calendar unchanged), so the verified clean marker is restored.
                // A failed bind only leaves the load unproven (no restoreGeneration echo).
                if (!memorySourcesInitialized && !string.IsNullOrWhiteSpace(loadSessionId))
                {
                    try
                    {
                        DialogueService.BindVerifiedNoOpLoadSession(campaignId, savePointId, postgresSnapshotPointId, loadSessionId);
                        MarkSaveSyncActiveStateClean(campaignId, point);
                    }
                    catch (Exception ex) when (!(ex is CampaignRequestReplacedException) && !(ex is OperationCanceledException))
                    {
                        OperationsService.LogOperational("save_sync.load_proof_bind_failed", TestDict("campaignId", campaignId,
                            "savePointId", savePointId, "error", ex.GetType().Name));
                    }
                }
                string noOpProofGeneration = DialogueService.TerminalReportLoadProofGeneration(campaignId, savePointId, loadSessionId);
                if (noOpProofGeneration.Length > 0) result["restoreGeneration"] = noOpProofGeneration;
                timer.Stop();
                result["durationMs"] = timer.ElapsedMilliseconds;
                StoreSaveSyncLoadReceipt(
                    ledger, savePointId, loadSessionId, result);
                UpdateSaveSyncLastResult(ledger, point, result, "");
                WriteSaveSyncLedger(campaignId, ledger);
                AppendSaveSyncAudit(campaignId, "load.no_op", result);
                return result;
            }

            return ApplyPostgreSqlSaveSync(campaignId, savePointId, loadSessionId, pointRoot,
                postgresSnapshotPointId, calendarSha, manifest, ledger, point, result, timer);
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
            AddSaveSyncOperationStatus(replay, ReadString(replay, "campaignId", ""), ReadString(replay, "backupId", ""));
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

        private static string SaveSyncDatabaseHash(string schema)
        {
            // Physical relation sizes and statistics change across clones. Frame each
            // table with its ordered column shape and a digest of its row multiset.
            using var connection = ReignPostgreSqlStorage.OpenMetadataConnection();
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.RepeatableRead);
            using (var exists = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_namespace WHERE nspname=@schema);",
                connection, transaction))
            {
                exists.Parameters.AddWithValue("schema", schema);
                if (!(bool)exists.ExecuteScalar()) throw new InvalidDataException("Save Sync SQL schema is missing.");
            }
            using var shape = new NpgsqlCommand(@"
SELECT c.relname, COALESCE(json_agg(json_build_array(a.attname, format_type(a.atttypid, a.atttypmod))
       ORDER BY a.attnum) FILTER (WHERE a.attnum IS NOT NULL), '[]')::text
FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
LEFT JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped
WHERE n.nspname=@schema AND c.relkind IN ('r','p')
GROUP BY c.relname ORDER BY c.relname COLLATE ""C"";", connection, transaction);
            shape.Parameters.AddWithValue("schema", schema);
            var tables = new List<(string Name, string Columns)>();
            using (var reader = shape.ExecuteReader())
                while (reader.Read()) tables.Add((reader.GetString(0), reader.GetString(1)));
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var rows = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var table in tables)
            {
                hash.AppendData(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { table.Name, table.Columns })));
                string qualified = QuotePostgreSqlIdentifier(schema) + "." + QuotePostgreSqlIdentifier(table.Name);
                using var command = new NpgsqlCommand("SELECT sha256(convert_to(row_to_json(r)::text,'UTF8')) FROM "
                    + qualified + " r ORDER BY 1;", connection, transaction) { CommandTimeout = 180 };
                using var reader = command.ExecuteReader();
                while (reader.Read()) rows.AppendData(reader.GetFieldValue<byte[]>(0));
                hash.AppendData(rows.GetHashAndReset());
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        private static void VerifySaveSyncDatabase(string schema, string expected)
        {
            if (SaveSyncDatabaseHash(schema) != expected)
                throw new InvalidDataException("Save Sync SQL state differs from the operation snapshot.");
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
