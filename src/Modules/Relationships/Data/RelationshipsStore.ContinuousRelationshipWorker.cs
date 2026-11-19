using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT *,COUNT(*) OVER () AS pending_count FROM relationship_daily_inputs
WHERE status='pending'
ORDER BY CASE WHEN timeline_id=$preferredTimeline THEN 0 ELSE 1 END,day_key,timeline_id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='processing',started_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND status='pending';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT day_key,world_day,presence_groups_json,hero_changes_json
FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND day_key BETWEEN $start AND $day
ORDER BY day_key;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
status='processed',pair_cursor='',candidate_pairs=$candidates,processed_pairs=$processed,
cadence_shard=$shard,window_start_day=$windowStart,window_day_count=$windowDays,
pair_day_evaluations=$pairDays,processed_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
presence_groups_json='[]',hero_changes_json='[]'
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='processed' AND day_key<=$consumedThrough;", parameters, transaction);
        }

        internal static void DeleteRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='processed' AND day_key<$day-7;", parameters, transaction);
        }

        internal static void Pragma(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "PRAGMA wal_checkpoint(PASSIVE);", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='pending',last_error=$error
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }
    }
}
