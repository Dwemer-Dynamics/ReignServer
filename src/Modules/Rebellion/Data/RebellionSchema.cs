using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Rebellion module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class RebellionSchema
    {
        internal static void CreateRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_movements (
movement_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,parent_kingdom_id TEXT NOT NULL,
rebel_kingdom_id TEXT NOT NULL DEFAULT '',leader_clan_id TEXT NOT NULL,leader_hero_id TEXT NOT NULL,ruler_hero_id TEXT NOT NULL,
objective TEXT NOT NULL,stage TEXT NOT NULL,pressure REAL NOT NULL,relationship_pressure REAL NOT NULL,
trait_pressure REAL NOT NULL,factual_pressure REAL NOT NULL,viability REAL NOT NULL,readiness REAL NOT NULL,
demand_json TEXT NOT NULL DEFAULT '{}',correlation_id TEXT NOT NULL,created_day REAL NOT NULL,updated_day REAL NOT NULL,
cooldown_until_day REAL NOT NULL DEFAULT 0,is_player_kingdom INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_memberships (
movement_id TEXT NOT NULL,clan_id TEXT NOT NULL,leader_hero_id TEXT NOT NULL,side TEXT NOT NULL,support_score REAL NOT NULL,
switch_count INTEGER NOT NULL DEFAULT 0,last_switch_day REAL NOT NULL DEFAULT 0,reason_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(movement_id,clan_id));", parameters, transaction);
        }

        internal static void CreateRebellionTransitions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_transitions (
transition_id TEXT PRIMARY KEY,movement_id TEXT NOT NULL,world_day REAL NOT NULL,from_stage TEXT NOT NULL,to_stage TEXT NOT NULL,
kind TEXT NOT NULL,actor_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateNegotiatedActionDrafts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS negotiated_action_drafts (
negotiation_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,broker_hero_id TEXT NOT NULL,
first_kingdom_id TEXT NOT NULL,second_kingdom_id TEXT NOT NULL,first_ruler_id TEXT NOT NULL,second_ruler_id TEXT NOT NULL,
command TEXT NOT NULL,political_result TEXT NOT NULL DEFAULT '',terms_json TEXT NOT NULL,terms_hash TEXT NOT NULL,
status TEXT NOT NULL,created_day REAL NOT NULL,updated_day REAL NOT NULL,expires_day REAL NOT NULL,
queued_action_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateNegotiatedActionApprovals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS negotiated_action_approvals (
negotiation_id TEXT NOT NULL,ruler_hero_id TEXT NOT NULL,kingdom_id TEXT NOT NULL,terms_hash TEXT NOT NULL,
status TEXT NOT NULL,approved_day REAL NOT NULL,reason TEXT NOT NULL DEFAULT '',PRIMARY KEY(negotiation_id,ruler_hero_id));", parameters, transaction);
        }

        internal static void CreateRebellionBackingRequests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_backing_requests (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,world_day REAL NOT NULL,
rebel_kingdom_id TEXT NOT NULL,parent_kingdom_id TEXT NOT NULL,rebel_ruler_id TEXT NOT NULL,parent_ruler_id TEXT NOT NULL,
asked_kingdom_id TEXT NOT NULL DEFAULT '',asked_ruler_id TEXT NOT NULL DEFAULT '',eligibility_attitude INTEGER NOT NULL DEFAULT 0,
asked_to_rebel_attitude INTEGER NOT NULL DEFAULT 0,asked_to_parent_attitude INTEGER NOT NULL DEFAULT 0,
rebel_roll INTEGER NOT NULL DEFAULT 0,parent_roll INTEGER NOT NULL DEFAULT 0,rebel_score INTEGER NOT NULL DEFAULT 0,parent_score INTEGER NOT NULL DEFAULT 0,
support_power REAL NOT NULL DEFAULT 0,opposition_power REAL NOT NULL DEFAULT 0,military_chance INTEGER NOT NULL DEFAULT 0,military_roll INTEGER NOT NULL DEFAULT 0,
support_coalition_json TEXT NOT NULL DEFAULT '[]',opposition_coalition_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL,
reason TEXT NOT NULL DEFAULT '',action_id TEXT NOT NULL DEFAULT '',event_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,movement_id));", parameters, transaction);
        }

        internal static void CreateIndexRebellionBackingStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rebellion_backing_status ON rebellion_backing_requests(campaign_id,timeline_id,status,world_day);", parameters, transaction);
        }

        internal static void CreateRebellionRecognitionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_recognition_attempts (
attempt_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,
attempt_index INTEGER NOT NULL,world_day REAL NOT NULL,rebel_kingdom_id TEXT NOT NULL,parent_kingdom_id TEXT NOT NULL,
rebel_ruler_id TEXT NOT NULL,parent_ruler_id TEXT NOT NULL,status TEXT NOT NULL,eligible INTEGER NOT NULL,
chance_percent INTEGER NOT NULL DEFAULT 0,roll INTEGER NOT NULL DEFAULT 0,relationship_attitude INTEGER NOT NULL DEFAULT 0,
rebel_strength REAL NOT NULL DEFAULT 0,parent_strength REAL NOT NULL DEFAULT 0,rebel_fiefs INTEGER NOT NULL DEFAULT 0,
parent_fiefs INTEGER NOT NULL DEFAULT 0,parent_other_wars INTEGER NOT NULL DEFAULT 0,parent_judgment INTEGER NOT NULL DEFAULT 50,
parent_pragmatism INTEGER NOT NULL DEFAULT 50,parent_mercy INTEGER NOT NULL DEFAULT 50,parent_pride INTEGER NOT NULL DEFAULT 50,
reason TEXT NOT NULL DEFAULT '',negotiation_id TEXT NOT NULL DEFAULT '',action_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,movement_id,attempt_index));", parameters, transaction);
        }

        internal static void CreateIndexRebellionRecognitionMovement(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_rebellion_recognition_movement
ON rebellion_recognition_attempts(campaign_id,timeline_id,movement_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexRebellionCampaignStage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rebellion_campaign_stage ON rebellion_movements(campaign_id,timeline_id,stage,updated_day);", parameters, transaction);
        }

        internal static void CreateIndexRebellionParent(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rebellion_parent ON rebellion_movements(campaign_id,parent_kingdom_id,stage);", parameters, transaction);
        }

        internal static void CreateIndexNegotiationCampaignStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_negotiation_campaign_status ON negotiated_action_drafts(campaign_id,status,expires_day);", parameters, transaction);
        }
    }
}
