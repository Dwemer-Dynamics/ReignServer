using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_history_lie_checks WHERE lie_check_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_benefits WHERE benefit_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id=$giver
AND recipient_id=$recipient
ORDER BY updated_ts DESC, benefit_id ASC
LIMIT 20;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id=$giver
AND recipient_id=$recipient
ORDER BY updated_ts DESC, benefit_id ASC
LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_type,summary,payload_json,'' AS category,'completed' AS phase,participants_json,'events' AS source_table FROM events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_type,summary,payload_json,category,phase,'[]' AS participants_json,'world_history_events' AS source_table FROM world_history_events WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT entity_id FROM world_history_entities WHERE event_id=$id AND entity_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT entity_id
FROM world_history_entities
WHERE event_id=$id AND entity_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id
FROM events
WHERE campaign_id=$campaign
AND ts>=$cutoff
AND (
    event_type LIKE '%gift%'
    OR event_type LIKE '%transfer%'
    OR event_type LIKE '%rescue%'
    OR event_type LIKE '%saved%'
)
ORDER BY ts DESC
LIMIT 50;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,source_evidence_json,payload_json)
VALUES($id,$campaign,$timeline,$giver,$recipient,$kind,$description,$event,$turn,$delta,1,$utility,'retained',$ts,$ts,$evidence,$payload);", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET benefit_event_id=$benefit WHERE receipt_id=$receipt;", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_benefits SET retained_appreciation=$retained,
status=$status,updated_ts=$ts,payload_json=$payload WHERE benefit_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts ORDER BY created_ts DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits4(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_benefits ORDER BY updated_ts DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET affinity_a_to_b=10 WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void InsertEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('gift_evidence',$campaign,$ts,10,'verified_gift','A valuable home was transferred.',
'[""player"",""npc_b""]','[""player"",""npc_b""]',$utc);", parameters, transaction);
        }

        internal static void InsertEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('dialogue_gift_claim',$campaign,$ts,10,'dialogue_memory','The player said a fine silver cup was a gift.',
'[""player"",""npc_c""]','[""player"",""npc_c""]',$utc);", parameters, transaction);
        }

        internal static void InsertEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('dialogue_betrayal_claim',$campaign,$ts,10,'dialogue_memory','The player claimed to have betrayed the observer.',
'[""player"",""npc_dialogue_betrayal""]','[""player"",""npc_dialogue_betrayal""]',$utc);", parameters, transaction);
        }

        internal static void InsertEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
known_by_json,created_utc) VALUES('betrayal_evidence',$campaign,$ts,10,'verified_betrayal','A completed action receipt proves the ally sold the observer out to an enemy.',
'[""player"",""npc_c""]','[""player"",""npc_c""]',$utc);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id='player'
AND recipient_id='npc_b'
AND payload_json LIKE '%gift_evidence%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationRelationshipBenefits2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM conversation_relationship_benefits WHERE source_event_id='gift';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipBenefits5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM conversation_relationship_benefits
WHERE benefit_id='home_gift'
LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM conversation_relationship_benefits
WHERE campaign_id=$campaign
AND giver_id='player'
AND recipient_id='npc_b';", parameters, transaction);
        }

        internal static void InsertConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,payload_json)
VALUES('benefit_generated_duplicate',$campaign,$timeline,'player','npc_b',$kind,$description,$event,$turn,
$delta,$retained,$utility,$status,$ts,$ts,$payload);", parameters, transaction);
        }

        internal static void InsertConversationRelationshipBenefits2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_benefits(
benefit_id,campaign_id,timeline_id,giver_id,recipient_id,benefit_kind,description,source_event_id,source_turn_id,
original_delta,retained_appreciation,continued_utility,status,created_ts,updated_ts,payload_json)
VALUES('home_gift',$campaign,$timeline,'player','npc_b',$kind,$description,$event,$turn,
$delta,$retained,$utility,$status,$ts,$ts,$payload);", parameters, transaction);
        }

        internal static void UpsertWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO world_history_lie_checks(
lie_check_id,episode_key,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,formula_version,outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts)
VALUES('caught_lie','caught_lie_episode',$campaign,'main',10,'player','npc_c','false claim','false claim',
'contradicted','firsthand',$formula,'detected_firsthand','[""evidence_1""]','[""evidence_1""]','[]','[]','{}',$ts,$ts);", parameters, transaction);
        }

        internal static void InsertWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_history_lie_checks(
lie_check_id,episode_key,campaign_id,timeline_id,world_day,claimant_id,target_id,raw_claim,normalized_claim,
objective_verdict,knowledge_basis,formula_version,outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts)
SELECT $id,$id,campaign_id,timeline_id,world_day,$claimant,$observer,raw_claim,normalized_claim,
$verdict,$basis,formula_version,$outcome,evidence_event_ids_json,known_event_ids_json,matched_json,
mismatched_json,prompt_packet_json,created_ts,updated_ts FROM world_history_lie_checks WHERE lie_check_id='caught_lie';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='missing_reaction_fallback' ORDER BY observer_id,target_id;", parameters, transaction);
        }

        internal static void InsertConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(
turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,channel,world_day,ts,
location_id,participants_json,status,payload_json)
VALUES('prior_npc_turn',$session,'',2,'party_prior_session_turn_1','npc','npc_late','Purios',
'Count seven open conversations on the clay disk, then rest when all seven marks are used.','party_chat',10,$ts,
'town_danustica','[""player"",""npc_early"",""npc_late""]','active','{}');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='stale_native_snapshot';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='cancelling_directions' ORDER BY observer_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE exchange_id='band_limited_reciprocal' ORDER BY observer_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT speaker_id,speaker_name,text,turn_order,exchange_id
FROM conversation_turns
WHERE session_id=$session AND exchange_id<>$exchange AND role='npc' AND status='active'
ORDER BY turn_order ASC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,mbti_a,mbti_b,base_chance_a_to_b,
base_chance_b_to_a,chance_a_to_b,chance_b_to_a,
affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,
projected_native_relation,first_day,last_day,last_context_kind,
last_context_id,processing_shard,compatibility_version,updated_ts)
VALUES($pair,$a,$b,$typeA,$typeB,$baseAB,$baseBA,$chanceAB,$chanceBA,
$ab,$ba,$tagAB,$tagBA,0,$day,$day,'conversation',$exchange,$shard,$version,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(
receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,mode,act_kind,severity_tier,valence,
base_delta,modifier_delta,final_delta,prior_affinity,resulting_affinity,prior_native_relation,resulting_native_relation,native_pair_delta,
  lie_check_id,benefit_event_id,gift_recipient_id,confidence,evidence_json,modifiers_json,summary,current_conduct_quote,status,created_ts)
VALUES($id,$campaign,$timeline,$exchange,$turn,$observer,$target,$mode,$act,$tier,$valence,$base,$modifier,$final,
$priorAffinity,$resultingAffinity,$priorNative,$resultingNative,0,$lie,$benefit,$giftRecipient,$confidence,$evidence,$modifiers,$summary,$currentConductQuote,$status,$ts);", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conversation_relationship_receipts SET native_application_status='skipped' WHERE receipt_id=$id;", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta
WHERE receipt_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts
WHERE exchange_id=$exchange AND ((observer_id=$a AND target_id=$b) OR (observer_id=$b AND target_id=$a));", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta,
native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT restore_generation FROM memory_precision_state WHERE state_id='current' FOR NO KEY UPDATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConversationRelationshipReceipts8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM conversation_relationship_receipts WHERE receipt_id=$id FOR UPDATE;", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET prior_native_relation=$prior,resulting_native_relation=$result,native_pair_delta=$delta,
native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id AND native_application_status!='applied';", parameters, transaction);
        }

        internal static void UpdateConversationRelationshipReceipts6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE conversation_relationship_receipts
SET native_application_status='applied',native_applied_ts=$ts,native_receipt_json=$receipt
WHERE receipt_id=$id AND native_application_status!='applied';", parameters, transaction);
        }

        internal static void InsertConversationRelationshipReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_relationship_receipts(
receipt_id,campaign_id,timeline_id,exchange_id,source_turn_id,observer_id,target_id,mode,act_kind,severity_tier,valence,
base_delta,modifier_delta,final_delta,native_pair_delta,evidence_json,summary,status,native_application_status,created_ts)
VALUES($id,$campaign,$timeline,$exchange,$action,$subject,$target,$mode,$mode,$tier,$valence,
$delta,0,$delta,$delta,$evidence,$summary,$status,'pending',$ts);", parameters, transaction);
        }

        internal const string SelectRelationshipPairChemistrySql = "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1 FOR UPDATE;";

        internal const string SelectRelationshipPairChemistrySql2 = "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;";

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static List<Dictionary<string, object>> QuerySql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, sql, parameters, transaction);
        }
    }
}
