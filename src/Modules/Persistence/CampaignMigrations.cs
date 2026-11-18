using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    // Per-campaign versioned schema migrations (docs/server/CampaignMigrations.md).
    // The version lives in the campaign schema's own schema_meta, so Save Sync
    // snapshots and imported archives carry it and are upgraded when reopened.
    internal static partial class PersistenceService
    {
        internal const string CampaignMigrationVersionKey = "campaign_migration_version";
        internal const int PlayerApproachSchemaMigrationVersion = 2;

        private sealed class CampaignSchemaMigration
        {
            internal CampaignSchemaMigration(int version, string name,
                Action<ReignDbConnection> apply)
            {
                Version = version;
                Name = name;
                Apply = apply;
            }

            internal int Version { get; }
            internal string Name { get; }
            internal Action<ReignDbConnection> Apply { get; }
        }

        // Ordered and append-only: never renumber, reorder or edit a shipped entry.
        private static readonly CampaignSchemaMigration[] CampaignSchemaMigrations =
        {
            // The core baseline. ApplyCampaignBaselineSchema applies its DDL on
            // every cold open (it also reconciles the semantic provider
            // generation), so this entry only records the version.
            new CampaignSchemaMigration(1, "core_baseline", null),
            new CampaignSchemaMigration(PlayerApproachSchemaMigrationVersion,
                "player_approach_tables", RelationshipsService.ApplyPlayerApproachSchemaDdl),
        };

        internal static int LatestCampaignMigrationVersion =>
            CampaignSchemaMigrations[CampaignSchemaMigrations.Length - 1].Version;

        // Migration 1: the core campaign schema created on the first open.
        // Every step is idempotent and re-run on each cold open, exactly as
        // before versioned migrations existed. Do not add new DDL here; append
        // a numbered migration instead.
        internal static void ApplyCampaignBaselineSchema(ReignDbConnection connection,
            string campaignId, string providerGeneration)
        {
            Program.EnsureMemorySchema(connection);
            Program.CampaignSchemaAfterMemoryForTests?.Invoke();
            DialogueService.EnsureCategorizedMemorySchema(connection);
            WorldSimulationService.EnsureWorldHistorySchema(connection);
            DialogueService.EnsureSemanticMemorySchema(connection, providerGeneration);
            DialogueService.RunCategorizedMemoryMigration(connection, campaignId);
            // Repair legacy scalar memories once, outside warm reads.
            Program.ExecuteSql(connection, @"DELETE FROM memories
WHERE source='dialogue_llm'
  AND lower(trim(summary)) IN ('0','1','true','false','null','undefined');");
        }

        // Applies every numbered migration above the schema's recorded version.
        // Requires the baseline (schema_meta) to exist. Each migration runs in
        // its own transaction under the campaign schema mutation advisory lock
        // and re-reads the version after taking it, so concurrent openers
        // (threads or processes) apply each migration exactly once. A schema
        // recorded at a newer version than this server knows is left untouched.
        // Returns the schema's resulting version.
        internal static int ApplyCampaignSchemaMigrations(ReignDbConnection connection,
            List<int> applied = null)
        {
            int current = ReadCampaignMigrationVersion(connection, null);
            if (current >= LatestCampaignMigrationVersion)
                return current;
            foreach (CampaignSchemaMigration migration in CampaignSchemaMigrations)
            {
                if (migration.Version <= current)
                    continue;
                if (ApplyCampaignSchemaMigration(connection, migration))
                    applied?.Add(migration.Version);
            }
            return Math.Max(current, LatestCampaignMigrationVersion);
        }

        private static bool ApplyCampaignSchemaMigration(ReignDbConnection connection,
            CampaignSchemaMigration migration)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    // Statements must not retry individually inside the
                    // migration transaction; the whole migration retries.
                    using (new Program.SqlStatementRetrySuppression(connection))
                    using (ReignDbTransaction transaction = connection.BeginTransaction())
                    {
                        AcquirePostgreSqlTransactionMutationLock(connection, transaction);
                        if (ReadCampaignMigrationVersion(connection, transaction) >= migration.Version)
                        {
                            transaction.Rollback();
                            return false;
                        }
                        migration.Apply?.Invoke(connection);
                        Program.ExecuteSql(connection,
                            "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$value);",
                            new Dictionary<string, object>
                            {
                                ["key"] = CampaignMigrationVersionKey,
                                ["value"] = migration.Version.ToString(CultureInfo.InvariantCulture)
                            }, transaction);
                        transaction.Commit();
                    }
                    Program.LogOperational("campaign.schema_migration_applied", new Dictionary<string, object>
                    {
                        ["campaignId"] = ReignPostgreSqlStorage.CampaignIdForConnection(connection),
                        ["version"] = migration.Version,
                        ["name"] = migration.Name
                    });
                    return true;
                }
                catch (Exception ex) when (IsTransientDatabaseContention(ex) && attempt < 4)
                {
                    Thread.Sleep(25 * (attempt + 1));
                }
            }
        }

        private static int ReadCampaignMigrationVersion(ReignDbConnection connection,
            ReignDbTransaction transaction)
        {
            string value = ReadString(Program.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;",
                new Dictionary<string, object> { ["key"] = CampaignMigrationVersionKey },
                transaction).FirstOrDefault(), "value", "");
            return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int version) ? version : 0;
        }

        // Records the in-memory fast path that lets migration-owned Ensure*
        // methods skip their DDL. InvalidateCampaignSchemaCaches clears it.
        internal static void MarkCampaignMigrationsReady(ReignDbConnection connection,
            int version)
        {
            string campaignId = ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrWhiteSpace(campaignId))
                return;
            int ready = Math.Min(version, LatestCampaignMigrationVersion);
            for (int value = 1; value <= ready; value++)
                Program.PostgreSqlComponentSchemaFastPaths[CampaignMigrationFastPathKey(campaignId, value)] = 0;
        }

        internal static bool IsCampaignMigrationReady(ReignDbConnection connection, int version)
        {
            string campaignId = ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            return !string.IsNullOrWhiteSpace(campaignId)
                && Program.PostgreSqlComponentSchemaFastPaths.ContainsKey(
                    CampaignMigrationFastPathKey(campaignId, version));
        }

        private static string CampaignMigrationFastPathKey(string campaignId, int version) =>
            campaignId + "|" + CampaignMigrationVersionKey + "|"
            + version.ToString(CultureInfo.InvariantCulture);
    }
}
