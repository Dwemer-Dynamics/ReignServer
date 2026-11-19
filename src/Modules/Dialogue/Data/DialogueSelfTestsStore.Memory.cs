using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static void InsertConversationSessions13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('old_mill',$campaign,'npc','player','closed',1,2,'[""npc"",""player""]','{""timelineId"":""route_branch""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM conversation_turns WHERE channel=$channel;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectChildhoodExperiences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT summary FROM childhood_experiences WHERE hero_id='child';", parameters, transaction);
        }

        internal static void InsertRouteAtomicFixture(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO route_atomic_fixture(id) VALUES('fault');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRouteAtomicFixture(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM route_atomic_fixture;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationRouteAcceptance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM conversation_route_acceptance WHERE unit_id=$unit;", parameters, transaction);
        }

        internal static void UpdateConversationRouteAcceptance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_route_acceptance SET accepted_reply_hash='changed' WHERE accepted_reply_hash=$hash;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns51(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE channel='empty_recovery' AND role='npc';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRouteAcceptance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_route_acceptance WHERE unit_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns44(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET accepted_sequence=$sequence WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns52(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT *,xmin::text AS source_revision FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationTurns45(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='A changed source.' WHERE turn_id='question';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryLateInteractionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_late_interaction_attempts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryLateInteractionSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_late_interaction_sources;", parameters, transaction);
        }

        internal static void UpdateMemoryLateInteractionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_late_interaction_attempts SET next_attempt_ts=0;", parameters, transaction);
        }

        internal static void UpdateMemoryLateInteractionSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_late_interaction_sources SET schema_version=@schema;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns53(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='original';", parameters, transaction);
        }

        internal static void InsertConversationSessions14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('group_source',$campaign,'npc_a','player','closed',1,10,'[""npc_a"",""npc_b"",""player""]','{""timelineId"":""timeline_a""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,'group_source',$event,$order,'agreement_exchange',$role,$speaker,$speaker,$text,20,$ts,'[""npc_a"",""npc_b"",""player""]','{""timelineId"":""timeline_a""}');", parameters, transaction);
        }

        internal static void InsertConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,'npc_b','npc_a npc_b player','party_chat');", parameters, transaction);
        }

        internal static void InsertMemories4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status)
SELECT 'unrelated_' || n,'npc_other',100+n,'caravan unrelated private detail','[""npc_other""]','private','active' FROM generate_series(1,2000) AS n;
INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status)
VALUES('eligible_old','npc_b',1,'300 denars only after arrival','[""npc_b""]','private','active');", parameters, transaction);
        }

        internal static void InsertBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO beliefs(belief_id,believer_id,claim,ts)
SELECT 'belief_unrelated_'||n,'npc_b','Unrelated apples and oranges.',100+n FROM generate_series(1,2000) n;
INSERT INTO beliefs(belief_id,believer_id,claim,ts) VALUES('old_relevant_belief','npc_b','I believe the caravan will arrive late.',1);", parameters, transaction);
        }

        internal static void UpdateMemories2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET hidden_from_json='[\"npc_b\"]' WHERE memory_id='eligible_old';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectTemporalKnowledgeAssertions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE assertion_id=$id;", parameters, transaction);
        }

        internal static void InsertTemporalKnowledgeAssertions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO temporal_knowledge_assertions(assertion_id,campaign_id,fact_key,claim,perspective_owner_id,assertion_kind,visibility,payload_json,timeline_id,event_day,learned_day,valid_from_ts,observed_ts)
SELECT 'regional_'||n,$campaign,'regional_'||n,'caravan caravan caravan caravan','npc_other','reported','public_settlement','{""settlementId"":""other_town""}','timeline_a',20,29,100,100 FROM generate_series(1,200) n;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns54(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE source='precision_staging';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns55(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns ORDER BY turn_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySources2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_sources WHERE document_type='summary' AND document_id=$id AND source_type='turn';", parameters, transaction);
        }

        internal static void InsertSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO summaries(summary_id,owner_id,summary_type,summary,status,source,updated_ts)
VALUES('old_staged_scene','npc_a','scene','Earlier derived scene text.','active','precision_staging',1);
UPDATE conversation_sessions SET scene_summary_id='old_staged_scene' WHERE session_id='group_source';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE source='precision_staging' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE summary_id='old_staged_scene';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT f.* FROM summary_fts f JOIN summaries s ON s.summary_id=f.summary_id WHERE s.source='precision_staging' AND s.status='active';", parameters, transaction);
        }

        internal static void InsertConversationSessions15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
SELECT 'recall_probe_'||n,$campaign,'npc_b','player','closed',100+n,200+n,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}' FROM generate_series(1,160) n;
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
SELECT 'probe_turn_'||n,'recall_probe_'||n,'probe_event_'||n,1,'probe_exchange_'||n,'player','player','Recall our last conversation about the caravan.',21,200+n,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}' FROM generate_series(1,160) n;", parameters, transaction);
        }

        internal static void InsertConversationSessions16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('foundation_cache_source',$campaign,'npc_b','player','closed',400,401,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('foundation_cache_turn','foundation_cache_source','foundation_cache_event',1,'foundation_cache_exchange','npc','npc_b',
'I trust you. That does not mean I agree to leave my family.',20,401,'[""npc_b"",""player""]','{""timelineId"":""timeline_a""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns56(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='foundation_cache_turn';", parameters, transaction);
        }

        internal static void DeleteConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_turns WHERE session_id='foundation_cache_source'; DELETE FROM conversation_sessions WHERE session_id='foundation_cache_source';", parameters, transaction);
        }

        internal static void UpdateConversationTurns46(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=text || ' Revised boundary.' WHERE turn_id='source_1';", parameters, transaction);
        }

        internal static void InsertConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('whisper','group_source','private_event',4,'agreement_exchange','player','player','SECRET_UNHEARD_PRICE',20,4,'[""npc_a"",""player""]','{""timelineId"":""timeline_a""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns57(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='source_1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM group_conversation_contributions WHERE session_id='shared_beat' AND role='npc';", parameters, transaction);
        }

        internal static void InsertAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,updated_ts)
VALUES('archive_npc','archive_player','verified','Player','fixture',1,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id='archive_session';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns58(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='archive_session' AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns59(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='archive_first';", parameters, transaction);
        }

        internal static void InsertConversationSessions17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('source_session',$campaign,'npc','player','closed',100,100,'[""npc"",""player""]',$payload);", parameters, transaction);
        }

        internal static void UpdateConversationTurns47(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET accepted_sequence=CASE WHEN turn_id='source_question' THEN 10 ELSE 11 END;", parameters, transaction);
        }

        internal static void InsertMemorySources2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_sources(document_type,document_id,source_type,source_id)
VALUES('summary','linked_summary','memory','intermediate_memory'),('memory','intermediate_memory','event','source_event');", parameters, transaction);
        }

        internal static void UpdateConversationTurns48(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='inactive' WHERE event_id='source_event';", parameters, transaction);
        }

        internal static void InsertConversationSessions18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('group_window',$campaign,'other','player','closed',1,35,'[""observer"",""other"",""player""]','{""timelineId"":""window_main""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
SELECT 'window_turn_'||n,'group_window','window_event_'||n,n,'window_exchange_'||n,'player','player','Michael',
CASE WHEN n=35 THEN 'The price is 300 after arrival; no payment has occurred.' ELSE 'Complete message '||n||': '||repeat('unaltered source words ',40) END,
20,n,'[""observer"",""other"",""player""]','{""timelineId"":""window_main""}' FROM generate_series(1,35) n;
INSERT INTO summaries(summary_id,owner_id,scope,summary_type,summary,start_ts,end_ts,updated_ts,known_by_json,payload_json)
SELECT 'window_summary_'||n,'observer','scene:episode_'||n,'scene',
'Complete summary '||n||': '||repeat('retained context ',80)||' END-SUMMARY-'||n,n,n,1000-n,'[""observer""]',
'{""timelineId"":""window_main"",""worldDay"":20}' FROM generate_series(1,40) n;
INSERT INTO summaries(summary_id,owner_id,scope,summary_type,summary,start_ts,end_ts,updated_ts,known_by_json,payload_json)
VALUES('duplicate_view','observer','scene:episode_40','middle_term','DUPLICATE-VIEW',40,40,9999,'[""observer""]','{""timelineId"":""window_main""}'),
('hidden_summary','observer','scene:hidden','scene','HIDDEN-SUMMARY',90,90,90,'[""observer""]','{""timelineId"":""window_main""}'),
('future_summary','observer','scene:future','scene','FUTURE-SUMMARY',91,91,91,'[""observer""]','{""timelineId"":""window_main"",""worldDay"":60}'),
('other_branch','observer','scene:branch','scene','OTHER-BRANCH',92,92,92,'[""observer""]','{""timelineId"":""different""}'),
('unheard_summary','stranger','scene:unheard','scene','UNHEARD-SUMMARY',93,93,93,'[""stranger""]','{""timelineId"":""window_main""}');
UPDATE summaries SET hidden_from_json='[""observer""]' WHERE summary_id='hidden_summary';", parameters, transaction);
        }

        internal static void InsertSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO summaries(summary_id,owner_id,scope,summary_type,summary,start_ts,end_ts,updated_ts,known_by_json,payload_json)
VALUES('oversized_duplicate','observer','scene:episode_40','scene',$archive,40,40,2000,'[""observer""]','{""timelineId"":""window_main"",""compactionStatus"":""incomplete"",""method"":""extractive_fallback""}'),
('retained_archive','observer','scene:old_caravan','middle_term',$archive,41,41,2001,'[""observer""]','{""timelineId"":""window_main"",""compactionStatus"":""incomplete"",""method"":""extractive_fallback""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT summary FROM summaries WHERE summary_id='retained_archive';", parameters, transaction);
        }

        internal static void UpdateConversationTurns49(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_turns SET exchange_id='old_caravan_terms',
role=CASE WHEN turn_order=2 THEN 'player' ELSE 'npc' END,
speaker_id=CASE WHEN turn_order=2 THEN 'player' ELSE 'observer' END,
speaker_name=CASE WHEN turn_order=2 THEN 'Michael' ELSE 'Observer' END,
text=CASE turn_order WHEN 1 THEN 'What are the caravan price and payment conditions?'
WHEN 2 THEN '300 only after arrival. No payment has occurred.' ELSE 'I acknowledge those terms and that no payment has occurred.' END
WHERE session_id='group_window' AND turn_order<=3;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE summary_id='retained_archive';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns60(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='group_window' ORDER BY ts,turn_order;", parameters, transaction);
        }

        internal static void InsertConversationSessions19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('binding_window',$campaign,'observer','player','closed',1,520,'[""observer"",""player""]','{""timelineId"":""window_main""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
SELECT 'binding_'||n,'binding_window','binding_event',n,'binding_exchange','player','player','Michael','Yes.',20,n,
'[""observer"",""player""]',CASE WHEN n=520 THEN '{""timelineId"":""window_main"",""inputSourceId"":""input_current""}'
ELSE '{""timelineId"":""window_main""}' END FROM generate_series(1,520) n;", parameters, transaction);
        }

        internal static void InsertWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES('parent',$campaign,'','',7,$head,1,$created,$created);
INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('past',$campaign,'npc','player','closed',1,4,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('current',$campaign,'npc','player','open',5,0,'[""npc"",""player""]','{""timelineId"":""child""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES('horse','past','horse',1,'horse','player','player','player',$story,19,1,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('frogs','past','frogs',2,'horse','npc','npc','npc',$reply,19,2,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('secret','past','secret',3,'secret','npc','npc','npc','Private stable password silverfox.',19,3,'[""npc""]','{""timelineId"":""parent""}'),
('sibling','past','sibling',4,'sibling','npc','npc','npc','Tempest was sold in an abandoned future.',19,4,'[""npc"",""player""]','{""timelineId"":""sibling""}');
INSERT INTO beliefs(belief_id,believer_id,claim,ts,payload_json) VALUES('private','npc','I suspect the stablekeeper.',3,'{""timelineId"":""parent""}');
INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status,payload_json)
VALUES('horse_memory','npc',3,'Tempest gets angry with frogs.','[""npc""]','private','active','{""timelineId"":""parent"",""worldDay"":19}');", parameters, transaction);
        }

        internal static void InsertConversationTurnFts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) SELECT turn_id,text,speaker_id,participants_json,channel FROM conversation_turns;", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,source,is_complete,dissemination_class,payload_json,created_utc)
VALUES($id,$campaign,'parent',$sequence,19,'tournament_finished','bannerlord_native',1,$scope,'{}',$created);
INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,role) VALUES($id,0,'player','hero','winner');", parameters, transaction);
        }

        internal static void InsertWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode)
VALUES('private_rule',$id,'entity','npc',19,'participant');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id=$id;", parameters, transaction);
        }

        internal static void UpdateCharacterPerformanceEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_performance_events SET outcome='loss' WHERE event_id='native_changed_receipt'; UPDATE world_history_events SET payload_json='{\"altered\":true}' WHERE event_id='native_changed_native';", parameters, transaction);
        }

        internal static void UpdateWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_history_timelines SET is_active=0;
INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES('child',$campaign,'parent',$fork,9,'child_000000000009_abcdef12',1,$created,$created);
INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,created_utc)
VALUES('child_000000000009_abcdef12',$campaign,'child',9,20,'test',$created);
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('later_parent','past','later_parent',5,'later','player','player','Abandoned parent future: Tempest was given away.',20,5,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('failed_question','current','failed',1,'failed','player','player','Do you remember Tempest?',20,10,'[""npc"",""player""]','{""timelineId"":""child""}'),
('failed_reply','current','failed',2,'failed','npc','npc','Tempest? I cannot recall him.',20,11,'[""npc"",""player""]','{""timelineId"":""child""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id='child';", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,source,is_complete,dissemination_class,payload_json,created_utc)
VALUES('native_after_proof',$campaign,'parent',8,20,'tournament_finished','bannerlord_native',1,'ordinary','{}',$created);
INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,role) VALUES('native_after_proof',0,'player','hero','winner');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id='native_after_proof';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns61(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='horse';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns62(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='failed_reply';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPerformanceEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_performance_events;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_restored_sources WHERE source_table='character_performance_events';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSourceContext2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_restored_source_context WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPerformanceEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT knowledge_rules_json FROM character_performance_events WHERE event_id='native_private';", parameters, transaction);
        }

        internal static void DeleteMemoryRestoredSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_restored_sources WHERE source_table='character_performance_events'; UPDATE character_performance_events SET knowledge_rules_json=$rules WHERE event_id='native_private';", parameters, transaction);
        }

        internal static void UpdateCharacterPerformanceEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_performance_events SET knowledge_rules_json=$rules WHERE event_id='native_private';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns63(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='secret';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM beliefs WHERE belief_id='private';", parameters, transaction);
        }

        internal static void UpdateConversationTurns50(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed horse source.' WHERE turn_id='horse';", parameters, transaction);
        }

        internal static void UpdateConversationTurns51(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$story WHERE turn_id='horse';", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_events SET sequence=10 WHERE timeline_id='child';", parameters, transaction);
        }

        internal static void InsertWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,created_utc) VALUES($fork,$campaign,'parent',8,20,'test',$created);", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id=$fork;", parameters, transaction);
        }

        internal static void UpdateWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_events SET sequence=9 WHERE timeline_id='child';", parameters, transaction);
        }

        internal static void UpdateSaveSyncSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE reign_meta.save_sync_snapshots SET created_utc=now()+interval '1 day' WHERE campaign_id=$campaign AND save_point_id='old';", parameters, transaction);
        }

        internal static void UpdateSaveSyncSnapshots2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE reign_meta.save_sync_snapshots SET created_utc=to_timestamp($epoch) WHERE campaign_id=$campaign AND save_point_id='old';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns64(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t
WHERE t.status='active' AND NOT EXISTS(SELECT 1 FROM memory_specialist_sources i WHERE i.turn_id=t.turn_id);", parameters, transaction);
        }

        internal static void InsertConversationSessions20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($id,$campaign,'npc','player','closed',1,10,'[""npc"",""player""]','{""timelineId"":""order_main""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,$session,$id,$order,$session,$role,$speaker,$name,$text,1,10,'[""npc"",""player""]','{""timelineId"":""order_main""}');", parameters, transaction);
        }

        internal static void UpdateConversationSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET payload_json=payload_json WHERE session_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationSessions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET end_ts=11 WHERE session_id='alpha';", parameters, transaction);
        }

        internal static void InsertConversationSessions21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($id,$campaign,'npc','player','closed',$ts,$ts,'[""npc"",""player""]','{""timelineId"":""recall_main""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns
(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,$session,$id,$order,$exchange,$role,$speaker,$speaker,$text,$day,$ts,$audience,$payload);", parameters, transaction);
        }

        internal static void UpdateConversationTurns52(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET role='player_source',status='source_only',participants_json='[\"player\"]' WHERE turn_id='projection_source';", parameters, transaction);
        }

        internal static void UpdateConversationTurns53(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET payload_json=$payload WHERE turn_id IN ('projected_player','private');", parameters, transaction);
        }

        internal static void InsertConversationTurnFts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) SELECT turn_id,text,speaker_id,participants_json,channel FROM conversation_turns WHERE status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT count(*) AS count FROM memory_specialist_sources;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT count(*) AS count FROM conversation_turns WHERE status='active';", parameters, transaction);
        }

        internal static void UpdateTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE temporal_knowledge_assertions SET claim=$claim WHERE assertion_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns65(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='projected_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT public_topics_json FROM memory_specialist_sources WHERE turn_id='projected_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns66(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='projection_source';", parameters, transaction);
        }

        internal static void UpdateMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_specialist_sources SET schema_version=$old,public_topics_json='[]',public_entities_json='[]' WHERE turn_id='projected_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySpecialistSources2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT schema_version,public_topics_json FROM memory_specialist_sources WHERE turn_id='projected_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns67(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='private';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySpecialistSources3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM memory_specialist_sources WHERE turn_id='projection_source';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns68(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='gait_callback';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns69(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='gait';", parameters, transaction);
        }

        internal static void UpdateConversationTurns54(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET speaker_id='other' WHERE turn_id='context_hidden';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns70(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='context_answer';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns71(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='loss_player';", parameters, transaction);
        }

        internal static void UpdateConversationTurns55(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='loss_player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns72(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='horse';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns73(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='failed_0_n';", parameters, transaction);
        }

        internal static void InsertConversationSessions22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($id,$campaign,'npc','player','closed',$day,$day,'[""npc"",""player""]','{""timelineId"":""growth_main""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns
(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,$session,$id,$order,$exchange,$role,$speaker,$speaker,$text,$day,$day,'[""npc"",""player""]','{""timelineId"":""growth_main""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns74(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='old' ORDER BY turn_id;", parameters, transaction);
        }

        internal static void InsertConversationTurns6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,accepted_sequence,participants_json,payload_json)
VALUES($id,'history',$id,$sequence,$id,'player','player',$id,$day,$ts,$sequence,$audience,$payload);", parameters, transaction);
        }

        internal static void InsertConversationSessions23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('history',$campaign,'npc','player','closed',1,2,'[""npc"",""player""]','{""timelineId"":""parent""}');", parameters, transaction);
        }

        internal static void InsertConversationSessions24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('past',$campaign,'npc','player','closed',1,4,'[""npc"",""listener"",""player""]','{""timelineId"":""main""}'),
('current',$campaign,'npc','player','open',5,0,'[""npc"",""player""]','{""timelineId"":""branch""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,'past',$id,$order,'exchange',$role,$who,$who,$text,20,$order,$audience,$payload);", parameters, transaction);
        }

        internal static void InsertConversationTurnFts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,$who,$audience,'party_chat');", parameters, transaction);
        }

        internal static void InsertBeliefs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO beliefs(belief_id,believer_id,claim,ts,payload_json) VALUES
('private_belief','npc','I privately suspect the orchard merchant.',3,'{""timelineId"":""main""}');
INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status,payload_json) VALUES
('old_memory','npc',3,'300 denars only after caravan arrival. Nothing has been paid.','[""npc""]','private','active','{""timelineId"":""main""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('later_same_day','past','later',6,'later','player','player','Actually pay 9000 immediately.',20,6,'[""npc"",""player""]','{""timelineId"":""main""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns75(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='later_same_day';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns76(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='sibling';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectBeliefs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM beliefs WHERE belief_id='private_belief';", parameters, transaction);
        }

        internal static void InsertConversationTurns9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
SELECT 'foreign_'||n,'past','foreign_'||n,100+n,'foreign','player','player','caravan price caravan price unrelated branch',20,100+n,
'[""npc"",""player""]','{""timelineId"":""sibling""}' FROM generate_series(1,2000) n;", parameters, transaction);
        }

        internal static void UpdateConversationTurns56(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed question after restore.' WHERE turn_id='question';", parameters, transaction);
        }

        internal static void UpdateConversationTurns57(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id='question';", parameters, transaction);
        }

        internal static void InsertConversationTurns10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES('accepted_ack','current','new_answer',1,'new_answer','npc','npc','npc',$text,20,10,'[""npc"",""player""]','{""timelineId"":""branch""}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='old_question';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity11(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE " + part + ";", parameters, transaction);
        }

        internal static void UpdateConversationTurns58(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed canonical question.' WHERE turn_id='question';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSources2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_restored_sources WHERE source_table='conversation_turns' AND source_id='question';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSources3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_restored_sources;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,'npc','player','closed',1,$ts,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySpecialistSources4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM memory_specialist_sources WHERE turn_id='source';", parameters, transaction);
        }

        internal static void UpdateConversationTurns59(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET participants_json='[\"player\",\"outsider\"]' WHERE turn_id='clarification';", parameters, transaction);
        }

        internal static void UpdateConversationTurns60(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET text='The weather is cold.' WHERE turn_id='private';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns77(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.turn_id FROM conversation_turns t
JOIN memory_specialist_sources i ON i.turn_id=t.turn_id WHERE t.turn_id='private' AND i.source_revision<>t.xmin::text;", parameters, transaction);
        }

        internal static void InsertConversationContinuity2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_continuity
(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES('travel_root',$timeline,'npc_a','player','agenda','open',0.8,1,'accepted_plan','turn_plan',1,$json);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='travel_root';", parameters, transaction);
        }

        internal static void InsertConversationSessions25(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,start_world_day,participants_json,payload_json)
VALUES('real_chat',$campaign,'npc_a','player','in_person','open',1,10,'[""player"",""npc_a""]',$json);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedTravelContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_travel_contact WHERE owner_id='npc_a';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedTravelContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_travel_contact;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id='activity_team_win';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSharedActivityEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM shared_activity_events;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('public_world_news_legacy_rules_v1','1');", parameters, transaction);
        }

        internal static void InsertWorldHistoryTimelines2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES($child,$campaign,$parent,'dialogue_win',100,'dialogue_win',0,$now,$now);", parameters, transaction);
        }

        internal static void UpdateWorldHistoryTimelines2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_timelines SET fork_event_id='unproved_fork' WHERE timeline_id=$child;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_knowledge_rules WHERE event_id='dialogue_win' AND audience_type='global';", parameters, transaction);
        }

        internal static void UpdateWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_knowledge_rules SET available_day=113 WHERE event_id='dialogue_win' AND audience_type='kingdom';", parameters, transaction);
        }

        internal static void DeleteSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM schema_meta WHERE key='world_history_immediate_tournament_news_v1';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM world_history_events WHERE event_id='dialogue_win';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM world_history_knowledge_rules WHERE event_id='dialogue_win' AND audience_type='global';", parameters, transaction);
        }
    }
}
