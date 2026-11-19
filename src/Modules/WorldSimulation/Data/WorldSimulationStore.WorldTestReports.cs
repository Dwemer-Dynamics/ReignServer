using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MIN(world_day) AS day FROM world_test_native_heartbeats
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestCheckpointReports(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline AND checkpoint_day=$gate;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestDailyRollups6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT day_key,world_day,overall_status,rollup_json,updated_ts
FROM world_test_daily_rollups
WHERE campaign_id=$campaign AND timeline_id=$timeline AND world_day>=$target
ORDER BY world_day LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestCheckpointReports(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT checkpoint_day,report_json FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline AND checkpoint_day<$gate
ORDER BY checkpoint_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestManifests2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT manifest_json FROM world_test_manifests
WHERE campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestDiagnosticEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key<=$day;", parameters, transaction);
        }

        internal static void InsertWorldTestCheckpointReports(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_checkpoint_reports(
campaign_id,timeline_id,checkpoint_day,report_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$gate,$report,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT pair_key,hero_a_id,hero_b_id,
affinity_a_to_b,
affinity_b_to_a
FROM relationship_pair_chemistry
WHERE $player='' OR (hero_a_id<>$player AND hero_b_id<>$player);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestCheckpointReports2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT checkpoint_day,report_json,updated_ts FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY checkpoint_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestCheckpointReports3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT report_json FROM world_test_checkpoint_reports
WHERE campaign_id=$campaign AND timeline_id=$timeline AND checkpoint_day=126
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestManifests3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT manifest_json FROM world_test_manifests
WHERE campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRollupQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT status,revision FROM world_test_rollup_queue
WHERE campaign_id='campaign' AND timeline_id='main' AND day_key=31 LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestRollupQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_test_rollup_queue
WHERE campaign_id='campaign' AND timeline_id='branch';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRollupQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT status FROM world_test_rollup_queue
WHERE campaign_id='campaign' AND timeline_id='main' AND day_key=31;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM sqlite_master
WHERE type='table' AND name IN (
'rebellion_movements','rebellion_memberships','rebellion_transitions',
'negotiated_action_drafts','negotiated_action_approvals');", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,
effective_affinity_a_to_b,effective_affinity_b_to_a,first_day,last_day,updated_ts)
VALUES('reconcile_a|reconcile_b','reconcile_a','reconcile_b',10,20,10,20,1,1,1);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET affinity_a_to_b=101 WHERE pair_key='reconcile_a|reconcile_b';", parameters, transaction);
        }
    }
}
