using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations;", parameters, transaction);
        }

        internal static void UpdateObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET timeline_id='main'; DELETE FROM schema_meta WHERE key='character_commitment_timeline_v1';", parameters, transaction);
        }

        internal static void UpdateObligations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET timeline_id=$timeline,revision=$revision WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static void DeleteSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM schema_meta WHERE key='character_commitment_timeline_v1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE obligation_id LIKE 'migration_%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations3(ReignDbConnection connection, object part, object part2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT obligation_id FROM obligations WHERE " + part + " AND " + part2 + ";", parameters, transaction);
        }

        internal static void UpdateObligations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET status='fulfilled' WHERE description=$description;", parameters, transaction);
        }

        internal static void UpdateObligations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET status='fulfilled';", parameters, transaction);
        }

        internal static void InsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('promise_bootstrap',$campaign,'bootstrap_npc','player','open',1,1,'[""player"",""bootstrap_npc""]','{""timelineId"":""promise_branch""}');", parameters, transaction);
        }

        internal static void DeleteSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM schema_meta WHERE key='explicit_accepted_commitment_bootstrap_v1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE owed_by='bootstrap_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rumor_occurrences;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_reputations;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE event_id=$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM events WHERE event_id=$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedActivityObservers(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_activity_observers WHERE activity_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedActivityEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_activity_events WHERE activity_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE correlation_id=$action;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPerformanceEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_performance_events WHERE event_id=$event;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events;", parameters, transaction);
        }

        internal static void UpdateCharacterPerformanceEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_performance_events SET source_hash='changed' WHERE competition_id='battle_0';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE payload_json::jsonb->>'proposedRevisionOf'='native_protected';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT description FROM obligations WHERE obligation_id='native_protected';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM obligations WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE event_id=$event;", parameters, transaction);
        }

        internal static void UpdateObligations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET payload_json=$payload WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed accepted wording.' WHERE turn_id='original_npc';", parameters, transaction);
        }

        internal static void UpdateConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='original_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id,text,payload_json FROM conversation_turns ORDER BY turn_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='revision_npc';", parameters, transaction);
        }

        internal static void DeleteConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_turns WHERE event_id IN ('original','revision');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE event_id='shared';", parameters, transaction);
        }

        internal static void UpdateObligations6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE obligations SET known_by_json=$known,payload_json=$payload WHERE event_id='shared';", parameters, transaction);
        }

        internal static void UpdateConversationTurns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"npc\",\"player\"]' WHERE turn_id='shared_player';", parameters, transaction);
        }

        internal static void UpdateConversationTurns6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET channel='letter' WHERE event_id='shared';", parameters, transaction);
        }

        internal static void UpdateConversationTurns7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='tampered source' WHERE turn_id=$turn;", parameters, transaction);
        }

        internal static void InsertConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('accepted_unit_session',$campaign,'npc','player','open',1,1,'[""npc"",""player"",""listener""]','{""timelineId"":""agency_timeline""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountDynamicDynamic(ReignDbConnection connection, object table, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM " + table + (part) + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turn_fts WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE event_id=$event AND source='dialogue_llm' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id;", parameters, transaction);
        }

        internal static void InsertIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult) VALUES($id,$id,1,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneHandoffs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_scene_handoffs WHERE owner_id='listener';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneHandoffs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_scene_handoffs WHERE owner_id='npc';", parameters, transaction);
        }

        internal static void UpdateConversationContinuityPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_continuity_pending SET status='discarded_test_future';", parameters, transaction);
        }

        internal static void UpdateConversationTurns8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed source must not authorize recovery.' WHERE event_id=$event AND speaker_id='npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationAcceptedUnits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_json FROM conversation_accepted_units WHERE unit_id=$unit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPgTables(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM pg_tables WHERE schemaname=$schema;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE s.npc_id='npc' AND t.status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT version() AS version;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT letter_id FROM letters;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationNegotiationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM conversation_negotiation_turns WHERE turn_id='future_pressure';", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuityVersions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity_versions;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT revision FROM conversation_continuity WHERE record_id=$id;", parameters, transaction);
        }

        internal static void InsertTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO temporal_knowledge_assertions(assertion_id,campaign_id,fact_key,claim,perspective_owner_id,assertion_kind,truth_status,valid_from_ts,observed_ts,known_by_json,visibility,source_event_id)
VALUES($id,$campaign,$id,$claim,$owner,'interpretation','asserted',1,$ts,'[""npc_a"",""npc_b""]','public','legacy_source');", parameters, transaction);
        }

        internal static void InsertCourtSocialSignalEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_social_signal_evidence(campaign_id,timeline_id,exchange_id,signal_id,signal_type,speaker_id,target_id,supporting_quote,accepted,created_ts)
VALUES($campaign,'timeline_a','shared_night','pair_intimacy','sexual_intimacy_completed','npc_a','player','Accepted private shared intimacy between this pair.',1,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='parent_concern' AND status='open';", parameters, transaction);
        }

        internal static void DeleteConversationSceneState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_scene_state WHERE hero_id='npc_a';", parameters, transaction);
        }

        internal static void InsertConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('old_tournament',$campaign,'npc_a','player','closed',1,2,'[""npc_a"",""player""]');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE assertion_id='legacy_1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='recovered_father_concern';", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='father_source';", parameters, transaction);
        }

        internal static void InsertMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memories(memory_id,event_id,owner_id,memory_type,ts,world_day,location_id,summary,participants_json,known_by_json,visibility,importance,tags_json,status,payload_json)
VALUES('consolidated_closeness','affection_event','npc_a','episodic',4,0,'town_a','She accepted his cloak, leaned against him, and asked him to stay.','[""npc_a"",""player""]','[""npc_a"",""player""]','private',1,'[""intimacy"",""vulnerability""]','consolidated','{}');", parameters, transaction);
        }

        internal static void InsertMemories2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memories(memory_id,event_id,owner_id,memory_type,ts,world_day,location_id,summary,participants_json,known_by_json,visibility,importance,tags_json,status,payload_json)
VALUES('group_reported_intimacy','affection_event','npc_a','episodic',4,0,'town_a','The player described a private night with another participant.','[""npc_a"",""player"",""npc_other""]','[""npc_a"",""player"",""npc_other""]','private',1,'[""intimacy""]','consolidated','{}');", parameters, transaction);
        }

        internal static void InsertConversationContinuityVersions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts) VALUES('atomic_letter_1','atomic_letter','test_collision',1,'{}',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT record_id FROM conversation_continuity WHERE record_id='atomic_letter';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='episode_report_event_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='episode_report';", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts
(receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,act_kind,severity_tier,summary,current_conduct_quote,created_ts)
VALUES($receipt,$campaign,'timeline_a','conduct_exchange','conduct_turn','npc_a','player','help','meaningful',$summary,$quote,1);", parameters, transaction);
        }

        internal static void InsertConversationSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,'npc_a','player','in_person','open',2,2,20,20,'town_a','[""npc_a"",""player""]','{}');", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts
(receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,mode,act_kind,severity_tier,summary,current_conduct_quote,created_ts)
VALUES('native_conduct_receipt',$campaign,'timeline_a',$exchange,$turn,'npc_a','player','in_person_conversation','care','meaningful',$summary,$quote,2);", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET timeline_id='' WHERE receipt_id='native_conduct_receipt';", parameters, transaction);
        }

        internal static void InsertConversationSessions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$observer,$peer,'in_person','closed',1,9,19,19,'town_a','[""scope_npc"",""scope_player""]','{}');", parameters, transaction);
        }

        internal static void UpdateConversationTurns9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET accepted_sequence=201 WHERE turn_id='scope_future_sequence';", parameters, transaction);
        }

        internal static void InsertMemories3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memories
(memory_id,event_id,owner_id,ts,world_day,summary,participants_json,visibility,tags_json,status,payload_json)
VALUES($id,$event,$observer,$timestamp,19,$summary,'[""scope_npc"",""scope_player""]','private','[""trust""]','consolidated',$payload);", parameters, transaction);
        }

        internal static void InsertMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_sources(document_type,document_id,source_type,source_id)
VALUES('memory','pair_memory_private_lineage','turn','scope_hidden');", parameters, transaction);
        }

        internal static void UpdateConversationTurns10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='I trust you with an altered ledger.' WHERE turn_id='scope_restored';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuityPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity_pending WHERE pending_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationContinuityPending2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_continuity_pending SET source_hash='invalid' WHERE pending_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='superseded' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationContinuityPending3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_continuity_pending SET source_hash=$hash WHERE pending_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text,status=$status WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationSessions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,'npc','player','open',1,1,'[""npc"",""player"",""listener""]','{""timelineId"":""input_branch""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE event_id=$id AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='observer_source';", parameters, transaction);
        }

        internal static void UpdateMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET participants_json=$ids,known_by_json=$ids,hidden_from_json='[]' WHERE memory_id='mixed_fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE memory_id='mixed_fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT summary FROM memories WHERE memory_id='mixed_fixture';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id='private_input_acceptance' AND status='source_only';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='private_input_acceptance_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id='observer_scene';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='observer_scene' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM conversation_continuity WHERE record_id='question_plan';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT revision FROM group_conversation_state WHERE session_id='group_questions';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM group_conversation_contributions WHERE session_id='group_questions' AND role='npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM group_conversation_contributions WHERE session_id='group_questions' AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT validation_status FROM group_conversation_contributions WHERE source_event_id='group_failed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT validation_status FROM group_conversation_contributions WHERE source_event_id='group_success';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='information_request';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='nominal_question';", parameters, transaction);
        }

        internal static void UpdateConversationTurns14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='The source was changed.' WHERE turn_id='plan_answer';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='question_plan';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='review_question';", parameters, transaction);
        }

        internal static void UpdateConversationTurns15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET world_day=6 WHERE turn_id='future_day';", parameters, transaction);
        }

        internal static void UpdateConversationTurns16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET ts=9000 WHERE turn_id='future_time';", parameters, transaction);
        }

        internal static void UpdateConversationTurns17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET event_id='a_answer_event' WHERE turn_id='ambiguous_alias';", parameters, transaction);
        }

        internal static void UpdateConversationTurns18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='inactive' WHERE turn_id='ambiguous_alias';", parameters, transaction);
        }

        internal static void UpdateConversationTurns19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed source.' WHERE turn_id='z_original';", parameters, transaction);
        }

        internal static void InsertConversationSessions7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,$npc,'player',$status,1,1,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationReferenceEdgesDynamic(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM conversation_reference_edges" + (part) + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_reference_edges WHERE source_event_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='old_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT document_id FROM memory_sources WHERE document_type LIKE 'conversation_reference%';", parameters, transaction);
        }

        internal static void UpdateConversationTurns20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='old_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$event AND role='player' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id AND status='active';", parameters, transaction);
        }

        internal static void UpdateConversationTurns21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"other\"]' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json=$audience WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"other\",\"player\"]' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=text || ' Changed after the reference.' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_reference_edges SET world_day=40 WHERE from_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='old_npc';", parameters, transaction);
        }

        internal static void UpdateConversationReferenceEdges2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_reference_edges SET relation='withdraws' WHERE from_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationReferenceEdges3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_reference_edges SET owner_id='other' WHERE from_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_reference_edges(edge_id,owner_id,timeline_id,restore_generation,from_type,from_id,from_hash,to_type,to_id,to_hash,
relation,source_event_id,as_of_sequence,world_day,created_ts,payload_json)
SELECT $prefix || n,'npc','main',$generation,'turn',$from,$fromHash,'turn',$to,$toHash,'refers_to',$from,$sequence,10,10,$payload FROM generate_series(1,32) n;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE source_event_id=$event AND to_type<>'letter';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE source_event_id=$event AND payload_json LIKE '%targetTypeDerivedFromUniqueOffer%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status FROM letters WHERE letter_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM conversation_reference_edges WHERE restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_type='letter' AND to_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_id IN ('secret_npc','foreign_npc','future_npc');", parameters, transaction);
        }

        internal static void UpdateMemoryPrecisionState3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current'; DELETE FROM memory_restored_sources WHERE source_id='old_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_id='old_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation=$generation AND to_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT edge_id FROM conversation_reference_edges WHERE restore_generation<>$generation;", parameters, transaction);
        }

        internal static void DeleteConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_reference_edges; UPDATE conversation_turns SET text='Abandoned post-snapshot replacement.' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationSessions8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('source_session',$campaign,'npc','player','open',1,1,'[""player"",""npc""]');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void DeleteConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_turn_fts WHERE turn_id='accepted_npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns25(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='source_session' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns26(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='technical_new';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurnFts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turn_fts WHERE turn_id='technical_new';", parameters, transaction);
        }

        internal static void UpdateConversationTurns25(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='active' WHERE turn_id='technical_new';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns27(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='recovered_speech';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurnFts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turn_fts WHERE turn_id='recovered_speech';", parameters, transaction);
        }

        internal static void UpdateGroupConversationContributions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE group_conversation_contributions SET audience_json=$audience WHERE source_event_id='floor-accepted';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns28(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='floor-quiet' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns29(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE exchange_id='floor-quiet' AND status='active';", parameters, transaction);
        }
    }
}
