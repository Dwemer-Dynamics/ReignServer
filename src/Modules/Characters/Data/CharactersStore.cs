using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Characters module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class CharactersStore
    {
        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,COUNT(*) count FROM character_editor_native_commands
WHERE status IN ('pending','claimed','reconciling','failed','projection_conflict') GROUP BY hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_editor_heads WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO character_editor_heads(hero_id,revision_token,latest_revision_id,updated_ts) VALUES($id,$token,'',$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM knowledge_receipts WHERE npc_id=$id ORDER BY acquired_day DESC,created_ts DESC LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_sessions WHERE npc_id=$id OR player_id=$id ORDER BY start_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.* FROM conversation_turns t JOIN conversation_sessions s ON s.session_id=t.session_id
WHERE (s.npc_id=$id OR s.player_id=$id) AND t.status='active' ORDER BY t.ts DESC,t.turn_order DESC LIMIT 2000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT ms.* FROM memory_sources ms WHERE
(ms.document_type='summary' AND ms.document_id IN (SELECT summary_id FROM summaries WHERE owner_id=$id))
OR (ms.source_type='session' AND ms.source_id IN (SELECT session_id FROM conversation_sessions WHERE npc_id=$id OR player_id=$id))
ORDER BY ms.document_type,ms.document_id,ms.ordinal LIMIT 2000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM dynamic_characteristics WHERE owner_id=$id
ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'rejected' THEN 1 ELSE 2 END,importance DESC,last_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_incidents WHERE hero_a_id=$id OR hero_b_id=$id ORDER BY world_day DESC,created_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE observer_id=$id OR target_id=$id ORDER BY created_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_benefits WHERE giver_id=$id OR recipient_id=$id ORDER BY updated_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM acquaintances WHERE observer_id=$id OR subject_id=$id ORDER BY updated_ts DESC LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_evidence WHERE observer_id=$id OR subject_id=$id ORDER BY created_ts DESC LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT rst.*,ro.archetype_id,ro.world_day,ro.expires_day,ro.provenance_summary
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE rst.subject_id=$id ORDER BY ro.world_day DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_reputations WHERE subject_id=$id ORDER BY acquired_day DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$id OR recipient_id=$id ORDER BY updated_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpdateEditorLetterStatus(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE letters SET status=$value
WHERE letter_id=$id AND (sender_id=$hero OR recipient_id=$hero) AND status=$expected RETURNING status;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpdateEditorSummaryText(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE summaries SET summary=$value
WHERE summary_id=$id AND owner_id=$hero AND status='active' AND summary COLLATE ""C""=$expected RETURNING summary;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conceptions WHERE mother_id=$id OR biological_father_id=$id OR legal_father_id=$id ORDER BY updated_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectParentage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM parentage WHERE child_id=$id OR mother_id=$id OR biological_father_id=$id OR legal_father_id=$id ORDER BY updated_ts DESC LIMIT 500;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT revision_id,hero_id,section,description,native_changes_json,reconciliation_json,rollback_of,created_ts FROM character_editor_revisions WHERE hero_id=$id ORDER BY created_ts DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_editor_native_commands WHERE hero_id=$id ORDER BY created_ts DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM relationship_pair_chemistry
WHERE hero_a_id=$id OR hero_b_id=$id ORDER BY last_day DESC,updated_ts DESC LIMIT 1000;", parameters, transaction);
        }

        internal static void InsertCharacterEditorRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_editor_revisions(revision_id,hero_id,section,description,before_json,after_json,native_changes_json,reconciliation_json,created_ts) VALUES($revision,$hero,$section,$description,$before,$after,$native,$reconcile,$ts);", parameters, transaction);
        }

        internal static void InsertCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_editor_native_commands(command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts) VALUES($id,$revision,$hero,'pending',$changes,$inverse,$dangerous,$confirmation,$ts);", parameters, transaction);
        }

        internal static void UpsertCharacterEditorHeads2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO character_editor_heads(hero_id,revision_token,latest_revision_id,updated_ts) VALUES($hero,$token,$revision,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorRevisions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_editor_revisions WHERE revision_id=$revision AND hero_id=$hero LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT inverse_json FROM character_editor_native_commands WHERE revision_id=$id ORDER BY created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static void InsertCharacterEditorNativeCommands2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_editor_native_commands(command_id,revision_id,hero_id,status,changes_json,inverse_json,dangerous,confirmation,created_ts) VALUES($id,$revision,$hero,'pending',$changes,'{}',1,'rollback',$ts);", parameters, transaction);
        }

        internal static void InsertCharacterEditorRevisions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_editor_revisions(revision_id,hero_id,section,description,before_json,after_json,native_changes_json,reconciliation_json,rollback_of,created_ts) VALUES($id,$hero,'rollback',$description,$before,$after,$native,$reconcile,$rollback,$ts);", parameters, transaction);
        }

        internal static void UpdateCharacterEditorRevisionReconciliation(ReignDbConnection connection, Dictionary<string, object> parameters,
            ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_revisions SET reconciliation_json=$reconcile WHERE revision_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> ClaimCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"WITH next AS (
SELECT c.command_id FROM character_editor_native_commands c
WHERE (c.status='pending' OR (c.status='claimed' AND c.claimed_ts<$stale))
AND NOT EXISTS (SELECT 1 FROM character_editor_native_commands earlier
    WHERE earlier.hero_id=c.hero_id AND earlier.status IN ('pending','claimed','reconciling')
    AND (earlier.status='reconciling' OR (earlier.created_ts,earlier.command_id)<(c.created_ts,c.command_id)))
ORDER BY c.created_ts,c.command_id LIMIT 25 FOR UPDATE OF c SKIP LOCKED)
UPDATE character_editor_native_commands c SET status='claimed',claimed_ts=$ts,result_json=$claim
FROM next WHERE c.command_id=next.command_id RETURNING c.*;", parameters);
        }

        internal static List<Dictionary<string, object>> ReadCharacterEditorNativeReconciliations(ReignDbConnection connection)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM character_editor_native_commands
WHERE status='reconciling' ORDER BY created_ts,command_id;");
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorNativeCommands5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_editor_native_commands WHERE command_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static bool TransitionCharacterEditorNativeCommand(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE character_editor_native_commands SET
status=$status,resolved_ts=$ts,result_json=$result WHERE command_id=$id AND status=$previous AND result_json=$basis
RETURNING command_id;", parameters).Count == 1;
        }

        internal static void UpdateNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE notable_mbti_profiles SET
native_sync_status='synchronized',native_action_id='',
last_native_observed_json=$observed,updated_ts=$ts
WHERE hero_id=$hero AND native_action_id=$action
AND native_sync_status IN ('pending','claimed','awaiting_confirmation');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEditorHistorySources(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            // An accepted group event can own both a public placeholder and a private
            // turn for the same role and speaker. Event-scoped memories cannot prove
            // which one they project when those canonical rows coexist.
            return ReignDatabase.QuerySql(connection, @"SELECT t.*, owner_turn.payload_json AS owner_payload_json, e.payload_json AS event_payload_json,
e.participants_json AS event_participants_json, e.witnesses_json AS event_witnesses_json,
e.hidden_from_json AS event_hidden_from_json
FROM conversation_turns t
JOIN conversation_sessions s ON s.session_id=t.session_id
JOIN events e ON e.event_id=t.event_id AND e.campaign_id=s.campaign_id
JOIN conversation_turns owner_turn ON owner_turn.event_id=t.event_id AND owner_turn.session_id=t.session_id
AND owner_turn.exchange_id=t.exchange_id AND owner_turn.role='npc' AND owner_turn.speaker_id=$hero AND owner_turn.status='active'
WHERE s.campaign_id=$campaign AND s.session_id=$session AND s.legacy_inferred=0
AND t.exchange_id=$exchange AND t.role=$role AND t.status='active'
AND ($turn='' OR t.turn_id=$turn)
AND e.visibility='private' AND e.event_type IN ('dialogue_turn','party_chat_turn','social_event_turn')
AND NOT EXISTS (SELECT 1 FROM conversation_turns sibling
    WHERE sibling.event_id=t.event_id AND sibling.role=t.role AND sibling.speaker_id=t.speaker_id
    AND sibling.turn_id<>t.turn_id)
LIMIT 2;", parameters);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object table, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM "+table+" WHERE "+key+"=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateDynamic(ReignDbConnection connection, object table, object part, object key, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE "+table+" SET "+part+" WHERE "+key+"=$id;", parameters, transaction);
        }

        internal static void InsertCharacterEditorReconciliation(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO character_editor_reconciliation(job_id,revision_id,hero_id,section,status,summary,created_ts,updated_ts) VALUES($job,$revision,$hero,$section,'running','Rebuilding character projections.',$ts,$ts);", parameters, transaction);
        }

        internal static void DeleteSummaryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM summary_fts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSummaries(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM summaries WHERE status='active';", parameters, transaction);
        }

        internal static void UpdateCharacterEditorReconciliation(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_reconciliation SET status='completed',summary='Search and prompt projections rebuilt.',updated_ts=$ts WHERE job_id=$job;", parameters, transaction);
        }

        internal static void UpdateCharacterEditorReconciliation2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_reconciliation SET status='failed',summary=$summary,result_json=$result,updated_ts=$ts WHERE job_id=$job;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEditorHistoryMemories(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM memories
WHERE event_id=$event AND visibility='private' AND status IN ('active','superseded');", parameters);
        }

        internal static void SupersedeEditorHistoryMemory(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE memories SET status='superseded'
WHERE memory_id=$id AND event_id=$event AND status='active';", parameters);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,base_chance_b_to_a,
chance_a_to_b,chance_b_to_a,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
shared_tag,projected_native_relation,first_day,last_day,last_context_kind,last_context_id,updated_ts)
VALUES($pair,'editor_a','editor_b','ENTJ','ISFJ',72,63,62,53,74,41,'devoted','friend',
'close_friends',58,10,10,'party','editor_party',$ts);", parameters, transaction);
        }

        internal static void InsertRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,lover_started_day,last_processed_day,updated_ts)
VALUES($pair,'editor_a','editor_b',1,10,10,$ts);", parameters, transaction);
        }

        internal static void UpsertDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO dynamic_characteristics
(characteristic_id,owner_id,category,topic_key,text,normalized_key,status,confidence,importance,provenance,
compatibility_status,source_event_id,world_day,known_by_json,first_ts,last_ts,payload_json)
VALUES($id,$owner,'life_transition',$topic,$text,$id,'active',1,1,'source_backed_life_transition',
'verified_source_projection',$source,$day,$known,$ts,$ts,$payload)
ON CONFLICT(owner_id,normalized_key) DO UPDATE SET text=$text,payload_json=$payload,last_ts=$ts
WHERE dynamic_characteristics.payload_json<>excluded.payload_json;", parameters, transaction, skipSaveSyncDirtyIfNoRowsAffected: true);
        }

        internal static List<Dictionary<string, object>> SelectConversationNegotiationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT n.*,d.value AS decision_json FROM conversation_negotiation_turns n
CROSS JOIN LATERAL jsonb_array_elements(COALESCE(n.payload_json::jsonb->'updates','[]'::jsonb)) d(value)
WHERE n.owner_id=$owner
ORDER BY n.receipt_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_turns WHERE speaker_id=$owner
AND (turn_id=$turn OR turn_id=$npcTurn OR event_id=$turn OR exchange_id=$turn) ORDER BY turn_order;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectLetters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM letters WHERE sender_id=$owner AND (letter_id=$turn OR 'turn_letter_'||letter_id=$turn);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_events e WHERE e.timeline_id=$timeline
AND e.world_day<=$day AND e.is_complete=1 AND e.phase='completed' AND e.source IN ('native','bannerlord_native')
AND e.event_type ~* '(marri|divorc|birth|concep|death|killed|executed|prisoner|captur|releas|rescu|escape|wounded|clan_join|clan_leave|changed_clan|changed_kingdom|kingdom_join|kingdom_leave|kingdom_created|ruling_clan|ruler|succession|fief|settlement_owner|parentage|affair|lover|betray)'
AND EXISTS(SELECT 1 FROM world_history_entities p WHERE p.event_id=e.event_id AND (p.entity_id=$owner
OR (e.event_type ~* '(death|killed|executed|birth|prisoner|captur|rescu)' AND p.entity_id IN (SELECT jsonb_array_elements_text($family::jsonb))))
AND p.entity_type IN ('hero','character') AND p.role<>'' AND p.role NOT IN ('observer','witness','spectator','informed'))
AND EXISTS(SELECT 1 FROM world_history_knowledge_rules k WHERE k.event_id=e.event_id AND k.available_day<=$day
AND ((k.audience_type='entity' AND k.audience_id=$owner) OR k.audience_type='global'
OR (k.audience_type='kingdom' AND k.audience_id=$kingdom))) ORDER BY e.world_day,e.sequence;", parameters, transaction);
        }

        internal static void UpdateDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE dynamic_characteristics SET status='source_unavailable'
WHERE owner_id=$owner AND category='life_transition' AND status IS DISTINCT FROM 'source_unavailable'
AND characteristic_id NOT IN (SELECT jsonb_array_elements_text(CAST($observed AS jsonb)));", parameters, transaction, skipSaveSyncDirtyIfNoRowsAffected: true);
        }

        internal static void UpdateDynamicCharacteristics2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE dynamic_characteristics SET status=$status WHERE characteristic_id=$id AND status IS DISTINCT FROM $status;", parameters, transaction, skipSaveSyncDirtyIfNoRowsAffected: true);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero AND category='narrative_development' ORDER BY first_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.* FROM world_history_events e
WHERE e.event_id=$event AND e.campaign_id=$campaign AND e.source='native'
AND e.is_complete=1 AND e.phase='completed' AND e.world_day<=$day
AND e.timeline_id=(SELECT timeline_id FROM world_history_timelines WHERE is_active=1 AND campaign_id=$campaign ORDER BY updated_utc DESC LIMIT 1) LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT entity_id FROM world_history_entities WHERE event_id=$event;", parameters, transaction);
        }

        internal static void InsertOrIgnoreDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO dynamic_characteristics
(characteristic_id,owner_id,category,topic_key,text,normalized_key,status,confidence,importance,provenance,compatibility_status,rejection_reason,
source_event_id,source_session_id,source_exchange_id,world_day,first_ts,last_ts,payload_json)
VALUES($id,$hero,'narrative_development',$topic,$text,$id,$state,1,1,'evidence_gated_narrative_development',$decision,$reason,$event,$session,$exchange,$day,$ts,$ts,$payload)
ON CONFLICT(owner_id,normalized_key) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdateDynamicCharacteristics3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE dynamic_characteristics SET status='superseded' WHERE owner_id=$hero AND category='narrative_development' AND topic_key=$topic AND characteristic_id<>$id AND status='active';", parameters, transaction);
        }

        internal static void UpdateCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE character_editor_heads SET revision_token=$token,updated_ts=$ts WHERE hero_id=$hero;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$hero AND category='narrative_development' AND status='active';", parameters, transaction);
        }

        internal static void UpdateDynamicCharacteristics4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE dynamic_characteristics SET status='superseded',rejection_reason='superseded_by_character_editor' WHERE characteristic_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterEditorRevisions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT DISTINCT hero_id FROM character_editor_revisions;", parameters, transaction);
        }

        internal static void InsertReportedDynamicCharacteristic(ReignDbConnection connection, Dictionary<string, object> parameters)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO dynamic_characteristics(
characteristic_id,owner_id,category,topic_key,text,normalized_key,status,confidence,importance,provenance,compatibility_status,rejection_reason,
source_event_id,source_session_id,source_exchange_id,source_turn_ids_json,source_mode,location_id,world_day,known_by_json,first_ts,last_ts,repeat_count,payload_json)
VALUES($id,$owner,$category,$topic,$text,$id,$status,$confidence,$importance,$provenance,$compatibility,$rejection,
$event,$session,$exchange,$turns,$mode,$location,$day,$known,$ts,$ts,1,$payload)
ON CONFLICT(owner_id,normalized_key) DO NOTHING;", parameters);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE characteristic_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner ORDER BY CASE status WHEN 'active' THEN 0 WHEN 'superseded' THEN 1 ELSE 2 END,importance DESC,last_ts DESC LIMIT 250;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner AND category='narrative_development' AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamicCharacteristics10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM dynamic_characteristics WHERE owner_id=$owner AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"WITH changed AS (
 SELECT value AS id FROM jsonb_array_elements_text(CAST($heroes AS jsonb)))
SELECT hero_b_id AS subject_id FROM relationship_pair_chemistry JOIN changed ON hero_a_id=id
UNION SELECT hero_a_id FROM relationship_pair_chemistry JOIN changed ON hero_b_id=id;", parameters, transaction);
        }

        internal static void UpsertAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,first_met_day,last_met_day,updated_ts)
SELECT r.observer,r.subject,'verified',r.name,'historical_group_identity',1,$day,$day,$ts
FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(observer text,subject text,name text)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET
identity_state='verified',
canonical_name=CASE WHEN excluded.canonical_name<>'' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,
verification_source='historical_group_identity',confidence=1,last_met_day=$day,updated_ts=$ts
WHERE acquaintances.identity_state<>'verified';", parameters, transaction);
        }

        internal static void UpsertAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,claimed_name,aliases_json,verification_source,source_entity_id,confidence,first_met_day,last_met_day,updated_ts)
VALUES($observer,$subject,'verified',$canonical,$claimed,$aliases,$source,$sourceEntity,1,$day,$day,$ts)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET identity_state='verified',canonical_name=CASE WHEN excluded.canonical_name<>'' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,aliases_json=excluded.aliases_json,verification_source=excluded.verification_source,source_entity_id=excluded.source_entity_id,confidence=1,updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static void UpsertAcquaintances3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,claimed_name,aliases_json,verification_source,source_entity_id,confidence,first_met_day,last_met_day,last_encounter_id,payload_json,updated_ts)
VALUES($observer,$subject,$state,$canonical,$claimed,$aliases,$source,$sourceEntity,0.6,$day,$day,$encounter,$payload,$ts)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET identity_state=$state,canonical_name=CASE WHEN acquaintances.canonical_name='' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,claimed_name=$claimed,aliases_json=$aliases,verification_source=$source,source_entity_id=$sourceEntity,confidence=0.6,last_met_day=$day,last_encounter_id=CASE WHEN $encounter<>'' THEN $encounter ELSE acquaintances.last_encounter_id END,payload_json=$payload,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT evidence_type,claimed_name,source,payload_json FROM identity_evidence
WHERE observer_id=$observer AND subject_id=$subject AND claimed_name=$alias ORDER BY created_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT payload_json
FROM identity_evidence
WHERE observer_id=$observer AND subject_id=$subject
ORDER BY created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE acquaintances SET identity_state='encountered_unknown',claimed_name='',aliases_json='[]',
verification_source='quarantined_ambiguous_name_first_address',source_entity_id='',confidence=0,payload_json=$payload,updated_ts=$ts
WHERE observer_id=$observer AND subject_id=$subject AND identity_state<>'verified';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_roster WHERE hero_id=$observer OR hero_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM acquaintances WHERE observer_id=$observer AND subject_id=$subject LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO acquaintances(observer_id,subject_id,identity_state,canonical_name,first_met_day,last_met_day,encounter_count,last_encounter_id,payload_json,updated_ts)
VALUES($observer,$subject,'encountered_unknown',$canonical,$day,$day,1,$encounter,$payload,$ts);", parameters, transaction);
        }

        internal static void InsertIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO identity_evidence(evidence_id,observer_id,subject_id,evidence_type,claimed_name,canonical_name,source,source_entity_id,encounter_id,confidence,world_day,payload_json,created_ts)
VALUES($id,$observer,$subject,$type,$claimed,$canonical,$source,$sourceEntity,$encounter,$confidence,$day,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='identity_legacy_dialogue_migrated' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT DISTINCT npc_id,player_id FROM conversation_sessions WHERE status<>'deleted' AND npc_id<>'' AND player_id<>'';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_legacy_dialogue_migrated','1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM identity_roster;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM acquaintances WHERE verification_source IN ('same_clan','immediate_family','same_kingdom_nobility','ruler_network');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='relationship_pair_chemistry';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT evidence_id FROM identity_evidence WHERE observer_id='ochivos' AND subject_id='main_hero' AND source='self_introduction';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='identity_payload_compaction_version' LIMIT 1;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static void UpdateAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE acquaintances SET payload_json='{}' WHERE payload_json<>'{}';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT evidence_id,payload_json FROM identity_evidence WHERE length(payload_json)>2048;", parameters, transaction);
        }

        internal static void UpdateIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE identity_evidence SET payload_json=$payload WHERE evidence_id=$id;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_payload_compaction_version',$version);", parameters, transaction);
        }

        internal static void UpsertSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','1');", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='identity_roster_fingerprint' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_roster;", parameters, transaction);
        }

        internal static void DeleteIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM identity_roster;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_roster_fingerprint',$value);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtDynamicReputationMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM court_dynamic_reputation_metrics WHERE domain='popularity' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='identity_synthetic_repair_version' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM identity_evidence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM acquaintances;", parameters, transaction);
        }

        internal static void DeleteIdentityEvidence(ReignDbConnection connection, object syntheticSources, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM identity_evidence
WHERE source IN (" + syntheticSources + @");", parameters, transaction);
        }

        internal static void UpdateAcquaintances3(ReignDbConnection connection, object syntheticSources, object syntheticSources2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE acquaintances SET
identity_state=CASE WHEN claimed_name<>'' THEN 'claimed' ELSE 'encountered_unknown' END,
verification_source=CASE WHEN claimed_name<>'' THEN 'preserved_explicit_claim' ELSE 'recognition_required_after_rule_v3' END,
source_entity_id='',confidence=CASE WHEN claimed_name<>'' THEN 0.6 ELSE 0 END,
last_recognition_result='',last_recognition_encounter_id='',last_recognition_day=-1,
last_recognition_probability=0,last_recognition_roll=-1,recognition_rule_version=3
WHERE verification_source IN (" + syntheticSources + @")
AND NOT EXISTS (
    SELECT 1 FROM identity_evidence e
    WHERE e.observer_id=acquaintances.observer_id AND e.subject_id=acquaintances.subject_id
    AND e.source NOT IN (" + syntheticSources2 + @")
    AND e.evidence_type='verified'
);", parameters, transaction);
        }

        internal static void DeleteAcquaintances(ReignDbConnection connection, object syntheticSources, object syntheticSources2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM acquaintances
WHERE verification_source IN (" + syntheticSources + @")
AND encounter_count=0
AND claimed_name=''
AND NOT EXISTS (
    SELECT 1 FROM identity_evidence e
    WHERE e.observer_id=acquaintances.observer_id AND e.subject_id=acquaintances.subject_id
    AND e.source NOT IN (" + syntheticSources2 + @")
);", parameters, transaction);
        }

        internal static void UpsertSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('identity_synthetic_repair_version',$version);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='campaign_compaction_required' LIMIT 1;", parameters, transaction);
        }

        internal static void Pragma(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "PRAGMA wal_checkpoint(PASSIVE);", parameters, transaction);
        }

        internal static void Pragma2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "PRAGMA optimize;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('campaign_compaction_required','0');", parameters, transaction);
        }

        internal static void UpsertIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO identity_roster(
hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,family_ids_json,is_alive,is_adult,is_player,sex,clan_tier,current_charm,
occupation,is_notable,is_wanderer,is_clan_leader,is_mercenary_clan,governor_settlement_id,governor_settlement_name,updated_ts)
VALUES($hero,$name,$clan,$kingdom,$lord,$ruler,$family,$alive,$adult,$player,$sex,$tier,$charm,
$occupation,$notable,$wanderer,$clanLeader,$mercenary,$governorId,$governorName,$ts)
ON CONFLICT(hero_id) DO UPDATE SET canonical_name=$name,clan_id=$clan,kingdom_id=$kingdom,
is_lord=$lord,is_ruler=$ruler,family_ids_json=$family,is_alive=$alive,is_adult=$adult,is_player=$player,
sex=$sex,clan_tier=$tier,current_charm=$charm,occupation=$occupation,is_notable=$notable,is_wanderer=$wanderer,
is_clan_leader=$clanLeader,is_mercenary_clan=$mercenary,
governor_settlement_id=$governorId,governor_settlement_name=$governorName,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertAcquaintances4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,verification_source,confidence,first_met_day,last_met_day,updated_ts)
VALUES($observer,$subject,'verified',$name,'historical_group_identity',1,$day,$day,$ts)
ON CONFLICT(observer_id,subject_id) DO UPDATE SET
identity_state='verified',
canonical_name=CASE WHEN excluded.canonical_name<>'' THEN excluded.canonical_name ELSE acquaintances.canonical_name END,
verification_source='historical_group_identity',
confidence=1,
last_met_day=$day,
updated_ts=$ts
WHERE acquaintances.identity_state<>'verified';", parameters, transaction);
        }

        internal static void UpdateAcquaintances4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE acquaintances SET last_met_day=$day,encounter_count=encounter_count+1,last_encounter_id=$encounter,updated_ts=$ts WHERE observer_id=$observer AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM identity_evidence WHERE observer_id=$observer AND subject_id=$subject AND source='self_introduction' AND evidence_type='claimed_identity' ORDER BY created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateAcquaintances5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE acquaintances SET
recognition_attempts=recognition_attempts+1,
last_recognition_result=$result,
last_recognition_encounter_id=$encounter,
last_recognition_day=$day,
last_recognition_probability=$probability,
last_recognition_roll=$roll,
recognition_rule_version=$version,
updated_ts=$ts
WHERE observer_id=$observer AND subject_id=$subject
AND COALESCE(last_recognition_encounter_id,'')<>$encounter;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectAcquaintancesDynamic(ReignDbConnection connection, object whereValue, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM acquaintances" + whereValue + " ORDER BY updated_ts DESC LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityEvidence7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_evidence WHERE observer_id=$observer AND subject_id=$subject ORDER BY created_ts DESC LIMIT 200;", parameters, transaction);
        }

        internal static void DeleteAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM acquaintances WHERE observer_id=$observer AND subject_id=$subject;", parameters, transaction);
        }

        internal static void DeleteIdentityEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM identity_evidence WHERE observer_id=$observer AND subject_id=$subject;", parameters, transaction);
        }

        internal const string SelectXSql = @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        hero text,name text,clan text,kingdom text,lord integer,
        ruler integer,family text,alive integer,adult integer,
        player integer,sex text,tier integer,charm integer,
        occupation text,notable integer,wanderer integer,
        clanleader integer,mercenary integer,governorid text,
        governorname text,ts bigint)
)
INSERT INTO identity_roster(
    hero_id,canonical_name,clan_id,kingdom_id,is_lord,is_ruler,
    family_ids_json,is_alive,is_adult,is_player,sex,clan_tier,
    current_charm,occupation,is_notable,is_wanderer,is_clan_leader,
    is_mercenary_clan,governor_settlement_id,governor_settlement_name,
    updated_ts)
SELECT hero,name,clan,kingdom,lord,ruler,family,alive,adult,player,sex,
       tier,charm,occupation,notable,wanderer,clanleader,mercenary,
       governorid,governorname,ts
FROM x
ON CONFLICT(hero_id) DO UPDATE SET
    canonical_name=excluded.canonical_name,
    clan_id=excluded.clan_id,
    kingdom_id=excluded.kingdom_id,
    is_lord=excluded.is_lord,
    is_ruler=excluded.is_ruler,
    family_ids_json=excluded.family_ids_json,
    is_alive=excluded.is_alive,
    is_adult=excluded.is_adult,
    is_player=excluded.is_player,
    sex=excluded.sex,
    clan_tier=excluded.clan_tier,
    current_charm=excluded.current_charm,
    occupation=excluded.occupation,
    is_notable=excluded.is_notable,
    is_wanderer=excluded.is_wanderer,
    is_clan_leader=excluded.is_clan_leader,
    is_mercenary_clan=excluded.is_mercenary_clan,
    governor_settlement_id=excluded.governor_settlement_id,
    governor_settlement_name=excluded.governor_settlement_name,
    updated_ts=excluded.updated_ts
WHERE (identity_roster.canonical_name,identity_roster.clan_id,
       identity_roster.kingdom_id,identity_roster.is_lord,
       identity_roster.is_ruler,identity_roster.family_ids_json,
       identity_roster.is_alive,identity_roster.is_adult,
       identity_roster.is_player,identity_roster.sex,
       identity_roster.clan_tier,identity_roster.current_charm,
       identity_roster.occupation,identity_roster.is_notable,
       identity_roster.is_wanderer,identity_roster.is_clan_leader,
       identity_roster.is_mercenary_clan,
       identity_roster.governor_settlement_id,
       identity_roster.governor_settlement_name)
IS DISTINCT FROM
      (excluded.canonical_name,excluded.clan_id,excluded.kingdom_id,
       excluded.is_lord,excluded.is_ruler,excluded.family_ids_json,
       excluded.is_alive,excluded.is_adult,excluded.is_player,
       excluded.sex,excluded.clan_tier,excluded.current_charm,
       excluded.occupation,excluded.is_notable,excluded.is_wanderer,
       excluded.is_clan_leader,excluded.is_mercenary_clan,
       excluded.governor_settlement_id,
       excluded.governor_settlement_name);";

        internal static List<Dictionary<string, object>> SelectCharacterNpcLivesState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value_json FROM character_npc_lives_state WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpsertCharacterNpcLivesState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_npc_lives_state(timeline_id,value_json) VALUES($timeline,$json)
ON CONFLICT(timeline_id) DO UPDATE SET value_json=EXCLUDED.value_json;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void InsertConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,end_ts,start_world_day,end_world_day,location_id,participants_json,payload_json)
VALUES($session,$campaign,'',$player,'party_announcement','closed',$ts,$ts,$day,$day,$location,$audience,$payload);", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_input_receipts
(input_id,timeline_id,session_id,player_id,source_turn_id,source_hash,restore_generation,status,created_ts,updated_ts)
VALUES($id,$timeline,$session,$player,$source,$hash,$generation,'received',$now,$now) ON CONFLICT(input_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT source_hash,source_turn_id FROM conversation_input_receipts WHERE input_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.world_day FROM conversation_turns t WHERE t.speaker_id=$hero AND t.role='npc'
AND t.status='active' AND t.world_day<=$day AND t.channel IN ('in_person','dialogue','individual') AND jsonb_array_length(t.participants_json::jsonb)=2
AND t.participants_json::jsonb @> $player::jsonb AND t.payload_json::jsonb->>'timelineId'=$timeline
ORDER BY t.world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationSessions3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT session_id FROM conversation_sessions WHERE session_id=$id AND player_id=$player
AND participants_json::jsonb @> $audience::jsonb AND $audience::jsonb @> participants_json::jsonb
AND payload_json::jsonb->>'timelineId'=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COALESCE(MAX(turn_order),-1)+1 AS n FROM conversation_turns WHERE session_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$source AND speaker_id=$player AND role='player' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, object column, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET " + column + "=$value WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationAcceptedUnits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT receipt_json FROM conversation_accepted_units
WHERE receipt_json::jsonb->'politicalReflection'->>'heroId'=$hero
AND receipt_json::jsonb->'politicalReflection'->>'timelineId'=$timeline
ORDER BY created_ts,unit_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT t.text,t.payload_json,t.event_id,u.receipt_json::jsonb->>'outputContract' AS accepted_output_contract
FROM conversation_turns t LEFT JOIN conversation_accepted_units u ON u.unit_id=t.event_id
WHERE t.session_id=$session AND t.exchange_id=$exchange AND t.speaker_id=$speaker AND t.role='npc' AND t.turn_id=$turn LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT n.turn_id,n.payload_json,n.exchange_id FROM conversation_turns n
JOIN conversation_sessions s ON s.session_id=n.session_id
WHERE n.speaker_id=$hero AND n.role='npc' AND n.status='active' AND s.campaign_id=$campaign
AND EXISTS(SELECT 1 FROM conversation_turns p WHERE p.session_id=n.session_id AND p.exchange_id=n.exchange_id
AND p.role='player' AND p.speaker_id=$player AND p.status='active' AND length(trim(p.text))>0)
ORDER BY n.ts DESC LIMIT 256;", parameters, transaction);
        }
    }
}
