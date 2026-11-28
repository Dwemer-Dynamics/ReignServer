using System;
using System.Data;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private static Timer LegacyPressureRepairTimer;
        private static int LegacyPressureRepairQueued;
        private static string LegacyPressureRepairAfterCampaign = "";

        internal static void StartLegacyPressureRepair()
        {
            var timer = new Timer(_ => QueueLegacyPressureRepair(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            if (Interlocked.CompareExchange(ref LegacyPressureRepairTimer, timer, null) != null)
            {
                timer.Dispose();
                return;
            }
            timer.Change(TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
        }

        internal static void StopLegacyPressureRepair() =>
            Interlocked.Exchange(ref LegacyPressureRepairTimer, null)?.Dispose();

        private static void QueueLegacyPressureRepair()
        {
            if (Program.ShutdownRequested || Interlocked.Exchange(ref LegacyPressureRepairQueued, 1) != 0) return;
            long epoch = Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration);
            OperationsService.TryEnqueuePriorityBackgroundWork("diplomacy.legacy_pressure_metadata", "maintenance", () =>
            {
                try
                {
                    if (!PressureRepairEpochCurrent(epoch) || OperationsService.PriorityBackgroundWorkShouldYield()) return;
                    string campaign = ReignPostgreSqlStorage.ReadNextPressureRepairCampaign(LegacyPressureRepairAfterCampaign);
                    LegacyPressureRepairAfterCampaign = campaign ?? "";
                    if (!CampaignFileService.IsInternalCampaignId(campaign))
                        RepairLegacyPressureMetadata(campaign, epoch);
                }
                catch (Exception ex)
                {
                    // No foreground request owns this repair or its retry.
                    OperationsService.LogOperational("diplomacy.legacy_pressure_repair_deferred",
                        TestDict("errorType", ex.GetType().Name));
                }
                finally { Interlocked.Exchange(ref LegacyPressureRepairQueued, 0); }
            }, _ => Interlocked.Exchange(ref LegacyPressureRepairQueued, 0), out _);
        }

        private static bool PressureRepairEpochCurrent(long epoch) =>
            !Program.ShutdownRequested && epoch == Interlocked.Read(ref PersistenceService.CampaignReplacementGeneration);

        private static (bool Committed, int Inserted) RepairLegacyPressureMetadata(string campaign, long epoch)
        {
            if (!PressureRepairEpochCurrent(epoch) || !Program.CampaignDataGate.TryEnterReadLock(0)) return default;
            try
            {
                if (!PressureRepairEpochCurrent(epoch)
                    || !WorldSimulationService.TryEnterWorldHistoryTimelineScope(campaign, out var timelineScope))
                    return default;
                using (timelineScope)
                {
                    using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaign,
                        trackCampaignMutations: true, initializeInfrastructure: false, boundedMaintenance: true);
                    if (connection == null) return default;
                    using var suppress = new ReignDatabase.SqlStatementRetrySuppression(connection);
                    using var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted);
                    using (var budget = connection.CreateCommand())
                    {
                        budget.Transaction = transaction;
                        budget.CommandTimeout = 2;
                        budget.CommandText = "SET LOCAL lock_timeout='150ms'; SET LOCAL statement_timeout='1000ms'; "
                            + "SET LOCAL idle_in_transaction_session_timeout='5000ms';";
                        budget.ExecuteNonQuery();
                    }
                    if (!TryEnterPressureMutation(connection, transaction)) return default;

                    var scope = TestDict("campaign", campaign);
                    var owner = DiplomacyStore.ReadPressureRepairOwner(connection, transaction, scope);
                    string timeline = ReadString(owner, "timeline_id", "");
                    string storage = ReadString(owner, "storage_witness", "");
                    if (string.IsNullOrEmpty(timeline) || string.IsNullOrEmpty(storage)
                        || owner["restore_generation"] is not string generation) return default;
                    scope["timeline"] = timeline;
                    scope["witness"] = storage;
                    scope["restore"] = generation;
                    var cursor = DiplomacyStore.ReadPressureRepairCursor(connection, transaction, scope);
                    if (ReadBool(cursor, "cooling_down", false)) return default;

                    bool restart = cursor == null || ReadBool(cursor, "completed", false);
                    int through = restart ? DiplomacyStore.ReadPressureRepairHighWater(connection, transaction, scope)
                        : ReadInt(cursor, "through_day", -1);
                    int after = restart ? -1 : ReadInt(cursor, "last_day", -1);
                    scope["after"] = after;
                    scope["through"] = through;
                    scope["chance"] = PoliticalPressureIncidentChance;
                    scope["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    var repaired = after < through
                        ? DiplomacyStore.RepairLegacyPoliticalPressureMetadataWindow(connection, transaction, scope)
                        : (LastDay: (int?)null, Inserted: 0);

                    // Confirm the same physical database, exact active timeline and restore.
                    var current = DiplomacyStore.ReadPressureRepairOwner(connection, transaction, scope);
                    if (!PressureRepairEpochCurrent(epoch) || ReadString(current, "timeline_id", "") != timeline
                        || ReadString(current, "storage_witness", "") != storage
                        || ReadString(current, "restore_generation", null) != generation) return default;

                    scope["last"] = repaired.LastDay ?? after;
                    scope["completed"] = !repaired.LastDay.HasValue || repaired.LastDay.Value == through;
                    DiplomacyStore.PublishPressureRepairCursor(connection, transaction, scope);
                    // Save Sync register/finalize/restore hold the global write
                    // gate. This read lease prevents any clear before COMMIT.
                    if (!PressureRepairEpochCurrent(epoch)
                        || (repaired.Inserted > 0 && !PersistenceService.TryMarkSaveSyncDirtyForMaintenance(campaign)))
                        return default;
                    if (!PressureRepairEpochCurrent(epoch)) return default;
                    try { transaction.Commit(); }
                    finally
                    {
                        // If the commit response is lost, the database may have
                        // committed both rows and cursor. Refresh on either outcome.
                        if (repaired.Inserted > 0)
                            WorldSimulationService.InvalidateWorldTestOverviewCache(campaign, timeline);
                    }
                    return (true, repaired.Inserted);
                }
            }
            finally { Program.CampaignDataGate.ExitReadLock(); }
        }
    }
}
