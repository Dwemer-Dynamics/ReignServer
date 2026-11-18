using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private sealed class RelationshipPromptEvidenceBudget
        {
            private readonly HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            private int remaining = 14000;

            internal List<Dictionary<string, object>> Select(IEnumerable<Dictionary<string, object>> records)
            {
                var selected = new List<Dictionary<string, object>>();
                foreach (var record in records)
                {
                    string text = ReadString(record, "text", "");
                    string key = ReadString(record, "sourceId", "") + "|" + Program.PromptHash(text);
                    if (text.Length == 0 || text.Length > remaining || emitted.Contains(key)) continue;
                    emitted.Add(key); remaining -= text.Length; selected.Add(record);
                }
                return selected;
            }
        }

        private static List<Dictionary<string, object>> RelationshipHistorySourceEvidence(ReignDbConnection connection,
            string observer, string peer, Dictionary<string, object> context, bool sharedOnly)
        {
            double day = ReadDouble(context, "worldDay", double.NaN);
            if (string.IsNullOrWhiteSpace(observer) || string.IsNullOrWhiteSpace(peer) || !double.IsFinite(day) || day < 0)
                return new List<Dictionary<string, object>>();
            var access = Program.BuildKnowledgeAccessContext(observer, peer, "", context);
            var otherAccess = Program.BuildKnowledgeAccessContext(peer, observer, "", context);
            string query = ReadString(context, "playerText", "") + " " + peer
                + " relationship marriage spouse partner affection disagreement promise letter";
            var plan = Program.PlanMemoryQuery(observer, peer, query, context);
            // NPC-to-NPC speech is an eligible original statement. Requiring a
            // player turn here silently discards the very history this writer owns.
            // Ordinary player recall keeps its separate source-bearing guard.
            var ids = Program.FindPrecisionExchanges(connection, plan, access, 8, maximumCandidatePages: 1, requirePlayerSource: false)
                .SelectMany(e => e.SourceTurns).Select(t => ReadString(t, "id", "")).Distinct(StringComparer.Ordinal).ToList();
            var result = new List<Dictionary<string, object>>();
            int remaining = 14000;
            foreach (string id in ids)
            {
                var row = Program.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).SingleOrDefault();
                if (row == null || !Program.PrecisionTurnVisible(row, observer, access, connection)) continue;
                string text = Program.RenderExactHistoryTurn(row, access);
                if (sharedOnly)
                {
                    if (!Program.PrecisionTurnVisible(row, peer, otherAccess, connection)) continue;
                    // Different whisper/private-narration projections cannot be
                    // promoted into mutually shared background by a joint writer.
                    if (text != Program.RenderExactHistoryTurn(row, otherAccess)) continue;
                }
                if (text.Length == 0 || text.Length > remaining) continue;
                remaining -= text.Length;
                result.Add(TestDict("sourceId", id, "kind", "attributed_turn", "speakerId", ReadString(row, "speaker_id", ""),
                    "speakerRole", ReadString(row, "role", ""), "text", text, "sha256", Program.PromptHash(text),
                    "sourceHash", Program.MemoryTranscriptSourceHash(row), "worldDay", ReadDouble(row, "world_day", 0d),
                    "audienceScope", sharedOnly ? "both_observers" : "observer_only", "observerId", observer));
            }
            foreach (var evidence in Program.LoadCorrespondenceMemory(connection, observer, peer, query, context, access))
            {
                var letter = Program.QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", evidence.SourceId)).SingleOrDefault();
                if (letter == null) continue;
                if (sharedOnly && !Program.LetterVisibleToObserver(connection, letter, peer, otherAccess, out _)) continue;
                string text = sharedOnly ? "Letter from " + ReadString(letter, "sender_id", "") + " to " + ReadString(letter, "recipient_id", "")
                    + ": " + ReadString(letter, "body", "") + "\nThis is correspondence content, not evidence that either party knows it was read or that a proposed activity occurred."
                    : evidence.Text;
                if (text.Length > remaining) continue;
                remaining -= text.Length;
                result.Add(TestDict("sourceId", evidence.SourceId, "kind", "letter", "speakerId", ReadString(letter, "sender_id", ""),
                    "speakerRole", "letter_author", "text", text, "sha256", Program.PromptHash(text), "sourceHash", Program.LetterContentHash(letter),
                    "worldDay", evidence.Day, "audienceScope", sharedOnly ? "both_observers" : "observer_only", "observerId", observer));
            }
            return result;
        }

        private static string RenderRelationshipHistorySourceEvidence(IEnumerable<Dictionary<string, object>> records)
        {
            var text = new StringBuilder();
            foreach (var record in records)
                text.AppendLine(Program.RenderContinuityRecord(ReadString(record, "sourceId", ""), ReadString(record, "kind", ""),
                    ReadString(record, "observerId", ""), "", ReadDouble(record, "worldDay", 0d), 113, true, ReadString(record, "text", "")));
            return text.ToString();
        }

        private static Dictionary<string, object> SharedRelationshipHistoryPriorForJointWriter(Dictionary<string, object> prior) =>
            prior == null ? TestDict() : TestDict("objectiveSummary", ReadString(prior, "objectiveSummary", ""),
                "chapterOrdinal", ReadInt(prior, "chapterOrdinal", 0), "evidenceType", "generated_soft_relational_canon");

        private static bool SharedRelationshipHistoryContextVisible(Dictionary<string, object> row, Dictionary<string, object> context)
        {
            if (context == null) return true; // administrative/test reads retain their explicit separate scope
            double day = ReadDouble(context, "worldDay", double.NaN);
            if (!double.IsFinite(day) || ReadDouble(row, "created_world_day", double.MaxValue) > day) return false;
            var basis = TryParseJsonObject(ReadString(row, "source_basis_json", "{}"));
            string timeline = ReadString(basis, "timelineId", "");
            // Legacy unknown-branch chapters remain stored; source-backed personal
            // dialogue still remains available through ordinary observer retrieval.
            return timeline.Length > 0 && timeline == Program.ContinuityTimeline(context);
        }

        private static List<Dictionary<string, object>> ReadScopedRelationshipHistoryChapters(ReignDbConnection connection,
            string pair, Dictionary<string, object> context, int limit)
        {
            double day = ReadDouble(context, "worldDay", -1d);
            if (!double.IsFinite(day)) day = -1d;
            return Program.QuerySql(connection, @"SELECT * FROM shared_relationship_history_chapters WHERE pair_key=$pair
AND ($scoped=0 OR (created_world_day<=$day AND source_basis_json::jsonb->>'timelineId'=$timeline))
ORDER BY chapter_ordinal DESC LIMIT $limit;", TestDict("pair", pair, "scoped", context == null ? 0 : 1,
                "day", day, "timeline", Program.ContinuityTimeline(context), "limit", limit));
        }

        private static void ValidateSharedRelationshipHistorySources(ReignDbConnection connection, Dictionary<string, object> basis)
        {
            string expected = ReadString(basis, "restoreGeneration", "");
            if (expected.Length == 0) return; // compatibility for explicit historical/test records
            string current = ReadString(Program.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR SHARE;").Single(), "restore_generation", "");
            if (current != expected) throw new InvalidOperationException("The campaign changed during relationship-history generation.");
            foreach (var source in ReadDictionaryList(basis, "sharedSources"))
            {
                bool letter = ReadString(source, "kind", "") == "letter";
                var row = Program.QuerySql(connection, letter ? "SELECT * FROM letters WHERE letter_id=$id;" : "SELECT * FROM conversation_turns WHERE turn_id=$id;",
                    TestDict("id", ReadString(source, "sourceId", ""))).SingleOrDefault();
                if (row == null || (letter ? Program.LetterContentHash(row) : Program.MemoryTranscriptSourceHash(row)) != ReadString(source, "sourceHash", ""))
                    throw new InvalidOperationException("An original relationship-history source changed before publication.");
            }
        }

    }
}
