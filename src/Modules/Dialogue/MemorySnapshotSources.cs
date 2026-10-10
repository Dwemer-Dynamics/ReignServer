using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
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
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_restored_source_context (
state_id TEXT PRIMARY KEY, restore_generation TEXT NOT NULL, timeline_id TEXT NOT NULL,
save_point_id TEXT NOT NULL, world_day DOUBLE PRECISION NOT NULL, head_sequence BIGINT NOT NULL, head_event_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS memory_restored_sources (
source_table TEXT NOT NULL, source_id TEXT NOT NULL, restore_generation TEXT NOT NULL, content_hash TEXT NOT NULL,
PRIMARY KEY(source_table,source_id,restore_generation));
CREATE INDEX IF NOT EXISTS idx_memory_restored_sources_generation ON memory_restored_sources(restore_generation);");
            EnsureDatabaseColumn(connection, "memory_restored_source_context", "legacy_recovery_version", "TEXT NOT NULL DEFAULT ''");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_continuity_branches (
timeline_id TEXT NOT NULL,owner_id TEXT NOT NULL,source_record_id TEXT NOT NULL,record_id TEXT NOT NULL,
PRIMARY KEY(timeline_id,owner_id,source_record_id));
CREATE INDEX IF NOT EXISTS idx_memory_continuity_branch_record ON memory_continuity_branches(record_id);");
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

        private static string MemorySourceTimelineSql(string table, string alias = null, string timeline = "$timeline", KnowledgeAccessContext access = null)
        {
            alias ??= table;
            return "((" + timeline + "='' OR " + SnapshotSourceTimelineSql(alias) + " IN (''," + timeline + ") OR "
                + SnapshotSourceMembershipSql(table, alias, timeline) + ")"
                + (table == "conversation_continuity" ? " AND NOT " + VisibleContinuityReplacementSql(alias, timeline, access) : "") + ")";
        }

        private static string VisibleContinuityReplacementSql(string alias, string timeline, KnowledgeAccessContext access)
        {
            access ??= new KnowledgeAccessContext();
            return "EXISTS (SELECT 1 FROM memory_continuity_branches cb JOIN conversation_continuity replacement ON replacement.record_id=cb.record_id"
                + " WHERE cb.timeline_id=" + timeline + " AND cb.owner_id=" + alias + ".owner_id AND cb.source_record_id=" + alias + ".record_id"
                + " AND replacement.updated_ts<=" + access.AsOfTimestamp.ToString(CultureInfo.InvariantCulture)
                + " AND COALESCE((replacement.payload_json::jsonb->>'worldDay')::double precision,0)<=" + access.WorldDay.ToString("R", CultureInfo.InvariantCulture)
                + " AND (replacement.source_turn_id='' OR EXISTS (SELECT 1 FROM conversation_turns rt WHERE rt.turn_id=replacement.source_turn_id"
                + " AND rt.accepted_sequence<=" + access.AsOfAcceptedSequence.ToString(CultureInfo.InvariantCulture) + ")))";
        }

        private static bool ContinuityReplacedAsOf(ReignDbConnection connection, Dictionary<string, object> row, KnowledgeAccessContext access) =>
            QuerySql(connection, "SELECT original.record_id FROM conversation_continuity original WHERE original.record_id=$id AND "
                + VisibleContinuityReplacementSql("original", "$timeline", access) + ";",
                TestDict("id", ReadString(row, "record_id", ""), "timeline", access.TimelineId)).Count > 0;

        // Called only by exact Save Sync restore (before advancing its generation),
        // or by an identity-verified no-op load using the actual snapshot schema.
        // A no-op compares each entire source row; a clean-state marker alone is
        // insufficient. Old, unproven foreign branches remain excluded.
        private static void CaptureRestoredMemorySources(ReignDbConnection connection, Dictionary<string, object> point,
            string newGeneration, string snapshotSchema = "")
        {
            EnsureMemorySnapshotSourceSchema(connection);
            if (point == null || ReadString(point, "savePointId", "").Length == 0 || ReadString(point, "timelineId", "").Length == 0)
            {
                ExecuteSql(connection, "DELETE FROM memory_restored_sources; DELETE FROM memory_restored_source_context;");
                return;
            }
            if (snapshotSchema.Length > 0 && (!snapshotSchema.StartsWith("reign_save_", StringComparison.Ordinal)
                || snapshotSchema.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_'))))
                throw new InvalidOperationException("Invalid memory snapshot schema.");
            string prefix = snapshotSchema.Length == 0 ? "" : snapshotSchema + ".";
            bool priorProof = snapshotSchema.Length == 0 || ReadString(QuerySql(connection,
                "SELECT to_regclass($name)::text AS name;", TestDict("name", prefix + "memory_restored_source_context")).Single(), "name", "").Length > 0;
            var parameters = TestDict("generation", newGeneration, "timeline", ReadString(point, "timelineId", ""),
                "point", ReadString(point, "savePointId", ""), "day", ReadDouble(point, "campaignTimeDays", 0d),
                "sequence", ReadLong(point, "worldHistorySequence", 0), "head", ReadString(point, "worldHistoryHeadEventId", ""));
            foreach (var entry in SnapshotMemoryKeys)
            {
                if (!TableExists(connection, entry.Key)) continue;
                if (snapshotSchema.Length > 0 && ReadString(QuerySql(connection, "SELECT to_regclass($name)::text AS name;",
                    TestDict("name", prefix + entry.Key)).Single(), "name", "").Length == 0) continue;
                string inherited = priorProof ? SnapshotSourceMembershipSql(entry.Key, "saved", "$timeline", prefix) : "FALSE";
                string day = "COALESCE((to_jsonb(saved)->>'world_day')::double precision,(to_jsonb(saved)->>'learned_day')::double precision,"
                    + "((to_jsonb(saved)->>'payload_json')::jsonb->>'worldDay')::double precision,0)";
                string join = snapshotSchema.Length == 0 ? "" : " JOIN " + entry.Key + " current_source ON current_source." + entry.Value
                    + "=saved." + entry.Value + " AND " + SnapshotSourceHashSql("current_source") + "=" + SnapshotSourceHashSql("saved");
                ExecuteSql(connection, "INSERT INTO memory_restored_sources(source_table,source_id,restore_generation,content_hash) SELECT '"
                    + entry.Key + "',saved." + entry.Value + ",$generation," + SnapshotSourceHashSql("saved") + " FROM " + prefix + entry.Key + " saved" + join
                    + " WHERE " + day + "<=$day AND (" + SnapshotSourceTimelineSql("saved") + " IN ('',$timeline) OR " + inherited + ")"
                    + " ON CONFLICT(source_table,source_id,restore_generation) DO UPDATE SET content_hash=excluded.content_hash;", parameters);
            }
            ExecuteSql(connection, @"INSERT INTO memory_restored_source_context
(state_id,restore_generation,timeline_id,save_point_id,world_day,head_sequence,head_event_id)
VALUES('current',$generation,$timeline,$point,$day,$sequence,$head)
ON CONFLICT(state_id) DO UPDATE SET restore_generation=excluded.restore_generation,timeline_id=excluded.timeline_id,
save_point_id=excluded.save_point_id,world_day=excluded.world_day,head_sequence=excluded.head_sequence,head_event_id=excluded.head_event_id;
DELETE FROM memory_restored_sources WHERE restore_generation<>$generation;", parameters);
        }

        private static void BindRecoveredMemorySources(ReignDbConnection connection, string requested, string recovered, long sequence, string head)
        {
            ExecuteSql(connection, @"UPDATE memory_restored_source_context SET timeline_id=$recovered
WHERE state_id='current' AND restore_generation=(SELECT restore_generation FROM memory_precision_state WHERE state_id='current')
AND timeline_id=$requested AND head_sequence=$sequence AND head_event_id=$head;",
                TestDict("requested", requested, "recovered", recovered, "sequence", sequence, "head", head));
            // Carry only mappings whose replacement record is itself verified in
            // this restored generation. An abandoned sibling cannot supply one.
            ExecuteSql(connection, @"INSERT INTO memory_continuity_branches(timeline_id,owner_id,source_record_id,record_id)
SELECT $recovered,b.owner_id,b.source_record_id,b.record_id FROM memory_continuity_branches b
JOIN conversation_continuity r ON r.record_id=b.record_id AND r.owner_id=b.owner_id
WHERE b.timeline_id=$requested AND " + SnapshotSourceMembershipSql("conversation_continuity", "r", "$recovered")
                + " ON CONFLICT(timeline_id,owner_id,source_record_id) DO NOTHING;", TestDict("requested", requested, "recovered", recovered));
        }

        private static void InitializeVerifiedSnapshotMemorySources(string campaign, Dictionary<string, object> point, string snapshotPoint)
        {
            using var connection = OpenCampaignConnection(campaign);
            using var transaction = connection.BeginTransaction();
            AcquirePostgreSqlTransactionMutationLock(connection);
            string generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            ExecuteSql(connection, "DELETE FROM memory_restored_sources; DELETE FROM memory_restored_source_context;");
            CaptureRestoredMemorySources(connection, point, generation, ReignPostgreSqlStorage.SnapshotSchemaName(campaign, snapshotPoint));
            RecoverLegacySnapshotMemorySources(connection, campaign, point, generation);
            ExecuteSql(connection, "DELETE FROM memory_projections; UPDATE memory_precision_state SET projection_generation=projection_generation+1,source_revision=source_revision+1 WHERE state_id='current';");
            QueueAllEmbeddingJobs(connection, true);
            transaction.Commit();
        }

        private static Dictionary<string, object> RestoredMemorySourceContext(ReignDbConnection connection, string timeline) =>
            QuerySql(connection, @"SELECT c.* FROM memory_restored_source_context c JOIN memory_precision_state s
ON c.restore_generation=s.restore_generation AND s.state_id='current' WHERE c.state_id='current' AND c.timeline_id=$timeline;",
                TestDict("timeline", timeline)).SingleOrDefault();

        private static bool IsVerifiedRestoredMemorySource(ReignDbConnection connection, Dictionary<string, object> row, string timeline)
        {
            if (connection == null || row == null || timeline.Length == 0) return false;
            // Use the most specific ID first (a turn also carries a session/event ID).
            var entry = SnapshotMemoryKeys.FirstOrDefault(e => ReadString(row, e.Value, "").Length > 0);
            if (entry.Key == null) return false;
            var current = QuerySql(connection, "SELECT source.* FROM " + entry.Key + " source WHERE source." + entry.Value
                + "=$id AND " + SnapshotSourceMembershipSql(entry.Key, "source") + ";",
                TestDict("id", ReadString(row, entry.Value, ""), "timeline", timeline)).SingleOrDefault();
            if (current == null || current.Keys.Any(k => !row.ContainsKey(k))) return false;
            var comparable = current.Keys.ToDictionary(k => k, k => row[k]);
            return MemorySourceHash(new[] { current }) == MemorySourceHash(new[] { comparable });
        }

        private static string CurrentContinuityRecordId(ReignDbConnection connection, string timeline, string owner, string sourceId) =>
            ReadString(QuerySql(connection, "SELECT record_id FROM memory_continuity_branches WHERE timeline_id=$timeline AND owner_id=$owner AND source_record_id=$source;",
                TestDict("timeline", timeline, "owner", owner, "source", sourceId)).SingleOrDefault(), "record_id", sourceId);

        private static void LinkBranchedContinuityRecord(ReignDbConnection connection, string timeline, string owner, string sourceId, string id)
        {
            ExecuteSql(connection, @"WITH RECURSIVE ancestors(id) AS (
SELECT $source::text UNION SELECT b.source_record_id FROM memory_continuity_branches b JOIN ancestors a ON b.record_id=a.id WHERE b.owner_id=$owner)
INSERT INTO memory_continuity_branches(timeline_id,owner_id,source_record_id,record_id)
SELECT $timeline,$owner,id,$id FROM ancestors WHERE id<>$id
ON CONFLICT(timeline_id,owner_id,source_record_id) DO UPDATE SET record_id=excluded.record_id;",
                TestDict("timeline", timeline, "owner", owner, "source", sourceId, "id", id));
        }
    }
}
