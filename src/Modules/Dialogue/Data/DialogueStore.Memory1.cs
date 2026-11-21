using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static void UpsertSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO summaries(
summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,location_id,
participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,
status,source,vector_id,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$owner,'scene',$summary,$events,$start,$end,$count,$location,$participants,'[]',$known,'[]','private',
$importance,$confidence,$tags,$status,$source,'',$embedding,$updated,$payload,$lane)
ON CONFLICT(summary_id) DO UPDATE SET summary=$summary,payload_json=$payload,updated_ts=$updated;", parameters, transaction);
        }

        internal static void UpdateConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET scene_summary_id=$summary WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT r.receipt_id,r.exchange_id,r.observer_id,r.target_id,r.lie_check_id,r.created_ts,
l.raw_claim,l.objective_verdict,l.knowledge_basis,l.outcome,l.evidence_event_ids_json,l.prompt_packet_json
FROM conversation_relationship_receipts r
JOIN world_history_lie_checks l ON l.lie_check_id=r.lie_check_id
WHERE r.exchange_id=$exchange AND r.status='applied' AND r.lie_check_id<>''
ORDER BY r.created_ts ASC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM summaries WHERE owner_id=$npc AND summary_type='scene' AND status='active'
AND memory_lane=$lane AND ($player='' OR participants_json LIKE $player_like) ORDER BY updated_ts DESC LIMIT 12;", parameters, transaction);
        }

        internal static void UpsertSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO summaries(
summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,location_id,
participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,
status,source,vector_id,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$owner,'arc',$summary,'[]',$start,$end,$count,'',$participants,'[]',$known,'[]','private',
0.75,$confidence,$tags,'active','conversation_arc','','not_indexed',$updated,$payload,'interpersonal_history');", parameters, transaction);
        }

        internal static void UpdateSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE summaries SET memory_lane=$lane WHERE summary_id=$id;", parameters, transaction);
        }

        internal static void DeleteMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_sources WHERE document_type='summary' AND document_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM summaries
WHERE owner_id=$npc AND summary_type='arc' AND status='active'
AND ($player='' OR participants_json LIKE $player_like);", parameters, transaction);
        }

        internal static void UpdateSummaries2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE summaries
SET summary=$summary,payload_json=$payload,updated_ts=$updated,embedding_status='not_indexed',vector_id=''
WHERE summary_id=$id;", parameters, transaction);
        }

        internal static void UpsertMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO memory_sources(document_type,document_id,source_type,source_id,ordinal,payload_json)
VALUES($document_type,$document_id,$source_type,$source_id,$ordinal,'{}');", parameters, transaction);
        }

        internal static void InsertOrIgnoreKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO knowledge_receipts(
receipt_id,event_id,npc_id,acquisition_type,confidence,reliability,acquired_day,source_id,status,payload_json,created_ts)
VALUES($id,$event,$npc,$type,$confidence,$reliability,$day,$source,'active','{}',$ts);", parameters, transaction);
        }

        internal static void UpdateMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET memory_domain=$domain WHERE memory_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE event_id='tournament_test' AND memory_type='world_knowledge' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE event_id='tournament_test' AND memory_type='personal_experience' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM knowledge_receipts WHERE event_id='tournament_test' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memories WHERE event_id='private_promise_test' AND owner_id='npc_a' AND memory_domain='commitments_and_plots' AND status='active';", parameters, transaction);
        }

        internal static void UpdateMemories2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET status='consolidated' WHERE memory_id='consolidated_semantic_memory';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE memory_id='consolidated_semantic_memory' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns48(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE summary_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM memory_sources WHERE document_type='summary' AND document_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM conversation_turns WHERE session_id=$id AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='categorized_memory_v1' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id,event_type,summary,payload_json FROM events;", parameters, transaction);
        }

        internal static void UpdateEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE events SET event_category=$category,event_subtype=$subtype WHERE event_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT memory_id,memory_type,summary,source,tags_json,payload_json FROM memories;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('categorized_memory_v1','complete');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='categorized_memory_domain_v2' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('categorized_memory_domain_v2','complete');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='conversation_turn_payload_v2' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('conversation_turn_payload_v2','complete');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');", parameters, transaction);
        }

        internal static void Begin2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,COUNT(*) AS row_count
FROM memories WHERE event_id<>'' AND visibility LIKE 'public%' AND status='active'
GROUP BY event_id HAVING COUNT(*)>1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE event_id=$event AND status='active' ORDER BY ts,memory_id;", parameters, transaction);
        }

        internal static void UpdateMemories3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET memory_domain='world_affairs' WHERE memory_id=$id;", parameters, transaction);
        }

        internal static void UpdateMemories4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memories SET memory_type='personal_experience',memory_domain='world_affairs',
visibility='private',known_by_json=$known WHERE memory_id=$id;", parameters, transaction);
        }

        internal static void UpdateMemories5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memories SET status='superseded',memory_domain='world_affairs' WHERE memory_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,
participants_json,close_reason,scene_summary_id,legacy_inferred,payload_json)
VALUES($id,$campaign,$npc,'',$channel,'closed',$start,$end,$start_day,$end_day,'','[]','legacy_import','',1,'{}');", parameters, transaction);
        }

        internal static void DeleteMemoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_fts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE status='active';", parameters, transaction);
        }

        internal static void DeleteConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_turn_fts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns49(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE npc_id=$npc AND status='open' ORDER BY start_ts;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,
participants_json,close_reason,scene_summary_id,legacy_inferred,payload_json)
VALUES($id,$campaign,$npc,$player,$channel,'open',$ts,0,$day,0,$location,$participants,'','',0,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_sessions WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE status='open' ORDER BY start_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns50(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM summaries WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSpeechOnlyNpcTurns(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.turn_id FROM conversation_turns t
JOIN conversation_accepted_units u ON u.unit_id=t.event_id
WHERE t.session_id=$id AND t.status='active' AND t.role='npc' AND u.receipt_json::jsonb->>'outputContract'=$contract;", parameters);
        }

        internal static void UpdateConversationSessions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET status=$status,end_ts=$ts,end_world_day=$day,close_reason=$reason WHERE session_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationSessions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_sessions SET status=$status,end_ts=$ts,end_world_day=$day,
close_reason=$reason WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT result_json FROM memory_background_jobs WHERE job_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns51(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(MAX(turn_order),-1)+1 AS next_order FROM conversation_turns WHERE session_id=$session;", parameters, transaction);
        }

        internal static void UpdateConversationSessions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_sessions SET end_ts=$ts,end_world_day=$day,location_id=CASE WHEN $location<>'' THEN $location ELSE location_id END WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE session_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT participants_json FROM conversation_sessions WHERE session_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns52(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$turn LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns53(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND session_id=$session AND speaker_id=$player AND status='active';", parameters, transaction);
        }

        internal static void UpdateConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_turns SET status='superseded_projection' WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void DeleteConversationTurnFts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_turn_fts WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationSessions7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_sessions SET end_ts=$ts,end_world_day=$day,
location_id=CASE WHEN $location<>'' THEN $location ELSE location_id END,participants_json=$participants
WHERE session_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,location_id,
participants_json,status,payload_json)
VALUES($turn,$session,$event,$ordinal,$exchange,$role,$speaker_id,$speaker_name,$text,$channel,$day,$ts,$location,$participants,$status,$payload);", parameters, transaction);
        }

        internal static void InsertConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) VALUES($id,$text,$speaker,$participants,$channel);", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions(
session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,
participants_json,close_reason,scene_summary_id,legacy_inferred,payload_json)
VALUES($id,$campaign,$npc,$player,'correspondence','closed',$ts,$ts,$day,$day,'',$participants,'letter_delivered','',0,$payload);", parameters, transaction);
        }

        internal const string SelectConversationTurnsSql2 = "SELECT turn_id,payload_json FROM conversation_turns WHERE length(payload_json)>2048;";

        internal const string UpdateConversationTurnsSql = "UPDATE conversation_turns SET payload_json=$payload WHERE turn_id=$id;";

        internal static List<Dictionary<string, object>> SelectConversationRouteAcceptance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_route_acceptance WHERE unit_id=$unit;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$npc,$player,$channel,'open',$now,$now,$day,$day,$location,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns54(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(MAX(turn_order),-1)+1 AS n FROM conversation_turns WHERE session_id=$session;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationRouteAcceptance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_route_acceptance(unit_id,input_hash,accepted_reply_hash,result_json,restore_generation,created_ts)
VALUES($unit,$input,$reply,$result,$generation,$now) ON CONFLICT(unit_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRouteAcceptance2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT input_hash,accepted_reply_hash FROM conversation_route_acceptance WHERE unit_id=$unit;", parameters, transaction);
        }

        internal static void UpdateConversationSessions8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_sessions SET end_ts=$now,end_world_day=$day,
status=CASE WHEN $phase='closing' THEN 'closed' ELSE status END WHERE session_id=$session;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns55(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.turn_id,t.session_id,t.exchange_id,t.speaker_id,t.status,
t.world_day,t.ts,t.accepted_sequence,t.participants_json,t.payload_json,i.source_hash,i.text_hash,i.transcript_hash,
CASE WHEN t.speaker_id=$npc THEN 'self' ELSE 'public' END AS view
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
LEFT JOIN memory_late_interaction_sources i ON i.turn_id=t.turn_id
 AND i.view=CASE WHEN t.speaker_id=$npc THEN 'self' ELSE 'public' END
 AND i.source_revision=t.xmin::text AND i.model_hash=$model AND i.schema_version=$schema
WHERE " + eligible + " ORDER BY t.turn_id LIMIT 50001;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns56(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id WHERE "
                        + eligible + " AND t.role='player' ORDER BY t.turn_id LIMIT 50001;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Statement3(ReignDbConnection connection, object missing, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, missing + " ORDER BY t.accepted_sequence DESC,t.turn_id LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Statement4(ReignDbConnection connection, object missing, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, missing + " ORDER BY t.accepted_sequence,t.turn_id LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns57(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT *,xmin::text AS source_revision FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void DeleteMemoryLateInteractionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_late_interaction_attempts WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpsertMemoryLateInteractionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_late_interaction_attempts(turn_id,source_revision,model_hash,schema_version,attempts,next_attempt_ts,outcome)
VALUES($id,$revision,$model,$schema,1,$next,$outcome) ON CONFLICT(turn_id) DO UPDATE SET
attempts=CASE WHEN memory_late_interaction_attempts.source_revision=excluded.source_revision
 AND memory_late_interaction_attempts.model_hash=excluded.model_hash AND memory_late_interaction_attempts.schema_version=excluded.schema_version
 THEN memory_late_interaction_attempts.attempts+1 ELSE 1 END,source_revision=excluded.source_revision,
model_hash=excluded.model_hash,schema_version=excluded.schema_version,next_attempt_ts=excluded.next_attempt_ts,outcome=excluded.outcome;", parameters, transaction);
        }

        internal static void UpsertMemoryLateInteractionSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_late_interaction_sources
(turn_id,view,source_hash,text_hash,source_revision,model_hash,schema_version,worker_epoch,updated_ts,transcript_hash)
VALUES($id,$view,$source,$text,$revision,$model,$schema,$epoch,$ts,$transcript)
ON CONFLICT(turn_id,view) DO UPDATE SET source_hash=excluded.source_hash,text_hash=excluded.text_hash,
source_revision=excluded.source_revision,model_hash=excluded.model_hash,schema_version=excluded.schema_version,
worker_epoch=excluded.worker_epoch,updated_ts=excluded.updated_ts,transcript_hash=excluded.transcript_hash;", parameters, transaction);
        }

        internal const string SelectConversationTurnsSql3 = @"SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t
LEFT JOIN memory_late_interaction_attempts a ON a.turn_id=t.turn_id AND a.source_revision=t.xmin::text
 AND a.model_hash=$model AND a.schema_version=$schema
WHERE t.status='active' AND t.text<>'' AND (a.turn_id IS NULL OR (a.attempts<4 AND a.next_attempt_ts<=$now))
AND (SELECT COUNT(*) FROM memory_late_interaction_sources i WHERE i.turn_id=t.turn_id
AND i.source_revision=t.xmin::text AND i.model_hash=$model AND i.schema_version=$schema AND i.worker_epoch=$epoch AND i.transcript_hash<>'')<2";

        internal static void UpsertSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('world_history_lie_detection_version','1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_lie_checks WHERE episode_key=$key LIMIT 1;", parameters, transaction);
        }

        internal static void InsertWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_lie_checks(
lie_check_id,episode_key,claim_check_id,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,claimant_roguery,claimant_charm,claimant_average,target_roguery,target_charm,target_average,
formula_version,success_chance,roll_value,outcome,reaction_strategy,exploit_opening,evidence_event_ids_json,known_event_ids_json,
matched_json,mismatched_json,prompt_packet_json,created_ts,updated_ts)
VALUES($id,$episode,$claimCheck,$campaign,$timeline,$day,$claimant,$target,$claim,$normalized,$verdict,$basis,
$cr,$cc,$ca,$tr,$tc,$ta,$formula,$chance,$roll,$outcome,$reaction,$exploit,$evidence,$known,$matched,$mismatched,$packet,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryLieChecks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_lie_checks WHERE lie_check_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT player_id FROM conversation_sessions
WHERE campaign_id=$campaign AND lower(player_id)=lower($target) AND trim(player_id)<>'' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='player_lie_detection_removed_v1' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT DISTINCT player_id FROM conversation_sessions WHERE trim(player_id)<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryLieChecks3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT lie_check_id,target_id FROM world_history_lie_checks;", parameters, transaction);
        }

        internal static void DeleteMemoryFts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_fts WHERE memory_id IN (SELECT memory_id FROM memories WHERE event_id=$id OR memory_id=$memory OR memory_id=$discovery);", parameters, transaction);
        }

        internal static void DeleteMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memories WHERE event_id=$id OR memory_id=$memory OR memory_id=$discovery;", parameters, transaction);
        }

        internal static void DeleteBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM beliefs WHERE event_id=$id;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryClaimBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_claim_beliefs WHERE lie_check_id=$id;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM world_history_lie_checks WHERE lie_check_id=$id;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('player_lie_detection_removed_v1','complete');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryLieChecks4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM world_history_lie_checks
WHERE ($claimant='' OR claimant_id=$claimant) AND ($target='' OR target_id=$target) ORDER BY created_ts DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_type FROM world_history_events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryKnowledgeRules2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM world_history_knowledge_rules WHERE event_id=$event AND available_day<=$day AND
((audience_type='entity' AND audience_id=$target) OR (audience_type='kingdom' AND audience_id=$kingdom) OR audience_type='global') ORDER BY available_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,event_type,category,phase,world_day,location_id,location_name,summary,dissemination_class
FROM world_history_events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertWorldHistoryClaimBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_claim_beliefs(belief_id,lie_check_id,campaign_id,timeline_id,believer_id,claimant_id,
raw_claim,normalized_claim,state,confidence,evidence_event_ids_json,created_day,disproven_day,discovered_event_ids_json,created_ts,updated_ts)
VALUES($id,$lie,$campaign,$timeline,$believer,$claimant,$claim,$normalized,'accepted',$confidence,$evidence,$day,-1,'[]',$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryClaimBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_claim_beliefs WHERE believer_id=$believer AND state='accepted';", parameters, transaction);
        }

        internal static void UpdateWorldHistoryClaimBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_history_claim_beliefs SET state='disproven',disproven_day=$day,discovered_event_ids_json=$events,updated_ts=$ts WHERE belief_id=$id;", parameters, transaction);
        }

        internal static void UpdateBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE beliefs SET confidence=0,payload_json=$payload WHERE event_id=$lie;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryClaimBeliefForLieCheck(ReignDbConnection connection, Dictionary<string, object> parameters, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_claim_beliefs WHERE lie_check_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns58(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE owner_id=$owner AND status='active' ORDER BY memory_id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_background_jobs
(job_id,job_type,owner_id,group_key,session_id,status,source_hash,processing_version,restore_generation,created_ts,updated_ts)
VALUES($id,$type,$owner,$group,$session,'pending',$hash,$version,$generation,$ts,$ts) ON CONFLICT(job_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpdateMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE memory_background_jobs SET status='running',attempt_count=attempt_count+1,
lease_token=$token,lease_until=$until,updated_ts=$now
WHERE job_id=$id AND attempt_count<4 AND next_attempt_ts<=$now
AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now)) RETURNING *;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE job_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateMemoryBackgroundJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_background_jobs SET lease_until=$until,updated_ts=$now WHERE job_id=$id AND status='running' AND lease_token=$token;", parameters, transaction);
        }

        internal static void UpdateMemoryBackgroundJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memory_background_jobs SET status=$status,result_json=$result,last_error='',lease_token='',lease_until=0,updated_ts=$now
WHERE job_id=$id AND lease_token=$token AND status='running';", parameters, transaction);
        }

        internal static void UpdateMemoryBackgroundJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memory_background_jobs SET status=$status,last_error=$error,result_json=$result,lease_token='',lease_until=0,next_attempt_ts=$next,updated_ts=$now
WHERE job_id=$id AND lease_token=$token AND status='running';", parameters, transaction);
        }

        internal static void UpdateMemoryBackgroundJobs5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_background_jobs SET status='incomplete',last_error='Retry limit reached after lease expiry' WHERE status='running' AND lease_until<=$now AND attempt_count>=4;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_background_jobs WHERE processing_version='legacy' AND status IN ('pending','running','failed');", parameters, transaction);
        }

        internal static void UpdateMemoryBackgroundJobs6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_background_jobs SET status='superseded' WHERE job_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT job_id FROM memory_background_jobs WHERE attempt_count<4 AND next_attempt_ts<=$now
AND (status IN ('pending','failed') OR (status='running' AND lease_until<=$now)) ORDER BY created_ts LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT status,COUNT(*) AS count FROM memory_background_jobs GROUP BY status;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryBackgroundJobs6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_background_jobs ORDER BY updated_ts DESC LIMIT 100;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns59(ReignDbConnection connection, object part, object part2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.status='active' AND "
                    + part + @" AND t.world_day<=$day
AND t.accepted_sequence<=$asOfSequence AND t.ts<=$asOfTimestamp
AND " + part2 + @"
ORDER BY t.ts DESC,t.turn_order DESC,t.turn_id LIMIT $batch OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries5(ReignDbConnection connection, object part, object part2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM summaries WHERE status='active'
AND summary_type IN ('scene','middle_term')
AND (summary_type='scene' OR scope LIKE 'scene:%' OR scope LIKE 'conversation:%'
 OR EXISTS (SELECT 1 FROM conversation_turns t WHERE t.status='active' AND t.event_id IN
 (SELECT jsonb_array_elements_text(COALESCE(NULLIF(summaries.source_events_json,''),'[]')::jsonb)))) AND " + part + " AND " + part2 + @"
ORDER BY COALESCE(NULLIF(end_ts,0),NULLIF(start_ts,0),updated_ts) DESC,
CASE WHEN length(summary)<=$compactLimit AND COALESCE(payload_json::jsonb->>'compactionStatus','') NOT IN ('incomplete','empty') THEN 0 ELSE 1 END,
CASE WHEN summary_type='scene' THEN 0 ELSE 1 END,summary_id LIMIT $batch OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity10(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_continuity WHERE owner_id=$owner AND " + part + @"
AND status<>'invalidated' AND kind<>'question' AND (kind<>'relationship' OR peer_id=$peer)
AND (kind<>'agenda' OR status NOT IN ('resolved','abandoned')) ORDER BY importance DESC,updated_ts DESC,record_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSocialSignalEvidence2(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_social_signal_evidence WHERE campaign_id=$campaign AND " + part + @"
AND accepted=1 AND signal_type='sexual_intimacy_completed'
AND ((speaker_id=$owner AND target_id=$peer) OR (speaker_id=$peer AND target_id=$owner)) ORDER BY created_ts,signal_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipMilestones2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM relationship_milestones WHERE subject_id=$owner AND target_id=$peer
AND status='active' ORDER BY stability DESC,updated_ts DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts5(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts WHERE campaign_id=$campaign AND " + part + @"
AND observer_id=$owner AND target_id=$peer AND status='applied' AND severity_tier<>'routine' ORDER BY created_ts,receipt_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns60(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.role='npc' AND t.speaker_id=$owner AND s.player_id=$peer AND t.status='active' AND t.world_day<=$day
AND (lower(t.text) LIKE '%trust you%' OR lower(t.text) LIKE '%kiss%' OR lower(t.text) LIKE '%our night%'
 OR lower(t.text) LIKE '%betrayed me%' OR lower(t.text) LIKE '%shared a bed%' OR lower(t.text) LIKE '%our partnership%')
ORDER BY t.ts,t.turn_order,t.turn_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryProjections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_projections WHERE projection_id=$id AND restore_generation=$generation AND projection_generation=$projection AND source_hash=$revision;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns61(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(MAX(accepted_sequence),0) AS watermark FROM conversation_turns WHERE status='active';", parameters, transaction);
        }

        internal static void UpsertMemoryProjections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_projections(projection_id,timeline_id,owner_id,peer_id,restore_generation,projection_generation,source_hash,watermark,payload_json,updated_ts)
VALUES($id,$timeline,$owner,$peer,$generation,$projection,$revision,$watermark,$payload,$ts)
ON CONFLICT(projection_id) DO UPDATE SET restore_generation=$generation,projection_generation=$projection,source_hash=$revision,watermark=$watermark,payload_json=$payload,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Statement5(ReignDbConnection connection, object orderedSql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, orderedSql+" LIMIT $batch OFFSET $offset;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns62(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND status='active' AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns63(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"WITH RECURSIVE lineage(source_type,source_id,depth) AS (
SELECT source_type,source_id,1 FROM memory_sources WHERE document_type=$type AND document_id=$id
UNION ALL SELECT m.source_type,m.source_id,l.depth+1 FROM memory_sources m JOIN lineage l
ON m.document_type=l.source_type AND m.document_id=l.source_id WHERE l.depth<3)
SELECT DISTINCT t.* FROM conversation_turns t WHERE ((t.event_id=$event AND $event<>'') OR t.turn_id IN (SELECT source_id FROM lineage WHERE source_type='turn')
OR t.event_id IN (SELECT source_id FROM lineage WHERE source_type='event'))
ORDER BY t.ts,t.turn_order,t.turn_id;", parameters, transaction);
        }

        internal static string SelectConversationTurnsSql4(object eligible)
        {
            return @"SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
 WHERE " + eligible + " AND s.session_id<>$session AND s.status IN ('closed','interrupted') ORDER BY t.ts DESC,t.turn_order DESC,t.turn_id";
        }

        internal static void InsertOrIgnoreMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_precision_state(state_id,mode,restore_generation,updated_ts)
VALUES('current','shadow',$generation,$ts) ON CONFLICT(state_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('memory_precision_v1','complete');", parameters, transaction);
        }

        internal static void Do(ReignDbConnection connection, object table, object table2, object table3, object table4, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname='precision_revision_" + table
                        + "' AND tgrelid='" + table2 + "'::regclass) THEN CREATE TRIGGER precision_revision_" + table3 + " AFTER INSERT OR UPDATE OR DELETE ON "
                        + table4 + " FOR EACH STATEMENT EXECUTE FUNCTION memory_precision_source_changed(); END IF; END $$;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_precision_state WHERE state_id='current';", parameters, transaction);
        }

        internal static void UpdateMemoryPrecisionState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_precision_state SET restore_generation=$generation,updated_ts=$ts WHERE state_id='current';", parameters, transaction);
        }

        internal static void UpdateMemoryBackgroundJobs7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_background_jobs SET status='pending',lease_token='',lease_until=0 WHERE status='running';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic3(ReignDbConnection connection, object table, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + key + "=$id FOR UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns64(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND status='active' ORDER BY turn_order DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurnsDynamicDynamic(ReignDbConnection connection, object letterPart, object communicationPart, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(n) AS watermark FROM (SELECT COALESCE(MAX(accepted_sequence),0) AS n FROM conversation_turns"
                + letterPart + communicationPart + ") sources;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns65(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE status='active' AND
(turn_id IN (SELECT jsonb_array_elements_text(CAST($ids AS jsonb)))
OR exchange_id IN (SELECT jsonb_array_elements_text(CAST($exchanges AS jsonb)))) ORDER BY ts DESC,turn_order DESC LIMIT 512;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT sequence FROM world_history_events WHERE event_id=$fork AND timeline_id=$parent;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id,sequence FROM world_history_events WHERE timeline_id=$child ORDER BY sequence LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id=$timeline AND campaign_id=$campaign;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCreatedUtc(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT extract(epoch FROM created_utc)::double precision AS created_epoch
FROM reign_meta.save_sync_snapshots WHERE campaign_id=$campaign AND save_point_id=$point AND schema_name=$schema;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT to_regclass($table)::text AS name;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines3(ReignDbConnection connection, object schema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM " + schema
                    + ".world_history_timelines WHERE campaign_id=$campaign AND timeline_id=$timeline AND is_active=1 AND head_sequence=$sequence AND head_event_id=$head;", parameters, transaction);
        }

        internal static void InsertOrIgnoreMemoryRestoredSources(ReignDbConnection connection, object key, object value, object part, object schema, object key2, object key3, object value2, object value3, object part2, object part3, object part4, object day, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO memory_restored_sources(source_table,source_id,restore_generation,content_hash) SELECT '"
                        + key + "',saved." + value + ",$generation," + part + " FROM " + schema + "." + key2
                        + " saved JOIN " + key3 + " current_source ON current_source." + value2 + "=saved." + value3
                        + " AND " + part2 + "=" + part3
                        + " WHERE " + part4 + "=$ancestor AND " + day + "<=$day"
                        + " ON CONFLICT(source_table,source_id,restore_generation) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdateMemoryRestoredSourceContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_restored_source_context SET legacy_recovery_version=$version WHERE state_id='current' AND restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPerformanceEvents2(ReignDbConnection connection, object part, object schema, object part2, object part3, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT p.*, " + part + " AS restored_content_hash,to_jsonb(saved)::text AS native_source_json"
                + " FROM character_performance_events p JOIN " + schema + ".world_history_events saved ON saved.event_id=p.event_id"
                + " JOIN world_history_events current_event ON current_event.event_id=saved.event_id AND " + part2 + "=" + part3
                + " WHERE p.timeline_id=$ancestor AND saved.timeline_id=$ancestor AND p.world_day<=$day AND saved.world_day<=$day"
                + " AND p.sequence<=$sequence AND saved.sequence<=$sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities(ReignDbConnection connection, object schema, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source.* FROM " + schema + ".world_history_entities source" + eligible + " ORDER BY source.event_id,source.ordinal;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryKnowledgeRules3(ReignDbConnection connection, object schema, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source.event_id,source.audience_type,source.audience_id,source.available_day,source.acquisition_mode FROM "
                + schema + ".world_history_knowledge_rules source" + eligible + ";", parameters, transaction);
        }

        internal static void InsertOrIgnoreMemoryRestoredSources2(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO memory_restored_sources(source_table,source_id,restore_generation,content_hash) VALUES " + part
                    + " ON CONFLICT(source_table,source_id,restore_generation) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns66(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
WHERE t.role='player' AND t.status='active' AND t.text ILIKE $cause AND t.text ~* 'memor|amnesia'
AND t.world_day<=$day AND t.accepted_sequence<=$sequence AND t.ts<=$timestamp
AND (t.speaker_id=$observer OR t.participants_json::jsonb ? $observer)
ORDER BY t.accepted_sequence DESC,t.ts DESC,t.turn_order DESC LIMIT 128;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns67(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT speaker_name FROM conversation_turns
WHERE session_id=$session AND role='npc' AND speaker_id=$observer AND status='active'
AND world_day<=$day AND accepted_sequence<=$sequence AND ts<=$timestamp
ORDER BY turn_order LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE campaign_id=$campaign AND status IN ('closed','interrupted') ORDER BY session_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic4(ReignDbConnection connection, object table, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM "+table+" ORDER BY "+key+";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns68(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$id AND status='active' ORDER BY turn_order FOR UPDATE;", parameters, transaction);
        }

        internal static void InsertOrIgnoreSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO summaries(summary_id,scope,owner_id,summary_type,summary,source_events_json,start_ts,end_ts,event_count,
location_id,participants_json,about_entities_json,known_by_json,hidden_from_json,visibility,importance,confidence,tags_json,status,source,vector_id,embedding_status,updated_ts,payload_json,memory_lane)
VALUES($id,$scope,$owner,'scene',$summary,$events,$start,$end,$count,$location,$known,'[]',$known,'[]','private',0.65,1.0,$tags,'active','precision_staging','','not_indexed',$ts,$payload,'interpersonal_history')
ON CONFLICT(summary_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdateSummaries3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE summaries SET status='superseded' WHERE summary_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns69(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND status='active' AND turn_order<$order ORDER BY turn_order DESC LIMIT 1;", parameters, transaction);
        }

        internal static string SelectEligibleSql(object eligible)
        {
            return @"WITH eligible AS MATERIALIZED (
 SELECT t.turn_id,length(t.text) AS characters,to_tsvector('english',t.text) AS document
 FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id WHERE " + eligible + @"
), query_groups AS (
 SELECT value AS query,ordinality AS group_index FROM jsonb_array_elements_text($queryGroups::jsonb) WITH ORDINALITY
), query_terms AS (
 SELECT DISTINCT group_index,unnest(tsvector_to_array(to_tsvector('english',query))) AS term FROM query_groups
), weights AS MATERIALIZED (
 SELECT q.group_index,plainto_tsquery('simple',q.term) AS term_query,
 ln(1.0+(SELECT count(*) FROM eligible)::double precision/(1.0+(SELECT count(*) FROM eligible e
      WHERE e.document @@ plainto_tsquery('simple',q.term)))) AS weight FROM query_terms q
), group_scores AS (
 SELECT e.turn_id,g.group_index,
 COALESCE((SELECT sum(w.weight) FROM weights w WHERE w.group_index=g.group_index AND e.document @@ w.term_query),0)
 /GREATEST(0.25,0.25+0.75*e.characters/NULLIF((SELECT avg(characters) FROM eligible),0)) AS score
 FROM eligible e CROSS JOIN query_groups g WHERE e.document @@ websearch_to_tsquery('english',g.query)
), ranked_groups AS (
 SELECT turn_id,group_index,score,row_number() OVER(PARTITION BY group_index ORDER BY score DESC,turn_id) AS query_rank FROM group_scores
), best_rank AS (
 SELECT DISTINCT ON(turn_id) turn_id,group_index,query_rank,score FROM ranked_groups ORDER BY turn_id,query_rank,group_index
)
SELECT t.*,COALESCE(5.0/(r.query_rank+1),0) AS relevance,r.group_index AS recall_query_group,r.query_rank AS recall_query_rank
FROM eligible e JOIN conversation_turns t ON t.turn_id=e.turn_id
LEFT JOIN best_rank r ON r.turn_id=t.turn_id
WHERE e.document @@ websearch_to_tsquery('english',$query)
ORDER BY r.query_rank NULLS LAST,r.group_index NULLS LAST,r.score DESC,t.ts DESC,t.turn_order DESC,t.turn_id";
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns70(ReignDbConnection connection, object part, object excludeCurrent, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE t.turn_id=$id AND " + part + @" AND t.status='active'" + excludeCurrent + " LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns71(ReignDbConnection connection, object part, object excludeCurrent, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE " + part + @" AND t.status='active'" + excludeCurrent + @"
ORDER BY ABS(t.world_day-$day),t.ts DESC LIMIT 12;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns72(ReignDbConnection connection, object part, object excludeCurrent, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id WHERE " + part + @" AND t.status='active'" + excludeCurrent + @"
ORDER BY t.ts DESC LIMIT 16;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions18(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id=$session LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns73(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND status='active'
 AND turn_order BETWEEN $start AND $end ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns74(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND exchange_id=$exchange AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE session_id=$id AND channel='npc_gossip' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns75(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT text FROM conversation_turns
WHERE session_id=$session AND status='active' AND role='player' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions20(ReignDbConnection connection, object part, object exclusion, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_sessions
WHERE status='closed' AND (npc_id=$npc OR " + part + ")" + exclusion + @"
ORDER BY end_ts DESC,start_ts DESC,session_id DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns76(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND status='active'
ORDER BY turn_order DESC,turn_id DESC;", parameters, transaction);
        }

        internal static string SelectConversationTurnFtsSql(object part, object excludeCurrent)
        {
            return @"SELECT t.*,bm25(conversation_turn_fts) AS lexical_rank FROM conversation_turn_fts f
JOIN conversation_turns t ON t.turn_id=f.turn_id JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE conversation_turn_fts MATCH $query AND " + part + @" AND t.status='active'" + excludeCurrent + @"
ORDER BY bm25(conversation_turn_fts),t.ts DESC LIMIT 8;";
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity11(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT original.record_id FROM conversation_continuity original WHERE original.record_id=$id AND "
                + part + ";", parameters, transaction);
        }

        internal static void DeleteMemoryRestoredSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_restored_sources; DELETE FROM memory_restored_source_context;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT to_regclass($name)::text AS name;", parameters, transaction);
        }

        internal static void UpsertMemoryRestoredSources(ReignDbConnection connection, object key, object value, object part, object prefix, object key2, object joinValue, object day, object part2, object inherited, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO memory_restored_sources(source_table,source_id,restore_generation,content_hash) SELECT '"
                    + key + "',saved." + value + ",$generation," + part + " FROM " + prefix + key2 + " saved" + joinValue
                    + " WHERE " + day + "<=$day AND (" + part2 + " IN ('',$timeline) OR " + inherited + ")"
                    + " ON CONFLICT(source_table,source_id,restore_generation) DO UPDATE SET content_hash=excluded.content_hash;", parameters, transaction);
        }

        internal static void UpsertMemoryRestoredSourceContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_restored_source_context
(state_id,restore_generation,timeline_id,save_point_id,world_day,head_sequence,head_event_id,source_generation,load_session_id)
VALUES('current',$generation,$timeline,$point,$day,$sequence,$head,$source,$session)
ON CONFLICT(state_id) DO UPDATE SET restore_generation=excluded.restore_generation,timeline_id=excluded.timeline_id,
save_point_id=excluded.save_point_id,world_day=excluded.world_day,head_sequence=excluded.head_sequence,head_event_id=excluded.head_event_id,
source_generation=excluded.source_generation,load_session_id=excluded.load_session_id;
DELETE FROM memory_restored_sources WHERE restore_generation<>$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic5(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM " + table + " WHERE state_id='current';", parameters, transaction);
        }

        internal static void UpdateMemoryRestoredSourceContext2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memory_restored_source_context SET load_session_id=$session,
source_generation=$source
WHERE state_id='current' AND save_point_id=$point
AND restore_generation=(SELECT restore_generation FROM memory_precision_state WHERE state_id='current');", parameters, transaction);
        }

        internal static void UpdateMemoryRestoredSourceContext3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memory_restored_source_context SET timeline_id=$recovered
WHERE state_id='current' AND restore_generation=(SELECT restore_generation FROM memory_precision_state WHERE state_id='current')
AND timeline_id=$requested AND head_sequence=$sequence AND head_event_id=$head;", parameters, transaction);
        }

        internal static void InsertOrIgnoreMemoryContinuityBranches(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_continuity_branches(timeline_id,owner_id,source_record_id,record_id)
SELECT $recovered,b.owner_id,b.source_record_id,b.record_id FROM memory_continuity_branches b
JOIN conversation_continuity r ON r.record_id=b.record_id AND r.owner_id=b.owner_id
WHERE b.timeline_id=$requested AND " + part
                + " ON CONFLICT(timeline_id,owner_id,source_record_id) DO NOTHING;", parameters, transaction);
        }

        internal static void DeleteMemoryProjections2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_projections; UPDATE memory_precision_state SET projection_generation=projection_generation+1,source_revision=source_revision+1 WHERE state_id='current';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredSourceContext3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT c.* FROM memory_restored_source_context c JOIN memory_precision_state s
ON c.restore_generation=s.restore_generation AND s.state_id='current' WHERE c.state_id='current' AND c.timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic6(ReignDbConnection connection, object key, object value, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source.* FROM " + key + " source WHERE source." + value
                + "=$id AND " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryContinuityBranches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT record_id FROM memory_continuity_branches WHERE timeline_id=$timeline AND owner_id=$owner AND source_record_id=$source;", parameters, transaction);
        }

        internal static void SelectAncestors(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"WITH RECURSIVE ancestors(id) AS (
SELECT $source::text UNION SELECT b.source_record_id FROM memory_continuity_branches b JOIN ancestors a ON b.record_id=a.id WHERE b.owner_id=$owner)
INSERT INTO memory_continuity_branches(timeline_id,owner_id,source_record_id,record_id)
SELECT $timeline,$owner,id,$id FROM ancestors WHERE id<>$id
ON CONFLICT(timeline_id,owner_id,source_record_id) DO UPDATE SET record_id=excluded.record_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns77(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpsertMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO memory_specialist_sources
(turn_id,source_hash,text_hash,schema_version,speaker_id,topics_json,entities_json,payload_json,updated_ts,public_topics_json,public_entities_json,source_revision)
VALUES($id,$hash,$textHash,$schema,$speaker,$topics,$entities,$payload,$ts,$publicTopics,$publicEntities,$revision)
ON CONFLICT(turn_id) DO UPDATE SET source_hash=$hash,text_hash=$textHash,schema_version=$schema,speaker_id=$speaker,
topics_json=$topics,entities_json=$entities,payload_json=$payload,updated_ts=$ts,public_topics_json=$publicTopics,public_entities_json=$publicEntities,source_revision=$revision;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memory_specialist_sources WHERE turn_id=$id;", parameters, transaction);
        }

        internal static void UpdateMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memory_specialist_sources i SET source_revision=t.xmin::text
FROM conversation_turns t WHERE t.turn_id=i.turn_id AND i.turn_id=$id AND i.source_hash=$hash
AND t.xmin::text=$revision;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns78(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,t.xmin::text AS source_revision FROM conversation_turns t
JOIN memory_specialist_sources i ON i.turn_id=t.turn_id WHERE t.status='active' AND i.schema_version=$schema
AND (COALESCE(i.payload_json::jsonb->>'extractorModelHash','')<>$model
OR COALESCE(i.payload_json::jsonb->>'extractorStatus','')<>'source_bound_candidates')
AND (COALESCE(i.payload_json::jsonb->>'extractorExpectedModelHash','')<>$model
OR (COALESCE((i.payload_json::jsonb->>'extractorAttempts')::int,0)<4
AND COALESCE((i.payload_json::jsonb->>'extractorNextAttemptTs')::bigint,0)<=$now))
ORDER BY i.updated_ts,t.accepted_sequence,t.turn_id LIMIT 4;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(world_day) AS day FROM world_history_events WHERE timeline_id=$timeline;", parameters, transaction);
        }
    }
}
