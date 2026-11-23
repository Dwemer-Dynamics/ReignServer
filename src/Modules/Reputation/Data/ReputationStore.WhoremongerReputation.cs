using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT r.campaign_id,r.timeline_id
FROM social_outcome_receipts r
JOIN world_history_events e ON e.event_id=r.event_id
WHERE ((r.completed=-1 AND r.last_error IN ($orderingError,$identityError))
 OR (r.completed=0 AND r.attempt_count=0 AND r.last_error=''
  AND r.subject_id='' AND r.archetype_id='' AND r.result_json='{}'
  AND e.correlation_id LIKE 'whoremonger_town_entry|%'))
AND e.subject_id<>'';", parameters, transaction);
        }

        internal static void UpdateSocialOutcomeReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_outcome_receipts SET
completed=0,attempt_count=0,last_error='',result_json='{}',updated_ts=$ts
WHERE completed=-1 AND last_error IN ($orderingError,$identityError);", parameters, transaction);
        }

        internal static void UpsertSocialSubjectWorkQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_subject_work_queue(
campaign_id,timeline_id,subject_id,status,first_sequence,pending_count,
claim_owner,claim_expires_ts,updated_ts,last_error)
SELECT $campaign,$timeline,e.subject_id,'pending',MIN(e.sequence),COUNT(*),'',0,$ts,''
FROM world_history_events e
WHERE e.campaign_id=$campaign AND e.timeline_id=$timeline
AND e.event_type='social_outcome' AND e.subject_id<>''
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
GROUP BY e.subject_id
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
first_sequence=excluded.first_sequence,pending_count=excluded.pending_count,
status=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN 'processing'
 ELSE 'pending' END,claim_owner=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN social_subject_work_queue.claim_owner
 ELSE '' END,claim_expires_ts=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN social_subject_work_queue.claim_expires_ts
 ELSE 0 END,updated_ts=$ts,last_error='';", parameters, transaction);
        }

        internal static void InsertOrIgnoreWhoremongerActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO whoremonger_activity(
campaign_id,timeline_id,subject_id,is_player,updated_ts)
VALUES($campaign,$timeline,$subject,$player,$ts) ON CONFLICT(campaign_id,timeline_id,subject_id) DO NOTHING;
UPDATE whoremonger_activity SET updated_ts=updated_ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT input_json,result_json FROM whoremonger_event_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT is_player,is_alive,is_adult FROM identity_roster WHERE hero_id=$subject LIMIT 1;", parameters, transaction);
        }

        internal static void InsertWhoremongerEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO whoremonger_event_receipts(campaign_id,timeline_id,event_id,
subject_id,town_id,world_day,input_json,result_json,created_ts)
VALUES($campaign,$timeline,$event,$subject,$town,$day,$input,$result,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertWhoremongerActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO whoremonger_activity(campaign_id,timeline_id,subject_id,
is_player,visit_count,last_activity_day,last_event_day,updated_ts)
VALUES($campaign,$timeline,$subject,$player,$count,$activity,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
is_player=$player,visit_count=$count,last_activity_day=$activity,last_event_day=$day,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences SET expires_day=$expires,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger'
AND thread_key=$subject||'|whoremonger' AND status='active';", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='superseded',updated_ts=$ts
WHERE subject_id=$subject AND tag_id='whoremonger' AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger')
AND (occurrence_id<>$occurrence OR EXISTS (SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND tag_id='whoremonger' AND status='active'));", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='expired',updated_ts=$ts
WHERE subject_id=$subject AND tag_id='whoremonger' AND status='active'
AND occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger');
UPDATE rumor_occurrences SET status='expired',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='whoremonger'
AND thread_key=$subject||'|whoremonger' AND status='active';
DELETE FROM rumor_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='whoremonger' AND thread_key=$subject||'|whoremonger';
DELETE FROM shared_tag_exposure_streaks WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id='whoremonger';
UPDATE whoremonger_activity SET visit_count=0,last_activity_day=-1,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWhoremongerActivity2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id FROM whoremonger_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline AND last_activity_day>=0
AND last_activity_day+30<=$day;", parameters, transaction);
        }
    }
}
