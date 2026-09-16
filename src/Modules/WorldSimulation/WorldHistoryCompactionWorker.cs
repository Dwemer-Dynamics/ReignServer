using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int PostgreSqlWorldHistoryCompactionBatchSize = 2000;

        private sealed class WorldHistoryCompactionLane
        {
            public readonly object Gate = new object();
            public string CampaignId = string.Empty;
            public string TimelineId = string.Empty;
            public double RequestedDay;
            public bool Running;
            public int LastCompacted;
            public int RemainingBacklog;
            public int LastAggregateWrites;
            public long LastDurationMs;
            public long TotalDurationMs;
            public long TotalCompacted;
            public long CompletedBatches;
            public string LastError = string.Empty;
            public string LastCompletedUtc = string.Empty;
        }

        private static readonly ConcurrentDictionary<string,
            WorldHistoryCompactionLane> WorldHistoryCompactionLanes =
            new ConcurrentDictionary<string, WorldHistoryCompactionLane>(
                StringComparer.OrdinalIgnoreCase);

        private static Dictionary<string, object> ScheduleWorldHistoryCompaction(
            string campaignId, string timelineId, double currentDay)
        {
            string key = campaignId + "\u001f" + timelineId;
            WorldHistoryCompactionLane lane =
                WorldHistoryCompactionLanes.GetOrAdd(key, _ =>
                    new WorldHistoryCompactionLane
                    {
                        CampaignId = campaignId,
                        TimelineId = timelineId
                    });
            bool start = false;
            lock (lane.Gate)
            {
                lane.RequestedDay = Math.Max(lane.RequestedDay, currentDay);
                if (!lane.Running)
                {
                    lane.Running = true;
                    start = true;
                }
            }
            if (start) Task.Run(() => RunWorldHistoryCompactionLane(lane));
            return WorldHistoryCompactionSnapshot(lane, "queued");
        }

        private static void RunWorldHistoryCompactionLane(
            WorldHistoryCompactionLane lane)
        {
            try
            {
                while (true)
                {
                    double currentDay;
                    lock (lane.Gate) currentDay = lane.RequestedDay;
                    Dictionary<string, object> result;
                    Stopwatch timer = Stopwatch.StartNew();
                    using (ReignDbConnection connection =
                        OpenCampaignConnection(lane.CampaignId))
                    {
                        EnsureWorldHistorySchema(connection);
                        using (ReignDbTransaction transaction =
                            connection.BeginTransaction())
                        {
                            int batchSize = ReignPostgreSqlDialect.IsPostgreSql(
                                connection)
                                ? PostgreSqlWorldHistoryCompactionBatchSize
                                : WorldHistoryCompactionBatchSize;
                            result = CompactWorldHistory(connection,
                                lane.CampaignId, lane.TimelineId, currentDay,
                                batchSize);
                            transaction.Commit();
                        }
                    }
                    timer.Stop();
                    int remaining = ReadInt(result, "remainingBacklog", 0);
                    lock (lane.Gate)
                    {
                        lane.LastCompacted = ReadInt(result, "compacted", 0);
                        lane.RemainingBacklog = remaining;
                        lane.LastAggregateWrites = ReadInt(result,
                            "aggregateWrites", 0);
                        lane.LastDurationMs = timer.ElapsedMilliseconds;
                        lane.TotalDurationMs += timer.ElapsedMilliseconds;
                        lane.TotalCompacted += lane.LastCompacted;
                        lane.CompletedBatches++;
                        lane.LastError = string.Empty;
                        lane.LastCompletedUtc = DateTime.UtcNow.ToString("o");
                        if (remaining == 0
                            && lane.RequestedDay <= currentDay)
                        {
                            lane.Running = false;
                            return;
                        }
                    }
                    Thread.Yield();
                }
            }
            catch (Exception ex)
            {
                lock (lane.Gate)
                {
                    lane.LastError = ex.Message;
                    lane.LastCompletedUtc = DateTime.UtcNow.ToString("o");
                    lane.Running = false;
                }
                LogOperational("world_history.compaction_failed",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = lane.CampaignId,
                        ["timelineId"] = lane.TimelineId,
                        ["error"] = ex.ToString()
                    });
            }
        }

        private static Dictionary<string, object>
            CompactWorldHistorySynchronously(string campaignId,
                string timelineId, double currentDay)
        {
            Stopwatch total = Stopwatch.StartNew();
            int compacted = 0;
            int aggregateWrites = 0;
            int remaining;
            int batches = 0;
            do
            {
                Dictionary<string, object> result;
                using (ReignDbConnection connection =
                    OpenCampaignConnection(campaignId))
                {
                    EnsureWorldHistorySchema(connection);
                    using (ReignDbTransaction transaction =
                        connection.BeginTransaction())
                    {
                        int batchSize = ReignPostgreSqlDialect.IsPostgreSql(
                            connection)
                            ? PostgreSqlWorldHistoryCompactionBatchSize
                            : WorldHistoryCompactionBatchSize;
                        result = CompactWorldHistory(connection, campaignId,
                            timelineId, currentDay, batchSize);
                        transaction.Commit();
                    }
                }
                compacted += ReadInt(result, "compacted", 0);
                aggregateWrites += ReadInt(result, "aggregateWrites", 0);
                remaining = ReadInt(result, "remainingBacklog", 0);
                batches++;
            }
            while (remaining > 0);
            total.Stop();
            return new Dictionary<string, object>
            {
                ["status"] = "completed",
                ["running"] = false,
                ["compacted"] = compacted,
                ["remainingBacklog"] = remaining,
                ["aggregateWrites"] = aggregateWrites,
                ["durationMs"] = total.ElapsedMilliseconds,
                ["completedBatches"] = batches
            };
        }

        private static Dictionary<string, object>
            WorldHistoryCompactionSnapshot(WorldHistoryCompactionLane lane,
                string status)
        {
            lock (lane.Gate)
            {
                return new Dictionary<string, object>
                {
                    ["status"] = lane.Running ? status : "idle",
                    ["running"] = lane.Running,
                    ["requestedDay"] = lane.RequestedDay,
                    ["compacted"] = lane.LastCompacted,
                    ["remainingBacklog"] = lane.RemainingBacklog,
                    ["aggregateWrites"] = lane.LastAggregateWrites,
                    ["durationMs"] = lane.LastDurationMs,
                    ["totalCompacted"] = lane.TotalCompacted,
                    ["totalDurationMs"] = lane.TotalDurationMs,
                    ["completedBatches"] = lane.CompletedBatches,
                    ["lastCompletedUtc"] = lane.LastCompletedUtc,
                    ["lastError"] = lane.LastError,
                    ["batchSize"] = PostgreSqlWorldHistoryCompactionBatchSize
                };
            }
        }
    }
}
