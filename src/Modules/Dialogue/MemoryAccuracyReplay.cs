using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string MemoryReplayInputPath(string file)
        {
            if (ActiveServerPort > 0 || !HasArg(Environment.GetCommandLineArgs(), "--run-verification")
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)
                || !new[] { "input.json", "evidence-input.json" }.Contains(file))
                throw new InvalidOperationException("Replay input requires isolated artifact-bound verification.");
            string path = Path.Combine(TestsDir, "memory-accuracy", file);
            // Linux verification writes to a fresh temporary data root. Its
            // explicitly staged, read-only input remains beside this exact artifact.
            if (!File.Exists(path)) path = Path.Combine(AppContext.BaseDirectory, "data", "tests", "memory-accuracy", file);
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new InvalidOperationException("Stage bounded memory-accuracy/" + file + " beside the validated artifact.");
            return path;
        }

        private static string MemoryReplayInputError(Dictionary<string, object> item)
        {
            var sources = ReadDictionaryList(item, "availableSources");
            if (ReadString(item, "caseId", "").Length == 0 || ReadString(item, "query", "").Length == 0
                || sources.Count == 0 || sources.Count > 512) return "missing_or_unbounded_case";
            if (item.ContainsKey("requiredEvidence") || item.ContainsKey("semanticExpectation")) return "labels_must_not_enter_candidate";
            if (sources.Select(s => ReadString(s, "sourceId", "")).Distinct(StringComparer.Ordinal).Count() != sources.Count)
                return "duplicate_source";
            bool sequenceBound = item.ContainsKey("asOfSequence");
            bool timeBound = DateTimeOffset.TryParse(ReadString(item, "asOfUtc", ""), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var asOf);
            if (!sequenceBound && !timeBound) return "missing_as_of_boundary";
            foreach (var source in sources)
            {
                if (ReadString(source, "sourceId", "").Length == 0 || ReadString(source, "text", "").Length == 0
                    || ReadString(source, "text", "").Length > 24000
                    || !new[] { "player", "npc" }.Contains(ReadString(source, "speakerId", ""))) return "invalid_source";
                if (sequenceBound && (!source.ContainsKey("sequence") || ReadLong(source, "sequence", long.MaxValue) > ReadLong(item, "asOfSequence", 0)))
                    return "future_or_unsequenced_source";
                if (timeBound && (!DateTimeOffset.TryParse(ReadString(source, "availableUtc", ""), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var available) || available > asOf)) return "future_or_undated_source";
            }
            return "";
        }

        // Explicit CLI suite only. Private replay input lives beside this exact
        // validated artifact; no live campaign or external path is read.
        private static Dictionary<string, object> RunMemoryAccuracyReplay()
        {
            if (ActiveServerPort > 0 || !HasArg(Environment.GetCommandLineArgs(), "--run-verification")
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                || string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                throw new InvalidOperationException("Memory replay requires the isolated artifact-bound Verification Lab CLI.");
            string path = MemoryReplayInputPath("input.json");
            if (!File.Exists(path) || new FileInfo(path).Length > 32 * 1024 * 1024)
                throw new InvalidOperationException("A bounded memory-accuracy/input.json must be staged under the validated artifact's data/tests directory.");
            var input = ReadJsonObject(path);
            var cases = ReadDictionaryList(input, "cases");
            if (ReadString(input, "schema", "") != "reign-memory-retrieval-replay-input-v1" || cases.Count < 1 || cases.Count > 1000
                || cases.Select(c => ReadString(c, "caseId", "")).Distinct(StringComparer.Ordinal).Count() != cases.Count)
                throw new InvalidOperationException("Invalid memory replay manifest.");
            foreach (var item in cases)
            {
                string error = MemoryReplayInputError(item);
                if (error.Length > 0) throw new InvalidOperationException(ReadString(item, "caseId", "") + ": " + error);
            }
            var results = new List<Dictionary<string, object>>();
            string campaign = "memory_replay_" + Guid.NewGuid().ToString("N");
            try
            {
                for (int index = 0; index < cases.Count && !ShouldCancelVerification(); index++)
                {
                    var item = cases[index];
                    string npc = "replay_npc_" + index, player = "replay_player_" + index, session = "replay_session_" + index;
                    string query = ReadString(item, "query", ""), current = ReadString(item, "currentPlayerText", "");
                    var sources = ReadDictionaryList(item, "availableSources");
                    var context = TestDict("timelineId", "replay", "worldDay", 1d, "playerHeroStringId", player,
                        "conversationSessionId", session, "memoryActiveTopic", sources.LastOrDefault(s => ReadString(s, "speakerId", "") == "npc") is { } latest
                            ? ReadString(latest, "text", "") : "");
                    using (var connection = OpenCampaignConnection(campaign))
                    {
                        // Case-local identities and source IDs prevent a prior replay
                        // from becoming evidence for a later independent case.
                        ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,
start_ts,start_world_day,participants_json,payload_json) VALUES($session,$campaign,$npc,$player,'in_person','open',1,1,$participants,$payload);",
                            TestDict("session", session, "campaign", campaign, "npc", npc, "player", player,
                                "participants", Json.Serialize(new[] { npc, player }), "payload", Json.Serialize(context)));
                        int ordinal = 0;
                        foreach (var source in sources)
                        {
                            string role = ReadString(source, "speakerId", ""), text = ReadString(source, "text", "");
                            if (ReadString(source, "sourceId", "") == ReadString(item, "currentSourceId", "")) continue;
                            InsertConversationTurn(connection, index + ":" + ReadString(source, "sourceId", ""), session,
                                "event_" + index + "_" + ordinal, ordinal, "exchange_" + index + "_" + ordinal / 2,
                                role, role == "npc" ? npc : player, role == "npc" ? "NPC" : "Player", text, "in_person", 1d,
                                ordinal + 1, "", new List<string> { npc, player }, context);
                            ordinal++;
                        }
                    }
                    var transcript = CanonicalizePromptTranscript(ReadCurrentSessionDialogueLines(campaign, session, 30), 30);
                    SetMemoryTranscriptContext(context, transcript);
                    var settings = TestDict("useMemoryLlmForConsolidation", false, "enableSemanticMemory", false,
                        "memoryPrecisionMode", "legacy", "enableMemoryPrecisionCrossEncoder", false,
                        "highMemoryMode", false, "memoryPrecisionOrdinaryTokens", 4000);
                    var timer = Stopwatch.StartNew();
                    var legacy = BuildNpcMemoryPacket(campaign, npc, player, "", query, 6000, context, settings);
                    long legacyMs = timer.ElapsedMilliseconds;
                    timer.Restart();
                    var precision = BuildPrecisionMemoryPacket(campaign, npc, player, "", query, context, settings, true);
                    long precisionMs = timer.ElapsedMilliseconds;
                    string common = "CURRENT PLAYER [speakerId=player]: " + current + "\nRECENT DIALOGUE:\n" + FormatEventLinesForPrompt(transcript);
                    string legacyText = ReadString(legacy, "memoryPacket", ""), precisionText = ReadString(precision, "memoryPacket", "");
                    results.Add(TestDict("caseId", ReadString(item, "caseId", ""), "legacyPacket", legacyText,
                        "precisionPacket", precisionText, "commonTranscript", common, "precisionEvidence", ReadDictionary(precision, "precisionEvidence"),
                        "legacyRetrievalMs", legacyMs, "precisionRetrievalMs", precisionMs,
                        "legacyMemoryTokensEstimate", EstimateContinuityTokens(legacyText), "precisionMemoryTokensEstimate", EstimateContinuityTokens(precisionText),
                        "commonTranscriptTokensEstimate", EstimateContinuityTokens(common), "providerCalls", 0,
                        "sourceCount", sources.Count, "sourceSetHash", PromptHash(CanonicalJson(sources))));
                }
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            var report = TestDict("schema", "reign-memory-retrieval-replay-result-v1", "ok", results.Count == cases.Count,
                "inputSha256", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                "completedCases", results.Count, "expectedCases", cases.Count, "providerCalls", 0, "cases", results,
                "semanticReplay", false, "certificateEligible", false,
                "coverageLimits", new[] { "Original attributed dialogue only; unavailable historical derivative/world context is not recreated.",
                    "Precision SQL path compared with legacy SQL; semantic worker/reranker not invoked.",
                    "Memory and common transcript token estimates are not full provider-input or generation measurements.",
                    "Retrieval outputs require independent source/answer coverage evaluation and later semantic/voice/native acceptance." });
            string resultPath = Path.Combine(TestsDir, "memory-accuracy", "retrieval-result-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            WriteJsonObject(resultPath, report);
            return TestDict("ok", results.Count == cases.Count, "resultPath", resultPath, "completedCases", results.Count,
                "inputSha256", report["inputSha256"], "certificateEligible", false, "providerCalls", 0);
        }
    }
}
