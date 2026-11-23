using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM schema_meta WHERE key='ruler_favoring_supersession_v1' LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rumor_subject_tags SET status='superseded'
WHERE status='active' AND (tag_id='favored_by' OR tag_id LIKE 'favored_by:%');", parameters, transaction);
        }

        internal static void UpdateCharacterReputations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations SET status='superseded_by_ruler_favoring',updated_ts=$ts
WHERE status='active' AND (tag_id='favored_by' OR tag_id LIKE 'favored_by:%');", parameters, transaction);
        }

        internal static void UpsertSchemaMeta2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value) VALUES('ruler_favoring_supersession_v1','1')
ON CONFLICT(key) DO UPDATE SET value=excluded.value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateCharacterReputations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE character_reputations
SET status='derived_requirement_lost',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND tag_id=$tag AND status='active';", parameters, transaction);
        }

        internal static void InsertOrIgnoreReputationLifecycleEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO reputation_lifecycle_events(
lifecycle_id,campaign_id,timeline_id,subject_id,tag_id,event_type,source_event_id,world_day,details_json,created_ts)
VALUES($id,$campaign,$timeline,$subject,$tag,'derived_requirement_lost',$event,$day,$details,$ts);", parameters, transaction);
        }

        internal static void UpsertCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(campaign_id,timeline_id,subject_id,tag_id,
source_occurrence_id,archetype_id,subject_role,description,reputation_value,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,'','court_dynamic','derived',$description,$value,$day,$revision,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO UPDATE SET
source_occurrence_id='',archetype_id='court_dynamic',subject_role='derived',description=$description,
reputation_value=$value,acquired_day=CASE WHEN character_reputations.status='active' THEN character_reputations.acquired_day ELSE $day END,
catalog_revision=$revision,snapshot_json=$snapshot,status='active',updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
SUM(CASE WHEN incoming_affinity>30 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN incoming_affinity<-30 THEN 1 ELSE 0 END) AS negative_count
FROM (
 SELECT observer.hero_id,
 CASE WHEN pair.hero_a_id=observer.hero_id THEN pair.affinity_a_to_b ELSE pair.affinity_b_to_a END AS incoming_affinity
 FROM identity_roster observer
 JOIN relationship_pair_chemistry pair
   ON (pair.hero_a_id=observer.hero_id AND pair.hero_b_id=$subject)
   OR (pair.hero_b_id=observer.hero_id AND pair.hero_a_id=$subject)
 WHERE observer.hero_id<>$subject AND observer.is_alive=1 AND observer.is_adult=1
 AND observer.is_lord=1 AND observer.is_player=0 AND observer.is_notable=0 AND observer.is_wanderer=0
 AND observer.kingdom_id=$kingdom AND observer.is_mercenary_clan=0
) qualified;", parameters, transaction);
        }

        internal static void UpsertCourtDynamicReputationMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_dynamic_reputation_metrics(
campaign_id,timeline_id,subject_id,domain,positive_count,negative_count,dynamic_value,evidence_json,calculated_day,updated_ts)
VALUES($campaign,$timeline,$subject,'popularity',$positive,$negative,0,$evidence,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,domain) DO UPDATE SET
positive_count=$positive,negative_count=$negative,evidence_json=$evidence,calculated_day=$day,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_lord=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRequested(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
WITH requested AS (
    SELECT value AS subject_id
    FROM jsonb_array_elements_text(CAST($subjects AS jsonb))
),
directed AS (
    SELECT pair.hero_b_id AS subject_id,
           pair.hero_a_id AS observer_id,
           pair.affinity_a_to_b AS incoming_affinity
    FROM relationship_pair_chemistry pair
    JOIN requested requested_subject
      ON requested_subject.subject_id=pair.hero_b_id
    UNION ALL
    SELECT pair.hero_a_id AS subject_id,
           pair.hero_b_id AS observer_id,
           pair.affinity_b_to_a AS incoming_affinity
    FROM relationship_pair_chemistry pair
    JOIN requested requested_subject
      ON requested_subject.subject_id=pair.hero_a_id
)
SELECT requested.subject_id,
CASE WHEN subject.is_alive=1 AND subject.is_adult=1
          AND subject.is_lord=1 AND subject.kingdom_id<>''
     THEN 1 ELSE 0 END AS eligible,
COALESCE(SUM(CASE
    WHEN subject.is_alive=1 AND subject.is_adult=1
     AND subject.is_lord=1 AND subject.kingdom_id<>''
     AND observer.hero_id IS NOT NULL
     AND observer.is_alive=1 AND observer.is_adult=1
     AND observer.is_lord=1 AND observer.is_player=0
     AND observer.is_notable=0 AND observer.is_wanderer=0
     AND observer.kingdom_id=subject.kingdom_id
     AND observer.is_mercenary_clan=0
     AND directed.incoming_affinity>30 THEN 1 ELSE 0 END),0)
     AS positive_count,
COALESCE(SUM(CASE
    WHEN subject.is_alive=1 AND subject.is_adult=1
     AND subject.is_lord=1 AND subject.kingdom_id<>''
     AND observer.hero_id IS NOT NULL
     AND observer.is_alive=1 AND observer.is_adult=1
     AND observer.is_lord=1 AND observer.is_player=0
     AND observer.is_notable=0 AND observer.is_wanderer=0
     AND observer.kingdom_id=subject.kingdom_id
     AND observer.is_mercenary_clan=0
     AND directed.incoming_affinity<-30 THEN 1 ELSE 0 END),0)
     AS negative_count
FROM requested
LEFT JOIN identity_roster subject
  ON subject.hero_id=requested.subject_id
LEFT JOIN directed
  ON directed.subject_id=requested.subject_id
LEFT JOIN identity_roster observer
  ON observer.hero_id=directed.observer_id
GROUP BY requested.subject_id,subject.is_alive,subject.is_adult,
         subject.is_lord,subject.kingdom_id
ORDER BY requested.subject_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT subject_id,tag_id
FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status='active'
AND subject_id IN (
    SELECT value FROM jsonb_array_elements_text(CAST($subjects AS jsonb)))
AND tag_id IN (
    'well_regarded','court_favorite','beloved_of_the_court',
    'ill_regarded','shunned_at_court','court_pariah');", parameters, transaction);
        }

        internal static void SelectX(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(CAST($rows AS jsonb)) AS r(
        campaign text,timeline text,subject text,positive integer,
        negative integer,evidence text,day double precision,ts bigint)
)
INSERT INTO court_dynamic_reputation_metrics(
campaign_id,timeline_id,subject_id,domain,positive_count,negative_count,
dynamic_value,evidence_json,calculated_day,updated_ts)
SELECT campaign,timeline,subject,'popularity',positive,negative,0,
       evidence,day,ts
FROM x
ON CONFLICT(campaign_id,timeline_id,subject_id,domain) DO UPDATE SET
positive_count=excluded.positive_count,
negative_count=excluded.negative_count,
evidence_json=excluded.evidence_json,
calculated_day=excluded.calculated_day,
updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlayerFavoringExchanges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_player_favoring_exchanges
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlayerFavoringExchangeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM court_player_favoring_exchange_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND ruler_id=$ruler AND favorite_id=$favorite
AND exchange_id=$exchange LIMIT 1;", parameters, transaction);
        }

        internal static void InsertCourtPlayerFavoringExchangeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_player_favoring_exchange_receipts(
campaign_id,timeline_id,ruler_id,favorite_id,exchange_id,world_day,created_ts)
VALUES($campaign,$timeline,$ruler,$favorite,$exchange,$day,$ts);", parameters, transaction);
        }

        internal static void UpsertCourtPlayerFavoringExchanges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_player_favoring_exchanges(
campaign_id,timeline_id,ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day,updated_ts)
VALUES($campaign,$timeline,$ruler,$favorite,$count,$exchange,$day,$ts)
ON CONFLICT(campaign_id,timeline_id,ruler_id,favorite_id) DO UPDATE SET
exchange_count=$count,last_exchange_id=$exchange,last_world_day=$day,updated_ts=$ts;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCourtSocialSignalEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_social_signal_evidence(
campaign_id,timeline_id,exchange_id,signal_id,session_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts)
VALUES($campaign,$timeline,$exchange,$signal,$session,$type,$speaker,$target,$quote,$accepted,$reason,$evidence,$ts)
ON CONFLICT(campaign_id,timeline_id,exchange_id,signal_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtIntimacyExposureReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal LIMIT 1;", parameters, transaction);
        }

        internal static void InsertCourtIntimacyExposureReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_intimacy_exposure_receipts(
campaign_id,timeline_id,signal_id,player_id,partner_id,eligible,exposed,probability,roll,result_json,created_ts)
VALUES($campaign,$timeline,$signal,$player,$partner,$eligible,$exposed,$probability,$roll,$result,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtConversationSignalReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND session_id=$session AND signal_key=$key LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFlirtTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND target_id=$target LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCourtFlirtTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject AND target_id<>$target;", parameters, transaction);
        }

        internal static void UpsertCourtFlirtTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_flirt_targets(
campaign_id,timeline_id,subject_id,target_id,first_session_id,first_world_day,last_session_id,last_world_day,incident_count,updated_ts)
VALUES($campaign,$timeline,$subject,$target,$session,$day,$session,$day,1,$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,target_id) DO UPDATE SET
last_session_id=$session,last_world_day=$day,
incident_count=court_flirt_targets.incident_count+1,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
AND tag_id='flirt' AND status='active' LIMIT 1;", parameters, transaction);
        }

        internal static void InsertCourtConversationSignalReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_conversation_signal_receipts(
campaign_id,timeline_id,session_id,signal_key,signal_type,speaker_id,target_id,result_json,created_ts)
VALUES($campaign,$timeline,$session,$key,'flirtation',$speaker,$target,$result,$ts);", parameters, transaction);
        }
    }
}
