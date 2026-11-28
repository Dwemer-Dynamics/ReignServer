using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Rebellion module's SQL statements; every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class RebellionStore
    {
        internal static List<Dictionary<string, object>> SelectRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rebellion_movements WHERE campaign_id=$campaign ORDER BY updated_day DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionMovements2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rebellion_movements WHERE movement_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rebellion_memberships WHERE movement_id=$id ORDER BY support_score DESC;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionTransitions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM rebellion_transitions WHERE movement_id=$id ORDER BY world_day;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionBackingRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND movement_id=$id ORDER BY world_day DESC LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionRecognitionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$id
ORDER BY attempt_index DESC LIMIT 20;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionDrafts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT terms_hash FROM negotiated_action_drafts WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static void DeleteNegotiatedActionApprovals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM negotiated_action_approvals WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static void UpsertNegotiatedActionDrafts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO negotiated_action_drafts(negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,
second_kingdom_id,first_ruler_id,second_ruler_id,command,political_result,terms_json,terms_hash,status,created_day,updated_day,expires_day,queued_action_id,payload_json)
VALUES($id,$campaign,$timeline,$broker,$firstKingdom,$secondKingdom,$firstRuler,$secondRuler,$command,$result,$terms,$hash,'draft',
COALESCE((SELECT created_day FROM negotiated_action_drafts WHERE negotiation_id=$id),$day),$day,$expires,'',$payload);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionDrafts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM negotiated_action_drafts WHERE negotiation_id=$id AND campaign_id=$campaign;", parameters, transaction);
        }

        internal static void UpdateNegotiatedActionDrafts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='expired',updated_day=$day WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static void UpsertNegotiatedActionApprovals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO negotiated_action_approvals(negotiation_id,ruler_hero_id,kingdom_id,terms_hash,status,approved_day,reason)
VALUES($id,$ruler,$kingdom,$hash,$status,$day,$reason);", parameters, transaction);
        }

        internal static void UpdateNegotiatedActionDrafts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='rejected',updated_day=$day WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionApprovals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM negotiated_action_approvals WHERE negotiation_id=$id AND status='approved' AND terms_hash=$hash;", parameters, transaction);
        }

        internal static void UpdateNegotiatedActionDrafts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='awaiting_approval',updated_day=$day WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static void UpdateNegotiatedActionDrafts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='failed',updated_day=$day,payload_json=$payload WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static void UpdateNegotiatedActionDrafts5(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='queued',queued_action_id=$action,updated_day=$day WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionDrafts3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM negotiated_action_drafts WHERE campaign_id=$campaign ORDER BY updated_day DESC LIMIT 200;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionApprovals2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM negotiated_action_approvals WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionDrafts4(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM negotiated_action_drafts WHERE negotiation_id=$id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectNegotiatedActionApprovals3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM negotiated_action_approvals WHERE negotiation_id=$id ORDER BY approved_day;", parameters, transaction);
        }

        internal static void UpdateNegotiatedActionDrafts6(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE negotiated_action_drafts SET status=$status,updated_day=$day,payload_json=$payload
WHERE campaign_id=$campaign AND queued_action_id=$action;", parameters, transaction);
        }

        internal static void UpdateRebellionBackingRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE rebellion_backing_requests SET status=$status,
reason=CASE WHEN $terminal=1 THEN $reason ELSE reason END,payload_json=$payload
WHERE campaign_id=$campaign AND action_id=$action;", parameters, transaction);
        }

        internal static void UpsertSchemaMeta(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES($key,$value)
ON CONFLICT(key) DO UPDATE SET value=$value;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionBackingRequests2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$movement LIMIT 1;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionRecognitionAttempts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT *
FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$movement
ORDER BY attempt_index DESC LIMIT 1;", parameters, transaction);
        }

        internal static void InsertNegotiatedActionDrafts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO negotiated_action_drafts(
negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,second_kingdom_id,
first_ruler_id,second_ruler_id,command,political_result,terms_json,terms_hash,status,
created_day,updated_day,expires_day,queued_action_id,payload_json)
VALUES($id,$campaign,$timeline,$broker,$rebelKingdom,$parentKingdom,$rebelRuler,$parentRuler,
'resolve_civil_war','recognized_independence',$terms,$hash,'awaiting_approval',$day,$day,$expires,'',$payload);", parameters, transaction);
        }

        internal static void InsertNegotiatedActionApprovals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO negotiated_action_approvals(
negotiation_id,ruler_hero_id,kingdom_id,terms_hash,status,approved_day,reason)
VALUES($id,$ruler,$kingdom,$hash,'approved',$day,$reason);", parameters, transaction);
        }

        internal static void InsertRebellionRecognitionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rebellion_recognition_attempts(
attempt_id,campaign_id,timeline_id,movement_id,attempt_index,world_day,rebel_kingdom_id,
parent_kingdom_id,rebel_ruler_id,parent_ruler_id,status,eligible,chance_percent,roll,
relationship_attitude,rebel_strength,parent_strength,rebel_fiefs,parent_fiefs,parent_other_wars,
parent_judgment,parent_pragmatism,parent_mercy,parent_pride,reason,negotiation_id,action_id,payload_json,created_ts)
VALUES($attempt,$campaign,$timeline,$movement,$index,$day,$rebelKingdom,$parentKingdom,$rebelRuler,
$parentRuler,$status,$eligible,$chance,$roll,$attitude,$rebelStrength,$parentStrength,$rebelFiefs,
$parentFiefs,$otherWars,$judgment,$pragmatism,$mercy,$pride,$reason,$negotiation,$action,$payload,$ts);", parameters, transaction);
        }

        internal static void InsertRebellionBackingRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rebellion_backing_requests(
campaign_id,timeline_id,movement_id,world_day,rebel_kingdom_id,parent_kingdom_id,rebel_ruler_id,parent_ruler_id,
asked_kingdom_id,asked_ruler_id,eligibility_attitude,asked_to_rebel_attitude,asked_to_parent_attitude,
rebel_roll,parent_roll,rebel_score,parent_score,support_power,opposition_power,military_chance,military_roll,
support_coalition_json,opposition_coalition_json,status,reason,action_id,event_id,payload_json,created_ts)
VALUES($campaign,$timeline,$movement,$day,$rebelKingdom,$parentKingdom,$rebelRuler,$parentRuler,
$askedKingdom,$askedRuler,$eligibility,$askedRebel,$askedParent,$rebelRoll,$parentRoll,$rebelScore,$parentScore,
$supportPower,$oppositionPower,$chance,$militaryRoll,$supportCoalition,$oppositionCoalition,$status,$reason,$action,$event,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectRebellionMovements3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT stage,timeline_id,updated_day,payload_json FROM rebellion_movements WHERE movement_id=$id;", parameters, transaction);
        }

        internal static void UpsertRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rebellion_movements(movement_id,campaign_id,timeline_id,parent_kingdom_id,rebel_kingdom_id,leader_clan_id,leader_hero_id,ruler_hero_id,
objective,stage,pressure,relationship_pressure,trait_pressure,factual_pressure,viability,readiness,demand_json,correlation_id,created_day,updated_day,cooldown_until_day,is_player_kingdom,payload_json)
VALUES($id,$campaign,$timeline,$parent,$rebel,$clan,$hero,$ruler,$objective,$stage,$pressure,$relationship,$trait,$factual,$viability,$readiness,$demand,$correlation,$created,$updated,$cooldown,$player,$payload)
ON CONFLICT(movement_id) DO UPDATE SET timeline_id=$timeline,rebel_kingdom_id=$rebel,leader_clan_id=$clan,leader_hero_id=$hero,stage=$stage,pressure=$pressure,
relationship_pressure=$relationship,trait_pressure=$trait,factual_pressure=$factual,viability=$viability,readiness=$readiness,demand_json=$demand,updated_day=$updated,cooldown_until_day=$cooldown,payload_json=$payload;", parameters, transaction);
        }

        internal static void InsertRebellionTransitions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rebellion_transitions(transition_id,movement_id,world_day,from_stage,to_stage,kind,actor_id,payload_json,created_ts)
VALUES($transition,$movement,$day,$from,$to,'game_state_mirror',$actor,$payload,$ts);", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT DISTINCT e.event_type,e.world_day,e.payload_json
FROM world_history_entities n JOIN world_history_events e ON e.event_id=n.event_id
WHERE n.entity_id IN ($hero,$clan,$kingdom) AND e.world_day >= $from AND e.world_day <= $to;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT COUNT(*) count FROM events WHERE event_type='support_claimant' AND payload_json LIKE $clan;", parameters, transaction);
        }

        internal static void UpsertRebellionMovements2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT OR REPLACE INTO rebellion_movements(movement_id,campaign_id,timeline_id,parent_kingdom_id,rebel_kingdom_id,
leader_clan_id,leader_hero_id,ruler_hero_id,objective,stage,pressure,relationship_pressure,trait_pressure,factual_pressure,viability,readiness,
demand_json,correlation_id,created_day,updated_day,cooldown_until_day,is_player_kingdom,payload_json)
VALUES($id,$campaign,$timeline,$parent,$rebel,$clan,$hero,$ruler,$objective,$stage,$pressure,$relationship,$trait,$factual,$viability,$readiness,
$demand,$correlation,COALESCE((SELECT created_day FROM rebellion_movements WHERE movement_id=$id),$day),$day,0,$player,$payload);", parameters, transaction);
        }

        internal static void DeleteRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM rebellion_memberships WHERE movement_id=$id;", parameters, transaction);
        }

        internal static void InsertRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rebellion_memberships(movement_id,clan_id,leader_hero_id,side,support_score,switch_count,last_switch_day,reason_json)
VALUES($movement,$clan,$hero,$side,$score,$switches,$last,$reason);", parameters, transaction);
        }

        internal static void InsertRebellionTransitions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO rebellion_transitions(transition_id,movement_id,world_day,from_stage,to_stage,kind,actor_id,payload_json,created_ts)
VALUES($id,$movement,$day,$from,$to,'stage_changed',$actor,$payload,$ts);", parameters, transaction);
        }
    }
}
