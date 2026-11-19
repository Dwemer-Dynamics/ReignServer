using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class ReputationStore
    {
        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id IN ($female,$male)
ORDER BY sequence DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id IN ($first,$second)
ORDER BY event_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='the_unchaste' AND source_event_id IN ($first,$second)
ORDER BY source_event_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE tag_id='the_unchaste' AND occurrence_id IN ($first,$second)
ORDER BY subject_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND tag_id='the_unchaste' AND subject_id IN ($female,$male)
ORDER BY subject_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND correlation_id=$source ORDER BY sequence DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,
completed,attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='ruler_favoring_presence' AND source_event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id=$occurrence AND tag_id LIKE 'ruler_favoring:%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$ruler AND status='active' AND tag_id LIKE 'ruler_favoring:%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,is_player,is_ruler,is_alive,is_adult,clan_tier
FROM identity_roster WHERE hero_id=$player LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlayerFavoringExchanges2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT ruler_id,favorite_id,exchange_count,last_exchange_id,last_world_day
FROM court_player_favoring_exchanges WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlayerFavoringExchangeReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT exchange_id,world_day,created_ts
FROM court_player_favoring_exchange_receipts WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ruler_id=$ruler AND favorite_id=$favorite ORDER BY created_ts,exchange_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,thread_key,source_event_id,status,
exposure_chance,exposure_roll,participants_json,provenance_summary FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline AND archetype_id='ruler_favoring_dialogue'
AND thread_key=$thread;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,correlation_id,sequence,world_day,payload_json
FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)
ORDER BY sequence;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialOutcomeReceipts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_id,subject_id,archetype_id,completed,
attempt_count,last_error,result_json,updated_ts FROM social_outcome_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_id IN (SELECT event_id FROM world_history_events WHERE campaign_id=$campaign
AND timeline_id=$timeline AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second))
ORDER BY event_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='ruler_favoring_presence'
AND source_event_id IN (SELECT event_id FROM world_history_events WHERE campaign_id=$campaign
AND timeline_id=$timeline AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second))
ORDER BY occurrence_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign
AND timeline_id=$timeline AND archetype_id='ruler_favoring_presence' AND source_event_id IN
(SELECT event_id FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)))
AND tag_id LIKE 'ruler_favoring:%' ORDER BY occurrence_id,tag_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations13(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$ruler AND status='active' AND tag_id LIKE 'ruler_favoring:%'
AND source_occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE campaign_id=$campaign
AND timeline_id=$timeline AND archetype_id='ruler_favoring_presence' AND source_event_id IN
(SELECT event_id FROM world_history_events WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='social_outcome' AND (correlation_id=$first OR correlation_id=$second)))
ORDER BY tag_id,source_occurrence_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pair_chemistry WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations14(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,
COUNT(*) AS favoring_count FROM character_reputations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND tag_id LIKE 'ruler_favoring:%' GROUP BY subject_id
ORDER BY favoring_count DESC,subject_id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,kingdom_id,
current_charm FROM identity_roster WHERE hero_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations15(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(acquired_day) AS day
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND status='active' AND tag_id LIKE 'ruler_favoring:%';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,kingdom_id,is_player
FROM identity_roster WHERE is_alive=1 AND is_adult=1 AND kingdom_id=$kingdom
AND hero_id<>$subject ORDER BY hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT hero_id FROM identity_roster WHERE is_player=1 LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster10(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND kingdom_id<>'' AND kingdom_id<>$kingdom
ORDER BY hero_id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,source_event_id,
world_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary,snapshot_json
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='the_unchaste' AND source_event_id=$event LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,subject_id,tag_id,subject_role,
rumor_value,reputation_value,status,snapshot_json FROM rumor_subject_tags
WHERE occurrence_id=$occurrence AND subject_id=$subject AND tag_id='the_unchaste' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations16(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,tag_id,source_occurrence_id,
archetype_id,subject_role,description,reputation_value,acquired_day,status,snapshot_json
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id=$subject AND tag_id='the_unchaste' AND source_occurrence_id=$occurrence LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster11(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,is_player,is_alive,is_adult,sex,
kingdom_id,current_charm FROM identity_roster WHERE hero_id=$subject LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSocialSignalEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT exchange_id,signal_id,
session_id,signal_type,speaker_id,target_id,supporting_quote,accepted,rejection_reason,evidence_json
FROM court_social_signal_evidence WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY exchange_id,signal_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtConversationSignalReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT session_id,signal_key,
signal_type,speaker_id,target_id,result_json FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY session_id,signal_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlayerFavoringExchanges3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT ruler_id,favorite_id,
exchange_count,last_exchange_id,last_world_day FROM court_player_favoring_exchanges
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY ruler_id,favorite_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtPlayerFavoringExchangeReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT ruler_id,favorite_id,
exchange_id,world_day FROM court_player_favoring_exchange_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY ruler_id,favorite_id,exchange_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtIntimacyExposureReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT signal_id,player_id,partner_id,
eligible,exposed,probability,roll,result_json FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY signal_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFlirtTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,target_id,
first_session_id,first_world_day,last_session_id,last_world_day,incident_count FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY subject_id,target_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
AND (tag_id='favored_by' OR tag_id LIKE 'favored_by:%');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM rumor_subject_tags tags JOIN rumor_occurrences occurrences
ON occurrences.occurrence_id=tags.occurrence_id
WHERE occurrences.campaign_id=$campaign AND occurrences.timeline_id=$timeline AND tags.status='active'
AND (tags.tag_id='favored_by' OR tags.tag_id LIKE 'favored_by:%');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM schema_meta WHERE key='ruler_favoring_supersession_v1' AND value='1' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM social_balance_runs WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSocialBalanceSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO social_balance_snapshots(
campaign_id,timeline_id,run_id,period_key,world_day,snapshot_json,created_ts)
VALUES($campaign,$timeline,$run,$period,$day,$snapshot,$now);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT rst.tag_id,
COUNT(*) AS occurrence_count,COUNT(DISTINCT rst.subject_id) AS subject_count,
SUM(rst.rumor_value) AS raw_effect_sum,MIN(ro.world_day) AS oldest_day,
MAX(ro.world_day) AS newest_day,MAX(ro.expires_day) AS latest_expiration_day
FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
AND ro.status='active' AND rst.status='active'
GROUP BY rst.tag_id ORDER BY rst.tag_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations17(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT tag_id,
COUNT(*) AS subject_count,SUM(reputation_value) AS raw_effect_sum,
MIN(acquired_day) AS oldest_day,MAX(acquired_day) AS newest_day
FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='active'
GROUP BY tag_id ORDER BY tag_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags9(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,
SUM(rumor_count) AS active_rumor_count,SUM(reputation_count) AS active_reputation_count,
SUM(rumor_effect) AS raw_rumor_effect,SUM(reputation_effect) AS raw_reputation_effect
FROM (
 SELECT rst.subject_id,COUNT(*) AS rumor_count,0 AS reputation_count,
 SUM(rst.rumor_value) AS rumor_effect,0 AS reputation_effect
 FROM rumor_subject_tags rst JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id
 WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
 AND ro.status='active' AND rst.status='active' GROUP BY rst.subject_id
 UNION ALL
 SELECT subject_id,0,COUNT(*),0,SUM(reputation_value)
 FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
 AND status='active' GROUP BY subject_id
) social GROUP BY subject_id ORDER BY subject_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDirectionalViews(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"WITH directional_views AS (
SELECT COALESCE(sb.standing_value,0) AS social_modifier
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id
UNION ALL
SELECT COALESCE(sa.standing_value,0) AS social_modifier
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
)
SELECT
COUNT(*) AS pair_count,
SUM(CASE WHEN social_modifier<0 THEN 1 ELSE 0 END) AS negative_count,
SUM(CASE WHEN social_modifier>0 THEN 1 ELSE 0 END) AS positive_count,
SUM(CASE WHEN social_modifier<=-20 THEN 1 ELSE 0 END) AS rebellion_pressure_count,
MIN(social_modifier) AS minimum_modifier,MAX(social_modifier) AS maximum_modifier,
AVG(social_modifier) AS average_modifier
FROM directional_views;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEffectivePairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"WITH effective_pairs AS (
SELECT
MAX(-100,MIN(100,p.affinity_a_to_b+COALESCE(sb.standing_value,0))) AS effective_a_to_b,
MAX(-100,MIN(100,p.affinity_b_to_a+COALESCE(sa.standing_value,0))) AS effective_b_to_a
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id
)
SELECT
COUNT(*) AS pair_count,
SUM(CASE WHEN effective_a_to_b<-20 OR effective_b_to_a<-20 THEN 1 ELSE 0 END) AS below_rebellion_threshold_pairs,
SUM(CASE WHEN effective_a_to_b<=-50 OR effective_b_to_a<=-50 THEN 1 ELSE 0 END) AS deeply_hostile_pairs,
MIN(MIN(effective_a_to_b,effective_b_to_a)) AS minimum_effective_affinity,
MAX(MAX(effective_a_to_b,effective_b_to_a)) AS maximum_effective_affinity
FROM effective_pairs;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationLifecycleEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_type,
COUNT(*) AS event_count,COUNT(DISTINCT subject_id) AS subject_count,
MIN(world_day) AS oldest_day,MAX(world_day) AS newest_day
FROM reputation_lifecycle_events WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY event_type ORDER BY event_type;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectReputationActivations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT reason_status,
COUNT(*) AS activation_count,AVG(generation_attempts) AS average_attempts
FROM reputation_activations WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY reason_status ORDER BY reason_status;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtDynamicReputationMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
subject_id,positive_count,negative_count,dynamic_value,calculated_day
FROM court_dynamic_reputation_metrics
WHERE campaign_id=$campaign AND timeline_id=$timeline AND domain='popularity'
ORDER BY subject_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceCaseResults(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM social_balance_case_results WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($run='' OR run_id=$run) AND ($case='' OR case_id=$case) ORDER BY case_id,period_key,updated_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT period_key,world_day,snapshot_json,created_ts
FROM social_balance_snapshots WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($run='' OR run_id=$run) ORDER BY world_day,period_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceLongitudinalCheckpoints(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT run_id,world_day,snapshot_json,created_ts
FROM social_balance_longitudinal_checkpoints WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($run='' OR run_id=$run) ORDER BY world_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceRuns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT 1 FROM social_balance_runs
WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline
AND save_prefix=$prefix AND main_hero_id=$hero AND status IN ('prepared','running','paused') LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceRuns3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM social_balance_runs
WHERE ($campaign='' OR campaign_id=$campaign) AND ($timeline='' OR timeline_id=$timeline)
ORDER BY updated_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceRuns4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM social_balance_runs WHERE run_id=$run LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceCaseResults2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM social_balance_case_results
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY case_id,period_key;", parameters, transaction);
        }

        internal static void UpsertSocialBalanceRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO social_balance_runs(
run_id,campaign_id,timeline_id,save_prefix,main_hero_id,status,manifest_json,created_ts,updated_ts)
VALUES($run,$campaign,$timeline,$prefix,$hero,'prepared',$manifest,
COALESCE((SELECT created_ts FROM social_balance_runs WHERE run_id=$run),$now),$now);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceRuns5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM social_balance_runs WHERE run_id=$run AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertSocialBalanceCaseResults(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_balance_case_results(
campaign_id,timeline_id,case_id,period_key,run_id,scenario_version,dependency_fingerprint,
status,attempt_count,evidence_json,first_started_ts,completed_ts,updated_ts)
VALUES($campaign,$timeline,$case,$period,$run,$version,$fingerprint,$status,1,$evidence,$now,
CASE WHEN $terminal=1 THEN $now ELSE 0 END,$now)
ON CONFLICT(campaign_id,timeline_id,case_id,period_key) DO UPDATE SET
run_id=excluded.run_id,scenario_version=excluded.scenario_version,
dependency_fingerprint=excluded.dependency_fingerprint,status=excluded.status,
attempt_count=social_balance_case_results.attempt_count+1,evidence_json=excluded.evidence_json,
completed_ts=excluded.completed_ts,updated_ts=excluded.updated_ts;", parameters, transaction);
        }

        internal static void UpdateSocialBalanceRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE social_balance_runs SET updated_ts=$now WHERE run_id=$run;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSocialSignalEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND exchange_id IN ($valid,$hypothetical)
ORDER BY created_ts,signal_id;", parameters, transaction);
        }

        internal static void UpsertSocialBalanceLongitudinalCheckpoints(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO social_balance_longitudinal_checkpoints(
campaign_id,timeline_id,run_id,world_day,snapshot_json,created_ts)
VALUES($campaign,$timeline,$run,$day,$snapshot,$now)
ON CONFLICT(campaign_id,timeline_id,run_id,world_day) DO UPDATE SET
snapshot_json=excluded.snapshot_json,created_ts=excluded.created_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceLongitudinalCheckpoints2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT world_day,snapshot_json,created_ts
FROM social_balance_longitudinal_checkpoints WHERE campaign_id=$campaign
AND timeline_id=$timeline AND run_id=$run ORDER BY world_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceSnapshots2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT snapshot_json
FROM social_balance_snapshots WHERE campaign_id=$campaign AND timeline_id=$timeline
AND run_id=$run AND period_key=$period LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSocialBalanceCaseResults3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM social_balance_case_results WHERE campaign_id=$campaign AND timeline_id=$timeline
AND case_id=$case AND period_key='once' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSocialSignalEvidence3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND exchange_id=$exchange
ORDER BY created_ts,signal_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtConversationSignalReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT session_id,signal_key,signal_type,speaker_id,target_id,
result_json,created_ts FROM court_conversation_signal_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND session_id=$session
AND speaker_id=$player AND target_id=$target ORDER BY created_ts,signal_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFlirtTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT subject_id,target_id,first_session_id,first_world_day,
last_session_id,last_world_day,incident_count,updated_ts FROM court_flirt_targets
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$player AND target_id=$target;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtSocialSignalEvidence4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT exchange_id,signal_id,signal_type,speaker_id,target_id,
supporting_quote,accepted,rejection_reason,evidence_json,created_ts
FROM court_social_signal_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtIntimacyExposureReceipts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT signal_id,player_id,partner_id,eligible,exposed,
probability,roll,result_json,created_ts FROM court_intimacy_exposure_receipts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT occurrence_id,archetype_id,thread_key,source_event_id,
world_day,expires_day,status,exposure_chance,exposure_roll,participants_json,provenance_summary
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='affair' AND source_event_id=$event;", parameters, transaction);
        }

        internal static void UpdateCourtIntimacyExposureReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE court_intimacy_exposure_receipts
SET exposed=1,result_json=$result WHERE campaign_id=$campaign AND timeline_id=$timeline AND signal_id=$signal;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectIdentityRoster12(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT 1 FROM identity_roster WHERE hero_id=$id AND is_alive=1 AND is_adult=1 LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterPublicStanding3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT standing_value
FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subject_id=$subject
LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,first_day,last_day,updated_ts)
VALUES($pair,$a,$b,$day,$day,$ts);", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, object affinityColumn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE relationship_pair_chemistry SET "
                    + affinityColumn + "=$value,last_day=MAX(last_day,$day),updated_ts=$ts WHERE pair_key=$pair;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
affinity_a_to_b,affinity_b_to_a,effective_affinity_a_to_b,effective_affinity_b_to_a,
projected_native_relation,native_action_pending,native_action_id
FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }
    }
}
