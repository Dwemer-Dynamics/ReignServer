using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

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
    }
}
