using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> SelectRelationshipStoryThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_story_threads WHERE subject_id=$subject AND target_id=$target AND kind=$kind LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipStoryThreads2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_story_threads WHERE pair_key=$pair AND status='active';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipStorySeededPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT pair_key FROM relationship_story_seeded_pairs WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPassiveRelationshipEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM passive_relationship_events WHERE world_day >= $day AND ((hero_a_id=$a AND hero_b_id=$b) OR (hero_a_id=$b AND hero_b_id=$a)) ORDER BY world_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipMilestones(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,target_id,kind FROM relationship_milestones WHERE status='active' AND ((subject_id=$a AND target_id=$b) OR (subject_id=$b AND target_id=$a));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPressures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,target_id,kind,intensity FROM relationship_pressures WHERE status='active' AND updated_day >= $day AND ((subject_id=$a AND target_id=$b) OR (subject_id=$b AND target_id=$a));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDevelopments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT subject_id,target_id,kind FROM relationship_developments WHERE status IN ('ready','claimed') AND available_day >= $day AND ((subject_id=$a AND target_id=$b) OR (subject_id=$b AND target_id=$a));", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipStorySeededPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR IGNORE INTO relationship_story_seeded_pairs(pair_key,seeded_day,created_ts) VALUES($pair,$day,$ts);", parameters, transaction);
        }

        internal static void UpsertRelationshipStoryThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_story_threads(thread_key,pair_key,subject_id,target_id,kind,route,intensity,status,qualifying_events,last_event_type,last_event_id,last_event_day,payload_json,created_ts,updated_ts)
VALUES($key,$pair,$subject,$target,$kind,$route,$intensity,$status,$qualifying,$eventType,$event,$day,$payload,$ts,$ts)
ON CONFLICT(subject_id,target_id,kind) DO UPDATE SET route=CASE WHEN $route='' THEN relationship_story_threads.route ELSE $route END,
intensity=$intensity,status=$status,qualifying_events=$qualifying,last_event_type=$eventType,last_event_id=$event,last_event_day=$day,payload_json=$payload,updated_ts=$ts;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipMilestones(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_milestones(milestone_id,subject_id,target_id,kind,status,stability,strength,reason,created_event_id,last_event_id,evidence_json,created_day,last_meaningful_day,updated_ts,payload_json)
VALUES($id,$subject,$target,'feud','active',75,80,'Repeated political conflict escalated into an active feud.',$event,$event,$evidence,$day,$day,$ts,$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipStoryThreads3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT thread_key FROM relationship_story_threads WHERE pair_key=$pair AND last_event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairLifecycle2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM relationship_pair_lifecycle
WHERE lover_active=1
  AND ((hero_a_id=$a AND hero_b_id<>$b) OR (hero_b_id=$a AND hero_a_id<>$b)
    OR (hero_a_id=$b AND hero_b_id<>$a) OR (hero_b_id=$b AND hero_a_id<>$a));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipStoryThreads4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT target_id FROM relationship_story_threads WHERE subject_id=$subject AND kind='romance' AND route='courtship' AND status='active' AND target_id<>$spouse;", parameters, transaction);
        }
    }
}
