using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Platform module's SQL statements used only by its self-tests (verification build only); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PlatformSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id='group_test';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE fact_key=(SELECT fact_key FROM temporal_knowledge_assertions WHERE assertion_id=$id) ORDER BY observed_ts;", parameters, transaction);
        }

        internal static void UpdateSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE schema_meta SET value='2' WHERE key='version';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM sqlite_master
WHERE type='table' AND name IN ('group_conversation_state','group_conversation_contributions');", parameters, transaction);
        }

        internal static void UpdateConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',end_ts=302 WHERE session_id='social_event_social_memory_event';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM conversation_turns
WHERE session_id='social_event_social_memory_event' AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM conversation_turns
WHERE session_id='social_event_social_memory_event' AND role='npc';", parameters, transaction);
        }

        internal static void UpdateConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',end_ts=402 WHERE session_id='social_event_social_recall_event';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
(SELECT COUNT(*) FROM events WHERE event_id='recall_projection_event') AS events,
(SELECT COUNT(*) FROM memories WHERE event_id='recall_projection_event') AS memories,
(SELECT COUNT(*) FROM beliefs WHERE event_id='recall_projection_event') AS beliefs,
(SELECT COUNT(*) FROM event_fts WHERE event_id='recall_projection_event') AS event_fts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
(SELECT COUNT(*) FROM events WHERE event_id='low_salience_turn_event') AS low_events,
(SELECT COUNT(*) FROM memories WHERE event_id='low_salience_turn_event') AS low_memories,
(SELECT COUNT(*) FROM event_fts WHERE event_id='low_salience_turn_event') AS low_event_fts,
(SELECT COUNT(*) FROM events WHERE event_id='durable_turn_event') AS durable_events,
(SELECT COUNT(*) FROM memories WHERE event_id='durable_turn_event') AS durable_memories,
(SELECT COUNT(*) FROM memory_fts WHERE memory_id IN (SELECT memory_id FROM memories WHERE event_id='durable_turn_event')) AS durable_memory_fts,
(SELECT COUNT(*) FROM memories WHERE event_id='durable_turn_event' AND payload_json::jsonb->>'observerScope'='author_interpretation'
 AND known_by_json::jsonb='[""npc_a""]'::jsonb) AS author_memories,
(SELECT COUNT(*) FROM memories WHERE event_id='durable_turn_event' AND payload_json::jsonb->>'observerScope'='observable_source_spans'
 AND known_by_json::jsonb @> '[""npc_a"",""player""]'::jsonb) AS observable_memories,
(SELECT COUNT(*) FROM event_fts WHERE event_id='durable_turn_event') AS durable_event_fts,
(SELECT COUNT(*) FROM events WHERE event_id IN ('low_salience_turn_event','durable_turn_event') AND embedding_status='skipped') AS skipped_event_embeddings,
(SELECT COUNT(*) FROM embedding_jobs WHERE source_type='event' AND source_id IN ('low_salience_turn_event','durable_turn_event')) AS raw_event_embedding_jobs,
(SELECT COUNT(*) FROM conversation_turns WHERE status='active') AS active_original_turns,
(SELECT COUNT(*) FROM conversation_turns t WHERE t.status='active' AND EXISTS
 (SELECT 1 FROM embedding_jobs j WHERE j.source_type='conversation_turn' AND j.source_id=t.turn_id)) AS indexed_active_originals,
(SELECT COUNT(*) FROM embedding_jobs WHERE source_type='memory' AND source_id IN (SELECT memory_id FROM memories WHERE event_id='durable_turn_event')) AS durable_memory_embedding_jobs;", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(
event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,payload_json,created_utc)
VALUES($event_id,$campaign,'main',1,1,'social_outcome','old outcome',
'{""subjectId"":""old_subject"",""archetypeId"":""battle_proven""}',$created);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationIntoxication(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM conversation_intoxication WHERE hero_id='npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationDrinkingTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_key FROM conversation_drinking_turns WHERE hero_id='npc';", parameters, transaction);
        }
    }
}
