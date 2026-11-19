using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationStore
    {
        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS pair_count,
SUM(CASE WHEN p.last_delta_a_to_b>0 THEN 1 ELSE 0 END)
 +SUM(CASE WHEN p.last_delta_b_to_a>0 THEN 1 ELSE 0 END) AS positive_rolls,
SUM(CASE WHEN p.last_delta_a_to_b<0 THEN 1 ELSE 0 END)
 +SUM(CASE WHEN p.last_delta_b_to_a<0 THEN 1 ELSE 0 END) AS negative_rolls,
SUM(CASE WHEN p.last_context_kind='party' THEN 1 ELSE 0 END) AS party_pairs,
SUM(CASE WHEN p.last_context_kind='settlement' THEN 1 ELSE 0 END) AS settlement_pairs,
SUM(CASE WHEN
  CASE
    WHEN p.affinity_a_to_b<=-70 THEN 'nemesis' WHEN p.affinity_a_to_b<=-50 THEN 'enemy'
    WHEN p.affinity_a_to_b<=-30 THEN 'rival' WHEN p.affinity_a_to_b<=-10 THEN 'irritant'
    WHEN p.affinity_a_to_b<=9 THEN 'neutral' WHEN p.affinity_a_to_b<=29 THEN 'acquaintance'
    WHEN p.affinity_a_to_b<=49 THEN 'friend' WHEN p.affinity_a_to_b<=69 THEN 'close_friend'
    WHEN p.affinity_a_to_b<=84 THEN 'devoted' ELSE 'bonded' END
  =
  CASE
    WHEN p.affinity_b_to_a<=-70 THEN 'nemesis' WHEN p.affinity_b_to_a<=-50 THEN 'enemy'
    WHEN p.affinity_b_to_a<=-30 THEN 'rival' WHEN p.affinity_b_to_a<=-10 THEN 'irritant'
    WHEN p.affinity_b_to_a<=9 THEN 'neutral' WHEN p.affinity_b_to_a<=29 THEN 'acquaintance'
    WHEN p.affinity_b_to_a<=49 THEN 'friend' WHEN p.affinity_b_to_a<=69 THEN 'close_friend'
    WHEN p.affinity_b_to_a<=84 THEN 'devoted' ELSE 'bonded' END
  THEN 1 ELSE 0 END) AS symmetric_pairs,
SUM(CASE WHEN p.first_day=$latestDay THEN 1 ELSE 0 END) AS new_pairs,
SUM(CASE WHEN (na.hero_id IS NULL)<>(nb.hero_id IS NULL) THEN 1 ELSE 0 END)
 AS noble_notable_pairs,
SUM(CASE WHEN p.mbti_a='' OR p.mbti_a='XXXX' OR p.mbti_b='' OR p.mbti_b='XXXX'
  THEN 1 ELSE 0 END) AS missing_mbti
FROM relationship_pair_chemistry p
LEFT JOIN notable_mbti_profiles na ON na.hero_id=p.hero_a_id
LEFT JOIN notable_mbti_profiles nb ON nb.hero_id=p.hero_b_id
WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM notable_mbti_profiles;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry p
LEFT JOIN notable_mbti_profiles a ON p.hero_a_id=a.hero_id
LEFT JOIN notable_mbti_profiles b ON p.hero_b_id=b.hero_id
WHERE ($player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player))
AND ((a.hero_id IS NOT NULL AND p.mbti_a<>a.mbti_type)
  OR (b.hero_id IS NOT NULL AND p.mbti_b<>b.mbti_type));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectHeroCounts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
WITH directions AS (
 SELECT p.pair_key,p.hero_a_id AS hero_id,p.affinity_a_to_b AS affinity,
        p.tag_a_to_b AS directional_tag
 FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION ALL
 SELECT p.pair_key,p.hero_b_id,p.affinity_b_to_a,p.tag_b_to_a
 FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
), category_edges AS (
 SELECT 'band' AS category_type,
   CASE WHEN affinity<=-70 THEN 'nemesis' WHEN affinity<=-50 THEN 'enemy'
        WHEN affinity<=-30 THEN 'rival' WHEN affinity<=-10 THEN 'irritant'
        WHEN affinity<=9 THEN 'neutral' WHEN affinity<=29 THEN 'acquaintance'
        WHEN affinity<=49 THEN 'friend' WHEN affinity<=69 THEN 'close_friend'
        WHEN affinity<=84 THEN 'devoted' ELSE 'bonded' END AS category_key,
   hero_id,pair_key,1 AS directional
 FROM directions
 UNION ALL
 SELECT 'tag',directional_tag,hero_id,pair_key,1 FROM directions
 WHERE directional_tag<>''
 UNION ALL
 SELECT 'tag',p.shared_tag,p.hero_a_id,p.pair_key,0
 FROM relationship_pair_chemistry p
 WHERE p.shared_tag<>'' AND ($player='' OR
   (p.hero_a_id<>$player AND p.hero_b_id<>$player))
 UNION ALL
 SELECT 'tag',p.shared_tag,p.hero_b_id,p.pair_key,0
 FROM relationship_pair_chemistry p
 WHERE p.shared_tag<>'' AND ($player='' OR
   (p.hero_a_id<>$player AND p.hero_b_id<>$player))
), hero_counts AS (
 SELECT category_type,category_key,hero_id,SUM(directional) AS directional_count
 FROM category_edges GROUP BY category_type,category_key,hero_id
), pair_counts AS (
 SELECT category_type,category_key,COUNT(DISTINCT pair_key) AS pair_count
 FROM category_edges GROUP BY category_type,category_key
)
SELECT h.category_type,h.category_key,COUNT(*) AS unique_npc_count,
SUM(h.directional_count) AS directional_edge_count,p.pair_count
FROM hero_counts h JOIN pair_counts p
 ON p.category_type=h.category_type AND p.category_key=h.category_key
GROUP BY h.category_type,h.category_key,p.pair_count;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDirections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
WITH directions AS (
 SELECT p.affinity_a_to_b AS affinity FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION ALL
 SELECT p.affinity_b_to_a FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
)
SELECT affinity,COUNT(*) AS directional_edge_count
FROM directions GROUP BY affinity;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
WITH heroes AS (
 SELECT p.hero_a_id AS hero_id FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION
 SELECT p.hero_b_id FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
)
SELECT COUNT(*) AS count FROM heroes;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT director_action_id,status,world_day,actor_id,target_id,payload_json,result_json
FROM relationship_director_actions WHERE action_type='marriage';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT hero_id,profile_json,last_observed_day
FROM relationship_observed_heroes;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT evaluation_id,hero_a_id,hero_b_id FROM marriage_evaluations WHERE route='arranged' AND status='executed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count
FROM relationship_pair_lifecycle lifecycle
JOIN relationship_observed_heroes observed_a
  ON observed_a.hero_id=lifecycle.hero_a_id
JOIN relationship_observed_heroes observed_b
  ON observed_b.hero_id=lifecycle.hero_b_id
WHERE lifecycle.lover_active=1 AND (
    (COALESCE(json_extract(observed_a.profile_json,'$.spouseId'),'')<>''
     AND COALESCE(json_extract(observed_a.profile_json,'$.spouseId'),'')<>lifecycle.hero_b_id)
 OR (COALESCE(json_extract(observed_b.profile_json,'$.spouseId'),'')<>''
     AND COALESCE(json_extract(observed_b.profile_json,'$.spouseId'),'')<>lifecycle.hero_a_id)
);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS rolls,SUM(passed) AS passes,
SUM(CASE WHEN status='passed_unmatched' THEN 1 ELSE 0 END) AS unmatched,
COUNT(DISTINCT world_day) AS observed_days
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE campaign_id=$campaign
AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS pairings,SUM(same_sex) AS same_sex,
SUM(CASE WHEN same_sex=0 THEN 1 ELSE 0 END) AS opposite_sex,
SUM(short_affair) AS short_affairs,SUM(fixed_discovered) AS fixed_discoveries,
SUM(pregnancy_discovered) AS pregnancy_discoveries,
SUM(CASE WHEN conception_attempt_id<>'' THEN 1 ELSE 0 END) AS conception_attempts,
SUM(conceived) AS conceptions,SUM(discovery_applied) AS discovered_encounters
FROM relationship_fling_encounters WHERE campaign_id=$campaign
AND timeline_id=$timeline AND status='completed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (judgment<0 OR judgment>40 OR chance_percent<>50-judgment
 OR roll<1 OR roll>100 OR passed<>CASE WHEN roll<=chance_percent THEN 1 ELSE 0 END);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingSparks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (chance_percent<>25 OR roll<1 OR roll>100
 OR passed<>CASE WHEN roll<=chance_percent THEN 1 ELSE 0 END);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingEncounters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters encounter
WHERE encounter.campaign_id=$campaign AND encounter.timeline_id=$timeline
AND (NOT EXISTS (SELECT 1 FROM relationship_fling_rolls roll
  WHERE roll.campaign_id=encounter.campaign_id
    AND roll.timeline_id=encounter.timeline_id
    AND roll.world_day=encounter.world_day AND roll.hero_id=encounter.hero_a_id
    AND roll.passed=1 AND roll.location_kind=encounter.location_kind
    AND roll.location_id=encounter.location_id)
 OR NOT EXISTS (SELECT 1 FROM relationship_fling_rolls roll
  WHERE roll.campaign_id=encounter.campaign_id
    AND roll.timeline_id=encounter.timeline_id
    AND roll.world_day=encounter.world_day AND roll.hero_id=encounter.hero_b_id
    AND roll.passed=1 AND roll.location_kind=encounter.location_kind
    AND roll.location_id=encounter.location_id)
 OR (EXISTS (SELECT 1 FROM relationship_fling_sparks spark_day
      WHERE spark_day.campaign_id=encounter.campaign_id
        AND spark_day.timeline_id=encounter.timeline_id
        AND spark_day.world_day=encounter.world_day)
     AND NOT EXISTS (SELECT 1 FROM relationship_fling_sparks spark
      WHERE spark.campaign_id=encounter.campaign_id
        AND spark.timeline_id=encounter.timeline_id
        AND spark.world_day=encounter.world_day
        AND spark.pair_key=encounter.pair_key AND spark.passed=1
        AND spark.location_kind=encounter.location_kind
        AND spark.location_id=encounter.location_id)));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectOrdered(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
WITH participation AS (
 SELECT hero_a_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
 UNION ALL
 SELECT hero_b_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
), ordered AS (
 SELECT hero_id,world_day,LAG(world_day) OVER(PARTITION BY hero_id ORDER BY world_day) AS prior_day
 FROM participation
)
SELECT COUNT(*) AS count FROM ordered
WHERE prior_day IS NOT NULL AND world_day-prior_day<7;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingEncounters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND same_sex=1
AND (conception_attempt_id<>'' OR conception_id<>'' OR conceived=1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectParticipation(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
WITH participation AS (
 SELECT hero_a_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
 UNION ALL
 SELECT hero_b_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed'
)
SELECT COUNT(*) AS count FROM (
 SELECT hero_id,world_day,COUNT(*) AS uses FROM participation
 GROUP BY hero_id,world_day HAVING COUNT(*)>1
) duplicates;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_fling_encounters
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,encounter_id DESC LIMIT 20;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,pair_key LIMIT 40;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_fling_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,hero_id LIMIT 40;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='affair';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipConceptionCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE status='married';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipConceptionCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE status='broke_up';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipConceptionCommitments3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE chance_a>90 OR chance_b>90 OR chance_a<>MIN(90,honor_a+10) OR chance_b<>MIN(90,honor_b+10);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipAffairCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_affair_commitments WHERE status='married';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipAffairCommitments2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_affair_commitments WHERE chance_a>30 AND spouse_a_id<>'' OR chance_b>30 AND spouse_b_id<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountParentage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM parentage WHERE revealed=0 AND is_illegitimate=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets
WHERE status IN ('pending','claimed')
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed') AND $latest-world_day>1
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_native_sync_batches
WHERE timeline_id=$timeline ORDER BY world_day DESC,issued_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets WHERE status='failed'
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_director_actions
WHERE action_type<>'native_relation' AND status='failed'
AND ($player='' OR (actor_id<>$player AND target_id<>$player));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipOpeningSeedRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_opening_seed_runs
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY seed_version DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='arranged_marriage_periods';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectArrangedMarriagePeriods(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT period_index,world_day,status FROM arranged_marriage_periods WHERE timeline_id=$t ORDER BY period_index DESC LIMIT 16;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestChunkCounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT day_key,counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subsystem='diplomacy' AND chunk_key LIKE 'evaluation_%'
ORDER BY day_key DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRumorOccurrences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS rumor_count,
COUNT(*) FILTER (WHERE status='active' AND expires_day<=$day) AS expired_active,
COALESCE(MAX(world_day),-1) AS last_occurrence_day
FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldHistoryEvents8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS source_count,
COUNT(*) FILTER (WHERE r.completed=1) AS processed_count,
COUNT(*) FILTER (WHERE r.completed<0 OR
 (COALESCE(r.completed,0)=0 AND COALESCE(r.attempt_count,0)>=3)) AS failed_count,
COUNT(*) FILTER (WHERE r.event_id IS NULL OR
 (r.completed=0 AND r.attempt_count<3)) AS pending_count,
COALESCE(MIN(e.world_day) FILTER (WHERE r.event_id IS NULL OR
 (r.completed=0 AND r.attempt_count<3)),-1) AS oldest_pending_day,
COALESCE(MAX(e.world_day) FILTER (WHERE r.completed=1),-1) AS last_processed_day
FROM world_history_events e
LEFT JOIN social_outcome_receipts r ON r.event_id=e.event_id
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT
(SELECT COUNT(*) FROM rumor_subject_tags rst JOIN rumor_occurrences ro
 ON ro.occurrence_id=rst.occurrence_id WHERE ro.campaign_id=$campaign
 AND ro.timeline_id=$timeline) AS tag_count,
(SELECT COUNT(*) FROM rumor_promotion_results rpr
 JOIN rumor_occurrences ro ON ro.occurrence_id=rpr.occurrence_id
 WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline) AS promotion_count,
(SELECT COUNT(*) FROM rumor_promotion_results rpr
 JOIN rumor_occurrences ro ON ro.occurrence_id=rpr.occurrence_id
 WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
 AND rpr.promoted=1) AS promoted_count,
(SELECT COUNT(*) FROM character_reputations
 WHERE campaign_id=$campaign AND timeline_id=$timeline) AS reputation_count;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT ro.occurrence_id AS rumor_id,ro.provenance_summary AS claim,
ro.status,ro.archetype_id AS archetype,
COUNT(DISTINCT rst.subject_id) AS subject_count,
COUNT(rst.occurrence_id) AS tag_count,
COALESCE(MAX(rpr.promoted),0) AS promoted,
COALESCE(MAX(rpr.streak_count),0) AS streak_count
FROM rumor_occurrences ro
LEFT JOIN rumor_subject_tags rst ON rst.occurrence_id=ro.occurrence_id
LEFT JOIN rumor_promotion_results rpr ON rpr.occurrence_id=ro.occurrence_id
WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline
GROUP BY ro.occurrence_id,ro.provenance_summary,ro.status,ro.archetype_id
ORDER BY COUNT(DISTINCT rst.subject_id) DESC,ro.world_day DESC
LIMIT 50;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionRecognitionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC, attempt_index DESC LIMIT 100;", parameters, transaction);
        }

        internal const string SelectRumorOccurrencesSql = "SELECT status AS key,COUNT(*) AS count FROM rumor_occurrences "
                + "WHERE campaign_id=$campaign AND timeline_id=$timeline GROUP BY status;";

        internal const string SelectRumorOccurrencesSql2 = "SELECT COALESCE(NULLIF(archetype_id,''),'other') AS key,COUNT(*) AS count "
                + "FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline "
                + "GROUP BY COALESCE(NULLIF(archetype_id,''),'other');";

        internal const string SelectRumorSubjectTagsSql = "SELECT rst.status AS key,COUNT(*) AS count FROM rumor_subject_tags rst "
                + "JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id "
                + "WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline GROUP BY rst.status;";

        /// <summary>Runs a statement this module composes at run time from text kept in this class.</summary>
        internal static List<Dictionary<string, object>> QuerySql(ReignDbConnection connection, string sql, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, sql, parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_daily_inputs
WHERE timeline_id=$timeline AND status IN ('pending','processing');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MAX(world_day) AS day FROM relationship_daily_inputs
WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT day_key,pair_cursor,candidate_pairs,processed_pairs,pair_day_evaluations,
cadence_shard,window_start_day,window_day_count,started_ts,last_error
FROM relationship_daily_inputs WHERE timeline_id=$timeline AND status='processing'
ORDER BY day_key LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='relationship_mbti_error' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT processing_shard,COUNT(*) AS count
FROM relationship_pair_chemistry
WHERE processing_shard BETWEEN 0 AND $maxShard
GROUP BY processing_shard
ORDER BY processing_shard;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MIN(world_day) AS day FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestChunkCounters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT day_key,counters_json FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subsystem='relationships'
AND chunk_key='daily_random_matching_summary'
ORDER BY day_key DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT MAX(world_day) AS day FROM rumor_occurrences;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEvents22(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(e.world_day) AS day
FROM world_history_events e JOIN social_outcome_receipts r ON r.event_id=e.event_id
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome' AND r.completed=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSqliteMaster3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT name FROM sqlite_master WHERE type='table' AND name='relationship_director_actions' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDirectorActions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_director_actions;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRebellionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM world_test_rebellion_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY rolled_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM world_test_rebellion_movements WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY updated_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM world_test_rebellion_memberships WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT payload_json FROM world_test_native_heartbeats ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key='mbti_relationship_last_processed_day' LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT value FROM schema_meta WHERE key=$key LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipDailyInputs3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(world_day) AS day
FROM relationship_daily_inputs WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(world_day) AS day
FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestDailyRollups(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(world_day) AS day
FROM world_test_daily_rollups WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountDynamic(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM " + table + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectDynamic(ReignDbConnection connection, object column, object table, object column2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT " + column + ",COUNT(*) AS count FROM " + table + " GROUP BY " + column2 + ";", parameters, transaction);
        }

        internal static void Savepoint2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SAVEPOINT pressure_observation_contract;", parameters, transaction);
        }

        internal static void InsertPoliticalPressureIncidents(ReignDbConnection connection, object part, object part2, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT INTO political_pressure_incidents (" + part
                    + ") VALUES (" + part2 + ");", parameters, transaction);
        }

        internal static void InsertPoliticalPressureActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_activity
(activity_id,campaign_id,timeline_id,world_day,incident_id,actor_kingdom_id,target_kingdom_id,event_type,created_ts) VALUES
('pressure_observation_main',$campaign,'pressure_main',1,'pressure_observation_incident','player','foreign','incident_completed',1),
('pressure_observation_orphan',$campaign,'pressure_main',2,'missing_incident','','','message_receipt',2),
('pressure_observation_other',$campaign,'pressure_other',1,'pressure_observation_incident','player','foreign','incident_completed',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM political_pressure_incidents ORDER BY incident_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM political_pressure_activity ORDER BY activity_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM political_pressure_state ORDER BY actor_kingdom_id,target_kingdom_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureDeltaReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM political_pressure_delta_receipts ORDER BY incident_id,actor_kingdom_id,target_kingdom_id;", parameters, transaction);
        }

        internal static void Rollback3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK TO SAVEPOINT pressure_observation_contract;", parameters, transaction);
        }

        internal static void Release2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "RELEASE SAVEPOINT pressure_observation_contract;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_native_heartbeats;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestDailyRollups(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_daily_rollups;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountWorldTestRebellionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM world_test_rebellion_rolls;", parameters, transaction);
        }

        internal static void InsertRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_pair_chemistry(
pair_key,hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a,tag_a_to_b,tag_b_to_a,shared_tag,
first_day,last_day,updated_ts) VALUES
('a|b','a','b',-70,85,'nemesis','bonded','',1,7,1),
('a|c','a','c',10,10,'acquaintance','acquaintance','',2,7,1),
('a|player_hero','a','player_hero',100,100,'bonded','bonded','',2,7,1);", parameters, transaction);
        }

        internal static void InsertRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_observed_heroes(
hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
VALUES
('a','{""heroStringId"":""a"",""isFemale"":false,""isAlive"":true,""spouseId"":""c""}','',1,7,1),
('c','{""heroStringId"":""c"",""isFemale"":true,""isAlive"":true,""spouseId"":""a""}','',1,7,1),
('unconfirmed_a','{""heroStringId"":""unconfirmed_a"",""isFemale"":false,""isAlive"":true,""spouseId"":""""}','',1,7,1),
('unconfirmed_b','{""heroStringId"":""unconfirmed_b"",""isFemale"":true,""isAlive"":true,""spouseId"":""""}','',1,7,1);", parameters, transaction);
        }

        internal static void InsertMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO marriage_evaluations(
evaluation_id,route,status,hero_a_id,hero_b_id,clan_a_id,clan_b_id,leader_a_id,leader_b_id,
world_day,score,leader_a_approved,leader_b_approved,resentment_a,resentment_b,payload_json,created_ts,updated_ts,timeline_id)
VALUES('arranged_executed','arranged','executed','a','b','clan_a','clan_b','leader_a','leader_b',
7,80,1,1,0,0,'{}',1,1,'main'),
('arranged_pending','arranged','approved','a','c','clan_a','clan_c','leader_a','leader_c',
7,70,1,1,0,0,'{}',1,1,'main'),
('arranged_player','arranged','executed','a','player_hero','clan_a','player_clan','leader_a','player_hero',
7,99,1,1,0,0,'{}',1,1,'main');", parameters, transaction);
        }

        internal static void InsertRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_director_actions(
director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,created_ts,resolved_ts)
VALUES
('romantic_completed','marriage','completed',7,'a','c',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""a|c""}',1,2),
('romantic_completed_duplicate','marriage','completed',7,'a','c',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""a|c""}',2,3),
('romantic_unconfirmed','marriage','completed',7,'unconfirmed_a','unconfirmed_b',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""unconfirmed_a|unconfirmed_b""}',2,3),
('romantic_pending','marriage','pending',7,'b','c',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""b|c""}',1,0),
('diplomatic_completed','marriage','completed',7,'leader_a','leader_b',
'{""source"":""world_diplomacy_director"",""route"":""alliance_marriage""}',1,2),
('romantic_player','marriage','completed',7,'a','player_hero',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""a|player_hero""}',1,2);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS n FROM relationship_pair_chemistry;", parameters, transaction);
        }

        internal static void Savepoint3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "SAVEPOINT pair_observation_contract;", parameters, transaction);
        }

        internal static void InsertCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO court_ruler_favor_contact
(campaign_id,timeline_id,ruler_id,favorite_id,last_contact_day,grace_day,contact_id)
VALUES($campaign,'main','player_hero','a',-100,-100,'observation_fixture');", parameters, transaction);
        }

        internal static void InsertCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations
(campaign_id,timeline_id,subject_id,tag_id,acquired_day,catalog_revision,snapshot_json,status,updated_ts)
VALUES($campaign,'main','player_hero','ruler_favoring:observation_fixture',-100,1,'{""linkedHeroId"":""a""}','active',1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM court_ruler_favor_contact;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM character_reputations;", parameters, transaction);
        }

        internal static void Rollback4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK TO SAVEPOINT pair_observation_contract;", parameters, transaction);
        }

        internal static void Release3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "RELEASE SAVEPOINT pair_observation_contract;", parameters, transaction);
        }

        internal static void UpdateRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=35,affinity_b_to_a=35,tag_a_to_b='friend',tag_b_to_a='friend',
last_day=9,updated_ts=2 WHERE pair_key='a|c';", parameters, transaction);
        }

        internal static void UpsertSchemaMeta7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES($key,$day) ON CONFLICT(key) DO UPDATE SET value=$day;", parameters, transaction);
        }

        internal static void UpsertRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_occurrences(
occurrence_id,campaign_id,timeline_id,archetype_id,thread_key,world_day,expires_day,status,
catalog_revision,participants_json,created_ts,updated_ts)
VALUES('rumor_inspector_fixture',$campaign,'main','marital_strife','pair_fixture',7,37,'active',1,'[]',1,1);", parameters, transaction);
        }

        internal static void UpsertRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rumor_subject_tags(
occurrence_id,subject_id,tag_id,subject_role,description,status,updated_ts)
VALUES('rumor_inspector_fixture','npc_observer','marital_strife','spouse','Fixture social report','active',1);", parameters, transaction);
        }

        internal static void UpdateWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE world_test_native_heartbeats SET updated_ts=9999999999;", parameters, transaction);
        }

        internal static void UpsertWorldTestRebellionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rebellion_rolls(
campaign_id,timeline_id,kingdom_id,clan_id,week_index,rolled_day,was_eligible,triggered,roll,ruler_relation,is_player_clan,exclusion_reason,
personal_affinity_to_ruler,ruler_public_standing,ruler_standing_revision,payload_json,updated_ts)
VALUES($campaign,$timeline,$kingdom,$clan,$week,$day,$eligible,$triggered,$roll,$relation,$player,$exclusion,
$personal,$standing,$standingRevision,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,kingdom_id,clan_id,week_index) DO UPDATE SET
rolled_day=$day,was_eligible=$eligible,triggered=$triggered,roll=$roll,ruler_relation=$relation,is_player_clan=$player,
exclusion_reason=$exclusion,personal_affinity_to_ruler=$personal,
ruler_public_standing=$standing,ruler_standing_revision=$standingRevision,
payload_json=$payload,updated_ts=$ts
WHERE world_test_rebellion_rolls.rolled_day<>$day
OR world_test_rebellion_rolls.was_eligible<>$eligible
OR world_test_rebellion_rolls.triggered<>$triggered
OR world_test_rebellion_rolls.roll<>$roll
OR world_test_rebellion_rolls.ruler_relation<>$relation
OR world_test_rebellion_rolls.is_player_clan<>$player
OR world_test_rebellion_rolls.exclusion_reason<>$exclusion
OR world_test_rebellion_rolls.personal_affinity_to_ruler<>$personal
OR world_test_rebellion_rolls.ruler_public_standing<>$standing
OR world_test_rebellion_rolls.ruler_standing_revision<>$standingRevision
OR world_test_rebellion_rolls.payload_json<>$payload;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestRebellionMovements2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT stage,resolution_applied
FROM world_test_rebellion_movements
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertWorldTestRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rebellion_movements(
campaign_id,timeline_id,movement_id,updated_day,stage,resolution_applied,payload_json,updated_ts)
VALUES($campaign,$timeline,$id,$day,$stage,$resolved,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,movement_id) DO UPDATE SET
updated_day=$day,stage=$stage,resolution_applied=$resolved,payload_json=$payload,updated_ts=$ts
WHERE world_test_rebellion_movements.updated_day<>$day
OR world_test_rebellion_movements.stage<>$stage
OR world_test_rebellion_movements.resolution_applied<>$resolved
OR world_test_rebellion_movements.payload_json<>$payload;", parameters, transaction);
        }

        internal static void UpsertWorldTestRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_rebellion_memberships(
campaign_id,timeline_id,movement_id,clan_id,side,payload_json,updated_ts)
VALUES($campaign,$timeline,$movement,$clan,$side,$payload,$ts)
ON CONFLICT(campaign_id,timeline_id,movement_id,clan_id) DO UPDATE SET
side=$side,payload_json=$payload,updated_ts=$ts
WHERE world_test_rebellion_memberships.side<>$side
OR world_test_rebellion_memberships.payload_json<>$payload;", parameters, transaction);
        }

        internal static void UpsertWorldTestManifests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_manifests(
campaign_id,timeline_id,manifest_json,first_observed_day,latest_observed_day,created_ts,updated_ts)
VALUES($campaign,$timeline,$manifest,$day,$day,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id) DO UPDATE SET
manifest_json=$manifest,latest_observed_day=MAX(world_test_manifests.latest_observed_day,$day),updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpsertWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO world_test_native_heartbeats(
campaign_id,timeline_id,day_key,world_day,eligible_npc_count,kingdom_count,payload_json,created_ts,updated_ts)
VALUES($campaign,$timeline,$key,$day,$npcs,$kingdoms,$payload,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=$day,eligible_npc_count=$npcs,kingdom_count=$kingdoms,payload_json=$payload,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairProvenance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT
SUM(CASE WHEN source='kingdom_leadership' AND active=1 AND required=1
    THEN 1 ELSE 0 END) AS kingdom_pairs,
SUM(CASE WHEN source='ruler_network' AND active=1 AND required=1
    THEN 1 ELSE 0 END) AS ruler_pairs
FROM relationship_pair_provenance
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM identity_roster
WHERE is_alive=1 AND is_adult=1 AND is_player=0 AND is_clan_leader=1; ", parameters, transaction);
        }

        internal static void UpdateIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE identity_roster SET
is_ruler=0,is_clan_leader=0
                    WHERE is_ruler<>0 OR is_clan_leader<>0;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta8(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$value);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT timeline_id,MIN(world_day) AS first_day,MAX(world_day) AS latest_day,
COUNT(*) AS observed_days,MAX(updated_ts) AS updated_ts
FROM world_test_native_heartbeats GROUP BY timeline_id ORDER BY latest_day DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT p.*,
COALESCE(sa.standing_value,0) AS public_standing_a,
COALESCE(sb.standing_value,0) AS public_standing_b,
COALESCE(sa.revision,1) AS standing_revision_a,
COALESCE(sb.revision,1) AS standing_revision_b,
MAX(-100,MIN(100,p.affinity_a_to_b+COALESCE(sb.standing_value,0)))
AS effective_attitude_a_to_b,
MAX(-100,MIN(100,p.affinity_b_to_a+COALESCE(sa.standing_value,0)))
AS effective_attitude_b_to_a
FROM relationship_pair_chemistry p
LEFT JOIN character_public_standing sa ON sa.campaign_id=$campaign
AND sa.timeline_id=$timeline AND sa.subject_id=p.hero_a_id
LEFT JOIN character_public_standing sb ON sb.campaign_id=$campaign
AND sb.timeline_id=$timeline AND sb.subject_id=p.hero_b_id
ORDER BY p.last_day DESC,p.pair_key LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorOccurrences4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rumor_occurrences ORDER BY world_day DESC,updated_ts DESC LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRumorSubjectTags2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rumor_subject_tags WHERE occurrence_id=$id ORDER BY status,subject_id LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestChunkCounters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
'counter' AS record_kind,day_key,subsystem,chunk_key AS evidence_key,
'info' AS severity,counters_json AS payload_json,updated_ts
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem='rumors'
UNION ALL
SELECT 'evidence' AS record_kind,day_key,subsystem,evidence_key,severity,payload_json,updated_ts
FROM world_test_diagnostic_evidence
WHERE campaign_id=$campaign AND timeline_id=$timeline AND subsystem='rumors'
ORDER BY day_key DESC,updated_ts DESC LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM clan_conflict_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,incident_id LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictRelationshipEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM clan_conflict_relationship_effects WHERE incident_id=$incident ORDER BY effect_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
'native_target:'||pair_key AS actionId,
'RelationshipNativeProjection' AS type,status,world_day AS createdDay,
world_day AS lastAttemptDay,attempt_count AS attemptCount,last_error AS failureReason,
pair_key,hero_a_id,hero_b_id,target_relation,observed_relation
FROM relationship_native_targets
ORDER BY world_day,pair_key LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestDailyRollups2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_test_daily_rollups WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1000;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtFamilyAttention(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT player_id,hero_id,dismissals,neglected,directional_relation,last_decay_day,decay_finished
FROM court_family_attention WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ((player_id=$a AND hero_id=$b) OR (player_id=$b AND hero_id=$a));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCourtRulerFavorContact2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM court_ruler_favor_contact WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ((ruler_id=$a AND favorite_id=$b) OR (ruler_id=$b AND favorite_id=$a));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectCharacterReputations3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM character_reputations WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subject_id IN ($a,$b) AND (tag_id='ruler_favoring' OR tag_id LIKE 'ruler_favoring:%');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestNativeHeartbeats5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM world_test_native_heartbeats WHERE timeline_id=$timeline ORDER BY world_day ASC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestManifests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT manifest_json
FROM world_test_manifests
WHERE campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestDailyRollups3(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT day_key,world_day,overall_status,rollup_json,updated_ts
FROM world_test_daily_rollups WHERE timeline_id=$timeline AND world_day>=$from AND world_day<=$to
ORDER BY world_day DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestDailyRollups4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT rollup_json FROM world_test_daily_rollups
WHERE timeline_id=$timeline AND day_key<$day
ORDER BY day_key DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldTestDailyRollups5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MAX(world_day) AS day FROM world_test_daily_rollups
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MAX(last_native_sync_day) AS day FROM relationship_pair_chemistry
WHERE last_native_sync_day>=0;", parameters, transaction);
        }
    }
}
