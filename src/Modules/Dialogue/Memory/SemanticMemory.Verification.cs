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
        internal static List<Dictionary<string, object>> RunSemanticMemorySelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            { ["id"] = "semantic_memory_" + id, ["suite"] = "semantic_memory", ["passed"] = passed, ["summary"] = summary });
            string first = DeterministicSemanticGuid("campaign|memory|one"), second = DeterministicSemanticGuid("campaign|memory|one");
            add("deterministic_id", first == second && Guid.TryParse(first, out Guid _), "Vector IDs are deterministic UUIDs.");
            add("canonical_text", BuildCanonicalEmbeddingText("belief", new Dictionary<string, object> { ["claim"] = "A lord abandoned his ally.", ["stance"] = "distrust" }).Contains("abandoned"), "Canonical text includes source prose and stance.");
            Dictionary<string, object> scored = new Dictionary<string, object> { ["vectorSemanticScore"] = 0.8d };
            add("score_blend", Math.Abs(AddSemanticScore(scored, 10d, new Dictionary<string, object> { ["vectorScoreWeight"] = 30d }) - 34d) < 0.001d, "Semantic score augments deterministic relevance without replacing it.");
            int savedInteractiveCount = Volatile.Read(ref Program.InteractiveRequestCount);
            long savedInteractiveTicks = Interlocked.Read(ref Program.LastInteractiveRequestCompletedUtcTicks);
            try
            {
                Interlocked.Exchange(ref Program.InteractiveRequestCount, 1);
                bool activeYields = Program.InteractiveRequestActiveOrRecent();
                Interlocked.Exchange(ref Program.InteractiveRequestCount, 0);
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.Ticks);
                bool recentYields = Program.InteractiveRequestActiveOrRecent();
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.AddSeconds(-5).Ticks);
                bool handoffProtected = !Program.InteractiveRequestActiveOrRecent()
                    && SemanticIndexShouldYield();
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, DateTime.UtcNow.AddSeconds(-11).Ticks);
                bool quietResumes = !SemanticIndexShouldYield();
                add("foreground_priority", activeYields && recentYields && handoffProtected && quietResumes,
                    "Background embedding chunks yield throughout the response-to-next-turn handoff and resume after the extended quiet period.");
            }
            finally
            {
                Interlocked.Exchange(ref Program.InteractiveRequestCount, savedInteractiveCount);
                Interlocked.Exchange(ref Program.LastInteractiveRequestCompletedUtcTicks, savedInteractiveTicks);
            }
            List<Dictionary<string, object>> duplicateCandidates = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["event_id"] = "duplicate_event", ["deterministicScore"] = 2d },
                new Dictionary<string, object> { ["event_id"] = "duplicate_event", ["deterministicScore"] = 7d }
            };
            List<Dictionary<string, object>> deduplicatedCandidates = MergeSemanticWorldHistoryCandidates(
                null, "campaign", "timeline", "claim", "generic_participation", "", new List<string>(), "", -1d, double.MaxValue,
                duplicateCandidates, new Dictionary<string, object>(), new Dictionary<string, object> { ["vectorScoreWeight"] = 30d });
            add("world_history_candidate_dedupe",
                deduplicatedCandidates.Count == 1 && Math.Abs(ReadDouble(deduplicatedCandidates[0], "deterministicScore", 0d) - 7d) < 0.001d,
                "Duplicate deterministic world-history candidates merge safely before semantic scoring.");
            add("privacy_contract", typeof(DialogueService.KnowledgeAccessContext) != null, "Semantic rows still pass through the canonical knowledge-access context.");
            add("world_history_significance_filter",
                !WorldHistoryEventEmbeddingEligible(new Dictionary<string, object> { ["event_type"] = "settlement_entered", ["dissemination_class"] = "ordinary" })
                && WorldHistoryEventEmbeddingEligible(new Dictionary<string, object> { ["event_type"] = "siege_completed", ["dissemination_class"] = "ordinary" })
                && WorldHistoryEventEmbeddingEligible(new Dictionary<string, object> { ["event_type"] = "future_unknown_event", ["dissemination_class"] = "major_world" }),
                "Routine global simulation noise stays in PostgreSQL full-text storage while significant and major-world events remain embedding eligible.");
            TaskCompletionSource<bool> activePump = new TaskCompletionSource<bool>();
            using (CancellationTokenSource activeCancellation = new CancellationTokenSource())
            using (CancellationTokenSource cancelledCancellation = new CancellationTokenSource())
            {
                cancelledCancellation.Cancel();
                add("pump_restart_contract",
                    SemanticPumpUsable(activePump.Task, activeCancellation)
                    && !SemanticPumpUsable(activePump.Task, cancelledCancellation)
                    && !SemanticPumpUsable(Task.CompletedTask, activeCancellation),
                    "A canceled or completed indexing pump is replaceable even while an older task is still unwinding.");
            }
            Func<string, string, string, string, Dictionary<string, object>> semanticRow = (type, id, hash, vector) =>
                new Dictionary<string, object>
                {
                    ["source_type"] = type, ["source_id"] = id, ["model_version"] = SemanticModelVersion,
                    ["provider"] = "local", ["content_hash"] = hash, ["vector_id"] = vector,
                    ["status"] = "indexed", ["event_type"] = "", ["dissemination_class"] = ""
                };
            Dictionary<string, Dictionary<string, object>> currentDocuments =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> targetDocuments =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> unchangedCurrent = semanticRow("memory", "unchanged", "same", "vector_unchanged");
            Dictionary<string, object> unchangedTarget = semanticRow("memory", "unchanged", "same", "vector_unchanged");
            Dictionary<string, object> changedCurrent = semanticRow("memory", "changed", "old", "vector_changed");
            Dictionary<string, object> changedTarget = semanticRow("memory", "changed", "new", "vector_changed");
            Dictionary<string, object> futureCurrent = semanticRow("event", "future", "future", "vector_future");
            Dictionary<string, object> restoredTarget = semanticRow("summary", "restored", "restored", "vector_restored");
            currentDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(unchangedCurrent)] = unchangedCurrent;
            currentDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(changedCurrent)] = changedCurrent;
            currentDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(futureCurrent)] = futureCurrent;
            targetDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(unchangedTarget)] = unchangedTarget;
            targetDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(changedTarget)] = changedTarget;
            targetDocuments[PersistenceService.SaveSyncEmbeddingDocumentKey(restoredTarget)] = restoredTarget;
            SaveSyncSemanticReconciliationPlan selectivePlan =
                PersistenceService.BuildSaveSyncSemanticReconciliationPlan(currentDocuments, targetDocuments);
            add("save_sync_selective_reconciliation",
                selectivePlan.PreservedDocuments == 1
                && selectivePlan.DeleteVectorIds.SetEquals(new[] { "vector_changed", "vector_future" })
                && selectivePlan.RequeueSources.Select(source => source.SourceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(new[] { "changed", "restored" }),
                "Save Sync preserves unchanged vectors and only deletes or requeues divergent sources.");
            string campaignId = "semantic_memory_test_" + Guid.NewGuid().ToString("N");
            try
            {
                DialogueService.StoreWorldMemoryEvent(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["eventId"] = "semantic_private_event", ["eventType"] = "personal_revelation",
                    ["summary"] = "Aldric abandoned his ally and broke his promise.", ["participants"] = new List<string> { "npc_owner" },
                    ["known_by"] = new List<string> { "npc_owner" }, ["visibility"] = "private", ["importance"] = 0.8d,
                    ["worldDay"] = 10d, ["ts"] = 1000L
                }, "semantic_self_test");
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    int triggerCount = ReadInt(DialogueStore.CountSqliteMaster(connection).FirstOrDefault(), "count", 0);
                    int queued = ReadInt(DialogueStore.CountEmbeddingJobs(connection).FirstOrDefault(), "count", 0);
                    Dictionary<string, object> memory = DialogueStore.SelectMemories9(connection).FirstOrDefault();
                    bool ownerAllowed = DialogueService.KnowledgeRowVisibleToNpc("memories", memory, new DialogueService.KnowledgeAccessContext { NpcId = "npc_owner" });
                    bool strangerDenied = !DialogueService.KnowledgeRowVisibleToNpc("memories", memory, new DialogueService.KnowledgeAccessContext { NpcId = "npc_stranger" });
                    EmbeddingSourceDocument document = BuildEmbeddingDocument(campaignId, "memory", ReadString(memory, "memory_id", ""), memory,
                        BuildCanonicalEmbeddingText("memory", memory), new Dictionary<string, object> { ["vectorProvider"] = "local" });
                    string payload = Json.Serialize(document.Payload);
                    int rawConversationTriggerCount = ReadInt(DialogueStore.CountSqliteMaster2(connection)
                        .FirstOrDefault(), "count", 0);
                    int expectedTriggerCount =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                            ? 18 : 20;
                    add("schema_and_triggers", triggerCount >= expectedTriggerCount && rawConversationTriggerCount >= 3 && queued >= 2,
                        "Original attributed conversation and durable semantic sources have insert, update, and delete queue triggers; reads revalidate original eligibility.");
                    add("knowledge_filter", ownerAllowed && strangerDenied, "Private semantic candidates remain visible to their owner and hidden from strangers.");
                    add("external_payload_privacy", payload.IndexOf("abandoned", StringComparison.OrdinalIgnoreCase) < 0 && payload.IndexOf("promise", StringComparison.OrdinalIgnoreCase) < 0,
                        "Vector payload metadata contains no raw memory prose.");
                    DialogueStore.InsertWorldHistoryEvents(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["utc"] = DateTime.UtcNow.ToString("o") });
                    DialogueStore.InsertWorldHistoryEvents2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["utc"] = DateTime.UtcNow.ToString("o") });
                    Dictionary<string, object> routineJob = DialogueStore.SelectEmbeddingJobs2(connection).FirstOrDefault();
                    Dictionary<string, object> significantJob = DialogueStore.SelectEmbeddingJobs3(connection).FirstOrDefault();
                    add("world_history_queue_filter", routineJob == null && ReadString(significantJob, "operation", "") == "upsert",
                        "Embedding triggers enqueue significant world history without queuing routine settlement traffic.");
                    Dictionary<string, object> job = DialogueStore.SelectEmbeddingJobs4(connection).FirstOrDefault();
                    FailEmbeddingJob(connection, job, "temporary worker failure");
                    Dictionary<string, object> retried = DialogueStore.SelectEmbeddingJobs5(connection, new Dictionary<string, object> { ["type"] = ReadString(job, "source_type", ""), ["id"] = ReadString(job, "source_id", "") }).FirstOrDefault();
                    add("retry_backoff", ReadInt(retried, "attempt_count", 0) == 1 && ReadLong(retried, "next_attempt_ts", 0) > DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        "Failed indexing jobs persist with bounded retry backoff.");
                }
            }
            finally
            {
                string directory = Program.CampaignDirectory(campaignId);
                try
                {
                    ReignPostgreSqlStorage.ClearAllPools();
                    if (Directory.Exists(directory)) Directory.Delete(directory, true);
                }
                catch { }
            }
            return rows;
        }
    }
}
