using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Diplomacy module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DiplomacyStore
    {
        internal static List<Dictionary<string, object>> SelectClanAccordEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM clan_accord_event_receipts WHERE timeline_id=$timeline AND event_id=$event;", parameters, transaction);
        }

        internal static void InsertOrIgnoreClanAccordEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_accord_event_receipts(timeline_id,event_id,payload_json)
VALUES($timeline,$event,$json) ON CONFLICT(timeline_id,event_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdateClanAccordEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE clan_accord_event_receipts SET effects_done=1 WHERE timeline_id=$timeline AND event_id=$event;", parameters, transaction);
        }

        internal static void UpdateClanAccordEventReceipts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE clan_accord_event_receipts SET memory_done=1 WHERE timeline_id=$timeline AND event_id=$event;", parameters, transaction);
        }

        internal static void UpsertClanAccordSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_accord_snapshots(timeline_id,player_hero_id,snapshot_json)
VALUES($timeline,$player,$json) ON CONFLICT(timeline_id) DO UPDATE SET player_hero_id=$player,snapshot_json=$json;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanAccordRelationReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT delta FROM clan_accord_relation_receipts
WHERE timeline_id=$timeline AND receipt_id=$receipt AND observer_id=$observer AND target_id=$target;", parameters, transaction);
        }

        internal static void InsertClanAccordRelationReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO clan_accord_relation_receipts(timeline_id,receipt_id,observer_id,target_id,delta)
VALUES($timeline,$receipt,$observer,$target,$delta);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT event_id FROM events WHERE event_id=$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectMemories(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT owner_id FROM memories WHERE event_id=$event;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectClanAccordSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT snapshot_json FROM clan_accord_snapshots WHERE timeline_id=$timeline;", parameters, transaction);
        }

        internal static void Begin(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyOpportunities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT opportunity_id,observer_id,target_id,correlation_id FROM ruler_diplomacy_opportunities
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status IN ('queued','waiting_cooldown');", parameters, transaction);
        }

        internal static void UpdateRulerDiplomacyOpportunities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE ruler_diplomacy_opportunities SET
status='expired',terminal_day=$day,outcome='season_expired',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status IN ('queued','waiting_cooldown') AND expiry_day<$day;", parameters, transaction);
        }

        internal static void Commit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "COMMIT;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRulerRelationshipRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM ruler_relationship_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
AND observer_id=$observer AND target_id=$target;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_chemistry WHERE pair_key=$pair LIMIT 1;", parameters, transaction);
        }

        internal static void InsertRulerRelationshipRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ruler_relationship_rolls(
campaign_id,timeline_id,day_key,world_day,observer_id,target_id,observer_kingdom_id,target_kingdom_id,
compatibility_chance,compatibility_roll,magnitude,delta,before_affinity,after_affinity,before_effective,after_effective,
co_located,correlation_id,created_ts)
VALUES($campaign,$timeline,$day,$worldDay,$observer,$target,$observerKingdom,$targetKingdom,
$chance,$roll,$magnitude,$delta,$beforeAffinity,$afterAffinity,$beforeEffective,$afterEffective,$coLocated,$correlation,$ts);", parameters, transaction);
        }

        internal static void Rollback(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ROLLBACK;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRulerRelationshipCrossings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM ruler_relationship_crossings WHERE campaign_id=$campaign AND timeline_id=$timeline
AND season_index=$season AND observer_id=$observer AND target_id=$target
AND polarity=$polarity AND threshold=$threshold;", parameters, transaction);
        }

        internal static void InsertOrIgnoreRulerRelationshipCrossings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO ruler_relationship_crossings(
campaign_id,timeline_id,season_index,observer_id,target_id,observer_kingdom_id,target_kingdom_id,polarity,
threshold,crossing_day,before_effective,after_effective,bonus_chance,bonus_roll,bonus_passed,opportunity_id,correlation_id,created_ts)
VALUES($campaign,$timeline,$season,$observer,$target,$observerKingdom,$targetKingdom,$polarity,$threshold,$day,
$before,$after,$chance,$roll,$passed,$opportunity,$correlation,$ts);", parameters, transaction);
        }

        internal static void InsertOrIgnoreRulerDiplomacyOpportunities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO ruler_diplomacy_opportunities(
opportunity_id,campaign_id,timeline_id,observer_id,target_id,observer_kingdom_id,target_kingdom_id,
polarity,threshold,season_index,crossing_day,effective_attitude,bonus_chance,bonus_roll,status,expiry_day,
correlation_id,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$observer,$target,$observerKingdom,$targetKingdom,$polarity,$threshold,$season,
$day,$effective,$chance,$roll,'queued',$expiry,$correlation,$ts,$ts);", parameters, transaction);
        }

        internal static void InsertOrIgnoreRulerDiplomacyActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO ruler_diplomacy_activity(
activity_id,campaign_id,timeline_id,world_day,observer_id,target_id,event_type,polarity,status,threshold,
before_value,after_value,roll,chance,action_id,correlation_id,reason,payload_json,created_ts)
VALUES($id,$campaign,$timeline,$day,$observer,$target,$event,$polarity,$status,$threshold,$before,$after,
$roll,$chance,$action,$correlation,$reason,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyOpportunities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM ruler_diplomacy_opportunities
WHERE opportunity_id=$id AND campaign_id=$campaign AND timeline_id=$timeline LIMIT 1;", parameters, transaction);
        }

        internal static void UpdateRulerDiplomacyOpportunities2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE ruler_diplomacy_opportunities SET status=$status,terminal_day=$day,
outcome=$outcome,action_id=$action,updated_ts=$ts WHERE opportunity_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRulerRelationshipRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS completed,
SUM(CASE WHEN delta>0 THEN 1 ELSE 0 END) AS positive,
SUM(CASE WHEN delta<0 THEN 1 ELSE 0 END) AS negative,
SUM(ABS(delta)) AS movement,
SUM(CASE WHEN co_located=1 THEN 1 ELSE 0 END) AS colocated,
MAX(world_day) AS latest_day
FROM ruler_relationship_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRulerRelationshipCrossings2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(bonus_passed) AS passed,
SUM(CASE WHEN bonus_passed=0 THEN 1 ELSE 0 END) AS failed,
SUM(CASE WHEN polarity='positive' THEN 1 ELSE 0 END) AS positive,
SUM(CASE WHEN polarity='negative' THEN 1 ELSE 0 END) AS negative,
MAX(crossing_day) AS latest_day
FROM ruler_relationship_crossings WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyOpportunities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM ruler_diplomacy_opportunities WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY threshold DESC,crossing_day,opportunity_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerRelationshipCrossings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT threshold,COUNT(*) AS count FROM ruler_relationship_crossings
WHERE campaign_id=$campaign AND timeline_id=$timeline GROUP BY threshold;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT SUM(CASE WHEN event_type='relationship_incident' THEN 1 ELSE 0 END) AS feedback,
MAX(CASE WHEN event_type='relationship_incident' THEN world_day ELSE -1 END) AS latest_feedback,
MAX(CASE WHEN event_type IN ('proposal','response','native_receipt') THEN world_day ELSE -1 END) AS latest_diplomacy
FROM ruler_diplomacy_activity WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerRelationshipRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM ruler_relationship_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline
AND observer_id=$observer AND target_id=$target ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyActivity2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM ruler_diplomacy_activity WHERE campaign_id=$campaign AND timeline_id=$timeline
AND observer_id=$observer AND target_id=$target ORDER BY world_day DESC,created_ts DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerRelationshipRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT r.observer_id,r.target_id FROM ruler_relationship_rolls r
LEFT JOIN relationship_pair_provenance p ON p.campaign_id=r.campaign_id AND p.timeline_id=r.timeline_id
 AND p.pair_key=CASE WHEN r.observer_id<r.target_id THEN r.observer_id||'|'||r.target_id ELSE r.target_id||'|'||r.observer_id END
 AND p.source='ruler_diplomacy_contact' AND p.active=1
WHERE r.campaign_id=$campaign AND r.timeline_id=$timeline AND r.day_key=$day AND p.pair_key IS NULL;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerRelationshipRolls3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT observer_id,target_id
FROM ruler_relationship_rolls WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyActivity3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT polarity,payload_json
FROM ruler_diplomacy_activity WHERE campaign_id=$campaign AND timeline_id=$timeline
AND event_type='proposal_response' AND status='accepted' AND polarity<>'';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomaticIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT event_kind,source_id,observer_id,target_id
FROM ruler_diplomatic_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline
GROUP BY event_kind,source_id,observer_id,target_id HAVING COUNT(*)>1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_native_targets WHERE status IN ('pending','claimed') AND $day-world_day>1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerAgreementHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT a.agreement_id FROM ruler_agreement_history a
JOIN ruler_war_origins w ON w.campaign_id=a.campaign_id AND w.timeline_id=a.timeline_id AND w.is_active=1
AND ((w.aggressor_kingdom_id=a.actor_kingdom_id AND w.defender_kingdom_id=a.target_kingdom_id)
 OR (w.aggressor_kingdom_id=a.target_kingdom_id AND w.defender_kingdom_id=a.actor_kingdom_id))
WHERE a.campaign_id=$campaign AND a.timeline_id=$timeline AND a.is_active=1
AND a.kind IN ('trade_agreement','non_aggression_pact','defensive_pact','alliance','guarantee_independence');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerTreatyObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT obligation_id FROM ruler_treaty_obligations
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status IN ('pending','failed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerWarOrigins(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT child.war_id FROM ruler_war_origins child
JOIN ruler_war_origins parent ON parent.campaign_id=child.campaign_id AND parent.timeline_id=child.timeline_id
AND parent.war_id=child.parent_war_id WHERE child.campaign_id=$campaign AND child.timeline_id=$timeline
AND child.origin_kind='defensive_pact' AND parent.origin_kind='defensive_pact';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionBackingRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT movement_id FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status<>'no_eligible_backer'
AND (rebel_roll<1 OR parent_roll<1 OR military_roll<1);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionBackingRequests2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT movement_id FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='accepted' AND $day-world_day>1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRulerDiplomaticIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN event_kind='preexisting_war_seed' THEN 1 ELSE 0 END) AS initial_war_seeds,
SUM(CASE WHEN event_kind='war_declaration' THEN 1 ELSE 0 END) AS war_incidents,
SUM(CASE WHEN event_kind='treaty_breach' THEN 1 ELSE 0 END) AS treaty_breaches,
SUM(CASE WHEN event_kind='defensive_pact_honored' THEN 1 ELSE 0 END) AS defensive_honors,
SUM(CASE WHEN event_kind LIKE 'rebellion_backing_%' THEN 1 ELSE 0 END) AS backing_incidents,
MAX(world_day) AS latest_day FROM ruler_diplomatic_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRebellionBackingRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN status IN ('accepted','completed') THEN 1 ELSE 0 END) AS accepted,
SUM(CASE WHEN status='refused' THEN 1 ELSE 0 END) AS refused,
SUM(CASE WHEN status='no_eligible_backer' THEN 1 ELSE 0 END) AS no_eligible,
SUM(CASE WHEN status='invalidated' THEN 1 ELSE 0 END) AS invalidated,
MAX(world_day) AS latest_day FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRelationshipNativeTargets2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerRelationshipCrossings2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(threshold) AS threshold
FROM ruler_relationship_crossings WHERE campaign_id=$campaign AND timeline_id=$timeline
AND observer_id=$observer AND target_id=$target;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyActivity4(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM ruler_diplomacy_activity
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND ($ruler='' OR observer_id=$ruler OR target_id=$ruler)
AND ($pairA='' OR (observer_id=$pairA AND target_id=$pairB) OR (observer_id=$pairB AND target_id=$pairA))
AND ($polarity='' OR polarity=$polarity) AND ($status='' OR status=$status)
AND ($event='' OR event_type=$event)
AND ($threshold=0 OR threshold=$threshold)
AND world_day>=$fromDay AND world_day<=$toDay
ORDER BY world_day DESC,created_ts DESC LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyOpportunities4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT * FROM ruler_diplomacy_opportunities
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND status IN ('queued','waiting_cooldown') AND expiry_day>=$day
ORDER BY threshold DESC,crossing_day,opportunity_id;", parameters, transaction);
        }

        internal static void UpdateRulerDiplomacyOpportunities3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE ruler_diplomacy_opportunities SET status='waiting_cooldown',updated_ts=$ts
WHERE opportunity_id=$id AND status<>'waiting_cooldown';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountRulerDiplomaticIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) AS count FROM ruler_diplomatic_incidents WHERE incident_id=$id;", parameters, transaction);
        }

        internal static void InsertRulerDiplomaticIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ruler_diplomatic_incidents(
incident_id,campaign_id,timeline_id,world_day,event_kind,source_id,observer_id,target_id,
observer_kingdom_id,target_kingdom_id,affinity_delta,public_subject_id,public_delta,before_effective,
after_effective,breakthroughs_enabled,correlation_id,payload_json,created_ts)
VALUES($id,$campaign,$timeline,$day,$kind,$source,$observer,$target,$observerKingdom,$targetKingdom,
$delta,$publicSubject,$publicDelta,$before,$after,$breakthroughs,$correlation,$payload,$ts);", parameters, transaction);
        }

        internal static void UpsertRulerWarOrigins(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ruler_war_origins(campaign_id,timeline_id,war_id,
aggressor_kingdom_id,defender_kingdom_id,aggressor_ruler_id,defender_ruler_id,declaration_day,origin_kind,
cause,source_action_id,parent_war_id,is_active,ended_day,payload_json)
VALUES($campaign,$timeline,$war,$aggressorKingdom,$defenderKingdom,$aggressor,$defender,$day,$kind,$cause,$action,$parent,$active,$ended,$payload)
ON CONFLICT(campaign_id,timeline_id,war_id) DO UPDATE SET is_active=$active,ended_day=$ended,payload_json=$payload;", parameters, transaction);
        }

        internal static void UpsertRulerAgreementHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ruler_agreement_history(campaign_id,timeline_id,agreement_id,kind,
actor_kingdom_id,target_kingdom_id,created_day,expire_day,is_active,ended_day,end_reason,breaker_kingdom_id,triggering_war_id,payload_json)
VALUES($campaign,$timeline,$id,$kind,$actor,$target,$created,$expire,$active,$ended,$reason,$breaker,$war,$payload)
ON CONFLICT(campaign_id,timeline_id,agreement_id) DO UPDATE SET is_active=$active,ended_day=$ended,
end_reason=$reason,breaker_kingdom_id=$breaker,triggering_war_id=$war,payload_json=$payload;", parameters, transaction);
        }

        internal static void UpsertRulerTreatyObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO ruler_treaty_obligations(campaign_id,timeline_id,obligation_id,
agreement_id,triggering_war_id,resulting_war_id,ally_kingdom_id,ally_ruler_id,defended_kingdom_id,
defended_ruler_id,aggressor_kingdom_id,triggered_day,status,reason,payload_json)
VALUES($campaign,$timeline,$id,$agreement,$trigger,$result,$allyKingdom,$ally,$defendedKingdom,$defended,$aggressor,$day,$status,$reason,$payload)
ON CONFLICT(campaign_id,timeline_id,obligation_id) DO UPDATE SET resulting_war_id=$result,status=$status,reason=$reason,payload_json=$payload;", parameters, transaction);
        }

        internal static void InsertOrIgnoreCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO character_reputations(
campaign_id,timeline_id,subject_id,tag_id,source_occurrence_id,archetype_id,
subject_role,description,reputation_value,acquired_day,catalog_revision,
snapshot_json,status,updated_ts)
VALUES($campaign,$timeline,$subject,$tag,$action,'diplomacy_incident','actor',
$description,$value,$day,0,$snapshot,'active',$ts)
ON CONFLICT(campaign_id,timeline_id,subject_id,tag_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomacyOpportunities5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM ruler_diplomacy_opportunities
WHERE opportunity_id=$id LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"
SELECT MAX(day_key) AS latest_day FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureDailyRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day LIMIT 1;", parameters, transaction);
        }

        internal static void InsertPoliticalPressureIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_incidents(
incident_id,campaign_id,timeline_id,day_key,world_day,archetype_id,polarity,channel,severity,severity_rank,
pressure_amount,relationship_penalty,origin_kingdom_id,target_kingdom_id,origin_kingdom_name,target_kingdom_name,
origin_ruler_id,target_ruler_id,origin_ruler_name,target_ruler_name,origin_war_count,target_war_count,hostile_chance,polarity_roll,
origin_clans_json,origin_lords_json,target_clans_json,target_lords_json,trait_routes_json,trait_rolls_json,origin_stance,target_stance,
origin_pressure_before,origin_pressure_after,target_pressure_before,target_pressure_after,headline,narrative,
llm_selector_status,llm_narration_status,notice_status,correlation_id,status,payload_json,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$day,$worldDay,$archetype,$polarity,$channel,$severity,$severityRank,$pressureAmount,$relationshipPenalty,
$originKingdom,$targetKingdom,$originKingdomName,$targetKingdomName,$originRuler,$targetRuler,$originRulerName,$targetRulerName,
$originWars,$targetWars,$hostileChance,$polarityRoll,$originClans,$originLords,$targetClans,$targetLords,$routes,$rolls,$originStance,
$targetStance,$originBefore,$originAfter,$targetBefore,$targetAfter,$headline,$narrative,'pending','pending','preparing',$correlation,
'completed',$payload,$ts,$ts);", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_incidents SET narrative=$narrative,
llm_selector_status=$selector,llm_narration_status=$narration,notice_status='ready',updated_ts=$ts WHERE incident_id=$id;", parameters, transaction);
        }

        internal static void UpsertPoliticalPressureDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_daily_rolls(campaign_id,timeline_id,day_key,world_day,
chance,roll,passed,status,origin_kingdom_id,incident_id,created_ts)
VALUES($campaign,$timeline,$day,$worldDay,$chance,$roll,$passed,$status,$origin,$incident,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=excluded.world_day,chance=excluded.chance,roll=excluded.roll,passed=excluded.passed,
status=excluded.status,origin_kingdom_id=excluded.origin_kingdom_id,incident_id=excluded.incident_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> UpsertPoliticalPressureDailyRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO political_pressure_daily_rolls(
campaign_id,timeline_id,day_key,world_day,chance,roll,passed,status,
origin_kingdom_id,incident_id,created_ts)
VALUES($campaign,$timeline,$day,$worldDay,$chance,$roll,0,'processing','','',$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=excluded.world_day,chance=excluded.chance,roll=excluded.roll,
passed=0,status='processing',origin_kingdom_id='',incident_id='',created_ts=excluded.created_ts
WHERE political_pressure_daily_rolls.status='processing'
AND political_pressure_daily_rolls.created_ts<$stale
RETURNING day_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(world_day) AS day FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND origin_kingdom_id=$kingdom;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT MAX(world_day) AS day FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND
((origin_kingdom_id=$origin AND target_kingdom_id=$target) OR (origin_kingdom_id=$target AND target_kingdom_id=$origin));", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureIncidents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT archetype_id FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND origin_kingdom_id=$origin AND target_kingdom_id=$target AND world_day>$cutoff;", parameters, transaction);
        }

        internal static void InsertOrIgnorePoliticalPressureNativeEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_native_effects(effect_id,incident_id,campaign_id,timeline_id,
world_day,effect_type,target_id,amount,status,created_ts,updated_ts)
VALUES($id,$incident,$campaign,$timeline,$day,'settlement_loyalty',$target,-10,'pending',$ts,$ts)
ON CONFLICT(effect_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT pressure_value FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> InsertOrIgnorePoliticalPressureDeltaReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO political_pressure_delta_receipts
(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,incident_id,requested_delta,world_day)
VALUES($campaign,$timeline,$actor,$target,$incident,$delta,$day)
ON CONFLICT(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,incident_id) DO NOTHING RETURNING incident_id;", parameters, transaction);
        }

        internal static void EnsurePoliticalPressureDirection(ReignDbConnection connection, Dictionary<string, object> parameters,
            ReignDbTransaction transaction)
        {
            ReignDatabase.QuerySql(connection, @"INSERT INTO political_pressure_state(
campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,revision) VALUES($campaign,$timeline,$actor,$target,0)
ON CONFLICT(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id) DO NOTHING;", parameters, transaction);
        }

        internal static int LockPoliticalPressureDirection(ReignDbConnection connection, Dictionary<string, object> parameters,
            ReignDbTransaction transaction)
        {
            var rows = ReignDatabase.QuerySql(connection, @"SELECT pressure_value FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target
FOR UPDATE;", parameters, transaction);
            if (rows.Count != 1) throw new InvalidOperationException("The exact political pressure direction disappeared.");
            return rows[0]["pressure_value"] switch
            {
                int value => value,
                long value => checked((int)value),
                _ => throw new InvalidOperationException("The stored political pressure is not an integer.")
            };
        }

        internal static List<Dictionary<string, object>> UpsertPoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"INSERT INTO political_pressure_state(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,
actor_kingdom_name,target_kingdom_name,actor_ruler_id,target_ruler_id,pressure_value,dominant_channel,updated_day,last_incident_id,revision)
VALUES($campaign,$timeline,$actor,$target,$actorName,$targetName,$actorRuler,$targetRuler,$value,$channel,$day,$incident,1)
ON CONFLICT(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id) DO UPDATE SET
actor_kingdom_name=$actorName,target_kingdom_name=$targetName,actor_ruler_id=$actorRuler,target_ruler_id=$targetRuler,
pressure_value=$value,dominant_channel=$channel,updated_day=$day,last_incident_id=$incident,revision=political_pressure_state.revision+1 RETURNING pressure_value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND (pressure_value>=10 OR pressure_value<=-10)
ORDER BY ABS(pressure_value) DESC,updated_day,actor_kingdom_id,target_kingdom_id;", parameters, transaction);
        }

        internal static void InsertOrIgnorePoliticalPressureActionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_action_rolls(campaign_id,timeline_id,day_key,world_day,
actor_kingdom_id,target_kingdom_id,pressure_value,polarity,channel,chance,roll,passed,selected,status,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$worldDay,$actor,$target,$value,$polarity,$channel,$chance,$roll,$passed,0,'evaluated',$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,actor_kingdom_id,target_kingdom_id) DO NOTHING;", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureActionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_action_rolls SET selected=1,status='selected',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND actor_kingdom_id=$actor AND target_kingdom_id=$target;", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_state SET pressure_value=0,dominant_channel='',last_action_day=$day,
revision=revision+1 WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target;", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureActionRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_action_rolls SET status='resolved',action_id=$action,outcome=$outcome,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target AND selected=1 AND status='selected';", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureActionRolls3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_action_rolls
SET status='resolved',outcome='no_credible_action',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND actor_kingdom_id=$actor AND target_kingdom_id=$target
AND selected=1 AND status='selected';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureIncidents4(ReignDbConnection connection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND notice_status IN ('ready','fetched')
ORDER BY world_day,incident_id LIMIT " + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureNativeEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT effect_id,effect_type,target_id,amount,status
FROM political_pressure_native_effects WHERE incident_id=$incident ORDER BY effect_id;", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE political_pressure_incidents SET notice_status='fetched',updated_ts=$ts WHERE incident_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> ReadOwnedPoliticalPressureEffects(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT effect_id,effect_type,target_id,amount,status
FROM political_pressure_native_effects WHERE incident_id=$incident AND campaign_id=$campaign
AND timeline_id=$timeline AND status='pending' ORDER BY effect_id;", parameters, transaction);
        }

        internal static void MarkOwnedPoliticalPressureNoticeFetched(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_incidents SET notice_status='fetched',updated_ts=$ts
WHERE incident_id=$incident AND campaign_id=$campaign AND timeline_id=$timeline
AND notice_status IN ('ready','fetched');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureState3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT actor_kingdom_id,target_kingdom_id,pressure_value,dominant_channel
FROM political_pressure_state WHERE campaign_id=$campaign AND timeline_id=$timeline
ORDER BY actor_kingdom_id,target_kingdom_id LIMIT 1025;", parameters, transaction);
        }

        internal static bool LockPoliticalPressureAcknowledgement(ReignDbConnection connection,
            Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT incident_id FROM political_pressure_incidents
WHERE incident_id=$incident AND campaign_id=$campaign AND timeline_id=$timeline FOR UPDATE;",
                parameters, transaction).Count == 1;
        }

        internal static List<Dictionary<string, object>> LockPoliticalPressureAcknowledgementEffects(
            ReignDbConnection connection, Dictionary<string, object> parameters, ReignDbTransaction transaction)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT effect_id FROM political_pressure_native_effects
WHERE incident_id=$incident AND campaign_id=$campaign AND timeline_id=$timeline ORDER BY effect_id FOR UPDATE;",
                parameters, transaction);
        }

        internal static void UpdatePoliticalPressureNativeEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_native_effects SET status=$status,before_value=$before,
after_value=$after,receipt_day=$day,error=$error,updated_ts=$ts
WHERE effect_id=$id AND incident_id=$incident AND campaign_id=$campaign
AND timeline_id=$timeline AND status='pending';", parameters, transaction);
        }

        internal static void UpdatePoliticalPressureIncidents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE political_pressure_incidents SET notice_status='acknowledged',updated_ts=$ts
WHERE incident_id=$incident AND campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static void InsertOrIgnorePoliticalPressureActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_activity(activity_id,campaign_id,timeline_id,world_day,
incident_id,actor_kingdom_id,target_kingdom_id,actor_ruler_id,target_ruler_id,event_type,polarity,channel,severity,status,
before_value,after_value,roll,chance,action_id,correlation_id,reason,payload_json,created_ts)
VALUES($id,$campaign,$timeline,$day,$incident,$actor,$target,$actorRuler,$targetRuler,$type,$polarity,$channel,$severity,
$status,$before,$after,$roll,$chance,$action,$correlation,$reason,$payload,$ts) ON CONFLICT(activity_id) DO NOTHING;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN passed=1 THEN 1 ELSE 0 END) AS passed,MAX(world_day) AS latest,
MIN(day_key) AS earliest_day,MAX(day_key) AS latest_day FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN polarity='hostile' THEN 1 ELSE 0 END) AS hostile,SUM(CASE WHEN polarity='peaceful' THEN 1 ELSE 0 END) AS peaceful,
SUM(CASE WHEN origin_stance='support_lords' THEN 1 ELSE 0 END)+SUM(CASE WHEN target_stance='support_lords' THEN 1 ELSE 0 END) AS supported,
SUM(CASE WHEN origin_stance='preserve_relations' THEN 1 ELSE 0 END)+SUM(CASE WHEN target_stance='preserve_relations' THEN 1 ELSE 0 END) AS refused,
MAX(world_day) AS latest FROM political_pressure_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS directions,
SUM(CASE WHEN pressure_value>0 THEN 1 ELSE 0 END) AS hostile,SUM(CASE WHEN pressure_value<0 THEN 1 ELSE 0 END) AS peaceful,
SUM(CASE WHEN pressure_value=0 THEN 1 ELSE 0 END) AS neutral,AVG(ABS(pressure_value)) AS average_magnitude,
MAX(ABS(pressure_value)) AS maximum_magnitude FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureActionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN passed=1 THEN 1 ELSE 0 END) AS passed,SUM(CASE WHEN selected=1 THEN 1 ELSE 0 END) AS selected,
SUM(CASE WHEN status='resolved' AND outcome LIKE '%refus%' THEN 1 ELSE 0 END) AS refused FROM political_pressure_action_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureNativeEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,SUM(CASE WHEN status='applied' THEN 1 ELSE 0 END) AS applied,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed FROM political_pressure_native_effects
WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRulerDiplomaticIncidents2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_preserved' THEN 1 ELSE 0 END) AS preserved,
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_sacrificed' THEN 1 ELSE 0 END) AS sacrificed,
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_preserved' THEN affinity_delta ELSE 0 END) AS gained,
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_sacrificed' THEN -affinity_delta ELSE 0 END) AS lost
FROM ruler_diplomatic_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureState2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND (pressure_value<-100 OR pressure_value>100);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureNativeEffects2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM political_pressure_native_effects
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='pending' AND world_day<$cutoff;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureIncidents3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND
(trait_rolls_json IS NULL OR trait_rolls_json='' OR origin_stance='' OR target_stance='');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureState4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT actor_kingdom_id,target_kingdom_id,
actor_kingdom_name,target_kingdom_name,actor_ruler_id,target_ruler_id,pressure_value,dominant_channel,
updated_day,last_incident_id,last_action_day,revision FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY actor_kingdom_name,target_kingdom_name;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureIncidents5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT channel,COUNT(*) AS count FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline GROUP BY channel;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureActivity(ReignDbConnection connection, object projection, object part, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT a.*,i.incident_id AS detail_id," + projection + @" FROM political_pressure_activity a
LEFT JOIN political_pressure_incidents i ON i.incident_id=a.incident_id
AND i.campaign_id=a.campaign_id AND i.timeline_id=a.timeline_id
WHERE a.campaign_id=$campaign AND a.timeline_id=$timeline ORDER BY a.world_day DESC,a.created_ts DESC LIMIT "
                + part + ";", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountPoliticalPressureDailyRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id='main' AND day_key=9;", parameters, transaction);
        }

        internal static void InsertPoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO political_pressure_state(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,pressure_value)
VALUES($campaign,'main','ours','hostile',60),($campaign,'main','ours','resolved',0),($campaign,'other','ours','foreign-timeline',100);", parameters, transaction);
        }
    }
}
