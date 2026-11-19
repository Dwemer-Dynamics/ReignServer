using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void InsertOrIgnoreConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conception_contexts(timeline_id,pregnancy_id,facts_json,world_day)
SELECT $new,pregnancy_id,facts_json,world_day FROM conception_contexts WHERE timeline_id=$old AND world_day<=$day
ON CONFLICT(timeline_id,pregnancy_id) DO NOTHING;
INSERT INTO conception_knowledge(timeline_id,pregnancy_id,observer_id,knowledge_json,source_id,source_hash,world_day)
SELECT $new,pregnancy_id,observer_id,knowledge_json,source_id,source_hash,world_day FROM conception_knowledge
WHERE timeline_id=$old AND world_day<=$day ON CONFLICT(timeline_id,pregnancy_id,observer_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionKnowledge(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT knowledge_json FROM conception_knowledge WHERE timeline_id=$timeline AND pregnancy_id=$id AND observer_id=$observer AND world_day<=$day;", parameters, transaction);
        }

        internal static void UpsertConceptionKnowledge(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conception_knowledge(timeline_id,pregnancy_id,observer_id,knowledge_json,source_id,source_hash,world_day)
VALUES($timeline,$id,$observer,$json,$source,$hash,$day) ON CONFLICT(timeline_id,pregnancy_id,observer_id) DO UPDATE SET
knowledge_json=$json,source_id=$source,source_hash=$hash,world_day=$day;", parameters, transaction);
        }

        internal static void DeleteWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM world_history_knowledge_rules WHERE acquisition_mode='realm_news'
AND event_id IN (SELECT event_id FROM world_history_events WHERE event_type='child_conceived' AND timeline_id=$timeline);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pregnancy_id,facts_json FROM conception_contexts WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpdateConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE conception_contexts SET facts_json=$json,world_day=$day WHERE timeline_id=$timeline AND pregnancy_id=$id;", parameters, transaction);
        }

        internal static void UpsertConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO conception_contexts(timeline_id,pregnancy_id,facts_json,world_day)
VALUES($timeline,$id,$json,$day) ON CONFLICT(timeline_id,pregnancy_id) DO UPDATE SET facts_json=$json,world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionContexts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT facts_json FROM conception_contexts WHERE timeline_id=$timeline AND world_day<=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationLifecycleEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT lifecycle_id FROM reputation_lifecycle_events WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND source_event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptionContexts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT facts_json FROM conception_contexts WHERE timeline_id=$timeline AND pregnancy_id=$id;", parameters, transaction);
        }
    }
}
