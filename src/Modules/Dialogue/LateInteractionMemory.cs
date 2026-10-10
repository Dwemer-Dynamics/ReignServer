using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string LateInteractionSchema = "reign-colbert-source-v1";
        private const string LateInteractionIndexSchema = LateInteractionSchema + "+player-narrative-v3";
        private const string LateInteractionModelHash = "bfe81fa313c4c2a12319ec9ef0cdf1c995daad051e9c5a1fcabcb90feb3c8286";
        // Offline adapters are scoped to the isolated verifier; never configured
        // by a player request or provider response.
        private static readonly AsyncLocal<Func<Dictionary<string, object>, Dictionary<string, object>>> LateInteractionReplay = new();
        private static readonly AsyncLocal<List<Dictionary<string, object>>> LateInteractionReplayAudit = new();
        private static DateTime LateInteractionUnavailableUntilUtc;
        private static readonly LateInteractionIndexRecovery LateInteractionRecovery = new();
        private static readonly AsyncLocal<LateInteractionIndexRecovery> LateInteractionReplayRecovery = new();

        // The existing maintenance timer revisits deferred work. Never sleep or
        // retry HTTP inside a dialogue turn, and never spend a source's four
        // content-failure attempts on an unavailable/busy worker.
        private sealed class LateInteractionIndexRecovery
        {
            private readonly object gate = new();
            private string worker = "";
            private int failures;
            private DateTime nextAttempt;
            internal bool Ready(string endpoint, DateTime now)
            {
                lock (gate) return worker != endpoint || now >= nextAttempt;
            }
            internal int Defer(string endpoint, DateTime now)
            {
                lock (gate)
                {
                    failures = worker == endpoint ? Math.Min(failures + 1, 4) : 1;
                    worker = endpoint;
                    int delay = new[] { 5, 15, 60, 300 }[failures - 1];
                    nextAttempt = now.AddSeconds(delay);
                    return delay;
                }
            }
            internal void Complete(string endpoint)
            {
                lock (gate) { worker = endpoint; failures = 0; nextAttempt = default; }
            }
        }

        private static Dictionary<string, object> LateInteractionFailureDetails(Exception exception)
        {
            var detail = TestDict("errorType", exception?.GetType().Name ?? "", "cause", "worker_response");
            if (exception == null) return detail;
            detail["cause"] = "operation_failed";
            for (var inner = exception; inner != null; inner = inner.InnerException)
            {
                detail["innerErrorType"] = inner.GetType().Name;
                if (inner is System.Net.WebException web)
                {
                    detail["transportStatus"] = web.Status.ToString();
                    detail["cause"] = web.Status == System.Net.WebExceptionStatus.Timeout ? "worker_timeout"
                        : web.Status is System.Net.WebExceptionStatus.ConnectFailure or System.Net.WebExceptionStatus.NameResolutionFailure
                            ? "worker_unreachable" : "worker_transport_error";
                    if (web.Response is System.Net.HttpWebResponse response)
                        detail["httpStatus"] = (int)response.StatusCode;
                }
                else if (inner is TimeoutException) detail["cause"] = "worker_timeout";
            }
            // Exception messages and HTTP bodies can contain original dialogue,
            // URLs or credentials. Only typed causes/statuses cross this boundary.
            return detail;
        }

        private static string LateInteractionIndexOutcome(string value) => value switch
        {
            "index_busy" or "index_unavailable" or "empty_visible_source" or "invalid_response"
                or "invalid_receipts" or "model_not_installed" or "model_contract_mismatch"
                or "worker_not_ready" => value,
            _ => "invalid_response"
        };

        private static bool LateInteractionTransientIndexResult(Dictionary<string, object> response) =>
            ReadString(response, "status", "") == "index_busy"
            || (ReadString(response, "status", "") == "index_unavailable"
                && ReadBool(response, "retryable", false));

        private static Dictionary<string, object> LateInteractionWorkerFailureDetails(Dictionary<string, object> response)
        {
            string type = ReadString(response, "errorType", "");
            string reason = ReadString(response, "reason", "");
            return TestDict("workerErrorType", new[] { "TimeoutError", "ValueError", "FileNotFoundError", "OperationalError" }.Contains(type) ? type : "unknown",
                "workerReason", new[] { "colbert_inference_busy", "colbert_model_not_installed", "colbert_model_hash_mismatch",
                    "source_exceeds_chunk_limit", "source_text_hash_mismatch", "incomplete_source_windows", "database is locked",
                    "database table is locked" }.Contains(reason) ? reason : "unclassified");
        }

        private static bool LateInteractionReceiptsComplete(Dictionary<string, object> response, List<Dictionary<string, object>> views) =>
            LateInteractionObjectList(response, "sources")
            && ReadDictionaryList(response, "sources").Count == views.Count
            && views.All(view => ReadDictionaryList(response, "sources").Count(receipt =>
                new[] { "sourceId", "view", "sourceHash", "textHash" }
                    .All(key => ReadString(view, key, "") == ReadString(receipt, key, ""))) == 1);

        private static Dictionary<string, object> LateInteractionIndexCall(string endpoint, string path, Dictionary<string, object> request, int timeout)
        {
            if (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
            {
                if (LateInteractionReplay.Value == null) throw new InvalidOperationException("An isolated index requires a replay adapter.");
                var replayRequest = CloneDictionary(request); replayRequest["operation"] = path;
                return LateInteractionReplay.Value(replayRequest);
            }
            return TryParseJsonObject(PostJsonToUrl(endpoint + path, Json.Serialize(request), timeout));
        }

        private static void DeferLateInteractionIndex(string campaign, LateInteractionIndexRecovery recovery,
            string endpoint, string phase, string outcome, Exception exception = null, Dictionary<string, object> response = null)
        {
            var audit = LateInteractionFailureDetails(exception);
            if (response != null) foreach (var value in LateInteractionWorkerFailureDetails(response)) audit[value.Key] = value.Value;
            audit["campaignId"] = campaign; audit["phase"] = phase;
            audit["outcome"] = LateInteractionIndexOutcome(outcome);
            audit["retryAfterSeconds"] = recovery.Defer(endpoint, DateTime.UtcNow);
            // Diagnostics are optional, including during a storage outage.
            try
            {
                if (LateInteractionReplayAudit.Value != null) LateInteractionReplayAudit.Value.Add(audit);
                else LogOperational("memory.late_interaction_index_deferred", audit);
            }
            catch { }
        }

        private static void EnsureLateInteractionMemorySchema(ReignDbConnection connection) => ExecuteSql(connection, @"
CREATE TABLE IF NOT EXISTS memory_late_interaction_sources (
 turn_id TEXT NOT NULL,view TEXT NOT NULL,source_hash TEXT NOT NULL,text_hash TEXT NOT NULL,
 source_revision TEXT NOT NULL,model_hash TEXT NOT NULL,schema_version TEXT NOT NULL,
 worker_epoch TEXT NOT NULL,updated_ts BIGINT NOT NULL,PRIMARY KEY(turn_id,view));
ALTER TABLE memory_late_interaction_sources ADD COLUMN IF NOT EXISTS transcript_hash TEXT NOT NULL DEFAULT '';
CREATE TABLE IF NOT EXISTS memory_late_interaction_attempts (
 turn_id TEXT PRIMARY KEY,source_revision TEXT NOT NULL,model_hash TEXT NOT NULL,schema_version TEXT NOT NULL,
 attempts INTEGER NOT NULL,next_attempt_ts BIGINT NOT NULL,outcome TEXT NOT NULL);");

        private static bool LateInteractionQualified(Dictionary<string, object> settings)
        {
            var c = ReadDictionary(settings, "memoryLateInteractionQualification");
            return ReadString(c, "schema", "") == "reign-colbert-qualification-v1"
                && new[] { "requiredEvidenceCoverage", "answerCoverage", "baselineAnswerCoverage", "quality", "baselineQuality" }
                    .All(k => double.IsFinite(ReadDouble(c, k, double.NaN)) && ReadDouble(c, k, -1) >= 0 && ReadDouble(c, k, 2) <= 1)
                && ReadString(c, "indexSchema", "") == LateInteractionSchema
                && ReadString(c, "modelHash", "") == LateInteractionModelHash
                && System.Text.RegularExpressions.Regex.IsMatch(ReadString(c, "corpusSha256", ""), "^[a-f0-9]{64}$")
                && ReadBool(c, "heldOut", false) && ReadBool(c, "independentlyLabeled", false)
                && ReadInt(c, "caseCount", 0) >= 100 && ReadInt(c, "criticalErrors", -1) == 0
                && ReadDouble(c, "requiredEvidenceCoverage", -1) >= .98
                && ReadDouble(c, "answerCoverage", -1) >= .98
                && ReadDouble(c, "answerCoverage", -1) >= ReadDouble(c, "baselineAnswerCoverage", 2)
                && ReadDouble(c, "quality", -1) >= ReadDouble(c, "baselineQuality", 2)
                && ReadDouble(c, "retrievalP95Ms", -1) >= 0 && ReadDouble(c, "retrievalP95Ms", double.MaxValue) <= 2000;
        }

        private static Dictionary<string, object> LateInteractionView(Dictionary<string, object> source, string view)
        {
            string speaker = ReadString(source, "speaker_id", "");
            string text = ObserverConversationSourceText(source, view == "self" ? speaker : "__other_observer__");
            if (string.IsNullOrWhiteSpace(text)) return null;
            // Preserve names and source identities separately. The label helps
            // search; it does not establish who performed a mentioned action.
            text = FirstNonEmpty(ReadString(source, "speaker_name", ""), speaker) + ": " + text;
            return TestDict("sourceId", ReadString(source, "turn_id", ""), "view", view,
                "sourceHash", MemorySpecialistSourceHash(source), "textHash", SemanticSha256Hex(text), "text", text);
        }

        private static Dictionary<string, object> LateInteractionRequest(string campaign) => TestDict(
            "schema", LateInteractionSchema, "modelHash", LateInteractionModelHash, "campaignId", campaign);

        private static void RecordLateInteractionAudit(Dictionary<string, object> audit)
        {
            if (LateInteractionReplayAudit.Value != null) LateInteractionReplayAudit.Value.Add(audit);
            else
            {
                // Optional search diagnostics must never reject usable dialogue.
                try { LogOperational("memory.late_interaction_search", audit); }
                catch { }
            }
        }

        private static bool LateInteractionNumericScore(Dictionary<string, object> hit) =>
            hit.TryGetValue("score", out var score) && (score is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal)
            && double.IsFinite(ReadDouble(hit, "score", double.NaN));

        private static bool LateInteractionObjectList(Dictionary<string, object> item, string key) =>
            item.TryGetValue(key, out var raw) && raw is System.Collections.IEnumerable entries && raw is not string
            && raw is not System.Collections.IDictionary && entries.Cast<object>().All(x => x is Dictionary<string,object>);

        private static string LateInteractionReplayInputError(Dictionary<string, object> item)
        {
            if (!item.ContainsKey("capturedLateInteractionSearches")) return "";
            if (!LateInteractionObjectList(item,"capturedLateInteractionSearches")) return "invalid_colbert_capture_count";
            var captures = ReadDictionaryList(item, "capturedLateInteractionSearches");
            if (captures.Count == 0 || captures.Count > 24) return "invalid_colbert_capture_count";
            var queries = new HashSet<string>(StringComparer.Ordinal);
            var sourceIds = ReadDictionaryList(item, "sources").Select(s=>ReadString(s,"sourceId","")).ToHashSet(StringComparer.Ordinal);
            foreach (var capture in captures)
            {
                string query = ReadString(capture,"query","");
                if (query.Length == 0 || query.Length > 12000 || !queries.Add(query)
                    || ReadString(capture,"schema","") != LateInteractionSchema
                    || ReadString(capture,"modelHash","") != LateInteractionModelHash) return "invalid_colbert_capture_identity";
                var hits = ReadDictionaryList(capture,"results");
                if (!LateInteractionObjectList(capture,"results") || hits.Count>128) return "invalid_colbert_capture_hits";
                var unique = new HashSet<string>(StringComparer.Ordinal);
                foreach (var hit in hits)
                    if (!sourceIds.Contains(ReadString(hit,"sourceId","")) || !unique.Add(ReadString(hit,"sourceId",""))
                        || !new[]{"self","public"}.Contains(ReadString(hit,"view",""))
                        || !System.Text.RegularExpressions.Regex.IsMatch(ReadString(hit,"textHash",""),"^[a-f0-9]{64}$")
                        || !LateInteractionNumericScore(hit)) return "invalid_colbert_capture_source";
            }
            return "";
        }

        private static Dictionary<string, object> ReplayLateInteractionSearch(Dictionary<string, object> item,
            Dictionary<string, object> request, Func<string,string> map, List<Dictionary<string,object>> views,
            List<string> pending, List<Dictionary<string,object>> captures)
        {
            if (LateInteractionReplayInputError(item).Length > 0)
            {
                pending.Add("invalid_colbert_capture");
                return TestDict("ok", false, "status", "invalid_colbert_capture");
            }
            var eligible = ReadDictionaryList(request,"eligible");
            var exported = views.Where(v=>eligible.Any(e=>new[]{"sourceId","sourceHash","textHash","view"}
                .All(k=>ReadString(v,k,"")==ReadString(e,k,"")))).ToList();
            var receipt = TestDict("request",CloneDictionary(request),"requestHash",PromptHash(CanonicalJson(request)),
                "documents",exported,"status","capture_pending");
            captures.Add(receipt);
            var capture = ReadDictionaryList(item,"capturedLateInteractionSearches")
                .SingleOrDefault(c=>ReadString(c,"query","")==ReadString(request,"query",""));
            if(capture==null)
            {
                pending.Add("capturedLateInteractionSearches:"+PromptHash(ReadString(request,"query","")));
                return TestDict("ok",false,"status","capture_pending");
            }
            var result = new List<Dictionary<string,object>>();
            foreach(var hit in ReadDictionaryList(capture,"results"))
            {
                var source = eligible.FirstOrDefault(e=>ReadString(e,"sourceId","")==map(ReadString(hit,"sourceId",""))
                    && ReadString(e,"view","")==ReadString(hit,"view","") && ReadString(e,"textHash","")==ReadString(hit,"textHash",""));
                if(source==null) { receipt["status"]="source_or_projection_mismatch"; pending.Add("colbert_source_mismatch"); return TestDict("ok",false,"status","source_or_projection_mismatch"); }
                var bound=CloneDictionary(source); bound["score"]=ReadDouble(hit,"score",0); result.Add(bound);
            }
            receipt["status"]="captured_local_ranks"; receipt["captureHash"]=PromptHash(CanonicalJson(capture));
            receipt["results"]=result;
            return TestDict("ok",true,"status","captured_local_ranks","schema",LateInteractionSchema,
                "modelHash",LateInteractionModelHash,"results",result);
        }

        private static List<Dictionary<string, object>> FindLateInteractionTurns(ReignDbConnection connection,
            MemoryQueryPlan plan, KnowledgeAccessContext knowledge, int limit, bool requirePlayerSource = true)
        {
            var empty = new List<Dictionary<string, object>>();
            bool isolated = !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value);
            if (isolated && LateInteractionReplay.Value == null) return empty;
            if (!isolated && DateTime.UtcNow < LateInteractionUnavailableUntilUtc) return empty;
            var timer = Stopwatch.StartNew();
            var audit = TestDict("schema", LateInteractionSchema, "applied", false, "modelHash", LateInteractionModelHash);
            try
            {
                var settings = isolated ? TestDict() : LoadSettings();
                string mode = isolated ? "replay" : ReadString(settings, "memoryLateInteractionMode", "shadow");
                audit["mode"] = mode;
                if (!new[] { "shadow", "on", "replay" }.Contains(mode) || string.IsNullOrWhiteSpace(plan.Query)) return empty;
                string campaign = ReignPostgreSqlStorage.CampaignIdForConnection(connection);
                if (!isolated && IsSaveSyncSemanticBlocked(campaign)) { audit["status"] = "save_sync_fallback"; return empty; }
                string eligible = MemorySessionObserverSql("s", "t") + @" AND t.status='active' AND t.text<>''
AND t.world_day<=$day AND t.accepted_sequence<=$asOfSequence AND t.ts<=$asOfTimestamp AND " + MemorySourceTimelineSql("conversation_turns", "t");
                var args = TestDict("npc", knowledge.NpcId, "timeline", knowledge.TimelineId, "day", knowledge.WorldDay,
                    "asOfSequence", knowledge.AsOfAcceptedSequence, "asOfTimestamp", knowledge.AsOfTimestamp,
                    "model", LateInteractionModelHash, "schema", LateInteractionIndexSchema);
                // Receipts bind exact source content to PostgreSQL's current row
                // revision. Read metadata for the whole eligible history, not all
                // prose and not just the current lexical candidate pool.
                var metadata = QuerySql(connection, @"SELECT t.turn_id,t.session_id,t.exchange_id,t.speaker_id,t.status,
t.world_day,t.ts,t.accepted_sequence,t.participants_json,t.payload_json,i.source_hash,i.text_hash,i.transcript_hash,
CASE WHEN t.speaker_id=$npc THEN 'self' ELSE 'public' END AS view
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
LEFT JOIN memory_late_interaction_sources i ON i.turn_id=t.turn_id
 AND i.view=CASE WHEN t.speaker_id=$npc THEN 'self' ELSE 'public' END
 AND i.source_revision=t.xmin::text AND i.model_hash=$model AND i.schema_version=$schema
WHERE " + eligible + " ORDER BY t.turn_id LIMIT 50001;", args);
                if (metadata.Count > 50000) { audit["status"] = "eligibility_limit_exceeded"; return empty; }
                metadata = metadata.Where(r => MemoryTimeVisible(r, knowledge, connection)
                    && !(plan.SuppliedSources != null && plan.SuppliedSources.TryGetValue(ReadString(r,"turn_id",""), out string suppliedHash)
                        && suppliedHash.Length > 0 && suppliedHash == ReadString(r,"transcript_hash",""))).ToList();
                if (requirePlayerSource)
                {
                    // Reject recall-only source sessions before scoring, including
                    // as-of limits. An unseen future answer cannot qualify a session.
                    var players = QuerySql(connection, "SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id WHERE "
                        + eligible + " AND t.role='player' ORDER BY t.turn_id LIMIT 50001;", args);
                    if (players.Count > 50000) { audit["status"] = "source_session_limit_exceeded"; return empty; }
                    var sourceSessions = players.Where(r => PrecisionTurnVisible(r, knowledge.NpcId, knowledge, connection)
                        && !IsPureConversationRecallRequest(ReadString(r, "text", "")))
                        .Select(r => ReadString(r, "session_id", "")).ToHashSet(StringComparer.Ordinal);
                    metadata = metadata.Where(r => sourceSessions.Contains(ReadString(r, "session_id", ""))).ToList();
                }
                var candidates = metadata.Where(r => ReadString(r, "source_hash", "").Length == 64
                    && ReadString(r,"text_hash","") != SemanticSha256Hex("")).ToList();
                audit["eligibleOriginals"] = metadata.Count;
                audit["indexedOriginals"] = candidates.Count;
                if (candidates.Count == 0) { audit["status"] = "index_not_ready"; return empty; }
                var request = LateInteractionRequest(campaign);
                request["query"] = plan.Query;
                request["limit"] = Math.Max(24, Math.Min(128, limit * 4));
                request["eligible"] = candidates.Select(r => TestDict("sourceId", ReadString(r, "turn_id", ""),
                    "sourceHash", ReadString(r, "source_hash", ""), "textHash", ReadString(r, "text_hash", ""), "view", ReadString(r, "view", ""))).ToList();
                var response = isolated ? LateInteractionReplay.Value(request)
                    : TryParseJsonObject(PostJsonToUrl(SemanticWorkerUrl(settings, "/memory/late/search"), Json.Serialize(request), 4000));
                audit["status"] = ReadString(response, "status", "invalid_response");
                audit["workerTiming"] = ReadDictionary(response, "timing");
                audit["workerIndexedOriginals"] = ReadInt(response, "indexedCount", 0);
                if (!ReadBool(response, "ok", false) || ReadString(response, "schema", "") != LateInteractionSchema
                    || ReadString(response, "modelHash", "") != LateInteractionModelHash) return empty;
                var allowed = candidates.ToDictionary(r => ReadString(r, "turn_id", ""), StringComparer.Ordinal);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var result = new List<Dictionary<string, object>>();
                var hits = ReadDictionaryList(response, "results");
                if (hits.Count > 128) { audit["status"] = "invalid_result_count"; return empty; }
                foreach (var hit in hits.OrderByDescending(h => ReadDouble(h, "score", double.NaN)))
                {
                    string id = ReadString(hit, "sourceId", "");
                    if (!LateInteractionNumericScore(hit) || !allowed.TryGetValue(id, out var selected)
                        || new[] { "view", "sourceHash", "textHash" }.Any(k => ReadString(hit, k, "") != ReadString(selected,
                            k == "sourceHash" ? "source_hash" : k == "textHash" ? "text_hash" : k, "")))
                    { audit["status"] = "invalid_source_result"; return empty; }
                    // Reload original words after the worker returns. A deleted,
                    // edited or restored-out row invalidates its cached score.
                    var row = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).SingleOrDefault();
                    if (row == null || !PrecisionTurnVisible(row, knowledge.NpcId, knowledge, connection)
                        || MemorySpecialistSourceHash(row) != ReadString(hit, "sourceHash", "")) continue;
                    var view = LateInteractionView(row, ReadString(hit, "view", ""));
                    if (view == null || ReadString(view, "textHash", "") != ReadString(hit, "textHash", "")) continue;
                    if (MemorySourceAlreadySupplied(row, plan.SuppliedSources)) continue;
                    string exchange = ReadString(row, "session_id", "") + "|" + FirstNonEmpty(ReadString(row, "exchange_id", ""), id);
                    if (!seen.Add(exchange)) continue;
                    row["late_interaction_rank"] = result.Count;
                    row["late_interaction_score"] = ReadDouble(hit, "score", 0);
                    result.Add(row);
                    if (result.Count >= limit) break;
                }
                audit["sourceIds"] = result.Select(r => ReadString(r, "turn_id", "")).ToList();
                bool apply = isolated || mode == "on" && LateInteractionQualified(settings);
                audit["applied"] = apply;
                if (mode == "on" && !apply) audit["status"] = "qualification_required";
                return apply ? result : empty;
            }
            catch (Exception ex)
            {
                audit["status"] = "local_search_fallback";
                audit["errorType"] = ex.GetType().Name;
                if (!isolated) LateInteractionUnavailableUntilUtc = DateTime.UtcNow.AddSeconds(30);
                return empty;
            }
            finally { audit["durationMs"] = timer.ElapsedMilliseconds; RecordLateInteractionAudit(audit); }
        }

        private static void ReconcileLateInteractionMemory(string campaign, Dictionary<string, object> settings, int limit = 16)
        {
            bool isolated = !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value);
            if ((isolated && LateInteractionReplay.Value == null)
                || !new[] { "shadow", "on" }.Contains(ReadString(settings, "memoryLateInteractionMode", "shadow"))) return;
            var recovery = isolated ? LateInteractionReplayRecovery.Value ?? new() : LateInteractionRecovery;
            string endpoint = SemanticWorkerUrl(settings, "");
            if (!recovery.Ready(endpoint, DateTime.UtcNow)) return;
            string phase = "worker_status";
            try
            {
                var status = LateInteractionIndexCall(endpoint, "/memory/late/status", LateInteractionRequest(campaign), 1500);
                if (ReadString(status, "modelHash", "") != LateInteractionModelHash || ReadString(status, "schema", "") != LateInteractionSchema
                    || !ReadBool(status, "modelInstalled", false))
                {
                    DeferLateInteractionIndex(campaign, recovery, endpoint, phase,
                        ReadBool(status, "modelInstalled", false) ? "model_contract_mismatch" : "model_not_installed");
                    return;
                }
                string epoch = ReadString(status, "indexGeneration", "");
                phase = "source_selection";
                List<Dictionary<string, object>> sources;
                using (var connection = OpenCampaignConnection(campaign))
                {
                    string missing = @"SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t
LEFT JOIN memory_late_interaction_attempts a ON a.turn_id=t.turn_id AND a.source_revision=t.xmin::text
 AND a.model_hash=$model AND a.schema_version=$schema
WHERE t.status='active' AND t.text<>'' AND (a.turn_id IS NULL OR (a.attempts<4 AND a.next_attempt_ts<=$now))
AND (SELECT COUNT(*) FROM memory_late_interaction_sources i WHERE i.turn_id=t.turn_id
AND i.source_revision=t.xmin::text AND i.model_hash=$model AND i.schema_version=$schema AND i.worker_epoch=$epoch AND i.transcript_hash<>'')<2";
                    var args = TestDict("model", LateInteractionModelHash, "schema", LateInteractionIndexSchema, "epoch", epoch,
                        "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "limit", Math.Clamp(limit, 1, 64));
                    sources = QuerySql(connection, missing + " ORDER BY t.accepted_sequence DESC,t.turn_id LIMIT $limit;", args)
                        .Concat(QuerySql(connection, missing + " ORDER BY t.accepted_sequence,t.turn_id LIMIT $limit;", args))
                        .GroupBy(r => ReadString(r, "turn_id", "")).Select(g => g.First()).ToList();
                }
                foreach (var source in sources)
                {
                    if (!isolated && (PriorityBackgroundWorkShouldYield() || IsSaveSyncSemanticBlocked(campaign))) break;
                    phase = "source_projection";
                    var views = new[] { "self", "public" }.Select(v => LateInteractionView(source, v)).Where(v => v != null).ToList();
                    if (views.Count == 0) { RecordLateInteractionIndexFailure(campaign, source, "empty_visible_source"); continue; }
                    var request = LateInteractionRequest(campaign);
                    request["sources"] = views;
                    phase = "worker_index";
                    var response = LateInteractionIndexCall(endpoint, "/memory/late/index", request, 15000);
                    if (!ReadBool(response, "ok", false) || ReadString(response, "schema", "") != LateInteractionSchema
                        || ReadString(response, "modelHash", "") != LateInteractionModelHash)
                    {
                        if (LateInteractionTransientIndexResult(response))
                        {
                            DeferLateInteractionIndex(campaign, recovery, endpoint, phase, ReadString(response, "status", "invalid_response"), response: response);
                            return;
                        }
                        RecordLateInteractionIndexFailure(campaign, source, LateInteractionIndexOutcome(ReadString(response, "status", "invalid_response")), response);
                        continue;
                    }
                    epoch = ReadString(response, "indexGeneration", "");
                    phase = "receipt_validation";
                    if (epoch.Length == 0 || epoch.Length > 128 || !LateInteractionReceiptsComplete(response, views))
                    { RecordLateInteractionIndexFailure(campaign, source, "invalid_receipts"); continue; }
                    phase = "receipt_publish";
                    using var connection = OpenCampaignConnection(campaign);
                    using var transaction = connection.BeginTransaction();
                    AcquirePostgreSqlTransactionMutationLock(connection);
                    var current = QuerySql(connection, "SELECT *,xmin::text AS source_revision FROM conversation_turns WHERE turn_id=$id;", TestDict("id", source["turn_id"])).SingleOrDefault();
                    if (current != null && MemorySpecialistSourceHash(current) == MemorySpecialistSourceHash(source))
                    {
                        foreach (var receipt in ReadDictionaryList(response, "sources"))
                        {
                            var expected = views.FirstOrDefault(v => new[] { "sourceId", "view", "sourceHash", "textHash" }
                                .All(k => ReadString(v, k, "") == ReadString(receipt, k, "")));
                            if (expected == null) continue;
                            StoreLateInteractionReceipt(connection, current, expected, epoch);
                        }
                        ExecuteSql(connection, "DELETE FROM memory_late_interaction_attempts WHERE turn_id=$id;", TestDict("id", source["turn_id"]));
                        // Empty observer projections have no searchable text but
                        // still need a completion receipt, preventing endless work.
                        foreach (string missingView in new[] { "self", "public" }.Except(views.Select(v => ReadString(v, "view", ""))))
                            StoreLateInteractionReceipt(connection, current, TestDict("view", missingView,
                                "sourceHash", MemorySpecialistSourceHash(current), "textHash", SemanticSha256Hex("")), epoch);
                    }
                    transaction.Commit();
                }
                recovery.Complete(endpoint);
            }
            catch (Exception ex) { DeferLateInteractionIndex(campaign, recovery, endpoint, phase, "worker_not_ready", ex); }
        }

        private static void RecordLateInteractionIndexFailure(string campaign, Dictionary<string, object> source, string outcome, Dictionary<string, object> response = null)
        {
            using var connection = OpenCampaignConnection(campaign);
            ExecuteSql(connection, @"INSERT INTO memory_late_interaction_attempts(turn_id,source_revision,model_hash,schema_version,attempts,next_attempt_ts,outcome)
VALUES($id,$revision,$model,$schema,1,$next,$outcome) ON CONFLICT(turn_id) DO UPDATE SET
attempts=CASE WHEN memory_late_interaction_attempts.source_revision=excluded.source_revision
 AND memory_late_interaction_attempts.model_hash=excluded.model_hash AND memory_late_interaction_attempts.schema_version=excluded.schema_version
 THEN memory_late_interaction_attempts.attempts+1 ELSE 1 END,source_revision=excluded.source_revision,
model_hash=excluded.model_hash,schema_version=excluded.schema_version,next_attempt_ts=excluded.next_attempt_ts,outcome=excluded.outcome;",
                TestDict("id", ReadString(source, "turn_id", ""), "revision", ReadString(source, "source_revision", ""),
                    "model", LateInteractionModelHash, "schema", LateInteractionIndexSchema, "next", DateTimeOffset.UtcNow.ToUnixTimeSeconds()+300,
                    "outcome", LateInteractionIndexOutcome(outcome)));
            var audit = response != null ? LateInteractionWorkerFailureDetails(response) : TestDict();
            audit["campaignId"] = campaign; audit["sourceId"] = ReadString(source, "turn_id", "");
            audit["outcome"] = LateInteractionIndexOutcome(outcome);
            try
            {
                if (LateInteractionReplayAudit.Value != null) LateInteractionReplayAudit.Value.Add(audit);
                else LogOperational("memory.late_interaction_index_source_failed", audit);
            }
            catch { }
        }

        private static void StoreLateInteractionReceipt(ReignDbConnection connection, Dictionary<string, object> source,
            Dictionary<string, object> view, string epoch) => ExecuteSql(connection, @"INSERT INTO memory_late_interaction_sources
(turn_id,view,source_hash,text_hash,source_revision,model_hash,schema_version,worker_epoch,updated_ts,transcript_hash)
VALUES($id,$view,$source,$text,$revision,$model,$schema,$epoch,$ts,$transcript)
ON CONFLICT(turn_id,view) DO UPDATE SET source_hash=excluded.source_hash,text_hash=excluded.text_hash,
source_revision=excluded.source_revision,model_hash=excluded.model_hash,schema_version=excluded.schema_version,
worker_epoch=excluded.worker_epoch,updated_ts=excluded.updated_ts,transcript_hash=excluded.transcript_hash;",
            TestDict("id", ReadString(source, "turn_id", ""), "view", ReadString(view, "view", ""),
                "source", ReadString(view, "sourceHash", ""), "text", ReadString(view, "textHash", ""),
                "revision", ReadString(source, "source_revision", ""), "model", LateInteractionModelHash,
                "schema", LateInteractionIndexSchema, "epoch", epoch, "ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                "transcript", MemoryTranscriptSourceHash(source)));
    }
}
