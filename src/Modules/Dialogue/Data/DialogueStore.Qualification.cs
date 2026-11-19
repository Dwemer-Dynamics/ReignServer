using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectSharedRelationshipHistoryChapters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_relationship_history_chapters WHERE history_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,
start_ts,start_world_day,participants_json,payload_json) VALUES($session,$campaign,$npc,$player,'in_person','open',1,1,$participants,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pg_try_advisory_lock(hashtextextended($owner,0)) AS acquired;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPgNamespace(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=$schema)
OR EXISTS(SELECT 1 FROM reign_meta.campaign_registry WHERE campaign_id=$campaign OR schema_name=$schema)
OR EXISTS(SELECT 1 FROM reign_meta.save_sync_snapshots WHERE campaign_id=$campaign) AS present;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pg_advisory_unlock(hashtextextended($owner,0));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic11(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE event_id=$event LIMIT 256;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE owner_id=$owner AND timeline_id=$timeline LIMIT 256;", parameters, transaction);
        }

        internal static void InsertConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,$npc,$player,'closed',$start,$end,$participants,$payload);", parameters, transaction);
        }

        internal static void UpdateConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET accepted_sequence=$sequence WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySpecialistSources2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json,public_topics_json,public_entities_json FROM memory_specialist_sources WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void SelectMemoryAcceptedTurnSequence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"SELECT setval('memory_accepted_turn_sequence',
GREATEST((SELECT last_value FROM memory_accepted_turn_sequence),$cutoff),true);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns82(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.turn_id,t.event_id,t.role,t.speaker_id,t.status,t.accepted_sequence,t.ts,t.world_day,t.participants_json,
t.payload_json::jsonb->>'timelineId' AS timeline_id,t.xmin::text AS source_revision,s.restore_generation
FROM conversation_turns t CROSS JOIN memory_precision_state s WHERE t.turn_id=$id AND s.state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns83(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM memories WHERE event_id=$event AND owner_id=$observer
AND source='dialogue_llm' AND status='active' FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateMemories8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET payload_json=$payload WHERE memory_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE memory_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_fts WHERE memory_id=$id ORDER BY text,tags,entities;", parameters, transaction);
        }

        internal static void UpdateConversationSessions9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status='closed',end_ts=$end WHERE session_id=$prior;", parameters, transaction);
        }

        internal static void InsertConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,$npc,$peer,'active',$start,$end,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns84(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session ORDER BY accepted_sequence;", parameters, transaction);
        }

        internal const string UpdateConversationTurnsSql2 = "UPDATE conversation_turns SET participants_json=$audience WHERE turn_id=$id RETURNING text,accepted_sequence,xmin::text AS source_revision;";

        internal const string UpdateConversationTurnsSql3 = "UPDATE conversation_turns SET status=status WHERE turn_id=$id RETURNING text,accepted_sequence,xmin::text AS source_revision;";

        internal static List<Dictionary<string, object>> SelectConversationSessions21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id AND status='closed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns85(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id ORDER BY accepted_sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT summary,status,payload_json FROM summaries WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM summary_fts WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT source_id FROM memory_sources WHERE document_type='summary' AND document_id=$id
AND source_type='turn' ORDER BY ordinal;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT current_setting('transaction_read_only') AS mode;", parameters, transaction);
        }

        internal const string SelectPgNamespaceSql = "SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname=@schema);";

        internal const string InsertSourceProbeSql = "INSERT INTO source_probe VALUES('original_turn',@body);";

        internal const string SelectSourceProbeSql = "SELECT id,body,source_identity_probe() FROM source_probe;";

        internal const string SelectSql2 = "SELECT pg_try_advisory_lock(hashtextextended(@key,0));";

        internal const string SelectSql3 = "SELECT pg_advisory_unlock(hashtextextended(@key,0));";

        internal const string SelectSql4 = "SELECT pg_backend_pid();";

        internal const string SelectPgStatActivitySql = "SELECT NOT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid=@pid);";

        internal const string SelectSql5 = "SELECT 1;";

        internal const string CountForeignProbeSql = "SELECT count(*) FROM foreign_probe;";

        internal const string SelectSentinelSql = "SELECT value FROM sentinel;";

        internal static List<Dictionary<string, object>> SelectConversationTurns86(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuityVersions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM conversation_continuity_versions v
JOIN conversation_continuity c ON c.record_id=v.record_id
WHERE c.owner_id=$observer AND c.timeline_id=$timeline AND c.revision>v.revision
 AND c.updated_ts<=$time AND COALESCE((c.payload_json::jsonb->>'worldDay')::double precision,0)<=$day
 AND v.payload_json::jsonb->>'sourceTurnId'=$turn
 AND (c.source_turn_id='' OR EXISTS (SELECT 1 FROM conversation_turns t WHERE t.turn_id=c.source_turn_id
  AND t.accepted_sequence<=$cutoff AND t.ts<=$time AND t.world_day<=$day)) LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns87(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange
ORDER BY turn_order LIMIT 5;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns88(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE event_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEventFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM event_fts WHERE event_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turn_fts WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summary_fts WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEmbeddingJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM embedding_jobs WHERE source_type='summary' AND source_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE scope=$scope AND summary_type='middle_term' ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) count FROM memories m
JOIN conversation_turns t ON t.event_id=m.event_id
WHERE t.session_id=$session AND m.owner_id=$npc AND m.status<>'consolidated';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_sessions WHERE npc_id=$npc;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE speaker_id=$npc OR participants_json LIKE $like;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE owner_id=$npc;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='scene';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='middle_term';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE owner_id=$npc AND summary_type='arc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT embedding_status FROM summaries WHERE summary_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT summary FROM summaries WHERE summary_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE speaker_id=$id OR participants_json LIKE $like;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE owner_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE summary_type='scene' AND (owner_id=$id OR participants_json LIKE $like);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE summary_type='middle_term' AND (owner_id=$id OR participants_json LIKE $like);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSummaries6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM summaries WHERE summary_type='arc' AND (owner_id=$id OR participants_json LIKE $like);", parameters, transaction);
        }
    }
}
