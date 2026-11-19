using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void DeleteEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM events
WHERE event_id=$id
AND event_type='verified_qualification_property_transfer';", parameters, transaction);
        }

        internal static void UpsertEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO events(
event_id,campaign_id,ts,world_day,event_type,summary,participants_json,
witnesses_json,about_entities_json,known_by_json,visibility,importance,
source_reliability,confidence,payload_json,embedding_status,created_utc)
VALUES($id,$campaign,$ts,$day,'verified_qualification_property_transfer',
$summary,$participants,$participants,$participants,$participants,'private',
0.95,1,1,$payload,'not_indexed',$utc)
ON CONFLICT(event_id) DO UPDATE SET
ts=$ts,world_day=$day,summary=$summary,participants_json=$participants,
witnesses_json=$participants,about_entities_json=$participants,
known_by_json=$participants,payload_json=$payload,created_utc=$utc;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void DeleteRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$ab,affinity_b_to_a=$ba,tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,
shared_tag=$shared,projected_native_relation=$projected,
native_action_pending=1,native_action_id=$action,updated_ts=$ts
WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static void UpsertRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,world_day,last_sync_day,
attempt_count,claimed_ts,last_error,updated_ts,revision)
VALUES($pair,$a,$b,$target,0,'pending',$day,-1000,0,0,'',$ts,1)
ON CONFLICT(pair_key) DO UPDATE SET
target_relation=$target,status='pending',world_day=$day,claimed_ts=0,last_error='',
revision=CASE WHEN relationship_native_targets.target_relation=$target
THEN relationship_native_targets.revision
ELSE relationship_native_targets.revision+1 END,
updated_ts=$ts;", parameters, transaction);
        }

        internal static void DeleteIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM identity_evidence
WHERE observer_id=$observer AND subject_id=$subject
AND source='qualification_manipulation_fixture';", parameters, transaction);
        }

        internal static void DeleteAcquaintances2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM acquaintances
WHERE observer_id=$observer AND subject_id=$subject;", parameters, transaction);
        }

        internal static void UpsertAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO acquaintances(
observer_id,subject_id,identity_state,canonical_name,claimed_name,aliases_json,
verification_source,source_entity_id,confidence,first_met_day,last_met_day,
encounter_count,last_encounter_id,recognition_attempts,last_recognition_result,
last_recognition_encounter_id,payload_json,updated_ts)
VALUES($observer,$subject,$state,$canonical,$claimed,$aliases,$source,$sourceEntity,
$confidence,$firstDay,$lastDay,$encounters,$lastEncounter,$attempts,$result,
$resultEncounter,$payload,$updated);", parameters, transaction);
        }

        internal static void InsertOwnershipSentinel(ReignDbConnection connection, object quotedSchema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO " + quotedSchema + ".ownership_sentinel VALUES('preserve-orphan');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectOwnershipSentinel(ReignDbConnection connection, object quotedSchema, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM " + quotedSchema + ".ownership_sentinel;", parameters, transaction);
        }

        internal static void InsertConversationSessions27(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('orchard_history',$campaign,'npc','player','closed',1,10,'[""npc"",""player""]','{""timelineId"":""branch_a""}');", parameters, transaction);
        }

        internal static void InsertConversationTurns11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES($id,'orchard_history',$id,$order,$id,$role,$speaker,$speaker,$text,$day,10,$audience,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountConversationTurns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM conversation_turns;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM memories;", parameters, transaction);
        }

        internal static void InsertConversationContinuity3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conversation_continuity
(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES('private_replacement','branch_a','npc','player','agenda','open',1,2,'unheard','unheard',10,'{""worldDay"":2}');
INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts)
VALUES('private_replacement_1','private_replacement','answer',1,'{""sourceTurnId"":""answer""}',8);", parameters, transaction);
        }

        internal const string SelectCampaignRegistrySql = "SELECT to_jsonb(r)::text AS row_json FROM reign_meta.campaign_registry r WHERE campaign_id=$campaign;";
    }
}
