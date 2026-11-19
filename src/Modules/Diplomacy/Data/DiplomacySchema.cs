using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Diplomacy module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DiplomacySchema
    {
        internal static void CreateClanAccordSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS clan_accord_snapshots(
timeline_id TEXT PRIMARY KEY, player_hero_id TEXT NOT NULL, snapshot_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS clan_accord_event_receipts(
timeline_id TEXT NOT NULL,event_id TEXT NOT NULL,payload_json TEXT NOT NULL,
effects_done INTEGER NOT NULL DEFAULT 0,memory_done INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(timeline_id,event_id));
CREATE TABLE IF NOT EXISTS clan_accord_relation_receipts(
timeline_id TEXT NOT NULL,receipt_id TEXT NOT NULL,observer_id TEXT NOT NULL,target_id TEXT NOT NULL,
delta INTEGER NOT NULL,PRIMARY KEY(timeline_id,receipt_id,observer_id,target_id));", parameters, transaction);
        }

        internal static void CreateRulerRelationshipRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_relationship_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
observer_id TEXT NOT NULL,target_id TEXT NOT NULL,observer_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
compatibility_chance INTEGER NOT NULL,compatibility_roll INTEGER NOT NULL,magnitude INTEGER NOT NULL,
delta INTEGER NOT NULL,before_affinity INTEGER NOT NULL,after_affinity INTEGER NOT NULL,
before_effective INTEGER NOT NULL,after_effective INTEGER NOT NULL,co_located INTEGER NOT NULL DEFAULT 0,
correlation_id TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,observer_id,target_id));", parameters, transaction);
        }

        internal static void CreateIndexRulerRelationshipRollsPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_ruler_relationship_rolls_pair
ON ruler_relationship_rolls(campaign_id,timeline_id,observer_id,target_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRulerRelationshipCrossings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_relationship_crossings (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,season_index INTEGER NOT NULL,
observer_id TEXT NOT NULL,target_id TEXT NOT NULL,observer_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
polarity TEXT NOT NULL,threshold INTEGER NOT NULL,crossing_day REAL NOT NULL,
before_effective INTEGER NOT NULL,after_effective INTEGER NOT NULL,bonus_chance INTEGER NOT NULL,
bonus_roll INTEGER NOT NULL,bonus_passed INTEGER NOT NULL,opportunity_id TEXT NOT NULL DEFAULT '',
correlation_id TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,season_index,observer_id,target_id,polarity,threshold));", parameters, transaction);
        }

        internal static void CreateRulerDiplomacyOpportunities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_diplomacy_opportunities (
opportunity_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
observer_id TEXT NOT NULL,target_id TEXT NOT NULL,observer_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
polarity TEXT NOT NULL,threshold INTEGER NOT NULL,season_index INTEGER NOT NULL,crossing_day REAL NOT NULL,
effective_attitude INTEGER NOT NULL,bonus_chance INTEGER NOT NULL,bonus_roll INTEGER NOT NULL,
status TEXT NOT NULL DEFAULT 'queued',expiry_day REAL NOT NULL,terminal_day REAL NOT NULL DEFAULT -1,
action_id TEXT NOT NULL DEFAULT '',outcome TEXT NOT NULL DEFAULT '',correlation_id TEXT NOT NULL,
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRulerOpportunityQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_ruler_opportunity_queue
ON ruler_diplomacy_opportunities(campaign_id,timeline_id,status,threshold DESC,crossing_day,opportunity_id);", parameters, transaction);
        }

        internal static void CreateRulerDiplomacyActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_diplomacy_activity (
activity_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
observer_id TEXT NOT NULL DEFAULT '',target_id TEXT NOT NULL DEFAULT '',event_type TEXT NOT NULL,
polarity TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT '',threshold INTEGER NOT NULL DEFAULT 0,
before_value INTEGER NOT NULL DEFAULT 0,after_value INTEGER NOT NULL DEFAULT 0,
roll INTEGER NOT NULL DEFAULT 0,chance INTEGER NOT NULL DEFAULT 0,action_id TEXT NOT NULL DEFAULT '',
correlation_id TEXT NOT NULL DEFAULT '',reason TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRulerActivityPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_ruler_activity_pair
ON ruler_diplomacy_activity(campaign_id,timeline_id,observer_id,target_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRulerDiplomaticIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_diplomatic_incidents (
incident_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
event_kind TEXT NOT NULL,source_id TEXT NOT NULL DEFAULT '',observer_id TEXT NOT NULL,target_id TEXT NOT NULL,
observer_kingdom_id TEXT NOT NULL DEFAULT '',target_kingdom_id TEXT NOT NULL DEFAULT '',
affinity_delta INTEGER NOT NULL,public_subject_id TEXT NOT NULL DEFAULT '',public_delta INTEGER NOT NULL DEFAULT 0,
before_effective INTEGER NOT NULL,after_effective INTEGER NOT NULL,breakthroughs_enabled INTEGER NOT NULL DEFAULT 1,
correlation_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRulerIncidentsPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_ruler_incidents_pair
ON ruler_diplomatic_incidents(campaign_id,timeline_id,observer_id,target_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRulerWarOrigins(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_war_origins (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,war_id TEXT NOT NULL,aggressor_kingdom_id TEXT NOT NULL,
defender_kingdom_id TEXT NOT NULL,aggressor_ruler_id TEXT NOT NULL,defender_ruler_id TEXT NOT NULL,
declaration_day REAL NOT NULL,origin_kind TEXT NOT NULL,cause TEXT NOT NULL DEFAULT '',source_action_id TEXT NOT NULL DEFAULT '',
parent_war_id TEXT NOT NULL DEFAULT '',is_active INTEGER NOT NULL,ended_day REAL NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(campaign_id,timeline_id,war_id));", parameters, transaction);
        }

        internal static void CreateRulerTreatyObligations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_treaty_obligations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,obligation_id TEXT NOT NULL,agreement_id TEXT NOT NULL,
triggering_war_id TEXT NOT NULL,resulting_war_id TEXT NOT NULL DEFAULT '',ally_kingdom_id TEXT NOT NULL,
ally_ruler_id TEXT NOT NULL,defended_kingdom_id TEXT NOT NULL,defended_ruler_id TEXT NOT NULL,
aggressor_kingdom_id TEXT NOT NULL,triggered_day REAL NOT NULL,status TEXT NOT NULL,reason TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',PRIMARY KEY(campaign_id,timeline_id,obligation_id));", parameters, transaction);
        }

        internal static void CreateRulerAgreementHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_agreement_history (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,agreement_id TEXT NOT NULL,kind TEXT NOT NULL,
actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,created_day REAL NOT NULL,expire_day REAL NOT NULL,
is_active INTEGER NOT NULL,ended_day REAL NOT NULL DEFAULT 0,end_reason TEXT NOT NULL DEFAULT '',
breaker_kingdom_id TEXT NOT NULL DEFAULT '',triggering_war_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(campaign_id,timeline_id,agreement_id));", parameters, transaction);
        }

        internal static void CreatePoliticalPressureDeltaReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_delta_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
incident_id TEXT NOT NULL,requested_delta INTEGER NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,incident_id));", parameters, transaction);
        }

        internal static void CreatePoliticalPressureDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_daily_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
chance INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,status TEXT NOT NULL,
origin_kingdom_id TEXT NOT NULL DEFAULT '',incident_id TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void CreatePoliticalPressureState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_state (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
actor_kingdom_name TEXT NOT NULL DEFAULT '',target_kingdom_name TEXT NOT NULL DEFAULT '',
actor_ruler_id TEXT NOT NULL DEFAULT '',target_ruler_id TEXT NOT NULL DEFAULT '',pressure_value INTEGER NOT NULL DEFAULT 0,
dominant_channel TEXT NOT NULL DEFAULT '',updated_day REAL NOT NULL DEFAULT 0,last_incident_id TEXT NOT NULL DEFAULT '',
last_action_day REAL NOT NULL DEFAULT -1,revision INTEGER NOT NULL DEFAULT 1,
PRIMARY KEY(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id));", parameters, transaction);
        }

        internal static void CreatePoliticalPressureIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_incidents (
incident_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
archetype_id TEXT NOT NULL,polarity TEXT NOT NULL,channel TEXT NOT NULL,severity TEXT NOT NULL,severity_rank INTEGER NOT NULL,
pressure_amount INTEGER NOT NULL,relationship_penalty INTEGER NOT NULL,
origin_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,origin_kingdom_name TEXT NOT NULL,target_kingdom_name TEXT NOT NULL,
origin_ruler_id TEXT NOT NULL,target_ruler_id TEXT NOT NULL,origin_ruler_name TEXT NOT NULL,target_ruler_name TEXT NOT NULL,
origin_war_count INTEGER NOT NULL,target_war_count INTEGER NOT NULL,hostile_chance INTEGER NOT NULL,polarity_roll INTEGER NOT NULL,
origin_clans_json TEXT NOT NULL,origin_lords_json TEXT NOT NULL,target_clans_json TEXT NOT NULL,target_lords_json TEXT NOT NULL,
trait_routes_json TEXT NOT NULL,trait_rolls_json TEXT NOT NULL,origin_stance TEXT NOT NULL,target_stance TEXT NOT NULL,
origin_pressure_before INTEGER NOT NULL,origin_pressure_after INTEGER NOT NULL,target_pressure_before INTEGER NOT NULL,target_pressure_after INTEGER NOT NULL,
headline TEXT NOT NULL,narrative TEXT NOT NULL,llm_selector_status TEXT NOT NULL,llm_narration_status TEXT NOT NULL,
notice_status TEXT NOT NULL DEFAULT 'ready',correlation_id TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'completed',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void CreateIndexPressureIncidentsPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_pressure_incidents_pair
ON political_pressure_incidents(campaign_id,timeline_id,origin_kingdom_id,target_kingdom_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreatePoliticalPressureNativeEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_native_effects (
effect_id TEXT PRIMARY KEY,incident_id TEXT NOT NULL,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
effect_type TEXT NOT NULL,target_id TEXT NOT NULL,amount INTEGER NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
before_value REAL NOT NULL DEFAULT -1,after_value REAL NOT NULL DEFAULT -1,receipt_day REAL NOT NULL DEFAULT -1,
error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexPressureEffectsPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_pressure_effects_pending
ON political_pressure_native_effects(campaign_id,timeline_id,status,world_day);", parameters, transaction);
        }

        internal static void CreatePoliticalPressureActionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_action_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,pressure_value INTEGER NOT NULL,polarity TEXT NOT NULL,
channel TEXT NOT NULL,chance INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,selected INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL,action_id TEXT NOT NULL DEFAULT '',outcome TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,actor_kingdom_id,target_kingdom_id));", parameters, transaction);
        }

        internal static void CreatePoliticalPressureActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_activity (
activity_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
incident_id TEXT NOT NULL DEFAULT '',actor_kingdom_id TEXT NOT NULL DEFAULT '',target_kingdom_id TEXT NOT NULL DEFAULT '',
actor_ruler_id TEXT NOT NULL DEFAULT '',target_ruler_id TEXT NOT NULL DEFAULT '',event_type TEXT NOT NULL,
polarity TEXT NOT NULL DEFAULT '',channel TEXT NOT NULL DEFAULT '',severity TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT '',
before_value INTEGER NOT NULL DEFAULT 0,after_value INTEGER NOT NULL DEFAULT 0,roll INTEGER NOT NULL DEFAULT 0,chance INTEGER NOT NULL DEFAULT 0,
action_id TEXT NOT NULL DEFAULT '',correlation_id TEXT NOT NULL DEFAULT '',reason TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexPressureActivityTimeline(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_pressure_activity_timeline
ON political_pressure_activity(campaign_id,timeline_id,world_day DESC,event_type);", parameters, transaction);
        }
    }
}
