using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Npgsql;
using Reign.Core.Contracts.Court;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        [ThreadStatic] internal static Action<bool> CampaignSchemaSettingsReadObserverForTests;
        [ThreadStatic] internal static Action<ReignDbConnection> CampaignSchemaConnectionOpenedForTests;
        [ThreadStatic] internal static Action CampaignSchemaInitializingForTests;
        [ThreadStatic] internal static Action CampaignSchemaAfterMemoryForTests;
        [ThreadStatic] internal static Action<bool> CampaignSchemaColdWaitObserverForTests;

        internal static ReignDbConnection OpenCampaignConnection(string campaignId)
        {
            // A caller must not carry the reverse order into the cold path.
            if (Monitor.IsEntered(CampaignSchemaLock) && !Monitor.IsEntered(FileLock))
                throw new InvalidOperationException("Campaign schema access requires FileLock before CampaignSchemaLock.");
            Directory.CreateDirectory(CampaignDirectory(campaignId));
            ReignDbConnection connection = ReignPostgreSqlStorage.OpenCampaignConnection(campaignId);
            try
            {
                CampaignSchemaConnectionOpenedForTests?.Invoke(connection);
                string schemaKey = ReignPostgreSqlStorage.CampaignSchemaName(campaignId);
                lock (CampaignSchemaLock)
                {
                    if (IsCampaignSchemaCachedReady(connection, campaignId, schemaKey))
                        return connection;
                }
                // Settings and action files share FileLock. Cold initialization
                // must use FileLock -> SchemaLock, including reentrant action reads.
                // Holding it through initialization also keeps the provider snapshot
                // consistent with settings saves. Warm checkouts never take FileLock.
                CampaignSchemaColdWaitObserverForTests?.Invoke(Monitor.IsEntered(CampaignSchemaLock));
                lock (SettingsSaveGate)
                {
                    string providerGeneration = ReadString(LoadSettings(), "vectorProvider", "local")
                        .ToLowerInvariant() + "|" + DialogueService.SemanticModelVersion;
                    lock (CampaignSchemaLock)
                    {
                        if (IsCampaignSchemaCachedReady(connection, campaignId, schemaKey))
                            return connection;
                        CampaignSchemaInitializingForTests?.Invoke();
                        // Migration 1 (baseline), then the numbered campaign migrations.
                        PersistenceService.ApplyCampaignBaselineSchema(connection, campaignId, providerGeneration);
                        int migrationVersion = PersistenceService.ApplyCampaignSchemaMigrations(connection);
                        ReignPostgreSqlStorage.DeduplicateCampaignIndexes(campaignId);
                        PersistenceService.MarkCampaignMigrationsReady(connection, migrationVersion);
                        CampaignSchemaReady.Add(schemaKey);
                    }
                }
                return connection;
            }
            catch
            {
                // Preserve the initialization exception even if cleanup also fails.
                try { connection.Dispose(); } catch { }
                throw;
            }
        }

        // Caller owns CampaignSchemaLock; no settings/file access on this path.
        private static bool IsCampaignSchemaCachedReady(ReignDbConnection connection,
            string campaignId, string schemaKey)
        {
            if (!CampaignSchemaReady.Contains(schemaKey)) return false;
            if (IsMemorySchemaRevisionCurrent(connection)) return true;
            // Save Sync can replace a schema beneath a pooled connection.
            InvalidateCampaignSchemaCaches(campaignId);
            return false;
        }

        private static bool IsMemorySchemaRevisionCurrent(
            ReignDbConnection connection)
        {
            try
            {
                Dictionary<string, object> version = QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='version' LIMIT 1;")
                    .FirstOrDefault();
                return string.Equals(ReadString(version, "value", ""), "5",
                    StringComparison.Ordinal) && QuerySql(connection,"SELECT value FROM schema_meta WHERE (key='memory_precision_v1' AND value='complete') OR (key IN ('explicit_accepted_commitment_bootstrap_v1','character_performance_backfill_v1','character_commitment_timeline_v1') AND value='1') OR (key='"+PersistenceService.CampaignMigrationVersionKey+"' AND CASE WHEN value ~ '^[0-9]{1,9}$' THEN value::integer>="+PersistenceService.LatestCampaignMigrationVersion.ToString(CultureInfo.InvariantCulture)+" ELSE false END);").Count==5;
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPostgreSqlComponentSchemaReady(
            ReignDbConnection connection, string markerKey, int revision)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return false;
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            string fastPathKey = campaignId + "|" + markerKey + "|"
                + revision.ToString(CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(campaignId)
                && PostgreSqlComponentSchemaFastPaths.ContainsKey(
                    fastPathKey))
                return true;
            string persisted = ReadString(QuerySql(connection, @"
SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object>
                {
                    ["key"] = markerKey
                }).FirstOrDefault(), "value", "");
            bool ready = persisted.Equals(
                revision.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
            if (ready && !string.IsNullOrWhiteSpace(campaignId))
                PostgreSqlComponentSchemaFastPaths[fastPathKey] = 0;
            return ready;
        }

        internal static void MarkPostgreSqlComponentSchemaReady(
            ReignDbConnection connection, string markerKey, int revision)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return;
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrWhiteSpace(campaignId))
                return;
            PostgreSqlComponentSchemaFastPaths[campaignId + "|"
                + markerKey + "|"
                + revision.ToString(CultureInfo.InvariantCulture)] = 0;
        }

        internal static void InvalidateCampaignSchemaCaches(
            string campaignId)
        {
            if (string.IsNullOrWhiteSpace(campaignId))
                return;
            string schemaKey =
                ReignPostgreSqlStorage.CampaignSchemaName(campaignId);
            lock (CampaignSchemaLock)
                CampaignSchemaReady.Remove(schemaKey);
            string prefix = campaignId + "|";
            foreach (string key in PostgreSqlComponentSchemaFastPaths.Keys
                .Where(value => value.StartsWith(prefix,
                    StringComparison.OrdinalIgnoreCase)))
                PostgreSqlComponentSchemaFastPaths.TryRemove(key,
                    out byte ignored);
            RelationshipsService.InvalidateRelationshipPairStateCache(campaignId);
            RelationshipsService.InvalidateRelationshipPermanentMbtiCache(campaignId);
        }

        private static bool IsMemorySchemaCurrent(ReignDbConnection connection)
        {
            try
            {
                Dictionary<string, object> version = QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='version' LIMIT 1;").FirstOrDefault();
                if (!string.Equals(ReadString(version, "value", ""), "5", StringComparison.Ordinal))
                    return false;

                int requiredTables = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM information_schema.tables
WHERE table_schema=current_schema() AND table_name IN (
'group_conversation_state',
'group_conversation_contributions',
'temporal_knowledge_assertions',
'memory_background_jobs',
'dynamic_characteristics',
'rebellion_movements',
'rebellion_memberships',
'rebellion_transitions',
'negotiated_action_drafts',
'negotiated_action_approvals');").FirstOrDefault(), "count", 0);
                bool compactTurnPayloads = string.Equals(ReadString(QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='conversation_turn_payload_v2' LIMIT 1;")
                    .FirstOrDefault(), "value", ""), "complete", StringComparison.OrdinalIgnoreCase);
                bool precision = string.Equals(ReadString(QuerySql(connection,
                    "SELECT value FROM schema_meta WHERE key='memory_precision_v1';").FirstOrDefault(), "value", ""), "complete", StringComparison.Ordinal);
                return requiredTables == 10 && compactTurnPayloads && precision;
            }
            catch
            {
                return false;
            }
        }

        internal static void EnsureMemorySchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS schema_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS events (
    event_id TEXT PRIMARY KEY,
    campaign_id TEXT NOT NULL,
    ts INTEGER NOT NULL,
    world_day REAL NOT NULL DEFAULT 0,
    event_type TEXT NOT NULL,
    location_id TEXT NOT NULL DEFAULT '',
    summary TEXT NOT NULL DEFAULT '',
    participants_json TEXT NOT NULL DEFAULT '[]',
    witnesses_json TEXT NOT NULL DEFAULT '[]',
    about_entities_json TEXT NOT NULL DEFAULT '[]',
    known_by_json TEXT NOT NULL DEFAULT '[]',
    heard_as_rumor_by_json TEXT NOT NULL DEFAULT '[]',
    hidden_from_json TEXT NOT NULL DEFAULT '[]',
    visibility TEXT NOT NULL DEFAULT 'public',
    importance REAL NOT NULL DEFAULT 0.5,
    source_reliability REAL NOT NULL DEFAULT 1.0,
    confidence REAL NOT NULL DEFAULT 1.0,
    payload_json TEXT NOT NULL DEFAULT '{}',
    vector_id TEXT NOT NULL DEFAULT '',
    embedding_status TEXT NOT NULL DEFAULT 'not_indexed',
    created_utc TEXT NOT NULL
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memories (
    memory_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL DEFAULT '',
    owner_id TEXT NOT NULL DEFAULT '',
    memory_type TEXT NOT NULL DEFAULT 'event',
    ts INTEGER NOT NULL,
    world_day REAL NOT NULL DEFAULT 0,
    location_id TEXT NOT NULL DEFAULT '',
    summary TEXT NOT NULL DEFAULT '',
    participants_json TEXT NOT NULL DEFAULT '[]',
    witnesses_json TEXT NOT NULL DEFAULT '[]',
    about_entities_json TEXT NOT NULL DEFAULT '[]',
    known_by_json TEXT NOT NULL DEFAULT '[]',
    heard_as_rumor_by_json TEXT NOT NULL DEFAULT '[]',
    hidden_from_json TEXT NOT NULL DEFAULT '[]',
    visibility TEXT NOT NULL DEFAULT 'private',
    importance REAL NOT NULL DEFAULT 0.5,
    emotional_weight REAL NOT NULL DEFAULT 0,
    confidence REAL NOT NULL DEFAULT 1.0,
    tags_json TEXT NOT NULL DEFAULT '[]',
    status TEXT NOT NULL DEFAULT 'active',
    source TEXT NOT NULL DEFAULT '',
    vector_id TEXT NOT NULL DEFAULT '',
    embedding_status TEXT NOT NULL DEFAULT 'not_indexed',
    payload_json TEXT NOT NULL DEFAULT '{}'
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS beliefs (
    belief_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL DEFAULT '',
    believer_id TEXT NOT NULL DEFAULT '',
    claim TEXT NOT NULL DEFAULT '',
    confidence REAL NOT NULL DEFAULT 0.5,
    source TEXT NOT NULL DEFAULT '',
    known_by_json TEXT NOT NULL DEFAULT '[]',
    about_entities_json TEXT NOT NULL DEFAULT '[]',
    hidden_from_json TEXT NOT NULL DEFAULT '[]',
    visibility TEXT NOT NULL DEFAULT 'private',
    ts INTEGER NOT NULL,
    payload_json TEXT NOT NULL DEFAULT '{}'
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS obligations (
    obligation_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL DEFAULT '',
    owed_by TEXT NOT NULL DEFAULT '',
    owed_to TEXT NOT NULL DEFAULT '',
    description TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'unresolved',
    created_day REAL NOT NULL DEFAULT 0,
    importance REAL NOT NULL DEFAULT 0.5,
    participants_json TEXT NOT NULL DEFAULT '[]',
    about_entities_json TEXT NOT NULL DEFAULT '[]',
    known_by_json TEXT NOT NULL DEFAULT '[]',
    hidden_from_json TEXT NOT NULL DEFAULT '[]',
    visibility TEXT NOT NULL DEFAULT 'private',
    ts INTEGER NOT NULL,
    payload_json TEXT NOT NULL DEFAULT '{}'
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS summaries (
    summary_id TEXT PRIMARY KEY,
    scope TEXT NOT NULL DEFAULT '',
    owner_id TEXT NOT NULL DEFAULT '',
    summary_type TEXT NOT NULL DEFAULT 'middle_term',
    summary TEXT NOT NULL DEFAULT '',
    source_events_json TEXT NOT NULL DEFAULT '[]',
    start_ts INTEGER NOT NULL DEFAULT 0,
    end_ts INTEGER NOT NULL DEFAULT 0,
    event_count INTEGER NOT NULL DEFAULT 0,
    location_id TEXT NOT NULL DEFAULT '',
    participants_json TEXT NOT NULL DEFAULT '[]',
    about_entities_json TEXT NOT NULL DEFAULT '[]',
    known_by_json TEXT NOT NULL DEFAULT '[]',
    hidden_from_json TEXT NOT NULL DEFAULT '[]',
    visibility TEXT NOT NULL DEFAULT 'private',
    importance REAL NOT NULL DEFAULT 0.5,
    confidence REAL NOT NULL DEFAULT 0.75,
    tags_json TEXT NOT NULL DEFAULT '[]',
    status TEXT NOT NULL DEFAULT 'active',
    source TEXT NOT NULL DEFAULT '',
    vector_id TEXT NOT NULL DEFAULT '',
    embedding_status TEXT NOT NULL DEFAULT 'not_indexed',
    updated_ts INTEGER NOT NULL,
    payload_json TEXT NOT NULL DEFAULT '{}'
);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS comprehension (
    comprehension_id TEXT PRIMARY KEY,
    event_id TEXT NOT NULL DEFAULT '',
    owner_id TEXT NOT NULL DEFAULT '',
    text TEXT NOT NULL DEFAULT '',
    stance TEXT NOT NULL DEFAULT '',
    confidence REAL NOT NULL DEFAULT 0.5,
    known_by_json TEXT NOT NULL DEFAULT '[]',
    about_entities_json TEXT NOT NULL DEFAULT '[]',
    hidden_from_json TEXT NOT NULL DEFAULT '[]',
    ts INTEGER NOT NULL,
    payload_json TEXT NOT NULL DEFAULT '{}'
);");
            ExecuteSql(connection, @"CREATE VIRTUAL TABLE IF NOT EXISTS memory_fts USING fts5(memory_id UNINDEXED, text, tags, entities);");
            ExecuteSql(connection, @"CREATE VIRTUAL TABLE IF NOT EXISTS event_fts USING fts5(event_id UNINDEXED, text, tags, entities);");
            EnsureDatabaseColumn(connection, "summaries", "summary_type", "TEXT NOT NULL DEFAULT 'middle_term'");
            EnsureDatabaseColumn(connection, "summaries", "start_ts", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "summaries", "end_ts", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "summaries", "event_count", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "summaries", "location_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "summaries", "participants_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureDatabaseColumn(connection, "summaries", "about_entities_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureDatabaseColumn(connection, "summaries", "known_by_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureDatabaseColumn(connection, "summaries", "hidden_from_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureDatabaseColumn(connection, "summaries", "visibility", "TEXT NOT NULL DEFAULT 'private'");
            EnsureDatabaseColumn(connection, "summaries", "importance", "REAL NOT NULL DEFAULT 0.5");
            EnsureDatabaseColumn(connection, "summaries", "confidence", "REAL NOT NULL DEFAULT 0.75");
            EnsureDatabaseColumn(connection, "summaries", "tags_json", "TEXT NOT NULL DEFAULT '[]'");
            EnsureDatabaseColumn(connection, "summaries", "status", "TEXT NOT NULL DEFAULT 'active'");
            EnsureDatabaseColumn(connection, "summaries", "source", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "summaries", "vector_id", "TEXT NOT NULL DEFAULT ''");
            EnsureDatabaseColumn(connection, "summaries", "embedding_status", "TEXT NOT NULL DEFAULT 'not_indexed'");
            ExecuteSql(connection, @"CREATE VIRTUAL TABLE IF NOT EXISTS summary_fts USING fts5(summary_id UNINDEXED, text, tags, entities);");
            RelationshipsService.EnsureRelationshipAndCorrespondenceSchema(connection);
            ReputationService.EnsureSocialReputationSchema(connection);
            CharactersService.EnsureCharacterEditorSchema(connection);
            CharactersService.EnsureDynamicCharacteristicsSchema(connection);
            CharactersService.EnsureIdentitySchema(connection);
            DialogueService.EnsureTemporalKnowledgeGraphSchema(connection);
            DialogueService.EnsureMemoryBackgroundWorkSchema(connection);
            DialogueService.EnsureCategorizedMemorySchema(connection);
            DialogueService.EnsureMemoryPrecisionSchema(connection);
            DialogueService.EnsureCharacterCommitmentTimeline(connection);
            DialogueService.EnsureLatestAcceptedCommitments(connection);
            DialogueService.EnsureGroupConversationStateSchema(connection);
            // Rebellion storage is campaign infrastructure. World Test and
            // Save Sync can inspect it before the first weekly evaluation, so
            // it must not depend on a rebellion endpoint having run first.
            RebellionService.EnsureRebellionSchema(connection);
#if !REIGN_EXCLUDE_COURT
            CourtService.EnsureCourtSchema(connection);
#endif
            DialogueService.EnsurePrecisionProjectionTracking(connection);
            ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key, value) VALUES('version', '5');");
        }

        internal static void EnsureDatabaseColumn(ReignDbConnection connection, string table, string column, string definition)
        {
            if (!Regex.IsMatch(table ?? string.Empty, @"^[A-Za-z_][A-Za-z0-9_]*$")
                || !Regex.IsMatch(column ?? string.Empty, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            {
                throw new InvalidOperationException("Unsafe database schema identifier.");
            }

            List<Dictionary<string, object>> columns = QuerySql(
                connection,
                @"SELECT column_name AS name
FROM information_schema.columns
WHERE table_schema=current_schema()
  AND table_name=$table
  AND column_name=$column;",
                new Dictionary<string, object>
                {
                    ["table"] = table,
                    ["column"] = column
                });
            if (columns.Count > 0)
            {
                return;
            }

            try
            {
                ExecuteSql(connection, "ALTER TABLE " + table + " ADD COLUMN " + column + " " + definition + ";");
            }
            catch (Exception ex) when (
                ex.Message.IndexOf("duplicate column", StringComparison.OrdinalIgnoreCase) >= 0
                || ex.Message.IndexOf("already exists", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Another writer completed the same idempotent schema migration
                // after the information-schema check.
            }
        }

        internal static void ExecuteSql(
            ReignDbConnection connection,
            string sql,
            Dictionary<string, object> parameters = null,
            ReignDbTransaction transaction = null)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            bool serializePostgreSqlSchemaMutation =
                ReignPostgreSqlDialect.IsPostgreSql(connection)
                && IsPostgreSqlSchemaMutation(sql);
            for (int attempt = 0; ; attempt++)
            {
                ReignDbTransaction ownedSchemaTransaction = null;
                try
                {
                    ReignDbTransaction commandTransaction = transaction;
                    if (serializePostgreSqlSchemaMutation)
                    {
                        if (commandTransaction == null)
                        {
                            try
                            {
                                ownedSchemaTransaction =
                                    connection.BeginTransaction();
                                commandTransaction =
                                    ownedSchemaTransaction;
                            }
                            catch (InvalidOperationException ex) when (
                                ex.Message.IndexOf(
                                    "transaction",
                                    StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                // Legacy callers can start a transaction with
                                // ExecuteSql("BEGIN;"). In that case the
                                // command automatically enlists in the active
                                // PostgreSQL transaction.
                            }
                        }
                        AcquirePostgreSqlTransactionMutationLock(
                            connection, commandTransaction);
                    }
                    string normalizedSql =
                        ReignPostgreSqlDialect.Normalize(connection, sql);
                    using (ReignDbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = normalizedSql;
                        command.CommandTimeout = 60;
                        // Leaving Transaction untouched preserves implicit enlistment
                        // for callers that begin a local
                        // transaction around shared helpers. Assign only when a caller
                        // passes an explicit transaction object.
                        if (commandTransaction != null)
                            command.Transaction = commandTransaction;
                        AddSqlParameters(command, parameters);
                        command.ExecuteNonQuery();
                    }
                    RelationshipsService.InvalidateRelationshipPairCacheForMutation(
                        connection, sql, parameters);
                    if (ownedSchemaTransaction != null)
                        ownedSchemaTransaction.Commit();
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection)
                        && normalizedSql.StartsWith(
                            "BEGIN;", StringComparison.OrdinalIgnoreCase))
                    {
                        AcquirePostgreSqlTransactionMutationLock(connection);
                    }
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                    {
                        string campaignId =
                            ReignPostgreSqlStorage.CampaignIdForConnection(connection);
                        if (!string.IsNullOrWhiteSpace(campaignId))
                        {
                            try { PersistenceService.MarkSaveSyncActiveStateDirty(campaignId); }
                            catch { }
                        }
                    }
                    return;
                }
                catch (Exception ex) when (!SqlStatementRetriesSuppressed(connection) && IsStalePostgreSqlPreparedPlan(ex)
                    && attempt < 2)
                {
                    ResetPostgreSqlPreparedPlans(connection);
                }
                catch (Exception ex) when (!SqlStatementRetriesSuppressed(connection) && IsTransientDatabaseContention(ex) && attempt < 240)
                {
                    SqlContentionRetryObserverForTests?.Invoke(ex);
                    Thread.Sleep(Math.Min(250, 10 + (attempt * 5)));
                }
                finally
                {
                    ownedSchemaTransaction?.Dispose();
                }
            }
        }

        [ThreadStatic] private static SqlReadTimingContext CurrentSqlReadTiming;

        internal sealed class SqlReadTimingContext : IDisposable
        {
            private readonly SqlReadTimingContext previous;
            public long SetupTicks, ExecuteTicks, MaterializeTicks, Rows;
            public int Queries;
            private readonly Dictionary<string, Dictionary<string, object>> queryProfiles =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            public readonly bool CaptureQueryProfiles = HasArg(Environment.GetCommandLineArgs(), "--run-verification") && ActiveServerPort <= 0;
            public List<Dictionary<string, object>> QueryProfiles => queryProfiles.Values.ToList();
            public double SetupMs => SetupTicks * 1000d / Stopwatch.Frequency;
            public double ExecuteMs => ExecuteTicks * 1000d / Stopwatch.Frequency;
            public double MaterializeMs => MaterializeTicks * 1000d / Stopwatch.Frequency;
            public SqlReadTimingContext() { previous = CurrentSqlReadTiming; CurrentSqlReadTiming = this; }
            public void RecordQuery(string sql, long setup, long execute, long materialize, int rows)
            {
                if (!CaptureQueryProfiles) return;
                // Only statement identity/table names, never parameter values or raw SQL.
                string hash = Sha256Hex(sql);
                if (!queryProfiles.TryGetValue(hash, out var profile))
                {
                    string tables = string.Join(",", System.Text.RegularExpressions.Regex.Matches(sql,
                        @"\b(?:FROM|JOIN)\s+([A-Za-z_][A-Za-z0-9_]*)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                        .Cast<System.Text.RegularExpressions.Match>().Select(match => match.Groups[1].Value)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Take(6));
                    queryProfiles[hash] = profile = new Dictionary<string, object>
                    { ["queryHash"] = hash, ["tables"] = tables, ["calls"] = 0, ["rows"] = 0L,
                        ["setupMs"] = 0d, ["executeMs"] = 0d, ["materializeMs"] = 0d };
                }
                profile["calls"] = ReadInt(profile, "calls", 0) + 1;
                profile["rows"] = ReadLong(profile, "rows", 0) + rows;
                profile["setupMs"] = ReadDouble(profile, "setupMs", 0) + setup * 1000d / Stopwatch.Frequency;
                profile["executeMs"] = ReadDouble(profile, "executeMs", 0) + execute * 1000d / Stopwatch.Frequency;
                profile["materializeMs"] = ReadDouble(profile, "materializeMs", 0) + materialize * 1000d / Stopwatch.Frequency;
            }
            public void Dispose() { CurrentSqlReadTiming = previous; }
        }

        [ThreadStatic] internal static SqlStatementRetrySuppression CurrentSqlRetrySuppression;
        [ThreadStatic] internal static Action<Exception> SqlContentionRetryObserverForTests;
        internal sealed class SqlStatementRetrySuppression : IDisposable
        {
            internal readonly ReignDbConnection Connection;
            internal readonly SqlStatementRetrySuppression Previous;
            private bool disposed;
            internal SqlStatementRetrySuppression(ReignDbConnection connection)
            {
                Connection=connection; Previous=CurrentSqlRetrySuppression;
                CurrentSqlRetrySuppression=this;
            }
            public void Dispose()
            {
                if(disposed)return;
                CurrentSqlRetrySuppression=Previous; disposed=true;
            }
        }
        internal static bool SqlStatementRetriesSuppressed(ReignDbConnection connection)
        {
            for(var scope=CurrentSqlRetrySuppression;scope!=null;scope=scope.Previous)
                if(ReferenceEquals(scope.Connection,connection))return true;
            return false;
        }

        internal static List<Dictionary<string, object>> QuerySql(
            ReignDbConnection connection,
            string sql,
            Dictionary<string, object> parameters = null,
            ReignDbTransaction transaction = null)
        {
            PersistenceService.ThrowIfCampaignRequestReplaced();
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                    SqlReadTimingContext timing = CurrentSqlReadTiming;
                    long setupStarted = timing == null ? 0 : Stopwatch.GetTimestamp();
                    using (ReignDbCommand command = connection.CreateCommand())
                    {
                        command.CommandText = ReignPostgreSqlDialect.Normalize(connection, sql);
                        command.CommandTimeout = 60;
                        if (transaction != null) command.Transaction = transaction;
                        AddSqlParameters(command, parameters);
                        long executeStarted = timing == null ? 0 : Stopwatch.GetTimestamp();
                        using (ReignDbDataReader reader = command.ExecuteReader())
                        {
                            long materializeStarted = timing == null ? 0 : Stopwatch.GetTimestamp();
                            int fieldCount = reader.FieldCount;
                            string[] columnNames = new string[fieldCount];
                            for (int index = 0; index < fieldCount; index++) columnNames[index] = reader.GetName(index);
                            while (reader.Read())
                            {
                                Dictionary<string, object> row = new Dictionary<string, object>(fieldCount, StringComparer.OrdinalIgnoreCase);
                                for (int index = 0; index < fieldCount; index++)
                                {
                                    row[columnNames[index]] = reader.IsDBNull(index) ? null : reader.GetValue(index);
                                }

                                rows.Add(row);
                            }
                            if (timing != null)
                            {
                                long materializeTicks = Stopwatch.GetTimestamp() - materializeStarted;
                                timing.SetupTicks += executeStarted - setupStarted;
                                timing.ExecuteTicks += materializeStarted - executeStarted;
                                timing.MaterializeTicks += materializeTicks;
                                timing.Queries++;
                                timing.Rows += rows.Count;
                                timing.RecordQuery(sql, executeStarted - setupStarted,
                                    materializeStarted - executeStarted, materializeTicks, rows.Count);
                            }
                        }
                    }
                    return rows;
                }
                catch (Exception ex) when (!SqlStatementRetriesSuppressed(connection) && IsStalePostgreSqlPreparedPlan(ex)
                    && attempt < 2)
                {
                    ResetPostgreSqlPreparedPlans(connection);
                }
                catch (Exception ex) when (!SqlStatementRetriesSuppressed(connection) && IsTransientDatabaseContention(ex) && attempt < 240)
                {
                    SqlContentionRetryObserverForTests?.Invoke(ex);
                    Thread.Sleep(Math.Min(250, 10 + (attempt * 5)));
                }
            }
        }
    }
}
