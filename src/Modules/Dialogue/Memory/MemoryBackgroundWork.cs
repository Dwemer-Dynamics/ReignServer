using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static Timer MemoryReconcileTimer;
        private static int MemoryReconcileQueued;
        private sealed class MemoryJobFence { internal string JobId, Token, Generation; }
        private static readonly AsyncLocal<MemoryJobFence> CurrentMemoryJob = new AsyncLocal<MemoryJobFence>();

        internal static void EnsureMemoryBackgroundWorkSchema(ReignDbConnection connection)
        {
            DialogueSchema.CreateMemoryBackgroundJobs(connection);
            foreach (var column in new Dictionary<string, string> {
                ["lease_token"] = "TEXT NOT NULL DEFAULT ''", ["lease_until"] = "BIGINT NOT NULL DEFAULT 0",
                ["next_attempt_ts"] = "BIGINT NOT NULL DEFAULT 0", ["source_hash"] = "TEXT NOT NULL DEFAULT ''",
                ["processing_version"] = "TEXT NOT NULL DEFAULT 'legacy'", ["restore_generation"] = "TEXT NOT NULL DEFAULT ''" })
                DialogueSchema.EnsureDatabaseColumn(connection, "memory_background_jobs", column.Key, column.Value);
            DialogueSchema.CreateIndexMemoryBackgroundDue(connection);
        }

        private static List<Dictionary<string, object>> ReadMemoryJobSources(
            ReignDbConnection connection, string type, string owner, string session)
        {
            if (type == "scene_summary")
                return DialogueStore.SelectConversationTurns58(connection, new Dictionary<string, object> { ["session"] = session });
            return DialogueStore.SelectMemories8(connection, new Dictionary<string, object> { ["owner"] = owner });
        }

        private static string EnqueueMemoryJob(ReignDbConnection connection, string campaign, string type, string owner, string group, string session)
        {
            string sourceHash = MemorySourceHash(ReadMemoryJobSources(connection, type, owner, session));
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            string jobId = "memory_" + PromptHash(campaign + "|" + type + "|" + owner + "|" + group + "|" + session
                + "|" + sourceHash + "|" + MemoryPrecisionVersion + "|" + generation).Substring(0, 32);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            DialogueStore.InsertOrIgnoreMemoryBackgroundJobs(connection, new Dictionary<string, object> { ["id"] = jobId, ["type"] = type, ["owner"] = owner ?? "", ["group"] = group ?? "",
                    ["session"] = session ?? "", ["hash"] = sourceHash, ["version"] = MemoryPrecisionVersion,
                    ["generation"] = generation, ["ts"] = ts });
            return jobId;
        }

        private static Dictionary<string, object> ScheduleMemoryConsolidation(string campaignId, string ownerId, string groupKey, string sessionId)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "owner_id_missing" };
            string id;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                id = EnqueueMemoryJob(connection, campaignId, "memory_consolidation", ownerId, groupKey, sessionId);
            // Rejection leaves the row pending (no claim, no attempt consumed); the 60 s
            // reconciliation timer re-admits it.
            OperationsService.EnqueuePriorityBackgroundWork("memory.consolidation", "maintenance", () => ProcessMemoryBackgroundJob(campaignId, id));
            return new Dictionary<string, object> { ["ok"] = true, ["queued"] = true, ["jobId"] = id, ["ownerId"] = ownerId, ["groupKey"] = groupKey ?? "" };
        }

        private static Dictionary<string, object> ClaimMemoryJob(ReignDbConnection connection, string id, string token, long now)
        {
            // One UPDATE claims the lease; enqueue never revives completed work.
            return DialogueStore.UpdateMemoryBackgroundJobs(connection, new Dictionary<string, object> { ["id"] = id, ["token"] = token, ["until"] = now + 120, ["now"] = now }).SingleOrDefault();
        }

        internal static void VerifyMemoryPublicationFence(ReignDbConnection connection)
        {
            AcquirePostgreSqlTransactionMutationLock(connection);
            var fence = CurrentMemoryJob.Value;
            if (fence == null) return;
            // Publication holds this row lock through commit. A reassigned worker
            // cannot publish its old provider result after a lease expiry/rewind.
            var row = DialogueStore.SelectMemoryBackgroundJobs2(connection, new Dictionary<string, object> { ["id"] = fence.JobId }).SingleOrDefault();
            if (row == null || ReadString(row, "status", "") != "running" || ReadString(row, "lease_token", "") != fence.Token
                || ReadLong(row, "lease_until", 0) <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                || ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "") != fence.Generation)
                throw new InvalidOperationException("Memory publication rejected: lease or restored source generation changed.");
        }

        private static void RenewMemoryLease(string campaign, string id, string token)
        {
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    DialogueStore.UpdateMemoryBackgroundJobs2(connection, new Dictionary<string, object> { ["id"] = id, ["token"] = token, ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["until"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120 });
            }
            catch (Exception ex) { OperationsService.LogOperational("memory.lease_renew_failed", new Dictionary<string, object> { ["jobId"] = id, ["error"] = DialogueService.LimitText(ex.Message, 300) }); }
        }

        private static void ProcessMemoryBackgroundJob(string campaignId, string jobId)
        {
            if (OperationsService.PriorityBackgroundWorkShouldYield()) return;
            string token = Guid.NewGuid().ToString("N");
            Dictionary<string, object> job;
            using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                job = ClaimMemoryJob(connection, jobId, token, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (job == null) return;
            var previousFence = CurrentMemoryJob.Value;
            CurrentMemoryJob.Value = new MemoryJobFence { JobId = jobId, Token = token, Generation = ReadString(job, "restore_generation", "") };
            using (var heartbeat = new Timer(_ => RenewMemoryLease(campaignId, jobId, token), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)))
            {
                try
                {
                    string type = ReadString(job, "job_type", ""), owner = ReadString(job, "owner_id", ""), sessionId = ReadString(job, "session_id", "");
                    string groupKey = ReadString(job, "group_key", "");
                    List<Dictionary<string, object>> verifiedOwnerRows = null;
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        var sourceRows = ReadMemoryJobSources(connection, type, owner, sessionId);
                        if (ReadString(job, "source_hash", "") != MemorySourceHash(sourceRows)
                            || CurrentMemoryJob.Value.Generation != ReadString(ReadMemoryPrecisionState(connection), "restore_generation", ""))
                        {
                            FinishMemoryJob(connection, jobId, token, "superseded", new Dictionary<string, object> { ["reason"] = "source_changed" });
                            EnqueueMemoryJob(connection, campaignId, type, owner, groupKey, sessionId);
                            return;
                        }
                        if (type == "memory_consolidation" && !string.IsNullOrWhiteSpace(groupKey))
                            verifiedOwnerRows = sourceRows;
                    }
                    Dictionary<string, object> result;
                    if (type == "scene_summary")
                    {
                        Dictionary<string, object> session;
                        List<Dictionary<string, object>> turns;
                        using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                        {
                            session = DialogueStore.SelectConversationSessions2(connection, new Dictionary<string, object> { ["id"] = sessionId }).Single();
                            turns = DialogueStore.SelectConversationTurns50(connection, new Dictionary<string, object> { ["id"] = sessionId });
                        }
                        var playerTurns = turns.Where(t => ReadString(t, "role", "") == "player").ToList();
                        bool recall = playerTurns.Count > 0 && playerTurns.All(t => IsPureConversationRecallRequest(ReadString(t, "text", "")));
                        result = CreateConversationSceneSummary(campaignId, session, turns, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), null, !recall);
                        if (!recall && !string.IsNullOrWhiteSpace(ReadString(result,"summaryId","")))
                        {
                            using (var followupConnection = ReignDatabase.OpenCampaignConnection(campaignId))
                                foreach (string participant in DialogueService.TextListFromJson(ReadString(session,"participants_json","[]"))
                                    .Where(p => p != ReadString(session,"player_id","")).Distinct(StringComparer.OrdinalIgnoreCase))
                                    EnqueueMemoryJob(followupConnection,campaignId,"memory_consolidation",participant,"conversation:"+sessionId,sessionId);
                            result["arc"] = UpdateRollingConversationArc(campaignId,owner,ReadString(session,"player_id",""),
                                ReadString(result,"memoryLane","interpersonal_history"),DateTimeOffset.UtcNow.ToUnixTimeSeconds(),ReadDictionary(result,"knowledgeBoundary"));
                            result["continuityArcRepairs"] = RepairExistingRollingConversationArcsAfterReengagement(campaignId,owner,ReadString(session,"player_id",""),DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                        }
                    }
                    else if (type == "memory_consolidation")
                        result = DialogueService.ConsolidateNpcMemory(campaignId, owner, false, groupKey, verifiedOwnerRows);
                    else throw new InvalidOperationException("Unknown memory job type.");
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        CompleteMemoryCompactionJob(connection, jobId, token, ReadInt(job, "attempt_count", 1), type, result);
                    }
                }
                catch (Exception ex)
                {
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                        RetryMemoryJob(connection, jobId, token, ReadInt(job, "attempt_count", 1), DialogueService.LimitText(ex.Message, 1000), new Dictionary<string, object>());
                    OperationsService.LogOperational("memory.job_failed", new Dictionary<string, object> { ["jobId"] = jobId, ["error"] = DialogueService.LimitText(ex.Message, 500) });
                }
                finally { CurrentMemoryJob.Value = previousFence; }
            }
        }

        private static void CompleteMemoryCompactionJob(ReignDbConnection connection, string id, string token,
            int attempt, string type, Dictionary<string, object> result)
        {
            string compactionStatus = ReadString(result, "compactionStatus", "");
            bool incomplete = compactionStatus == "incomplete";
            if (incomplete && !ReadBool(result, "retryable", true))
                FinishMemoryJob(connection, id, token, "incomplete", result);
            else if (incomplete)
                RetryMemoryJob(connection, id, token, attempt, "Compaction incomplete; original sources retained.", result);
            else if (compactionStatus == "partial")
                FinishMemoryJob(connection, id, token, "partial", result);
            else FinishMemoryJob(connection, id, token,
                ReadString(result, "reason", "") == "empty_session"
                    || (type == "memory_consolidation" && ReadInt(result, "summaryCount", 0) == 0) ? "no_op" : "completed", result);
        }

        private static void FinishMemoryJob(ReignDbConnection connection, string id, string token, string status, Dictionary<string, object> result) =>
            DialogueStore.UpdateMemoryBackgroundJobs3(connection, new Dictionary<string, object> {
                ["id"] = id, ["token"] = token, ["status"] = status, ["result"] = Json.Serialize(result), ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        private static void RetryMemoryJob(ReignDbConnection connection, string id, string token, int attempt, string error, Dictionary<string, object> result) =>
            DialogueStore.UpdateMemoryBackgroundJobs4(connection, new Dictionary<string, object> {
                ["id"] = id, ["token"] = token, ["status"] = attempt >= 4 ? "incomplete" : "failed", ["error"] = error,
                ["result"] = Json.Serialize(result), ["next"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 30L * (1L << Math.Min(6, attempt)),
                ["now"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        internal static void ResumePendingMemoryBackgroundJobs()
        {
            if (MemoryReconcileTimer == null)
                MemoryReconcileTimer = new Timer(_ => QueueMemoryReconciliation(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
            QueueMemoryReconciliation();
        }

        private static void QueueMemoryReconciliation()
        {
            if (Interlocked.Exchange(ref MemoryReconcileQueued, 1) != 0) return;
            // MemoryReconcileQueued is cleared exactly once: by the callback's finally after it
            // runs, or by the not-started release when admission is refused or the queued
            // callback is canceled before start. The timer then retries on its next tick.
            OperationsService.TryEnqueuePriorityBackgroundWork("memory.reconcile", "maintenance", () => {
                try
                {
                    foreach (var campaign in ReignPostgreSqlStorage.ListCampaignMetadata())
                    {
                        if (OperationsService.PriorityBackgroundWorkShouldYield()) return;
                        string campaignId = ReadString(campaign, "campaignId", "");
                        if (!CampaignFileService.HasLocalCampaignMetadata(campaignId)) continue;
                        using (var connection = ReignDatabase.OpenCampaignConnection(campaignId))
                        {
                            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                            DialogueStore.UpdateMemoryBackgroundJobs5(connection, new Dictionary<string, object> { ["now"] = now });
                            foreach (var legacy in DialogueStore.SelectMemoryBackgroundJobs3(connection))
                            {
                                EnqueueMemoryJob(connection, campaignId, ReadString(legacy, "job_type", "memory_consolidation"), ReadString(legacy, "owner_id", ""), ReadString(legacy, "group_key", ""), ReadString(legacy, "session_id", ""));
                                DialogueStore.UpdateMemoryBackgroundJobs6(connection, new Dictionary<string, object> { ["id"] = ReadString(legacy, "job_id", "") });
                            }
                            foreach (string id in DialogueStore.SelectMemoryBackgroundJobs4(connection, new Dictionary<string, object> { ["now"] = now })
                                .Select(r => ReadString(r, "job_id", "")).ToList())
                            {
                                // A refused resume leaves its row pending and unclaimed; the next tick retries.
                                if (OperationsService.TryEnqueuePriorityBackgroundWork("memory.resume", "maintenance",
                                    () => ProcessMemoryBackgroundJob(campaignId, id), null, out _) != PriorityWorkAdmission.Accepted) break;
                            }
                         }
                         ReconcileMemorySpecialistIndex(campaignId);
                    }
                }
                finally { Interlocked.Exchange(ref MemoryReconcileQueued, 0); }
            }, _ => Interlocked.Exchange(ref MemoryReconcileQueued, 0), out _);
        }

        internal static Dictionary<string, object> MemoryBackgroundStatusApi(Dictionary<string, string> query)
        {
            string campaign = query != null && query.TryGetValue("campaignId", out string requested) ? requested : CampaignFileService.ResolveLogCampaignId("");
            if (string.IsNullOrWhiteSpace(campaign) || !DialogueService.HasCampaignPostgreSqlStorage(campaign)) return new Dictionary<string, object> { ["ok"] = true, ["jobs"] = new List<object>() };
            using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
            {
                var counts = DialogueStore.SelectMemoryBackgroundJobs5(connection);
                var jobs = DialogueStore.SelectMemoryBackgroundJobs6(connection);
                var result = new Dictionary<string, object> { ["ok"] = true, ["campaignId"] = campaign,
                    ["jobs"] = jobs,
                    ["counts"] = counts, ["precision"] = ReadMemoryPrecisionState(connection), ["leaseSeconds"] = 120,
                    ["heartbeatSeconds"] = 30, ["reconciliationSeconds"] = 60,
                    ["effectiveMemoryMode"] = ResolveMemoryMode(SettingsService.LoadSettings()),
                    ["referenceGraphMode"] = ConversationReferenceMode() };
                foreach (string status in new[] { "pending", "running", "failed", "incomplete", "partial", "completed", "no_op" })
                    result[status] = ReadInt(counts.FirstOrDefault(r => ReadString(r, "status", "") == status), "count", 0);
                result["attentionRequired"] = ReadInt(result, "failed", 0) + ReadInt(result, "incomplete", 0);
                result["incompleteDiagnostics"] = MemoryIncompleteDiagnostics(jobs, ReadInt(result, "incomplete", 0));
                return result;
            }
        }

        private static Dictionary<string, object> MemoryIncompleteDiagnostics(List<Dictionary<string, object>> jobs, int total)
        {
            var inspected = jobs.Where(job => ReadString(job, "status", "") == "incomplete").ToList();
            var reasons = new Dictionary<string, int>(StringComparer.Ordinal);
            int nonRetryable = 0;
            foreach (var job in inspected)
            {
                var result = TryParseJsonObject(ReadString(job, "result_json", "{}")) ?? new Dictionary<string, object>();
                if (!ReadBool(result, "retryable", true)) nonRetryable++;
                string reason = ReadString(ReadDictionary(result, "compactionDiagnostic"), "reason", "");
                if (reason.Length == 0)
                    reason = string.Join("+", ReadDictionaryList(result, "summaries")
                        .Select(summary => ReadString(ReadDictionary(summary, "compactionDiagnostic"), "reason", "unknown"))
                        .Distinct().OrderBy(value => value, StringComparer.Ordinal));
                // Only diagnostic categories leave this projection, never source text.
                if (reason.Length == 0 || reason.Length > 200 || reason.Any(ch => !(ch >= 'a' && ch <= 'z') && ch != '_' && ch != '+')) reason = "unknown";
                reasons[reason] = (reasons.TryGetValue(reason, out int count) ? count : 0) + 1;
            }
            return TestDict("totalIncomplete", total, "inspectedIncompleteJobs", inspected.Count,
                "coverageComplete", inspected.Count == total, "coverage", "latest_100_jobs",
                "nonRetryableInspected", nonRetryable, "reasons", reasons.ToDictionary(pair => pair.Key, pair => (object)pair.Value, StringComparer.Ordinal),
                "sourceRetention", "Original source retention and retrieval eligibility are separate from compaction completion.");
        }
    }
}
