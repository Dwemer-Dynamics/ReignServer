using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static void UpsertSchemaMeta9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_world_test_telemetry_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static void UpsertWorldTestChunkCounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_chunk_counters(
campaign_id,timeline_id,day_key,subsystem,chunk_key,counters_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$subsystem,$chunk,$counters,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,subsystem,chunk_key) DO UPDATE SET
counters_json=$counters,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestDiagnosticEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem=$subsystem
AND evidence_key=$key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestDiagnosticEvidence3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem=$subsystem
AND day_key=$day AND severity NOT IN ('warning','error');", parameters, transaction);
        }

        internal static void UpsertWorldTestDiagnosticEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_diagnostic_evidence(
campaign_id,timeline_id,day_key,subsystem,evidence_key,severity,payload_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$subsystem,$key,$severity,$payload,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,subsystem,evidence_key) DO UPDATE SET
day_key=$day,severity=$severity,payload_json=$payload,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestRelationshipPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_relationship_pairs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND pair_key=$pair;", parameters, transaction);
        }

        internal static void DeleteWorldTestRelationshipPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_test_relationship_pairs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND pair_key=$pair;", parameters, transaction);
        }

        internal static void UpsertWorldTestRelationshipPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_relationship_pairs(
campaign_id,timeline_id,pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
tag_a_to_b,tag_b_to_a,shared_tag,mbti_a,mbti_b,notable_a,notable_b,
last_delta_a_to_b,last_delta_b_to_a,last_context_kind,first_day,last_day,updated_ts)
VALUES($campaign,$timeline,$pair,$a,$b,$affinityAB,$affinityBA,$tagAB,$tagBA,$shared,
$mbtiA,$mbtiB,$notableA,$notableB,$deltaAB,$deltaBA,$context,$first,$last,$ts)
ON CONFLICT(campaign_id,timeline_id,pair_key) DO UPDATE SET
hero_a_id=$a,hero_b_id=$b,affinity_a_to_b=$affinityAB,affinity_b_to_a=$affinityBA,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,shared_tag=$shared,mbti_a=$mbtiA,mbti_b=$mbtiB,
notable_a=$notableA,notable_b=$notableB,last_delta_a_to_b=$deltaAB,
last_delta_b_to_a=$deltaBA,last_context_kind=$context,last_day=$last,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertWorldTestRollupQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rollup_queue(
campaign_id,timeline_id,day_key,status,revision,reason,attempt_count,last_error,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,'pending',1,$reason,0,'',$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
status='pending',revision=world_test_rollup_queue.revision+1,
reason=$reason,last_error='',updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRelationshipMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT edge_count,directional_edge_count
FROM world_test_relationship_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND hero_id=$hero;", parameters, transaction);
        }

        internal static void DeleteWorldTestRelationshipMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_test_relationship_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND hero_id=$hero;", parameters, transaction);
        }

        internal static void UpsertWorldTestRelationshipMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_relationship_memberships(
campaign_id,timeline_id,category_type,category_key,hero_id,edge_count,
directional_edge_count,updated_day,updated_ts)
VALUES($campaign,$timeline,$type,$category,$hero,$count,$directional,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,category_type,category_key,hero_id) DO UPDATE SET
edge_count=$count,directional_edge_count=$directional,updated_day=$day,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRelationshipPairMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT edge_count,directional_edge_count
FROM world_test_relationship_pair_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND pair_key=$pair;", parameters, transaction);
        }

        internal static void DeleteWorldTestRelationshipPairMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_test_relationship_pair_memberships
WHERE campaign_id=$campaign AND timeline_id=$timeline AND category_type=$type
AND category_key=$category AND pair_key=$pair;", parameters, transaction);
        }

        internal static void UpsertWorldTestRelationshipPairMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_relationship_pair_memberships(
campaign_id,timeline_id,category_type,category_key,pair_key,edge_count,
directional_edge_count,updated_day,updated_ts)
VALUES($campaign,$timeline,$type,$category,$pair,$count,$directional,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,category_type,category_key,pair_key) DO UPDATE SET
edge_count=$count,directional_edge_count=$directional,updated_day=$day,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertWorldTestCurrentMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_current_metrics(
campaign_id,timeline_id,metric_scope,metric_key,metric_value,updated_day,updated_ts)
VALUES($campaign,$timeline,$scope,$key,MAX(0,$delta),$day,$ts)
ON CONFLICT(campaign_id,timeline_id,metric_scope,metric_key) DO UPDATE SET
metric_value=MAX(0,world_test_current_metrics.metric_value+$delta),
updated_day=$day,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestDiagnosticEvidence4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id='main' AND subsystem='relationships'
AND evidence_key='pair-a-b';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRelationshipPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a
FROM world_test_relationship_pairs
WHERE campaign_id=$campaign AND timeline_id='main' AND pair_key='a|b' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestRollupQueue3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM world_test_rollup_queue WHERE campaign_id=$campaign AND timeline_id='main' AND day_key=2;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestChunkCounters4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
AND subsystem=$subsystem;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestChunkCounters5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem=$subsystem;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestCurrentMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT metric_value
FROM world_test_current_metrics
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND metric_scope=$scope AND metric_key=$key LIMIT 1;", parameters, transaction);
        }
    }
}
