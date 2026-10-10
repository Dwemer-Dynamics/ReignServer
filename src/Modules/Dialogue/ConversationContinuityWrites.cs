using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Request-local server state. Neither HTTP JSON nor model output can supply
        // this snapshot. Keep the read revision, not the revision at write time:
        // genuine concurrent updates must still fail the compare-and-swap.
        private sealed class ContinuityReadSnapshot
        {
            internal string Timeline;
            internal readonly Dictionary<string, Dictionary<string, object>> Records =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.Ordinal);
            internal readonly Dictionary<string, string> DeliveredPending = new(StringComparer.Ordinal);
        }

        private static readonly ConditionalWeakTable<Dictionary<string, object>,
            Dictionary<string, ContinuityReadSnapshot>> ContinuityReadSnapshots = new();

        private static void CaptureContinuityReadSnapshot(ReignDbConnection connection,
            Dictionary<string, object> payload, string owner)
        {
            if (payload == null) return;
            EnsureConversationContinuitySchema(connection);
            var snapshot = new ContinuityReadSnapshot { Timeline = ContinuityTimeline(payload) };
            string peer = ReadFirstString(payload, "playerHeroStringId", "mainHeroStringId", "playerId");
            var access = BuildKnowledgeAccessContext(owner, peer, "", payload);
            foreach (var row in QuerySql(connection, @"SELECT * FROM conversation_continuity
WHERE owner_id=$owner AND " + MemorySourceTimelineSql("conversation_continuity", access: access) + @" AND status<>'invalidated'
AND (peer_id='' OR peer_id=$peer);", new Dictionary<string, object> {
                ["timeline"] = snapshot.Timeline, ["owner"] = owner, ["peer"] = peer }))
            {
                var record = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                if (!MemoryTimeVisible(row, access, connection)) continue;
                if (ReadDouble(record, "worldDay", 0d) > ReadDouble(payload, "worldDay", 0d)) continue;
                snapshot.Records[ReadString(row, "record_id", "")] = new Dictionary<string, object> {
                    ["revision"] = ReadInt(row, "revision", 0),
                    ["sourceHash"] = PromptHash(ReadString(row, "payload_json", "{}")) };
            }
            var owners = ContinuityReadSnapshots.GetOrCreateValue(payload);
            lock (owners) owners[owner] = snapshot;
        }

        private static ContinuityReadSnapshot GetContinuityReadSnapshot(Dictionary<string, object> payload, string owner)
        {
            if (payload == null || !ContinuityReadSnapshots.TryGetValue(payload, out var owners)) return null;
            lock (owners)
                return owners.TryGetValue(owner, out var snapshot)
                    && snapshot.Timeline == ContinuityTimeline(payload) ? snapshot : null;
        }

        // A quote assembled around a stage direction is not a contiguous quote.
        // Multiple explicitly supplied source spans preserve that distinction;
        // never silently remove narration to make invented wording match.
        private static List<Dictionary<string, object>> AcceptedContinuityEvidenceSpans(
            Dictionary<string, object> write, string reply)
        {
            reply ??= "";
            var supplied = ReadDictionaryList(write, "evidenceSpans");
            if (supplied.Count == 0)
            {
                string quote = ReadString(write, "evidenceQuote", "").Trim();
                int start = quote.Length >= 12 ? reply.IndexOf(quote, StringComparison.Ordinal) : -1;
                return start < 0 || reply.IndexOf(quote, start + 1, StringComparison.Ordinal) >= 0
                    ? new List<Dictionary<string, object>>() : new List<Dictionary<string, object>> {
                    new Dictionary<string, object> { ["quote"] = quote, ["start"] = start, ["length"] = quote.Length } };
            }
            if (supplied.Count > 8) return new List<Dictionary<string, object>>();
            var result = new List<Dictionary<string, object>>();
            int end = 0;
            foreach (var span in supplied)
            {
                string quote = ReadString(span, "quote", "");
                if (quote.Length < 4) return new List<Dictionary<string, object>>();
                int start = ReadInt(span, "start", -1);
                // The model can quote accurately while miscounting characters.
                // Bind only a unique literal occurrence; a duplicate cannot be
                // rescued by trusting either a stale or a conveniently matching offset.
                int actual = reply.IndexOf(quote, StringComparison.Ordinal);
                if (actual < 0 || reply.IndexOf(quote, actual + 1, StringComparison.Ordinal) >= 0)
                    return new List<Dictionary<string, object>>();
                if (start < 0 || start != actual) start = actual;
                if (start < end || start > reply.Length - quote.Length
                    || string.CompareOrdinal(reply, start, quote, 0, quote.Length) != 0)
                    return new List<Dictionary<string, object>>();
                result.Add(new Dictionary<string, object> { ["quote"] = quote, ["start"] = start, ["length"] = quote.Length });
                end = start + quote.Length;
            }
            return result.Sum(s => ReadInt(s, "length", 0)) >= 12 ? result : new List<Dictionary<string, object>>();
        }

        private static Dictionary<string, object> RetainPendingContinuityWrite(ReignDbConnection connection,
            Dictionary<string, object> payload, string owner, string peer, string eventId, string reply,
            string recordId, Dictionary<string, object> write, List<Dictionary<string, object>> spans,
            int expected, int current, long ts)
        {
            var source = BindAcceptedContinuityEvidence(connection, eventId, owner, reply, spans);
            string turn = ReadString(source, "turnId", "");
            // Only accepted canonical speech can enter recovery. Never promote
            // an uncommitted draft or a caller-supplied source ID.
            if (turn.Length == 0 || spans.Count == 0) return TestDict("status", "source_unavailable", "recoverable", false);
            string timeline = ContinuityTimeline(payload);
            string pendingId = "continuity_pending_" + PromptHash(timeline + "|" + owner + "|" + eventId + "|" + recordId).Substring(0, 32);
            var value = TestDict("proposal", CloneDictionary(write), "sourceEvidence", source,
                "expectedRevision", expected, "observedRevision", current,
                "reason", "concurrent_revision_changed", "meaning", "Unapplied proposal, not established memory.");
            ExecuteSql(connection, @"INSERT INTO conversation_continuity_pending
(pending_id,record_id,timeline_id,owner_id,peer_id,source_event_id,source_turn_id,source_hash,world_day,status,payload_json,created_ts)
VALUES($id,$record,$timeline,$owner,$peer,$event,$turn,$hash,$day,'pending',$payload,$ts)
ON CONFLICT(pending_id) DO NOTHING;", TestDict("id", pendingId, "record", recordId, "timeline", timeline,
                "owner", owner, "peer", peer, "event", eventId, "turn", turn, "hash", PromptHash(reply ?? ""),
                "day", ReadDouble(payload, "worldDay", 0d), "payload", Json.Serialize(value), "ts", ts));
            return TestDict("status", "pending_reconciliation", "recoverable", true, "pendingId", pendingId,
                "sourceTurnId", turn, "sourceHash", PromptHash(reply ?? ""));
        }

        private static string BuildPendingContinuityPrompt(ReignDbConnection connection,
            Dictionary<string, object> payload, string owner, string peer, List<string> sourceIds)
        {
            var snapshot = GetContinuityReadSnapshot(payload, owner);
            if (snapshot == null || !TableExists(connection, "conversation_turns")) return "";
            snapshot.DeliveredPending.Clear();
            var text = new StringBuilder();
            var access = BuildKnowledgeAccessContext(owner, peer, "", payload);
            var rows = QuerySql(connection, @"SELECT p.* FROM conversation_continuity_pending p
LEFT JOIN conversation_turns t ON t.turn_id=p.source_turn_id
WHERE p.timeline_id=$timeline AND p.owner_id=$owner AND p.peer_id=$peer AND p.status='pending' AND p.world_day<=$day
AND p.created_ts<=$asof AND COALESCE(t.accepted_sequence,0)<=$sequence
ORDER BY p.created_ts,p.pending_id LIMIT 17;", TestDict("timeline", ContinuityTimeline(payload), "owner", owner,
                "peer", peer, "day", access.WorldDay, "asof", access.AsOfTimestamp, "sequence", access.AsOfAcceptedSequence));
            var gaps = new List<string>();
            foreach (var row in rows.Take(16))
            {
                string id = ReadString(row, "pending_id", ""), recordId = ReadString(row, "record_id", "");
                var turn = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;",
                    TestDict("id", ReadString(row, "source_turn_id", ""))).SingleOrDefault();
                var value = TryParseJsonObject(ReadString(row, "payload_json", "{}"));
                var source = ReadDictionary(value, "sourceEvidence");
                string original = ReadString(turn, "text", "");
                if (turn == null || !PrecisionTurnVisible(turn, owner, access, connection)
                    || ReadString(turn, "speaker_id", "") != owner
                    || ReadString(turn, "event_id", "") != ReadString(row, "source_event_id", "")
                    || PromptHash(original) != ReadString(row, "source_hash", "")
                    || ContinuityTimeline(TryParseJsonObject(ReadString(turn, "payload_json", "{}"))) != snapshot.Timeline
                    || ReadDouble(turn, "world_day", 0d) > ReadDouble(payload, "worldDay", 0d))
                { gaps.Add(id + ": canonical source unavailable or changed"); continue; }
                var spans = ReadDictionaryList(source, "spans");
                if (spans.Count == 0 || spans.Any(span => {
                    int start = ReadInt(span, "start", -1); string quote = ReadString(span, "quote", "");
                    return start < 0 || quote.Length == 0 || start > original.Length - quote.Length
                        || string.CompareOrdinal(original, start, quote, 0, quote.Length) != 0;
                })) { gaps.Add(id + ": source spans unavailable or changed"); continue; }
                // Reread current state alongside the rejected update. Do not rebase
                // a prepared write onto a newer revision or execute its effects.
                var current = QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND timeline_id=$timeline;",
                    TestDict("id", recordId, "owner", owner, "timeline", snapshot.Timeline)).SingleOrDefault();
                var currentValue = TryParseJsonObject(ReadString(current, "payload_json", "{}"));
                if (current == null || !MemoryTimeVisible(current, access, connection))
                { gaps.Add(id + ": current continuity state unavailable"); continue; }
                string body = "MEMORY UPDATE INCOMPLETE: a concurrent change prevented applying this earlier proposal. "
                    + "These are your accepted words, not a successfully resolved record. Reconcile them with the current state; do not claim the player failed to answer merely because the update failed. "
                    + "Use the existing continuity record id for a supported update, or preserve a genuinely different unresolved concern.\n"
                    + "Record " + recordId + "; current revision " + ReadInt(current, "revision", 0)
                    + "; status " + ReadString(current, "status", "") + "; meaning: " + ReadString(currentValue, "meaning", "")
                    + "\nEarlier accepted speaker " + owner + ": " + string.Join("\n", spans.Select(s => ReadString(s, "quote", "")));
                sourceIds.Add(id); sourceIds.Add(ReadString(row, "source_turn_id", ""));
                text.AppendLine(RenderContinuityRecord(id, "pending_update_source", owner, ReadString(turn, "session_id", ""),
                    ReadDouble(row, "world_day", 0d), 130, true, body));
                snapshot.DeliveredPending[id] = recordId;
            }
            if (rows.Count > 16) gaps.Add("Additional pending records exceed the 16-record recovery prompt bound.");
            payload["continuityRecovery"] = TestDict("status", rows.Count == 0 ? "none" : "pending_reconciliation",
                "deliveredPendingIds", snapshot.DeliveredPending.Keys.ToArray(), "coverageGaps", gaps);
            return text.ToString();
        }

        private static void SupersedeReadPendingContinuity(ReignDbConnection connection,
            ContinuityReadSnapshot snapshot, string recordId, string eventId)
        {
            if (snapshot == null) return;
            foreach (var pending in snapshot.DeliveredPending.Where(p => p.Value == recordId))
                ExecuteSql(connection, @"UPDATE conversation_continuity_pending SET status='superseded_after_reread',resolved_event_id=$event
WHERE pending_id=$id AND record_id=$record AND timeline_id=$timeline AND status='pending' AND source_event_id<>$event;",
                    TestDict("event", eventId, "id", pending.Key, "record", recordId, "timeline", snapshot.Timeline));
        }

        private static Dictionary<string, object> BindAcceptedContinuityEvidence(ReignDbConnection connection,
            string eventId, string owner, string acceptedReply, List<Dictionary<string, object>> spans)
        {
            // The accepted event is server-issued. sceneTurnId can identify a
            // preceding client scene; it is never the canonical source of a reply.
            var turn = TableExists(connection, "conversation_turns") ? QuerySql(connection, @"SELECT turn_id
FROM conversation_turns WHERE event_id=$event AND speaker_id=$owner AND text=$text AND status='active'
ORDER BY turn_order DESC LIMIT 1;", new Dictionary<string, object> {
                ["event"] = eventId, ["owner"] = owner, ["text"] = acceptedReply ?? "" }).FirstOrDefault() : null;
            string quoted = string.Join("\n", spans.Select(s => ReadString(s, "quote", "")));
            string type = HasUncertainty(quoted)
                || System.Text.RegularExpressions.Regex.IsMatch(quoted, @"\b(?:if|imagined|suppose|dreamed|dreamt|hypothetically)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                ? "hypothesis" : System.Text.RegularExpressions.Regex.IsMatch(quoted, @"\b(?:will|shall|plan to|intend to|would like to)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    ? "future_intention" : "spoken_statement";
            return new Dictionary<string, object> {
                ["status"] = spans.Count == 1 ? "supported_span" : "supported_spans",
                ["sourceEventId"] = eventId, ["turnId"] = ReadString(turn, "turn_id", ""),
                ["sourceKind"] = turn == null ? "accepted_exchange" : "conversation_turn",
                ["speakerId"] = owner, ["evidenceType"] = type,
                ["sourceHash"] = PromptHash(acceptedReply ?? ""), ["spans"] = spans,
                ["quote"] = spans.Count == 1 ? ReadString(spans[0], "quote", "") : "" };
        }
    }
}
