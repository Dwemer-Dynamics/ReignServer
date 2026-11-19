using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class ReputationService
    {
        private static readonly object PendingSocialWorldHistoryLock = new object();
        private static readonly HashSet<string> PendingSocialWorldHistoryWorkers =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        internal const int SocialWorldHistoryBatchSize = 100;
        private const int SocialSubjectBatchSize = 32;
        private const int SocialSubjectFinalizationBatchSize = 16;
        internal static int SocialOutcomeActiveWorkers;
        internal static int SocialOutcomeConfiguredWorkers;
        internal static long SocialOutcomeCompletedTotal;
        internal static long SocialOutcomeLastBatchMs;
        private static long SocialOutcomeLastCompletedTs;
        internal static long SocialOutcomeLastTransactionMs;
        internal static long SocialOutcomeLastTransactionPerEventMs;
        internal static long SocialOutcomeTransactionConflicts;
        internal static long SocialOutcomeTransactionAttempts;
        internal static long SocialOutcomeLastBatchCompleted;
        private static int SocialOutcomeClaimableSubjects;
        private static int SocialOutcomeAdaptiveWorkers;
        internal static string SocialOutcomeLastError = "";

        internal static bool ResumePendingSocialWorldHistoryOutcomes(
            string campaignId, string timelineId)
        {
            if (string.IsNullOrWhiteSpace(campaignId)
                || string.IsNullOrWhiteSpace(timelineId)
                || !HasPendingSocialWorldHistoryOutcomes(campaignId, timelineId))
                return false;
            SchedulePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
            return true;
        }

        internal static void SchedulePendingSocialWorldHistoryOutcomes(
            string campaignId, string timelineId)
        {
            // Verification fixtures are synchronous and are dropped as soon
            // as their assertions finish. Scheduling ordinary background
            // social processing for them can reopen and recreate a fixture's
            // PostgreSQL schema after cleanup.
            if (CampaignFileService.IsInternalCampaignId(campaignId)) return;
            string workerKey = campaignId + "|" + timelineId;
            lock (PendingSocialWorldHistoryLock)
            {
                if (!PendingSocialWorldHistoryWorkers.Add(workerKey)) return;
            }
            _ = Task.Run(() => ProcessPendingSocialWorldHistoryOutcomesParallel(
                campaignId, timelineId, workerKey));
        }

        private static void ProcessPendingSocialWorldHistoryOutcomesParallel(
            string campaignId, string timelineId, string workerKey)
        {
            Stopwatch timer = Stopwatch.StartNew();
            try
            {
                int pendingEventCount = RefreshSocialSubjectWorkQueue(campaignId,
                    timelineId);
                // Expiration is campaign-wide work. Perform it once for the
                // pending wave rather than repeating the same table sweep for
                // every independently finalized subject lane.
                using (ReignDbConnection expirationConnection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialReputationSchema(expirationConnection);
                    WorldSimulationService.EnsureWorldTestTelemetrySchema(expirationConnection);
                    Dictionary<string, object> latest = ReputationStore.SelectWorldHistoryEvents(
                        expirationConnection, new Dictionary<string, object>
                        {
                            ["timeline"] = timelineId
                        }).FirstOrDefault();
                    double pendingWorldDay = ReadDouble(latest,
                        "world_day", 0d);
                    if (pendingWorldDay > 0d)
                        ExpireSocialRumors(expirationConnection, campaignId,
                            timelineId, pendingWorldDay);
                }
                int subjectCount = Volatile.Read(
                    ref SocialOutcomeClaimableSubjects);
                int initial = Clamp(Environment.ProcessorCount / 2, 2, 8);
                int maximum = Clamp(Environment.ProcessorCount - 1, 2, 16);
                if (Volatile.Read(ref SocialOutcomeAdaptiveWorkers) <= 0)
                    Interlocked.CompareExchange(ref SocialOutcomeAdaptiveWorkers,
                        initial, 0);
                int adaptive = Volatile.Read(ref SocialOutcomeAdaptiveWorkers);
                long lastTransactionMs = Interlocked.Read(
                    ref SocialOutcomeLastTransactionMs);
                long lastTransactionPerEventMs = Interlocked.Read(
                    ref SocialOutcomeLastTransactionPerEventMs);
                long transactionAttempts = Math.Max(1,
                    Interlocked.Read(ref SocialOutcomeTransactionAttempts));
                double conflictRate = Interlocked.Read(
                    ref SocialOutcomeTransactionConflicts)
                    / (double)transactionAttempts;
                if ((lastTransactionPerEventMs > 250 || conflictRate > 0.05d)
                    && adaptive > 2)
                    adaptive = Interlocked.Decrement(
                        ref SocialOutcomeAdaptiveWorkers);
                else if (pendingEventCount > adaptive * SocialSubjectBatchSize
                    && lastTransactionPerEventMs >= 0
                    && lastTransactionPerEventMs < 250
                    && conflictRate < 0.01d
                    && adaptive < maximum)
                    adaptive = Interlocked.Increment(
                        ref SocialOutcomeAdaptiveWorkers);
                int workerCount = Math.Min(subjectCount,
                    Math.Min(maximum, Math.Max(1, adaptive)));
                SocialOutcomeConfiguredWorkers = workerCount;
                long completedBefore = Interlocked.Read(
                    ref SocialOutcomeCompletedTotal);
                Interlocked.Exchange(ref SocialOutcomeLastTransactionMs, 0L);
                Interlocked.Exchange(ref SocialOutcomeLastTransactionPerEventMs, 0L);
                List<Task> workers = new List<Task>();
                for (int index = 0; index < workerCount; index++)
                {
                    string owner = workerKey + "|lane|" + index;
                    workers.Add(Task.Run(() => DrainSocialSubjectLanes(
                        campaignId, timelineId, owner)));
                }
                Task.WaitAll(workers.ToArray());
                FinalizeCompletedSocialSubjectLanes(campaignId, timelineId);
                Interlocked.Exchange(ref SocialOutcomeLastBatchCompleted,
                    Interlocked.Read(ref SocialOutcomeCompletedTotal)
                        - completedBefore);
                SocialOutcomeLastError = "";
            }
            catch (Exception ex)
            {
                SocialOutcomeLastError = DialogueService.LimitText(ex.ToString(), 2000);
                OperationsService.LogOperational("world_history.social_parallel_drain_failed",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["error"] = SocialOutcomeLastError
                    });
            }
            finally
            {
                timer.Stop();
                SocialOutcomeLastBatchMs = timer.ElapsedMilliseconds;
                SocialOutcomeLastCompletedTs =
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                lock (PendingSocialWorldHistoryLock)
                    PendingSocialWorldHistoryWorkers.Remove(workerKey);
                if (HasPendingSocialWorldHistoryOutcomes(campaignId, timelineId))
                    SchedulePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
            }
        }

        private static int RefreshSocialSubjectWorkQueue(string campaignId,
            string timelineId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                WorldSimulationService.EnsureWorldHistorySchema(connection);
                EnsureSocialReputationSchema(connection);
                EnsureSocialSubjectWorkSchema(connection);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                ReputationStore.UpdateSocialSubjectWorkQueue(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ts"] = now
                    });
                if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                {
                    ReputationStore.SelectGrouped(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId,
                            ["ts"] = now
                        });
                    Dictionary<string, object> summary = ReputationStore.CountWorldHistoryEvents(connection, new Dictionary<string, object>
                        {
                            ["timeline"] = timelineId
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    int postgreSqlSubjects = ReadInt(summary,
                        "subject_count", 0);
                    Volatile.Write(ref SocialOutcomeClaimableSubjects,
                        postgreSqlSubjects);
                    return ReadInt(summary, "event_count", 0);
                }
                List<Dictionary<string, object>> pending = ReputationStore.SelectWorldHistoryEvents2(connection, new Dictionary<string, object> { ["timeline"] = timelineId });
                foreach (IGrouping<string, Dictionary<string, object>> subject in pending
                    .Select(row => new
                    {
                        Row = row,
                        Subject = ReadFirstString(
                            TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                                ?? new Dictionary<string, object>(),
                            "subjectId", "heroId")
                    })
                    .Where(value => !string.IsNullOrWhiteSpace(value.Subject))
                    .GroupBy(value => value.Subject, value => value.Row,
                        StringComparer.OrdinalIgnoreCase))
                {
                    long first = subject.Min(row => ReadLong(row, "sequence", 0));
                    ReputationStore.UpsertSocialSubjectWorkQueue(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["subject"] = subject.Key, ["sequence"] = first,
                            ["count"] = subject.Count(), ["ts"] = now
                        });
                }
                int subjectCount = pending.Select(row => ReadFirstString(
                        TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                            ?? new Dictionary<string, object>(),
                        "subjectId", "heroId"))
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                Volatile.Write(ref SocialOutcomeClaimableSubjects,
                    subjectCount);
                return pending.Count;
            }
        }

        internal static void EnsureSocialSubjectWorkSchema(
            ReignDbConnection connection)
        {
            ReputationSchema.CreateSocialSubjectWorkQueue(connection);
            ReputationSchema.EnsureColumnSocialSubjectWorkQueueNeedsFinalize(connection);
            ReputationSchema.EnsureColumnSocialSubjectWorkQueueLastWorldDay(connection);
            ReputationSchema.EnsureColumnSocialSubjectWorkQueueLastEventId(connection);
            ReputationSchema.CreateIndexSocialSubjectWorkClaim(connection);
        }

        private static void DrainSocialSubjectLanes(string campaignId,
            string timelineId, string owner)
        {
            Interlocked.Increment(ref SocialOutcomeActiveWorkers);
            try
            {
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> socialCatalog =
                        ReadGlobalSocialCatalog();
                    while (!Program.ShutdownRequested)
                    {
                        string subjectId = ClaimSocialSubject(connection,
                            campaignId, timelineId, owner);
                        if (string.IsNullOrWhiteSpace(subjectId)) return;
                        Program.CampaignDataGate.EnterReadLock();
                        try
                        {
                            try
                            {
                                int processed = ProcessSocialSubjectBatch(
                                    connection, socialCatalog, campaignId,
                                    timelineId, subjectId);
                                Interlocked.Add(ref SocialOutcomeCompletedTotal,
                                    processed);
                            }
                            catch (Exception ex)
                            {
                                if (IsSocialOutcomeTransactionConflict(ex))
                                    Interlocked.Increment(
                                        ref SocialOutcomeTransactionConflicts);
                                using (ReignDbConnection recoveryConnection =
                                    ReignDatabase.OpenCampaignConnection(campaignId))
                                {
                                    EnsureSocialSubjectWorkSchema(
                                        recoveryConnection);
                                    ReputationStore.UpdateSocialSubjectWorkQueue2(recoveryConnection, new Dictionary<string, object>
                                        {
                                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                            ["error"] = DialogueService.LimitText(ex.Message, 1000),
                                            ["campaign"] = campaignId,
                                            ["timeline"] = timelineId,
                                            ["subject"] = subjectId
                                        });
                                }
                                Thread.Sleep(25);
                            }
                        }
                        finally
                        {
                            Program.CampaignDataGate.ExitReadLock();
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref SocialOutcomeActiveWorkers);
            }
        }

        private static string ClaimSocialSubject(ReignDbConnection connection,
            string campaignId, string timelineId, string owner)
        {
            using (ReignDbTransaction transaction = connection.BeginTransaction())
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                Dictionary<string, object> row = ReputationStore.SelectSocialSubjectWorkQueue(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["ts"] = now
                    },transaction).FirstOrDefault();
                string subjectId = ReadString(row, "subject_id", "");
                if (string.IsNullOrWhiteSpace(subjectId))
                {
                    transaction.Commit();
                    return "";
                }
                ReputationStore.UpdateSocialSubjectWorkQueue3(connection, new Dictionary<string, object>
                    {
                        ["owner"] = owner, ["expires"] = now + 120,
                        ["ts"] = now, ["campaign"] = campaignId,
                        ["timeline"] = timelineId, ["subject"] = subjectId
                    },transaction);
                transaction.Commit();
                return subjectId;
            }
        }

        private static int ProcessSocialSubjectBatch(
            ReignDbConnection connection,
            Dictionary<string, object> socialCatalog,
            string campaignId, string timelineId, string subjectId)
        {
            List<Dictionary<string, object>> rows;
            int processed = 0;
            double lastWorldDay = 0d;
            string lastEventId = "";
            Dictionary<string, object> batchTelemetry =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<string> batchEventIds = new List<string>();
            {
                Dictionary<string, object> lane = ReputationStore.SelectSocialSubjectWorkQueue2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
                lastWorldDay = ReadDouble(lane, "last_world_day", 0d);
                lastEventId = ReadString(lane, "last_event_id", "");
                rows = ReignPostgreSqlDialect.IsPostgreSql(connection)
                    ? ReputationStore.SelectWorldHistoryEvents3(connection, new Dictionary<string, object>
                        {
                            ["timeline"] = timelineId,
                            ["subject"] = subjectId,
                            ["limit"] = SocialSubjectBatchSize
                        })
                    : ReputationStore.SelectWorldHistoryEvents4(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                    .Where(row => ReadFirstString(
                        TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                            ?? new Dictionary<string, object>(),
                        "subjectId", "heroId").Equals(subjectId,
                            StringComparison.OrdinalIgnoreCase))
                    .Take(SocialSubjectBatchSize).ToList();
                Stopwatch eventTransactionTimer = Stopwatch.StartNew();
                Interlocked.Increment(ref SocialOutcomeTransactionAttempts);
                ReputationStore.Begin(connection);
                try
                {
                    foreach (Dictionary<string, object> row in rows)
                    {
                        Dictionary<string, object> item =
                            new Dictionary<string, object>
                            {
                                ["eventId"] = ReadString(row, "event_id", ""),
                                ["worldDay"] = ReadDouble(row, "world_day", 0d),
                                ["eventType"] = ReadString(row, "event_type", ""),
                                ["summary"] = ReadString(row, "summary", ""),
                                ["payload"] = TryParseJsonObject(ReadString(row,
                                    "payload_json", "{}"))
                                    ?? new Dictionary<string, object>()
                            };
                        ExecuteTransactionControlSql(connection,
                            "SAVEPOINT social_subject_event;");
                        try
                        {
                            string batchEventId = ReadString(row, "event_id", "");
                            ProcessSocialWorldHistoryEvent(campaignId,
                                timelineId, item, false, connection, true,
                                socialCatalog, (chunkKey, counters) =>
                                    MergeSocialOutcomeTelemetry(
                                        batchTelemetry, counters));
                            ExecuteTransactionControlSql(connection,
                                "RELEASE SAVEPOINT social_subject_event;");
                            processed++;
                            batchEventIds.Add(batchEventId);
                            lastWorldDay = ReadDouble(row, "world_day",
                                lastWorldDay);
                            lastEventId = ReadString(row, "event_id",
                                lastEventId);
                        }
                        catch (Exception ex)
                        {
                            ExecuteTransactionControlSql(connection,
                                "ROLLBACK TO SAVEPOINT social_subject_event;");
                            ExecuteTransactionControlSql(connection,
                                "RELEASE SAVEPOINT social_subject_event;");
                            RecordSocialWorldHistoryFailure(campaignId,
                                timelineId, ReadString(row, "event_id", ""),
                                ex, connection);
                        }
                    }
                    if (batchTelemetry.Count > 0 && batchEventIds.Count > 0)
                    {
                        string telemetryKey = "social_subject_batch_"
                            + DeterministicSocialId(string.Join("|",
                                campaignId, timelineId, subjectId,
                                batchEventIds.First(), batchEventIds.Last()));
                        WorldSimulationService.RecordWorldTestCounterSchemaReady(connection,
                            campaignId, timelineId,
                            (int)Math.Floor(lastWorldDay + 0.000001d),
                            "rumors", telemetryKey, batchTelemetry);
                    }
                    ReputationStore.Commit(connection);
                    eventTransactionTimer.Stop();
                    RecordSocialOutcomeTransactionDuration(
                        eventTransactionTimer.ElapsedMilliseconds,
                        Math.Max(1, rows.Count));
                }
                catch
                {
                    try { ReputationStore.Rollback(connection); } catch { }
                    throw;
                }
                if (processed > 0)
                {
                    ReputationStore.UpdateSocialSubjectWorkQueue4(connection, new Dictionary<string, object>
                        {
                            ["day"] = lastWorldDay, ["event"] = lastEventId,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId,
                            ["subject"] = subjectId
                        });
                }
                bool more = HasPendingSocialWorldHistoryOutcomesForSubject(
                    connection, timelineId, subjectId);
                ReputationStore.UpdateSocialSubjectWorkQueue5(connection, new Dictionary<string, object>
                    {
                        ["status"] = more ? "pending" : "completed",
                        ["count"] = more ? 1 : 0,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["subject"] = subjectId
                    });
            }
            if (processed > 0) SchedulePendingReputationReasonJobs(campaignId);
            return processed;
        }

        private static void FinalizeCompletedSocialSubjectLanes(
            string campaignId, string timelineId)
        {
            Dictionary<string, object> socialCatalog = ReadGlobalSocialCatalog();
            while (!Program.ShutdownRequested)
            {
                List<Dictionary<string, object>> lanes;
                using (ReignDbConnection readConnection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureSocialSubjectWorkSchema(readConnection);
                    lanes = ReputationStore.SelectSocialSubjectWorkQueue3(readConnection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId,
                        ["limit"] = SocialSubjectFinalizationBatchSize
                    });
                }
                if (lanes.Count == 0) return;

                Program.CampaignDataGate.EnterReadLock();
                try
                {
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        ReputationStore.Begin(connection);
                        try
                        {
                            foreach (Dictionary<string, object> lane in lanes)
                            {
                                string subjectId = ReadString(lane,
                                    "subject_id", "");
                                double worldDay = ReadDouble(lane,
                                    "last_world_day", 0d);
                                string eventId = ReadString(lane,
                                    "last_event_id", "");
                                RecomputeDerivedSocialReputations(connection,
                                    campaignId, timelineId, subjectId,
                                    worldDay, eventId, socialCatalog);
                                ReconcileSocialRelationshipsForSubjects(
                                    connection, campaignId, timelineId,
                                    new[] { subjectId }, worldDay, true, true);
                                ReputationStore.UpdateSocialSubjectWorkQueue6(connection, new Dictionary<string, object>
                                    {
                                        ["ts"] = DateTimeOffset.UtcNow
                                            .ToUnixTimeSeconds(),
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["subject"] = subjectId
                                    });
                            }
                            ReputationStore.Commit(connection);
                        }
                        catch
                        {
                            try { ReputationStore.Rollback(connection); }
                            catch { }
                            throw;
                        }
                    }
                }
                finally
                {
                    Program.CampaignDataGate.ExitReadLock();
                }
            }
        }

        private static void RecordSocialOutcomeTransactionDuration(
            long value, int eventCount)
        {
            long observed = Interlocked.Read(ref SocialOutcomeLastTransactionMs);
            while (value > observed)
            {
                long prior = Interlocked.CompareExchange(
                    ref SocialOutcomeLastTransactionMs, value, observed);
                if (prior == observed) break;
                observed = prior;
            }
            long perEvent = value / Math.Max(1, eventCount);
            observed = Interlocked.Read(
                ref SocialOutcomeLastTransactionPerEventMs);
            while (perEvent > observed)
            {
                long prior = Interlocked.CompareExchange(
                    ref SocialOutcomeLastTransactionPerEventMs,
                    perEvent, observed);
                if (prior == observed) return;
                observed = prior;
            }
        }

        private static void ExecuteTransactionControlSql(
            ReignDbConnection connection, string sql)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                // These statements intentionally run inside the transaction
                // already opened for the subject batch. The legacy PostgreSQL
                // compatibility normalizer wraps standalone savepoints in its
                // own BEGIN/COMMIT pair, which would defeat batching here.
                command.CommandText = sql;
                command.CommandTimeout = 60;
                command.ExecuteNonQuery();
            }
        }

        private static void MergeSocialOutcomeTelemetry(
            Dictionary<string, object> aggregate,
            Dictionary<string, object> counters)
        {
            if (aggregate == null || counters == null) return;
            foreach (KeyValuePair<string, object> item in counters)
            {
                if (item.Value == null) continue;
                if (item.Value is byte || item.Value is short
                    || item.Value is int || item.Value is long)
                {
                    long prior = aggregate.TryGetValue(item.Key,
                        out object existing)
                        ? Convert.ToInt64(existing, CultureInfo.InvariantCulture)
                        : 0L;
                    aggregate[item.Key] = prior + Convert.ToInt64(
                        item.Value, CultureInfo.InvariantCulture);
                }
                else if (item.Value is float || item.Value is double
                    || item.Value is decimal)
                {
                    double prior = aggregate.TryGetValue(item.Key,
                        out object existing)
                        ? Convert.ToDouble(existing, CultureInfo.InvariantCulture)
                        : 0d;
                    aggregate[item.Key] = prior + Convert.ToDouble(
                        item.Value, CultureInfo.InvariantCulture);
                }
                else
                {
                    aggregate[item.Key] = item.Value;
                }
            }
        }

        private static bool IsSocialOutcomeTransactionConflict(Exception ex)
        {
            string text = (ex == null ? "" : ex.ToString())
                .ToLowerInvariant();
            return text.Contains("40p01")
                || text.Contains("40001")
                || text.Contains("55p03")
                || text.Contains("deadlock detected")
                || text.Contains("could not serialize")
                || text.Contains("lock not available");
        }

        private static bool HasPendingSocialWorldHistoryOutcomesForSubject(
            ReignDbConnection connection, string timelineId, string subjectId)
        {
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                return ReputationStore.SelectWorldHistoryEvents5(connection, new Dictionary<string, object>
                {
                    ["timeline"] = timelineId,
                    ["subject"] = subjectId
                }).Count > 0;
            }
            return ReputationStore.SelectWorldHistoryEvents6(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                .Any(row => ReadFirstString(
                    TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                        ?? new Dictionary<string, object>(),
                    "subjectId", "heroId").Equals(subjectId,
                        StringComparison.OrdinalIgnoreCase));
        }

        private static void ProcessPendingSocialWorldHistoryOutcomeBatch(
            string campaignId, string timelineId, string workerKey)
        {
            try
            {
                Program.CampaignDataGate.EnterReadLock();
                try
                {
                    List<Dictionary<string, object>> pending;
                    bool processedAny = false;
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        WorldSimulationService.EnsureWorldHistorySchema(connection);
                        EnsureSocialReputationSchema(connection);
                        pending = ReputationStore.SelectWorldHistoryEvents7(connection, new Dictionary<string, object>
                            {
                                ["timeline"] = timelineId,
                                ["limit"] = SocialWorldHistoryBatchSize
                            });
                    }
                    foreach (Dictionary<string, object> row in pending)
                    {
                        if (OperationsService.PriorityBackgroundWorkShouldYield()) break;
                        Dictionary<string, object> item = new Dictionary<string, object>
                        {
                            ["eventId"] = ReadString(row, "event_id", ""),
                            ["worldDay"] = ReadDouble(row, "world_day", 0d),
                            ["eventType"] = ReadString(row, "event_type", ""),
                            ["summary"] = ReadString(row, "summary", ""),
                            ["payload"] = TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                                ?? new Dictionary<string, object>()
                        };
                        try
                        {
                            ProcessSocialWorldHistoryEvent(campaignId, timelineId, item, false);
                            processedAny = true;
                        }
                        catch (Exception ex)
                        {
                            RecordSocialWorldHistoryFailure(campaignId, timelineId,
                                ReadString(row, "event_id", ""), ex);
                            OperationsService.LogOperational("world_history.social_outcome_failed",
                                new Dictionary<string, object>
                                {
                                    ["campaignId"] = campaignId,
                                    ["timelineId"] = timelineId,
                                    ["eventId"] = ReadString(row, "event_id", ""),
                                    ["error"] = DialogueService.LimitText(ex.Message, 800)
                                });
                        }
                    }
                    if (processedAny)
                        SchedulePendingReputationReasonJobs(campaignId);
                }
                finally
                {
                    Program.CampaignDataGate.ExitReadLock();
                }
            }
            finally
            {
                lock (PendingSocialWorldHistoryLock)
                    PendingSocialWorldHistoryWorkers.Remove(workerKey);
                if (HasPendingSocialWorldHistoryOutcomes(campaignId, timelineId))
                    SchedulePendingSocialWorldHistoryOutcomes(campaignId, timelineId);
            }
        }

        private static bool HasPendingSocialWorldHistoryOutcomes(
            string campaignId, string timelineId)
        {
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    return ReputationStore.SelectWorldHistoryEvents8(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId
                    }).Any();
                }
            }
            catch { return false; }
        }
    }
}
