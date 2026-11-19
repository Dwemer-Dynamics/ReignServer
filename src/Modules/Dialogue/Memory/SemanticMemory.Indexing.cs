using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static void SemanticIndexPump(CancellationToken token, int generation)
        {
            // A replaced (disable/re-enable) or shut-down pump may still be running; it must not
            // enumerate, open a campaign DB or spawn a worker once stopped.
            Func<bool> stopRequested = () => token.IsCancellationRequested || Program.ShutdownRequested;
            while (!stopRequested())
            {
                if (SemanticIndexShouldYield())
                {
                    if (token.WaitHandle.WaitOne(250)) break;
                    continue;
                }

                int processed = 0;
                try
                {
                    SemanticPumpLastCycleUtc = DateTime.UtcNow;
                    Dictionary<string, object> settings = Program.LoadSettings();
                    if (ReadBool(settings, "enableSemanticMemory", true) && !stopRequested())
                    {
                        StartManagedSemanticWorker(settings, stopRequested);
                        // ListCampaignMetadata runs EnsureInfrastructure: short read lease around enumeration only.
                        List<Dictionary<string, object>> campaigns = null;
                        Program.CampaignDataGate.EnterReadLock();
                        try
                        {
                            if (!stopRequested()) campaigns = ReignPostgreSqlStorage.ListCampaignMetadata();
                        }
                        finally { Program.CampaignDataGate.ExitReadLock(); }
                        if (campaigns != null)
                        foreach (Dictionary<string, object> campaign in campaigns)
                        {
                            if (stopRequested()) break;
                            if (SemanticIndexShouldYield()) break;
                            string campaignId =
                                ReadString(campaign, "campaignId", "");
                            if (Program.HasLocalCampaignMetadata(campaignId))
                            {
                                Program.CampaignDataGate.EnterReadLock();
                                try
                                {
                                    lock (SemanticJobProcessingLock)
                                    {
                                        // Rechecked after both locks: a stale acquisition after the
                                        // delete-all write barrier is refused before any DB open.
                                        // Breaking here still releases both locks before leaving.
                                        if (stopRequested()) break;
                                        processed += ProcessSemanticJobs(campaignId, settings);
                                    }
                                }
                                finally { Program.CampaignDataGate.ExitReadLock(); }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SemanticWorkerLastError = ex.Message;
                    LogSemanticFailureOnce("semantic.index_pump_failed", ex.Message);
                }
                if (processed > 0)
                {
                    SemanticPumpLastBatchUtc = DateTime.UtcNow;
                    SemanticPumpLastBatchSize = processed;
                }
                if (token.WaitHandle.WaitOne(processed > 0 ? 150 : 3000)) break;
            }
            Program.LogOperational("semantic.index_pump_stopped", new Dictionary<string, object>
            {
                ["generation"] = generation,
                ["cancelled"] = token.IsCancellationRequested,
                ["shutdown"] = Program.ShutdownRequested
            });
        }

        private static bool SemanticIndexShouldYield()
        {
            // A completed NPC response is followed by client-side persistence,
            // relationship receipts, assertion evidence, and then the next turn.
            // Keep the vector worker free across that handoff; the generic two-second
            // quiet period was short enough for an embedding batch to start in the
            // middle and make the next foreground semantic search wait several seconds.
            return Program.InteractiveRequestActiveOrRecent(10000)
                || Program.PriorityBackgroundWorkShouldYield();
        }

        private static int ProcessSemanticJobs(string campaignId, Dictionary<string, object> settings)
        {
            int batchSize = Math.Max(1, Math.Min(128, ReadInt(settings, "embeddingBatchSize", 32)));
            List<Dictionary<string, object>> jobs;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                jobs = ReignDatabase.QuerySql(connection, @"SELECT * FROM embedding_jobs
WHERE status='pending' AND next_attempt_ts<=$now
ORDER BY CASE WHEN operation='delete' THEN 0 ELSE 1 END,
CASE source_type
    WHEN 'summary' THEN 0
    WHEN 'conversation_turn' THEN 1
    WHEN 'memory' THEN 2
    WHEN 'event' THEN 3
    WHEN 'belief' THEN 4
    WHEN 'comprehension' THEN 5
    WHEN 'world_history_event' THEN 7
    ELSE 8 END,
    CASE WHEN source_type IN ('summary','conversation_turn','memory','event') THEN -enqueued_ts ELSE enqueued_ts END
LIMIT $limit;",
                    new Dictionary<string, object> { ["now"] = now, ["limit"] = batchSize });
            }
            if (jobs.Count == 0) return 0;

            int completedJobs = 0;
            List<EmbeddingSourceDocument> documents = new List<EmbeddingSourceDocument>();
            List<Dictionary<string, object>> deletes = new List<Dictionary<string, object>>();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                foreach (Dictionary<string, object> job in jobs)
                {
                    string sourceType = ReadString(job, "source_type", "");
                    string sourceId = ReadString(job, "source_id", "");
                    Dictionary<string, object> row = LoadEmbeddingSourceRow(connection, sourceType, sourceId);
                    if (ReadString(job, "operation", "upsert") == "delete" || row == null || !EmbeddingRowActive(sourceType, row))
                    {
                        deletes.Add(job);
                        continue;
                    }
                    string text = BuildCanonicalEmbeddingText(sourceType, row);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        CompleteEmbeddingJob(connection, sourceType, sourceId, "skipped", "empty canonical text");
                        completedJobs++;
                        continue;
                    }
                    EmbeddingSourceDocument document = BuildEmbeddingDocument(campaignId, sourceType, sourceId, row, text, settings);
                    document.Payload["restoreGeneration"] = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                    var restoredContext = ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM memory_restored_source_context WHERE state_id='current';").SingleOrDefault();
                    string restoredTimeline = ReadString(restoredContext, "timeline_id", "");
                    if (IsVerifiedRestoredMemorySource(connection, row, restoredTimeline))
                        document.Payload["restoredSavePointId"] = ReadString(RestoredMemorySourceContext(connection, restoredTimeline), "save_point_id", "");
                    document.ContentHash = SemanticSha256Hex(document.Text + CanonicalJson(document.Payload));
                    Dictionary<string, object> indexed = ReignDatabase.QuerySql(connection, @"SELECT content_hash,status FROM embedding_documents
WHERE source_type=$type AND source_id=$id AND model_version=$model AND provider=$provider LIMIT 1;",
                        new Dictionary<string, object> { ["type"] = sourceType, ["id"] = sourceId, ["model"] = SemanticModelVersion, ["provider"] = document.Provider }).FirstOrDefault();
                    if (indexed != null && string.Equals(ReadString(indexed, "content_hash", ""), document.ContentHash, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(ReadString(indexed, "status", ""), "indexed", StringComparison.OrdinalIgnoreCase))
                    {
                        using (var unchanged = connection.BeginTransaction())
                        {
                            AcquirePostgreSqlTransactionMutationLock(connection);
                            var latest = LoadEmbeddingSourceRow(connection,sourceType,sourceId);
                            if (latest != null && MemorySourceHash(new[] { latest }) == ReadString(document.Payload,"sourceHash","")
                                && ReadString(document.Payload,"restoreGeneration","") == ReadString(ReadMemoryPrecisionState(connection),"restore_generation",""))
                                CompleteEmbeddingJob(connection, sourceType, sourceId, "completed", "unchanged");
                            unchanged.Commit();
                        }
                        completedJobs++;
                        continue;
                    }
                    documents.Add(document);
                }
            }

            try
            {
                bool yieldedForInteractive = false;
                if (documents.Count > 0)
                {
                    // Submit small, interruptible chunks so an interactive search never
                    // waits behind a full maintenance batch. Indexed chunks are committed
                    // immediately; unprocessed documents remain pending for the next pump.
                    int safeChunkSize = Math.Max(1, Math.Min(2,
                        ReadInt(settings, "embeddingForegroundSafeChunkSize", 1)));
                    for (int offset = 0; offset < documents.Count; offset += safeChunkSize)
                    {
                        if (SemanticIndexShouldYield())
                        {
                            yieldedForInteractive = true;
                            break;
                        }
                        List<EmbeddingSourceDocument> chunk = documents
                            .Skip(offset).Take(safeChunkSize).ToList();
                        Dictionary<string, object> request = SemanticProviderRequest(settings);
                        request["collection"] = SemanticCollection;
                        request["documents"] = chunk.Select(document => new Dictionary<string, object>
                        {
                            ["id"] = document.VectorId,
                            ["text"] = document.Text,
                            ["payload"] = document.Payload
                        }).ToList();
                        Dictionary<string, object> response = TryParseJsonObject(
                            Program.PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/upsert"),
                                Json.Serialize(request), 60000));
                        if (!ReadBool(response, "ok", false))
                            throw new InvalidOperationException(
                                ReadString(response, "error", "Vector upsert failed."));
                        using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                        using (var publication = connection.BeginTransaction())
                        {
                            AcquirePostgreSqlTransactionMutationLock(connection);
                            foreach (EmbeddingSourceDocument document in chunk)
                            {
                                var current = LoadEmbeddingSourceRow(connection, document.SourceType, document.SourceId);
                                if (current == null || MemorySourceHash(new[] { current }) != ReadString(document.Payload, "sourceHash", "")
                                    || ReadString(document.Payload, "restoreGeneration", "") != ReadString(ReadMemoryPrecisionState(connection), "restore_generation", ""))
                                    continue; // Outbox remains pending; a stale vector cannot pass source revalidation.
                                MarkEmbeddingIndexed(connection, document, settings);
                            }
                            publication.Commit();
                        }
                        completedJobs += chunk.Count;
                    }
                }
                if (!yieldedForInteractive && deletes.Count > 0)
                {
                    List<string> ids;
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        ids = deletes.SelectMany(job => ReignDatabase.QuerySql(connection, "SELECT vector_id FROM embedding_documents WHERE source_type=$type AND source_id=$id AND status<>'deleted';",
                            new Dictionary<string, object> { ["type"] = ReadString(job, "source_type", ""), ["id"] = ReadString(job, "source_id", "") }))
                            .Select(row => ReadString(row, "vector_id", "")).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                    }
                    if (ids.Count > 0)
                    {
                        Dictionary<string, object> request = SemanticProviderRequest(settings);
                        request["collection"] = SemanticCollection;
                        request["ids"] = ids;
                        Program.PostJsonToUrl(SemanticWorkerUrl(settings, "/vectors/delete"), Json.Serialize(request), 15000);
                    }
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    using (var deletion = connection.BeginTransaction())
                    {
                        AcquirePostgreSqlTransactionMutationLock(connection);
                        foreach (Dictionary<string, object> job in deletes)
                        {
                            string type = ReadString(job, "source_type", ""), id = ReadString(job, "source_id", "");
                            var latest = LoadEmbeddingSourceRow(connection,type,id);
                            if (latest != null && EmbeddingRowActive(type,latest)) continue;
                            ReignDatabase.ExecuteSql(connection, "UPDATE embedding_documents SET status='deleted',updated_ts=$ts WHERE source_type=$type AND source_id=$id;",
                                new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["type"] = type, ["id"] = id });
                            CompleteEmbeddingJob(connection, type, id, "deleted", "");
                            completedJobs++;
                        }
                        deletion.Commit();
                    }
                }
                SemanticWorkerLastHealthyUtc = DateTime.UtcNow;
                SemanticWorkerUnavailableUntilUtc = DateTime.MinValue;
                SemanticWorkerLastError = "";
            }
            catch (Exception ex)
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    foreach (Dictionary<string, object> job in jobs) FailEmbeddingJob(connection, job, ex.Message);
                }
                SemanticWorkerLastError = ex.Message;
                LogSemanticFailureOnce("semantic.index_batch_failed", ex.Message);
            }
            return completedJobs;
        }

        private static Dictionary<string, object> LoadEmbeddingSourceRow(ReignDbConnection connection, string sourceType, string sourceId)
        {
            string table, id;
            switch (sourceType)
            {
                case "event": table = "events"; id = "event_id"; break;
                case "memory": table = "memories"; id = "memory_id"; break;
                case "summary": table = "summaries"; id = "summary_id"; break;
                case "belief": table = "beliefs"; id = "belief_id"; break;
                case "comprehension": table = "comprehension"; id = "comprehension_id"; break;
                case "conversation_turn": table = "conversation_turns"; id = "turn_id"; break;
                case "world_history_event": table = "world_history_events"; id = "event_id"; break;
                default: return null;
            }
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + id + "=$id LIMIT 1;", new Dictionary<string, object> { ["id"] = sourceId }).FirstOrDefault();
        }

        private static bool EmbeddingRowActive(string sourceType, Dictionary<string, object> row)
        {
            if (string.Equals(sourceType, "event", StringComparison.OrdinalIgnoreCase)
                && !EventEmbeddingEligible(row)) return false;
            if (string.Equals(sourceType, "world_history_event", StringComparison.OrdinalIgnoreCase)
                && !WorldHistoryEventEmbeddingEligible(row)) return false;
            string status = ReadString(row, "status", "active");
            if (string.Equals(sourceType, "memory", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "summary", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sourceType, "conversation_turn", StringComparison.OrdinalIgnoreCase))
            {
                return string.Equals(status, "active", StringComparison.OrdinalIgnoreCase)
                    && (sourceType != "conversation_turn" || !string.IsNullOrWhiteSpace(ConversationTurnTextForRecall(row)));
            }
            return !string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "superseded", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "consolidated", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "inactive", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCanonicalEmbeddingText(string sourceType, Dictionary<string, object> row)
        {
            List<string> parts = new List<string> { sourceType.Replace('_', ' ') + ":" };
            parts.Add(sourceType == "conversation_turn" ? ConversationTurnTextForRecall(row)
                : ReadFirstString(row, "semantic_text", "summary", "claim", "text", "description"));
            parts.Add(ReadFirstString(row, "event_type", "memory_type", "summary_type", "category", "stance", "channel"));
            parts.Add(ReadFirstString(row, "location_name", "location_id"));
            parts.Add(ReadString(row, "speaker_name", ""));
            if (sourceType == "conversation_turn")
            {
                parts.Add("speaker=" + ReadString(row, "speaker_id", ""));
                parts.Add("role=" + ReadString(row, "role", ""));
            }
            parts.Add(ReadString(row, "tags_json", ""));
            parts.Add(ReadString(row, "about_entities_json", ""));
            parts.Add(ReadString(row, "participants_json", ""));
            return string.Join(" ", parts.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray());
        }

        private static EmbeddingSourceDocument BuildEmbeddingDocument(string campaignId, string sourceType, string sourceId,
            Dictionary<string, object> row, string text, Dictionary<string, object> settings)
        {
            string provider = ReadString(settings, "vectorProvider", "local").ToLowerInvariant();
            var sourcePayload = Program.KnowledgePayload(row);
            string timeline = FirstNonEmpty(ReadString(row, "timeline_id", ""), ReadString(sourcePayload, "timelineId", ""));
            bool mental = sourceType == "belief" || sourceType == "comprehension";
            var knownBy = Program.TextListFromJson(ReadString(row, "known_by_json", "[]"));
            if (!mental) knownBy = knownBy.Concat(Program.TextListFromJson(ReadString(row, "participants_json", "[]")))
                .Concat(Program.TextListFromJson(ReadString(row, "witnesses_json", "[]")))
                .Concat(Program.TextListFromJson(ReadString(row, "heard_as_rumor_by_json", "[]"))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string vectorId = DeterministicSemanticGuid(campaignId + "|" + ReadString(row, "timeline_id", "") + "|" + sourceType + "|" + sourceId + "|" + SemanticModelVersion);
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["timelineId"] = timeline,
                ["sourceType"] = sourceType,
                ["sourceId"] = sourceId,
                ["memoryLane"] = FirstNonEmpty(ReadFirstString(row, "memory_lane", "memory_domain"), InferEmbeddingLane(sourceType, row)),
                ["ownerId"] = sourceType == "conversation_turn" ? ReadString(row, "speaker_id", "") : ReadFirstString(row, "owner_id", "believer_id"),
                ["knownBy"] = mental ? new List<string>() : knownBy,
                ["hiddenFrom"] = Program.TextListFromJson(ReadString(row, "hidden_from_json", "[]")),
                ["privateMental"] = mental, ["eligibilityVersion"] = 2,
                ["publicUnscoped"] = !mental && !Program.KnowledgeHasExplicitScope(row, sourcePayload)
                    && new[] { "public", "world", "global", "common" }.Contains(ReadString(row, "visibility", "private")),
                ["sourceHash"] = MemorySourceHash(new[] { row }),
                ["visibility"] = ReadString(row, "visibility", "private"),
                ["status"] = "active",
                ["worldDay"] = ReadDouble(row, "world_day", 0d),
                ["importance"] = ReadDouble(row, "importance", 0.5d),
                ["modelVersion"] = SemanticModelVersion
            };
            return new EmbeddingSourceDocument
            {
                CampaignId = campaignId, SourceType = sourceType, SourceId = sourceId, Provider = provider,
                TimelineId = timeline, VectorId = vectorId, Text = text,
                ContentHash = SemanticSha256Hex(text + CanonicalJson(payload)), Payload = payload
            };
        }

        private static string InferEmbeddingLane(string sourceType, Dictionary<string, object> row)
        {
            if (sourceType == "world_history_event" || sourceType == "event") return "world_affairs";
            if (sourceType == "belief") return "beliefs_and_rumors";
            if (sourceType == "conversation_turn") return "interpersonal_history";
            if (sourceType == "comprehension") return "personal_state";
            return ClassifyMemoryDomain(row);
        }

        private static string DeterministicSemanticGuid(string value)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""));
                byte[] guid = new byte[16];
                Array.Copy(bytes, guid, 16);
                return new Guid(guid).ToString("D");
            }
        }

        internal static string SemanticSha256Hex(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))).Replace("-", "").ToLowerInvariant();
        }

        private static void MarkEmbeddingIndexed(ReignDbConnection connection, EmbeddingSourceDocument document, Dictionary<string, object> settings)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO embedding_documents(source_type,source_id,model_version,provider,campaign_id,timeline_id,content_hash,vector_id,dimensions,status,attempt_count,last_error,updated_ts,payload_json)
VALUES($type,$id,$model,$provider,$campaign,$timeline,$hash,$vector,384,'indexed',0,'',$ts,$payload);",
                new Dictionary<string, object>
                {
                    ["type"] = document.SourceType, ["id"] = document.SourceId, ["model"] = SemanticModelVersion,
                    ["provider"] = document.Provider, ["campaign"] = document.CampaignId, ["timeline"] = document.TimelineId,
                    ["hash"] = document.ContentHash, ["vector"] = document.VectorId, ["ts"] = now, ["payload"] = Json.Serialize(document.Payload)
                });
            if (document.SourceType == "event") ReignDatabase.ExecuteSql(connection, "UPDATE events SET vector_id=$vector,embedding_status='indexed' WHERE event_id=$id;", new Dictionary<string, object> { ["vector"] = document.VectorId, ["id"] = document.SourceId });
            if (document.SourceType == "memory") ReignDatabase.ExecuteSql(connection, "UPDATE memories SET vector_id=$vector,embedding_status='indexed' WHERE memory_id=$id;", new Dictionary<string, object> { ["vector"] = document.VectorId, ["id"] = document.SourceId });
            if (document.SourceType == "summary") ReignDatabase.ExecuteSql(connection, "UPDATE summaries SET vector_id=$vector,embedding_status='indexed' WHERE summary_id=$id;", new Dictionary<string, object> { ["vector"] = document.VectorId, ["id"] = document.SourceId });
            CompleteEmbeddingJob(connection, document.SourceType, document.SourceId, "completed", "");
        }

        private static void CompleteEmbeddingJob(ReignDbConnection connection, string type, string id, string status, string message)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM embedding_jobs WHERE source_type=$type AND source_id=$id;", new Dictionary<string, object> { ["type"] = type, ["id"] = id });
        }

        private static void FailEmbeddingJob(ReignDbConnection connection, Dictionary<string, object> job, string error)
        {
            int attempts = ReadInt(job, "attempt_count", 0) + 1;
            long delay = Math.Min(300, Math.Max(5, 5 * (1L << Math.Min(6, attempts - 1))));
            ReignDatabase.ExecuteSql(connection, "UPDATE embedding_jobs SET attempt_count=$attempts,next_attempt_ts=$next,last_error=$error,updated_ts=$ts WHERE source_type=$type AND source_id=$id;",
                new Dictionary<string, object>
                {
                    ["attempts"] = attempts, ["next"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + delay,
                    ["error"] = Program.LimitText(error, 500), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["type"] = ReadString(job, "source_type", ""), ["id"] = ReadString(job, "source_id", "")
                });
        }
    }
}
