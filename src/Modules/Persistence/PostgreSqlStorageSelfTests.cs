using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunPostgreSqlStorageSelfTests()
        {
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add =
                (id, passed, summary) => results.Add(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["passed"] = passed,
                        ["suite"] = "postgresql_storage",
                        ["caseId"] = id,
                        ["name"] = id,
                        ["summary"] = summary,
                        ["durationMs"] = 0
                    });
            string token = Guid.NewGuid().ToString("N").Substring(0, 10);
            string campaignId = "__postgres_test_" + token;
            string pointId = "point_" + token;
            try
            {
                Dictionary<string, object> diagnostics =
                    ReignPostgreSqlStorage.Diagnostics();
                add("database_identity",
                    ReadString(diagnostics, "database", "")
                        == ReignPostgreSqlStorage.Options.Database
                    && ReadString(diagnostics, "encoding", "")
                        .Equals("UTF8", StringComparison.OrdinalIgnoreCase)
                    && ReadString(diagnostics, "ownerUser", "") == ReignPostgreSqlStorage.Options.Username
                    && ReadString(diagnostics, "databaseOwner", "") == ReignPostgreSqlStorage.Options.Username,
                    "Reign connects to the exact environment-authorized UTF-8 PostgreSQL database as its configured owner.");

                using (var connection = ReignPostgreSqlStorage.OpenMetadataConnection())
                {
                    int first = ReignPostgreSqlStorage.ApplyInfrastructureMigrations(connection);
                    int repeated = ReignPostgreSqlStorage.ApplyInfrastructureMigrations(connection);
                    add("database_version_update_idempotent",
                        first == ReignPostgreSqlStorage.RequiredDatabaseSchemaVersion && repeated == first,
                        "Repeated database update checks retain the required schema version.");
                }
                bool newerSchemaRejected = false;
                try { ReignPostgreSqlStorage.ValidateDatabaseSchemaVersion(ReignPostgreSqlStorage.RequiredDatabaseSchemaVersion + 1); }
                catch (InvalidOperationException) { newerSchemaRejected = true; }
                add("database_version_downgrade_guard", newerSchemaRejected,
                    "Older server code rejects a newer database schema before changing it.");

                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                {
                    ExecuteSql(connection,
                        @"CREATE TABLE IF NOT EXISTS postgresql_probe(
probe_id TEXT PRIMARY KEY,payload TEXT NOT NULL);");
                    ExecuteSql(connection,
                        @"INSERT OR REPLACE INTO postgresql_probe(
probe_id,payload) VALUES('state','before');");
                    ExecuteSql(connection,
                        "CREATE INDEX postgresql_probe_payload_a ON postgresql_probe(payload);");
                    ExecuteSql(connection,
                        "CREATE INDEX postgresql_probe_payload_b ON postgresql_probe(payload);");
                }
                int removedIndexes =
                    ReignPostgreSqlStorage.DeduplicateCampaignIndexes(
                        campaignId);
                int payloadIndexes;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                    payloadIndexes = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM pg_indexes
WHERE schemaname=current_schema() AND tablename='postgresql_probe'
AND indexdef LIKE '%(payload)%';").FirstOrDefault(), "count", 0);
                add("structural_index_deduplication",
                    removedIndexes == 1 && payloadIndexes == 1,
                    "Structurally equivalent PostgreSQL indexes collapse to one canonical physical index.");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(
                    campaignId, pointId);
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                    ExecuteSql(connection,
                        "UPDATE postgresql_probe SET payload='after' WHERE probe_id='state';");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(
                    campaignId, pointId);
                string restored;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                    restored = ReadString(QuerySql(connection,
                        "SELECT payload FROM postgresql_probe WHERE probe_id='state';")
                        .FirstOrDefault(), "payload", "");
                add("schema_snapshot_round_trip",
                    restored == "before"
                    && ReignPostgreSqlStorage.SnapshotExists(
                        campaignId, pointId),
                    "Campaign schema cloning and restoration preserve the exact stored state.");

                add("campaign_registry_authoritative",
                    ReignPostgreSqlStorage.CampaignExists(campaignId),
                    "Campaign discovery is backed by the PostgreSQL registry.");

                Dictionary<string, object> metadata =
                    ReignPostgreSqlStorage.ListCampaignMetadata().Single(row =>
                        ReadString(row, "campaignId", "") == campaignId);
                add("campaign_metadata_without_storage_scan",
                    ReadString(metadata, "schemaName", "") ==
                        ReignPostgreSqlStorage.CampaignSchemaName(campaignId)
                    && ReadLong(metadata, "snapshotCount", 0) == 1
                    && !metadata.ContainsKey("databaseBytes")
                    && !string.IsNullOrWhiteSpace(ReadString(metadata, "updatedUtc", "")),
                    "Routine discovery preserves registry identity, order metadata and snapshot counts without requesting or fabricating database sizes.");

                for (int index = 0; index < 17; index++)
                    ReignPostgreSqlStorage.CloneCampaignToSnapshot(
                        campaignId, "drop_" + index.ToString(
                            CultureInfo.InvariantCulture) + "_" + token);
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                add("multi_snapshot_campaign_drop_is_bounded",
                    !ReignPostgreSqlStorage.CampaignExists(campaignId)
                    && ReignPostgreSqlStorage.ListSnapshots(campaignId).Count == 0,
                    "Campaign retirement drops many Save Sync schemas in bounded transactions and removes their metadata without exhausting a single PostgreSQL lock transaction.");
            }
            catch (Exception ex)
            {
                add("postgresql_storage_exception", false, ex.ToString());
            }
            finally
            {
                try
                {
                    ReignPostgreSqlStorage.DropCampaign(campaignId);
                }
                catch { }
                TryDeleteDirectory(CampaignDirectory(campaignId));
                TryDeleteDirectory(SaveSyncCampaignRoot(campaignId));
            }
            return results;
        }

        private static void RunCampaignMigrationSelfTests(
            Action<string, bool, string> add, string token)
        {
            string campaign = "__postgres_migration_" + token;
            string concurrent = "__postgres_migration_race_" + token;
            int latest = LatestCampaignMigrationVersion;
            bool workersJoined = true;
            int MigrationVersionOf(ReignDbConnection connection) =>
                ReadCampaignMigrationVersion(connection, null);
            bool ApproachTables(ReignDbConnection connection) => ReadInt(
                QuerySql(connection, @"SELECT COUNT(*) AS count FROM information_schema.tables
WHERE table_schema=current_schema()
AND table_name IN ('player_attention_events','player_approach_state');")
                    .FirstOrDefault(), "count", 0) == 2;
            // Simulates a schema saved before migration 2 (or before the runner when version is null).
            void Downgrade(ReignDbConnection connection, string version)
            {
                ExecuteSql(connection, "DROP TABLE IF EXISTS player_attention_events;");
                ExecuteSql(connection, "DROP TABLE IF EXISTS player_approach_state;");
                if (version == null)
                    ExecuteSql(connection, "DELETE FROM schema_meta WHERE key=$key;",
                        new Dictionary<string, object> { ["key"] = CampaignMigrationVersionKey });
                else
                    ExecuteSql(connection, "UPDATE schema_meta SET value=$value WHERE key=$key;",
                        new Dictionary<string, object> { ["key"] = CampaignMigrationVersionKey, ["value"] = version });
            }
            try
            {
                add("campaign_migration_list_ordered",
                    CampaignSchemaMigrations.Select((migration, index) => migration.Version == index + 1).All(ordered => ordered)
                    && CampaignSchemaMigrations[0].Apply == null,
                    "Campaign migrations are numbered 1..N without gaps and migration 1 is the core baseline.");

                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    add("campaign_migration_fresh_reaches_latest",
                        MigrationVersionOf(connection) == latest && ApproachTables(connection)
                        && IsCampaignMigrationReady(connection, PlayerApproachSchemaMigrationVersion),
                        "A fresh campaign schema records the latest migration version, has the migration-owned tables and enables the Ensure fast path.");

                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                {
                    var applied = new List<int>();
                    int repeated = ApplyCampaignSchemaMigrations(connection, applied);
                    add("campaign_migration_repeat_noop",
                        repeated == latest && applied.Count == 0 && MigrationVersionOf(connection) == latest,
                        "Running the migration runner again on a current schema applies nothing.");
                    Downgrade(connection, "1");
                }
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    add("campaign_migration_warm_check_detects_older_schema",
                        MigrationVersionOf(connection) == latest && ApproachTables(connection),
                        "A schema replaced beneath the warm cache by an older version is detected and upgraded on the next open.");

                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    Downgrade(connection, "1");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "migration_v1_" + token);
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    Downgrade(connection, null);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "migration_v0_" + token);
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "INSERT INTO player_approach_state(timeline_id,state_key,value_json) VALUES('t','after_upgrade','{}');");

                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "migration_v1_" + token);
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    add("campaign_migration_restored_v1_snapshot_upgraded",
                        MigrationVersionOf(connection) == latest && ApproachTables(connection)
                        && QuerySql(connection, "SELECT * FROM player_approach_state;").Count == 0,
                        "Restoring a Save Sync snapshot taken at migration version 1 brings it to the latest version on the next open.");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "migration_v0_" + token);
                using (ReignDbConnection connection = OpenCampaignConnection(campaign))
                    add("campaign_migration_restored_pre_runner_snapshot_upgraded",
                        MigrationVersionOf(connection) == latest && ApproachTables(connection),
                        "Restoring a snapshot saved before the runner existed brings it to the latest version on the next open.");

                // Concurrent first opens through the production path.
                Exception openError = null;
                int opened = 0;
                using (var start = new Barrier(2))
                {
                    var openers = Enumerable.Range(0, 2).Select(_ => new Thread(() =>
                    {
                        try
                        {
                            start.SignalAndWait(10000);
                            using ReignDbConnection connection = OpenCampaignConnection(concurrent);
                            if (MigrationVersionOf(connection) == latest && ApproachTables(connection))
                                Interlocked.Increment(ref opened);
                        }
                        catch (Exception ex) { openError = ex; }
                    }) { IsBackground = true }).ToList();
                    openers.ForEach(thread => thread.Start());
                    foreach (Thread thread in openers) workersJoined &= thread.Join(60000);
                }
                add("campaign_migration_concurrent_first_open",
                    workersJoined && opened == 2 && openError == null,
                    "Concurrent first opens of a new campaign both see the latest migration version. " + openError);
                if (!workersJoined) return;

                // Concurrent runners on separate connections (as separate processes would) apply each migration once.
                using (ReignDbConnection connection = OpenCampaignConnection(concurrent))
                    Downgrade(connection, "1");
                InvalidateCampaignSchemaCaches(concurrent);
                var appliedByRunner = new[] { new List<int>(), new List<int>() };
                Exception runnerError = null;
                using (var start = new Barrier(2))
                {
                    var runners = Enumerable.Range(0, 2).Select(index => new Thread(() =>
                    {
                        try
                        {
                            using ReignDbConnection connection =
                                ReignPostgreSqlStorage.OpenCampaignConnection(concurrent);
                            start.SignalAndWait(10000);
                            ApplyCampaignSchemaMigrations(connection, appliedByRunner[index]);
                        }
                        catch (Exception ex) { runnerError = ex; }
                    }) { IsBackground = true }).ToList();
                    runners.ForEach(thread => thread.Start());
                    foreach (Thread thread in runners) workersJoined &= thread.Join(60000);
                }
                if (!workersJoined) return;
                List<int> appliedTotal = appliedByRunner.SelectMany(list => list).OrderBy(value => value).ToList();
                bool raceUpgraded;
                using (ReignDbConnection connection = OpenCampaignConnection(concurrent))
                    raceUpgraded = MigrationVersionOf(connection) == latest && ApproachTables(connection);
                add("campaign_migration_concurrent_runners_apply_once",
                    runnerError == null && raceUpgraded
                    && appliedTotal.SequenceEqual(Enumerable.Range(2, latest - 1)),
                    "Two concurrent runners on separate connections apply each pending migration exactly once. applied="
                    + string.Join(",", appliedTotal) + " " + runnerError);
            }
            catch (Exception ex)
            {
                add("campaign_migration_exception", false, ex.ToString());
            }
            finally
            {
                // Never drop a schema that a timed-out worker may still use.
                if (workersJoined)
                {
                    foreach (string id in new[] { campaign, concurrent })
                    {
                        try { ReignPostgreSqlStorage.DropCampaign(id); } catch { }
                        TryDeleteDirectory(CampaignDirectory(id));
                        TryDeleteDirectory(SaveSyncCampaignRoot(id));
                    }
                }
                else
                {
                    add("campaign_migration_workers_joined", false, "A migration test worker did not finish; its schemas were left in place.");
                }
            }
        }
    }
}
