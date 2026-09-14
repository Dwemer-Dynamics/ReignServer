using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void EnsureMemoryBackgroundWorkSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_background_jobs (
job_id TEXT PRIMARY KEY,
job_type TEXT NOT NULL,
owner_id TEXT NOT NULL DEFAULT '',
group_key TEXT NOT NULL DEFAULT '',
session_id TEXT NOT NULL DEFAULT '',
priority TEXT NOT NULL DEFAULT 'maintenance',
status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',
result_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,
updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_memory_background_jobs_status ON memory_background_jobs(status,created_ts);");
        }

        private static Dictionary<string, object> ScheduleMemoryConsolidation(string campaignId, string ownerId, string groupKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "owner_id_missing" };
            string jobId = "consolidate_" + PromptHash(campaignId + "|" + ownerId + "|" + groupKey).Substring(0, 24);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMemoryBackgroundWorkSchema(connection);
                ExecuteSql(connection, @"INSERT INTO memory_background_jobs(job_id,job_type,owner_id,group_key,session_id,priority,status,attempt_count,last_error,result_json,created_ts,updated_ts)
VALUES($id,'memory_consolidation',$owner,$group,$session,'maintenance','pending',0,'','{}',$ts,$ts)
ON CONFLICT(job_id) DO UPDATE SET status=CASE WHEN memory_background_jobs.status='completed' THEN memory_background_jobs.status ELSE 'pending' END,updated_ts=$ts;",
                    new Dictionary<string, object> { ["id"] = jobId, ["owner"] = ownerId, ["group"] = groupKey ?? "", ["session"] = sessionId ?? "", ["ts"] = ts });
            }
            EnqueuePriorityBackgroundWork("memory.consolidation", "maintenance", () => ProcessMemoryBackgroundJob(campaignId, jobId));
            return new Dictionary<string, object> { ["ok"] = true, ["queued"] = true, ["jobId"] = jobId, ["ownerId"] = ownerId, ["groupKey"] = groupKey ?? "" };
        }

        private static void ProcessMemoryBackgroundJob(string campaignId, string jobId)
        {
            Dictionary<string, object> job;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureMemoryBackgroundWorkSchema(connection);
                job = QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = jobId }).FirstOrDefault();
                if (job == null || ReadString(job, "status", "") == "completed") return;
                ExecuteSql(connection, "UPDATE memory_background_jobs SET status='running',attempt_count=attempt_count+1,updated_ts=$ts WHERE job_id=$id;",
                    new Dictionary<string, object> { ["id"] = jobId, ["ts"] = ts });
            }
            try
            {
                Dictionary<string, object> result = ConsolidateNpcMemory(campaignId, ReadString(job, "owner_id", ""), false, ReadString(job, "group_key", ""));
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    ExecuteSql(connection, "UPDATE memory_background_jobs SET status='completed',result_json=$result,last_error='',updated_ts=$ts WHERE job_id=$id;",
                        new Dictionary<string, object> { ["id"] = jobId, ["result"] = Json.Serialize(result), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            }
            catch (Exception ex)
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    ExecuteSql(connection, "UPDATE memory_background_jobs SET status='failed',last_error=$error,updated_ts=$ts WHERE job_id=$id;",
                        new Dictionary<string, object> { ["id"] = jobId, ["error"] = LimitText(ex.Message, 1000), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                throw;
            }
        }

        private static void ResumePendingMemoryBackgroundJobs()
        {
            EnqueuePriorityBackgroundWork("memory.resume_pending", "maintenance", () =>
            {
                foreach (Dictionary<string, object> campaign
                    in ReignPostgreSqlStorage.ListCampaignMetadata())
                {
                    if (PriorityBackgroundWorkShouldYield()) return;
                    string campaignId = ReadString(campaign, "campaignId", "");
                    if (!HasLocalCampaignMetadata(campaignId)) continue;
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        EnsureMemoryBackgroundWorkSchema(connection);
                        foreach (string jobId in QuerySql(connection, "SELECT job_id FROM memory_background_jobs WHERE status IN ('pending','running','failed') AND attempt_count<4 ORDER BY created_ts LIMIT 50;")
                            .Select(row => ReadString(row, "job_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).ToList())
                            EnqueuePriorityBackgroundWork("memory.consolidation.resume", "maintenance", () => ProcessMemoryBackgroundJob(campaignId, jobId));
                    }
                }
            });
        }

        private static Dictionary<string, object> MemoryBackgroundStatusApi(Dictionary<string, string> query)
        {
            string campaignId = query != null && query.TryGetValue("campaignId", out string requested) ? requested : ResolveLogCampaignId("");
            if (string.IsNullOrWhiteSpace(campaignId)
                || !HasCampaignPostgreSqlStorage(campaignId))
                return new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaignId, ["jobs"] = new List<Dictionary<string, object>>() };
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                List<Dictionary<string, object>> jobs = QuerySql(connection, "SELECT * FROM memory_background_jobs ORDER BY updated_ts DESC LIMIT 100;");
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["campaignId"] = campaignId, ["jobs"] = jobs,
                    ["pending"] = jobs.Count(row => ReadString(row, "status", "") == "pending"),
                    ["running"] = jobs.Count(row => ReadString(row, "status", "") == "running"),
                    ["failed"] = jobs.Count(row => ReadString(row, "status", "") == "failed")
                };
            }
        }
    }
}
