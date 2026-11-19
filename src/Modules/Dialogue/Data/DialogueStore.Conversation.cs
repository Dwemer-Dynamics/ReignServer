using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key;", parameters, transaction);
        }

        internal static void UpdateObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE obligations o SET timeline_id=o.payload_json::jsonb->>'timelineId'
WHERE o.timeline_id='main' AND COALESCE((to_jsonb(o)->>'revision')::bigint,0)=0
AND COALESCE(o.payload_json::jsonb->>'timelineId','') NOT IN ('','main')
AND o.payload_json::jsonb->>'sourceValidation'='supported_span'
AND o.payload_json::jsonb->'sourceEvidence'->>'status'='supported_span';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,'1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations(ReignDbConnection connection, object promptActiveObligationSql, object part, object part2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE " + promptActiveObligationSql + " AND "
                + part + " AND " + part2
                + " AND COALESCE(payload_json,'{}') NOT LIKE '%\"continuityQuarantined\":true%' ORDER BY created_day,obligation_id;", parameters, transaction);
        }

        internal static void SelectStatement6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SELECT pg_advisory_xact_lock(hashtextextended($key,0));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM obligations WHERE obligation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE status='active' AND speaker_id=$speaker AND (turn_id=$turn OR event_id=$event);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id=$session AND exchange_id=$exchange AND status='active' ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM (
SELECT t.*,s.player_id,s.payload_json AS session_payload,
ROW_NUMBER() OVER(PARTITION BY t.speaker_id,s.player_id ORDER BY t.world_day DESC,t.ts DESC,t.turn_order DESC) AS latest
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.status='active' AND t.role='npc' AND t.channel NOT IN ('letter','correspondence') AND t.speaker_id<>s.player_id AND s.player_id<>''
) latest_turns WHERE latest=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectObligations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM obligations WHERE owed_by=$speaker AND owed_to=$peer AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationAcceptedUnits2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_accepted_units WHERE unit_id=$unit;", parameters, transaction);
        }

        internal static void InsertConversationAcceptedUnits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_accepted_units(unit_id,response_hash,restore_generation,receipt_json,created_ts)
VALUES($unit,$hash,$generation,$receipt,$ts);", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,$hero,$player,'private_whisper','open',$ts,$ts,$day,$day,$location,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id AND session_id=$session FOR SHARE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationAcceptedUnits3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation,receipt_json FROM conversation_accepted_units WHERE unit_id=$unit FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateConversationAcceptedUnits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_accepted_units SET receipt_json=$receipt WHERE unit_id=$unit AND restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationNegotiations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_negotiations WHERE timeline_id=$timeline AND owner_id=$owner AND peer_id=$peer ORDER BY record_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT record_id FROM conversation_continuity WHERE timeline_id=$timeline AND owner_id=$owner AND peer_id=$peer AND kind='relationship' AND status<>'invalidated';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_id FROM conversation_relationship_receipts WHERE timeline_id=$timeline AND observer_id=$owner AND target_id=$peer AND status='applied' AND valence='positive' AND severity_tier<>'routine';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM memories WHERE owner_id=$owner AND world_day<=$day AND status='active' ORDER BY ts DESC LIMIT 32;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationNegotiationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_negotiation_turns WHERE receipt_id=$id;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static void InsertConversationNegotiations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_negotiations(record_id,timeline_id,owner_id,peer_id,topic,object_id,revision,payload_json) VALUES($id,$timeline,$owner,$peer,$topic,$object,$revision,$payload);", parameters, transaction);
        }

        internal static void UpdateConversationNegotiations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_negotiations SET revision=$revision,payload_json=$payload WHERE record_id=$id AND revision=$expected;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationNegotiations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM conversation_negotiations WHERE record_id=$id;", parameters, transaction);
        }

        internal static void InsertConversationNegotiationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_negotiation_turns(receipt_id,timeline_id,owner_id,peer_id,turn_id,payload_json) VALUES($id,$timeline,$owner,$peer,$turn,$payload);", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneHandoffs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_scene_handoffs WHERE handoff_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneHandoffs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT handoff_id FROM conversation_scene_handoffs WHERE owner_id=$owner LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteConversationSceneState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_scene_state WHERE hero_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns
WHERE session_id=$session AND (exchange_id=$turn OR event_id=$turn OR turn_id=$turn)
AND status='active' ORDER BY turn_order LIMIT 16;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_scene_state WHERE hero_id=$id;", parameters, transaction);
        }

        internal static void UpsertConversationSceneHandoffs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO conversation_scene_handoffs
(handoff_id,timeline_id,owner_id,anchor,source_turn_id,world_day,payload_json) VALUES($id,$timeline,$owner,$anchor,$turn,$day,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity2(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT record_id FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND " + part + " AND kind='agenda';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpsertConversationContinuity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO conversation_continuity
(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES($id,$timeline,$owner,$peer,$kind,$status,$importance,$revision,$event,$turn,$ts,$payload)
ON CONFLICT(record_id) DO UPDATE SET status=excluded.status,importance=excluded.importance,
revision=excluded.revision,source_event_id=excluded.source_event_id,source_turn_id=excluded.source_turn_id,
updated_ts=excluded.updated_ts,payload_json=excluded.payload_json
WHERE conversation_continuity.revision=$expected AND conversation_continuity.owner_id=$owner
AND conversation_continuity.timeline_id=$timeline RETURNING record_id;", parameters, transaction);
        }

        internal static void InsertConversationContinuityVersions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts)
VALUES($id,$record,$event,$revision,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT turn.* FROM conversation_turns turn
JOIN jsonb_array_elements_text(CAST($ids AS jsonb)) requested ON requested.value=turn.turn_id
WHERE turn.speaker_id=$owner AND turn.role='npc' AND turn.status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity4(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_continuity WHERE owner_id=$owner AND " + part + @"
AND status<>'invalidated' AND kind<>'question' AND (kind<>'agenda' OR status NOT IN ('resolved','abandoned')) AND (kind<>'relationship' OR peer_id=$peer)
ORDER BY CASE WHEN kind='relationship' THEN 0 WHEN kind='agenda' THEN 1 ELSE 2 END,importance DESC,updated_ts DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSocialSignalEvidence(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND " + part + @" AND accepted=1 AND signal_type='sexual_intimacy_completed'
AND ((speaker_id=$observer AND target_id=$peer) OR (speaker_id=$peer AND target_id=$observer))
ORDER BY created_ts ASC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipMilestones(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM relationship_milestones
WHERE subject_id=$observer AND target_id=$peer AND status='active' ORDER BY stability DESC,updated_ts DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts2(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts
WHERE campaign_id=$campaign AND " + part + @" AND observer_id=$observer AND target_id=$peer
AND status='applied' AND severity_tier<>'routine' ORDER BY created_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories2(ReignDbConnection connection, object visibleTurn, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT m.*,t.turn_id AS accepted_turn_id,t.world_day AS accepted_world_day,
t.session_id AS accepted_session_id FROM memories m JOIN conversation_turns t ON t.event_id=m.event_id
JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE m.owner_id=$observer AND m.status IN ('active','consolidated') AND t.speaker_id=$observer
AND t.role='npc' AND s.player_id=$peer AND " + visibleTurn + @"
AND m.ts<=$asOf AND m.world_day<=$day AND " + part + @"
AND (lower(m.tags_json) LIKE '%intima%' OR lower(m.tags_json) LIKE '%milestone%'
 OR lower(m.tags_json) LIKE '%vulnerability%' OR lower(m.tags_json) LIKE '%trust%'
 OR lower(m.tags_json) LIKE '%betray%' OR lower(m.tags_json) LIKE '%boundary%') ORDER BY t.ts,m.ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns17(ReignDbConnection connection, object visibleTurn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE t.speaker_id=$observer AND t.role='npc' AND s.player_id=$peer AND " + visibleTurn + @"
AND (lower(t.text) LIKE '%kiss%' OR lower(t.text) LIKE '%intima%' OR lower(t.text) LIKE '%slept together%'
 OR lower(t.text) LIKE '%our night%' OR lower(t.text) LIKE '%trust you%' OR lower(t.text) LIKE '%betrayed me%'
 OR lower(t.text) LIKE '%our partnership%' OR lower(t.text) LIKE '%shared a bed%') ORDER BY t.ts,t.turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns18(ReignDbConnection connection, object visibleTurn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT t.* FROM conversation_turns t WHERE t.session_id=$session AND t.exchange_id=$exchange AND "
                        + visibleTurn + " ORDER BY t.turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuityRepairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT receipt_json FROM conversation_continuity_repairs WHERE manifest_hash=$hash;", parameters, transaction);
        }

        internal static void UpdateMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE memory_precision_state SET mode=$mode,projection_generation=projection_generation+1,updated_ts=$ts WHERE state_id='current';", parameters, transaction);
        }

        internal static void DeleteMemoryProjections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM memory_projections;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic2(ReignDbConnection connection, object table, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM " + table + " WHERE " + key + "=$id;", parameters, transaction);
        }

        internal static void UpdateTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE temporal_knowledge_assertions SET known_by_json=$known,visibility='private',truth_status=$truth WHERE assertion_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns19(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE speaker_id=$owner AND role='npc' AND status='active' ORDER BY ts DESC,turn_order DESC,turn_id DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns20(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id AND status='active';", parameters, transaction);
        }

        internal static void UpdateDynamic(ReignDbConnection connection, object table, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE " + table + " SET payload_json=$payload WHERE " + key + "=$id;", parameters, transaction);
        }

        internal static void InsertConversationContinuityRepairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO conversation_continuity_repairs(manifest_hash,campaign_id,timeline_id,receipt_json,created_ts) VALUES($hash,$campaign,$timeline,$receipt,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCampaignRegistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT schema_name FROM reign_meta.campaign_registry WHERE campaign_id=$campaign;", parameters, transaction);
        }

        internal static void Set2(ReignDbConnection connection, object schema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET LOCAL search_path TO \"" + schema + "\",reign_meta;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM world_history_timelines WHERE campaign_id=$campaign AND is_active=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity5(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_continuity
WHERE owner_id=$owner AND " + part + @" AND status<>'invalidated'
AND (peer_id='' OR peer_id=$peer);", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationContinuityPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_continuity_pending
(pending_id,record_id,timeline_id,owner_id,peer_id,source_event_id,source_turn_id,source_hash,world_day,status,payload_json,created_ts)
VALUES($id,$record,$timeline,$owner,$peer,$event,$turn,$hash,$day,'pending',$payload,$ts)
ON CONFLICT(pending_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuityPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT p.* FROM conversation_continuity_pending p
LEFT JOIN conversation_turns t ON t.turn_id=p.source_turn_id
WHERE p.timeline_id=$timeline AND p.owner_id=$owner AND p.peer_id=$peer AND p.status='pending' AND p.world_day<=$day
AND p.created_ts<=$asof AND COALESCE(t.accepted_sequence,0)<=$sequence
ORDER BY p.created_ts,p.pending_id LIMIT 17;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpdateConversationContinuityPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_continuity_pending SET status='superseded_after_reread',resolved_event_id=$event
WHERE pending_id=$id AND record_id=$record AND timeline_id=$timeline AND status='pending' AND source_event_id<>$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns21(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT turn_id
FROM conversation_turns WHERE event_id=$event AND speaker_id=$owner AND text=$text AND status='active'
ORDER BY turn_order DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id AND role='npc' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns23(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns24(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE turn_id=$id AND role='player';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT projection_json FROM conversation_input_receipts WHERE input_id=$id AND restore_generation=$generation;", parameters, transaction);
        }

        internal static void UpdateConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_input_receipts SET projection_json=$json,updated_ts=$now
WHERE input_id=$id AND restore_generation=$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns25(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$event AND status='active' ORDER BY accepted_sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns26(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.*,s.npc_id,s.player_id,s.payload_json AS session_payload
FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE s.player_id=$player AND ($session='' OR t.session_id=$session)
AND ($npc='' OR s.npc_id=$npc OR (t.channel='party_announcement' AND t.participants_json::jsonb @> jsonb_build_array($npc)) OR EXISTS(SELECT 1 FROM conversation_turns n
 WHERE n.session_id=t.session_id AND n.speaker_id=$npc AND n.role='npc'))
AND t.status IN ('active','source_only') ORDER BY t.ts DESC,t.session_id DESC,t.turn_order DESC,t.turn_id DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns27(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='source_only' AND speaker_id=$player AND turn_id IN ("
                    + part + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns28(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='source_only' AND speaker_id=$player"
                    + " AND payload_json::jsonb->'originalRecovery'->>'acceptedTurnId' IN (" + part + ");", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns29(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE event_id=$event AND speaker_id=$speaker
AND text=$text AND status='active' ORDER BY turn_order DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns30(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id AND speaker_id=$speaker AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity7(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND kind='question' AND "
                + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity8(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND "
                    + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationContinuity9(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT record_id FROM conversation_continuity WHERE record_id=$id AND owner_id=$owner AND " + part + " AND kind='question';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectStatement7(ReignDbConnection connection, object eligible, object eligible2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM (" + eligible + " ORDER BY accepted_sequence,ts,turn_id LIMIT 1) first_turn"
                + " UNION ALL SELECT * FROM (" + eligible2 + " ORDER BY accepted_sequence DESC,ts DESC,turn_id LIMIT 3) recent_turns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Statement(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, eligible + " ORDER BY updated_ts DESC,record_id LIMIT 4;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> Statement2(ReignDbConnection connection, object eligible, object score, object score2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, eligible + " AND " + score + ">0 ORDER BY " + score2
                    + " DESC,updated_ts DESC,record_id LIMIT 8;", parameters, transaction);
        }

        internal static string SelectConversationTurnsSql(object part, object part2)
        {
            return "SELECT * FROM conversation_turns WHERE status='active' AND session_id=$session"
                + " AND speaker_id IN (" + part + ") AND accepted_sequence>$after AND accepted_sequence<=$through"
                + " AND world_day<=$day AND ts<=$timestamp AND " + part2
                + " AND (speaker_id=$owner OR participants_json::jsonb @> jsonb_build_array($owner))";
        }

        internal static string SelectConversationContinuitySql(object part)
        {
            return @"SELECT * FROM conversation_continuity WHERE owner_id=$owner AND " + part + @"
AND kind='question' AND status<>'invalidated' AND (peer_id='' OR peer_id=$peer)
AND COALESCE((payload_json::jsonb->>'worldDay')::double precision,0)<=$day AND updated_ts<=$timestamp";
        }

        internal static void InsertOrIgnoreConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_reference_edges
(edge_id,owner_id,timeline_id,restore_generation,from_type,from_id,from_hash,to_type,to_id,to_hash,relation,
source_event_id,as_of_sequence,world_day,created_ts,payload_json)
VALUES($id,$owner,$timeline,$generation,'turn',$from,$fromHash,$type,$to,$toHash,$relation,$event,$sequence,$day,$ts,$payload)
ON CONFLICT(edge_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges(ReignDbConnection connection, object end, object end2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_reference_edges
WHERE owner_id=$owner AND timeline_id=$timeline AND restore_generation=$generation
AND as_of_sequence<=$sequence AND world_day<=$day AND " + end + @"_type=$type AND " + end2 + @"_id=$id
ORDER BY as_of_sequence DESC,edge_id LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationReferenceEdges2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM conversation_reference_edges e
JOIN memory_restored_sources m ON m.source_table='conversation_turns' AND m.source_id=e.from_id AND m.restore_generation=$generation
WHERE e.restore_generation<>$generation AND e.from_type='turn' AND e.world_day<=$day ORDER BY e.edge_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryRestoredLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT letter_id FROM memory_restored_letters WHERE letter_id=$id AND restore_generation=$generation AND content_hash=$hash;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationReferenceEdges2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_reference_edges
(edge_id,owner_id,timeline_id,restore_generation,from_type,from_id,from_hash,to_type,to_id,to_hash,relation,
source_event_id,as_of_sequence,world_day,created_ts,payload_json)
VALUES($id,$owner,$timeline,$generation,$fromType,$from,$fromHash,$toType,$to,$toHash,$relation,$event,$sequence,$day,$ts,$payload)
ON CONFLICT(edge_id) DO NOTHING;", parameters, transaction);
        }

        internal static void DeleteConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_reference_edges WHERE restore_generation<>$generation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationSceneState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM conversation_scene_state WHERE expires_world_hour<=$hour;", parameters, transaction);
        }

        internal static void DeleteConversationSceneState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_scene_state WHERE expires_world_hour<=$hour;", parameters, transaction);
        }

        internal static void DeleteConversationSceneProposals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM conversation_scene_proposals WHERE world_hour<$hour;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_scene_state WHERE hero_id=$id AND expires_world_hour>$hour LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneState3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT revision,source_turn_id FROM conversation_scene_state WHERE hero_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateConversationSceneProposals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_scene_proposals SET status=$status,updated_ts=$ts WHERE proposal_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSceneState4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_scene_state WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertConversationSceneState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO conversation_scene_state
(hero_id,location_override,location_class,clothing_override,source_turn_id,source_mode,world_hour,expires_world_hour,revision,updated_ts)
VALUES($id,$location,$class,$clothing,$turn,$mode,$hour,$expires,$revision,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id=$id AND timeline_id=$timeline AND world_day<=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT contribution_id FROM group_conversation_contributions WHERE session_id=$session AND timeline_id=$timeline AND scene_turn_id=$beat LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns31(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE session_id=$session AND exchange_id=$beat AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertGroupConversationState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO group_conversation_state
                (session_id,campaign_id,mode,participant_ids_json,state_json,revision,updated_ts,timeline_id,world_day)
                VALUES($session,$campaign,$mode,$participants,$state,1,$ts,$timeline,$day)
                ON CONFLICT(session_id) DO UPDATE SET state_json=$state,participant_ids_json=$participants,
                revision=group_conversation_state.revision+1,updated_ts=$ts,timeline_id=$timeline,world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM group_conversation_state WHERE session_id=$id AND timeline_id=$timeline AND world_day<=$day LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions2(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM group_conversation_contributions
WHERE session_id=$id AND scene_turn_id=$beat AND role='npc' AND text<>'' AND speaker_id<>$observer
AND timeline_id=$timeline AND world_day<=$day
AND " + part + " ORDER BY delivery_order,ts,contribution_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns32(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE event_id=$event AND session_id=$session
AND exchange_id=$beat AND speaker_id=$player AND role='player' AND text=$text AND status='active' ORDER BY turn_order LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationContributions3(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT contribution_id,speaker_id,delivery_order FROM group_conversation_contributions
WHERE session_id=$session AND scene_turn_id=$beat AND role='npc' AND text<>''
AND validation_status IN ('accepted_speech','visible_failed_repair')
AND timeline_id=$timeline AND world_day<=$day AND " + part
                + " ORDER BY delivery_order,ts,contribution_id LIMIT 65;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> InsertOrIgnoreGroupConversationContributions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO group_conversation_contributions
(contribution_id,session_id,speaker_id,speaker_name,role,text,addressed_ids_json,ts,scene_turn_id,audience_json,validation_status,delivery_order,timeline_id,world_day,source_event_id)
VALUES($id,$session,$speaker,$name,'npc',$text,$addressed,$ts,$beat,$audience,$validation,
(SELECT COALESCE(MAX(delivery_order),0)+1 FROM group_conversation_contributions WHERE session_id=$session),$timeline,$day,$event)
ON CONFLICT(contribution_id) DO NOTHING RETURNING contribution_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGroupConversationState3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT state_json FROM group_conversation_state WHERE session_id=$id AND timeline_id=$timeline AND world_day<=$day;", parameters, transaction);
        }

        internal static void InsertOrIgnoreGroupConversationContributions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO group_conversation_contributions
(contribution_id,session_id,speaker_id,speaker_name,role,text,addressed_ids_json,ts,scene_turn_id,audience_json,validation_status,timeline_id,world_day)
VALUES($id,$session,$speaker,$name,'player',$text,'[]',$ts,$beat,$audience,'accepted_speech',$timeline,$day);", parameters, transaction);
        }

        internal static void UpsertGroupConversationState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO group_conversation_state
(session_id,campaign_id,mode,participant_ids_json,last_speaker_id,last_speaker_name,last_text,open_questions_json,topics_json,state_json,revision,updated_ts,timeline_id,world_day)
VALUES($session,$campaign,$mode,$participants,$speaker,$name,$text,$questions,$topics,$state,1,$ts,$timeline,$day)
ON CONFLICT(session_id) DO UPDATE SET participant_ids_json=$participants,last_speaker_id=$speaker,last_speaker_name=$name,
last_text=$text,open_questions_json=$questions,topics_json=$topics,state_json=$state,revision=group_conversation_state.revision+1,updated_ts=$ts,timeline_id=$timeline,world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE payload_json::jsonb->>'continuedFromSessionId'=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT session_id FROM conversation_sessions WHERE payload_json::jsonb->>'logicalConversationId'=$logical LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns33(ReignDbConnection connection, object eligible, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_turns WHERE " + eligible
                + " ORDER BY accepted_sequence DESC,ts DESC,turn_order DESC,turn_id DESC LIMIT $limit;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationMotiveCooldowns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO conversation_motive_cooldowns(cooldown_id,subject_id,target_id,kind,session_id,world_day,created_ts) VALUES($id,$subject,$target,'romantic_advance',$session,$day,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationMotiveCooldowns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_motive_cooldowns WHERE subject_id=$subject AND target_id=$target AND kind='romantic_advance' AND session_id=$session LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationMotiveCooldowns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_motive_cooldowns WHERE subject_id=$subject AND target_id=$target AND kind='romantic_advance' ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns34(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text,status,speaker_id FROM conversation_turns WHERE turn_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns35(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(MIN(turn_order),0)-1 AS next_order FROM conversation_turns WHERE session_id=$session AND turn_order<0;", parameters, transaction);
        }

        internal static void InsertConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,location_id,participants_json,status,payload_json)
VALUES($id,$session,$event,$order,$exchange,'player_source',$player,$name,$text,'player_input_source',$day,$ts,$location,$participants,'source_only',$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_events WHERE is_complete=1 AND (event_type IN ('battle_completed','tournament_finished') OR (event_type='social_outcome' AND source='reign_social' AND payload_json LIKE '%\"duelMode\"%'));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT audience_type,audience_id,available_day,acquisition_mode FROM world_history_knowledge_rules WHERE event_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCharacterPerformanceEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO character_performance_events
(receipt_id,event_id,timeline_id,subject_id,world_day,sequence,kind,outcome,battle_type,competition_id,source,source_hash,knowledge_rules_json)
VALUES($receipt_id,$event_id,$timeline_id,$subject_id,$world_day,$sequence,$kind,$outcome,$battle_type,$competition_id,$source,$source_hash,$knowledge_rules_json);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPerformanceEvents(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_performance_events WHERE " + part
                    + " AND subject_id=$player AND world_day<=$day ORDER BY world_day,sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_id FROM identity_roster WHERE is_player=1 ORDER BY hero_id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns36(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
turn_id,session_id,turn_order,role,speaker_id,speaker_name AS speaker,text,ts
FROM conversation_turns
WHERE status='active' AND role='npc' AND speaker_id=$hero
ORDER BY ts DESC,turn_order DESC LIMIT $limit;", parameters, transaction);
        }
    }
}
