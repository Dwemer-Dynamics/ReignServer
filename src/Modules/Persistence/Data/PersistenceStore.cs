using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Persistence module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PersistenceStore
    {
        internal static List<Dictionary<string, object>> CountTables(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_name='embedding_documents';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEmbeddingDocuments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT vector_id FROM embedding_documents WHERE vector_id<>'' AND status<>'deleted';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('backup_test','preserved');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='backup_test';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('preexisting_test','ready');", parameters, transaction);
        }

        internal static void UpsertEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO events(event_id,campaign_id,ts,world_day,event_type,summary,created_utc) VALUES($id,$campaign,$ts,$day,'test',$id,$utc);", parameters, transaction);
        }

        internal static void UpsertMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO memories(memory_id,event_id,owner_id,ts,world_day,summary) VALUES($id,$event,'hero_a',$ts,$day,$id);", parameters, transaction);
        }

        internal static void UpsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
first_day,last_day,projected_native_relation,last_decay_period,updated_ts)
VALUES('hero_a|hero_b','hero_a','hero_b',$affinity,$affinity,'neutral','neutral',
$day,$day,$affinity,0,$ts);", parameters, transaction);
        }

        internal static void UpsertRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,last_processed_day,updated_ts)
VALUES('hero_a|hero_b','hero_a','hero_b',$day,$ts);", parameters, transaction);
        }

        internal static void UpsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO conversation_sessions(session_id,campaign_id,npc_id,start_ts) VALUES($id,$campaign,'hero_a',$ts);", parameters, transaction);
        }

        internal static void UpsertConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO conversation_turns(turn_id,session_id,turn_order,role,text,ts) VALUES($id,$session,1,'npc',$id,$ts);", parameters, transaction);
        }

        internal static void UpsertConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO conversation_turns(turn_id,session_id,turn_order,role,text,ts,status) VALUES($id,$session,-1,'player_source',$text,$ts,'source_only');", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_input_receipts(input_id,timeline_id,session_id,player_id,source_turn_id,source_hash,restore_generation,status,created_ts,updated_ts)
VALUES($id,'timeline_test',$session,'player',$source,$hash,'fixture','received',$ts,$ts) ON CONFLICT(input_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpsertRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_occurrences(
occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,source_event_id,world_day,expires_day,status,
exposure_chance,exposure_roll,catalog_revision,participants_json,provenance_summary,snapshot_json,created_ts,updated_ts)
VALUES($id,$campaign,'timeline_test','marital_strife','hero_a|hero_b',$event,$day,999999,'active',
1,0,1,'[""hero_a"",""hero_b""]',$summary,'{}',$ts,$ts);", parameters, transaction);
        }

        internal static void UpsertRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_subject_tags(
occurrence_id,subject_id,tag_id,subject_role,description,rumor_value,reputation_value,status,
co_participants_json,snapshot_json,updated_ts)
VALUES($occurrence,'hero_a','marital_strife','spouse',$description,-5,-10,'active','[""hero_b""]','{}',$ts);", parameters, transaction);
        }

        internal static void UpsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,summary,created_utc) VALUES($id,$campaign,'timeline_test',$ts,$day,'test',$id,$utc);", parameters, transaction);
        }

        internal static void UpsertRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rebellion_movements(movement_id,campaign_id,timeline_id,parent_kingdom_id,leader_clan_id,leader_hero_id,ruler_hero_id,objective,stage,pressure,relationship_pressure,trait_pressure,factual_pressure,viability,readiness,correlation_id,created_day,updated_day)
VALUES($id,$campaign,'timeline_test','kingdom_a','clan_a','hero_a','hero_b','claimant','organizing',1,1,1,1,1,1,$id,$day,$day);", parameters, transaction);
        }

        internal static void UpsertCourtSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO court_sessions(session_id,campaign_id,timeline_id,authority,host_settlement_id,created_ts,updated_ts) VALUES($id,$campaign,'timeline_test','royal','town_a',$ts,$ts);", parameters, transaction);
        }

        internal static void UpsertCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO character_reputations(
campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,archetype_id,subject_role,description,
reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,'timeline_test','hero_a','test_reputation',$occurrence,'test','subject',$description,1,$day,1,'{}','active',$ts);", parameters, transaction);
        }

        internal static void UpsertDirectorOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO director_outbox(outbox_id,kind,status,world_day,subject_ids_json,payload_json,created_ts) VALUES($id,'test','pending',$day,'[]',$payload,$ts);", parameters, transaction);
        }

        internal static void UpdateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_reputations SET reputation_value=99,updated_ts=20 WHERE campaign_id=$campaign AND subject_id='hero_a' AND tag_id='test_reputation';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM events WHERE event_id=$id;", parameters, transaction);
        }

        internal static string SelectMemoryPrecisionStateSql(object part)
        {
            return "SELECT restore_generation FROM " + part
                    + ".memory_precision_state WHERE state_id='current';";
        }

        internal const string SelectMemoryPrecisionStateSql2 = "SELECT restore_generation FROM memory_precision_state WHERE state_id='current';";

        internal const string SelectMemoryRestoredSourceContextSql = "SELECT source_generation FROM memory_restored_source_context WHERE state_id='current';";

        internal const string SelectMemoryRestoredSourceContextSql2 = "SELECT load_session_id FROM memory_restored_source_context WHERE state_id='current';";

        internal const string SelectRelationshipPairChemistrySql = "SELECT affinity_a_to_b FROM relationship_pair_chemistry WHERE pair_key='hero_a|hero_b';";

        internal const string SelectCharacterReputationsSql = "SELECT reputation_value FROM character_reputations WHERE campaign_id=$campaign AND subject_id='hero_a' AND tag_id='test_reputation';";

        internal const string SelectConversationTurnsSql = "SELECT text FROM conversation_turns WHERE turn_id='old_original';";

        internal const string SelectConversationInputReceiptsSql = "SELECT source_hash FROM conversation_input_receipts WHERE input_id='old_input';";

        internal const string CountEventsSql = "SELECT COUNT(*) count FROM events WHERE event_id='future_event';";

        internal const string CountMemoriesSql = "SELECT COUNT(*) count FROM memories WHERE memory_id='future_memory';";

        internal const string CountConversationTurnsSql = "SELECT COUNT(*) count FROM conversation_turns WHERE turn_id='future_turn';";

        internal const string CountConversationTurnsSql2 = "SELECT COUNT(*) count FROM conversation_turns WHERE turn_id='future_original';";

        internal const string CountConversationInputReceiptsSql = "SELECT COUNT(*) count FROM conversation_input_receipts WHERE input_id='future_input';";

        internal const string CountRumorOccurrencesSql = "SELECT COUNT(*) count FROM rumor_occurrences WHERE occurrence_id='future_rumor_occurrence';";

        internal const string CountRumorSubjectTagsSql = "SELECT COUNT(*) count FROM rumor_subject_tags WHERE occurrence_id='future_rumor_occurrence' AND subject_id='hero_a';";

        internal const string CountCharacterReputationsSql = "SELECT COUNT(*) count FROM character_reputations WHERE subject_id='hero_a' AND tag_id='test_reputation' AND reputation_value=99;";

        internal const string CountWorldHistoryEventsSql = "SELECT COUNT(*) count FROM world_history_events WHERE event_id='future_history';";

        internal const string CountRebellionMovementsSql = "SELECT COUNT(*) count FROM rebellion_movements WHERE movement_id='future_rebellion';";

        internal const string CountCourtSessionsSql = "SELECT COUNT(*) count FROM court_sessions WHERE session_id='future_court';";

        internal const string CountDirectorOutboxSql = "SELECT COUNT(*) count FROM director_outbox WHERE outbox_id='future_outbox';";

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static List<Dictionary<string, object>> QuerySql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, sql, parameters, transaction);
        }

        internal const string SelectEmbeddingDocumentsSql = @"SELECT d.*,w.event_type,w.dissemination_class FROM embedding_documents d
LEFT JOIN world_history_events w ON d.source_type='world_history_event' AND w.event_id=d.source_id;";

        internal const string SelectEmbeddingDocumentsSql2 = "SELECT d.*,'' event_type,'' dissemination_class FROM embedding_documents d;";
    }
}
