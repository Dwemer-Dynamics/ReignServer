using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Reputation module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='reputation_reasons_are_recorded_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES('reputation_reasons_are_recorded_v1','1')
ON CONFLICT(key) DO UPDATE SET value='1';", parameters, transaction);
        }

        internal static void InsertReputationActivations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO reputation_activations(activation_id,campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,source_event_id,activation_day,description,incident_description,evidence_json,reason_text,reason_status,
created_ts,updated_ts)
VALUES($activation,$campaign,$timeline,$subject,$tag,$occurrence,$event,$day,$description,$incident,$evidence,$fallback,'recorded',$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations
SET reason_activation_id=$activation,reason_text=$fallback,reason_status='recorded',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT provenance_summary FROM rumor_occurrences WHERE occurrence_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateReputationActivations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_activations
SET reason_text=COALESCE(NULLIF(incident_description,''),NULLIF(description,''),'A recorded event established this reputation.'),
reason_status='recorded',provider='',model='',last_error='',updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations
SET reason_text=COALESCE(NULLIF((SELECT a.incident_description FROM reputation_activations a
    WHERE a.activation_id=character_reputations.reason_activation_id),''),NULLIF(reason_text,''),NULLIF(description,''),'A recorded event established this reputation.'),
reason_status='recorded',updated_ts=$ts
WHERE reason_activation_id<>'';", parameters, transaction);
        }

        internal static void UpdateReputationReasonJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_reason_jobs
SET status='retired',last_error='Background LLM reason generation was retired; the recorded incident is authoritative.',updated_ts=$ts
WHERE status<>'retired';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT rst.tag_id,rst.subject_role,rst.description,
rst.rumor_value,rst.snapshot_json,ro.occurrence_id,ro.archetype_id,ro.source_event_id,ro.world_day,ro.expires_day,
ro.provenance_summary,ro.snapshot_json AS occurrence_snapshot_json
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline AND rst.subject_id=$subject
AND rst.status='active' AND ro.status='active' AND ro.expires_day>$day
ORDER BY ro.world_day DESC,ro.created_ts DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,description,reputation_value,
acquired_day,reason_text,reason_status,reason_activation_id,snapshot_json,archetype_id,subject_role
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND status='active'
ORDER BY acquired_day DESC,updated_ts DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='world_test_native_heartbeats';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM world_test_native_heartbeats ORDER BY world_day DESC,updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT timeline_id FROM rumor_occurrences ORDER BY world_day DESC,updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(world_day) AS day FROM world_test_native_heartbeats WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationActivations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT activation_id FROM reputation_activations WHERE campaign_id=$campaign AND source_event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id=$tag AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'','manual_control_center','manual',$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET source_occurrence_id='',archetype_id='manual_control_center',
subject_role='manual',description=$description,reputation_value=$value,acquired_day=$day,catalog_revision=$revision,
snapshot_json=$snapshot,status='active',updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationActivations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT incident_description,description FROM reputation_activations WHERE activation_id=$activation LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateReputationActivations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_activations
SET reason_text=$reason,reason_status='recorded',provider='',model='',last_error='',updated_ts=$ts
WHERE activation_id=$activation;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations
SET reason_text=$reason,reason_status='recorded',updated_ts=$ts WHERE reason_activation_id=$activation;", parameters, transaction);
        }

        internal static void UpdateReputationReasonJobs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE reputation_reason_jobs
SET status='retired',last_error='Background LLM reason generation was retired.',updated_ts=$ts
WHERE activation_id=$activation;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }
    }
}
