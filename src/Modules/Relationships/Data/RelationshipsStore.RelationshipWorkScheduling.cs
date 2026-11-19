using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT lifecycle.* FROM relationship_pair_lifecycle lifecycle
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested ON requested.value=lifecycle.pair_key;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='pending',last_error='worker_recovered_interrupted_day'
WHERE status='processing' AND ($initial=1 OR started_ts<$cutoff);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM relationship_pair_chemistry pair
WHERE pair.native_action_pending=1 AND ($player='' OR (pair.hero_a_id<>$player AND pair.hero_b_id<>$player))
AND NOT EXISTS (SELECT 1 FROM relationship_native_targets target WHERE target.pair_key=pair.pair_key);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle WHERE last_processed_day<$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"WITH presence AS MATERIALIZED (
 SELECT hero,group_key FROM jsonb_to_recordset(CAST($presence AS jsonb)) AS p(hero text,group_key integer)
)
SELECT lifecycle.* FROM relationship_pair_lifecycle lifecycle
WHERE last_processed_day<$day AND EXISTS (
 SELECT 1 FROM presence a JOIN presence b ON a.group_key=b.group_key
 WHERE a.hero=lower(lifecycle.hero_a_id) AND b.hero=lower(lifecycle.hero_b_id)
);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key,romance_stage FROM relationship_pair_lifecycle
WHERE last_processed_day<$day AND flirt_attempted=1 AND flirt_passed=0
AND lover_active=0;", parameters, transaction);
        }
    }
}
