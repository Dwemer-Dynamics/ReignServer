using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static void InsertRelationshipPersonalities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,'INTJ','Fixture','Fixture','self_test',50,1,$traits,1,1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks
WHERE timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM relationship_pair_lifecycle;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingEncounters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE timeline_id=$timeline AND same_sex=1
AND (conception_attempt_id<>'' OR conception_id<>'' OR conceived=1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_fling_rolls WHERE timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingSparks2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE timeline_id=$timeline
AND world_day=$day;", parameters, transaction);
        }

        internal static void UpsertRelationshipFlingProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_fling_profiles(
hero_id,judgment,source,updated_ts) VALUES($hero,$judgment,$source,$ts)
ON CONFLICT(hero_id) DO UPDATE SET judgment=$judgment,source=$source,updated_ts=$ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_fling_profiles;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT hero_id,MAX(world_day) AS world_day FROM (
 SELECT hero_a_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE timeline_id=$timeline AND status='completed'
 UNION ALL
 SELECT hero_b_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE timeline_id=$timeline AND status='completed'
) flings GROUP BY hero_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairLifecycle2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pair_key
FROM relationship_pair_lifecycle WHERE lover_active=1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectConceptions4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT mother_id FROM conceptions
WHERE status='pending_game';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_fling_sparks(
campaign_id,timeline_id,world_day,pair_key,hero_a_id,hero_b_id,
location_kind,location_id,chance_percent,roll,passed,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$pair,$a,$b,$kind,$location,$chance,
$roll,$passed,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND world_day=$day AND pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_fling_rolls
SET status='spark_failed',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND world_day=$day
AND hero_id IN ($a,$b);", parameters, transaction);
        }

        internal static void UpdateRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_fling_sparks
SET encounter_id=$encounter,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND world_day=$day AND pair_key=$pair;", parameters, transaction);
        }

        internal static void UpdateRelationshipFlingRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_fling_rolls
SET status='paired',encounter_id=$encounter,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND world_day=$day
AND hero_id IN ($a,$b);", parameters, transaction);
        }

        internal static void InsertOrIgnoreRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_fling_rolls(
campaign_id,timeline_id,world_day,hero_id,location_kind,location_id,
judgment,chance_percent,roll,passed,status,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$hero,$kind,$location,$judgment,$chance,
$roll,$passed,$status,$ts,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS rolls,SUM(passed) AS passes,
SUM(CASE WHEN status='passed_unmatched' THEN 1 ELSE 0 END) AS unmatched
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingSparks3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipFlingEncounters3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS pairings,SUM(same_sex) AS same_sex,
SUM(CASE WHEN same_sex=0 THEN 1 ELSE 0 END) AS opposite_sex,
SUM(short_affair) AS short_affairs,
SUM(CASE WHEN short_affair=1 THEN 1 ELSE 0 END) AS fixed_discovery_attempts,
SUM(fixed_discovered) AS fixed_discoveries,
SUM(CASE WHEN pregnancy_discovery_roll>=0 THEN 1 ELSE 0 END) AS pregnancy_discovery_attempts,
SUM(pregnancy_discovered) AS pregnancy_discoveries,
SUM(CASE WHEN conception_attempt_id<>'' THEN 1 ELSE 0 END) AS conception_attempts,
SUM(conceived) AS conceptions,
SUM(CASE WHEN conception_action_id<>'' THEN 1 ELSE 0 END) AS conception_actions
FROM relationship_fling_encounters WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day AND status='completed';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipFlingEncounters2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT encounter_id FROM relationship_fling_encounters
WHERE encounter_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_fling_encounters(
encounter_id,campaign_id,timeline_id,world_day,pair_key,hero_a_id,hero_b_id,
location_kind,location_id,same_sex,short_affair,status,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$day,$pair,$a,$b,$kind,$location,$sameSex,
$shortAffair,'processing',$ts,$ts);", parameters, transaction);
        }

        internal static void UpdateRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_fling_encounters SET
fixed_discovery_chance=$fixedChance,fixed_discovery_roll=$fixedRoll,
fixed_discovered=$fixedFound,pregnancy_discovery_chance=$pregnancyChance,
pregnancy_discovery_roll=$pregnancyRoll,pregnancy_discovered=$pregnancyFound,
discovery_applied=$discoveryApplied,rumor_id=$rumor,
conception_attempt_id=$attempt,conception_id=$conception,
conception_chance=$conceptionChance,conception_roll=$conceptionRoll,
conceived=$conceived,conception_action_id=$action,status='completed',
payload_json=$payload,updated_ts=$ts WHERE encounter_id=$id;", parameters, transaction);
        }

        internal static void InsertOrIgnoreConceptionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conception_attempts(
attempt_id,source,event_id,mother_id,father_id,mother_age,chance,roll,success,
world_day,payload_json,created_ts,status)
VALUES($id,'npc_fling',$event,$mother,$father,$age,$chance,$roll,$success,
$day,$payload,$ts,'resolved');", parameters, transaction);
        }

        internal static void InsertOrIgnoreConceptions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$bio,$legal,$day,$due,'pending_game',$secrecy,
$payload,$ts,$ts);", parameters, transaction);
        }

        internal const string SelectNotableMbtiProfilesSql = "SELECT hero_id,percentages_json FROM notable_mbti_profiles";

        internal const string SelectRelationshipPersonalitiesSql = "SELECT hero_id,traits_json FROM relationship_personalities";

        internal const string InsertOrIgnoreRelationshipFlingRollsSql = @"
INSERT INTO relationship_fling_rolls(campaign_id,timeline_id,world_day,hero_id,
location_kind,location_id,judgment,chance_percent,roll,passed,status,created_ts,updated_ts)
SELECT campaign,timeline,day,hero,kind,location,judgment,chance,roll,passed,status,ts,ts
FROM jsonb_to_recordset(@rows) AS r(campaign text,timeline text,day integer,hero text,
kind text,location text,judgment integer,chance integer,roll integer,passed integer,status text,ts bigint)
ON CONFLICT(campaign_id,timeline_id,world_day,hero_id) DO NOTHING;";
    }
}
