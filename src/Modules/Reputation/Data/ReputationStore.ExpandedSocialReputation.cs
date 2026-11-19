using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectSqliteMaster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT sql FROM sqlite_master WHERE type='table' AND name='rumor_occurrences' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='expanded_social_occurrence_keys_v2' LIMIT 1;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static void InsertRumorOccurrencesV2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rumor_occurrences_v2
SELECT occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,catalog_revision,participants_json,
provenance_summary,snapshot_json,created_ts,updated_ts FROM rumor_occurrences;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('expanded_social_occurrence_keys_v2','1');", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='rumor_occurrences' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='builtin_rumor_duration_45_v2' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_occurrences
SET expires_day=MAX(expires_day,world_day+45),updated_ts=$ts
WHERE status='active' AND archetype_id IN ('affair','marital_strife');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('builtin_rumor_duration_45_v2','1');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM social_outcome_receipts WHERE event_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(event_id,campaign_id,timeline_id,subject_id,archetype_id,updated_ts)
VALUES($event,$campaign,$timeline,$subject,$archetype,$ts);", parameters, transaction);
        }

        internal static void UpdateSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET counter_applied=1,completed=1,result_json=$result,updated_ts=$ts WHERE event_id=$event;", parameters, transaction);
        }

        internal static void UpdateSocialOutcomeReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET counter_applied=1,updated_ts=$ts WHERE event_id=$event;", parameters, transaction);
        }

        internal static void UpdateSocialOutcomeReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE social_outcome_receipts SET completed=1,result_json=$result,updated_ts=$ts WHERE event_id=$event;", parameters, transaction);
        }

        internal static void UpsertSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_outcome_receipts(
event_id,campaign_id,timeline_id,attempt_count,last_error,completed,result_json,updated_ts)
VALUES($event,$campaign,$timeline,1,$error,0,$result,$ts)
ON CONFLICT(event_id) DO UPDATE SET
attempt_count=social_outcome_receipts.attempt_count+1,
last_error=excluded.last_error,
completed=CASE WHEN social_outcome_receipts.attempt_count+1>=3 THEN -1 ELSE 0 END,
result_json=excluded.result_json,updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static void UpdateReputationCounterevidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_counterevidence SET consecutive_count=0,last_event_id=$event,
last_outcome_day=$day,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='countered',updated_ts=$ts
WHERE subject_id=$subject AND tag_id=$tag AND status='active';", parameters, transaction);
        }

        internal static void DeleteSharedTagExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM shared_tag_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;", parameters, transaction);
        }

        internal static void DeleteRumorExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM rumor_exposure_streaks
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id=$archetype AND thread_key=$thread;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationCounterevidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM reputation_counterevidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertReputationCounterevidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO reputation_counterevidence(campaign_id,timeline_id,subject_id,tag_id,
consecutive_count,last_event_id,last_outcome_day,last_chance,last_roll,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$count,$event,$day,$chance,$roll,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
consecutive_count=$count,last_event_id=$event,last_outcome_day=$day,last_chance=$chance,last_roll=$roll,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='removed_by_counterevidence',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';", parameters, transaction);
        }

        internal static void InsertOrIgnoreReputationLifecycleEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO reputation_lifecycle_events(lifecycle_id,campaign_id,timeline_id,
subject_id,tag_id,event_type,source_event_id,world_day,details_json,created_ts)
VALUES($id,$campaign,$timeline,$subject,$tag,'removed_by_counterevidence',$event,$day,$details,$ts);", parameters, transaction);
        }

        internal static void UpdateReputationCounterevidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_counterevidence SET consecutive_count=0,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,snapshot_json FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active';", parameters, transaction);
        }

        internal static void UpdateCharacterReputations7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='derived_requirement_lost',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';", parameters, transaction);
        }

        internal static void UpsertCharacterReputations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$source,'derived','derived',$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
source_occurrence_id=$source,archetype_id='derived',subject_role='derived',description=$description,
reputation_value=$value,acquired_day=$day,catalog_revision=$revision,snapshot_json=$snapshot,status='active',updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT description FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active'
AND subject_role<>'derived' ORDER BY acquired_day DESC LIMIT 20;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(e.world_day) AS world_day
FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3));", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue
SET status='pending',claim_owner='',claim_expires_ts=0,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='processing' AND claim_expires_ts<$ts;", parameters, transaction);
        }

        internal static void SelectGrouped(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH pending AS (
 SELECT e.sequence,e.subject_id
 FROM world_history_events e
 WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
 AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
  WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
), grouped AS (
 SELECT subject_id,MIN(sequence) AS first_sequence,COUNT(*) AS pending_count
 FROM pending WHERE subject_id<>'' GROUP BY subject_id
)
INSERT INTO social_subject_work_queue(
 campaign_id,timeline_id,subject_id,status,first_sequence,pending_count,
 claim_owner,claim_expires_ts,updated_ts,last_error)
SELECT $campaign,$timeline,subject_id,'pending',first_sequence,pending_count,
       '',0,$ts,'' FROM grouped
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
 first_sequence=excluded.first_sequence,pending_count=excluded.pending_count,
 status=CASE WHEN social_subject_work_queue.status='processing'
  AND social_subject_work_queue.claim_expires_ts>$ts THEN 'processing'
  ELSE 'pending' END,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS event_count,
       COUNT(DISTINCT e.subject_id)
         FILTER (WHERE e.subject_id<>'')
         AS subject_count
FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT e.sequence,e.event_id,e.payload_json
FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
ORDER BY e.sequence,e.event_id;", parameters, transaction);
        }

        internal static void UpsertSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_subject_work_queue(
campaign_id,timeline_id,subject_id,status,first_sequence,pending_count,
claim_owner,claim_expires_ts,updated_ts,last_error)
VALUES($campaign,$timeline,$subject,'pending',$sequence,$count,'',0,$ts,'')
ON CONFLICT(campaign_id,timeline_id,subject_id) DO UPDATE SET
first_sequence=excluded.first_sequence,pending_count=excluded.pending_count,
status=CASE WHEN social_subject_work_queue.status='processing'
 AND social_subject_work_queue.claim_expires_ts>$ts THEN 'processing'
 ELSE 'pending' END,updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue SET
status='pending',claim_owner='',claim_expires_ts=0,updated_ts=$ts,last_error=$error
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT subject_id FROM social_subject_work_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (status='pending' OR (status='processing' AND claim_expires_ts<$ts))
ORDER BY first_sequence,subject_id
LIMIT 1 FOR UPDATE SKIP LOCKED;", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue SET
status='processing',claim_owner=$owner,claim_expires_ts=$expires,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND (status='pending' OR claim_expires_ts<$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialSubjectWorkQueue2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
last_world_day,last_event_id FROM social_subject_work_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT e.sequence,e.event_id,e.world_day,e.event_type,e.summary,e.payload_json
FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND e.subject_id=$subject
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
ORDER BY e.sequence,e.event_id LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT e.sequence,e.event_id,e.world_day,e.event_type,e.summary,e.payload_json
FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
ORDER BY e.sequence,e.event_id;", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue SET
needs_finalize=1,last_world_day=$day,last_event_id=$event,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue SET
status=$status,claim_owner='',claim_expires_ts=0,pending_count=$count,
updated_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialSubjectWorkQueue3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT subject_id,last_world_day,last_event_id
FROM social_subject_work_queue
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='completed' AND needs_finalize<>0
ORDER BY first_sequence,subject_id
LIMIT $limit;", parameters, transaction);
        }

        internal static void UpdateSocialSubjectWorkQueue6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE social_subject_work_queue SET
needs_finalize=0,updated_ts=$ts,last_error=''
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND status='completed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT 1 AS present FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND e.subject_id=$subject
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3))
LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT e.payload_json FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (SELECT 1 FROM social_outcome_receipts r
 WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT e.event_id,e.world_day,e.event_type,e.summary,e.payload_json
FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (
    SELECT 1 FROM social_outcome_receipts r
    WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3)
)
ORDER BY e.sequence,e.event_id LIMIT $limit;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT 1 FROM world_history_events e
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome'
AND NOT EXISTS (
    SELECT 1 FROM social_outcome_receipts r
    WHERE r.event_id=e.event_id AND (r.completed<>0 OR r.attempt_count>=3)
)
UNION ALL
SELECT 1 FROM social_subject_work_queue q
WHERE q.campaign_id=$campaign AND q.timeline_id=$timeline
AND q.needs_finalize<>0
LIMIT 1;", parameters, transaction);
        }
    }
}
