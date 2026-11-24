using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class RelationshipsStore
    {
        internal static List<Dictionary<string, object>> CountClanConflictDaySweeps(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
COUNT(*) AS total,SUM(eligible_kingdoms) AS expected,SUM(processed_kingdoms) AS processed,
SUM(CASE WHEN status<>'completed' THEN 1 ELSE 0 END) AS incomplete,MAX(world_day) AS latest
FROM clan_conflict_day_sweeps WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
COUNT(*) AS total,SUM(CASE WHEN passed=1 THEN 1 ELSE 0 END) AS passed,
SUM(CASE WHEN status='roll_failed' THEN 1 ELSE 0 END) AS failed,
SUM(CASE WHEN status='no_eligible_pair' THEN 1 ELSE 0 END) AS no_pair,
MAX(world_day) AS latest FROM clan_conflict_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
COUNT(*) AS total,SUM(CASE WHEN mediation_succeeded=1 THEN 1 ELSE 0 END) AS mediated,
SUM(CASE WHEN mediation_succeeded=0 THEN 1 ELSE 0 END) AS feuds,
SUM(CASE WHEN notice_status='acknowledged' THEN 1 ELSE 0 END) AS acknowledged,
SUM(CASE WHEN notice_status IN ('ready','fetched') THEN 1 ELSE 0 END) AS pending_notices,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed,MAX(world_day) AS latest
FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictRelationshipEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
COUNT(*) AS total,SUM(CASE WHEN status='applied' THEN 1 ELSE 0 END) AS applied,
SUM(CASE WHEN status IN ('pending','applying') THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed
FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictDaySweeps(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
day_key,eligible_kingdom_ids_json FROM clan_conflict_day_sweeps
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY day_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
kingdom_id,MAX(kingdom_name) AS kingdom_name,COUNT(*) AS observed,
SUM(CASE WHEN passed=1 THEN 1 ELSE 0 END) AS passed,
SUM(CASE WHEN incident_id<>'' THEN 1 ELSE 0 END) AS incidents,
MIN(day_key) AS first_day,MAX(day_key) AS latest_day
FROM clan_conflict_daily_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY kingdom_id ORDER BY kingdom_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY world_day DESC,incident_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictRelationshipEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
effect_kind,COUNT(*) AS count,SUM(amount) AS net,MIN(amount) AS minimum,MAX(amount) AS maximum,
SUM(CASE WHEN status='applied' THEN 1 ELSE 0 END) AS applied,
SUM(CASE WHEN status IN ('pending','applying') THEN 1 ELSE 0 END) AS pending,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed
FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY effect_kind ORDER BY effect_kind;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictRelationshipEffects2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM (
SELECT incident_id,effect_kind,observer_id,target_id,COUNT(*) AS copies
FROM clan_conflict_relationship_effects WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY incident_id,effect_kind,observer_id,target_id HAVING COUNT(*)>1) AS duplicate_rows;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictDailyRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT day_key,kingdom_id FROM clan_conflict_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictDailyRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM clan_conflict_daily_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline AND chance<>8;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictRelationshipEffects2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT e.*,i.mediation_succeeded
FROM clan_conflict_relationship_effects e JOIN clan_conflict_incidents i ON i.incident_id=e.incident_id
WHERE e.campaign_id=$campaign AND e.timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictDailyRolls3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM clan_conflict_daily_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountClanConflictIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictDaySweeps2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MAX(day_key) AS latest_day FROM clan_conflict_day_sweeps
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='completed';", parameters, transaction);
        }

        internal static void UpsertClanConflictDaySweeps(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_conflict_day_sweeps(
campaign_id,timeline_id,day_key,world_day,eligible_kingdoms,processed_kingdoms,
eligible_kingdom_ids_json,status,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$worldDay,$eligible,0,$eligibleIds,'processing',$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=$worldDay,eligible_kingdoms=$eligible,eligible_kingdom_ids_json=$eligibleIds,
status='processing',updated_ts=$ts;", parameters, transaction);
        }

        internal static void UpdateClanConflictDaySweeps(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_day_sweeps SET
processed_kingdoms=$processed,status='completed',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictDailyRolls3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM clan_conflict_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND kingdom_id=$kingdom LIMIT 1;", parameters, transaction);
        }

        internal static void InsertOrIgnoreClanConflictDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_conflict_daily_rolls(
campaign_id,timeline_id,day_key,world_day,kingdom_id,kingdom_name,ruler_id,chance,roll,passed,status,
incident_id,eligible_clan_count,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$worldDay,$kingdom,$kingdomName,$ruler,$chance,$roll,$passed,$status,'',$eligible,$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,kingdom_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdateClanConflictDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_daily_rolls SET
status='incident_created',incident_id=$incident,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND kingdom_id=$kingdom;", parameters, transaction);
        }

        internal static void InsertOrIgnoreClanConflictIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_conflict_incidents(
incident_id,campaign_id,timeline_id,day_key,world_day,kingdom_id,kingdom_name,ruler_id,ruler_name,
clan_a_id,clan_a_name,clan_b_id,clan_b_name,leader_a_id,leader_b_id,involved_a_json,involved_b_json,
involved_a_names_json,involved_b_names_json,archetype_id,category,headline,narrative,
trait_votes_json,average_relation_a,average_relation_b,winning_side,
charm_skill,charm_chance_bp,charm_roll,mediation_succeeded,relationship_effect_count,notice_status,status,
last_error,payload_json,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$day,$worldDay,$kingdom,$kingdomName,$ruler,$rulerName,
$clanA,$clanAName,$clanB,$clanBName,$leaderA,$leaderB,$involvedA,$involvedB,
$involvedNamesA,$involvedNamesB,$archetype,$category,$headline,$narrative,$votes,$averageA,$averageB,
$winner,$charm,$chance,$roll,$mediated,$effectCount,'pending','effects_pending','',
$payload,$ts,$ts) ON CONFLICT(incident_id) DO NOTHING;", parameters, transaction);
        }

        internal static void InsertOrIgnoreClanConflictRelationshipEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_conflict_relationship_effects(
effect_id,incident_id,campaign_id,timeline_id,world_day,effect_kind,observer_id,target_id,amount,status,created_ts,updated_ts)
VALUES($effect,$incident,$campaign,$timeline,$day,$kind,$observer,$target,$amount,'pending',$ts,$ts)
ON CONFLICT(effect_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictRelationshipEffects3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT incident_id
FROM clan_conflict_relationship_effects
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status IN ('pending','applying')
ORDER BY incident_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictIncidents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM clan_conflict_incidents
WHERE incident_id=$incident LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictRelationshipEffects4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM clan_conflict_relationship_effects
WHERE incident_id=$incident AND status IN ('pending','applying') ORDER BY effect_id;", parameters, transaction);
        }

        internal static void UpdateClanConflictRelationshipEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE clan_conflict_relationship_effects SET status='applying',updated_ts=$ts WHERE effect_id=$effect AND status='pending';", parameters, transaction);
        }

        internal static void UpdateClanConflictRelationshipEffects2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_relationship_effects SET
status='applied',before_affinity=$before,after_affinity=$after,last_error='',updated_ts=$ts WHERE effect_id=$effect;", parameters, transaction);
        }

        internal static void UpdateClanConflictRelationshipEffects3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_relationship_effects SET
status='failed',last_error=$error,updated_ts=$ts WHERE effect_id=$effect;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictRelationshipEffects5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
SUM(CASE WHEN status='applied' THEN 1 ELSE 0 END) AS applied,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed,
SUM(CASE WHEN status IN ('pending','applying') THEN 1 ELSE 0 END) AS pending
FROM clan_conflict_relationship_effects WHERE incident_id=$incident;", parameters, transaction);
        }

        internal static void UpdateClanConflictIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_incidents SET
status=$status,notice_status=$notice,last_error=CASE WHEN $failed>0 THEN 'One or more relationship effects failed.' ELSE '' END,
updated_ts=$ts WHERE incident_id=$incident;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry7(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
hero_a_id,hero_b_id,affinity_a_to_b,affinity_b_to_a FROM relationship_pair_chemistry
WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanConflictIncidents4(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM clan_conflict_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline
AND notice_status IN ('ready','fetched') ORDER BY world_day,incident_id LIMIT "
                    + part + ";", parameters, transaction);
        }

        internal static void UpdateClanConflictIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_incidents SET
notice_status='fetched',updated_ts=$ts WHERE incident_id=$incident;", parameters, transaction);
        }

        internal static void MarkOwnedClanConflictNoticeFetched(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE clan_conflict_incidents SET notice_status='fetched',updated_ts=$ts
WHERE incident_id=$incident AND campaign_id=$campaign AND timeline_id=$timeline
AND notice_status IN ('ready','fetched');", parameters, transaction);
        }

        internal static bool AcknowledgeClanConflictIncident(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"UPDATE clan_conflict_incidents SET
notice_status='acknowledged',updated_ts=$ts WHERE incident_id=$incident
AND campaign_id=$campaign AND timeline_id=$timeline RETURNING incident_id;", parameters, transaction).Count == 1;
        }
    }
}
