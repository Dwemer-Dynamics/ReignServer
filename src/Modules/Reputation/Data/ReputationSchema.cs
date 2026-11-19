using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Reputation module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class ReputationSchema
    {
        internal static void CreateReputationActivations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_activations (
activation_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,source_occurrence_id TEXT NOT NULL DEFAULT '',
source_event_id TEXT NOT NULL DEFAULT '',activation_day REAL NOT NULL DEFAULT 0,
description TEXT NOT NULL DEFAULT '',incident_description TEXT NOT NULL DEFAULT '',
evidence_json TEXT NOT NULL DEFAULT '{}',reason_text TEXT NOT NULL DEFAULT '',
            reason_status TEXT NOT NULL DEFAULT 'recorded',provider TEXT NOT NULL DEFAULT '',
model TEXT NOT NULL DEFAULT '',generation_attempts INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexReputationActivationsSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_reputation_activations_subject
ON reputation_activations(campaign_id,timeline_id,subject_id,tag_id,activation_day DESC);", parameters, transaction);
        }

        internal static void CreateReputationReasonJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_reason_jobs (
job_id TEXT PRIMARY KEY,activation_id TEXT NOT NULL UNIQUE,campaign_id TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'retired',attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexReputationReasonJobsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_reputation_reason_jobs_status ON reputation_reason_jobs(status,updated_ts);", parameters, transaction);
        }

        internal static void EnsureColumnCharacterReputationsReasonActivationId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_reputations", "reason_activation_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnCharacterReputationsReasonText(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_reputations", "reason_text", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnCharacterReputationsReasonStatus(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_reputations", "reason_status", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateCourtDynamicReputationMetrics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_dynamic_reputation_metrics (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,domain TEXT NOT NULL,
positive_count INTEGER NOT NULL DEFAULT 0,negative_count INTEGER NOT NULL DEFAULT 0,
dynamic_value INTEGER NOT NULL DEFAULT 0,evidence_json TEXT NOT NULL DEFAULT '{}',
calculated_day REAL NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,domain));", parameters, transaction);
        }

        internal static void CreateCourtConversationSignalReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_conversation_signal_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,session_id TEXT NOT NULL,signal_key TEXT NOT NULL,
signal_type TEXT NOT NULL,speaker_id TEXT NOT NULL,target_id TEXT NOT NULL,
result_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,session_id,signal_key));", parameters, transaction);
        }

        internal static void CreateCourtSocialSignalEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_social_signal_evidence (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,exchange_id TEXT NOT NULL,signal_id TEXT NOT NULL,
session_id TEXT NOT NULL DEFAULT '',signal_type TEXT NOT NULL,speaker_id TEXT NOT NULL DEFAULT '',
target_id TEXT NOT NULL DEFAULT '',supporting_quote TEXT NOT NULL DEFAULT '',accepted INTEGER NOT NULL DEFAULT 0,
rejection_reason TEXT NOT NULL DEFAULT '',evidence_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,exchange_id,signal_id));", parameters, transaction);
        }

        internal static void CreateCourtPlayerFavoringExchanges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_player_favoring_exchanges (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
exchange_count INTEGER NOT NULL DEFAULT 0,last_exchange_id TEXT NOT NULL DEFAULT '',last_world_day REAL NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id));", parameters, transaction);
        }

        internal static void CreateCourtPlayerFavoringExchangeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_player_favoring_exchange_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
exchange_id TEXT NOT NULL,world_day REAL NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id,exchange_id));", parameters, transaction);
        }

        internal static void CreateCourtIntimacyExposureReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_intimacy_exposure_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,signal_id TEXT NOT NULL,player_id TEXT NOT NULL,
partner_id TEXT NOT NULL,eligible INTEGER NOT NULL DEFAULT 0,exposed INTEGER NOT NULL DEFAULT 0,
probability REAL NOT NULL DEFAULT 0,roll REAL NOT NULL DEFAULT 0,result_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,signal_id));", parameters, transaction);
        }

        internal static void CreateCourtFlirtTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_flirt_targets (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,target_id TEXT NOT NULL,
first_session_id TEXT NOT NULL DEFAULT '',first_world_day REAL NOT NULL DEFAULT 0,
last_session_id TEXT NOT NULL DEFAULT '',last_world_day REAL NOT NULL DEFAULT 0,
incident_count INTEGER NOT NULL DEFAULT 1,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,target_id));", parameters, transaction);
        }

        internal static void CreateIndexCourtFlirtSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_flirt_subject ON court_flirt_targets(campaign_id,timeline_id,subject_id,last_world_day DESC);", parameters, transaction);
        }

        internal static void CreateRumorOccurrencesV2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE rumor_occurrences_v2 (
occurrence_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
archetype_id TEXT NOT NULL,thread_key TEXT NOT NULL,source_event_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL,expires_day REAL NOT NULL,status TEXT NOT NULL DEFAULT 'active',
exposure_chance REAL NOT NULL DEFAULT 0,exposure_roll REAL NOT NULL DEFAULT 1,
catalog_revision INTEGER NOT NULL,participants_json TEXT NOT NULL DEFAULT '[]',
provenance_summary TEXT NOT NULL DEFAULT '',snapshot_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void DropRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE rumor_occurrences;", parameters, transaction);
        }

        internal static void AlterRumorOccurrencesV2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ALTER TABLE rumor_occurrences_v2 RENAME TO rumor_occurrences;", parameters, transaction);
        }

        internal static void CreateSocialOutcomeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_outcome_receipts (
event_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
subject_id TEXT NOT NULL DEFAULT '',archetype_id TEXT NOT NULL DEFAULT '',
counter_applied INTEGER NOT NULL DEFAULT 0,completed INTEGER NOT NULL DEFAULT 0,
result_json TEXT NOT NULL DEFAULT '{}',attempt_count INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void EnsureColumnSocialOutcomeReceiptsAttemptCount(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "social_outcome_receipts", "attempt_count", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnSocialOutcomeReceiptsLastError(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "social_outcome_receipts", "last_error", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateReputationCounterevidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_counterevidence (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
consecutive_count INTEGER NOT NULL DEFAULT 0,last_event_id TEXT NOT NULL DEFAULT '',
last_outcome_day REAL NOT NULL DEFAULT 0,last_chance REAL NOT NULL DEFAULT 0,last_roll REAL NOT NULL DEFAULT 1,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,tag_id));", parameters, transaction);
        }

        internal static void CreateReputationLifecycleEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS reputation_lifecycle_events (
lifecycle_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,event_type TEXT NOT NULL,
source_event_id TEXT NOT NULL DEFAULT '',world_day REAL NOT NULL DEFAULT 0,
details_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexReputationLifecycleSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_reputation_lifecycle_subject ON reputation_lifecycle_events(campaign_id,timeline_id,subject_id,tag_id,world_day);", parameters, transaction);
        }

        internal static void CreateSocialSubjectWorkQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_subject_work_queue(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
status TEXT NOT NULL DEFAULT 'pending',first_sequence INTEGER NOT NULL DEFAULT 0,
pending_count INTEGER NOT NULL DEFAULT 0,claim_owner TEXT NOT NULL DEFAULT '',
claim_expires_ts INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
last_error TEXT NOT NULL DEFAULT '',
PRIMARY KEY(campaign_id,timeline_id,subject_id));", parameters, transaction);
        }

        internal static void EnsureColumnSocialSubjectWorkQueueNeedsFinalize(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "social_subject_work_queue", "needs_finalize", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnSocialSubjectWorkQueueLastWorldDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "social_subject_work_queue", "last_world_day", "REAL NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnSocialSubjectWorkQueueLastEventId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "social_subject_work_queue", "last_event_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateIndexSocialSubjectWorkClaim(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_social_subject_work_claim
ON social_subject_work_queue(campaign_id,timeline_id,status,first_sequence,subject_id);", parameters, transaction);
        }

        internal static void CreateCharacterPublicStanding(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_public_standing (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
standing_value INTEGER NOT NULL DEFAULT 0,sources_json TEXT NOT NULL DEFAULT '[]',
revision INTEGER NOT NULL DEFAULT 1,last_changed_day REAL NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id));", parameters, transaction);
        }

        internal static void CreateIndexCharacterPublicStandingSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_character_public_standing_subject
ON character_public_standing(campaign_id,timeline_id,subject_id);", parameters, transaction);
        }

        internal static void EnsureColumnCharacterPublicStandingCalculationStatus(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_public_standing", "calculation_status", "TEXT NOT NULL DEFAULT 'ready'");
        }

        internal static void EnsureColumnCharacterPublicStandingLastError(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_public_standing", "last_error", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnCharacterPublicStandingCalculatedRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_public_standing", "calculated_revision", "INTEGER NOT NULL DEFAULT 1");
        }

        internal static void CreateCourtRulerFavorContact(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_ruler_favor_contact (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
last_contact_day REAL,grace_day REAL NOT NULL,expired_day REAL,contact_id TEXT NOT NULL DEFAULT '',
PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id));", parameters, transaction);
        }

        internal static void CreateCourtRulerFavorContactReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_ruler_favor_contact_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,ruler_id TEXT NOT NULL,favorite_id TEXT NOT NULL,
contact_id TEXT NOT NULL,world_day REAL NOT NULL,PRIMARY KEY(campaign_id,timeline_id,ruler_id,favorite_id,contact_id));", parameters, transaction);
        }

        internal static void CreateSocialBalanceRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_runs(
run_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
save_prefix TEXT NOT NULL,main_hero_id TEXT NOT NULL,status TEXT NOT NULL,
manifest_json TEXT NOT NULL,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexSocialBalanceRunsCampaign(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_social_balance_runs_campaign ON social_balance_runs(campaign_id,timeline_id,updated_ts);", parameters, transaction);
        }

        internal static void CreateSocialBalanceCaseResults(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_case_results(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,case_id TEXT NOT NULL,period_key TEXT NOT NULL,
run_id TEXT NOT NULL,scenario_version INTEGER NOT NULL,dependency_fingerprint TEXT NOT NULL,
status TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,evidence_json TEXT NOT NULL DEFAULT '{}',
first_started_ts INTEGER NOT NULL,completed_ts INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,case_id,period_key));", parameters, transaction);
        }

        internal static void CreateIndexSocialBalanceCaseRun(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_social_balance_case_run ON social_balance_case_results(run_id,status,updated_ts);", parameters, transaction);
        }

        internal static void CreateSocialBalanceSnapshots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_snapshots(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,run_id TEXT NOT NULL,period_key TEXT NOT NULL,
world_day REAL NOT NULL,snapshot_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
 PRIMARY KEY(campaign_id,timeline_id,period_key));", parameters, transaction);
        }

        internal static void CreateSocialBalanceLongitudinalCheckpoints(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_balance_longitudinal_checkpoints(
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,run_id TEXT NOT NULL,
world_day REAL NOT NULL,snapshot_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,run_id,world_day));", parameters, transaction);
        }

        internal static void CreateSocialCatalogRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS social_catalog_revisions (
revision INTEGER PRIMARY KEY,catalog_json TEXT NOT NULL,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRumorOccurrences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_occurrences (
occurrence_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
archetype_id TEXT NOT NULL,thread_key TEXT NOT NULL,source_event_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL,expires_day REAL NOT NULL,status TEXT NOT NULL DEFAULT 'active',
exposure_chance REAL NOT NULL DEFAULT 0,exposure_roll REAL NOT NULL DEFAULT 1,
catalog_revision INTEGER NOT NULL,participants_json TEXT NOT NULL DEFAULT '[]',
provenance_summary TEXT NOT NULL DEFAULT '',snapshot_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRumorOccurrenceActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rumor_occurrence_active ON rumor_occurrences(campaign_id,timeline_id,status,expires_day);", parameters, transaction);
        }

        internal static void CreateRumorSubjectTags(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_subject_tags (
occurrence_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
subject_role TEXT NOT NULL DEFAULT '',description TEXT NOT NULL DEFAULT '',
rumor_value INTEGER NOT NULL DEFAULT 0,reputation_value INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL DEFAULT 'active',co_participants_json TEXT NOT NULL DEFAULT '[]',
snapshot_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(occurrence_id,subject_id,tag_id));", parameters, transaction);
        }

        internal static void CreateIndexRumorSubjectActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rumor_subject_active ON rumor_subject_tags(subject_id,status,tag_id);", parameters, transaction);
        }

        internal static void CreateRumorExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_exposure_streaks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,archetype_id TEXT NOT NULL,
thread_key TEXT NOT NULL,streak_count INTEGER NOT NULL DEFAULT 0,last_exposure_day REAL NOT NULL DEFAULT -1,
last_occurrence_id TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,archetype_id,thread_key));", parameters, transaction);
        }

        internal static void CreateSharedTagExposureStreaks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS shared_tag_exposure_streaks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
streak_count INTEGER NOT NULL DEFAULT 0,last_exposure_day REAL NOT NULL DEFAULT -1,
last_occurrence_id TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,tag_id));", parameters, transaction);
        }

        internal static void CreateRumorPromotionResults(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rumor_promotion_results (
occurrence_id TEXT PRIMARY KEY,streak_count INTEGER NOT NULL,promotion_chance REAL NOT NULL DEFAULT 0,
promotion_roll REAL NOT NULL DEFAULT 1,promoted INTEGER NOT NULL DEFAULT 0,
promoted_tags_json TEXT NOT NULL DEFAULT '[]',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateCharacterReputations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_reputations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,tag_id TEXT NOT NULL,
source_occurrence_id TEXT NOT NULL DEFAULT '',archetype_id TEXT NOT NULL DEFAULT '',
subject_role TEXT NOT NULL DEFAULT '',description TEXT NOT NULL DEFAULT '',
reputation_value INTEGER NOT NULL DEFAULT 0,acquired_day REAL NOT NULL,
catalog_revision INTEGER NOT NULL,snapshot_json TEXT NOT NULL DEFAULT '{}',
status TEXT NOT NULL DEFAULT 'active',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id,tag_id));", parameters, transaction);
        }

        internal static void CreateIndexCharacterReputationsSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_character_reputations_subject ON character_reputations(campaign_id,timeline_id,subject_id,status);", parameters, transaction);
        }

        internal static void CreateDirectionalSocialProjections(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS directional_social_projections (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,observer_id TEXT NOT NULL,subject_id TEXT NOT NULL,
rumor_total REAL NOT NULL DEFAULT 0,reputation_total REAL NOT NULL DEFAULT 0,
charm INTEGER NOT NULL DEFAULT 0,kingdom_scale REAL NOT NULL DEFAULT 0.5,
social_modifier INTEGER NOT NULL DEFAULT 0,calculated_day REAL NOT NULL DEFAULT 0,
details_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,observer_id,subject_id));", parameters, transaction);
        }

        internal static void EnsureColumnRelationshipPairChemistrySocialModifierAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "social_modifier_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistrySocialModifierBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "social_modifier_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryEffectiveAffinityAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "effective_affinity_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryEffectiveAffinityBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "effective_affinity_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void DropDynamic(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE IF EXISTS " + table + ";", parameters, transaction);
        }

        internal static void CreateWhoremongerActivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS whoremonger_activity (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
is_player INTEGER NOT NULL,visit_count INTEGER NOT NULL DEFAULT 0,
last_activity_day REAL NOT NULL DEFAULT -1,last_event_day REAL NOT NULL DEFAULT -1,
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,subject_id));", parameters, transaction);
        }

        internal static void CreateWhoremongerEventReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS whoremonger_event_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,event_id TEXT NOT NULL,
subject_id TEXT NOT NULL,town_id TEXT NOT NULL,world_day REAL NOT NULL,
input_json TEXT NOT NULL,result_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,event_id));", parameters, transaction);
        }

        internal static void CreateIndexWhoremongerInactivity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_whoremonger_inactivity
ON whoremonger_activity(campaign_id,timeline_id,last_activity_day);", parameters, transaction);
        }
    }
}
