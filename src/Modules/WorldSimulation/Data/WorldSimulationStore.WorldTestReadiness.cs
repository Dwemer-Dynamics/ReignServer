using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static void Set(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET TRANSACTION READ ONLY;", parameters, transaction);
        }

        internal static void Set2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET LOCAL lock_timeout='200ms';", parameters, transaction);
        }

        internal static void Set3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SET LOCAL statement_timeout='3000ms';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeSyncBatches2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT client_failed FROM relationship_native_sync_batches WHERE timeline_id=$timeline ORDER BY world_day DESC,issued_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal const string SelectWorldTestNativeHeartbeatsSql = "SELECT MAX(world_day) AS day FROM world_test_native_heartbeats WHERE timeline_id=$timeline;";

        internal static string CountRelationshipNativeTargetsSql(object nativeFilter)
        {
            return "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed')" + nativeFilter + ";";
        }

        internal static string CountRelationshipNativeTargetsSql2(object nativeFilter)
        {
            return "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status='failed'" + nativeFilter + ";";
        }

        internal const string SelectRelationshipDailyInputsSql = "SELECT MAX(world_day) AS day FROM relationship_daily_inputs WHERE timeline_id=$timeline;";

        internal const string SelectWorldTestDailyRollupsSql = "SELECT MAX(world_day) AS day FROM world_test_daily_rollups WHERE campaign_id=$campaign AND timeline_id=$timeline;";

        internal const string SelectRelationshipPairChemistrySql = "SELECT MAX(last_native_sync_day) AS day FROM relationship_pair_chemistry WHERE last_native_sync_day>=0;";

        internal static string CountRelationshipNativeTargetsSql3(object nativeFilter)
        {
            return "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed') AND $latest-world_day>1" + nativeFilter + ";";
        }

        internal const string CountPoliticalPressureNativeEffectsSql = "SELECT COUNT(*) AS count FROM political_pressure_native_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='pending';";

        internal const string CountPoliticalPressureNativeEffectsSql2 = "SELECT COUNT(*) AS count FROM political_pressure_native_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='failed';";

        internal const string CountClanConflictRelationshipEffectsSql = "SELECT COUNT(*) AS count FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status IN ('pending','applying');";

        internal const string CountClanConflictRelationshipEffectsSql2 = "SELECT COUNT(*) AS count FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='failed';";

        internal const string CountClanConflictIncidentsSql = "SELECT COUNT(*) AS count FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='failed';";

        internal const string CountClanConflictIncidentsSql2 = "SELECT COUNT(*) AS count FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline AND notice_status IN ('ready','fetched');";
    }
}
