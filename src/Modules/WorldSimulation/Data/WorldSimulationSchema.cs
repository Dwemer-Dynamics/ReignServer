using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The WorldSimulation module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class WorldSimulationSchema
    {
        internal static void CreateWorldHistoryTimelines(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_timelines (
timeline_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,parent_timeline_id TEXT NOT NULL DEFAULT '',fork_event_id TEXT NOT NULL DEFAULT '',
history_complete_from_day REAL NOT NULL DEFAULT 0,head_sequence INTEGER NOT NULL DEFAULT 0,head_event_id TEXT NOT NULL DEFAULT '',
is_active INTEGER NOT NULL DEFAULT 1,created_utc TEXT NOT NULL,updated_utc TEXT NOT NULL); ", parameters, transaction);
        }

        internal static void CreateWorldHistoryEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_events (
event_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,sequence INTEGER NOT NULL,world_day REAL NOT NULL,
event_type TEXT NOT NULL,category TEXT NOT NULL DEFAULT '',phase TEXT NOT NULL DEFAULT 'completed',correlation_id TEXT NOT NULL DEFAULT '',
location_id TEXT NOT NULL DEFAULT '',location_name TEXT NOT NULL DEFAULT '',dissemination_class TEXT NOT NULL DEFAULT 'ordinary',
summary TEXT NOT NULL DEFAULT '',semantic_text TEXT NOT NULL DEFAULT '',source TEXT NOT NULL DEFAULT 'native',is_complete INTEGER NOT NULL DEFAULT 1,
checksum TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_utc TEXT NOT NULL); ", parameters, transaction);
        }

        internal static void EnsureColumnWorldHistoryEventsSubjectId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_history_events", "subject_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateWorldHistoryEntities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_entities (
event_id TEXT NOT NULL,ordinal INTEGER NOT NULL,entity_id TEXT NOT NULL DEFAULT '',entity_type TEXT NOT NULL DEFAULT '',
name_snapshot TEXT NOT NULL DEFAULT '',role TEXT NOT NULL DEFAULT '',side TEXT NOT NULL DEFAULT '',party_id TEXT NOT NULL DEFAULT '',
clan_id TEXT NOT NULL DEFAULT '',kingdom_id TEXT NOT NULL DEFAULT '',quantity REAL NOT NULL DEFAULT 0,before_json TEXT NOT NULL DEFAULT '{}',
after_json TEXT NOT NULL DEFAULT '{}',payload_json TEXT NOT NULL DEFAULT '{}',PRIMARY KEY(event_id,ordinal)); ", parameters, transaction);
        }

        internal static void CreateWorldHistoryKnowledgeRules(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_knowledge_rules (
rule_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,audience_type TEXT NOT NULL,audience_id TEXT NOT NULL DEFAULT '',
available_day REAL NOT NULL,acquisition_mode TEXT NOT NULL DEFAULT '',confidence REAL NOT NULL DEFAULT 1); ", parameters, transaction);
        }

        internal static void CreateWorldHistoryClaimChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_claim_checks (
check_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,claimant_id TEXT NOT NULL DEFAULT '',
speaker_id TEXT NOT NULL DEFAULT '',raw_claim TEXT NOT NULL,purpose TEXT NOT NULL,claim_family TEXT NOT NULL,
objective_verdict TEXT NOT NULL,speaker_verdict TEXT NOT NULL,explanation TEXT NOT NULL DEFAULT '',constraints_json TEXT NOT NULL DEFAULT '{}',
evidence_event_ids_json TEXT NOT NULL DEFAULT '[]',speaker_event_ids_json TEXT NOT NULL DEFAULT '[]',minime_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL); ", parameters, transaction);
        }

        internal static void CreateWorldHistoryIngestState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_ingest_state (
client_id TEXT NOT NULL,timeline_id TEXT NOT NULL,last_sequence INTEGER NOT NULL DEFAULT 0,last_event_id TEXT NOT NULL DEFAULT '',
updated_utc TEXT NOT NULL,PRIMARY KEY(client_id,timeline_id)); ", parameters, transaction);
        }

        internal static void CreateIndexWhEventTimelineTypeDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_timeline_type_day ON world_history_events(timeline_id,event_type,world_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexWhEventLocationDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_location_day ON world_history_events(timeline_id,location_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexWhEventCorrelation(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_correlation ON world_history_events(timeline_id,correlation_id,sequence);", parameters, transaction);
        }

        internal static void CreateIndexWhEventSequence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_wh_event_sequence ON world_history_events(timeline_id,sequence,event_id);", parameters, transaction);
        }

        internal static void CreateIndexWhEntityLookup(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_entity_lookup ON world_history_entities(entity_id,role,event_id);", parameters, transaction);
        }

        internal static void CreateIndexWhEntityEvent(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_entity_event ON world_history_entities(entity_id,event_id);", parameters, transaction);
        }

        internal static void CreateIndexWhEventEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_event_evidence ON world_history_events(timeline_id,event_type,world_day DESC,event_id);", parameters, transaction);
        }

        internal static void DropIdxWhEventTimelineTypeDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DROP INDEX IF EXISTS idx_wh_event_timeline_type_day;
DROP INDEX IF EXISTS idx_wh_social_subject_sequence;", parameters, transaction);
        }

        internal static void CreateIndexWhSocialSubjectSequenceV2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_wh_social_subject_sequence_v2
ON world_history_events(timeline_id,subject_id,sequence,event_id)
WHERE event_type='social_outcome';", parameters, transaction);
        }

        internal static void CreateIndexWhSocialSubjectSequence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_social_subject_sequence ON world_history_events(timeline_id,event_type,subject_id,sequence,event_id);", parameters, transaction);
        }

        internal static void CreateIndexWhEntityKingdom(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_entity_kingdom ON world_history_entities(kingdom_id,event_id);", parameters, transaction);
        }

        internal static void CreateIndexWhRuleEvent(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_rule_event ON world_history_knowledge_rules(event_id,available_day);", parameters, transaction);
        }

        internal static void CreateIndexWhRuleAudience(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_rule_audience ON world_history_knowledge_rules(audience_type,audience_id,available_day);", parameters, transaction);
        }

        internal static void CreateWorldHistoryFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE VIRTUAL TABLE IF NOT EXISTS world_history_fts USING fts5(event_id UNINDEXED,text,entities,roles,location,event_type);", parameters, transaction);
        }

        internal static void CreateWorldHistoryNoiseEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TEMP TABLE IF NOT EXISTS world_history_noise_events(event_id TEXT PRIMARY KEY);", parameters, transaction);
        }

        internal static void EnsureColumnWorldHistoryEventsRetentionClass(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_history_events", "retention_class", "TEXT NOT NULL DEFAULT 'significant'");
        }

        internal static void EnsureColumnWorldHistoryEventsPlayerInvolved(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_history_events", "player_involved", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void CreateIndexWhRetentionDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_retention_day ON world_history_events(timeline_id,retention_class,world_day,event_id);", parameters, transaction);
        }

        internal static void CreateWorldHistoryDailyAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_daily_aggregates (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
event_type TEXT NOT NULL,category TEXT NOT NULL,retention_class TEXT NOT NULL,
kingdom_id TEXT NOT NULL DEFAULT '',player_involved INTEGER NOT NULL DEFAULT 0,
event_count INTEGER NOT NULL DEFAULT 0,quantity_total REAL NOT NULL DEFAULT 0,
first_world_day REAL NOT NULL,last_world_day REAL NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,event_type,category,retention_class,kingdom_id,player_involved));", parameters, transaction);
        }

        internal static void CreateWorldHistorySeasonalAggregates(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_seasonal_aggregates (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,season_start_day REAL NOT NULL,
event_type TEXT NOT NULL,category TEXT NOT NULL,retention_class TEXT NOT NULL,
kingdom_id TEXT NOT NULL DEFAULT '',player_involved INTEGER NOT NULL DEFAULT 0,
event_count INTEGER NOT NULL DEFAULT 0,quantity_total REAL NOT NULL DEFAULT 0,
first_world_day REAL NOT NULL,last_world_day REAL NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,season_start_day,event_type,category,retention_class,kingdom_id,player_involved));", parameters, transaction);
        }

        internal static void CreateIndexWhDailyAggregateDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_daily_aggregate_day ON world_history_daily_aggregates(timeline_id,day_key);", parameters, transaction);
        }

        internal static void CreateIndexWhSeasonalAggregateDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_seasonal_aggregate_day ON world_history_seasonal_aggregates(timeline_id,season_start_day);", parameters, transaction);
        }

        internal static void CreateWorldHistoryRemovedNpcLootIds(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TEMP TABLE IF NOT EXISTS world_history_removed_npc_loot_ids(event_id TEXT PRIMARY KEY);", parameters, transaction);
        }

        internal static void CreateWorldHistoryExpiredIds(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TEMP TABLE IF NOT EXISTS world_history_expired_ids(event_id TEXT PRIMARY KEY);", parameters, transaction);
        }

        internal static void CreateWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_native_heartbeats (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
eligible_npc_count INTEGER NOT NULL DEFAULT 0,kingdom_count INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void CreateIndexWorldTestHeartbeatDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_world_test_heartbeat_day ON world_test_native_heartbeats(timeline_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateWorldTestDailyRollups(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_daily_rollups (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
overall_status TEXT NOT NULL DEFAULT 'insufficient_data',rollup_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void CreateIndexWorldTestRollupDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_world_test_rollup_day ON world_test_daily_rollups(timeline_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateWorldTestRebellionRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,kingdom_id TEXT NOT NULL,clan_id TEXT NOT NULL,
week_index INTEGER NOT NULL,rolled_day REAL NOT NULL,was_eligible INTEGER NOT NULL,triggered INTEGER NOT NULL,
roll INTEGER NOT NULL,ruler_relation INTEGER NOT NULL,is_player_clan INTEGER NOT NULL DEFAULT 0,
exclusion_reason TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,kingdom_id,clan_id,week_index));", parameters, transaction);
        }

        internal static void EnsureColumnWorldTestRebellionRollsExclusionReason(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls", "exclusion_reason", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnWorldTestRebellionRollsPersonalAffinityToRuler(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls", "personal_affinity_to_ruler", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnWorldTestRebellionRollsRulerPublicStanding(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls", "ruler_public_standing", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnWorldTestRebellionRollsRulerStandingRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "world_test_rebellion_rolls", "ruler_standing_revision", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void CreateWorldTestRebellionMovements(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_movements (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,updated_day REAL NOT NULL,
stage TEXT NOT NULL DEFAULT '',resolution_applied INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,movement_id));", parameters, transaction);
        }

        internal static void CreateWorldTestRebellionMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rebellion_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,clan_id TEXT NOT NULL,
side TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,movement_id,clan_id));", parameters, transaction);
        }

        internal static void CreateWorldTestManifests(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_manifests (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,manifest_json TEXT NOT NULL DEFAULT '{}',
first_observed_day REAL NOT NULL DEFAULT 0,latest_observed_day REAL NOT NULL DEFAULT 0,
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id));", parameters, transaction);
        }

        internal static void CreateWorldTestChunkCounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_chunk_counters (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
subsystem TEXT NOT NULL,chunk_key TEXT NOT NULL,counters_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,subsystem,chunk_key));", parameters, transaction);
        }

        internal static void CreateWorldTestCurrentMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_current_metrics (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,metric_scope TEXT NOT NULL,
metric_key TEXT NOT NULL,metric_value INTEGER NOT NULL DEFAULT 0,
updated_day INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,metric_scope,metric_key));", parameters, transaction);
        }

        internal static void CreateWorldTestRelationshipMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_relationship_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,category_type TEXT NOT NULL,
category_key TEXT NOT NULL,hero_id TEXT NOT NULL,edge_count INTEGER NOT NULL DEFAULT 0,
directional_edge_count INTEGER NOT NULL DEFAULT 0,updated_day INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,category_type,category_key,hero_id));", parameters, transaction);
        }

        internal static void CreateWorldTestRelationshipPairMemberships(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_relationship_pair_memberships (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,category_type TEXT NOT NULL,
category_key TEXT NOT NULL,pair_key TEXT NOT NULL,edge_count INTEGER NOT NULL DEFAULT 0,
directional_edge_count INTEGER NOT NULL DEFAULT 0,updated_day INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,category_type,category_key,pair_key));", parameters, transaction);
        }

        internal static void CreateWorldTestRelationshipPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_relationship_pairs (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,pair_key TEXT NOT NULL,
hero_a_id TEXT NOT NULL DEFAULT '',hero_b_id TEXT NOT NULL DEFAULT '',
affinity_a_to_b INTEGER NOT NULL DEFAULT 0,affinity_b_to_a INTEGER NOT NULL DEFAULT 0,
tag_a_to_b TEXT NOT NULL DEFAULT '',tag_b_to_a TEXT NOT NULL DEFAULT '',
shared_tag TEXT NOT NULL DEFAULT '',mbti_a TEXT NOT NULL DEFAULT '',
mbti_b TEXT NOT NULL DEFAULT '',notable_a INTEGER NOT NULL DEFAULT 0,
notable_b INTEGER NOT NULL DEFAULT 0,last_delta_a_to_b INTEGER NOT NULL DEFAULT 0,
last_delta_b_to_a INTEGER NOT NULL DEFAULT 0,last_context_kind TEXT NOT NULL DEFAULT '',
first_day INTEGER NOT NULL DEFAULT 0,last_day INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,pair_key));", parameters, transaction);
        }

        internal static void CreateWorldTestDiagnosticEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_diagnostic_evidence (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
subsystem TEXT NOT NULL,evidence_key TEXT NOT NULL,severity TEXT NOT NULL DEFAULT 'info',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subsystem,evidence_key));", parameters, transaction);
        }

        internal static void CreateIndexWorldTestEvidenceDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_world_test_evidence_day
ON world_test_diagnostic_evidence(campaign_id,timeline_id,subsystem,day_key);", parameters, transaction);
        }

        internal static void CreateWorldTestRollupQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_rollup_queue (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
status TEXT NOT NULL DEFAULT 'pending',revision INTEGER NOT NULL DEFAULT 1,
reason TEXT NOT NULL DEFAULT '',attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void CreateIndexWorldTestRollupQueueStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_world_test_rollup_queue_status
ON world_test_rollup_queue(status,updated_ts);", parameters, transaction);
        }

        internal static void CreateWorldTestCheckpointReports(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_test_checkpoint_reports (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,checkpoint_day REAL NOT NULL,
report_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,checkpoint_day));", parameters, transaction);
        }
    }
}
