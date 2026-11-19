using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static readonly AutoResetEvent WorldTestRollupSignal = new AutoResetEvent(false);
        private static readonly object WorldTestRollupStatusGate = new object();
        private static Thread WorldTestRollupThread;
        private static bool WorldTestRollupWorkerStarted;
        private static bool WorldTestRollupWorkerStopping;
        private static string WorldTestRollupActiveCampaign = string.Empty;
        private static string WorldTestRollupActiveTimeline = string.Empty;
        private static int WorldTestRollupActiveDay = -1;
        private static string WorldTestRollupLastError = string.Empty;
        private static long WorldTestRollupCompleted;
        private static long WorldTestRollupFailed;
        private static long WorldTestRollupLastDurationMs;
        private static long WorldTestRollupLastCompletedTs;

        internal static void StartWorldTestRollupWorker()
        {
            lock (WorldTestRollupStatusGate)
            {
                if (WorldTestRollupWorkerStarted) return;
                WorldTestRollupWorkerStarted = true;
                WorldTestRollupWorkerStopping = false;
                WorldTestRollupThread = new Thread(WorldTestRollupLoop)
                {
                    IsBackground = true,
                    Name = "ReignWorldTestRollups"
                };
                WorldTestRollupThread.Start();
            }
        }

        internal static void SignalWorldTestRollupWorker()
        {
            WorldTestRollupSignal.Set();
        }

        private static void WorldTestRollupLoop()
        {
            while (!WorldTestRollupWorkerStopping && !Program.ShutdownRequested)
            {
                bool processed = false;
                try
                {
                    processed = ProcessNextWorldTestRollup();
                }
                catch (Exception ex)
                {
                    lock (WorldTestRollupStatusGate)
                    {
                        WorldTestRollupLastError = ex.Message;
                        WorldTestRollupFailed++;
                    }
                }
                if (!processed) WorldTestRollupSignal.WaitOne(1000);
            }
        }

        private static bool ProcessNextWorldTestRollup()
        {
            WorldTestRollupWorkItem item = FindNextWorldTestRollup();
            if (item == null) return false;
            Stopwatch stopwatch = Stopwatch.StartNew();
            lock (WorldTestRollupStatusGate)
            {
                WorldTestRollupActiveCampaign = item.CampaignId;
                WorldTestRollupActiveTimeline = item.TimelineId;
                WorldTestRollupActiveDay = item.DayKey;
            }
            try
            {
                // Overview readers acquire the campaign gate for the individual
                // authoritative ledgers they inspect. Holding the same
                // non-recursive read lock around the whole rollup causes a
                // recursive-lock failure before any rollup can be written.
                Dictionary<string, object> overview = BuildWorldTestOverview(
                    item.CampaignId, item.TimelineId, double.MinValue, double.MaxValue, 100);
                Dictionary<string, object> rollup = CompactWorldTestRollup(overview);
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(item.CampaignId))
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                    EnsureWorldTestSchema(connection);
                    EnsureWorldTestTelemetrySchema(connection);
                    Dictionary<string, object> heartbeat = WorldSimulationStore.SelectWorldTestNativeHeartbeats7(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = item.CampaignId,
                            ["timeline"] = item.TimelineId,
                            ["day"] = item.DayKey
                        }).FirstOrDefault();
                    double worldDay = ReadDouble(heartbeat, "world_day", item.DayKey);
                    rollup["worldDay"] = worldDay;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    WorldSimulationStore.UpsertWorldTestDailyRollups(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = item.CampaignId, ["timeline"] = item.TimelineId,
                            ["day"] = item.DayKey, ["worldDay"] = worldDay,
                            ["status"] = ReadString(overview, "overallStatus", "insufficient_data"),
                            ["rollup"] = Json.Serialize(rollup), ["ts"] = now
                        });
                    WorldSimulationStore.UpdateWorldTestRollupQueue(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = item.CampaignId, ["timeline"] = item.TimelineId,
                            ["day"] = item.DayKey, ["revision"] = item.Revision, ["ts"] = now
                        });
                    GenerateEligibleWorldTestCheckpointReports(
                        connection, item.CampaignId, item.TimelineId, worldDay);
                    CleanupCompletedWorldTestTelemetry(
                        connection, item.CampaignId, item.TimelineId, item.DayKey);
                    transaction.Commit();
                    WorldSimulationStore.Pragma(connection);
                }
                stopwatch.Stop();
                lock (WorldTestRollupStatusGate)
                {
                    WorldTestRollupCompleted++;
                    WorldTestRollupLastDurationMs = stopwatch.ElapsedMilliseconds;
                    WorldTestRollupLastCompletedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    WorldTestRollupLastError = string.Empty;
                }
                return true;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (!ReignPostgreSqlStorage.CampaignExists(
                    item.CampaignId))
                {
                    // A test fixture or deleted campaign can disappear after
                    // work discovery but before its rollup begins. That is a
                    // cancellation, not a failed production rollup, and must
                    // not recreate the removed campaign merely to report an
                    // error into its queue.
                    lock (WorldTestRollupStatusGate)
                    {
                        WorldTestRollupLastDurationMs =
                            stopwatch.ElapsedMilliseconds;
                        WorldTestRollupLastError = string.Empty;
                    }
                    return true;
                }
                try
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(item.CampaignId))
                    {
                        EnsureWorldTestTelemetrySchema(connection);
                        WorldSimulationStore.UpdateWorldTestRollupQueue2(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = item.CampaignId, ["timeline"] = item.TimelineId,
                                ["day"] = item.DayKey, ["error"] = ex.Message,
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                    }
                }
                catch { }
                lock (WorldTestRollupStatusGate)
                {
                    WorldTestRollupFailed++;
                    WorldTestRollupLastDurationMs = stopwatch.ElapsedMilliseconds;
                    WorldTestRollupLastError = ex.Message;
                }
                return true;
            }
            finally
            {
                lock (WorldTestRollupStatusGate)
                {
                    WorldTestRollupActiveCampaign = string.Empty;
                    WorldTestRollupActiveTimeline = string.Empty;
                    WorldTestRollupActiveDay = -1;
                }
            }
        }

        private static void CleanupCompletedWorldTestTelemetry(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            int completedDayKey)
        {
            Dictionary<string, object> parameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["queueCutoff"] = completedDayKey - 31,
                ["evidenceCutoff"] = completedDayKey - 63
            };
            WorldSimulationStore.DeleteWorldTestRollupQueue(connection, parameters);
            WorldSimulationStore.DeleteWorldTestDiagnosticEvidence(connection, parameters);
        }

        private static WorldTestRollupWorkItem FindNextWorldTestRollup()
        {
            WorldTestRollupWorkItem best = null;
            foreach (Dictionary<string, object> campaign
                in ReignPostgreSqlStorage.ListCampaignMetadata())
            {
                string campaignId =
                    ReadString(campaign, "campaignId", "");
                if (CampaignFileService.IsInternalCampaignId(campaignId)) continue;
                string campaignDirectory = CampaignFileService.CampaignDirectory(campaignId);
                if (!Directory.Exists(campaignDirectory)
                    || !CampaignFileService.IsRealCampaignDirectory(
                        new DirectoryInfo(campaignDirectory))) continue;
                try
                {
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureWorldTestTelemetrySchema(connection);
                        RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                        Dictionary<string, object> row = WorldSimulationStore.SelectWorldTestRollupQueue3(connection).FirstOrDefault();
                        if (row == null) continue;
                        WorldTestRollupWorkItem candidate = new WorldTestRollupWorkItem
                        {
                            CampaignId = ReadString(
                                row, "campaign_id", campaignId),
                            TimelineId = ReadString(row, "timeline_id", "main"),
                            DayKey = ReadInt(row, "day_key", -1),
                            Revision = ReadInt(row, "revision", 1),
                            UpdatedTs = ReadLong(row, "updated_ts", 0)
                        };
                        if (best == null || candidate.DayKey < best.DayKey
                            || (candidate.DayKey == best.DayKey && candidate.UpdatedTs < best.UpdatedTs))
                            best = candidate;
                    }
                }
                catch { }
            }
            return best;
        }

        private static Dictionary<string, object> WorldTestRollupWorkerStatus()
        {
            int pending = 0;
            foreach (Dictionary<string, object> campaign
                in ReignPostgreSqlStorage.ListCampaignMetadata())
            {
                string campaignId =
                    ReadString(campaign, "campaignId", "");
                if (CampaignFileService.IsInternalCampaignId(campaignId)) continue;
                string campaignDirectory = CampaignFileService.CampaignDirectory(campaignId);
                if (!Directory.Exists(campaignDirectory)
                    || !CampaignFileService.IsRealCampaignDirectory(
                        new DirectoryInfo(campaignDirectory))) continue;
                try
                {
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureWorldTestTelemetrySchema(connection);
                        pending += ReadInt(WorldSimulationStore.CountWorldTestRollupQueue2(connection)
                            .FirstOrDefault(), "count", 0);
                    }
                }
                catch { }
            }
            lock (WorldTestRollupStatusGate)
            {
                return new Dictionary<string, object>
                {
                    ["started"] = WorldTestRollupWorkerStarted,
                    ["pending"] = pending,
                    ["activeCampaignId"] = WorldTestRollupActiveCampaign,
                    ["activeTimelineId"] = WorldTestRollupActiveTimeline,
                    ["activeDayKey"] = WorldTestRollupActiveDay,
                    ["completed"] = WorldTestRollupCompleted,
                    ["failed"] = WorldTestRollupFailed,
                    ["lastDurationMs"] = WorldTestRollupLastDurationMs,
                    ["lastCompletedUtc"] = UnixToIso(WorldTestRollupLastCompletedTs),
                    ["lastError"] = WorldTestRollupLastError
                };
            }
        }

        private sealed class WorldTestRollupWorkItem
        {
            internal string CampaignId;
            internal string TimelineId;
            internal int DayKey;
            internal int Revision;
            internal long UpdatedTs;
        }
    }
}
