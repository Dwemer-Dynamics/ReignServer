using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // This is saved-state membership, not ancestry inference. Native world
        // history and letters have their own visibility/receipt authorities.
        private static readonly Dictionary<string, string> SnapshotMemoryKeys = new(StringComparer.Ordinal) {
            ["conversation_turns"] = "turn_id",
            ["character_performance_events"] = "receipt_id",
            ["memories"] = "memory_id", ["summaries"] = "summary_id",
            ["beliefs"] = "belief_id", ["comprehension"] = "comprehension_id", ["obligations"] = "obligation_id",
            ["conversation_continuity"] = "record_id", ["temporal_knowledge_assertions"] = "assertion_id",
            ["conversation_relationship_receipts"] = "receipt_id", ["relationship_milestones"] = "milestone_id",
            ["court_social_signal_evidence"] = "signal_id", ["conversation_sessions"] = "session_id", ["events"] = "event_id"
        };

        private static void EnsureMemorySnapshotSourceSchema(ReignDbConnection connection)
        {
            DialogueSchema.CreateMemoryRestoredSourceContext(connection);
            DialogueSchema.EnsureColumnMemoryRestoredSourceContextLegacyRecoveryVersion(connection);
            // Terminal report recovery authority: the restored snapshot's own generation and the
            // Save Sync load session that bound this context. '' is always unproven.
            DialogueSchema.EnsureColumnMemoryRestoredSourceContextSourceGeneration(connection);
            DialogueSchema.EnsureColumnMemoryRestoredSourceContextLoadSessionId(connection);
            DialogueSchema.CreateMemoryContinuityBranches(connection);
        }

        private static string SnapshotSourceTimelineSql(string alias) =>
            "COALESCE(NULLIF(to_jsonb(" + alias + ".*)->>'timeline_id',''),NULLIF((to_jsonb(" + alias
            + ".*)->>'payload_json')::jsonb->>'timelineId',''),'')";

        private static string SnapshotSourceHashSql(string alias) =>
            "encode(sha256(convert_to((to_jsonb(" + alias + ".*)-'vector_id'-'embedding_status')::text,'UTF8')),'hex')";

        private static string SnapshotSourceMembershipSql(string table, string alias, string timeline = "$timeline", string schema = "")
        {
            if (!SnapshotMemoryKeys.TryGetValue(table, out var key)) return "FALSE";
            return "EXISTS (SELECT 1 FROM " + schema + "memory_restored_sources ms JOIN " + schema
                + "memory_restored_source_context mc ON mc.restore_generation=ms.restore_generation AND mc.state_id='current'"
                + " JOIN " + schema + "memory_precision_state ps ON ps.state_id='current' AND ps.restore_generation=mc.restore_generation"
                + " WHERE mc.timeline_id=" + timeline + " AND ms.source_table='" + table + "' AND ms.source_id=" + alias + "." + key
                + " AND ms.content_hash=" + SnapshotSourceHashSql(alias) + ")";
        }

        internal static string MemorySourceTimelineSql(string table, string alias = null, string timeline = "$timeline", Program.KnowledgeAccessContext access = null)
        {
            alias ??= table;
            return "((" + timeline + "='' OR " + SnapshotSourceTimelineSql(alias) + " IN (''," + timeline + ") OR "
                + SnapshotSourceMembershipSql(table, alias, timeline) + ")"
                + (table == "conversation_continuity" ? " AND NOT " + VisibleContinuityReplacementSql(alias, timeline, access) : "") + ")";
        }

        private static string VisibleContinuityReplacementSql(string alias, string timeline, Program.KnowledgeAccessContext access)
        {
            access ??= new Program.KnowledgeAccessContext();
            return "EXISTS (SELECT 1 FROM memory_continuity_branches cb JOIN conversation_continuity replacement ON replacement.record_id=cb.record_id"
                + " WHERE cb.timeline_id=" + timeline + " AND cb.owner_id=" + alias + ".owner_id AND cb.source_record_id=" + alias + ".record_id"
                + " AND replacement.updated_ts<=" + access.AsOfTimestamp.ToString(CultureInfo.InvariantCulture)
                + " AND COALESCE((replacement.payload_json::jsonb->>'worldDay')::double precision,0)<=" + access.WorldDay.ToString("R", CultureInfo.InvariantCulture)
                + " AND (replacement.source_turn_id='' OR EXISTS (SELECT 1 FROM conversation_turns rt WHERE rt.turn_id=replacement.source_turn_id"
                + " AND rt.accepted_sequence<=" + access.AsOfAcceptedSequence.ToString(CultureInfo.InvariantCulture) + ")))";
        }

        private static bool ContinuityReplacedAsOf(ReignDbConnection connection, Dictionary<string, object> row, Program.KnowledgeAccessContext access) =>
            DialogueStore.SelectConversationContinuity11(connection, VisibleContinuityReplacementSql("original", "$timeline", access),                TestDict("id", ReadString(row, "record_id", ""), "timeline", access.TimelineId)).Count > 0;

        // Called only by exact Save Sync restore (before advancing its generation),
        // or by an identity-verified no-op load using the actual snapshot schema.
        // A no-op compares each entire source row; a clean-state marker alone is
        // insufficient. Old, unproven foreign branches remain excluded.
        // loadSessionId binds the context to the Save Sync load that wrote it; every other
        // caller passes "" and leaves the context unproven for terminal report recovery.
        private static void CaptureRestoredMemorySources(ReignDbConnection connection, Dictionary<string, object> point,
            string newGeneration, string snapshotSchema = "", string loadSessionId = "")
        {
            EnsureMemorySnapshotSourceSchema(connection);
            if (point == null || ReadString(point, "savePointId", "").Length == 0 || ReadString(point, "timelineId", "").Length == 0)
            {
                DialogueStore.DeleteMemoryRestoredSources(connection);
                return;
            }
            if (snapshotSchema.Length > 0 && (!snapshotSchema.StartsWith("reign_save_", StringComparison.Ordinal)
                || snapshotSchema.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))))
                throw new InvalidOperationException("Invalid memory snapshot schema.");
            string prefix = snapshotSchema.Length == 0 ? "" : snapshotSchema + ".";
            bool priorProof = snapshotSchema.Length == 0 || ReadString(DialogueStore.SelectStatement9(connection, TestDict("name", prefix + "memory_restored_source_context")).Single(), "name", "").Length > 0;
            // Read before the caller overwrites restore_generation: for an exact restore the active
            // schema is the snapshot itself; for a no-op it is the named snapshot schema.
            string sourceGeneration = SnapshotRestoreGeneration(connection, snapshotSchema);
            var parameters = TestDict("generation", newGeneration, "timeline", ReadString(point, "timelineId", ""),
                "point", ReadString(point, "savePointId", ""), "day", ReadDouble(point, "campaignTimeDays", 0d),
                "sequence", ReadLong(point, "worldHistorySequence", 0), "head", ReadString(point, "worldHistoryHeadEventId", ""),
                "source", sourceGeneration, "session", loadSessionId ?? "");
            foreach (var entry in SnapshotMemoryKeys)
            {
                if (!WorldSimulationService.TableExists(connection, entry.Key)) continue;
                if (snapshotSchema.Length > 0 && ReadString(DialogueStore.SelectStatement9(connection, TestDict("name", prefix + entry.Key)).Single(), "name", "").Length == 0) continue;
                string inherited = priorProof ? SnapshotSourceMembershipSql(entry.Key, "saved", "$timeline", prefix) : "FALSE";
                string day = "COALESCE((to_jsonb(saved)->>'world_day')::double precision,(to_jsonb(saved)->>'learned_day')::double precision,"
                    + "((to_jsonb(saved)->>'payload_json')::jsonb->>'worldDay')::double precision,0)";
                string join = snapshotSchema.Length == 0 ? "" : " JOIN " + entry.Key + " current_source ON current_source." + entry.Value
                    + "=saved." + entry.Value + " AND " + SnapshotSourceHashSql("current_source") + "=" + SnapshotSourceHashSql("saved");
                DialogueStore.UpsertMemoryRestoredSources(connection, entry.Key, entry.Value, SnapshotSourceHashSql("saved"), prefix, entry.Key, join, day, SnapshotSourceTimelineSql("saved"), inherited,parameters);
            }
            DialogueStore.UpsertMemoryRestoredSourceContext(connection, parameters);
        }

        // The restore generation recorded in a snapshot schema ("" = the active schema). An
        // older snapshot without the table yields "", which keeps recovery unproven.
        private static string SnapshotRestoreGeneration(ReignDbConnection connection, string snapshotSchema)
        {
            snapshotSchema ??= "";
            if (snapshotSchema.Length > 0 && (!snapshotSchema.StartsWith("reign_save_", StringComparison.Ordinal)
                || snapshotSchema.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))))
                throw new InvalidOperationException("Invalid memory snapshot schema.");
            string table = (snapshotSchema.Length == 0 ? "" : snapshotSchema + ".") + "memory_precision_state";
            if (ReadString(DialogueStore.SelectStatement9(connection, TestDict("name", table)).Single(), "name", "").Length == 0)
                return "";
            return ReadString(DialogueStore.SelectDynamic5(connection, table).FirstOrDefault(),
                "restore_generation", "");
        }

        // Verified no-op load whose context already names this save point in the current
        // generation (otherwise InitializeVerifiedSnapshotMemorySources rewrites it): bind
        // the load session and always record the snapshot's own generation. Snapshots are
        // immutable, so this rewrites the same value normally, and it replaces a value left
        // stale by an older server build ("" when the snapshot predates it keeps recovery
        // unproven). One statement; the caller restores the clean Save Sync marker afterwards.
        internal static void BindVerifiedNoOpLoadSession(string campaign, string savePointId, string snapshotPoint, string loadSessionId)
        {
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            EnsureMemorySnapshotSourceSchema(connection);
            string source = SnapshotRestoreGeneration(connection, ReignPostgreSqlStorage.SnapshotSchemaName(campaign, snapshotPoint));
            DialogueStore.UpdateMemoryRestoredSourceContext2(connection, TestDict("session", loadSessionId ?? "", "source", source, "point", savePointId ?? ""));
        }

        internal static void BindRecoveredMemorySources(ReignDbConnection connection, string requested, string recovered, long sequence, string head)
        {
            DialogueStore.UpdateMemoryRestoredSourceContext3(connection, TestDict("requested", requested, "recovered", recovered, "sequence", sequence, "head", head));
            // Carry only mappings whose replacement record is itself verified in
            // this restored generation. An abandoned sibling cannot supply one.
            DialogueStore.InsertOrIgnoreMemoryContinuityBranches(connection, SnapshotSourceMembershipSql("conversation_continuity", "r", "$recovered"),TestDict("requested", requested, "recovered", recovered));
        }

        internal static void InitializeVerifiedSnapshotMemorySources(string campaign, Dictionary<string, object> point, string snapshotPoint,
            string loadSessionId = "")
        {
            using var connection = ReignDatabase.OpenCampaignConnection(campaign);
            using var transaction = connection.BeginTransaction();
            AcquirePostgreSqlTransactionMutationLock(connection);
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            DialogueStore.DeleteMemoryRestoredSources(connection);
            CaptureRestoredMemorySources(connection, point, generation, ReignPostgreSqlStorage.SnapshotSchemaName(campaign, snapshotPoint), loadSessionId);
            RecoverLegacySnapshotMemorySources(connection, campaign, point, generation);
            DialogueStore.DeleteMemoryProjections2(connection);
            QueueAllEmbeddingJobs(connection, true);
            transaction.Commit();
        }

        internal static Dictionary<string, object> RestoredMemorySourceContext(ReignDbConnection connection, string timeline) =>
            DialogueStore.SelectMemoryRestoredSourceContext3(connection, TestDict("timeline", timeline)).SingleOrDefault();

        private static bool IsVerifiedRestoredMemorySource(ReignDbConnection connection, Dictionary<string, object> row, string timeline)
        {
            if (connection == null || row == null || timeline.Length == 0) return false;
            // Use the most specific ID first (a turn also carries a session/event ID).
            var entry = SnapshotMemoryKeys.FirstOrDefault(e => ReadString(row, e.Value, "").Length > 0);
            if (entry.Key == null) return false;
            var current = DialogueStore.SelectDynamic6(connection, entry.Key, entry.Value, SnapshotSourceMembershipSql(entry.Key, "source"),                TestDict("id", ReadString(row, entry.Value, ""), "timeline", timeline)).SingleOrDefault();
            if (current == null || current.Keys.Any(k => !row.ContainsKey(k))) return false;
            var comparable = current.Keys.ToDictionary(k => k, k => row[k]);
            return MemorySourceHash(new[] { current }) == MemorySourceHash(new[] { comparable });
        }

        private static string CurrentContinuityRecordId(ReignDbConnection connection, string timeline, string owner, string sourceId) =>
            ReadString(DialogueStore.SelectMemoryContinuityBranches(connection, TestDict("timeline", timeline, "owner", owner, "source", sourceId)).SingleOrDefault(), "record_id", sourceId);

        private static void LinkBranchedContinuityRecord(ReignDbConnection connection, string timeline, string owner, string sourceId, string id)
        {
            DialogueStore.SelectAncestors(connection, TestDict("timeline", timeline, "owner", owner, "source", sourceId, "id", id));
        }
    }
}
