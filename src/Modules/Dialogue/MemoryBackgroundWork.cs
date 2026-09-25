using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Timer MemoryReconcileTimer;
        private static int MemoryReconcileQueued;
        private sealed class MemoryJobFence { internal string JobId, Token, Generation; }
        private static readonly AsyncLocal<MemoryJobFence> CurrentMemoryJob = new AsyncLocal<MemoryJobFence>();

        private static void EnsureMemoryBackgroundWorkSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_background_jobs (
job_id TEXT PRIMARY KEY,job_type TEXT NOT NULL,owner_id TEXT NOT NULL DEFAULT '',group_key TEXT NOT NULL DEFAULT '',
session_id TEXT NOT NULL DEFAULT '',priority TEXT NOT NULL DEFAULT 'maintenance',status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',result_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            foreach (var column in new Dictionary<string, string> {
                ["lease_token"] = "TEXT NOT NULL DEFAULT ''", ["lease_until"] = "BIGINT NOT NULL DEFAULT 0",
                ["next_attempt_ts"] = "BIGINT NOT NULL DEFAULT 0", ["source_hash"] = "TEXT NOT NULL DEFAULT ''",
                ["processing_version"] = "TEXT NOT NULL DEFAULT 'legacy'", ["restore_generation"] = "TEXT NOT NULL DEFAULT ''" })
                EnsureDatabaseColumn(connection, "memory_background_jobs", column.Key, column.Value);
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_memory_background_due ON memory_background_jobs(status,next_attempt_ts,lease_until);");
        }

        private static string MemoryJobSourceHash(ReignDbConnection connection, string type, string owner, string session)
        {
            if (type == "scene_summary")
                return MemorySourceHash(QuerySql(connection,
                    "SELECT * FROM conversation_turns WHERE session_id=$session AND status='active' ORDER BY turn_order;",
                    new Dictionary<string, object> { ["session"] = session }));
            return MemorySourceHash(QuerySql(connection,
                "SELECT * FROM memories WHERE owner_id=$owner AND status='active' ORDER BY memory_id;",
                new Dictionary<string, object> { ["owner"] = owner }));
        }

        private static string EnqueueMemoryJob(ReignDbConnection connection, string campaign, string type, string owner, string group, string session)
        {
            string sourceHash = MemoryJobSourceHash(connection, type, owner, session);
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            string jobId = "memory_" + PromptHash(campaign + "|" + type + "|" + owner + "|" + group + "|" + session
                + "|" + sourceHash + "|" + MemoryPrecisionVersion + "|" + generation).Substring(0, 32);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ExecuteSql(connection, @"INSERT INTO memory_background_jobs
(job_id,job_type,owner_id,group_key,session_id,status,source_hash,processing_version,restore_generation,created_ts,updated_ts)
VALUES($id,$type,$owner,$group,$session,'pending',$hash,$version,$generation,$ts,$ts) ON CONFLICT(job_id) DO NOTHING;",
                new Dictionary<string, object> { ["id"] = jobId, ["type"] = type, ["owner"] = owner ?? "", ["group"] = group ?? "",
                    ["session"] = session ?? "", ["hash"] = sourceHash, ["version"] = MemoryPrecisionVersion,
                    ["generation"] = generation, ["ts"] = ts });
            return jobId;
        }

        private static Dictionary<string, object> ScheduleMemoryConsolidation(string campaignId, string ownerId, string groupKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "owner_id_missing" };
            string id;
            using (var connection = OpenCampaignConnection(campaignId))
                id = EnqueueMemoryJob(connection, campaignId, "memory_consolidation", ownerId, groupKey, sessionId);
            EnqueuePriorityBackgroundWork("memory.consolidation", "maintenance", () => ProcessMemoryBackgroundJob(campaignId, id));
            return new Dictionary<string, object> { ["ok"] = true, ["queued"] = true, ["jobId"] = id, ["ownerId"] = ownerId, ["groupKey"] = groupKey ?? "" };
        }

        private static Dictionary<string, object> ClaimMemoryJob(ReignDbConnection connection, string id, string token, long now)
        {
            // One UPDATE claims the lease; enqueue never revives completed work.
            return QuerySql(connection, @"UPDATE memory_background_jobs SET status='running',attempt_count=attempt_count+1,
lease_token=$token,lease_until=$until,updated_ts=$now
WHERE job_id=$id AND attempt_count<4 AND next_attempt_ts<=$now
AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now)) RETURNING *;",
                new Dictionary<string, object> { ["id"] = id, ["token"] = token, ["until"] = now + 120, ["now"] = now }).SingleOrDefault();
        }

        private static void VerifyMemoryPublicationFence(ReignDbConnection connection)
        {
            AcquirePostgreSqlTransactionMutationLock(connection);
            var fence = CurrentMemoryJob.Value;
            if (fence == null) return;
            // Publication holds this row lock through commit. A reassigned worker
            // cannot publish its old provider result after a lease expiry/rewind.
            var row = QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id FOR UPDATE;",
                new Dictionary<string, object> { ["id"] = fence.JobId }).SingleOrDefault();
            if (row == null || ReadString(row, "status", "") != "running" || ReadString(row, "lease_token", "") != fence.Token
                || ReadLong(row, "lease_until", 0) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "") != fence.Generation)
                throw new InvalidOperationException("Memory publication rejected: lease or restored source generation changed.");
        }

        private static void RenewMemoryLease(string campaign, string id, string token)
        {
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "UPDATE memory_background_jobs SET lease_until=$until,updated_ts=$now WHERE job_id=$id AND status='running' AND lease_token=$token;",
                        new Dictionary<string, object> { ["id"] = id, ["token"] = token, ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["until"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120 });
            }
            catch (Exception ex) { LogOperational("memory.lease_renew_failed", new Dictionary<string, object> { ["jobId"] = id, ["error"] = LimitText(ex.Message, 300) }); }
        }

        private static void ProcessMemoryBackgroundJob(string campaignId, string jobId)
        {
            if (PriorityBackgroundWorkShouldYield()) return;
            string token = Guid.NewGuid().ToString("N");
            Dictionary<string, object> job;
            using (var connection = OpenCampaignConnection(campaignId))
                job = ClaimMemoryJob(connection, jobId, token, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (job == null) return;
            var previousFence = CurrentMemoryJob.Value;
            CurrentMemoryJob.Value = new MemoryJobFence { JobId = jobId, Token = token, Generation = ReadString(job, "restore_generation", "") };
            using (var heartbeat = new Timer(_ => RenewMemoryLease(campaignId, jobId, token), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)))
            {
                try
                {
                    string type = ReadString(job, "job_type", ""), owner = ReadString(job, "owner_id", ""), sessionId = ReadString(job, "session_id", "");
                    using (var connection = OpenCampaignConnection(campaignId))
                    {
                        if (ReadString(job, "source_hash", "") != MemoryJobSourceHash(connection, type, owner, sessionId)
                            || CurrentMemoryJob.Value.Generation != ReadString(ReadMemoryPrecisionState(connection), "restore_generation", ""))
                        {
                            FinishMemoryJob(connection, jobId, token, "superseded", new Dictionary<string, object> { ["reason"] = "source_changed" });
                            EnqueueMemoryJob(connection, campaignId, type, owner, ReadString(job, "group_key", ""), sessionId);
                            return;
                        }
                    }
                    Dictionary<string, object> result;
                    if (type == "scene_summary")
                    {
                        Dictionary<string, object> session;
                        List<Dictionary<string, object>> turns;
                        using (var connection = OpenCampaignConnection(campaignId))
                        {
                            session = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id;", new Dictionary<string, object> { ["id"] = sessionId }).Single();
                            turns = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id AND status='active' ORDER BY turn_order;", new Dictionary<string, object> { ["id"] = sessionId });
                        }
                        var playerTurns = turns.Where(t => ReadString(t, "role", "") == "player").ToList();
                        bool recall = playerTurns.Count > 0 && playerTurns.All(t => IsPureConversationRecallRequest(ReadString(t, "text", "")));
                        result = CreateConversationSceneSummary(campaignId, session, turns, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, !recall);
                        if (!recall && !string.IsNullOrWhiteSpace(ReadString(result,"summaryId","")))
                        {
                            using (var followupConnection = OpenCampaignConnection(campaignId))
                                foreach (string participant in TextListFromJson(ReadString(session,"participants_json","[]"))
                                    .Where(p => p != ReadString(session,"player_id","")).Distinct(StringComparer.OrdinalIgnoreCase))
                                    EnqueueMemoryJob(followupConnection,campaignId,"memory_consolidation",participant,"conversation:"+sessionId,sessionId);
                            result["arc"] = UpdateRollingConversationArc(campaignId,owner,ReadString(session,"player_id",""),
                                ReadString(result,"memoryLane","interpersonal_history"),DateTimeOffset.UtcNow.ToUnixTimeSeconds(),ReadDictionary(result,"knowledgeBoundary"));
                            result["continuityArcRepairs"] = RepairExistingRollingConversationArcsAfterReengagement(campaignId,owner,ReadString(session,"player_id",""),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        }
                    }
                    else if (type == "memory_consolidation")
                        result = ConsolidateNpcMemory(campaignId, owner, false, ReadString(job, "group_key", ""));
                    else throw new InvalidOperationException("Unknown memory job type.");
                    bool incomplete = ReadString(result, "compactionStatus", "") == "incomplete";
                    using (var connection = OpenCampaignConnection(campaignId))
                    {
                        if (incomplete) RetryMemoryJob(connection, jobId, token, ReadInt(job, "attempt_count", 1), "Compaction incomplete; original sources retained.", result);
                        else FinishMemoryJob(connection, jobId, token,
                            ReadString(result, "reason", "") == "empty_session" || (type == "memory_consolidation" && ReadInt(result, "summaryCount", 0) == 0) ? "no_op" : "completed", result);
                    }
                }
                catch (Exception ex)
                {
                    using (var connection = OpenCampaignConnection(campaignId))
                        RetryMemoryJob(connection, jobId, token, ReadInt(job, "attempt_count", 1), LimitText(ex.Message, 1000), new Dictionary<string, object>());
                    LogOperational("memory.job_failed", new Dictionary<string, object> { ["jobId"] = jobId, ["error"] = LimitText(ex.Message, 500) });
                }
                finally { CurrentMemoryJob.Value = previousFence; }
            }
        }

        private static void FinishMemoryJob(ReignDbConnection connection, string id, string token, string status, Dictionary<string, object> result) =>
            ExecuteSql(connection, @"UPDATE memory_background_jobs SET status=$status,result_json=$result,last_error='',lease_token='',lease_until=0,updated_ts=$now
WHERE job_id=$id AND lease_token=$token AND status='running';", new Dictionary<string, object> {
                ["id"] = id, ["token"] = token, ["status"] = status, ["result"] = Json.Serialize(result), ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        private static void RetryMemoryJob(ReignDbConnection connection, string id, string token, int attempt, string error, Dictionary<string, object> result) =>
            ExecuteSql(connection, @"UPDATE memory_background_jobs SET status=$status,last_error=$error,result_json=$result,lease_token='',lease_until=0,next_attempt_ts=$next,updated_ts=$now
WHERE job_id=$id AND lease_token=$token AND status='running';", new Dictionary<string, object> {
                ["id"] = id, ["token"] = token, ["status"] = attempt >= 4 ? "incomplete" : "failed", ["error"] = error,
                ["result"] = Json.Serialize(result), ["next"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30L * (1L << Math.Min(6, attempt)),
                ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        private static void ResumePendingMemoryBackgroundJobs()
        {
            if (MemoryReconcileTimer == null)
                MemoryReconcileTimer = new Timer(_ => QueueMemoryReconciliation(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            QueueMemoryReconciliation();
        }

        private static void QueueMemoryReconciliation()
        {
            if (Interlocked.Exchange(ref MemoryReconcileQueued, 1) != 0) return;
            EnqueuePriorityBackgroundWork("memory.reconcile", "maintenance", () => {
                try
                {
                    foreach (var campaign in ReignPostgreSqlStorage.ListCampaignMetadata())
                    {
                        if (PriorityBackgroundWorkShouldYield()) return;
                        string campaignId = ReadString(campaign, "campaignId", "");
                        if (!HasLocalCampaignMetadata(campaignId)) continue;
                        using (var connection = OpenCampaignConnection(campaignId))
                        {
                            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            ExecuteSql(connection, "UPDATE memory_background_jobs SET status='incomplete',last_error='Retry limit reached after lease expiry' WHERE status='running' AND lease_until<=$now AND attempt_count>=4;", new Dictionary<string, object> { ["now"] = now });
                            foreach (var legacy in QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE processing_version='legacy' AND status IN ('pending','running','failed');"))
                            {
                                EnqueueMemoryJob(connection, campaignId, ReadString(legacy, "job_type", "memory_consolidation"), ReadString(legacy, "owner_id", ""), ReadString(legacy, "group_key", ""), ReadString(legacy, "session_id", ""));
                                ExecuteSql(connection, "UPDATE memory_background_jobs SET status='superseded' WHERE job_id=$id;", new Dictionary<string, object> { ["id"] = ReadString(legacy, "job_id", "") });
                            }
                            foreach (string id in QuerySql(connection, @"SELECT job_id FROM memory_background_jobs WHERE attempt_count<4 AND next_attempt_ts<=$now
AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now)) ORDER BY created_ts LIMIT 50;", new Dictionary<string, object> { ["now"] = now })
                                .Select(r => ReadString(r, "job_id", "")).ToList())
                                EnqueuePriorityBackgroundWork("memory.resume", "maintenance", () => ProcessMemoryBackgroundJob(campaignId, id));
                        }
                    }
                }
                finally { Interlocked.Exchange(ref MemoryReconcileQueued, 0); }
            });
        }

        private static Dictionary<string, object> MemoryBackgroundStatusApi(Dictionary<string, string> query)
        {
            string campaign = query != null && query.TryGetValue("campaignId", out string requested) ? requested : ResolveLogCampaignId("");
            if (string.IsNullOrWhiteSpace(campaign) || !HasCampaignPostgreSqlStorage(campaign)) return new Dictionary<string, object> { ["ok"] = true, ["jobs"] = new List<object>() };
            using (var connection = OpenCampaignConnection(campaign))
            {
                var counts = QuerySql(connection, "SELECT status,COUNT(*) AS count FROM memory_background_jobs GROUP BY status;");
                var result = new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaign,
                    ["jobs"] = QuerySql(connection, "SELECT * FROM memory_background_jobs ORDER BY updated_ts DESC LIMIT 100;"),
                    ["counts"] = counts, ["precision"] = ReadMemoryPrecisionState(connection), ["leaseSeconds"] = 120,
                    ["heartbeatSeconds"] = 30, ["reconciliationSeconds"] = 60 };
                foreach (string status in new[] { "pending", "running", "failed", "incomplete", "completed", "no_op" })
                    result[status] = ReadInt(counts.FirstOrDefault(r => ReadString(r, "status", "") == status), "count", 0);
                return result;
            }
        }
    }
}
