using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int RelationshipEvaluationBasePerSecond = 10000;
        private const int RelationshipEvaluationBurstPerSecond = 50000;
        private static readonly AutoResetEvent ContinuousRelationshipSignal = new AutoResetEvent(false);
        private static readonly object ContinuousRelationshipWorkerLock = new object();
        private static Thread ContinuousRelationshipWorkerThread;
        private static string ContinuousRelationshipActiveCampaign = "";
        private static string ContinuousRelationshipActiveTimeline = "";
        private static int ContinuousRelationshipActiveDay = -1;
        private static int ContinuousRelationshipActiveShard = -1;
        private static int ContinuousRelationshipActiveWindowStartDay = -1;
        private static int ContinuousRelationshipProcessedPairs;
        private static int ContinuousRelationshipEvaluatedPairDays;
        private static double ContinuousRelationshipEvaluationsPerSecond;
        private static long ContinuousRelationshipLastCompletedTs;
        private static string ContinuousRelationshipLastError = "";
        private static int ContinuousRelationshipAdaptiveLimit = RelationshipEvaluationBasePerSecond;
        private static int ContinuousRelationshipQueuedDays;
        private static int ContinuousRelationshipPreferredCampaignDays;
        private static double ContinuousRelationshipLastCpuFraction;
        private static double ContinuousRelationshipLastDatabaseSeconds;
        private static int ContinuousRelationshipParallelWorkers = 1;
        private static int ContinuousRelationshipParallelWorkItems;
        private static long ContinuousRelationshipParallelComputeMs;
        private static long ContinuousRelationshipSerialWriteMs;
        private static long ContinuousRelationshipPayloadPreparationMs;
        private static long ContinuousRelationshipSchemaReadinessMs;
        private static long ContinuousRelationshipProgressLoadMs;
        private static long ContinuousRelationshipLifecycleLoadMs;
        private static long ContinuousRelationshipHeroHydrationMs;
        private static long ContinuousRelationshipMatchingMs;
        private static long ContinuousRelationshipPersonalityLoadMs;
        private static long ContinuousRelationshipCommandPreparationMs;
        private static long ContinuousRelationshipTransactionBeginMs;
        private static long ContinuousRelationshipCommitMs;
        private static long ContinuousRelationshipUnattributedMs;
        private static long ContinuousRelationshipDecayMs;
        private static long ContinuousRelationshipStateLoadMs;
        private static long ContinuousRelationshipPairProcessingMs;
        private static long ContinuousRelationshipFinalizationMs;
        private static long ContinuousRelationshipFinalFlushMs;
        private static long ContinuousRelationshipIndependentLifecycleMs;
        private static long ContinuousRelationshipFlingProcessingMs;
        private static long ContinuousRelationshipFlingPreparationMs;
        private static int ContinuousRelationshipStandingReads;
        private static int ContinuousRelationshipObserverRosterReads;
        private static int ContinuousRelationshipPlayerRosterReads;
        private static int ContinuousRelationshipFlingDocumentsLoaded;
        private static int ContinuousRelationshipFlingRollWriteCommands;
        private static long ContinuousRelationshipCourtPopularityMs;
        private static long ContinuousRelationshipNativeCountMs;
        private static long ContinuousRelationshipInputLoadMs;
        private static long ContinuousRelationshipLockWaitMs;
        private static long ContinuousRelationshipPostgreSqlStagedRows;
        private static long ContinuousRelationshipPostgreSqlWrittenRows;
        private static long ContinuousRelationshipPostgreSqlCopyMs;
        private static long ContinuousRelationshipPostgreSqlMergeMs;
        private static int ContinuousRelationshipCourtPopularitySubjects;
        private static int ContinuousRelationshipCourtPopularityChangedSubjects;
        private static string ContinuousRelationshipBottleneck = "idle";
        private static readonly object CampaignRelationshipWriteLocksGuard = new object();
        private static readonly Dictionary<string, object> CampaignRelationshipWriteLocks =
            new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        private static readonly object RelationshipTokenBucketLock = new object();
        private static long RelationshipTokenWindowStarted = Stopwatch.GetTimestamp();
        private static int RelationshipTokenWindowUsed;

        private static void StartContinuousRelationshipWorker()
        {
            lock (ContinuousRelationshipWorkerLock)
            {
                if (ContinuousRelationshipWorkerThread != null && ContinuousRelationshipWorkerThread.IsAlive) return;
                ContinuousRelationshipWorkerThread = new Thread(ContinuousRelationshipLoop)
                {
                    IsBackground = true,
                    Name = "Reign continuous MBTI relationship worker"
                };
                ContinuousRelationshipWorkerThread.Start();
                ContinuousRelationshipSignal.Set();
            }
        }

        private static void SignalContinuousRelationshipWorker()
        {
            if (ActiveServerPort <= 0) return;
            StartContinuousRelationshipWorker();
            ContinuousRelationshipSignal.Set();
        }

        private static void ContinuousRelationshipLoop()
        {
            while (!ShutdownRequested)
            {
                try
                {
                    bool invalidated = TryProcessNextPublicStandingInvalidation();
                    bool processedDay = TryProcessNextRelationshipDayUnderCampaignGate();
                    if (!invalidated && !processedDay)
                        ContinuousRelationshipSignal.WaitOne(30000);
                }
                catch (Exception ex)
                {
                    ContinuousRelationshipLastError = LimitText(ex.ToString(), 2000);
                    LogOperational("relationships.continuous_worker_failed",
                        new Dictionary<string, object> { ["error"] = ContinuousRelationshipLastError });
                    ContinuousRelationshipSignal.WaitOne(1000);
                }
            }
        }

        private static bool TryProcessNextRelationshipDayUnderCampaignGate()
        {
            // Save Sync replaces the complete campaign directory while holding the
            // global write lock. The continuous relationship worker used to bypass
            // that lock, so it could recreate campaign state between the old
            // directory move and the restored-directory install. That left Save
            // Sync unable to install or recover because the destination existed.
            //
            // Holding the read lock for the complete durable day keeps the atomic
            // boundary honest. ReaderWriterLockSlim gives a waiting Save Sync
            // writer priority, so no later relationship day can begin ahead of it.
            CampaignDataGate.EnterReadLock();
            try
            {
                return TryProcessNextRelationshipDay();
            }
            finally
            {
                CampaignDataGate.ExitReadLock();
            }
        }

        private static bool TryProcessNextRelationshipDay()
        {
            var selectionTimer = Stopwatch.StartNew();
            string root = CampaignsRoot();
            if (!Directory.Exists(root)) return false;
            Dictionary<string, object> selected = null;
            string selectedCampaign = "";
            Dictionary<string, object> runtime = LoadLiveTestRuntime("");
            string preferredCampaign = IsFreshLiveTestRuntime(runtime) ? ReadString(runtime, "campaignId", "") : "";
            string preferredTimeline = ReadString(ReadDictionary(runtime, "saveSync"), "timelineId", "");
            bool serveInactive = ContinuousRelationshipPreferredCampaignDays >= 8;
            int selectedPriority = int.MaxValue;
            foreach (string directory in RelationshipCampaignCandidates(root)
                .OrderBy(path => Path.GetFileName(path).Equals(preferredCampaign, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                string campaignId = Path.GetFileName(directory);
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        RecoverInterruptedRelationshipDays(connection, campaignId);
                        long readyVersion = RelationshipReadyVersion(campaignId);
                        Dictionary<string, object> row = QuerySql(connection, @"
SELECT *,COUNT(*) OVER () AS pending_count FROM relationship_daily_inputs
WHERE status='pending'
ORDER BY CASE WHEN timeline_id=$preferredTimeline THEN 0 ELSE 1 END,day_key,timeline_id LIMIT 1;",
                            new Dictionary<string, object> { ["preferredTimeline"] =
                                campaignId == preferredCampaign ? preferredTimeline : "" }).FirstOrDefault();
                        ObserveRelationshipCampaignPending(campaignId, ReadInt(row, "pending_count", 0), readyVersion);
                        if (row == null) continue;
                        row["ready_version"] = readyVersion;
                        int priority = RelationshipCampaignWorkPriority(campaignId, ReadString(row, "timeline_id", ""),
                            preferredCampaign, preferredTimeline, serveInactive);
                        if (selected == null || priority < selectedPriority
                            || priority == selectedPriority && (ReadInt(row, "day_key", int.MaxValue) < ReadInt(selected, "day_key", int.MaxValue)
                            || ReadInt(row, "day_key", int.MaxValue) == ReadInt(selected, "day_key", int.MaxValue)
                                && ReadLong(row, "received_ts", long.MaxValue) < ReadLong(selected, "received_ts", long.MaxValue))
                            )
                        {
                            selected = row;
                            selectedCampaign = campaignId;
                            selectedPriority = priority;
                        }
                        if (!serveInactive && !string.IsNullOrWhiteSpace(preferredCampaign)
                            && priority == 0) break;
                    }
                }
                catch (Exception ex)
                {
                    LogOperational("relationships.pending_scan_failed",
                        new Dictionary<string, object> { ["campaignId"] = campaignId, ["error"] = LimitText(ex.Message, 800) });
                    if (DatabaseAvailability.Shared.BackingOff) break;
                }
            }
            if (selected == null) return false;
            selectionTimer.Stop();
            ContinuousRelationshipPreferredCampaignDays = !serveInactive && selectedCampaign == preferredCampaign
                ? ContinuousRelationshipPreferredCampaignDays + 1 : 0;

            // Any durable relationship work means the server is behind the game,
            // even if it is only the newly arrived current day. Enter burst mode
            // before processing starts instead of waiting for measured throughput
            // to approach the base ceiling.
            ContinuousRelationshipQueuedDays = CachedQueuedRelationshipDays();
            ContinuousRelationshipAdaptiveLimit =
                RelationshipLimitForQueuedDays(ContinuousRelationshipQueuedDays);
            string timelineId = ReadString(selected, "timeline_id", "main");
            int day = ReadInt(selected, "day_key", -1);
            int cadenceShard = ((day % RelationshipCadenceShardCount)
                + RelationshipCadenceShardCount) % RelationshipCadenceShardCount;
            int windowStartDay = day - RelationshipCadenceDays + 1;
            double worldDay = ReadDouble(selected, "world_day", day);
            long startedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(selectedCampaign))
            {
                EnsureMbtiRelationshipSchema(connection);
                ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='processing',started_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND status='pending';",
                    new Dictionary<string, object>
                    {
                        ["ts"] = startedTs, ["campaign"] = selectedCampaign,
                        ["timeline"] = timelineId, ["day"] = day
                    });
            }

            ContinuousRelationshipActiveCampaign = selectedCampaign;
            ContinuousRelationshipActiveTimeline = timelineId;
            ContinuousRelationshipActiveDay = day;
            ContinuousRelationshipActiveShard = cadenceShard;
            ContinuousRelationshipActiveWindowStartDay = windowStartDay;
            Stopwatch timer = Stopwatch.StartNew();
            TimeSpan cpuStarted = Process.GetCurrentProcess().TotalProcessorTime;
            List<object> cadenceInputs;
            Stopwatch inputLoadTimer = Stopwatch.StartNew();
            using (ReignDbConnection connection = OpenCampaignConnection(selectedCampaign))
            {
                EnsureMbtiRelationshipSchema(connection);
                cadenceInputs = QuerySql(connection, @"
SELECT day_key,world_day,presence_groups_json,hero_changes_json
FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND day_key BETWEEN $start AND $day
ORDER BY day_key;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = selectedCampaign,
                        ["timeline"] = timelineId,
                        ["start"] = windowStartDay,
                        ["day"] = day
                    })
                    .Select(row => (object)new Dictionary<string, object>
                    {
                        ["campaignId"] = selectedCampaign,
                        ["timelineId"] = timelineId,
                        ["worldDay"] = ReadDouble(row, "world_day",
                            ReadInt(row, "day_key", day)),
                        ["presenceGroups"] =
                            Json.Deserialize<List<Dictionary<string, object>>>(
                                ReadString(row, "presence_groups_json", "[]"))
                            ?? new List<Dictionary<string, object>>(),
                        ["heroes"] =
                            Json.Deserialize<List<Dictionary<string, object>>>(
                                ReadString(row, "hero_changes_json", "[]"))
                            ?? new List<Dictionary<string, object>>()
                    }).ToList();
            }
            inputLoadTimer.Stop();
            ContinuousRelationshipInputLoadMs =
                inputLoadTimer.ElapsedMilliseconds;
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["campaignId"] = selectedCampaign,
                ["timelineId"] = timelineId,
                ["worldDay"] = worldDay,
                ["continuousWorker"] = true,
                ["cadenceShard"] = cadenceShard,
                ["cadenceInputs"] = cadenceInputs
            };
            Dictionary<string, object> result;
            try
            {
                object campaignWriteLock =
                    CampaignRelationshipWriteLock(selectedCampaign);
                Stopwatch lockWaitTimer = Stopwatch.StartNew();
                lock (campaignWriteLock)
                {
                    lockWaitTimer.Stop();
                    ContinuousRelationshipLockWaitMs =
                        lockWaitTimer.ElapsedMilliseconds;
                    result = MbtiRelationshipSnapshotApi(payload);
                }
                if (!ReadBool(result, "ok", false))
                    throw new InvalidOperationException(ReadString(result, "error", "Relationship day processing failed."));
                int processedPairs = ReadInt(result, "processedPairs", 0);
                int evaluatedPairDays = ReadInt(result, "evaluatedPairDays", processedPairs);
                ContinuousRelationshipDecayMs =
                    ReadLong(result, "decayMs", 0);
                ContinuousRelationshipStateLoadMs =
                    ReadLong(result, "stateLoadMs", 0);
                ContinuousRelationshipPairProcessingMs =
                    ReadLong(result, "pairProcessingMs", 0);
                ContinuousRelationshipFinalizationMs =
                    ReadLong(result, "finalizationMs", 0);
                ContinuousRelationshipFinalFlushMs =
                    ReadLong(result, "finalFlushMs", 0);
                ContinuousRelationshipIndependentLifecycleMs = ReadLong(result, "independentLifecycleMs", 0);
                ContinuousRelationshipFlingProcessingMs = ReadLong(result, "flingProcessingMs", 0);
                ContinuousRelationshipStandingReads = ReadInt(result, "standingReadQueries", 0);
                ContinuousRelationshipObserverRosterReads = ReadInt(result, "observerRosterReadQueries", 0);
                ContinuousRelationshipPlayerRosterReads = ReadInt(result, "playerRosterReadQueries", 0);
                Dictionary<string, object> flingMetrics = ReadDictionary(result, "flings");
                ContinuousRelationshipFlingPreparationMs = ReadLong(flingMetrics, "preparationMs", 0);
                ContinuousRelationshipFlingDocumentsLoaded = ReadInt(flingMetrics, "profileDocumentsLoaded", 0);
                ContinuousRelationshipFlingRollWriteCommands = ReadInt(flingMetrics, "rollWriteCommands", 0);
                ContinuousRelationshipCourtPopularityMs =
                    ReadLong(result, "courtPopularityMs", 0);
                ContinuousRelationshipNativeCountMs =
                    ReadLong(result, "nativeCountMs", 0);
                ContinuousRelationshipPostgreSqlStagedRows =
                    ReadLong(result, "postgresStagedRows", 0);
                ContinuousRelationshipPostgreSqlWrittenRows =
                    ReadLong(result, "postgresWrittenRows", 0);
                ContinuousRelationshipPostgreSqlCopyMs =
                    ReadLong(result, "postgresCopyMs", 0);
                ContinuousRelationshipPostgreSqlMergeMs =
                    ReadLong(result, "postgresMergeMs", 0);
                ContinuousRelationshipCourtPopularitySubjects =
                    ReadInt(result, "courtPopularitySubjects", 0);
                ContinuousRelationshipCourtPopularityChangedSubjects =
                    ReadInt(result, "courtPopularityChangedSubjects", 0);
                ContinuousRelationshipProcessedPairs += processedPairs;
                ContinuousRelationshipEvaluatedPairDays += evaluatedPairDays;
                double databaseSeconds = timer.Elapsed.TotalSeconds;
                ContinuousRelationshipLastDatabaseSeconds = databaseSeconds;
                TimeSpan cpuUsed = Process.GetCurrentProcess().TotalProcessorTime - cpuStarted;
                ContinuousRelationshipLastCpuFraction = cpuUsed.TotalSeconds
                    / Math.Max(0.001d, databaseSeconds * Math.Max(1, Environment.ProcessorCount));
                ContinuousRelationshipQueuedDays = CachedQueuedRelationshipDays();
                bool backlogExists = ContinuousRelationshipQueuedDays > 0;
                ContinuousRelationshipAdaptiveLimit =
                    RelationshipLimitForQueuedDays(ContinuousRelationshipQueuedDays);
                timer.Stop();
                ContinuousRelationshipEvaluationsPerSecond = evaluatedPairDays
                    / Math.Max(0.001d, timer.Elapsed.TotalSeconds);
                ContinuousRelationshipLastCompletedTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ContinuousRelationshipLastError = "";
                using (ReignDbConnection connection = OpenCampaignConnection(selectedCampaign))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
status='processed',pair_cursor='',candidate_pairs=$candidates,processed_pairs=$processed,
cadence_shard=$shard,window_start_day=$windowStart,window_day_count=$windowDays,
pair_day_evaluations=$pairDays,processed_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;",
                        new Dictionary<string, object>
                        {
                            ["candidates"] = ReadInt(result, "candidatePairs", processedPairs),
                            ["processed"] = processedPairs, ["ts"] = ContinuousRelationshipLastCompletedTs,
                            ["shard"] = cadenceShard, ["windowStart"] = windowStartDay,
                            ["windowDays"] = cadenceInputs.Count, ["pairDays"] = evaluatedPairDays,
                            ["campaign"] = selectedCampaign, ["timeline"] = timelineId, ["day"] = day
                        });
                    ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
presence_groups_json='[]',hero_changes_json='[]'
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='processed' AND day_key<=$consumedThrough;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = selectedCampaign,
                            ["timeline"] = timelineId,
                            ["consumedThrough"] = day - RelationshipCadenceDays + 1
                        });
                    PruneRelationshipHeroObservations(connection, timelineId, day);
                    ExecuteSql(connection, @"DELETE FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='processed' AND day_key<$day-7;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = selectedCampaign, ["timeline"] = timelineId, ["day"] = day
                        });
                    ExecuteSql(connection, "PRAGMA wal_checkpoint(PASSIVE);");
                }
                Dictionary<string, object> performance = result
                    .Where(entry => entry.Key.EndsWith("Ms", StringComparison.Ordinal)
                        || entry.Key.EndsWith("Queries", StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value);
                ObserveRelationshipCampaignPending(selectedCampaign, Math.Max(0, ReadInt(selected, "pending_count", 1) - 1), ReadLong(selected, "ready_version", -1));
                SignalWorldTestRollupWorker();
                performance["schema"] = "reign_relationship_day_performance_v1";
                performance["selectionMs"] = selectionTimer.ElapsedMilliseconds;
                performance["firstReceivedTs"] = ReadLong(selected, "received_ts", 0);
                performance["queueWaitMs"] = Math.Max(0, (startedTs - ReadLong(selected, "received_ts", startedTs)) * 1000);
                performance["campaignId"] = selectedCampaign;
                performance["timelineId"] = timelineId;
                performance["day"] = day;
                performance["processedPairs"] = processedPairs;
                performance["flings"] = flingMetrics;
                performance["workerSeconds"] = databaseSeconds;
                performance["inputLoadMs"] = ContinuousRelationshipInputLoadMs;
                performance["campaignLockWaitMs"] = ContinuousRelationshipLockWaitMs;
                performance["serverModuleId"] = typeof(Program).Module.ModuleVersionId.ToString("D");
                LogOperational("relationships.day_completed", performance);
            }
            catch (Exception ex)
            {
                timer.Stop();
                ContinuousRelationshipLastError = LimitText(ex.ToString(), 2000);
                using (ReignDbConnection connection = OpenCampaignConnection(selectedCampaign))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='pending',last_error=$error
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;",
                        new Dictionary<string, object>
                        {
                            ["error"] = ContinuousRelationshipLastError,
                            ["campaign"] = selectedCampaign, ["timeline"] = timelineId, ["day"] = day
                        });
                }
                throw;
            }
            finally
            {
                ContinuousRelationshipActiveCampaign = "";
                ContinuousRelationshipActiveTimeline = "";
                ContinuousRelationshipActiveDay = -1;
                ContinuousRelationshipActiveShard = -1;
                ContinuousRelationshipActiveWindowStartDay = -1;
            }
            return true;
        }

        private static object CampaignRelationshipWriteLock(string campaignId)
        {
            string key = string.IsNullOrWhiteSpace(campaignId) ? "default" : campaignId;
            lock (CampaignRelationshipWriteLocksGuard)
            {
                if (!CampaignRelationshipWriteLocks.TryGetValue(key, out object value))
                {
                    value = new object();
                    CampaignRelationshipWriteLocks[key] = value;
                }
                return value;
            }
        }

        private static void ThrottleContinuousRelationshipChunk(int evaluationUnits)
        {
            if (evaluationUnits <= 0) return;
            lock (RelationshipTokenBucketLock)
            {
                while (true)
                {
                    long now = Stopwatch.GetTimestamp();
                    double elapsed = (now - RelationshipTokenWindowStarted) / (double)Stopwatch.Frequency;
                    if (elapsed >= 1d)
                    {
                        RelationshipTokenWindowStarted = now;
                        RelationshipTokenWindowUsed = 0;
                    }
                    if (RelationshipTokenWindowUsed + evaluationUnits <= ContinuousRelationshipAdaptiveLimit)
                    {
                        RelationshipTokenWindowUsed += evaluationUnits;
                        return;
                    }
                    int waitMs = Math.Max(1, (int)Math.Ceiling((1d - elapsed) * 1000d));
                    Thread.Sleep(waitMs);
                }
            }
        }

        private static int RelationshipLimitForQueuedDays(int queuedDays)
        {
            return queuedDays > 0
                ? RelationshipEvaluationBurstPerSecond
                : RelationshipEvaluationBasePerSecond;
        }

        private static int RelationshipParallelismForWork(int workItems,
            bool forceBurst = false)
        {
            if (workItems < 256 || Environment.ProcessorCount <= 2) return 1;
            int logicalProcessors = Math.Max(1, Environment.ProcessorCount);
            int reserve = logicalProcessors >= 6 ? 2 : 1;
            int available = Math.Max(1,
                Math.Min(32, logicalProcessors - reserve));
            // Daily matching makes the dice stage intentionally small. Giving
            // ~800 trivial deterministic rolls twenty threads costs more in
            // scheduling than it saves. Scale in bounded tiers; queued days
            // still run continuously, but do not oversubscribe the database
            // preparation that follows the CPU-only stage.
            int target = workItems < 2048 ? 2
                : workItems < 8192 ? 4
                : 8;
            return Math.Min(Math.Min(available, target), workItems);
        }

        private static void RecordRelationshipParallelStage(int workerCount,
            int workItems, long computeMs)
        {
            ContinuousRelationshipParallelWorkers = Math.Max(1, workerCount);
            ContinuousRelationshipParallelWorkItems = Math.Max(0, workItems);
            ContinuousRelationshipParallelComputeMs = Math.Max(0, computeMs);
        }

        private static void RecordRelationshipPhaseTimings(
            Dictionary<string, object> result)
        {
            result = result ?? new Dictionary<string, object>();
            ContinuousRelationshipSerialWriteMs = Math.Max(0,
                ReadLong(result, "nonParallelMs",
                    ReadLong(result, "serialWriteMs", 0)));
            ContinuousRelationshipPayloadPreparationMs = Math.Max(0,
                ReadLong(result, "payloadPreparationMs", 0));
            ContinuousRelationshipSchemaReadinessMs = Math.Max(0,
                ReadLong(result, "schemaReadinessMs", 0));
            ContinuousRelationshipProgressLoadMs = Math.Max(0,
                ReadLong(result, "progressLoadMs", 0));
            ContinuousRelationshipLifecycleLoadMs = Math.Max(0,
                ReadLong(result, "lifecycleLoadMs", 0));
            ContinuousRelationshipHeroHydrationMs = Math.Max(0,
                ReadLong(result, "heroHydrationMs", 0));
            ContinuousRelationshipMatchingMs = Math.Max(0,
                ReadLong(result, "matchingMs", 0));
            ContinuousRelationshipPersonalityLoadMs = Math.Max(0,
                ReadLong(result, "personalityLoadMs", 0));
            ContinuousRelationshipCommandPreparationMs = Math.Max(0,
                ReadLong(result, "commandPreparationMs", 0));
            ContinuousRelationshipTransactionBeginMs = Math.Max(0,
                ReadLong(result, "transactionBeginMs", 0));
            ContinuousRelationshipCommitMs = Math.Max(0,
                ReadLong(result, "commitMs", 0));
            ContinuousRelationshipUnattributedMs = Math.Max(0,
                ReadLong(result, "unattributedMs", 0));
            ContinuousRelationshipDecayMs = Math.Max(0,
                ReadLong(result, "decayMs", 0));
            ContinuousRelationshipStateLoadMs = Math.Max(0,
                ReadLong(result, "stateLoadMs", 0));
            ContinuousRelationshipPairProcessingMs = Math.Max(0,
                ReadLong(result, "pairProcessingMs", 0));
            ContinuousRelationshipFinalizationMs = Math.Max(0,
                ReadLong(result, "finalizationMs", 0));
            List<KeyValuePair<string, long>> phases =
                new List<KeyValuePair<string, long>>
                {
                    new KeyValuePair<string, long>("payload_preparation",
                        ContinuousRelationshipPayloadPreparationMs),
                    new KeyValuePair<string, long>("schema_readiness",
                        ContinuousRelationshipSchemaReadinessMs),
                    new KeyValuePair<string, long>("progress_lookup",
                        ContinuousRelationshipProgressLoadMs),
                    new KeyValuePair<string, long>("lifecycle_state_load",
                        ContinuousRelationshipLifecycleLoadMs),
                    new KeyValuePair<string, long>("hero_hydration",
                        ContinuousRelationshipHeroHydrationMs),
                    new KeyValuePair<string, long>("daily_matching",
                        ContinuousRelationshipMatchingMs),
                    new KeyValuePair<string, long>("parallel_compute",
                        ContinuousRelationshipParallelComputeMs),
                    new KeyValuePair<string, long>("personality_load",
                        ContinuousRelationshipPersonalityLoadMs),
                    new KeyValuePair<string, long>("command_preparation",
                        ContinuousRelationshipCommandPreparationMs),
                    new KeyValuePair<string, long>("transaction_begin",
                        ContinuousRelationshipTransactionBeginMs),
                    new KeyValuePair<string, long>("decay",
                        ContinuousRelationshipDecayMs),
                    new KeyValuePair<string, long>("relationship_state_load",
                        ContinuousRelationshipStateLoadMs),
                    new KeyValuePair<string, long>("pair_processing",
                        ContinuousRelationshipPairProcessingMs),
                    new KeyValuePair<string, long>("independent_lifecycle",
                        ContinuousRelationshipIndependentLifecycleMs),
                    new KeyValuePair<string, long>("finalization",
                        ContinuousRelationshipFinalizationMs),
                    new KeyValuePair<string, long>("commit",
                        ContinuousRelationshipCommitMs),
                    new KeyValuePair<string, long>("unattributed_non_parallel",
                        ContinuousRelationshipUnattributedMs)
                };
            KeyValuePair<string, long> largest = phases
                .OrderByDescending(item => item.Value).First();
            if (largest.Value <= 0 && ContinuousRelationshipLockWaitMs <= 0)
                ContinuousRelationshipBottleneck = "idle";
            else if (ContinuousRelationshipLockWaitMs
                > Math.Max(1, largest.Value) * 2)
                ContinuousRelationshipBottleneck =
                    "campaign_lock_contention";
            else
                ContinuousRelationshipBottleneck = largest.Key;
        }

        private static int CountQueuedRelationshipDays()
        {
            string root = CampaignsRoot();
            if (!Directory.Exists(root)) return 0;
            int count = 0;
            foreach (string directory in Directory.GetDirectories(root))
            {
                try
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(Path.GetFileName(directory)))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        count += ReadInt(QuerySql(connection,
                            "SELECT COUNT(*) AS count FROM relationship_daily_inputs WHERE status='pending';")
                            .FirstOrDefault(), "count", 0);
                    }
                }
                catch { }
            }
            return count;
        }

        private static int RelationshipCampaignWorkPriority(string campaignId, string timelineId,
            string activeCampaignId, string activeTimelineId, bool serveInactive)
        {
            if (string.IsNullOrWhiteSpace(activeCampaignId)) return 0;
            bool active = campaignId.Equals(activeCampaignId, StringComparison.OrdinalIgnoreCase);
            if (serveInactive) return active ? 1 : 0;
            if (!active) return 2;
            return string.IsNullOrWhiteSpace(activeTimelineId)
                || timelineId.Equals(activeTimelineId, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        private static Dictionary<string, object> ContinuousRelationshipWorkerStatus()
        {
            return new Dictionary<string, object>
            {
                ["running"] = ContinuousRelationshipWorkerThread != null && ContinuousRelationshipWorkerThread.IsAlive,
                ["activeCampaignId"] = ContinuousRelationshipActiveCampaign,
                ["activeTimelineId"] = ContinuousRelationshipActiveTimeline,
                ["activeDay"] = ContinuousRelationshipActiveDay,
                ["activeShard"] = ContinuousRelationshipActiveShard,
                ["activeWindowStartDay"] = ContinuousRelationshipActiveWindowStartDay,
                ["cadenceDays"] = RelationshipCadenceDays,
                ["shardCount"] = RelationshipCadenceShardCount,
                ["completedPairDays"] = ContinuousRelationshipProcessedPairs,
                ["evaluatedPairDays"] = ContinuousRelationshipEvaluatedPairDays,
                ["evaluationsPerSecond"] = Math.Min(ContinuousRelationshipAdaptiveLimit, ContinuousRelationshipEvaluationsPerSecond),
                ["baseLimitPerSecond"] = RelationshipEvaluationBasePerSecond,
                ["adaptiveLimitPerSecond"] = ContinuousRelationshipAdaptiveLimit,
                ["burstLimitPerSecond"] = RelationshipEvaluationBurstPerSecond,
                ["queuedDays"] = ContinuousRelationshipQueuedDays,
                ["lastCpuFraction"] = ContinuousRelationshipLastCpuFraction,
                ["lastDatabaseSeconds"] = ContinuousRelationshipLastDatabaseSeconds,
                ["logicalProcessors"] = Environment.ProcessorCount,
                ["parallelWorkers"] = ContinuousRelationshipParallelWorkers,
                ["parallelWorkItems"] = ContinuousRelationshipParallelWorkItems,
                ["parallelComputeMs"] = ContinuousRelationshipParallelComputeMs,
                ["serialWriteMs"] = ContinuousRelationshipSerialWriteMs,
                ["nonParallelMs"] = ContinuousRelationshipSerialWriteMs,
                ["payloadPreparationMs"] =
                    ContinuousRelationshipPayloadPreparationMs,
                ["schemaReadinessMs"] =
                    ContinuousRelationshipSchemaReadinessMs,
                ["progressLoadMs"] = ContinuousRelationshipProgressLoadMs,
                ["lifecycleLoadMs"] = ContinuousRelationshipLifecycleLoadMs,
                ["heroHydrationMs"] = ContinuousRelationshipHeroHydrationMs,
                ["matchingMs"] = ContinuousRelationshipMatchingMs,
                ["personalityLoadMs"] =
                    ContinuousRelationshipPersonalityLoadMs,
                ["commandPreparationMs"] =
                    ContinuousRelationshipCommandPreparationMs,
                ["transactionBeginMs"] =
                    ContinuousRelationshipTransactionBeginMs,
                ["commitMs"] = ContinuousRelationshipCommitMs,
                ["unattributedMs"] = ContinuousRelationshipUnattributedMs,
                ["decayMs"] = ContinuousRelationshipDecayMs,
                ["stateLoadMs"] = ContinuousRelationshipStateLoadMs,
                ["pairProcessingMs"] =
                    ContinuousRelationshipPairProcessingMs,
                ["finalizationMs"] = ContinuousRelationshipFinalizationMs,
                ["finalFlushMs"] = ContinuousRelationshipFinalFlushMs,
                ["independentLifecycleMs"] = ContinuousRelationshipIndependentLifecycleMs,
                ["flingProcessingMs"] = ContinuousRelationshipFlingProcessingMs,
                ["flingPreparationMs"] = ContinuousRelationshipFlingPreparationMs,
                ["standingReadQueries"] = ContinuousRelationshipStandingReads,
                ["observerRosterReadQueries"] = ContinuousRelationshipObserverRosterReads,
                ["playerRosterReadQueries"] = ContinuousRelationshipPlayerRosterReads,
                ["flingProfileDocumentsLoaded"] = ContinuousRelationshipFlingDocumentsLoaded,
                ["flingRollWriteCommands"] = ContinuousRelationshipFlingRollWriteCommands,
                ["courtPopularityMs"] =
                    ContinuousRelationshipCourtPopularityMs,
                ["nativeCountMs"] = ContinuousRelationshipNativeCountMs,
                ["inputLoadMs"] = ContinuousRelationshipInputLoadMs,
                ["campaignLockWaitMs"] =
                    ContinuousRelationshipLockWaitMs,
                ["postgresStagedRows"] =
                    ContinuousRelationshipPostgreSqlStagedRows,
                ["postgresWrittenRows"] =
                    ContinuousRelationshipPostgreSqlWrittenRows,
                ["postgresCopyMs"] =
                    ContinuousRelationshipPostgreSqlCopyMs,
                ["postgresMergeMs"] =
                    ContinuousRelationshipPostgreSqlMergeMs,
                ["courtPopularitySubjects"] =
                    ContinuousRelationshipCourtPopularitySubjects,
                ["courtPopularityChangedSubjects"] =
                    ContinuousRelationshipCourtPopularityChangedSubjects,
                ["bottleneck"] = ContinuousRelationshipBottleneck,
                ["chunkSize"] = PostgreSqlRelationshipWriteChunkSize,
                ["lastCompletedTs"] = ContinuousRelationshipLastCompletedTs,
                ["lastError"] = ContinuousRelationshipLastError
            };
        }
    }
}
