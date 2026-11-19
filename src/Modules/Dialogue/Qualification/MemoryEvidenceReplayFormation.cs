using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private const string SceneFormationReplaySchema = "reign-scene-formation-replay-input-v1";

        private static Dictionary<string, object> SceneFormationEvidenceManifest(Dictionary<string, object> input)
        {
            if (!ReignPostgreSqlStorage.Options.IsValidation || input.Keys.Any(key => key is not ("schema" or "cases"))
                || !IsConversationObjectArray(input.GetValueOrDefault("cases")))
                throw new InvalidOperationException("Scene formation requires a source-only isolated manifest.");
            var cases = ReadDictionaryList(input, "cases");
            if (cases.Count is < 1 or > 16) throw new InvalidOperationException("Scene formation requires 1 to 16 bounded cases.");
            int total = 0;
            foreach (var item in cases)
            {
                string observer = ReadString(item, "observerId", ""), peer = ReadString(item, "peerId", "");
                double day = ReadDouble(item, "worldDay", double.NaN);
                long cutoff = ReadLong(item, "asOfSequence", -1);
                if (item.Keys.Any(key => key is not ("caseId" or "observerId" or "peerId" or "worldDay" or "asOfSequence" or "sources"))
                    || observer.Length is < 1 or > 200 || peer.Length is < 1 or > 200 || observer == peer
                    || !double.IsFinite(day) || day < 0 || cutoff < 1 || !IsConversationObjectArray(item.GetValueOrDefault("sources")))
                    throw new InvalidOperationException("Scene formation cases contain only scope and eligible sources.");
                var sources = ReadDictionaryList(item, "sources");
                total += sources.Count;
                if (sources.Count is < 1 or > 128 || total > 512
                    || sources.Select(row => ReadLong(row, "sequence", -1)).Distinct().Count() != sources.Count)
                    throw new InvalidOperationException("Scene formation sources must be bounded and have distinct accepted sequences.");
                foreach (var row in sources)
                {
                    var audience = ReadStringList(row, "audience");
                    string role = ReadString(row, "role", ""), speaker = ReadString(row, "speakerId", "");
                    long sequence = ReadLong(row, "sequence", -1);
                    double sourceDay = ReadDouble(row, "worldDay", double.NaN);
                    if (row.Keys.Any(key => key is not ("sourceId" or "sessionId" or "exchangeId" or "role" or "speakerId"
                        or "speakerName" or "text" or "audience" or "sequence" or "worldDay"))
                        || role is not ("npc" or "player") || speaker != (role == "npc" ? observer : peer)
                        || !audience.Contains(observer) || !audience.Contains(peer) || audience.Count > 16
                        || ReadString(row, "sessionId", "").Length is < 1 or > 512 || sequence < 1 || sequence > cutoff
                        || !double.IsFinite(sourceDay) || sourceDay < 0 || sourceDay > day)
                        throw new InvalidOperationException("Scene formation sources must be older, attributed and shared with the evaluated pair.");
                }
            }
            // The common seeder expects a query field. This marker never reaches
            // retrieval or formation, and the external manifest admits no query.
            return TestDict("schema", "reign-memory-evidence-replay-input-v1", "cases", cases.Select(item => {
                var seed = Program.CloneDictionary(item);
                seed["query"] = "scene_formation_seed_only";
                return seed;
            }).ToList());
        }

        private static Dictionary<string, object> CaptureSceneFormation(string campaign, string caseId, int scope, IEnumerable<string> sessions)
        {
            var scenes = new List<Dictionary<string, object>>();
            foreach (string sessionId in sessions)
            {
                Dictionary<string, object> session;
                List<Dictionary<string, object>> turns;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var key = TestDict("id", sessionId);
                    session = ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id AND status='closed';", key).Single();
                    turns = ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id ORDER BY accepted_sequence;", key);
                }
                var arms = new List<Dictionary<string, object>>();
                List<string> options = null;
                foreach (int budget in new[] { 1800, 5000 })
                {
                    var settings = TestDict("useMemoryLlmForConsolidation", false, "memoryConsolidationMaxSummaryChars", budget,
                        "enableSemanticMemory", false, "enableMinime", false, "enableMinimeMemoryWorker", false);
                    List<string> currentOptions = null;
                    var timer = Stopwatch.StartNew();
                    var result = CreateConversationSceneSummary(campaign, session, turns, ReadLong(session, "end_ts", 0), settings,
                        captureSourceRecords: records => currentOptions = records.ToList());
                    double formationMs = timer.Elapsed.TotalMilliseconds;
                    if (currentOptions == null || currentOptions.Count == 0 || options != null && !options.SequenceEqual(currentOptions))
                        throw new InvalidOperationException("The native formation source options were empty or changed between budgets.");
                    options = currentOptions;
                    string summary = ReadString(result, "summary", ""), id = ReadString(result, "summaryId", "");
                    var diagnostic = ReadDictionary(result, "compactionDiagnostic");
                    using var read = ReignDatabase.OpenCampaignConnection(campaign);
                    var stored = ReignDatabase.QuerySql(read, "SELECT summary,status,payload_json FROM summaries WHERE summary_id=$id;", TestDict("id", id)).Single();
                    var indexed = ReignDatabase.QuerySql(read, "SELECT text FROM summary_fts WHERE summary_id=$id;", TestDict("id", id)).Single();
                    var linked = ReignDatabase.QuerySql(read, @"SELECT source_id FROM memory_sources WHERE document_type='summary' AND document_id=$id
AND source_type='turn' ORDER BY ordinal;", TestDict("id", id)).Select(row => ReadString(row, "source_id", "")).ToList();
                    if (!ReadBool(result, "ok", false) || ReadBool(diagnostic, "providerAttempted", true) || ReadString(stored, "status", "") != "active"
                        || ReadString(stored, "summary", "") != summary || ReadString(indexed, "text", "") != summary
                        || !linked.SequenceEqual(turns.Select(row => ReadString(row, "turn_id", ""))))
                        throw new InvalidOperationException("Scene formation failed its no-provider, persistence or source-link receipt.");
                    var payload = TryParseJsonObject(ReadString(stored, "payload_json", ""));
                    arms.Add(TestDict("budgetChars", budget, "summaryId", id, "summary", summary, "summaryChars", summary.Length,
                        "method", ReadString(payload, "method", ""), "compactionDiagnostic", diagnostic,
                        "selectedSourceIndices", payload.GetValueOrDefault("selectedSourceIndices"), "formationMs", formationMs,
                        "linkedTurnIds", linked, "persistedAndIndexed", true));
                }
                int fullHeader = MemorySummarySelectionText(new() { "" }, new() { 1 }).Length;
                int partialHeader = MemorySummarySelectionText(new() { "", "" }, new() { 1 }).Length;
                int separator = MemorySummarySelectionText(new() { "", "" }, new() { 1, 2 }).Length - fullHeader;
                scenes.Add(TestDict("sessionId", sessionId, "sourceTurnIds", turns.Select(row => ReadString(row, "turn_id", "")).ToList(),
                    "sourceOptions", options, "completeHeaderChars", fullHeader, "partialHeaderChars", partialHeader,
                    "separatorChars", separator, "arms", arms));
            }
            return TestDict("caseId", caseId, "sourceScopeIndex", scope, "sceneFormation", scenes, "fullyReplayed", true);
        }
    }
}
