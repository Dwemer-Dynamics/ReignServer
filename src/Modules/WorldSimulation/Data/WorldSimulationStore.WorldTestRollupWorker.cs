using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT world_day FROM world_test_native_heartbeats
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertWorldTestDailyRollups(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_daily_rollups(
campaign_id,timeline_id,day_key,world_day,overall_status,rollup_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$worldDay,$status,$rollup,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=$worldDay,overall_status=$status,rollup_json=$rollup,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateWorldTestRollupQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_test_rollup_queue SET
status='completed',attempt_count=attempt_count+1,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
AND revision=$revision;", parameters, transaction);
        }

        internal static void Pragma(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "PRAGMA wal_checkpoint(PASSIVE);", parameters, transaction);
        }

        internal static void UpdateWorldTestRollupQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE world_test_rollup_queue SET
status='pending',attempt_count=attempt_count+1,last_error=$error,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }

        internal static void DeleteWorldTestRollupQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_test_rollup_queue
WHERE rowid IN (
  SELECT rowid FROM world_test_rollup_queue
  WHERE campaign_id=$campaign AND timeline_id=$timeline
    AND status='completed' AND day_key<$queueCutoff
  ORDER BY day_key LIMIT 100
);", parameters, transaction);
        }

        internal static void DeleteWorldTestDiagnosticEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_test_diagnostic_evidence
WHERE rowid IN (
  SELECT rowid FROM world_test_diagnostic_evidence
  WHERE campaign_id=$campaign AND timeline_id=$timeline
    AND severity NOT IN ('warning','error') AND day_key<$evidenceCutoff
  ORDER BY day_key LIMIT 100
);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRollupQueue3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT queue.campaign_id,queue.timeline_id,queue.day_key,queue.revision,queue.updated_ts
FROM world_test_rollup_queue queue WHERE queue.status='pending'
AND NOT EXISTS (SELECT 1 FROM relationship_daily_inputs input
 WHERE input.campaign_id=queue.campaign_id AND input.timeline_id=queue.timeline_id
 AND input.day_key<=queue.day_key AND input.status IN ('pending','processing'))
ORDER BY queue.day_key,queue.updated_ts LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestRollupQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_rollup_queue WHERE status='pending';", parameters, transaction);
        }
    }
}
