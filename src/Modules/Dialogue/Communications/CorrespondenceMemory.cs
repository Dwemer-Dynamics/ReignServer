using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static void EnsureCorrespondenceMemorySchema(ReignDbConnection connection)
        {
            DialogueSchema.EnsureColumnLettersTimelineId(connection);
            DialogueSchema.EnsureColumnLettersDispatchSequence(connection);
            DialogueSchema.EnsureColumnLettersDeliveryTimelineId(connection);
            DialogueSchema.CreateMemoryRestoredLetters(connection);
        }

        internal static string LetterSourceTimeline(Dictionary<string, object> letter) => FirstNonEmpty(
            ReadString(letter, "timeline_id", ""), ReadString(TryParseJsonObject(ReadString(letter, "payload_json", "{}")), "timelineId", ""), "main");

        internal static string LetterContentHash(Dictionary<string, object> letter) => PromptHash(CanonicalJson(TestDict(
            "id", ReadString(letter, "letter_id", ""), "sender", ReadString(letter, "sender_id", ""),
            "recipient", ReadString(letter, "recipient_id", ""), "body", ReadString(letter, "body", ""),
            "dispatch", ReadDouble(letter, "dispatch_day", 0d), "timeline", LetterSourceTimeline(letter))));

        // This projection is created only immediately after restoring the exact
        // PostgreSQL snapshot. It grants no access based on a save/branch name.
        // Source records are untouched and every read checks their content hash.
        private static void CaptureRestoredLetterSources(ReignDbConnection connection, Dictionary<string, object> point)
        {
            EnsureCorrespondenceMemorySchema(connection);
            DialogueStore.DeleteMemoryRestoredLetters(connection);
            if (point == null || ReadString(point, "savePointId", "").Length == 0 || ReadString(point, "timelineId", "").Length == 0) return;
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            RelationshipsService.EnsureCorrespondenceDeliverySchema(connection);
            RelationshipsService.RebindRestoredCorrespondenceJobs(connection, ReadString(point, "timelineId", ""), generation);
            DialogueStore.InsertMemoryRestoredLetterContext(connection, TestDict("generation", generation,
                "timeline", ReadString(point, "timelineId", ""), "point", ReadString(point, "savePointId", ""),
                "day", ReadDouble(point, "campaignTimeDays", 0d), "sequence", ReadLong(point, "worldHistorySequence", 0),
                "head", ReadString(point, "worldHistoryHeadEventId", "")));
            foreach (var letter in DialogueStore.SelectLetters(connection, TestDict("day", ReadDouble(point, "campaignTimeDays", 0d))))
                DialogueStore.InsertMemoryRestoredLetters(connection, TestDict("id", ReadString(letter, "letter_id", ""), "generation", generation,
                    "hash", LetterContentHash(letter), "received", ReadString(letter, "status", "") == "read" ? ReadDouble(letter, "read_day", 0d) : 0d));
        }

        internal static void BindRecoveredLetterSources(ReignDbConnection connection, string requested, string recovered,
            long savedSequence, string savedHead)
        {
            if (!WorldSimulationService.TableExists(connection, "memory_restored_letter_context")) return;
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            DialogueStore.UpdateMemoryRestoredLetterContext(connection, TestDict("recovered", recovered, "generation", generation,
                "requested", requested, "sequence", savedSequence, "head", savedHead));
            var verified = RestoredLetterContext(connection, recovered);
            if (verified != null && ReadLong(verified, "head_sequence", -1) == savedSequence
                && ReadString(verified, "head_event_id", "") == savedHead)
                RelationshipsService.RebindRestoredCorrespondenceJobs(connection, recovered, generation);
        }

        internal static Dictionary<string, object> RestoredLetterContext(ReignDbConnection connection, string timeline)
        {
            if (!WorldSimulationService.TableExists(connection, "memory_restored_letter_context")) return null;
            return DialogueStore.SelectMemoryRestoredLetterContext(connection, TestDict("timeline", timeline)).FirstOrDefault();
        }

        internal static bool LetterVisibleToObserver(ReignDbConnection connection, Dictionary<string, object> letter,
            string observer, Program.KnowledgeAccessContext access, out double learnedDay)
        {
            learnedDay = 0d;
            if (letter == null || observer.Length == 0 || access == null) return false;
            bool author = ReadString(letter, "sender_id", "") == observer;
            bool recipient = ReadString(letter, "recipient_id", "") == observer;
            if (!author && !recipient) return false;
            if (access.PlayerIdentityUnknown && (ReadString(letter, "sender_id", "") == access.PlayerId
                || ReadString(letter, "recipient_id", "") == access.PlayerId)) return false;
            double dispatched = ReadDouble(letter, "dispatch_day", 0d);
            learnedDay = author ? dispatched : ReadDouble(letter, "read_day", 0d);
            if (!author && (ReadString(letter, "status", "") != "read" || learnedDay <= 0d)) return false;
            if (dispatched > access.WorldDay || learnedDay > access.WorldDay
                || ReadLong(letter, "created_ts", 0) > access.AsOfTimestamp
                || ReadLong(letter, "dispatch_sequence", 0) > access.AsOfAcceptedSequence) return false;
            if (!author && ReadLong(letter, "updated_ts", 0) > access.AsOfTimestamp) return false;
            string timeline = FirstNonEmpty(access.TimelineId, "main");
            if (LetterSourceTimeline(letter) == timeline) return true;
            var restored = RestoredLetterContext(connection, timeline);
            if (restored == null) return false;
            var member = DialogueStore.SelectMemoryRestoredLetters(connection, TestDict("id", ReadString(letter, "letter_id", ""),
                "generation", ReadString(restored, "restore_generation", ""))).FirstOrDefault();
            if (member == null || ReadString(member, "content_hash", "") != LetterContentHash(letter)) return false;
            if (author) return true;
            // Receipt in the restored snapshot or subsequent receipt on this exact
            // branch is evidence. Another branch's later read flag is not.
            return ReadDouble(member, "received_day", 0d) == learnedDay && learnedDay > 0d
                || ReadString(letter, "delivery_timeline_id", "") == timeline;
        }

        internal static List<MemoryEvidence> LoadCorrespondenceMemory(ReignDbConnection connection,
            string observer, string peer, string query, Dictionary<string, object> context, Program.KnowledgeAccessContext access)
        {
            var result = new List<MemoryEvidence>();
            if (ReadBool(context, "officialMemoryFirewall", false) || access.PlayerIdentityUnknown
                || observer.Length == 0 || !WorldSimulationService.TableExists(connection, "letters")) return result;
            string timeline = FirstNonEmpty(access.TimelineId, "main");
            var args = TestDict("observer", observer, "peer", peer ?? "", "day", access.WorldDay,
                "ts", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence, "timeline", timeline);
            var terms = Program.MemoryQueryTerms((query ?? "") + " " + ReadString(context, "memoryActiveTopic", ""))
                .Where(t => !ExactRecallStopWords.Contains(t, StringComparer.OrdinalIgnoreCase)).Distinct().Take(16).ToList();
            string rank = "0";
            if (terms.Count > 0)
            {
                var ranks = new List<string>();
                for (int i = 0; i < terms.Count; i++)
                {
                    args["term" + i] = "%" + terms[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_").ToLowerInvariant() + "%";
                    ranks.Add("CASE WHEN lower(l.body) LIKE $term" + i + " THEN 1 ELSE 0 END");
                }
                rank = "(" + string.Join("+", ranks) + ")";
            }
            // Eligibility precedes relevance/recency limits. Restore membership
            // is bounded by the current generation and exact restored branch.
            string eligible = @" FROM letters l WHERE (sender_id=$observer OR (recipient_id=$observer AND status='read' AND read_day>0 AND read_day<=$day AND updated_ts<=$ts))
AND ($peer='' OR sender_id=$peer OR recipient_id=$peer) AND dispatch_day<=$day AND created_ts<=$ts AND dispatch_sequence<=$sequence
AND (COALESCE(NULLIF(l.timeline_id,''),NULLIF(l.payload_json::jsonb->>'timelineId',''),'main')=$timeline
OR EXISTS(SELECT 1 FROM memory_restored_letters r JOIN memory_restored_letter_context c ON c.restore_generation=r.restore_generation
JOIN memory_precision_state s ON s.restore_generation=c.restore_generation AND s.state_id='current'
WHERE r.letter_id=l.letter_id AND c.state_id='current' AND c.timeline_id=$timeline))";
            var rows = DialogueStore.SelectStatement4(connection, eligible,args);
            if (terms.Count > 0)
                rows.AddRange(DialogueStore.SelectStatement5(connection, eligible, rank, rank,args));
            var chosen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var letter in rows)
            {
                if (!LetterVisibleToObserver(connection, letter, observer, access, out double learned)) continue;
                string id = ReadString(letter, "letter_id", "");
                if (!chosen.Add(id)) continue;
                result.Add(CorrespondenceMemoryEvidence(letter, observer, learned));
                if (result.Count == 6) break;
            }
            if (context != null) context["correspondenceEvidence"] = TestDict("sourceIds", result.Select(r => r.SourceId).ToList(),
                "coverage", "selected_ledger_sources_verified", "observer", observer);
            return result;
        }

        // Callers must apply LetterVisibleToObserver first. Keep author/read
        // semantics identical for ordinary retrieval and linked-source expansion.
        private static MemoryEvidence CorrespondenceMemoryEvidence(Dictionary<string, object> letter, string observer, double learned)
        {
            string id = ReadString(letter, "letter_id", "");
            bool author = ReadString(letter, "sender_id", "") == observer;
            var body = new StringBuilder("CORRESPONDENCE — exact accepted letter, quoted historical data\n")
                .Append("source=").Append(id).Append("; author=").Append(ReadString(letter, "sender_id", ""))
                .Append(" (").Append(ReadString(letter, "sender_name", "")).Append("); addressed to=")
                .Append(ReadString(letter, "recipient_id", "")).Append(" (").Append(ReadString(letter, "recipient_name", "")).AppendLine(")")
                .Append("thread=").Append(ReadString(letter, "thread_id", "")).Append("; parent=").AppendLine(ReadString(letter, "parent_letter_id", ""))
                .Append("Dispatched day ").Append(ReadDouble(letter, "dispatch_day", 0d).ToString("R", CultureInfo.InvariantCulture)).AppendLine(".")
                .AppendLine(author ? "You authored and sent these words. Recipient receipt/read state is unknown to you unless separately acknowledged."
                    : "You received and read these words on day " + learned.ToString("R", CultureInfo.InvariantCulture) + ".")
                .AppendLine("A written intention is not completed action. It does not establish native transfers, ownership or consent beyond its actual terms.")
                .AppendLine("Exact letter body:").Append(ReadString(letter, "body", ""));
            return new MemoryEvidence { SourceId = id, SourceHash = LetterContentHash(letter), Kind = "letter",
                Owner = observer, Session = ReadString(letter, "thread_id", ""), Day = learned,
                Score = 119, Required = true, Text = body.ToString(),
                SourceTurns = new List<Dictionary<string, object>> { TestDict("id", id,
                    "hash", LetterContentHash(letter), "speakerId", ReadString(letter, "sender_id", ""),
                    "role", "author", "text", ReadString(letter, "body", "")) } };
        }

        private static Dictionary<string, object> ResolveLetterQuestionSource(ReignDbConnection connection, string id,
            string quote, string requestedSpeaker, string observer, string peer, Dictionary<string, object> context)
        {
            if (!id.StartsWith("letter_", StringComparison.Ordinal) || quote.Length < 4) return null;
            var row = DialogueStore.SelectLetters2(connection, TestDict("id", id)).FirstOrDefault();
            if (!LetterVisibleToObserver(connection, row, observer, Program.BuildKnowledgeAccessContext(observer, peer, "", context), out double day)
                || ReadString(row, "sender_id", "") != requestedSpeaker) return null;
            string body = ReadString(row, "body", ""); int start = body.IndexOf(quote, StringComparison.Ordinal);
            if (start < 0) return null;
            return TestDict("turnId", id, "sourceAlias", id, "eventId", id, "speakerId", requestedSpeaker,
                "quote", quote, "start", start, "length", quote.Length, "sourceHash", PromptHash(body),
                "requestForm", ConversationInformationRequestForm(body, quote, start),
                "worldDay", day, "sequence", ReadLong(row, "dispatch_sequence", 0),
                "sessionId", ReadString(row, "thread_id", ""), "beatId", id,
                "audienceIds", new[] { ReadString(row, "sender_id", ""), ReadString(row, "recipient_id", "") });
        }
    }
}
