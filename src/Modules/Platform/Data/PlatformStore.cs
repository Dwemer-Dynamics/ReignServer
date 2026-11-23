using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Platform module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class PlatformStore
    {
        internal static List<Dictionary<string, object>> CountWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND completed=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSocialOutcomeReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (completed=-1 OR attempt_count>=3);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS total,
SUM(CASE WHEN status='processing' THEN 1 ELSE 0 END) AS processing,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='processing' AND claim_expires_ts<$ts THEN 1 ELSE 0 END) AS expired
FROM social_subject_work_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPublicStandingInvalidations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM public_standing_invalidations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status IN ('pending','processing');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND calculation_status='error';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets
WHERE status='failed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static void UpdateSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_outcome_receipts
SET completed=0,attempt_count=0,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (completed=-1 OR attempt_count>=3);", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue
SET status='pending',claim_owner='',claim_expires_ts=0,last_error='',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status<>'completed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT campaign_id,timeline_id,head_sequence
FROM world_history_timelines
WHERE campaign_id=$campaign AND timeline_id=$timeline
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_native_targets;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT target_count,client_failed,client_remaining
FROM relationship_native_sync_batches
WHERE campaign_id=$campaign AND timeline_id=$timeline AND plan_id=$plan
LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertInitializationReadinessSeals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO initialization_readiness_seals(
campaign_id,timeline_id,seal_version,generation_id,history_sequence,
relationship_plan_id,acknowledged_utc)
VALUES($campaign,$timeline,$version,$generation,$sequence,$plan,$utc)
ON CONFLICT(campaign_id,timeline_id,seal_version) DO UPDATE SET
generation_id=$generation,history_sequence=MAX(initialization_readiness_seals.history_sequence,$sequence),
relationship_plan_id=$plan,acknowledged_utc=$utc;", parameters, transaction);
        }

        internal static void UpdateRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs
SET status='pending',last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='initialization_pending';", parameters, transaction);
        }

        internal static void InsertRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_sync_batches(
plan_id,campaign_id,timeline_id,world_day,target_count,issued_ts,
client_applied,client_remaining,client_failed)
VALUES($plan,$campaign,$timeline,1,1,1,1,0,0);", parameters, transaction);
        }

        internal static void InsertRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_native_targets(
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation,status,
world_day,last_sync_day,attempt_count,claimed_ts,last_error,updated_ts)
VALUES('later-a|later-b','later-a','later-b',3,0,'pending',
1,-1000,0,0,'',1);", parameters, transaction);
        }

        internal static void InsertRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_daily_inputs(
campaign_id,timeline_id,day_key,world_day,status,presence_groups_json,
hero_changes_json,group_count,received_ts)
VALUES($campaign,$timeline,1,1,'initialization_pending','[]','[]',0,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT status FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=1;", parameters, transaction);
        }

        internal static void DeleteRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM relationship_native_targets WHERE pair_key='later-a|later-b';", parameters, transaction);
        }

        internal const string SelectSql = "SELECT pg_advisory_xact_lock(hashtextextended(current_schema(), 9137));";
    }
}
