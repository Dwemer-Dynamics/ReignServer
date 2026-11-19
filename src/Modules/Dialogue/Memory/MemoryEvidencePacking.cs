using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Process-owned delivery plan. A payload field or model-provided source ID
        // cannot authorize deleting evidence from a prompt. Registered copies are
        // complete required transcript/reference records, before receipt capture.
        private static readonly ConditionalWeakTable<Dictionary<string, object>, Dictionary<string, string>> PromptDeliveredSources = new();
        internal static readonly ConditionalWeakTable<Dictionary<string, object>, List<Dictionary<string, object>>> PromptStatementSources = new();
        private const int RequiredConversationTailCount = 30;

        internal static Dictionary<string, string> PlannedPromptSources(Dictionary<string, object> context) =>
            context != null && PromptDeliveredSources.TryGetValue(context, out var sources) ? sources : null;

        private static bool TryProjectBoundTranscript(List<Dictionary<string, object>> lines,
            Func<List<Dictionary<string, object>>> project)
        {
            try
            {
                var visible = project().ToList();
                lines.Clear(); lines.AddRange(visible);
                return true;
            }
            catch { lines.Clear(); return false; } // Visibility cannot fail open.
        }

        private static Dictionary<string, object> MatchPromptSource(Dictionary<string, object> line,
            IEnumerable<Dictionary<string, object>> candidates)
        {
            // JSONL `id` identifies a storage row, not a conversation_turns key.
            // Only an explicitly bound durable ID can override the exchange match.
            string id = ReadFirstString(line, "sourceTurnId", "turn_id");
            string exchange = ReadFirstString(line, "exchangeId", "exchange_id", "sceneTurnId", "turnId");
            string session = ReadFirstString(line, "sessionId", "session_id");
            string speaker = ReadFirstString(line, "speakerHeroStringId", "speaker_id", "speakerId");
            var matches = candidates.Where(row => (id.Length > 0 ? id == ReadString(row, "turn_id", "")
                    : exchange.Length > 0 && exchange == ReadString(row, "exchange_id", ""))
                && (session.Length == 0 || session == ReadString(row, "session_id", ""))
                && speaker.Length > 0 && speaker == ReadString(row, "speaker_id", "")
                && ReadString(line, "role", "") == ReadString(row, "role", "")
                && ReadString(line, "text", "") == ReadString(row, "text", "")).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        internal static void BindPromptTranscriptSources(string campaign, string observer, Dictionary<string, object> context,
            List<Dictionary<string, object>> lines)
        {
            var supplied = PromptDeliveredSources.GetValue(context, _ => new(StringComparer.Ordinal));
            supplied.Clear();
            var statements = PromptStatementSources.GetValue(context, _ => new());
            statements.Clear();
            context["promptSourceBinding"] = TestDict("schema", "reign-prompt-source-binding-v1", "sourceCount", 0,
                "status", "withheld_or_not_bound", "semanticValidation", "not_claimed");
            if (!TryProjectBoundTranscript(lines, () => ProjectConversationLinesForObserver(lines, observer, context)))
            {
                context["promptSourceBinding"] = TestDict("status", "projection_unavailable_history_withheld", "sourceCount", 0);
                SetMemoryTranscriptContext(context, lines);
                return;
            }
            try
            {
            var ids = lines.Select(l => ReadFirstString(l, "sourceTurnId", "turn_id", "id")).Where(id => id.Length > 0).Distinct().ToList();
            var exchanges = lines.Select(l => ReadFirstString(l, "exchangeId", "exchange_id", "sceneTurnId", "turnId"))
                .Append(ReadFirstString(context, "sceneTurnId", "turnId", "exchangeId", "contributionId"))
                .Where(id => id.Length > 0).Distinct().ToList();
            if (PlayerInputSources.TryGetValue(context, out var input) && input.SourceId.Length > 0) ids.Add(input.SourceId);
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            var access = Program.BuildKnowledgeAccessContext(observer, ReadFirstString(context, "playerHeroStringId", "mainHeroStringId", "playerId"), "", context);
            if (access.PlayerIdentityUnknown || ReadBool(context, "officialMemoryFirewall", false)) return;
            var candidates = ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE status='active' AND
(turn_id IN (SELECT jsonb_array_elements_text(CAST($ids AS jsonb)))
OR exchange_id IN (SELECT jsonb_array_elements_text(CAST($exchanges AS jsonb)))) ORDER BY ts DESC,turn_order DESC LIMIT 512;",
                TestDict("ids", Json.Serialize(ids), "exchanges", Json.Serialize(exchanges)))
                .Where(row => PrecisionTurnVisible(row, observer, access, connection)).ToList();
            int bound = 0;
            foreach (var line in lines)
            {
                var source = MatchPromptSource(line, candidates);
                if (source == null) continue;
                line["sourceTurnId"] = ReadString(source, "turn_id", "");
                line["sessionId"] = ReadString(source, "session_id", "");
                line["exchangeId"] = ReadString(source, "exchange_id", "");
                line["acceptedSequence"] = ReadLong(source, "accepted_sequence", 0);
                bound++;
            }
            // The current input is sent separately, after history. Bind existing
            // floor/accepted views to that exact admitted original, never by a
            // guessed suffix or an unrelated equal-text player turn.
            if (input != null)
            {
                var latestIds = new HashSet<string>(StringComparer.Ordinal);
                string player = ReadFirstString(context, "playerHeroStringId", "mainHeroStringId", "playerId");
                string projected = ProjectCurrentPlayerPromptText(input.Original, observer, context);
                foreach (var row in candidates.Where(r => ReadString(r, "role", "") == "player" && ReadString(r, "speaker_id", "") == player
                    && input.SourceId.Length > 0 && ReadString(TryParseJsonObject(ReadString(r, "payload_json", "{}")), "inputSourceId", "") == input.SourceId))
                    if (ObserverConversationSourceText(row, observer) == projected)
                    {
                        string id = ReadString(row, "turn_id", "");
                        supplied[id] = MemoryTranscriptSourceHash(row);
                        latestIds.Add(id);
                    }
                lines.RemoveAll(line => latestIds.Contains(ReadString(line, "sourceTurnId", "")));
            }
            // Compute coverage from the final tail, after current-input removal.
            foreach (var line in lines.TakeLast(RequiredConversationTailCount))
            {
                var source = MatchPromptSource(line, candidates);
                if (source != null) supplied[ReadString(source, "turn_id", "")] = MemoryTranscriptSourceHash(source);
            }
            bound = lines.Count(line => MatchPromptSource(line, candidates) != null);
            context["promptSourceBinding"] = TestDict("schema", "reign-prompt-source-binding-v1", "boundTranscriptTurns", bound,
                "unboundTranscriptTurns", Math.Max(0, lines.Count - bound), "sourceCount", supplied.Count, "semanticValidation", "not_claimed");
            foreach (var row in candidates.Where(row => supplied.ContainsKey(ReadString(row, "turn_id", ""))))
                statements.Add(TestDict("turnId", ReadString(row, "turn_id", ""), "speakerId", ReadString(row, "speaker_id", ""),
                    "eventId", ReadString(row, "event_id", ""), "originalSourceHash", PromptHash(ConversationTurnTextForRecall(row)),
                    "timestamp", ReadLong(row, "ts", 0), "acceptedSequence", ReadLong(row, "accepted_sequence", 0),
                    "text", Program.SanitizeUnknownIdentityEvidenceText(CommitmentVisibleSourceText(row, access), access)));
            }
            catch (Exception error)
            {
                // Binding is an optimization, not permission to prevent dialogue.
                supplied.Clear(); statements.Clear();
                context["promptSourceBinding"] = TestDict("schema", "reign-prompt-source-binding-v1",
                    "sourceCount", 0, "status", "unavailable_originals_retained", "errorType", error.GetType().Name);
            }
            finally { SetMemoryTranscriptContext(context, lines); }
        }

        private static string MemoryProjectedTranscriptHash(Dictionary<string, object> row, Program.KnowledgeAccessContext knowledge)
        {
            string rendered = RenderExactHistoryTurn(row, knowledge);
            int boundary = rendered.IndexOf(": ", StringComparison.Ordinal);
            if (boundary < 0) return "";
            return PromptHash(ReadString(row, "role", "") + "\n" + ReadString(row, "speaker_id", "")
                + "\n" + rendered.Substring(boundary + 2));
        }

        private static bool MemoryTurnAlreadySupplied(Dictionary<string, object> turn, Dictionary<string, string> supplied) =>
            supplied != null && supplied.TryGetValue(ReadString(turn, "id", ""), out string hash)
            && (hash == ReadString(turn, "hash", "") || hash == ReadString(turn, "viewHash", ""));

        private static MemoryEvidence CopyMemoryEvidence(MemoryEvidence source, string text = null,
            List<Dictionary<string, object>> turns = null) => new MemoryEvidence {
                SourceId = source.SourceId, SourceHash = source.SourceHash, Kind = source.Kind, Text = text ?? source.Text, RankingText = source.RankingText,
                Owner = source.Owner, Session = source.Session, Revision = source.Revision, Day = source.Day,
                Score = source.Score, Required = source.Required, RelationshipFoundation = source.RelationshipFoundation,
                SourceTurns = turns ?? source.SourceTurns, SelectionRank = source.SelectionRank, SelectionRelevance = source.SelectionRelevance,
                PackingText = source.PackingText, PackingTurns = source.PackingTurns, PackingAudit = source.PackingAudit };

        private static MemoryEvidence RemoveSuppliedMemoryTurns(MemoryEvidence original, Dictionary<string, string> supplied)
        {
            if (original.Kind != "reported_exchange" && original.Kind != "active_conversation" || original.SourceTurns.Count == 0) return original;
            var remaining = original.SourceTurns.Where(t => !MemoryTurnAlreadySupplied(t, supplied)).ToList();
            if (remaining.Count == 0) return null;
            if (remaining.Count == original.SourceTurns.Count) return original;
            var evidence = CopyMemoryEvidence(original, string.Join("\n", remaining.Select(t => ReadString(t, "text", ""))), remaining);
            // Ranking still sees the complete linked exchange even when an
            // identical question/answer is already supplied by the transcript.
            evidence.RankingText = original.RankingText.Length > 0 ? original.RankingText : original.Text;
            evidence.SourceId = string.Join(",", remaining.Select(t => ReadString(t, "id", "")));
            evidence.SourceHash = PromptHash(CanonicalJson(remaining));
            // A compact view binds to the complete candidate. Recompute after deduplication.
            evidence.PackingText = ""; evidence.PackingTurns = null; evidence.PackingAudit = null;
            return evidence;
        }

        private static List<MemoryEvidence> DistinctMemoryBundles(IEnumerable<MemoryEvidence> candidates,
            Dictionary<string, string> supplied) => (candidates ?? Enumerable.Empty<MemoryEvidence>())
            .Where(e => e != null && e.Text.Length > 0).Select(e => RemoveSuppliedMemoryTurns(e, supplied))
            .Where(e => e != null)
            .GroupBy(e => e.Kind + "|" + e.Owner + "|" + e.Session + "|" + e.Revision + "|"
                + (e.SourceTurns.Count == 0 ? e.SourceId + "|" + e.SourceHash
                    : string.Join(";", e.SourceTurns.Select(t => ReadString(t, "id", "") + ":" + ReadString(t, "hash", "")
                        + ":" + PromptHash(ReadString(t, "text", "")))))
                + "|ranking_context=" + MemorySelectionTextHash(MemorySelectionRankingText(e)), StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => e.Required).ThenByDescending(e => e.Score).First()).ToList();

        // Compression is limited to optional, older narration. Every spoken word,
        // source label, question, answer, correction and linked turn stays intact.
        // An excerpt is explicitly incomplete; it cannot become a stored memory.
        private static bool MemoryPackingRequiresOriginalNarration(MemoryQueryPlan plan) =>
            Regex.IsMatch(plan.Query ?? "", @"\b(?:exact(?:ly)?|verbatim|quot(?:e|ed|ing)|word[ -]for[ -]word|unabridged|full (?:original |conversation |scene )?(?:text|transcript|description)|(?:every|all) (?:word|gesture|movement|detail)s?)\b", RegexOptions.IgnoreCase);

        private static List<Dictionary<string, object>> MemoryPackingSegments(MemoryEvidence evidence, MemoryQueryPlan plan)
        {
            var result = new List<Dictionary<string, object>>();
            if (evidence.Required || evidence.Kind != "reported_exchange" || evidence.SourceTurns.Count == 0
                || evidence.Text.Length > 24000 || MemoryPackingRequiresOriginalNarration(plan)) return result;
            const string critical = @"[\d?\""“”:]|\b(?:no|not|never|nothing|neither|without|only|unless|until|if|before|after|when|while|because|although|but|except|instead|would|could|might|must|may|will|shall|should|today|tomorrow|yesterday|agreed?|accept\w*|refus\w*|promis\w*|correct\w*|answer\w*|ask\w*|den(?:y|ied)|marri\w*|died|dead|widow\w*|follow\w*|join\w*|leav\w*|left|depart\w*|arriv\w*|return\w*|plan\w*|give\w*|gave|giv\w*|hand(?:s|ed|ing)?|pass(?:es|ed|ing)?|offer\w*|take\w*|took|tak\w*|pay\w*|paid|buy\w*|bought|sell\w*|sold|send\w*|sent|receiv\w*|drink\w*|drank|sip\w*|pull\w*|swallow\w*|kiss\w*|touch\w*|embrac\w*|writ\w*|wrote|read\w*|whisper\w*|secret\w*|private\w*|remember\w*|recall\w*|said|says?|told|tell\w*)\b|n['’]t\b";
            int cursor = 0;
            void Add(int start, int length, bool protect)
            {
                if (length <= 0) return;
                result.Add(TestDict("index", result.Count, "start", start,
                    "text", evidence.Text.Substring(start, length), "protected", protect));
            }
            foreach (Match match in Regex.Matches(evidence.Text, @"\*[^*]+\*", RegexOptions.Singleline))
            {
                Add(cursor, match.Index - cursor, true);
                string narration = match.Value;
                bool protect = narration.Length < 96 || Regex.IsMatch(narration, critical, RegexOptions.IgnoreCase)
                    || plan.Terms.Any(term => Regex.IsMatch(narration, @"(?<![\p{L}\p{Nd}])" + Regex.Escape(term) + @"(?![\p{L}\p{Nd}])", RegexOptions.IgnoreCase));
                Add(match.Index, match.Length, protect);
                cursor = match.Index + match.Length;
            }
            Add(cursor, evidence.Text.Length - cursor, true);
            return result.Count <= 256 && result.Any(s => !ReadBool(s, "protected", true)) ? result : new();
        }

        private static bool AcceptMemoryPacking(MemoryEvidence evidence, List<Dictionary<string, object>> segments,
            Dictionary<string, object> result)
        {
            if (ReadString(result, "textHash", "") != MemorySelectionTextHash(evidence.Text)) return false;
            if (!result.TryGetValue("selectedSegments", out object raw) || raw is string
                || raw is not System.Collections.IEnumerable values) return false;
            var selected = new HashSet<int>();
            foreach (object value in values)
            {
                if (!int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out int index)
                    || index < 0 || index >= segments.Count || !selected.Add(index)) return false;
            }
            if (selected.Count == 0 || segments.Where(s => ReadBool(s, "protected", true)).Any(s => !selected.Contains(ReadInt(s, "index", -1)))) return false;
            if (selected.Count == segments.Count) return true;
            const string omitted = "[background narration omitted; original retained]";
            string Render(int start, int end)
            {
                var text = new StringBuilder();
                foreach (var segment in segments)
                {
                    int lo = ReadInt(segment, "start", 0);
                    string source = ReadString(segment, "text", "");
                    int hi = lo + source.Length;
                    if (hi <= start || lo >= end) continue;
                    if (selected.Contains(ReadInt(segment, "index", -1)))
                        text.Append(evidence.Text.Substring(Math.Max(lo, start), Math.Min(hi, end) - Math.Max(lo, start)));
                    else text.Append(omitted);
                }
                return text.ToString();
            }
            string packed = Render(0, evidence.Text.Length);
            if (packed.Length >= evidence.Text.Length) return true;
            var turns = new List<Dictionary<string, object>>();
            int cursor = 0;
            foreach (var turn in evidence.SourceTurns)
            {
                string original = ReadString(turn, "text", "");
                int start = evidence.Text.IndexOf(original, cursor, StringComparison.Ordinal);
                if (start < 0 || original.Length == 0) return false;
                var copy = new Dictionary<string, object>(turn) { ["text"] = Render(start, start + original.Length),
                    ["originalViewHash"] = MemorySelectionTextHash(original), ["viewCoverage"] = "spoken_and_protected_spans_complete_narration_excerpt" };
                turns.Add(copy); cursor = start + original.Length;
            }
            evidence.PackingText = packed;
            evidence.PackingTurns = turns;
            evidence.PackingAudit = TestDict("schema", "reign-memory-packing-view-v1", "originalTextHash", MemorySelectionTextHash(evidence.Text),
                "packedTextHash", MemorySelectionTextHash(packed), "originalCharacters", evidence.Text.Length, "packedCharacters", packed.Length,
                "selectedSegments", selected.OrderBy(i => i).ToList(), "protectedSpansPreserved", true,
                "coverage", "narration_excerpt_not_complete_source", "storedSourceChanged", false,
                "spans", segments.Select(s => TestDict("start", ReadInt(s, "start", 0), "length", ReadString(s, "text", "").Length,
                    "retained", selected.Contains(ReadInt(s, "index", -1)), "protected", ReadBool(s, "protected", true))).ToList());
            return true;
        }
    }
}
