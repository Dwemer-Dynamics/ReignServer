using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string MemorySelectionSchema = "reign-memory-selection-v1";
        private const string MemorySelectionRankingPolicy = "complete-source-window-max-v1";
        private const string MemorySelectionRankerHash = "91541a53f5faa6796553aa0d29fea3b24abceccfd45ccfeb78a267d4ea34908a";
        private const string MemorySelectionRuntimeVersion = "1.22.0";
        private const string MemorySelectionRankerTokenizerHash = "aeb13307a71acd8fe81861d94ad54ab689df773318809eed3cbe794b4492dae4";
        private const string MemorySelectionCompressorHash = "6745c7aff5cb5c2c3c96b8e72ab8a65ce42157058f9221f6c340f0c37e760f5d";
        private const string MemorySelectionCompressorTokenizerHash = "bf1b59b7b11c95f194f51708d918eea378e09d05f84c0e1656dc5180e8117088";
        private static readonly AsyncLocal<Func<Dictionary<string, object>, Dictionary<string, object>>> MemorySelectionReplay = new();
        private static long MemorySelectionUnavailableUntilTicks;

        // Worker receipts bind the exact UTF-8 body. PromptHash intentionally
        // normalizes line whitespace and must not be used for transport fences.
        private static string MemorySelectionTextHash(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? "")));

        private static bool MemorySelectionQualified(Dictionary<string, object> settings, bool compression)
        {
            var c = ReadDictionary(settings, "memorySelectionQualification");
            return ReadString(c, "schema", "") == "reign-memory-selection-certificate-v1"
                && ReadString(c, "pipelineSchema", "") == MemorySelectionSchema
                && ReadString(c, "rankingPolicy", "") == MemorySelectionRankingPolicy
                && ReadString(c, "rankerHash", "") == MemorySelectionRankerHash
                && ReadString(c, "rankerTokenizerHash", "") == MemorySelectionRankerTokenizerHash
                && ReadString(c, "runtimeVersion", "") == MemorySelectionRuntimeVersion
                && new[] { "cpu", "cuda" }.Contains(ReadString(c, "runtimeBackend", ""))
                && (!compression || ReadString(c, "compressorHash", "") == MemorySelectionCompressorHash
                    && ReadString(c, "compressorTokenizerHash", "") == MemorySelectionCompressorTokenizerHash
                    && ReadBool(c, "compressionEvaluated", false) && ReadInt(c, "lostProtectedSpans", -1) == 0)
                && Regex.IsMatch(ReadString(c, "heldOutCorpusSha256", ""), "^[a-f0-9]{64}$")
                && ReadBool(c, "heldOut", false) && ReadBool(c, "independentlyLabeled", false)
                && ReadInt(c, "caseCount", 0) >= 100 && ReadInt(c, "criticalErrors", -1) == 0
                && new[] { "requiredFactCoverage", "answerCoverage", "baselineAnswerCoverage", "quality", "baselineQuality" }
                    .All(k => double.IsFinite(ReadDouble(c, k, double.NaN)) && ReadDouble(c, k, -1) >= 0 && ReadDouble(c, k, 2) <= 1)
                && ReadDouble(c, "requiredFactCoverage", -1) >= .98 && ReadDouble(c, "answerCoverage", -1) >= .98
                && ReadDouble(c, "answerCoverage", -1) >= ReadDouble(c, "baselineAnswerCoverage", 2)
                && ReadDouble(c, "quality", -1) > ReadDouble(c, "baselineQuality", 2)
                && double.IsFinite(ReadDouble(c, "rerankP95Ms", double.NaN)) && ReadDouble(c, "rerankP95Ms", -1) > 0
                && double.IsFinite(ReadDouble(c, "baselineP95Ms", double.NaN)) && ReadDouble(c, "baselineP95Ms", -1) > 0
                && ReadDouble(c, "rerankP95Ms", double.MaxValue) <= 1.2 * ReadDouble(c, "baselineP95Ms", 0);
        }

        private static Dictionary<string, object> RequestMemorySelection(Dictionary<string, object> request,
            Dictionary<string, object> settings, int timeout)
        {
            if (!string.IsNullOrWhiteSpace(CampaignsRootOverride.Value))
                return MemorySelectionReplay.Value?.Invoke(request) ?? TestDict("ok", false, "status", "isolated_no_capture");
            return TryParseJsonObject(PostJsonToUrl(SemanticWorkerUrl(settings, "/memory/selection"), Json.Serialize(request), timeout + 500));
        }

        private static bool ValidateMemorySelectionResponse(Dictionary<string, object> response, string operation,
            List<Dictionary<string, object>> candidates, out Dictionary<string, Dictionary<string, object>> results, string backend = "cpu")
        {
            results = new(StringComparer.Ordinal);
            string model = operation == "rank" ? MemorySelectionRankerHash : MemorySelectionCompressorHash;
            string tokenizer = operation == "rank" ? MemorySelectionRankerTokenizerHash : MemorySelectionCompressorTokenizerHash;
            if (!ReadBool(response, "ok", false) || ReadString(response, "schema", "") != MemorySelectionSchema
                || ReadString(response, "operation", "") != operation || ReadString(response, "modelHash", "") != model
                || ReadString(response, "tokenizerHash", "") != tokenizer
                || ReadString(response, "rankingPolicy", "") != MemorySelectionRankingPolicy
                || ReadString(response, "backend", "") != backend
                || ReadString(response, "runtimeVersion", "") != MemorySelectionRuntimeVersion) return false;
            var expected = candidates.ToDictionary(c => ReadString(c, "id", ""), StringComparer.Ordinal);
            foreach (var row in ReadDictionaryList(response, "results"))
            {
                string id = ReadString(row, "id", "");
                if (!expected.TryGetValue(id, out var candidate) || !results.TryAdd(id, row)
                    || ReadString(row, "textHash", "") != ReadString(candidate, "textHash", "")) return false;
                double score = ReadDouble(row, "score", double.NaN);
                if (operation == "rank" && (!double.IsFinite(score) || score < 0 || score > 1
                    || !ValidateMemoryScoringCoverage(ReadDictionary(row, "scoringCoverage"), ReadString(candidate, "text", "")))) return false;
            }
            return results.Count == expected.Count;
        }

        private static bool ValidateMemoryScoringCoverage(Dictionary<string, object> coverage, string text)
        {
            if (coverage == null || text.Length == 0) return false;
            var spans = ReadObjectList(coverage, "sourceSpansUtf16");
            int count = ReadInt(coverage, "windowCount", 0);
            int tokens = ReadInt(coverage, "maxWindowTokens", 0);
            if (count < 1 || count > 16 || count != spans.Count || tokens < 1 || tokens > 2048
                || ReadString(coverage, "sourceCoverage", "") != (count == 1 ? "complete" : "complete_overlapping_source_windows")) return false;
            int priorStart = -1, priorEnd = 0;
            foreach (var span in spans)
            {
                if (span is not System.Collections.IEnumerable values) return false;
                var pair = values.Cast<object>().ToArray();
                if (pair.Length != 2 || !Offset(pair[0], out int start) || !Offset(pair[1], out int end)
                    || start < 0 || start <= priorStart || start > priorEnd || end <= priorEnd || end > text.Length
                    || start >= end || SplitSurrogate(start) || SplitSurrogate(end)) return false;
                priorStart = start; priorEnd = end;
            }
            return priorEnd == text.Length;

            bool SplitSurrogate(int offset) => offset > 0 && offset < text.Length
                && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
            static bool Offset(object value, out int offset)
            {
                offset = value is int i ? i : value is long l && l >= int.MinValue && l <= int.MaxValue ? (int)l : -1;
                return offset >= 0;
            }
        }

        private static void FinalizeMemorySelectionAudit(Dictionary<string, object> diagnostics, MemoryEvidencePacket packet, bool shadow)
        {
            var audit = ReadDictionary(diagnostics, "selectionHelpers");
            if (audit == null) return;
            audit["compressionSelectedSourceCount"] = packet.Packing.Count;
            audit["compressionApplied"] = !shadow && packet.Packing.Count > 0;
        }

        private static string BuildMemorySelectionQuery(MemoryQueryPlan plan, Dictionary<string, object> context)
        {
            string current = "CURRENT CONTRIBUTION:\n" + plan.Query;
            var recent = new List<string>();
            foreach (var turn in ReadDictionaryList(context, "memoryEvidenceRecentTurns").TakeLast(4).Reverse())
            {
                string entry = "\nPRIOR " + ReadString(turn, "speakerId", ReadString(turn, "role", "unknown")) + ": " + ReadString(turn, "text", "");
                if (current.Length + recent.Sum(s => s.Length) + entry.Length <= 2400) recent.Add(entry);
            }
            if (recent.Count == 0) return current;
            recent.Reverse();
            // Recent dialogue resolves short answers and references, but must not
            // replace the current contribution as the relevance target after a topic change.
            return "BACKGROUND FOR RESOLVING REFERENCES ONLY:\n" + string.Join("", recent).TrimStart('\n')
                + "\n\nRetrieve evidence for the CURRENT CONTRIBUTION below. Prior topics alone do not establish relevance.\n" + current;
        }

        private static List<MemoryEvidence> ApplyLocalMemorySelection(List<MemoryEvidence> candidates, MemoryQueryPlan plan,
            Dictionary<string, object> context, Dictionary<string, object> settings, Dictionary<string, object> diagnostics, bool shadow)
        {
            // Canonical duplicates are removed before spending model time; source
            // identity and the exact authorized observer view both matter.
            candidates = DistinctMemoryBundles(candidates, plan.SuppliedSources);
            string mode = ReadString(settings, "memorySelectionMode", ReadBool(settings, "enableMemoryPrecisionCrossEncoder", false) ? "on" : "off");
            bool isolated = mode == "replay" && !string.IsNullOrWhiteSpace(CampaignsRootOverride.Value) && MemorySelectionReplay.Value != null;
            bool compression = ReadBool(settings, "enableMemoryEvidenceCompression", false);
            var audit = TestDict("schema", MemorySelectionSchema, "mode", mode, "ranker", "Qwen/Qwen3-Reranker-0.6B",
                "rankingPolicy", MemorySelectionRankingPolicy,
                "rankerHash", MemorySelectionRankerHash, "compressor", "LLMLingua-2", "rankApplied", false,
                "compressionApplied", false, "compressionViewsPrepared", false, "remoteCalls", 0, "localRequests", 0, "status", "disabled");
            diagnostics["selectionHelpers"] = audit;
            if (!new[] { "on", "shadow", "replay" }.Contains(mode)) return candidates;
            if (mode == "replay" && !isolated) { audit["status"] = "replay_requires_isolation"; return candidates; }
            bool qualified = MemorySelectionQualified(settings, false);
            bool apply = isolated || mode == "on" && !shadow && qualified;
            if (mode == "on" && !qualified && !isolated)
            { audit["status"] = "held_out_qualification_required"; return candidates; }
            if (!isolated && DateTime.UtcNow.Ticks < Interlocked.Read(ref MemorySelectionUnavailableUntilTicks))
            { audit["status"] = "local_fallback_cooldown"; return candidates; }
            var timer = Stopwatch.StartNew();
            int timeout = Math.Clamp(ReadInt(settings, "memorySelectionTimeoutMs", 15000), 500, 30000);
            try
            {
                string backend = isolated || mode == "shadow" ? ReadString(settings, "memorySelectionBackend", "cpu")
                    : ReadString(ReadDictionary(settings, "memorySelectionQualification"), "runtimeBackend", "");
                if (!new[] { "cpu", "cuda" }.Contains(backend)) throw new InvalidOperationException("invalid_selection_backend");
                audit["backend"] = backend; audit["runtimeVersion"] = MemorySelectionRuntimeVersion;
                var ranked = candidates.Where(e => !e.Required).OrderByDescending(e => e.Score)
                    .ThenBy(e => e.SourceId, StringComparer.Ordinal).Take(32).ToList();
                if (ranked.Count == 0) { audit["status"] = "no_optional_candidates"; return candidates; }
                string query = BuildMemorySelectionQuery(plan, context);
                var inputs = ranked.Select((e, i) => TestDict("id", "r" + i, "text", e.Text, "textHash", MemorySelectionTextHash(e.Text))).ToList();
                var request = TestDict("schema", MemorySelectionSchema, "operation", "rank", "query", query,
                    "candidates", inputs, "timeoutMs", timeout, "backend", backend, "rankingPolicy", MemorySelectionRankingPolicy);
                audit["candidateCount"] = ranked.Count;
                audit["localRequests"] = 1;
                var response = RequestMemorySelection(request, settings, timeout);
                audit["rankMs"] = ReadDouble(response, "elapsedMs", timer.ElapsedMilliseconds);
                if (!ValidateMemorySelectionResponse(response, "rank", inputs, out var scores, backend))
                    throw new InvalidOperationException("rank_rejected:" + ReadString(response, "status", "invalid_or_incomplete"));
                var order = ranked.Select((e, i) => new { Evidence = e, Alias = "r" + i, Score = ReadDouble(scores["r" + i], "score", 0) })
                    .OrderByDescending(x => x.Score).ThenBy(x => x.Alias, StringComparer.Ordinal).ToList();
                audit["ranks"] = order.Select((x, i) => TestDict("sourceId", x.Evidence.SourceId, "textHash", MemorySelectionTextHash(x.Evidence.Text),
                    "rank", i, "relevance", x.Score, "priorPriority", x.Evidence.Score,
                    "scoringCoverage", ReadDictionary(scores[x.Alias], "scoringCoverage") ?? TestDict())).ToList();
                if (apply)
                    for (int i = 0; i < order.Count; i++)
                    {
                        order[i].Evidence.SelectionRank = i;
                        order[i].Evidence.SelectionRelevance = order[i].Score;
                    }
                audit["rankApplied"] = apply;
                audit["status"] = apply ? "ranked" : "shadow_not_applied";
                if (!compression || !(isolated || MemorySelectionQualified(settings, true))) return candidates;
                // Spend compression time only where it can change allocation.
                // Shadow predicts the ranked allocation on copies, without altering the writer.
                var previewCandidates = candidates.Select(e => CopyMemoryEvidence(e)).ToList();
                for (int i = 0; i < order.Count; i++)
                {
                    var previewEvidence = previewCandidates[candidates.IndexOf(order[i].Evidence)];
                    previewEvidence.SelectionRank = i;
                    previewEvidence.SelectionRelevance = order[i].Score;
                }
                var preview = SelectPrecisionEvidence(previewCandidates,
                    Math.Clamp(ReadInt(settings, "memoryPrecisionOrdinaryTokens", 4000), 2000, 8000), plan.SuppliedSources);
                var pressure = preview.Rejected.Where(r => ReadString(r, "reason", "") == "whole_record_budget")
                    .GroupBy(r => ReadString(r, "sourceId", ""), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Max(r => ReadInt(r, "remainingTokens", 0)), StringComparer.Ordinal);
                var packable = order.Where(x => x.Evidence.Text.Length >= 1800 && pressure.ContainsKey(x.Evidence.SourceId))
                    .Select(x => new { x.Evidence, x.Alias, Segments = MemoryPackingSegments(x.Evidence, plan) })
                    .Where(x => x.Segments.Count > 0 && MinimumMemoryPackingFits(x.Evidence, x.Segments,
                        pressure[x.Evidence.SourceId], preview.Selected.Count + 1)).Take(8).ToList();
                audit["compressionBudgetRejectedCount"] = pressure.Count;
                if (packable.Count == 0) { audit["compressionStatus"] = "no_beneficial_safe_compaction"; return candidates; }
                var packingInputs = packable.Select(x => TestDict("id", x.Alias, "text", x.Evidence.Text,
                    "textHash", MemorySelectionTextHash(x.Evidence.Text), "segments", x.Segments, "rate", .65d)).ToList();
                audit["localRequests"] = 2;
                var packed = RequestMemorySelection(TestDict("schema", MemorySelectionSchema, "operation", "compress", "query", "",
                    "candidates", packingInputs, "timeoutMs", timeout, "backend", "cpu", "rankingPolicy", MemorySelectionRankingPolicy), settings, timeout);
                audit["compressionMs"] = ReadDouble(packed, "elapsedMs", 0);
                if (!ValidateMemorySelectionResponse(packed, "compress", packingInputs, out var views))
                { audit["compressionStatus"] = "invalid_or_incomplete_originals_retained"; return candidates; }
                // Validate every result on copies before accepting any compact view.
                var copies = packable.Select(x => CopyMemoryEvidence(x.Evidence)).ToList();
                if (packable.Where((x, i) => !AcceptMemoryPacking(copies[i], x.Segments, views[x.Alias])).Any())
                { audit["compressionStatus"] = "protected_span_or_source_failure_originals_retained"; return candidates; }
                if (apply)
                    for (int i = 0; i < packable.Count; i++)
                    {
                        packable[i].Evidence.PackingText = copies[i].PackingText;
                        packable[i].Evidence.PackingTurns = copies[i].PackingTurns;
                        packable[i].Evidence.PackingAudit = copies[i].PackingAudit;
                    }
                audit["compressionViewsPrepared"] = copies.Any(c => c.PackingText.Length > 0);
                audit["compressionStatus"] = apply ? "source_bound_views_available" : "shadow_views_not_applied";
                audit["compressionViews"] = copies.Where(c => c.PackingAudit != null).Select(c => c.PackingAudit).ToList();
            }
            catch (Exception ex)
            {
                audit["status"] = ReadBool(audit, "rankApplied", false) ? "ranked_compression_unavailable" : "local_fallback";
                audit["failure"] = LimitText(ex.Message, 180);
                if (!isolated && !ReadBool(audit, "rankApplied", false))
                    Interlocked.Exchange(ref MemorySelectionUnavailableUntilTicks, DateTime.UtcNow.AddSeconds(30).Ticks);
            }
            finally { audit["elapsedMs"] = timer.ElapsedMilliseconds; }
            return candidates;
        }

        private static bool MinimumMemoryPackingFits(MemoryEvidence evidence, List<Dictionary<string, object>> segments,
            int remainingTokens, int aliasIndex)
        {
            var minimum = CopyMemoryEvidence(evidence);
            var protectedIndices = segments.Where(s => ReadBool(s, "protected", true)).Select(s => ReadInt(s, "index", -1)).ToArray();
            if (!AcceptMemoryPacking(minimum, segments, TestDict("textHash", MemorySelectionTextHash(evidence.Text),
                "selectedSegments", protectedIndices)) || minimum.PackingText.Length == 0) return false;
            var compact = CopyMemoryEvidence(minimum, minimum.PackingText, minimum.PackingTurns);
            return Math.Max(1, EstimateContinuityTokens(RenderMemoryEvidence(compact, "m" + aliasIndex)) - 128) <= remainingTokens;
        }
    }
}
