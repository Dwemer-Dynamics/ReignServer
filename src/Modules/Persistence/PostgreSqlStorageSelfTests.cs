using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

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
            RunCampaignCleanupScopeSelfTests(add);
            return results;
        }

        private static HashSet<string> RegisteredCampaignIdsForSelfTest()
        {
            return new HashSet<string>(
                ReignPostgreSqlStorage.ListCampaignMetadata()
                    .Select(row => ReadString(row, "campaignId", "")),
                StringComparer.OrdinalIgnoreCase);
        }

        // Cleanup must remove only the campaigns its own run or process owns: a verification run's
        // sweep keeps campaigns other processes or live requests created during the run, and startup
        // cleanup keeps internal campaigns another live process still owns.
        private static void RunCampaignCleanupScopeSelfTests(Action<string, bool, string> add)
        {
            string token = Guid.NewGuid().ToString("N").Substring(0, 10);
            string runInternal = "__sweep_run_" + token;
            string runReal = "sweep_run_" + token;
            string liveReal = "sweep_live_" + token;
            string backgroundInternal = "__sweep_background_" + token;
            string foreignInternal = "__sweep_foreign_" + token;
            try
            {
                HashSet<string> preexisting = RegisteredCampaignIdsForSelfTest();
                CampaignCreationScope scope =
                    ReignPostgreSqlStorage.BeginCampaignCreationScope();
                try
                {
                    using (ReignPostgreSqlStorage.OpenCampaignConnection(runInternal)) { }
                    using (ReignPostgreSqlStorage.OpenCampaignConnection(runReal)) { }
                    // Work in this process that does not carry the run's context: a live
                    // game request (real campaign) and background work (internal campaign).
                    using (ExecutionContext.SuppressFlow())
                    {
                        Task.Run(() =>
                        {
                            using (ReignPostgreSqlStorage.OpenCampaignConnection(liveReal)) { }
                            using (ReignPostgreSqlStorage.OpenCampaignConnection(backgroundInternal)) { }
                        }).Wait();
                    }
                    // Another server process registers its own fixture campaign meanwhile.
                    string foreignSchema =
                        ReignPostgreSqlStorage.CampaignSchemaName(foreignInternal);
                    using (NpgsqlConnection other =
                        ReignPostgreSqlStorage.OpenCampaignOwnerSessionConnection())
                    {
                        using (NpgsqlCommand command = new NpgsqlCommand(
                            "CREATE SCHEMA IF NOT EXISTS \"" + foreignSchema + "\";", other))
                            command.ExecuteNonQuery();
                        using (NpgsqlCommand command = new NpgsqlCommand(@"
INSERT INTO reign_meta.campaign_registry(campaign_id,schema_name,created_utc,updated_utc)
VALUES(@campaign_id,@schema_name,now(),now());", other))
                        {
                            command.Parameters.AddWithValue("campaign_id", foreignInternal);
                            command.Parameters.AddWithValue("schema_name", foreignSchema);
                            command.ExecuteNonQuery();
                        }
                    }
                }
                finally
                {
                    ReignPostgreSqlStorage.EndCampaignCreationScope(scope);
                }
                bool registeredDuringRun = RegisteredCampaignIdsForSelfTest().IsSupersetOf(
                    new[] { runInternal, runReal, liveReal, backgroundInternal, foreignInternal });
                // The former sweep dropped every campaign registered after the run began, which
                // includes the other process's internal campaign; it must not be attributed now.
                bool foreignWasFormerSweepTarget = registeredDuringRun
                    && !preexisting.Contains(foreignInternal);
                bool foreignUnattributed =
                    !scope.CreatedCampaignIds.Contains(foreignInternal, StringComparer.OrdinalIgnoreCase)
                    && !scope.UnattributedCampaignIds.Contains(foreignInternal, StringComparer.OrdinalIgnoreCase);
                List<string> leaked = DropVerificationRunCampaigns(scope, preexisting);
                HashSet<string> after = RegisteredCampaignIdsForSelfTest();
                add("verification_sweep_drops_only_run_campaigns",
                    registeredDuringRun
                    && foreignWasFormerSweepTarget
                    && foreignUnattributed
                    && leaked.Count == 0
                    && !after.Contains(runInternal)
                    && !after.Contains(runReal)
                    && !after.Contains(backgroundInternal)
                    && after.Contains(liveReal)
                    && after.Contains(foreignInternal),
                    "A verification run's cleanup removes every campaign the run created (including internal campaigns its background work created) and keeps real campaigns created outside the run and other processes' campaigns.");
            }
            catch (Exception ex)
            {
                add("verification_sweep_exception", false, ex.ToString());
            }
            finally
            {
                foreach (string id in new[] { runInternal, runReal, liveReal, backgroundInternal, foreignInternal })
                {
                    try { ReignPostgreSqlStorage.DropCampaign(id); }
                    catch { }
                }
            }

            // A long-lived worker started during an earlier run keeps that run's (ended) scope in
            // its execution context. Its later creations must reach the current run's sweep.
            string staleInternal = "__sweep_stale_" + token;
            using (ManualResetEventSlim proceed = new ManualResetEventSlim(false))
            {
                Thread worker = null;
                Exception workerError = null;
                try
                {
                    using (ExecutionContext.SuppressFlow())
                    {
                        Task.Run(() =>
                        {
                            CampaignCreationScope earlier =
                                ReignPostgreSqlStorage.BeginCampaignCreationScope();
                            worker = new Thread(() =>
                            {
                                try
                                {
                                    proceed.Wait(TimeSpan.FromSeconds(60));
                                    using (ReignPostgreSqlStorage.OpenCampaignConnection(staleInternal)) { }
                                }
                                catch (Exception error) { workerError = error; }
                            }) { IsBackground = true };
                            worker.Start();
                            ReignPostgreSqlStorage.EndCampaignCreationScope(earlier);
                        }).Wait();
                    }
                    HashSet<string> preexisting = RegisteredCampaignIdsForSelfTest();
                    CampaignCreationScope current =
                        ReignPostgreSqlStorage.BeginCampaignCreationScope();
                    bool finished;
                    try
                    {
                        proceed.Set();
                        finished = worker.Join(TimeSpan.FromSeconds(60));
                    }
                    finally
                    {
                        ReignPostgreSqlStorage.EndCampaignCreationScope(current);
                    }
                    bool unattributed = current.UnattributedCampaignIds.Contains(
                        staleInternal, StringComparer.OrdinalIgnoreCase);
                    List<string> leaked = DropVerificationRunCampaigns(current, preexisting);
                    add("verification_sweep_ignores_ended_scope_of_long_lived_worker",
                        finished
                        && workerError == null
                        && unattributed
                        && leaked.Count == 0
                        && !RegisteredCampaignIdsForSelfTest().Contains(staleInternal),
                        "An internal campaign created by a worker that outlived an earlier run's scope is attributed to the active run and removed by its cleanup.");
                }
                catch (Exception ex)
                {
                    add("verification_sweep_stale_scope_exception", false, ex.ToString());
                }
                finally
                {
                    proceed.Set();
                    worker?.Join(TimeSpan.FromSeconds(60));
                    try { ReignPostgreSqlStorage.DropCampaign(staleInternal); }
                    catch { }
                }
            }

            string liveOwned = "__owner_live_" + token;
            string orphaned = "__owner_orphan_" + token;
            NpgsqlConnection otherProcess = null;
            try
            {
                using (ReignPostgreSqlStorage.OpenCampaignConnection(liveOwned)) { }
                using (ReignPostgreSqlStorage.OpenCampaignConnection(orphaned)) { }
                bool claimed = ReignPostgreSqlStorage.OwnsInternalCampaign(liveOwned)
                    && ReignPostgreSqlStorage.OwnsInternalCampaign(orphaned);
                // Hand one campaign to another live session (as another server process would
                // hold it) and orphan the other (as if its process had exited).
                ReignPostgreSqlStorage.ReleaseInternalCampaignOwnership(liveOwned);
                ReignPostgreSqlStorage.ReleaseInternalCampaignOwnership(orphaned);
                otherProcess = ReignPostgreSqlStorage.OpenCampaignOwnerSessionConnection();
                bool otherOwns = ReignPostgreSqlStorage.TryAcquireCampaignOwnerLock(
                    otherProcess, liveOwned);
                HashSet<string> kept =
                    ReignPostgreSqlStorage.DropInternalCampaignsWithoutLiveOwner(
                        new[] { liveOwned, orphaned });
                bool liveKept = kept.Contains(liveOwned)
                    && ReignPostgreSqlStorage.CampaignExists(liveOwned);
                bool orphanDropped = !kept.Contains(orphaned)
                    && !ReignPostgreSqlStorage.CampaignExists(orphaned);
                otherProcess.Dispose();
                otherProcess = null;
                HashSet<string> keptAfterExit =
                    ReignPostgreSqlStorage.DropInternalCampaignsWithoutLiveOwner(
                        new[] { liveOwned });
                bool droppedAfterExit = keptAfterExit.Count == 0
                    && !ReignPostgreSqlStorage.CampaignExists(liveOwned);
                add("startup_cleanup_keeps_live_owned_internal_campaigns",
                    claimed && otherOwns && liveKept && orphanDropped && droppedAfterExit,
                    "Startup cleanup keeps internal campaigns another live server process owns and removes orphans, including the same campaign once its owner has exited.");
            }
            catch (Exception ex)
            {
                add("startup_cleanup_ownership_exception", false, ex.ToString());
            }
            finally
            {
                otherProcess?.Dispose();
                foreach (string id in new[] { liveOwned, orphaned })
                {
                    try { ReignPostgreSqlStorage.DropCampaign(id); }
                    catch { }
                }
            }

            // Losing the owner session (for example a PostgreSQL restart) drops every lock; the
            // next claim reconnects and re-claims what the process still owns.
            string reclaimed = "__owner_reclaim_" + token;
            string trigger = "__owner_trigger_" + token;
            try
            {
                using (ReignPostgreSqlStorage.OpenCampaignConnection(reclaimed)) { }
                bool ownedBefore = ReignPostgreSqlStorage.OwnsInternalCampaign(reclaimed);
                ReignPostgreSqlStorage.BreakCampaignOwnerSessionForSelfTest();
                using (ReignPostgreSqlStorage.OpenCampaignConnection(trigger)) { }
                bool heldElsewhere;
                using (NpgsqlConnection probe =
                    ReignPostgreSqlStorage.OpenCampaignOwnerSessionConnection())
                {
                    heldElsewhere =
                        !ReignPostgreSqlStorage.TryAcquireCampaignOwnerLock(probe, reclaimed)
                        && !ReignPostgreSqlStorage.TryAcquireCampaignOwnerLock(probe, trigger);
                }
                add("internal_campaign_ownership_survives_session_loss",
                    ownedBefore
                    && heldElsewhere
                    && ReignPostgreSqlStorage.OwnsInternalCampaign(reclaimed)
                    && ReignPostgreSqlStorage.OwnsInternalCampaign(trigger),
                    "After the owner session is lost, the next claim reconnects and re-claims every internal campaign the process still owns.");
            }
            catch (Exception ex)
            {
                add("internal_campaign_ownership_session_exception", false, ex.ToString());
            }
            finally
            {
                foreach (string id in new[] { reclaimed, trigger })
                {
                    try { ReignPostgreSqlStorage.DropCampaign(id); }
                    catch { }
                }
            }
        }
    }
}
