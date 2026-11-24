using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    /// <summary>The Spymaster module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class SpymasterStore
    {
        internal static List<Dictionary<string, object>> SelectSpymasterMissionMemory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT mission_id,phase,mission_type,target_type,target_id,target_name,
started_day,due_day,resolved_day,request_summary,result_summary
FROM spymaster_mission_memory
WHERE campaign_id=$campaign AND timeline_id=$timeline AND spymaster_hero_id=$hero
ORDER BY started_day DESC,updated_ts DESC LIMIT 12;", parameters, transaction);
        }

        internal static void UpsertSpymasterMissionMemory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO spymaster_mission_memory(campaign_id,timeline_id,mission_id,spymaster_hero_id,
phase,mission_type,target_type,target_id,target_name,started_day,due_day,resolved_day,request_summary,result_summary,payload_json,updated_ts)
VALUES($campaign,$timeline,$mission,$spymaster,$phase,$type,$targetType,$target,$targetName,$started,$due,$resolved,$request,$result,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,mission_id) DO UPDATE SET phase=$phase,resolved_day=$resolved,
result_summary=$result,payload_json=$payload,updated_ts=$ts;", parameters, transaction);
        }

        internal static bool MitigationReceiptTableExists(ReignDbConnection connection)
        {
            return (bool)ReignDatabase.QuerySql(connection,
                "SELECT to_regclass(current_schema() || '.spymaster_mitigation_receipts') IS NOT NULL AS present;")[0]["present"];
        }

        internal static List<Dictionary<string, object>> SelectMitigationReceipt(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT terms_hash,response_json FROM spymaster_mitigation_receipts
WHERE campaign_id=$campaign AND mission_id=$mission;", parameters, transaction);
        }

        internal static void InsertMitigationReceipt(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO spymaster_mitigation_receipts(campaign_id,mission_id,terms_hash,response_json)
VALUES($campaign,$mission,$terms,$response);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> LockMitigationTimelines(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT timeline_id,is_active FROM world_history_timelines
WHERE campaign_id=$campaign AND (is_active=1 OR timeline_id=$timeline) ORDER BY timeline_id FOR SHARE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> MitigateRumorTags(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"WITH eligible AS (
    SELECT ro.occurrence_id,ro.catalog_revision FROM rumor_subject_tags rst
    JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
    WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND ro.status='active'
      AND ro.world_day<=$day AND ro.expires_day>$day
      AND rst.subject_id=$subject AND rst.tag_id=$tag AND rst.status='active'
    ORDER BY ro.occurrence_id FOR UPDATE OF ro,rst
), changed AS (
    UPDATE rumor_subject_tags rst SET status=CASE WHEN $remove THEN 'disproven' ELSE rst.status END,
        rumor_value=CASE WHEN $remove THEN rst.rumor_value ELSE CAST(rst.rumor_value*$factor AS INTEGER) END,updated_ts=$ts
    FROM eligible WHERE rst.occurrence_id=eligible.occurrence_id AND rst.subject_id=$subject AND rst.tag_id=$tag
      AND ($remove OR rst.rumor_value<>CAST(rst.rumor_value*$factor AS INTEGER))
    RETURNING rst.occurrence_id
)
SELECT eligible.occurrence_id,eligible.catalog_revision,(changed.occurrence_id IS NOT NULL) AS changed
FROM eligible LEFT JOIN changed USING(occurrence_id) ORDER BY eligible.occurrence_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> MitigateReputationTag(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"WITH eligible AS (
    SELECT tag_id,source_occurrence_id,catalog_revision FROM character_reputations
    WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
      AND tag_id=$tag AND status='active' AND acquired_day<=$day FOR UPDATE
), changed AS (
    UPDATE character_reputations cr SET status=CASE WHEN $remove THEN 'mitigated' ELSE cr.status END,
        reputation_value=CASE WHEN $remove THEN cr.reputation_value ELSE CAST(cr.reputation_value*$factor AS INTEGER) END,updated_ts=$ts
    FROM eligible WHERE cr.campaign_id=$campaign AND cr.timeline_id=$timeline AND cr.subject_id=$subject
      AND cr.tag_id=eligible.tag_id AND ($remove OR cr.reputation_value<>CAST(cr.reputation_value*$factor AS INTEGER))
    RETURNING cr.tag_id
)
SELECT eligible.tag_id,eligible.source_occurrence_id,eligible.catalog_revision,(changed.tag_id IS NOT NULL) AS changed
FROM eligible LEFT JOIN changed USING(tag_id);", parameters, transaction);
        }

        internal static void UpsertCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'',$archetype,'target',$description,$value,$day,0,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET description=$description,reputation_value=$value,
acquired_day=$day,status='active',updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_occurrences(occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,
source_event_id,world_day,expires_day,status,exposure_chance,exposure_roll,catalog_revision,participants_json,provenance_summary,snapshot_json,created_ts,updated_ts)
VALUES($occurrence,$campaign,$timeline,'spymaster_fabrication',$thread,'',$day,$expires,'active',1,0,0,$participants,
'An unattributed story is circulating.',$snapshot,$ts,$ts);", parameters, transaction);
        }

        internal static void UpsertRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_subject_tags(occurrence_id,subject_id,tag_id,subject_role,description,
rumor_value,reputation_value,status,co_participants_json,snapshot_json,updated_ts)
VALUES($occurrence,$subject,$tag,'target',$description,$value,0,'active','[]',$snapshot,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT agent_hero_id,sponsor_kingdom_id,sponsor_ruler_id,
settlement_id,is_notable,status,recruited_day,next_action_day,last_action_day,payload_json
FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY sponsor_kingdom_id,agent_hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT loyalty FROM relationships WHERE subject_id=$hero AND target_id=$ruler LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 AS found FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline AND sponsor_kingdom_id=$sponsor AND status IN ('dormant','active') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT agent_hero_id FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static void InsertForeignAgentAssignments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO foreign_agent_assignments(campaign_id,timeline_id,agent_hero_id,sponsor_kingdom_id,
sponsor_ruler_id,settlement_id,is_notable,status,recruited_day,next_action_day,last_action_day,payload_json,updated_ts,assignment_id)
VALUES($campaign,$timeline,$agent,$sponsor,$ruler,$settlement,$notable,'dormant',$day,$next,-1,$payload,$ts,$assignment);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline AND sponsor_kingdom_id=$sponsor AND status IN ('dormant','active') LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateForeignAgentAssignments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='active',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", parameters, transaction);
        }

        internal static void UpdateForeignAgentAssignments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET status='dormant',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", parameters, transaction);
        }

        internal static void UpdateForeignAgentAssignments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE foreign_agent_assignments SET last_action_day=$day,next_action_day=$next,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent;", parameters, transaction);
        }

        internal static void InsertForeignAgentDisruptionReceipt(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO foreign_agent_disruption_receipts
(campaign_id,timeline_id,restore_generation,action_id,assignment_id,scheduled_due_day,issued_day,expires_day,action_json)
VALUES($campaign,$timeline,$generation,$action,$assignment,$due,$issued,$expires,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> ReadForeignAgentDisruptionReceipts(
            ReignDbConnection connection, Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT action_id,assignment_id,issued_day,expires_day,action_json
FROM foreign_agent_disruption_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND restore_generation=$generation
  AND issued_day<=$day AND expires_day>$day
ORDER BY issued_day,action_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectForeignAgentAssignments5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT agent_hero_id,sponsor_kingdom_id,settlement_id,is_notable,status,recruited_day,next_action_day,payload_json,assignment_id
FROM foreign_agent_assignments WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key,hero_a_id,hero_b_id,
affinity_a_to_b,affinity_b_to_a,effective_affinity_a_to_b,effective_affinity_b_to_a,
last_day,last_context_kind,last_context_id,state_revision
FROM relationship_pair_chemistry
WHERE (hero_a_id=$sponsor AND hero_b_id=$player)
   OR (hero_a_id=$player AND hero_b_id=$sponsor)
LIMIT 1;", parameters, transaction);
        }

        internal static Dictionary<string, object> ReadForeignAgentAssignment(ReignDbConnection connection, Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM foreign_agent_assignments
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent FOR UPDATE;", parameters, transaction).SingleOrDefault();
        }

        internal static bool ExposeForeignAgentAssignment(ReignDbConnection connection, Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE foreign_agent_assignments SET status='exposed',payload_json=$payload,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND agent_hero_id=$agent AND status IN ('dormant','active')
RETURNING agent_hero_id;", parameters, transaction).Count == 1;
        }
    }
}
