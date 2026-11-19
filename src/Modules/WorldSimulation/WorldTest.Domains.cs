using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static Dictionary<string, object> BuildWorldTestRelationships(
            ReignDbConnection connection, string campaignId, string timelineId, double latestDay, string mainHeroId)
        {
            RelationshipsService.EnsureNotableMbtiSchema(connection);
            RelationshipsService.EnsureRelationshipFlingSchemaCore(connection);
            EnsureWorldTestTelemetrySchema(connection);
            Dictionary<string, object> standingAndPolitical =
                RelationshipsService.BuildWorldTestStandingAndPoliticalMetrics(connection, campaignId,
                    timelineId, latestDay);
            Dictionary<string, object> pairParameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["player"] = mainHeroId ?? string.Empty
            };
            Dictionary<string, object> pairSummary = Program.QuerySql(connection, @"
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
WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player);",
                new Dictionary<string, object>(pairParameters)
                {
                    ["latestDay"] = (int)Math.Floor(latestDay)
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int pairCount = ReadInt(pairSummary, "pair_count", 0);
            List<Dictionary<string, object>> notableProfiles = Program.QuerySql(connection, "SELECT * FROM notable_mbti_profiles;");
            Dictionary<string, int> notableTypes = notableProfiles
                .GroupBy(x => ReadString(x, "mbti_type", "XXXX"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> notableSync = notableProfiles
                .GroupBy(x => ReadString(x, "native_sync_status", "pending"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            int nobleNotablePairs = ReadInt(pairSummary, "noble_notable_pairs", 0);
            int missingMbti = ReadInt(pairSummary, "missing_mbti", 0);
            int typeMismatches = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry p
LEFT JOIN notable_mbti_profiles a ON p.hero_a_id=a.hero_id
LEFT JOIN notable_mbti_profiles b ON p.hero_b_id=b.hero_id
WHERE ($player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player))
AND ((a.hero_id IS NOT NULL AND p.mbti_a<>a.mbti_type)
  OR (b.hero_id IS NOT NULL AND p.mbti_b<>b.mbti_type));",
                pairParameters).FirstOrDefault(), "count", 0);
            int notableSyncErrors = notableProfiles.Count(x => Program.ContainsAny(ReadString(x, "native_sync_status", ""), "failed", "contradicted"));
            int notableSyncOverdue = notableProfiles.Count(x =>
                Program.ContainsAny(ReadString(x, "native_sync_status", ""), "pending", "claimed")
                && latestDay - ReadDouble(x, "native_action_created_day", latestDay) > 1d);
            Dictionary<string, Dictionary<string, int>> bands = WorldTestRelationshipBands.ToDictionary(
                x => x, x => new Dictionary<string, int> { ["uniqueNpcCount"] = 0, ["directionalEdgeCount"] = 0, ["pairCount"] = 0 },
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> materializedCategories = Program.QuerySql(connection, @"
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
GROUP BY h.category_type,h.category_key,p.pair_count;",
                pairParameters);
            Dictionary<string, Dictionary<string, object>> tags =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, int> affinityHistogram = new Dictionary<int, int>();
            foreach (Dictionary<string, object> row in materializedCategories)
            {
                string type = ReadString(row, "category_type", "");
                string key = ReadString(row, "category_key", "");
                Dictionary<string, int> counts = new Dictionary<string, int>
                {
                    ["uniqueNpcCount"] = ReadInt(row, "unique_npc_count", 0),
                    ["directionalEdgeCount"] = ReadInt(row, "directional_edge_count", 0),
                    ["pairCount"] = ReadInt(row, "pair_count", 0)
                };
                if (type.Equals("band", StringComparison.OrdinalIgnoreCase) && bands.ContainsKey(key))
                    bands[key] = counts;
                else if (type.Equals("tag", StringComparison.OrdinalIgnoreCase))
                    tags[key] = new Dictionary<string, object>
                    {
                        ["tag"] = key,
                        ["uniqueNpcCount"] = counts["uniqueNpcCount"],
                        ["directionalEdgeCount"] = counts["directionalEdgeCount"],
                        ["pairCount"] = counts["pairCount"]
                    };
            }
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, @"
WITH directions AS (
 SELECT p.affinity_a_to_b AS affinity FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION ALL
 SELECT p.affinity_b_to_a FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
)
SELECT affinity,COUNT(*) AS directional_edge_count
FROM directions GROUP BY affinity;", pairParameters))
            {
                affinityHistogram[ReadInt(row, "affinity", 0)] =
                    ReadInt(row, "directional_edge_count", 0);
            }
            int uniqueNpcCount = ReadInt(Program.QuerySql(connection, @"
WITH heroes AS (
 SELECT p.hero_a_id AS hero_id FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
 UNION
 SELECT p.hero_b_id FROM relationship_pair_chemistry p
 WHERE $player='' OR (p.hero_a_id<>$player AND p.hero_b_id<>$player)
)
SELECT COUNT(*) AS count FROM heroes;",
                pairParameters).FirstOrDefault(), "count", 0);
            List<Dictionary<string, object>> lifecycle = Program.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;");
            List<Dictionary<string, object>> lifecycleMarriageActions = Program.QuerySql(connection,
                    @"SELECT director_action_id,status,world_day,actor_id,target_id,payload_json,result_json
FROM relationship_director_actions WHERE action_type='marriage';")
                .Where(row =>
                {
                    if (!string.IsNullOrWhiteSpace(mainHeroId)
                        && (ReadString(row, "actor_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                            || ReadString(row, "target_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    Dictionary<string, object> actionPayload =
                        TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
                    return ReadString(actionPayload, "source", "").Equals(
                            "mbti_relationship_lifecycle", StringComparison.OrdinalIgnoreCase)
                        && ReadString(actionPayload, "timelineId", timelineId)
                            .Equals(timelineId, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            Func<Dictionary<string, object>, string> lifecycleMarriageRoute = row =>
                ReadString(TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>(), "route", "");
            List<Dictionary<string, object>> organicMarriageActions =
                lifecycleMarriageActions.Where(row => lifecycleMarriageRoute(row)
                    .Equals("mutual_affinity",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> pregnancyCommitmentMarriageActions =
                lifecycleMarriageActions.Where(row => lifecycleMarriageRoute(row)
                    .Equals("pregnancy_commitment",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            Dictionary<string, Dictionary<string, object>> observedMarriageRows =
                Program.QuerySql(connection, @"SELECT hero_id,profile_json,last_observed_day
FROM relationship_observed_heroes;")
                    .ToDictionary(row => ReadString(row, "hero_id", ""), row => row,
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> observedMarriageProfiles =
                observedMarriageRows.ToDictionary(row => row.Key, row =>
                    TryParseJsonObject(ReadString(row.Value, "profile_json", "{}"))
                        ?? new Dictionary<string, object>(),
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> completedOrganicMarriageReports =
                organicMarriageActions.Where(row => ReadString(row, "status", "")
                    .Equals("completed", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> completedPregnancyCommitmentMarriageReports =
                pregnancyCommitmentMarriageActions.Where(row => ReadString(row,
                    "status", "").Equals("completed",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            Func<Dictionary<string, object>, bool> reciprocalNativeMarriage = row =>
            {
                string actorId = ReadString(row, "actor_id", "");
                string targetId = ReadString(row, "target_id", "");
                return observedMarriageProfiles.TryGetValue(actorId,
                        out Dictionary<string, object> actorProfile)
                    && observedMarriageProfiles.TryGetValue(targetId,
                        out Dictionary<string, object> targetProfile)
                    && ReadString(actorProfile, "spouseId", "")
                        .Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(targetProfile, "spouseId", "")
                        .Equals(actorId, StringComparison.OrdinalIgnoreCase);
            };
            Func<Dictionary<string, object>, bool> nativeMarriageConfirmationOverdue = row =>
            {
                string actorId = ReadString(row, "actor_id", "");
                string targetId = ReadString(row, "target_id", "");
                if (!observedMarriageRows.TryGetValue(actorId,
                        out Dictionary<string, object> actorObservation)
                    || !observedMarriageRows.TryGetValue(targetId,
                        out Dictionary<string, object> targetObservation))
                    return false;
                Dictionary<string, object> result = TryParseJsonObject(
                    ReadString(row, "result_json", "{}"))
                    ?? new Dictionary<string, object>();
                double reportDay = ReadDouble(result, "worldDay",
                    ReadDouble(row, "world_day", 0d));
                double observedThrough = Math.Min(
                    ReadDouble(actorObservation, "last_observed_day", -1d),
                    ReadDouble(targetObservation, "last_observed_day", -1d));
                return observedThrough >= Math.Floor(reportDay) + 1d;
            };
            Func<Dictionary<string, object>, string> organicPairKey = row =>
            {
                Dictionary<string, object> actionPayload = TryParseJsonObject(
                    ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                return FirstNonEmpty(ReadString(actionPayload, "pairKey", ""),
                    RelationshipsService.AmbientPairKey(ReadString(row, "actor_id", ""),
                        ReadString(row, "target_id", "")));
            };
            int romanticMarriages = completedOrganicMarriageReports
                .Where(reciprocalNativeMarriage).Select(organicPairKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int unconfirmedOrganicMarriageReports =
                completedOrganicMarriageReports.Count(row =>
                    !reciprocalNativeMarriage(row)
                    && nativeMarriageConfirmationOverdue(row));
            int duplicateOrganicMarriageReports = Math.Max(0,
                completedOrganicMarriageReports.Count
                - completedOrganicMarriageReports.Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            int pregnancyCommitmentConfirmedMarriages =
                completedPregnancyCommitmentMarriageReports
                    .Where(reciprocalNativeMarriage).Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int unconfirmedPregnancyCommitmentMarriageReports =
                completedPregnancyCommitmentMarriageReports.Count(row =>
                    !reciprocalNativeMarriage(row)
                    && nativeMarriageConfirmationOverdue(row));
            int duplicatePregnancyCommitmentMarriageReports = Math.Max(0,
                completedPregnancyCommitmentMarriageReports.Count
                - completedPregnancyCommitmentMarriageReports
                    .Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            int arrangedMarriages = Program.QuerySql(connection,
                    "SELECT evaluation_id,hero_a_id,hero_b_id FROM marriage_evaluations WHERE route='arranged' AND status='executed';")
                .Count(row => string.IsNullOrWhiteSpace(mainHeroId)
                    || (!ReadString(row, "hero_a_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                        && !ReadString(row, "hero_b_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)));
            int affairEligibleLovers = ReadInt(Program.QuerySql(connection, @"
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
);", new Dictionary<string, object>()).FirstOrDefault(), "count", 0);
            Dictionary<string, object> flingParameters =
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                };
            Dictionary<string, object> flingRollTotals = Program.QuerySql(connection, @"
SELECT COUNT(*) AS rolls,SUM(passed) AS passes,
SUM(CASE WHEN status='passed_unmatched' THEN 1 ELSE 0 END) AS unmatched,
COUNT(DISTINCT world_day) AS observed_days
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline;", flingParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> flingSparkTotals = Program.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE campaign_id=$campaign
AND timeline_id=$timeline;", flingParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> flingEncounterTotals = Program.QuerySql(connection, @"
SELECT COUNT(*) AS pairings,SUM(same_sex) AS same_sex,
SUM(CASE WHEN same_sex=0 THEN 1 ELSE 0 END) AS opposite_sex,
SUM(short_affair) AS short_affairs,SUM(fixed_discovered) AS fixed_discoveries,
SUM(pregnancy_discovered) AS pregnancy_discoveries,
SUM(CASE WHEN conception_attempt_id<>'' THEN 1 ELSE 0 END) AS conception_attempts,
SUM(conceived) AS conceptions,SUM(discovery_applied) AS discovered_encounters
FROM relationship_fling_encounters WHERE campaign_id=$campaign
AND timeline_id=$timeline AND status='completed';", flingParameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            int flingChanceViolations = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (judgment<0 OR judgment>40 OR chance_percent<>50-judgment
 OR roll<1 OR roll>100 OR passed<>CASE WHEN roll<=chance_percent THEN 1 ELSE 0 END);",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingSparkChanceViolations = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND (chance_percent<>25 OR roll<1 OR roll>100
 OR passed<>CASE WHEN roll<=chance_percent THEN 1 ELSE 0 END);",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingReceiptViolations = ReadInt(Program.QuerySql(connection, @"
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
        AND spark.location_id=encounter.location_id)));", flingParameters)
                .FirstOrDefault(), "count", 0);
            int flingCooldownViolations = ReadInt(Program.QuerySql(connection, @"
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
WHERE prior_day IS NOT NULL AND world_day-prior_day<7;",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingSameSexConceptionViolations = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE campaign_id=$campaign AND timeline_id=$timeline AND same_sex=1
AND (conception_attempt_id<>'' OR conception_id<>'' OR conceived=1);",
                flingParameters).FirstOrDefault(), "count", 0);
            int flingDuplicatePersonDayViolations = ReadInt(Program.QuerySql(connection, @"
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
) duplicates;", flingParameters).FirstOrDefault(), "count", 0);
            Dictionary<string, object> flingTelemetry =
                new Dictionary<string, object>
                {
                    ["judgmentMaximum"] = RelationshipsService.FlingMaximumJudgment,
                    ["chanceAtZeroJudgmentPercent"] = 50,
                    ["chanceAtFortyJudgmentPercent"] = 10,
                    ["sparkChancePercent"] = RelationshipsService.FlingSparkChancePercent,
                    ["cooldownDays"] = RelationshipsService.FlingCooldownDays,
                    ["shortAffairDiscoveryPercent"] =
                        RelationshipsService.FlingDiscoveryChancePercent,
                    ["observedDays"] = ReadInt(flingRollTotals,
                        "observed_days", 0),
                    ["eligibleRolls"] = ReadInt(flingRollTotals, "rolls", 0),
                    ["passes"] = ReadInt(flingRollTotals, "passes", 0),
                    ["unmatched"] = ReadInt(flingRollTotals, "unmatched", 0),
                    ["sparkAttempts"] = ReadInt(flingSparkTotals,
                        "attempts", 0),
                    ["sparkPasses"] = ReadInt(flingSparkTotals,
                        "passes", 0),
                    ["pairs"] = ReadInt(flingEncounterTotals, "pairings", 0),
                    ["sameSexPairs"] = ReadInt(flingEncounterTotals,
                        "same_sex", 0),
                    ["oppositeSexPairs"] = ReadInt(flingEncounterTotals,
                        "opposite_sex", 0),
                    ["shortAffairs"] = ReadInt(flingEncounterTotals,
                        "short_affairs", 0),
                    ["fixedDiscoveries"] = ReadInt(flingEncounterTotals,
                        "fixed_discoveries", 0),
                    ["pregnancyDiscoveries"] = ReadInt(flingEncounterTotals,
                        "pregnancy_discoveries", 0),
                    ["discoveredEncounters"] = ReadInt(flingEncounterTotals,
                        "discovered_encounters", 0),
                    ["conceptionAttempts"] = ReadInt(flingEncounterTotals,
                        "conception_attempts", 0),
                    ["conceptions"] = ReadInt(flingEncounterTotals,
                        "conceptions", 0),
                    ["chanceViolations"] = flingChanceViolations,
                    ["sparkChanceViolations"] =
                        flingSparkChanceViolations,
                    ["receiptViolations"] = flingReceiptViolations,
                    ["cooldownViolations"] = flingCooldownViolations,
                    ["sameSexConceptionViolations"] =
                        flingSameSexConceptionViolations,
                    ["duplicatePersonDayViolations"] =
                        flingDuplicatePersonDayViolations,
                    ["recentEncounters"] = Program.QuerySql(connection, @"
SELECT * FROM relationship_fling_encounters
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,encounter_id DESC LIMIT 20;", flingParameters),
                    ["recentSparks"] = Program.QuerySql(connection, @"
SELECT * FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,pair_key LIMIT 40;", flingParameters),
                    ["recentRolls"] = Program.QuerySql(connection, @"
SELECT * FROM relationship_fling_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,hero_id LIMIT 40;", flingParameters)
                };
            Dictionary<string, object> lifecycleCounts = new Dictionary<string, object>
            {
                ["lovers"] = lifecycle.Count(x => ReadInt(x, "lover_active", 0) == 1),
                ["activeAffairs"] = lifecycle.Count(x => ReadInt(x, "affair_active", 0) == 1),
                ["affairEligibleLovers"] = affairEligibleLovers,
                ["flirtationStagePairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals("flirtation",
                        StringComparison.OrdinalIgnoreCase)),
                ["growingAttractionPairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals(
                        "growing_attraction", StringComparison.OrdinalIgnoreCase)),
                ["seriousAttractionPairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals(
                        "serious_attraction", StringComparison.OrdinalIgnoreCase)),
                ["flirtationAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "flirt_attempted", 0) == 1),
                ["flirtationPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "flirt_passed", 0) == 1),
                ["affairJudgmentAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_attempted", 0) == 1),
                ["affairJudgmentPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_passed", 0) == 1),
                ["romanceBonusTenPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "romance_positive_bonus", 0)
                        == RelationshipsService.RomanceFlirtationPositiveChanceBonus),
                ["romanceBonusTwentyPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "romance_positive_bonus", 0)
                        == RelationshipsService.RomanceGrowingPositiveChanceBonus),
                ["restrictedLoverAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_attempted", 0) == 1),
                ["restrictedLoverPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_passed", 0) == 1),
                ["married"] = lifecycle.Count(x => ReadInt(x, "married", 0) == 1),
                ["estrangedDirections"] = lifecycle.Sum(x => ReadInt(x, "estranged_a_to_b", 0) + ReadInt(x, "estranged_b_to_a", 0)),
                ["divorced"] = lifecycle.Count(x => ReadInt(x, "divorced", 0) == 1),
                ["romanticMarriages"] = romanticMarriages,
                ["romanticMarriageAttempts"] = organicMarriageActions.Count,
                ["romanticMarriageCompletedReports"] = completedOrganicMarriageReports.Count,
                ["romanticMarriageUnconfirmedReports"] = unconfirmedOrganicMarriageReports,
                ["romanticMarriageDuplicateReports"] = duplicateOrganicMarriageReports,
                ["pregnancyCommitmentMarriageAttempts"] =
                    pregnancyCommitmentMarriageActions.Count,
                ["pregnancyCommitmentMarriageCompletedReports"] =
                    completedPregnancyCommitmentMarriageReports.Count,
                ["pregnancyCommitmentConfirmedMarriages"] =
                    pregnancyCommitmentConfirmedMarriages,
                ["pregnancyCommitmentMarriageUnconfirmedReports"] =
                    unconfirmedPregnancyCommitmentMarriageReports,
                ["pregnancyCommitmentMarriageDuplicateReports"] =
                    duplicatePregnancyCommitmentMarriageReports,
                ["romanticMarriageBlockedPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "marriage_blocked", 0) == 1),
                ["arrangedMarriages"] = arrangedMarriages,
                ["affairDiscoveries"] = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND archetype_id='affair';", flingParameters).FirstOrDefault(), "count", 0),
                ["flings"] = flingTelemetry,
                ["pregnancyCommitments"] = TableCount(connection,
                    "relationship_conception_commitments"),
                ["pregnancyCommitmentMarriages"] = ReadInt(Program.QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE status='married';")
                    .FirstOrDefault(), "count", 0),
                ["pregnancyCommitmentBreakups"] = ReadInt(Program.QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE status='broke_up';")
                    .FirstOrDefault(), "count", 0),
                ["pregnancyCommitmentChanceViolations"] = ReadInt(Program.QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_conception_commitments WHERE chance_a>90 OR chance_b>90 OR chance_a<>MIN(90,honor_a+10) OR chance_b<>MIN(90,honor_b+10);")
                    .FirstOrDefault(), "count", 0),
                ["affairMarriageCommitments"] = TableCount(connection,
                    "relationship_affair_commitments"),
                ["affairMarriageCommitmentMarriages"] = ReadInt(Program.QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_affair_commitments WHERE status='married';")
                    .FirstOrDefault(), "count", 0),
                ["affairMarriageChanceCapViolations"] = ReadInt(Program.QuerySql(connection,
                    "SELECT COUNT(*) AS count FROM relationship_affair_commitments WHERE chance_a>30 AND spouse_a_id<>'' OR chance_b>30 AND spouse_b_id<>'';")
                    .FirstOrDefault(), "count", 0),
                ["incidents"] = TableCount(connection, "relationship_incidents"),
                ["conceptions"] = StatusCounts(connection, "conceptions", "status"),
                ["concealedParentage"] = ReadInt(Program.QuerySql(connection, "SELECT COUNT(*) AS count FROM parentage WHERE revealed=0 AND is_illegitimate=1;").FirstOrDefault(), "count", 0)
            };
            Dictionary<string, object> playerParameter = new Dictionary<string, object>
            {
                ["player"] = mainHeroId ?? string.Empty,
                ["latest"] = latestDay
            };
            int pending = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets
WHERE status IN ('pending','claimed')
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", playerParameter).FirstOrDefault(), "count", 0);
            int overdue = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets
WHERE status IN ('pending','claimed','failed') AND $latest-world_day>1
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));",
                playerParameter).FirstOrDefault(), "count", 0);
            int unrepresentedNativeFlags = RelationshipsService.CountUnrepresentedNativeFlags(connection, mainHeroId);
            pending += unrepresentedNativeFlags;
            Dictionary<string, object> latestNativeBatch = Program.QuerySql(connection, @"
SELECT * FROM relationship_native_sync_batches
WHERE timeline_id=$timeline ORDER BY world_day DESC,issued_ts DESC LIMIT 1;",
                new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            int clientRemaining = ReadInt(latestNativeBatch, "client_remaining", 0);
            int persistedTargetFailures = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets WHERE status='failed'
AND ($player='' OR (hero_a_id<>$player AND hero_b_id<>$player));", playerParameter).FirstOrDefault(), "count", 0);
            int latestClientFailures = ReadInt(latestNativeBatch, "client_failed", 0);
            int nativeFailures = Math.Max(persistedTargetFailures, latestClientFailures);
            int directorFailures = ReadInt(Program.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_director_actions
WHERE action_type<>'native_relation' AND status='failed'
AND ($player='' OR (actor_id<>$player AND target_id<>$player));", playerParameter).FirstOrDefault(), "count", 0);
            int failed = nativeFailures + directorFailures;
            Dictionary<string, object> openingSeed = Program.QuerySql(connection, @"
SELECT * FROM relationship_opening_seed_runs
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY seed_version DESC LIMIT 1;", pairParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            openingSeed["directionalCount"] =
                ReadInt(openingSeed, "completed_pairs", 0) * 2;
            openingSeed["averageDelta"] = ReadDouble(openingSeed,
                "average_delta", 0d);
            openingSeed["medianDelta"] = ReadDouble(openingSeed,
                "median_delta", 0d);
            openingSeed["minimumDelta"] = ReadInt(openingSeed,
                "minimum_delta", 0);
            openingSeed["maximumDelta"] = ReadInt(openingSeed,
                "maximum_delta", 0);
            openingSeed["startingBands"] = TryParseJsonObject(ReadString(
                openingSeed, "starting_bands_json", "{}"))
                ?? new Dictionary<string, object>();
            openingSeed["resultingBands"] = TryParseJsonObject(ReadString(
                openingSeed, "resulting_bands_json", "{}"))
                ?? new Dictionary<string, object>();
            string openingSeedStatus = ReadString(openingSeed, "status", "");
            double lastProcessed = RelationshipsService.RelationshipLastProcessedDay(connection, timelineId);
            bool enabled = LatestFeatureFlag(connection, "passiveRelationshipsEnabled", true);
            int eligible = LatestPopulationValue(connection, "eligibleNpcCount");
            string status = !enabled ? "disabled" : pairCount == 0 ? "insufficient_data"
                : failed > 0 || notableSyncErrors > 0 || missingMbti > 0 || typeMismatches > 0
                    || openingSeedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    || unconfirmedOrganicMarriageReports > 0
                    || unconfirmedPregnancyCommitmentMarriageReports > 0
                    || flingChanceViolations > 0
                    || flingSparkChanceViolations > 0
                    || flingReceiptViolations > 0
                    || flingCooldownViolations > 0
                    || flingSameSexConceptionViolations > 0
                    || flingDuplicatePersonDayViolations > 0
                    ? "error"
                : lastProcessed < 0d ? "insufficient_data"
                : eligible > 1 && latestDay - lastProcessed > 1d || notableSyncOverdue > 0 || overdue > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (nativeFailures > 0) anomalies.Add(nativeFailures + " native relationship projections failed in the latest or still-persisted plan.");
            if (directorFailures > 0) anomalies.Add(directorFailures + " relationship lifecycle actions failed.");
            if (unconfirmedOrganicMarriageReports > 0)
                anomalies.Add(unconfirmedOrganicMarriageReports
                    + " organic marriage completion reports lack reciprocal native spouse confirmation.");
            if (duplicateOrganicMarriageReports > 0)
                anomalies.Add(duplicateOrganicMarriageReports
                    + " duplicate organic marriage completion reports were recorded for existing pairs.");
            if (unconfirmedPregnancyCommitmentMarriageReports > 0)
                anomalies.Add(unconfirmedPregnancyCommitmentMarriageReports
                    + " pregnancy-commitment marriage completion reports lack reciprocal native spouse confirmation.");
            if (duplicatePregnancyCommitmentMarriageReports > 0)
                anomalies.Add(duplicatePregnancyCommitmentMarriageReports
                    + " duplicate pregnancy-commitment marriage completion reports were recorded for existing pairs.");
            if (flingChanceViolations > 0)
                anomalies.Add(flingChanceViolations
                    + " fling roll receipts violate the judgment/chance contract.");
            if (flingSparkChanceViolations > 0)
                anomalies.Add(flingSparkChanceViolations
                    + " fling spark receipts violate the fixed twenty-five-percent contract.");
            if (flingReceiptViolations > 0)
                anomalies.Add(flingReceiptViolations
                    + " fling encounters lack two passed participant receipts or a passed same-location spark receipt.");
            if (flingCooldownViolations > 0)
                anomalies.Add(flingCooldownViolations
                    + " fling participants were reused inside the seven-day cooldown.");
            if (flingSameSexConceptionViolations > 0)
                anomalies.Add(flingSameSexConceptionViolations
                    + " same-sex fling encounters incorrectly created conception work.");
            if (flingDuplicatePersonDayViolations > 0)
                anomalies.Add(flingDuplicatePersonDayViolations
                    + " characters participated in more than one fling on the same day.");
            if (notableSyncErrors > 0) anomalies.Add(notableSyncErrors + " notable MBTI native-trait synchronizations failed or were contradicted.");
            if (missingMbti > 0) anomalies.Add(missingMbti + " relationship pairs contain a missing MBTI type.");
            if (typeMismatches > 0) anomalies.Add(typeMismatches + " relationship pairs disagree with a notable's persisted MBTI assignment.");
            if (notableSyncOverdue > 0) anomalies.Add(notableSyncOverdue + " notable native-trait synchronizations are overdue.");
            if (enabled && eligible > 1 && lastProcessed >= 0 && latestDay - lastProcessed > 1d) anomalies.Add("The daily relationship producer is overdue.");
            if (overdue > 0) anomalies.Add(overdue + " native relationship projections remain unconfirmed after one campaign day.");
            if (openingSeedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase))
                anomalies.Add("Campaign-opening relationship seeding failed: "
                    + ReadString(openingSeed, "last_error", "unknown error"));
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Relationships, Romance, and Family", ["status"] = status,
                ["latestSuccessfulDay"] = lastProcessed, ["pairCount"] = pairCount,
                ["uniqueNpcCount"] = uniqueNpcCount,
                ["directionalEdgeCount"] = pairCount * 2,
                ["newPairsLatestDay"] = ReadInt(pairSummary, "new_pairs", 0),
                ["partyPairs"] = ReadInt(pairSummary, "party_pairs", 0),
                ["settlementPairs"] = ReadInt(pairSummary, "settlement_pairs", 0),
                ["positiveLatestRolls"] = ReadInt(pairSummary, "positive_rolls", 0),
                ["negativeLatestRolls"] = ReadInt(pairSummary, "negative_rolls", 0),
                ["symmetricPairCount"] = ReadInt(pairSummary, "symmetric_pairs", 0),
                ["asymmetricPairCount"] = pairCount - ReadInt(pairSummary, "symmetric_pairs", 0),
                ["affinity"] = AffinitySummaryFromHistogram(affinityHistogram),
                ["bands"] = bands.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase),
                ["tags"] = tags.Values.OrderByDescending(x => ReadInt(x, "directionalEdgeCount", 0)).ToList(),
                ["nativeSync"] = new Dictionary<string, object>
                {
                    ["pending"] = pending, ["failed"] = nativeFailures,
                    ["aligned"] = Math.Max(0, pairCount - pending - persistedTargetFailures),
                    ["chemistryPending"] = pending,
                    ["unrepresentedNativeFlags"] = unrepresentedNativeFlags,
                    ["overdue"] = overdue,
                    ["planId"] = ReadString(latestNativeBatch, "plan_id", ""),
                    ["planTargetCount"] = ReadInt(latestNativeBatch, "target_count", 0),
                    ["clientApplied"] = ReadInt(latestNativeBatch, "client_applied", 0),
                    ["clientAlreadyAligned"] = ReadInt(latestNativeBatch, "client_already_aligned", 0),
                    ["clientObsolete"] = ReadInt(latestNativeBatch, "client_obsolete", 0),
                    ["clientFailed"] = ReadInt(latestNativeBatch, "client_failed", 0),
                    ["clientRemaining"] = clientRemaining,
                    ["clientDurationMs"] = ReadInt(latestNativeBatch, "client_duration_ms", 0),
                    ["lastClientReportDay"] = ReadDouble(latestNativeBatch, "last_report_day", -1d)
                },
                ["notableMbti"] = new Dictionary<string, object>
                {
                    ["assignedCount"] = notableProfiles.Count,
                    ["nobleNotablePairCount"] = nobleNotablePairs,
                    ["missingPairTypes"] = missingMbti,
                    ["typeMismatches"] = typeMismatches,
                    ["types"] = notableTypes,
                    ["nativeSyncStatuses"] = notableSync
                },
                ["openingSeed"] = openingSeed,
                ["lifecycle"] = lifecycleCounts,
                ["marriageFunnel"] = WorldTestCounterTotals(
                    connection, campaignId, timelineId, "marriages"),
                ["politicalMarriage"] = new Dictionary<string, object>
                {
                    ["rolls"] = TableCount(connection, "marriage_leader_rolls"),
                    ["evaluations"] = TableCount(connection, "marriage_evaluations"),
                    ["romanticMarriages"] = romanticMarriages,
                    ["arrangedMarriages"] = arrangedMarriages,
                    ["statuses"] = StatusCounts(connection, "marriage_evaluations", "status"),
                    ["proposalPolicy"] = Reign.Relationships.ArrangedMarriagePolicy.Version,
                    ["completedPeriods"] = Program.QuerySql(connection,"SELECT name FROM sqlite_master WHERE type='table' AND name='arranged_marriage_periods';").Any()
                        ? Program.QuerySql(connection,"SELECT period_index,world_day,status FROM arranged_marriage_periods WHERE timeline_id=$t ORDER BY period_index DESC LIMIT 16;",new Dictionary<string,object>{{"t",timelineId}})
                        : new List<Dictionary<string,object>>()
                },
                ["publicStanding"] = ReadDictionary(standingAndPolitical,
                    "publicStanding") ?? new Dictionary<string, object>(),
                ["politicalNetwork"] = ReadDictionary(standingAndPolitical,
                    "politicalNetwork") ?? new Dictionary<string, object>(),
                ["effectiveBands"] = standingAndPolitical.TryGetValue(
                    "effectiveBands", out object effectiveBands)
                    ? effectiveBands : new Dictionary<string, int>(),
                ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, object> BuildWorldTestDiplomacy(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay, double elapsedObservedDays,
            Dictionary<string, object> nativePayload)
        {
            List<Dictionary<string, object>> events = ReadWorldTestDiplomacyEvents(campaignId);
            Dictionary<string, object> state = DiplomacyService.ReadDirectorState(campaignId);
            List<Dictionary<string, object>> actions = ReadWorldTestActions(campaignId);
            List<Dictionary<string, object>> diplomaticActions = actions.Where(x =>
            {
                Dictionary<string, object> record = ReadDictionary(x, "record") ?? x;
                return ReadString(record, "source", "").Equals("world_diplomacy_director", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            int failed = diplomaticActions.Count(x => IsTerminalFailureStatus(ReadString(x, "status", "")));
            int pending = diplomaticActions.Count(x => IsPendingStatus(ReadString(x, "status", "")));
            List<Dictionary<string, object>> nativeAgreements =
                ReadDictionaryList(ReadDictionary(nativePayload,
                    "nativeOutcomes") ?? new Dictionary<string, object>(),
                    "agreements");
            List<Dictionary<string, object>> missingAgreementReceipts = events
                .Where(x => ReadBool(x, "accepted", false)
                    && ReadString(x, "executionStatus", "")
                        .Equals("completed", StringComparison.OrdinalIgnoreCase)
                    && latestDay - ReadDouble(x, "worldDay", latestDay) > 0.01d)
                .Where(x => ExpectedAgreementKindForDiplomacyCommand(
                    ReadString(x, "command", "")) != "")
                .Where(x => !WorldTestNativeAgreementExists(nativeAgreements,
                    ExpectedAgreementKindForDiplomacyCommand(
                        ReadString(x, "command", "")),
                    ReadString(x, "actorKingdomId", ""),
                    ReadString(x, "targetKingdomId", "")))
                .ToList();
            double lastDirectorEvent = ReadDouble(state, "lastEvaluatedDay", -1d);
            double lastEvaluation = Math.Max(lastDirectorEvent,
                LatestSuccessfulDiplomacyEvaluationDay(connection, campaignId,
                    timelineId));
            bool enabled = LatestFeatureFlagFromPayload(campaignId, "worldDiplomacyEnabled", true);
            string status = !enabled ? "disabled" : lastEvaluation < 0 ? "insufficient_data"
                : failed > 0 || missingAgreementReceipts.Count > 0 ? "error"
                : latestDay - lastEvaluation > 3d ? "warning" : "healthy";
            Dictionary<string, int> commands = CountBy(events, "command");
            Dictionary<string, int> outcomes = CountBy(events, "outcome");
            Dictionary<string, int> actors = CountBy(events, "actorKingdomName");
            List<string> anomalies = new List<string>();
            if (failed > 0) anomalies.Add(failed + " diplomatic actions failed.");
            if (lastEvaluation >= 0 && latestDay - lastEvaluation > 3d) anomalies.Add("Diplomacy evaluation is past its three-day cadence.");
            if (pending > 0) anomalies.Add(pending + " diplomatic actions remain pending.");
            if (missingAgreementReceipts.Count > 0)
                anomalies.Add(missingAgreementReceipts.Count
                    + " completed treaty actions are absent from the next native agreement heartbeat.");
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Diplomacy", ["status"] = status, ["latestSuccessfulDay"] = lastEvaluation,
                ["eventCount"] = events.Count, ["accepted"] = events.Count(x => ReadBool(x, "accepted", false)),
                ["refused"] = events.Count(x => !ReadBool(x, "accepted", false)), ["pendingActions"] = pending, ["failedActions"] = failed,
                ["undeliveredAnnouncements"] = events.Count(x =>
                    ReadBool(x, "announcementReady", false)
                    && !ReadBool(x, "delivered", false)
                    && !ReadBool(x, "acknowledged", false)),
                ["unacknowledgedAnnouncements"] = events.Count(x =>
                    ReadBool(x, "announcementReady", false)
                    && !ReadBool(x, "acknowledged", false)),
                ["nativeAgreementMismatches"] = missingAgreementReceipts.Count,
                ["missingAgreementReceipts"] = missingAgreementReceipts,
                ["eventsPerSeason"] = elapsedObservedDays > 0
                    ? Math.Round(events.Count / Math.Max(1d, elapsedObservedDays / 31.5d), 2) : 0d,
                ["eventsPerYear"] = elapsedObservedDays > 0
                    ? Math.Round(events.Count / Math.Max(1d, elapsedObservedDays / 126d), 2) : 0d,
                ["commands"] = commands, ["outcomes"] = outcomes, ["actorKingdoms"] = actors,
                ["targetKingdoms"] = CountBy(events, "targetKingdomName"),
                ["intentFamilies"] = CountDiplomacyIntentFamilies(events), ["anomalies"] = anomalies,
                ["recentEvents"] = events.OrderByDescending(x => ReadDouble(x, "worldDay", 0d)).Take(20).ToList()
            };
        }

        private static double LatestSuccessfulDiplomacyEvaluationDay(
            ReignDbConnection connection, string campaignId, string timelineId)
        {
            if (connection == null) return -1d;
            foreach (Dictionary<string, object> row in Program.QuerySql(connection, @"SELECT day_key,counters_json
FROM world_test_chunk_counters
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND subsystem='diplomacy' AND chunk_key LIKE 'evaluation_%'
ORDER BY day_key DESC;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId ?? string.Empty,
                    ["timeline"] = string.IsNullOrWhiteSpace(timelineId)
                        ? "main" : timelineId
                }))
            {
                Dictionary<string, object> counters = TryParseJsonObject(
                    ReadString(row, "counters_json", "{}"))
                    ?? new Dictionary<string, object>();
                if (ReadInt(counters, "completed", 0) > 0
                    && ReadInt(counters, "failures", 0) == 0)
                    return ReadDouble(row, "day_key", -1d);
            }
            return -1d;
        }

        private static string ExpectedAgreementKindForDiplomacyCommand(
            string command)
        {
            switch (Program.NormalizeLookup(command))
            {
                case "sign_trade_agreement": return "trade_agreement";
                case "sign_non_aggression_pact": return "non_aggression_pact";
                case "sign_alliance": return "alliance";
                case "sign_defensive_pact": return "defensive_pact";
                default: return "";
            }
        }

        private static bool WorldTestNativeAgreementExists(
            IEnumerable<Dictionary<string, object>> agreements, string kind,
            string actorKingdomId, string targetKingdomId)
        {
            return (agreements ?? Enumerable.Empty<Dictionary<string, object>>())
                .Any(row => ReadBool(row, "isActive", true)
                    && ReadString(row, "kind", "").Equals(kind,
                        StringComparison.OrdinalIgnoreCase)
                    && ((ReadString(row, "actorKingdomId", "").Equals(
                            actorKingdomId, StringComparison.OrdinalIgnoreCase)
                         && ReadString(row, "targetKingdomId", "").Equals(
                            targetKingdomId, StringComparison.OrdinalIgnoreCase))
                        || (ReadString(row, "actorKingdomId", "").Equals(
                            targetKingdomId, StringComparison.OrdinalIgnoreCase)
                         && ReadString(row, "targetKingdomId", "").Equals(
                            actorKingdomId, StringComparison.OrdinalIgnoreCase))));
        }

        private static Dictionary<string, object> BuildWorldTestRumors(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            double latestDay)
        {
            Dictionary<string, object> rumorSummary = Program.QuerySql(connection, @"
SELECT COUNT(*) AS rumor_count,
COUNT(*) FILTER (WHERE status='active' AND expires_day<=$day) AS expired_active,
COALESCE(MAX(world_day),-1) AS last_occurrence_day
FROM rumor_occurrences
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["day"] = latestDay
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> outcomeSummary = Program.QuerySql(connection, @"
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
WHERE e.timeline_id=$timeline AND e.event_type='social_outcome';",
                new Dictionary<string, object>
                {
                    ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int rumorCount = ReadInt(rumorSummary, "rumor_count", 0);
            int expiredActive = ReadInt(rumorSummary, "expired_active", 0);
            double lastOccurrenceDay = ReadDouble(rumorSummary,
                "last_occurrence_day", -1d);
            int sourceOutcomeCount = ReadInt(outcomeSummary,
                "source_count", 0);
            int processedSourceOutcomes = ReadInt(outcomeSummary,
                "processed_count", 0);
            int failedSourceOutcomes = ReadInt(outcomeSummary,
                "failed_count", 0);
            int pendingSourceOutcomeCount = ReadInt(outcomeSummary,
                "pending_count", 0);
            double oldestPendingDay = ReadDouble(outcomeSummary,
                "oldest_pending_day", -1d);
            double lastProcessedSourceDay = ReadDouble(outcomeSummary,
                "last_processed_day", -1d);
            Dictionary<string, int> funnel =
                WorldTestCounterTotals(connection, campaignId, timelineId, "rumors");
            bool enabled = LatestFeatureFlag(connection, "rumorsEnabled", true);
            string status = !enabled ? "disabled" : failedSourceOutcomes > 0 ? "error"
                : pendingSourceOutcomeCount > ReputationService.SocialWorldHistoryBatchSize * 2 ? "warning"
                : sourceOutcomeCount > 0 && processedSourceOutcomes > 0 ? "healthy"
                : rumorCount == 0 ? "insufficient_data"
                : expiredActive > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (expiredActive > 0) anomalies.Add(expiredActive + " expired rumor occurrences are still marked active.");
            if (pendingSourceOutcomeCount > ReputationService.SocialWorldHistoryBatchSize * 2)
                anomalies.Add(pendingSourceOutcomeCount + " social outcomes are waiting for rumor/reputation processing.");
            if (failedSourceOutcomes > 0)
                anomalies.Add(failedSourceOutcomes + " social outcomes exhausted their processing retries.");
            Dictionary<string, int> statuses = WorldTestGroupedCounts(connection,
                "SELECT status AS key,COUNT(*) AS count FROM rumor_occurrences "
                + "WHERE campaign_id=$campaign AND timeline_id=$timeline GROUP BY status;",
                campaignId, timelineId);
            Dictionary<string, int> sources = WorldTestGroupedCounts(connection,
                "SELECT COALESCE(NULLIF(archetype_id,''),'other') AS key,COUNT(*) AS count "
                + "FROM rumor_occurrences WHERE campaign_id=$campaign AND timeline_id=$timeline "
                + "GROUP BY COALESCE(NULLIF(archetype_id,''),'other');",
                campaignId, timelineId);
            Dictionary<string, int> tagStatuses = WorldTestGroupedCounts(connection,
                "SELECT rst.status AS key,COUNT(*) AS count FROM rumor_subject_tags rst "
                + "JOIN rumor_occurrences ro ON ro.occurrence_id=rst.occurrence_id "
                + "WHERE ro.campaign_id=$campaign AND ro.timeline_id=$timeline GROUP BY rst.status;",
                campaignId, timelineId);
            Dictionary<string, object> supportingCounts = Program.QuerySql(connection, @"
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
 WHERE campaign_id=$campaign AND timeline_id=$timeline) AS reputation_count;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> reach = Program.QuerySql(connection, @"
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
LIMIT 50;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).Select(row => new Dictionary<string, object>
                {
                    ["rumorId"] = ReadString(row, "rumor_id", ""),
                    ["claim"] = ReadString(row, "claim", ""),
                    ["status"] = ReadString(row, "status", ""),
                    ["archetype"] = ReadString(row, "archetype", ""),
                    ["subjectCount"] = ReadInt(row, "subject_count", 0),
                    ["tagCount"] = ReadInt(row, "tag_count", 0),
                    ["promoted"] = ReadInt(row, "promoted", 0) == 1,
                    ["streakCount"] = ReadInt(row, "streak_count", 0)
                }).ToList();
            return new Dictionary<string, object>
            {
                ["name"] = "Character-Owned Social Reputation", ["status"] = status,
                ["latestSuccessfulDay"] = Math.Max(lastOccurrenceDay, lastProcessedSourceDay),
                ["rumorCount"] = rumorCount,
                ["subjectTagCount"] = ReadInt(supportingCounts, "tag_count", 0),
                ["statuses"] = statuses, ["sources"] = sources,
                ["tagStatuses"] = tagStatuses,
                ["promotionResults"] = ReadInt(supportingCounts,
                    "promotion_count", 0),
                ["promotedOccurrences"] = ReadInt(supportingCounts,
                    "promoted_count", 0),
                ["durableReputations"] = ReadInt(supportingCounts,
                    "reputation_count", 0), ["expiredActive"] = expiredActive,
                ["sourceOutcomeCount"] = sourceOutcomeCount,
                ["processedSourceOutcomes"] = processedSourceOutcomes,
                ["pendingSourceOutcomes"] = pendingSourceOutcomeCount,
                ["failedSourceOutcomes"] = failedSourceOutcomes,
                ["oldestPendingSourceDay"] = oldestPendingDay,
                ["funnel"] = funnel,
                ["eligibleHooks"] = funnel.TryGetValue("eligibleHooks", out int eligibleHooks) ? eligibleHooks : 0,
                ["exposureAttempts"] = funnel.TryGetValue("exposureAttempts", out int exposureAttempts) ? exposureAttempts : 0,
                ["exposurePassed"] = funnel.TryGetValue("exposurePassed", out int exposurePassed) ? exposurePassed : 0,
                ["exposureFailed"] = funnel.TryGetValue("exposureFailed", out int exposureFailed) ? exposureFailed : 0,
                ["duplicateSourcesSuppressed"] = funnel.TryGetValue("duplicateSourcesSuppressed", out int duplicates) ? duplicates : 0,
                ["occurrencesCreated"] = funnel.TryGetValue("occurrencesCreated", out int occurrences) ? occurrences : 0,
                ["promotionPassed"] = funnel.TryGetValue("promotionPassed", out int promotionPassed) ? promotionPassed : 0,
                ["promotionFailed"] = funnel.TryGetValue("promotionFailed", out int promotionFailed) ? promotionFailed : 0,
                ["promotionEligible"] = (funnel.TryGetValue("promotionPassed", out int passedPromotions) ? passedPromotions : 0)
                    + (funnel.TryGetValue("promotionFailed", out int failedPromotions) ? failedPromotions : 0),
                ["reach"] = reach, ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, int> WorldTestGroupedCounts(
            ReignDbConnection connection, string sql, string campaignId,
            string timelineId)
        {
            return Program.QuerySql(connection, sql, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).Where(row => !string.IsNullOrWhiteSpace(
                    ReadString(row, "key", "")))
                .ToDictionary(row => ReadString(row, "key", ""),
                    row => ReadInt(row, "count", 0),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildWorldTestRebellions(ReignDbConnection connection, string campaignId, string timelineId, double latestDay)
        {
            List<Dictionary<string, object>> rolls = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "weeklyRolls");
            List<Dictionary<string, object>> movements = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "movements");
            List<Dictionary<string, object>> memberships = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "memberships");
            List<Dictionary<string, object>> recognitionAttempts = Program.QuerySql(connection,
                @"SELECT * FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC, attempt_index DESC LIMIT 100;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            int eligible = rolls.Count(x => ReadBool(x, "wasEligible", false));
            int triggered = rolls.Count(x => ReadBool(x, "triggered", false));
            bool playerIncluded = rolls.Any(x => ReadBool(x, "isPlayerClan", false)) || memberships.Any(x => ReadBool(x, "automaticNpcSelection", false) && ReadBool(x, "isPlayerClan", false));
            double lastRollDay = rolls.Count == 0 ? -1d : rolls.Max(x => ReadDouble(x, "rolledDay", -1d));
            List<int> missingWeekIndexes = MissingRebellionWeekIndexes(rolls);
            bool enabled = LatestFeatureFlag(connection, "rebellionsEnabled", true);
            string status = !enabled ? "disabled" : rolls.Count == 0 ? "insufficient_data"
                : playerIncluded ? "error"
                : latestDay - lastRollDay > 8d || missingWeekIndexes.Count > 0
                    ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (playerIncluded) anomalies.Add("The player appeared in an automatic NPC rebellion roll or side selection.");
            if (lastRollDay >= 0 && latestDay - lastRollDay > 8d) anomalies.Add("Weekly rebellion rolls are overdue by more than one campaign day.");
            if (missingWeekIndexes.Count > 0) anomalies.Add(
                "Weekly rebellion roll sets are missing for week indexes: "
                + string.Join(", ", missingWeekIndexes.Select(value =>
                    value.ToString(CultureInfo.InvariantCulture))) + ".");
            int recognitionQueueFailures = recognitionAttempts.Count(x =>
                ReadString(x, "status", "") == "accepted_queue_failed");
            int recognitionChanceViolations = recognitionAttempts.Count(x =>
                ReadInt(x, "chance_percent", 0) < 1
                || ReadInt(x, "chance_percent", 0)
                    > RebellionService.RebellionRecognitionMaximumChancePercent);
            if (recognitionQueueFailures > 0)
                anomalies.Add("One or more accepted rebel-independence recognition attempts failed to queue their exact negotiated resolution.");
            if (recognitionChanceViolations > 0)
                anomalies.Add("A rebel-independence recognition attempt escaped the hard 1-30 percent acceptance range.");
            if (recognitionQueueFailures > 0 || recognitionChanceViolations > 0)
                status = "error";
            return new Dictionary<string, object>
            {
                ["name"] = "Relationship-Triggered Rebellions", ["status"] = status, ["latestSuccessfulDay"] = lastRollDay,
                ["configuredWeeklyChancePercent"] = RebellionService.RebellionWeeklyOutbreakPercent,
                ["configuredRelationshipThreshold"] = RebellionService.RebellionRelationshipThreshold,
                ["weeklyRollCount"] = rolls.Count, ["eligibleRollCount"] = eligible, ["triggeredRollCount"] = triggered,
                ["observedSuccessPercent"] = eligible == 0 ? 0d : Math.Round(100d * triggered / eligible, 2),
                ["sampleStatus"] = eligible < 30 ? "insufficient_data" : "informational",
                ["missingWeeklyRollSetCount"] = missingWeekIndexes.Count,
                ["missingWeeklyRollWeekIndexes"] = missingWeekIndexes,
                ["playerAutomaticInclusion"] = playerIncluded,
                ["movementCount"] = movements.Count, ["activeCivilWars"] = movements.Count(x => ReadString(x, "stage", "") == "civil_war"),
                ["resolvedCivilWars"] = movements.Count(x => ReadBool(x, "resolutionApplied", false) || ReadString(x, "stage", "") == "resolved"),
                ["resolutionCauses"] = CountBy(movements, "resolutionCause"),
                ["membershipSides"] = CountBy(memberships, "side"),
                ["loneRebellions"] = movements.Count(m => memberships.Count(x => ReadString(x, "movementId", "") == ReadString(m, "movementId", "") && ReadString(x, "side", "") == "rebel") <= 1),
                ["fateOutcomes"] = CountBy(memberships, "fateOutcome"), ["anomalies"] = anomalies,
                ["recognitionAttemptCount"] = recognitionAttempts.Count,
                ["recognitionAcceptedCount"] = recognitionAttempts.Count(x =>
                    ReadString(x, "status", "").StartsWith("accepted",
                        StringComparison.OrdinalIgnoreCase)),
                ["recognitionQueueFailureCount"] = recognitionQueueFailures,
                ["recognitionChanceViolationCount"] = recognitionChanceViolations,
                ["recognitionMaximumChancePercent"] = RebellionService.RebellionRecognitionMaximumChancePercent,
                ["recognitionStatuses"] = CountBy(recognitionAttempts, "status"),
                ["recentRecognitionAttempts"] = recognitionAttempts.Take(20).ToList(),
                ["recentMovements"] = movements.OrderByDescending(x => ReadDouble(x, "updatedDay", 0d)).Take(20).ToList()
            };
        }

        internal static Dictionary<string, object> BuildWorldTestWarEconomy(
            Dictionary<string, object> nativePayload, double latestDay)
        {
            Dictionary<string, object> economy = ReadDictionary(nativePayload,
                "warEconomy") ?? new Dictionary<string, object>();
            bool active = ReadBool(economy, "active", false);
            bool hooksHealthy = ReadBool(economy, "allHooksHealthy", false) && ReadBool(economy, "allModelsHealthy", false);
            bool versioned = ReadInt(economy, "schemaVersion", 0) >= 2;
            var raidFood = ReadDictionary(economy, "raidFood") ?? new Dictionary<string, object>();
            bool raidFoodCurrent = ReadInt(economy, "raidFoodEvidenceVersion", 0) == 1
                && ReadString(raidFood, "schema", "") == "reign-raid-food-v1"
                && ReadBool(raidFood, "hooksHealthy", false) && ReadInt(raidFood, "unsupported", 0) == 0
                && !ReadBool(raidFood, "fixtureTainted", true)
                && !ReadBool(economy, "raidFoodVillagesTruncated", true) && !ReadBool(economy, "raidFoodHoldingsTruncated", true);
            bool fresh = versioned && Math.Abs(latestDay - ReadDouble(economy, "observedDay", -1000)) <= 1
                && DateTime.TryParse(ReadString(economy, "observedUtc", ""), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime observedUtc)
                && (DateTime.UtcNow - observedUtc.ToUniversalTime()).TotalMinutes >= -1
                && (DateTime.UtcNow - observedUtc.ToUniversalTime()).TotalMinutes <= 10;
            var accounting = ReadDictionary(economy, "accounting") ?? new Dictionary<string, object>();
            int unsupported = ReadInt(accounting, "unsupportedEvents", 0), pending = ReadInt(accounting, "pendingTransactions", 0);
            int mismatches = ReadInt(economy, "patrolConservationMismatches", 0);
            int duplicateCreations = ReadInt(economy,
                "retinueDuplicateCreations", 0);
            double reliefShipped = ReadDouble(economy, "reliefFoodShipped", 0d);
            double reliefDelivered = ReadDouble(economy, "reliefFoodDelivered", 0d);
            double reliefLost = ReadDouble(economy, "reliefFoodLost", 0d);
            bool reliefConserved = ReliefFoodIsConserved(
                reliefShipped, reliefDelivered, reliefLost);
            string patchError = ReadString(economy, "lastPatchError", "");
            string status = economy.Count == 0 || !versioned || !fresh ? "insufficient_data"
                : !active || !hooksHealthy || !string.IsNullOrWhiteSpace(patchError)
                    || mismatches > 0 || !reliefConserved || unsupported > 0 || pending > 0 ? "error"
                : duplicateCreations > 0 || !ReadBool(economy, "behavioralCoverageComplete", false) ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (!ReadBool(economy, "behavioralCoverageComplete", false)) anomalies.Add("Native source, reload and balance acceptance remains incomplete; installed hooks do not certify behavior.");
            if (!versioned) anomalies.Add("Legacy economy evidence is readable but cannot establish current source coverage.");
            if (versioned && !fresh) anomalies.Add("Economy evidence is stale or missing its observation clock.");
            if (unsupported > 0) anomalies.Add(unsupported + " recruitment events lack a supported accounting adapter.");
            if (pending > 0) anomalies.Add(pending + " interrupted economy transactions require reconciliation.");
            if (!active) anomalies.Add("The Reign economy Harmony patch group is inactive.");
            if (active && !hooksHealthy) anomalies.Add("One or more required economy hooks or active model owners are missing or replaced.");
            if (!string.IsNullOrWhiteSpace(patchError)) anomalies.Add("Economy patch error: " + patchError);
            if (mismatches > 0) anomalies.Add(mismatches + " patrol troop conservation mismatches were observed.");
            if (!reliefConserved) anomalies.Add("Realm relief food was not conserved between shipped, delivered, and lost totals.");
            if (duplicateCreations > 0) anomalies.Add(duplicateCreations
                + " mobile-party identifiers received household retinues more than once in this process lifetime.");
            Dictionary<string, object> result = new Dictionary<string, object>(economy,
                StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "War Economy and Manpower",
                ["status"] = status,
                ["currentSchemaEvidence"] = versioned, ["evidenceFresh"] = fresh,
                ["behavioralCoverageComplete"] = versioned && fresh && raidFoodCurrent
                    && ReadString(raidFood, "nativeAcceptance", "pending") == "passed"
                    && ReadBool(economy, "behavioralCoverageComplete", false),
                ["raidFoodEvidenceCurrent"] = raidFoodCurrent && fresh,
                ["latestSuccessfulDay"] = economy.Count == 0 ? -1d : latestDay,
                ["anomalies"] = anomalies
            };
            return result;
        }

        internal static bool ReliefFoodIsConserved(
            double shipped, double delivered, double lost)
        {
            if (shipped < -0.01d || delivered < -0.01d || lost < -0.01d)
                return false;
            // Native campaign counters are persisted as cumulative single-precision
            // floats. Thousands of individually conserved shipments can therefore
            // acquire a few parts-per-million of independent summation drift.
            // Keep a strict absolute floor for small samples and a 10 ppm relative
            // allowance for long campaigns; genuine missing food remains an error.
            double tolerance = Math.Max(0.1d, Math.Abs(shipped) * 0.00001d);
            return Math.Abs(shipped - delivered - lost) <= tolerance;
        }

        private static List<int> MissingRebellionWeekIndexes(
            IEnumerable<Dictionary<string, object>> rolls)
        {
            List<int> observed = (rolls
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(row => ReadInt(row, "weekIndex", -1))
                .Where(value => value >= 0).Distinct().OrderBy(value => value)
                .ToList();
            if (observed.Count < 2) return new List<int>();
            HashSet<int> present = new HashSet<int>(observed);
            List<int> missing = new List<int>();
            for (int week = observed[0]; week <= observed[observed.Count - 1]; week++)
                if (!present.Contains(week)) missing.Add(week);
            return missing;
        }
    }
}
