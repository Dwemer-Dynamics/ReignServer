using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string DenseReplayScopeKey(Dictionary<string, object> item) => PromptHash(CanonicalJson(TestDict(
            "observer", ReadString(item, "observerId", ""), "peer", ReadString(item, "peerId", ""),
            "worldDay", ReadDouble(item, "worldDay", 0), "asOfSequence", ReadLong(item, "asOfSequence", 0),
            "sources", ReadDictionaryList(item, "sources"))));

        private static string DenseReplayConfigError(Dictionary<string, object> input)
        {
            if (!input.ContainsKey("denseWorkerReplay")) return "";
            if (new[] { "captureLateInteraction", "captureMemorySelection", "enableLocalMemoryExtractor", "indexSpecialistSources" }
                .Any(k => ReadBool(input, k, false))
                || ReadDictionaryList(input, "cases").Any(item => ReadDictionary(item, "writerReplay") is { } writer
                    && new[] { "response", "continuation", "repairResponses" }.Any(writer.ContainsKey)))
                return "dense_replay_is_retrieval_only";
            var c = ReadDictionary(input, "denseWorkerReplay");
            if (c == null || c.Keys.Any(k => !new[] { "port", "token", "model", "modelTreeSha256", "rounds", "modelWindowTokens" }.Contains(k)))
                return "invalid_dense_replay_configuration";
            if (!c.TryGetValue("port", out var port) || port is not (int or long)
                || ReadInt(c, "port", 0) is < 40000 or > 65000) return "invalid_dense_replay_port";
            foreach (var field in new[] { "token", "modelTreeSha256" })
            {
                var value = ReadString(c, field, "");
                if (value.Length != (field == "token" ? 32 : 64) || value.Any(ch => !Uri.IsHexDigit(ch)))
                    return "invalid_dense_replay_" + field;
            }
            if (!new[] { "bge", "minilm" }.Contains(ReadString(c, "model", ""))) return "invalid_dense_replay_model";
            if (c.TryGetValue("rounds", out var rounds) && (rounds is not (int or long) || ReadInt(c, "rounds", 0) is < 1 or > 3))
                return "invalid_dense_replay_rounds";
            if (c.TryGetValue("modelWindowTokens", out var windowRaw) && windowRaw is not (int or long))
                return "invalid_dense_replay_window";
            int window = ReadInt(c, "modelWindowTokens", ReadString(c, "model", "") == "bge" ? 512 : 256);
            if (window != 256 && (window != 512 || ReadString(c, "model", "") != "bge")) return "invalid_dense_replay_window";
            return "";
        }

        private static List<Dictionary<string, object>> DenseReplayContracts()
        {
            var checks = new List<Dictionary<string, object>>();
            var valid = TestDict("port", 49123, "token", new string('b', 32), "model", "minilm", "modelTreeSha256", new string('a', 64));
            void Check(string id, bool passed) => checks.Add(TestDict("caseId", id, "passed", passed));
            Check("legacy_replay_without_worker_is_unchanged", DenseReplayConfigError(TestDict()) == "");
            Check("bounded_local_worker_is_valid", DenseReplayConfigError(TestDict("denseWorkerReplay", valid)) == "");
            var history = TestDict("observerId", "npc", "peerId", "player", "worldDay", 3d, "asOfSequence", 2L,
                "sources", new[] { TestDict("sourceId", "source", "speakerId", "player", "text", "The price is700.") });
            var nextQuestion = CloneDictionary(history); nextQuestion["query"] = "What price?";
            Check("questions_share_unchanged_history", DenseReplayScopeKey(history) == DenseReplayScopeKey(nextQuestion));
            nextQuestion["observerId"] = "other";
            Check("different_observers_do_not_share_history", DenseReplayScopeKey(history) != DenseReplayScopeKey(nextQuestion));
            nextQuestion = CloneDictionary(history); nextQuestion["asOfSequence"] = 1L;
            Check("different_cutoffs_do_not_share_history", DenseReplayScopeKey(history) != DenseReplayScopeKey(nextQuestion));
            foreach (var mutation in new[] { TestDict("port", 5102), TestDict("port", true), TestDict("port", 65001),
                TestDict("token", "../settings"), TestDict("model", "remote"), TestDict("modelTreeSha256", ""),
                TestDict("rounds", 4), TestDict("rounds", true), TestDict("modelWindowTokens", 512),
                TestDict("modelWindowTokens", true), TestDict("apiKey", "forbidden") })
            {
                var changed = CloneDictionary(valid);
                foreach (var pair in mutation) changed[pair.Key] = pair.Value;
                Check("reject_" + mutation.Keys.Single() + "_" + checks.Count,
                    DenseReplayConfigError(TestDict("denseWorkerReplay", changed)) != "");
            }
            return checks;
        }

        private sealed class DenseReplayWorker
        {
            internal readonly string Endpoint;
            internal readonly string Model;
            internal readonly string ModelTreeHash;
            internal readonly int Rounds;
            internal readonly Dictionary<string, object> InitialStatus;
            private readonly Dictionary<string, Dictionary<string, object>> indexedScopes = new();

            internal DenseReplayWorker(Dictionary<string, object> input)
            {
                if (ActiveServerPort > 0 || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") != "1"
                    || !ReignPostgreSqlStorage.Options.IsValidation || string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                    throw new InvalidOperationException("Dense replay requires the non-listening isolated validation database.");
                string error = DenseReplayConfigError(input);
                if (error.Length > 0) throw new InvalidOperationException(error);
                var c = ReadDictionary(input, "denseWorkerReplay");
                Endpoint = "http://127.0.0.1:" + ReadInt(c, "port", 0) + "/" + ReadString(c, "token", "");
                Model = ReadString(c, "model", ""); ModelTreeHash = ReadString(c, "modelTreeSha256", "");
                Rounds = ReadInt(c, "rounds", 3);
                InitialStatus = Post("/vectors/status", TestDict());
                var proof = ReadDictionary(InitialStatus, "denseReplay");
                int window = ReadInt(c, "modelWindowTokens", Model == "bge" ? 512 : 256);
                if (!ReadBool(proof, "isolated", false) || !ReadBool(proof, "externalNetworkBlocked", false)
                    || ReadString(proof, "model", "") != Model || ReadString(proof, "modelTreeSha256", "") != ModelTreeHash
                    || ReadInt(proof, "modelWindowTokens", 0) != window || !ReadBool(InitialStatus, "modelLoaded", false))
                    throw new InvalidOperationException("Dense replay worker identity or isolation does not match the pinned fixture.");
            }

            internal Dictionary<string, object> Post(string path, Dictionary<string, object> body)
            {
                var result = TryParseJsonObject(PostJsonToUrl(Endpoint + path, Json.Serialize(body), 30000)) ?? TestDict();
                if (!ReadBool(result, "ok", false)) throw new InvalidOperationException("Isolated dense worker failed: " + ReadString(result, "error", "unknown"));
                return result;
            }

            internal Dictionary<string, object> Index(string campaign, string observer, string peer,
                Dictionary<string, object> context, List<Dictionary<string, object>> sources, int caseIndex)
            {
                var clock = Stopwatch.StartNew();
                string scope = campaign + "|" + observer + "|" + ReadString(context, "timelineId", "");
                if (indexedScopes.TryGetValue(scope, out var prior))
                {
                    var reused = CloneDictionary(prior); reused["durationMs"] = 0L; reused["reusedHistory"] = true;
                    return reused;
                }
                var documents = new List<Dictionary<string, object>>();
                var bindings = new List<Dictionary<string, object>>();
                string generation;
                using (var connection = OpenCampaignConnection(campaign))
                {
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                    if (string.IsNullOrWhiteSpace(generation))
                        throw new InvalidOperationException("Dense replay requires an initialized source restore generation.");
                    var access = BuildKnowledgeAccessContext(observer, peer, "", context);
                    foreach (var source in sources)
                    {
                        string originalId = caseIndex + ":" + ReadString(source, "sourceId", "");
                        var original = LoadEmbeddingSourceRow(connection, "conversation_turn", originalId);
                        if (original == null || !PrecisionTurnVisible(original, observer, access, connection)) continue;
                        string memoryId = "dense_extract:" + originalId;
                        var payload = TestDict("timelineId", ReadString(context, "timelineId", ""),
                            "worldDay", ReadDouble(source, "worldDay", 0), "sourceTurnIds", new[] { originalId },
                            "sourceSpeakerId", ReadString(original, "speaker_id", ""),
                            "fixtureProjection", "verbatim_original_reported_speech_v1");
                        var audience = TextListFromJson(ReadString(original, "participants_json", "[]"));
                        string summary = ReadString(source, "speakerName", "Speaker") + " (reported "
                            + ReadString(source, "role", "speech") + "): " + ReadString(original, "text", "");
                        InsertMemoryRow(connection, memoryId, originalId, observer, "episodic", ReadLong(source, "sequence", 0),
                            ReadDouble(source, "worldDay", 0), "", summary, audience, new(), "[]", audience, new(), new(),
                            "private", .5d, 0d, 1d, new(), "active", "isolated_verbatim_projection", Json.Serialize(payload), "", "not_indexed");
                        var row = LoadEmbeddingSourceRow(connection, "memory", memoryId);
                        var document = BuildEmbeddingDocument(campaign, "memory", memoryId, row, BuildCanonicalEmbeddingText("memory", row), TestDict("vectorProvider", "local"));
                        // Match the production indexing queue's generation fence;
                        // the document builder alone does not attach it.
                        document.Payload["restoreGeneration"] = generation;
                        document.Payload["modelVersion"] = "isolated:" + Model + ":" + ModelTreeHash;
                        document.ContentHash = SemanticSha256Hex(document.Text + CanonicalJson(document.Payload));
                        documents.Add(TestDict("id", document.VectorId, "text", document.Text, "payload", document.Payload));
                        bindings.Add(TestDict("memoryId", memoryId, "sourceId", originalId,
                            "sourceTextSha256", PromptHash(ReadString(original, "text", "")),
                            "documentSha256", PromptHash(CanonicalJson(documents.Last()))));
                    }
                }
                var responses = new List<Dictionary<string, object>>();
                for (int offset = 0; offset < documents.Count; offset += 4)
                    responses.Add(Post("/vectors/upsert", TestDict("provider", "local", "collection", SemanticCollection,
                        "documents", documents.Skip(offset).Take(4).ToList())));
                var probe = Post("/vectors/search", TestDict("provider", "local", "collection", SemanticCollection,
                    "query", "Reign isolated index eligibility probe", "limit", 1, "filters", TestDict(
                        "campaignId", campaign, "status", "active", "sourceType", new[] { "memory" },
                        "timelineId", ReadString(context, "timelineId", ""), "observerId", observer,
                        "restoreGeneration", generation)));
                var hits = ReadDictionaryList(probe, "results");
                if (!ReadBool(probe, "eligibilityApplied", false) || hits.Count != 1
                    || !bindings.Any(b => ReadString(b, "memoryId", "") == ReadString(ReadDictionary(hits[0], "payload"), "sourceId", "")))
                    throw new InvalidOperationException("Dense replay index eligibility probe failed; do not grade an empty or foreign index.");
                var indexed = TestDict("projection", "verbatim_source_linked_memories_not_reconstructed_saved_derivatives",
                    "documents", documents.Count, "bindings", bindings, "durationMs", clock.ElapsedMilliseconds,
                    "workerResponses", responses, "reusedHistory", false,
                    "eligibilityProbe", TestDict("passed", true, "candidateCount", hits.Count, "restoreGeneration", generation));
                indexedScopes[scope] = indexed;
                return indexed;
            }
        }
    }
}
