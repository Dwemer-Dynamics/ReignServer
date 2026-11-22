using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Relationships module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class RelationshipsSchema
    {
        internal static void CreateArrangedMarriagePeriods(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS arranged_marriage_periods (
timeline_id TEXT NOT NULL,period_index INTEGER NOT NULL,world_day REAL NOT NULL,
status TEXT NOT NULL,created_ts INTEGER NOT NULL,PRIMARY KEY(timeline_id,period_index));", parameters, transaction);
        }

        internal static void CreateArrangedMarriageEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS arranged_marriage_effects (
effect_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,proposal_id TEXT NOT NULL,delta INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateArrangedMarriageRefusals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS arranged_marriage_refusals (
refusal_key TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,proposal_id TEXT NOT NULL);", parameters, transaction);
        }

        internal static void DropIdxWorldTestHeartbeatDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP INDEX idx_world_test_heartbeat_day;", parameters, transaction);
        }

        internal static void DropWorldTestNativeHeartbeats(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE world_test_native_heartbeats;", parameters, transaction);
        }

        internal static void CreateIndexRelationshipNativeTargetsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_native_targets_status ON relationship_native_targets(status,world_day,pair_key);", parameters, transaction);
        }

        internal static void DropIdxRelationshipNativeTargetsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP INDEX idx_relationship_native_targets_status;", parameters, transaction);
        }

        internal static void AlterRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ALTER TABLE relationship_native_targets DROP COLUMN requires_observation;", parameters, transaction);
        }

        internal static void CreateAutonomousCorrespondenceChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS autonomous_correspondence_checks (
timeline_id TEXT NOT NULL, slot_day INTEGER NOT NULL, sender_id TEXT NOT NULL,
chance INTEGER NOT NULL, roll INTEGER NOT NULL, status TEXT NOT NULL,
letter_id TEXT NOT NULL DEFAULT '', updated_ts INTEGER NOT NULL,
PRIMARY KEY(timeline_id,slot_day,sender_id));", parameters, transaction);
        }

        internal static void CreateAutonomousCorrespondenceCooldowns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS autonomous_correspondence_cooldowns (
sender_id TEXT PRIMARY KEY, last_sent_day REAL NOT NULL,
next_eligible_day REAL NOT NULL, cooldown_days INTEGER NOT NULL,
letter_id TEXT NOT NULL, updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateClanConflictDaySweeps(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS clan_conflict_day_sweeps (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
eligible_kingdoms INTEGER NOT NULL,processed_kingdoms INTEGER NOT NULL,status TEXT NOT NULL,
eligible_kingdom_ids_json TEXT NOT NULL DEFAULT '[]',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void EnsureColumnClanConflictDaySweepsEligibleKingdomIdsJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "clan_conflict_day_sweeps", "eligible_kingdom_ids_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void CreateClanConflictDailyRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS clan_conflict_daily_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
kingdom_id TEXT NOT NULL,kingdom_name TEXT NOT NULL DEFAULT '',ruler_id TEXT NOT NULL DEFAULT '',
chance INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,status TEXT NOT NULL,
incident_id TEXT NOT NULL DEFAULT '',eligible_clan_count INTEGER NOT NULL DEFAULT 0,
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,kingdom_id));", parameters, transaction);
        }

        internal static void CreateClanConflictIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS clan_conflict_incidents (
incident_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,
world_day REAL NOT NULL,kingdom_id TEXT NOT NULL,kingdom_name TEXT NOT NULL,ruler_id TEXT NOT NULL,ruler_name TEXT NOT NULL,
clan_a_id TEXT NOT NULL,clan_a_name TEXT NOT NULL,clan_b_id TEXT NOT NULL,clan_b_name TEXT NOT NULL,
leader_a_id TEXT NOT NULL,leader_b_id TEXT NOT NULL,involved_a_json TEXT NOT NULL,involved_b_json TEXT NOT NULL,
involved_a_names_json TEXT NOT NULL DEFAULT '[]',involved_b_names_json TEXT NOT NULL DEFAULT '[]',
archetype_id TEXT NOT NULL,category TEXT NOT NULL,headline TEXT NOT NULL,narrative TEXT NOT NULL,
trait_votes_json TEXT NOT NULL,average_relation_a REAL NOT NULL,average_relation_b REAL NOT NULL,
winning_side TEXT NOT NULL,charm_skill INTEGER NOT NULL,charm_chance_bp INTEGER NOT NULL,charm_roll INTEGER NOT NULL,
mediation_succeeded INTEGER NOT NULL,relationship_effect_count INTEGER NOT NULL DEFAULT 0,
notice_status TEXT NOT NULL DEFAULT 'pending',status TEXT NOT NULL DEFAULT 'effects_pending',
last_error TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,day_key,kingdom_id));", parameters, transaction);
        }

        internal static void EnsureColumnClanConflictIncidentsInvolvedANamesJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "clan_conflict_incidents", "involved_a_names_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void EnsureColumnClanConflictIncidentsInvolvedBNamesJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "clan_conflict_incidents", "involved_b_names_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void CreateIndexClanConflictIncidentsTimeline(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_clan_conflict_incidents_timeline
ON clan_conflict_incidents(campaign_id,timeline_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateClanConflictRelationshipEffects(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS clan_conflict_relationship_effects (
effect_id TEXT PRIMARY KEY,incident_id TEXT NOT NULL,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
world_day REAL NOT NULL,effect_kind TEXT NOT NULL,observer_id TEXT NOT NULL,target_id TEXT NOT NULL,amount INTEGER NOT NULL,
before_affinity INTEGER NOT NULL DEFAULT 0,after_affinity INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'pending',
last_error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexClanConflictEffectsPending(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_clan_conflict_effects_pending
ON clan_conflict_relationship_effects(campaign_id,timeline_id,status,world_day);", parameters, transaction);
        }

        internal static void CreateConversationRelationshipReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_relationship_receipts (
receipt_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL DEFAULT '',exchange_id TEXT NOT NULL,
source_turn_id TEXT NOT NULL DEFAULT '',observer_id TEXT NOT NULL,target_id TEXT NOT NULL,mode TEXT NOT NULL DEFAULT '',
act_kind TEXT NOT NULL DEFAULT 'routine',severity_tier TEXT NOT NULL DEFAULT 'routine',valence TEXT NOT NULL DEFAULT 'positive',
base_delta INTEGER NOT NULL DEFAULT 0,modifier_delta INTEGER NOT NULL DEFAULT 0,final_delta INTEGER NOT NULL DEFAULT 0,
prior_affinity INTEGER NOT NULL DEFAULT 0,resulting_affinity INTEGER NOT NULL DEFAULT 0,prior_native_relation INTEGER NOT NULL DEFAULT 0,
resulting_native_relation INTEGER NOT NULL DEFAULT 0,native_pair_delta INTEGER NOT NULL DEFAULT 0,
lie_check_id TEXT NOT NULL DEFAULT '',benefit_event_id TEXT NOT NULL DEFAULT '',
gift_recipient_id TEXT NOT NULL DEFAULT '',
confidence REAL NOT NULL DEFAULT 0,evidence_json TEXT NOT NULL DEFAULT '[]',modifiers_json TEXT NOT NULL DEFAULT '[]',
            summary TEXT NOT NULL DEFAULT '',current_conduct_quote TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'applied',native_application_status TEXT NOT NULL DEFAULT 'pending',
native_applied_ts INTEGER NOT NULL DEFAULT 0,native_receipt_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(exchange_id,observer_id,target_id));", parameters, transaction);
        }

        internal static void EnsureColumnConversationRelationshipReceiptsNativeApplicationStatus(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_application_status", "TEXT NOT NULL DEFAULT 'pending'");
        }

        internal static void EnsureColumnConversationRelationshipReceiptsNativeAppliedTs(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_applied_ts", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnConversationRelationshipReceiptsNativeReceiptJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_receipt_json", "TEXT NOT NULL DEFAULT '{}'");
        }

        internal static void EnsureColumnConversationRelationshipReceiptsNativePairDelta(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "native_pair_delta", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnConversationRelationshipReceiptsGiftRecipientId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "gift_recipient_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnConversationRelationshipReceiptsCurrentConductQuote(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_receipts", "current_conduct_quote", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateIndexConversationRelationshipRecent(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_relationship_recent ON conversation_relationship_receipts(created_ts DESC);", parameters, transaction);
        }

        internal static void CreateIndexConversationRelationshipPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_relationship_pair ON conversation_relationship_receipts(observer_id,target_id,created_ts DESC);", parameters, transaction);
        }

        internal static void CreateConversationRelationshipBenefits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_relationship_benefits (
benefit_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL DEFAULT '',giver_id TEXT NOT NULL,recipient_id TEXT NOT NULL,
benefit_kind TEXT NOT NULL DEFAULT 'gift',description TEXT NOT NULL DEFAULT '',source_event_id TEXT NOT NULL DEFAULT '',source_turn_id TEXT NOT NULL DEFAULT '',
original_delta INTEGER NOT NULL DEFAULT 0,retained_appreciation REAL NOT NULL DEFAULT 1,continued_utility REAL NOT NULL DEFAULT 1,
status TEXT NOT NULL DEFAULT 'retained',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
source_evidence_json TEXT NOT NULL DEFAULT '[]',payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void EnsureColumnConversationRelationshipBenefitsSourceEvidenceJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_relationship_benefits", "source_evidence_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void CreateIndexConversationBenefitRecipient(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_benefit_recipient ON conversation_relationship_benefits(recipient_id,giver_id,updated_ts DESC);", parameters, transaction);
        }

        internal static void CreateCorrespondenceDeliveryJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS correspondence_delivery_jobs (
letter_id TEXT PRIMARY KEY,player_id TEXT NOT NULL,timeline_id TEXT NOT NULL,restore_generation TEXT NOT NULL,
source_hash TEXT NOT NULL,status TEXT NOT NULL,attempt_count INTEGER NOT NULL DEFAULT 0,
lease_token TEXT NOT NULL DEFAULT '',lease_until BIGINT NOT NULL DEFAULT 0,next_attempt_ts BIGINT NOT NULL DEFAULT 0,
reply_json TEXT NOT NULL DEFAULT '',result_json TEXT NOT NULL DEFAULT '{}',acknowledged INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',created_ts BIGINT NOT NULL,updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_correspondence_delivery_work ON correspondence_delivery_jobs(status,next_attempt_ts,lease_until);", parameters, transaction);
        }

        internal static void ApplyCorrespondenceArrivalHistoryDdl(ReignDbConnection connection)
        {
            // Prepare the old job and disclosure tables before the courier can acquire letter row locks.
            CreateCorrespondenceDeliveryJobs(connection);
            CreateConceptionContexts(connection);
            ReignDatabase.EnsureDatabaseColumn(connection, "letters", "arrival_retry_after_ts", "BIGINT NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "correspondence_delivery_jobs", "arrival_history_json", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.EnsureDatabaseColumn(connection, "correspondence_delivery_jobs", "arrival_history_terminal_error", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.EnsureDatabaseColumn(connection, "correspondence_delivery_jobs", "arrival_history_retry_after_ts", "BIGINT NOT NULL DEFAULT 0");
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_correspondence_arrival_history
ON correspondence_delivery_jobs(player_id,timeline_id,restore_generation,arrival_history_retry_after_ts,created_ts,letter_id)
WHERE arrival_history_json<>'' AND arrival_history_terminal_error='';
CREATE INDEX IF NOT EXISTS idx_letters_arrival_retry
ON letters(arrival_retry_after_ts,delivery_day,letter_id) WHERE status='in_transit';");
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static void DowngradeCorrespondenceArrivalHistoryForTests(ReignDbConnection connection)
        {
            using var transaction = connection.BeginTransaction();
            ReignDatabase.ExecuteSql(connection, @"ALTER TABLE correspondence_delivery_jobs DROP COLUMN arrival_history_json,
DROP COLUMN arrival_history_terminal_error, DROP COLUMN arrival_history_retry_after_ts;
ALTER TABLE letters DROP COLUMN arrival_retry_after_ts;", transaction: transaction);
            ReignDatabase.ExecuteSql(connection, "UPDATE schema_meta SET value='2' WHERE key=$key;",
                TestDict("key", PersistenceService.CampaignMigrationVersionKey), transaction);
            transaction.Commit();
        }
#endif

        internal const string CreateRelationshipPairChemistryStageSql = @"
CREATE TEMP TABLE IF NOT EXISTS relationship_pair_chemistry_stage(
pair text NOT NULL,a text NOT NULL,b text NOT NULL,
typea text NOT NULL,typeb text NOT NULL,axesa text NOT NULL,
axesb text NOT NULL,baseab bigint NOT NULL,baseba bigint NOT NULL,
chanceab bigint NOT NULL,chanceba bigint NOT NULL,
affinityab bigint NOT NULL,affinityba bigint NOT NULL,
signalab bigint NOT NULL,signalba bigint NOT NULL,
tagab text NOT NULL,tagba text NOT NULL,shared text NOT NULL,
firstday bigint NOT NULL,lastday bigint NOT NULL,
consecutive bigint NOT NULL,weighted double precision NOT NULL,
kind text NOT NULL,contextid text NOT NULL,rollab bigint NOT NULL,
rollba bigint NOT NULL,deltaab bigint NOT NULL,deltaba bigint NOT NULL,
projected bigint NOT NULL,nativeday double precision NOT NULL,
pending bigint NOT NULL,actionid text NOT NULL,shard bigint NOT NULL,
presenceday bigint NOT NULL,batchsize bigint NOT NULL,
presencemask bigint NOT NULL,decayperiod bigint NOT NULL,
version bigint NOT NULL,updatedts bigint NOT NULL)
ON COMMIT DELETE ROWS;
TRUNCATE relationship_pair_chemistry_stage;";

        internal static void AlterRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
ALTER TABLE relationship_pair_chemistry SET (
    fillfactor=80,
    autovacuum_vacuum_scale_factor=0.02,
    autovacuum_analyze_scale_factor=0.02);
ALTER TABLE relationship_pair_provenance SET (
    fillfactor=90,
    autovacuum_vacuum_scale_factor=0.05);
ALTER TABLE relationship_native_targets SET (
    fillfactor=70,
    autovacuum_vacuum_scale_factor=0.02);
ALTER TABLE relationship_daily_inputs SET (
    fillfactor=80,
    autovacuum_vacuum_scale_factor=0.05);", parameters, transaction);
        }

        internal static void CreateRelationshipPairChemistry(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_pair_chemistry (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
mbti_a TEXT NOT NULL DEFAULT 'XXXX',mbti_b TEXT NOT NULL DEFAULT 'XXXX',
axes_a_json TEXT NOT NULL DEFAULT '{}',axes_b_json TEXT NOT NULL DEFAULT '{}',
base_chance_a_to_b INTEGER NOT NULL DEFAULT 50,base_chance_b_to_a INTEGER NOT NULL DEFAULT 50,
chance_a_to_b INTEGER NOT NULL DEFAULT 40,chance_b_to_a INTEGER NOT NULL DEFAULT 40,
affinity_a_to_b INTEGER NOT NULL DEFAULT 0,affinity_b_to_a INTEGER NOT NULL DEFAULT 0,
native_incident_offset INTEGER NOT NULL DEFAULT 0,
signal_a_to_b INTEGER NOT NULL DEFAULT 0,signal_b_to_a INTEGER NOT NULL DEFAULT 0,
tag_a_to_b TEXT NOT NULL DEFAULT 'neutral',tag_b_to_a TEXT NOT NULL DEFAULT 'neutral',
shared_tag TEXT NOT NULL DEFAULT '',first_day INTEGER NOT NULL,last_day INTEGER NOT NULL,
consecutive_days INTEGER NOT NULL DEFAULT 0,weighted_exposure REAL NOT NULL DEFAULT 0,
last_context_kind TEXT NOT NULL DEFAULT '',last_context_id TEXT NOT NULL DEFAULT '',
last_roll_a_to_b INTEGER NOT NULL DEFAULT 0,last_roll_b_to_a INTEGER NOT NULL DEFAULT 0,
last_delta_a_to_b INTEGER NOT NULL DEFAULT 0,last_delta_b_to_a INTEGER NOT NULL DEFAULT 0,
projected_native_relation INTEGER NOT NULL DEFAULT 0,last_native_sync_day REAL NOT NULL DEFAULT -1000,
native_action_pending INTEGER NOT NULL DEFAULT 0,native_action_id TEXT NOT NULL DEFAULT '',
last_decay_period INTEGER NOT NULL DEFAULT -1,
compatibility_version INTEGER NOT NULL DEFAULT 3,
state_revision INTEGER NOT NULL DEFAULT 1,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexPairChemistryDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_day ON relationship_pair_chemistry(last_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexPairChemistryHeroA(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_hero_a ON relationship_pair_chemistry(hero_a_id,hero_b_id);", parameters, transaction);
        }

        internal static void CreateIndexPairChemistryHeroB(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_hero_b ON relationship_pair_chemistry(hero_b_id,hero_a_id);", parameters, transaction);
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

        internal static void EnsureColumnRelationshipPairChemistryProcessingShard(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "processing_shard", "INTEGER NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnRelationshipPairChemistryLastPresenceDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_presence_day", "INTEGER NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnRelationshipPairChemistryLastBatchSize(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_batch_size", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryLastPresenceMask(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_presence_mask", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedVersion(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_version", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedBaseline(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_baseline", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedChanceAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_chance_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedChanceBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_chance_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedSignRollAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_sign_roll_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedSignRollBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_sign_roll_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedMagnitudeRollAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_magnitude_roll_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedMagnitudeRollBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_magnitude_roll_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedDeltaAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_delta_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedDeltaBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_delta_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedAffinityAToB(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_affinity_a_to_b", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedAffinityBToA(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_affinity_b_to_a", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedEligible(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_eligible", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryOpeningSeedProvenance(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "opening_seed_provenance", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateIndexPairChemistryShard(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_pair_chemistry_shard ON relationship_pair_chemistry(processing_shard,last_day);", parameters, transaction);
        }

        internal static void CreateRelationshipNativeSyncBatches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_native_sync_batches (
plan_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
target_count INTEGER NOT NULL DEFAULT 0,issued_ts INTEGER NOT NULL DEFAULT 0,
client_applied INTEGER NOT NULL DEFAULT 0,client_already_aligned INTEGER NOT NULL DEFAULT 0,
client_obsolete INTEGER NOT NULL DEFAULT 0,client_failed INTEGER NOT NULL DEFAULT 0,
client_remaining INTEGER NOT NULL DEFAULT 0,client_duration_ms INTEGER NOT NULL DEFAULT 0,
last_report_day REAL NOT NULL DEFAULT -1,last_report_ts INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '');", parameters, transaction);
        }

        internal static void CreateIndexNativeSyncBatchDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_native_sync_batch_day ON relationship_native_sync_batches(timeline_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipDailyInputs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_daily_inputs (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
status TEXT NOT NULL DEFAULT 'pending',presence_groups_json TEXT NOT NULL DEFAULT '[]',
hero_changes_json TEXT NOT NULL DEFAULT '[]',group_count INTEGER NOT NULL DEFAULT 0,
received_ts INTEGER NOT NULL,processed_ts INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(campaign_id,timeline_id,day_key));", parameters, transaction);
        }

        internal static void CreateIndexRelationshipDailyInputsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_daily_inputs_status ON relationship_daily_inputs(timeline_id,status,day_key);", parameters, transaction);
        }

        internal static void EnsureColumnRelationshipDailyInputsPairCursor(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "pair_cursor", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnRelationshipDailyInputsCandidatePairs(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "candidate_pairs", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipDailyInputsProcessedPairs(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "processed_pairs", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipDailyInputsStartedTs(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "started_ts", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipDailyInputsLastError(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "last_error", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnRelationshipDailyInputsCadenceShard(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "cadence_shard", "INTEGER NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnRelationshipDailyInputsWindowStartDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "window_start_day", "INTEGER NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnRelationshipDailyInputsWindowDayCount(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "window_day_count", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipDailyInputsPairDayEvaluations(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_daily_inputs", "pair_day_evaluations", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void CreateRelationshipObservedHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_observed_heroes (
hero_id TEXT PRIMARY KEY,profile_json TEXT NOT NULL,profile_hash TEXT NOT NULL DEFAULT '',
first_observed_day INTEGER NOT NULL,last_observed_day INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRelationshipHeroObservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_hero_observations (
timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,observed_day INTEGER NOT NULL,
profile_json TEXT NOT NULL,profile_hash TEXT NOT NULL,
PRIMARY KEY(timeline_id,hero_id,observed_day));", parameters, transaction);
        }

        internal static void CreateRelationshipPersonalities(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_personalities (
hero_id TEXT PRIMARY KEY,mbti_type TEXT NOT NULL,title TEXT NOT NULL,description TEXT NOT NULL,
source TEXT NOT NULL,assignment_day REAL NOT NULL,template_version INTEGER NOT NULL,
traits_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRelationshipOpeningSeedRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_opening_seed_runs (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,seed_version INTEGER NOT NULL,
generation_id TEXT NOT NULL DEFAULT '',world_day REAL NOT NULL,status TEXT NOT NULL,
candidate_pairs INTEGER NOT NULL DEFAULT 0,completed_pairs INTEGER NOT NULL DEFAULT 0,
skipped_pairs INTEGER NOT NULL DEFAULT 0,failed_pairs INTEGER NOT NULL DEFAULT 0,
co_location_pairs INTEGER NOT NULL DEFAULT 0,spouse_pairs INTEGER NOT NULL DEFAULT 0,
family_pairs INTEGER NOT NULL DEFAULT 0,kingdom_leadership_pairs INTEGER NOT NULL DEFAULT 0,
ruler_network_pairs INTEGER NOT NULL DEFAULT 0,positive_directions INTEGER NOT NULL DEFAULT 0,
negative_directions INTEGER NOT NULL DEFAULT 0,missing_mbti INTEGER NOT NULL DEFAULT 0,
average_delta REAL NOT NULL DEFAULT 0,median_delta REAL NOT NULL DEFAULT 0,
minimum_delta INTEGER NOT NULL DEFAULT 0,maximum_delta INTEGER NOT NULL DEFAULT 0,
starting_bands_json TEXT NOT NULL DEFAULT '{}',resulting_bands_json TEXT NOT NULL DEFAULT '{}',
duration_ms INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,seed_version));", parameters, transaction);
        }

        internal static void EnsureColumnRelationshipOpeningSeedRunsParallelWorkers(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_opening_seed_runs", "parallel_workers", "INTEGER NOT NULL DEFAULT 1");
        }

        internal static void EnsureColumnRelationshipOpeningSeedRunsParallelComputeMs(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_opening_seed_runs", "parallel_compute_ms", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipOpeningSeedRunsPairsPerSecond(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_opening_seed_runs", "pairs_per_second", "REAL NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryLastDecayPeriod(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "last_decay_period", "INTEGER NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnRelationshipPairChemistryNativeIncidentOffset(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "native_incident_offset", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairChemistryStateRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_chemistry", "state_revision", "INTEGER NOT NULL DEFAULT 1");
        }

        internal static void EnsureColumnRelationshipNativeTargetsRequiresObservation(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_native_targets", "requires_observation", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static string DropDynamicSql(object index)
        {
            return "DROP INDEX IF EXISTS " + index + ";";
        }

        internal static void CreateNotableMbtiProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS notable_mbti_profiles (
hero_id TEXT PRIMARY KEY,mbti_type TEXT NOT NULL,title TEXT NOT NULL,description TEXT NOT NULL,
foundation_json TEXT NOT NULL,percentages_json TEXT NOT NULL,native_traits_json TEXT NOT NULL,
source TEXT NOT NULL DEFAULT 'notable_mbti_template',template_version INTEGER NOT NULL,
first_observed_day REAL NOT NULL,native_sync_status TEXT NOT NULL DEFAULT 'pending',
native_action_id TEXT NOT NULL DEFAULT '',native_action_created_day REAL NOT NULL DEFAULT -1,
last_native_observed_json TEXT NOT NULL DEFAULT '{}',last_native_observed_day REAL NOT NULL DEFAULT -1,
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexNotableMbtiType(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_notable_mbti_type ON notable_mbti_profiles(mbti_type);", parameters, transaction);
        }

        internal static void CreateIndexNotableMbtiSync(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_notable_mbti_sync ON notable_mbti_profiles(native_sync_status);", parameters, transaction);
        }

        internal static void CreatePlayerAttentionEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS player_attention_events (
sequence_id BIGSERIAL PRIMARY KEY,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,
observer_id TEXT NOT NULL,target_id TEXT NOT NULL,calendar_day INTEGER NOT NULL,
target_name TEXT NOT NULL,location_id TEXT NOT NULL,exchange_id TEXT NOT NULL,source_id TEXT NOT NULL,event_text TEXT NOT NULL,created_ts BIGINT NOT NULL DEFAULT 0,
UNIQUE(timeline_id,player_id,observer_id,target_id,calendar_day));", parameters, transaction);
        }

        internal static void CreatePlayerApproachState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS player_approach_state (
timeline_id TEXT NOT NULL,state_key TEXT NOT NULL,value_json TEXT NOT NULL,
PRIMARY KEY(timeline_id,state_key));", parameters, transaction);
        }

        internal static void CreateConceptionContexts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conception_contexts (
timeline_id TEXT NOT NULL,pregnancy_id TEXT NOT NULL,facts_json TEXT NOT NULL,world_day DOUBLE PRECISION NOT NULL,
PRIMARY KEY(timeline_id,pregnancy_id));
CREATE TABLE IF NOT EXISTS conception_knowledge (
timeline_id TEXT NOT NULL,pregnancy_id TEXT NOT NULL,observer_id TEXT NOT NULL,
knowledge_json TEXT NOT NULL,source_id TEXT NOT NULL,source_hash TEXT NOT NULL,world_day DOUBLE PRECISION NOT NULL,
PRIMARY KEY(timeline_id,pregnancy_id,observer_id));", parameters, transaction);
        }

        internal static void CreateRelationshipDirectorRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_director_runs (
run_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,world_day REAL NOT NULL,status TEXT NOT NULL,
candidate_count INTEGER NOT NULL DEFAULT 0,event_count INTEGER NOT NULL DEFAULT 0,minime_json TEXT NOT NULL DEFAULT '{}',
input_json TEXT NOT NULL DEFAULT '{}',output_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexDirectorRunsDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_director_runs_day ON relationship_director_runs(world_day DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipDirectorActions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_director_actions (
director_action_id TEXT PRIMARY KEY,action_type TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',world_day REAL NOT NULL,
actor_id TEXT NOT NULL DEFAULT '',target_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,claimed_ts INTEGER NOT NULL DEFAULT 0,resolved_ts INTEGER NOT NULL DEFAULT 0,result_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateIndexDirectorActionsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_director_actions_status ON relationship_director_actions(status,world_day);", parameters, transaction);
        }

        internal static void CreateIndexDirectorActionsPairStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_director_actions_pair_status ON relationship_director_actions(actor_id,target_id,status);", parameters, transaction);
        }

        internal static void CreateConceptionAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conception_attempts (
attempt_id TEXT PRIMARY KEY,source TEXT NOT NULL,event_id TEXT NOT NULL DEFAULT '',mother_id TEXT NOT NULL,father_id TEXT NOT NULL,
mother_age REAL NOT NULL,chance REAL NOT NULL,roll REAL NOT NULL,success INTEGER NOT NULL,world_day REAL NOT NULL,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void EnsureColumnConceptionAttemptsStatus(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conception_attempts", "status", "TEXT NOT NULL DEFAULT 'resolved'");
        }

        internal static void CreateConceptions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conceptions (
conception_id TEXT PRIMARY KEY,attempt_id TEXT NOT NULL DEFAULT '',mother_id TEXT NOT NULL,biological_father_id TEXT NOT NULL,
legal_father_id TEXT NOT NULL DEFAULT '',conception_day REAL NOT NULL,due_day REAL NOT NULL,status TEXT NOT NULL DEFAULT 'pending_game',
secrecy REAL NOT NULL DEFAULT 0,child_id TEXT NOT NULL DEFAULT '',revealed INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateParentage(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS parentage (
child_id TEXT PRIMARY KEY,mother_id TEXT NOT NULL,biological_father_id TEXT NOT NULL,legal_father_id TEXT NOT NULL DEFAULT '',
is_illegitimate INTEGER NOT NULL DEFAULT 0,revealed INTEGER NOT NULL DEFAULT 0,bastard_surname TEXT NOT NULL DEFAULT '',
conception_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateDirectorOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS director_outbox (
outbox_id TEXT PRIMARY KEY,kind TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',world_day REAL NOT NULL,
subject_ids_json TEXT NOT NULL DEFAULT '[]',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS marriage_leader_rolls (
roll_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL DEFAULT 'main',period_index INTEGER NOT NULL,leader_id TEXT NOT NULL,clan_id TEXT NOT NULL,world_day REAL NOT NULL,
chance REAL NOT NULL,roll REAL NOT NULL,urgency REAL NOT NULL,status TEXT NOT NULL,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(timeline_id,period_index,leader_id));", parameters, transaction);
        }

        internal static void CreateMarriageEvaluations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS marriage_evaluations (
evaluation_id TEXT PRIMARY KEY,route TEXT NOT NULL,status TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
clan_a_id TEXT NOT NULL DEFAULT '',clan_b_id TEXT NOT NULL DEFAULT '',leader_a_id TEXT NOT NULL DEFAULT '',leader_b_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL,score REAL NOT NULL DEFAULT 0,leader_a_approved INTEGER NOT NULL DEFAULT 0,leader_b_approved INTEGER NOT NULL DEFAULT 0,
resentment_a REAL NOT NULL DEFAULT 0,resentment_b REAL NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexMarriageEvaluationsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_marriage_evaluations_status ON marriage_evaluations(status,world_day DESC);", parameters, transaction);
        }

        internal static void EnsureColumnMarriageEvaluationsTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "marriage_evaluations", "timeline_id", "TEXT NOT NULL DEFAULT 'main'");
        }

        internal static void CreateRelationshipNativeTargets(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_native_targets (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
target_relation INTEGER NOT NULL,observed_relation INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL DEFAULT 'pending',world_day REAL NOT NULL,last_sync_day REAL NOT NULL DEFAULT -1000,
attempt_count INTEGER NOT NULL DEFAULT 0,claimed_ts INTEGER NOT NULL DEFAULT 0,
last_error TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void EnsureColumnRelationshipNativeTargetsRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_native_targets", "revision", "INTEGER NOT NULL DEFAULT 1");
        }

        internal static void AlterMarriageLeaderRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "ALTER TABLE marriage_leader_rolls RENAME TO marriage_leader_rolls_legacy;", parameters, transaction);
        }

        internal static void CreateMarriageLeaderRolls2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE marriage_leader_rolls (
roll_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL DEFAULT 'main',period_index INTEGER NOT NULL,leader_id TEXT NOT NULL,clan_id TEXT NOT NULL,world_day REAL NOT NULL,
chance REAL NOT NULL,roll REAL NOT NULL,urgency REAL NOT NULL,status TEXT NOT NULL,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(timeline_id,period_index,leader_id));", parameters, transaction);
        }

        internal static void DropMarriageLeaderRollsLegacy(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TABLE marriage_leader_rolls_legacy;", parameters, transaction);
        }

        internal static void CreateRelationshipFlingProfiles(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_profiles (
hero_id TEXT PRIMARY KEY,judgment INTEGER NOT NULL,source TEXT NOT NULL DEFAULT '',
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRelationshipFlingRolls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day INTEGER NOT NULL,
hero_id TEXT NOT NULL,location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
judgment INTEGER NOT NULL,chance_percent INTEGER NOT NULL,roll INTEGER NOT NULL,
passed INTEGER NOT NULL,status TEXT NOT NULL,encounter_id TEXT NOT NULL DEFAULT '',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,world_day,hero_id));", parameters, transaction);
        }

        internal static void CreateIndexRelationshipFlingRollsHero(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_rolls_hero
ON relationship_fling_rolls(timeline_id,hero_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipFlingSparks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_sparks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day INTEGER NOT NULL,
pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
chance_percent INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,
encounter_id TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,world_day,pair_key));", parameters, transaction);
        }

        internal static void CreateIndexRelationshipFlingSparksDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_sparks_day
ON relationship_fling_sparks(timeline_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipFlingEncounters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_encounters (
encounter_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
world_day INTEGER NOT NULL,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
same_sex INTEGER NOT NULL DEFAULT 0,short_affair INTEGER NOT NULL DEFAULT 0,
fixed_discovery_chance REAL NOT NULL DEFAULT 0,fixed_discovery_roll REAL NOT NULL DEFAULT -1,
fixed_discovered INTEGER NOT NULL DEFAULT 0,pregnancy_discovery_chance REAL NOT NULL DEFAULT 0,
pregnancy_discovery_roll REAL NOT NULL DEFAULT -1,pregnancy_discovered INTEGER NOT NULL DEFAULT 0,
discovery_applied INTEGER NOT NULL DEFAULT 0,rumor_id TEXT NOT NULL DEFAULT '',
conception_attempt_id TEXT NOT NULL DEFAULT '',conception_id TEXT NOT NULL DEFAULT '',
conception_chance REAL NOT NULL DEFAULT 0,conception_roll REAL NOT NULL DEFAULT -1,
conceived INTEGER NOT NULL DEFAULT 0,conception_action_id TEXT NOT NULL DEFAULT '',
status TEXT NOT NULL DEFAULT 'processing',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRelationshipFlingEncountersDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_day
ON relationship_fling_encounters(timeline_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexRelationshipFlingEncountersA(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_a
ON relationship_fling_encounters(timeline_id,hero_a_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexRelationshipFlingEncountersB(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_b
ON relationship_fling_encounters(timeline_id,hero_b_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipPairLifecycle(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_pair_lifecycle (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
lover_active INTEGER NOT NULL DEFAULT 0,lover_started_day REAL NOT NULL DEFAULT 0,
lover_exception_attempted INTEGER NOT NULL DEFAULT 0,lover_exception_passed INTEGER NOT NULL DEFAULT 0,
lover_exception_roll INTEGER NOT NULL DEFAULT 0,lover_rumor_id TEXT NOT NULL DEFAULT '',
affair_active INTEGER NOT NULL DEFAULT 0,affair_started_day REAL NOT NULL DEFAULT 0,
affair_rumor_id TEXT NOT NULL DEFAULT '',
romance_stage TEXT NOT NULL DEFAULT '',romance_stage_started_day REAL NOT NULL DEFAULT 0,
romance_last_colocated_day REAL NOT NULL DEFAULT 0,romance_positive_bonus INTEGER NOT NULL DEFAULT 0,
flirt_episode INTEGER NOT NULL DEFAULT 0,flirt_attempted INTEGER NOT NULL DEFAULT 0,
flirt_passed INTEGER NOT NULL DEFAULT 0,flirt_trait_a INTEGER NOT NULL DEFAULT 0,
flirt_trait_b INTEGER NOT NULL DEFAULT 0,flirt_roll_a INTEGER NOT NULL DEFAULT 0,
flirt_roll_b INTEGER NOT NULL DEFAULT 0,affair_episode INTEGER NOT NULL DEFAULT 0,
affair_attempted INTEGER NOT NULL DEFAULT 0,affair_passed INTEGER NOT NULL DEFAULT 0,
judgment_trait_a INTEGER NOT NULL DEFAULT 0,judgment_trait_b INTEGER NOT NULL DEFAULT 0,
judgment_roll_a INTEGER NOT NULL DEFAULT 0,judgment_roll_b INTEGER NOT NULL DEFAULT 0,
estranged_a_to_b INTEGER NOT NULL DEFAULT 0,estranged_b_to_a INTEGER NOT NULL DEFAULT 0,
estranged_rumor_id TEXT NOT NULL DEFAULT '',
married INTEGER NOT NULL DEFAULT 0,marriage_action_id TEXT NOT NULL DEFAULT '',
marriage_blocked INTEGER NOT NULL DEFAULT 0,marriage_block_reason TEXT NOT NULL DEFAULT '',
divorced INTEGER NOT NULL DEFAULT 0,divorce_day REAL NOT NULL DEFAULT 0,
divorce_action_id TEXT NOT NULL DEFAULT '',divorce_rumor_id TEXT NOT NULL DEFAULT '',
conception_action_id TEXT NOT NULL DEFAULT '',last_conception_day REAL NOT NULL DEFAULT -1000,
last_marriage_evaluation_day REAL NOT NULL DEFAULT -1000,
organic_marriage_colocated_days INTEGER NOT NULL DEFAULT 0,
last_processed_day INTEGER NOT NULL DEFAULT -1,version INTEGER NOT NULL DEFAULT 1,
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void EnsureColumnRelationshipPairLifecycleLastMarriageEvaluationDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle", "last_marriage_evaluation_day", "REAL NOT NULL DEFAULT -1000");
        }

        internal static void EnsureColumnRelationshipPairLifecycleOrganicMarriageColocatedDays(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle", "organic_marriage_colocated_days", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairLifecycleMarriageBlocked(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle", "marriage_blocked", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnRelationshipPairLifecycleMarriageBlockReason(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle", "marriage_block_reason", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateIndexRelationshipLifecycleHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_lifecycle_heroes ON relationship_pair_lifecycle(hero_a_id,hero_b_id);", parameters, transaction);
        }

        internal static void CreateRelationshipMarriageReservations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_marriage_reservations (
hero_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,director_action_id TEXT NOT NULL,
created_day REAL NOT NULL,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRelationshipMarriageReservationAction(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_marriage_reservation_action
ON relationship_marriage_reservations(director_action_id);", parameters, transaction);
        }

        internal static void CreateRelationshipConceptionCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_conception_commitments (
conception_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
honor_a INTEGER NOT NULL,honor_b INTEGER NOT NULL,chance_a INTEGER NOT NULL,chance_b INTEGER NOT NULL,
roll_a INTEGER NOT NULL,roll_b INTEGER NOT NULL,passed_a INTEGER NOT NULL,passed_b INTEGER NOT NULL,
status TEXT NOT NULL,marriage_action_id TEXT NOT NULL DEFAULT '',affinity_delta INTEGER NOT NULL DEFAULT 0,
world_day REAL NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRelationshipAffairCommitments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_affair_commitments (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,spouse_a_id TEXT NOT NULL DEFAULT '',
spouse_b_id TEXT NOT NULL DEFAULT '',chance_a INTEGER NOT NULL DEFAULT 0,chance_b INTEGER NOT NULL DEFAULT 0,
roll_a INTEGER NOT NULL DEFAULT 0,roll_b INTEGER NOT NULL DEFAULT 0,passed_a INTEGER NOT NULL DEFAULT 0,
passed_b INTEGER NOT NULL DEFAULT 0,divorce_action_a_id TEXT NOT NULL DEFAULT '',divorce_action_b_id TEXT NOT NULL DEFAULT '',
divorce_a_completed INTEGER NOT NULL DEFAULT 0,divorce_b_completed INTEGER NOT NULL DEFAULT 0,
marriage_action_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL,world_day REAL NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRelationshipIncidents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_incidents (
incident_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,kind TEXT NOT NULL,
hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,world_day REAL NOT NULL,
summary TEXT NOT NULL DEFAULT '',rumor_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRelationshipIncidentsPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_incidents_pair ON relationship_incidents(pair_key,world_day DESC);", parameters, transaction);
        }

        internal static void EnsureDatabaseColumn(ReignDbConnection connection, string table, string column, string definition)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, table, column, definition);
        }

        internal static void CreateRelationshipStoryThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_story_threads (
thread_key TEXT PRIMARY KEY,pair_key TEXT NOT NULL,subject_id TEXT NOT NULL,target_id TEXT NOT NULL,
kind TEXT NOT NULL,route TEXT NOT NULL DEFAULT '',intensity REAL NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'active',
qualifying_events INTEGER NOT NULL DEFAULT 0,last_event_type TEXT NOT NULL DEFAULT '',last_event_id TEXT NOT NULL DEFAULT '',
last_event_day REAL NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(subject_id,target_id,kind));", parameters, transaction);
        }

        internal static void CreateIndexStoryThreadsPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_story_threads_pair ON relationship_story_threads(pair_key,kind,status,last_event_day DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipStorySeededPairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_story_seeded_pairs (
pair_key TEXT PRIMARY KEY,seeded_day REAL NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateRelationshipInteractionReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_interaction_receipts (
    event_id TEXT NOT NULL,
    subject_id TEXT NOT NULL,
    target_id TEXT NOT NULL,
    native_delta INTEGER NOT NULL DEFAULT 0,
    world_day REAL NOT NULL DEFAULT 0,
    reason TEXT NOT NULL DEFAULT '',
    source TEXT NOT NULL DEFAULT '',
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(event_id,subject_id,target_id)
);", parameters, transaction);
        }

        internal static void CreateCorrespondenceThreads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS correspondence_threads (
    thread_id TEXT PRIMARY KEY,
    participant_a TEXT NOT NULL,
    participant_b TEXT NOT NULL,
    last_letter_id TEXT NOT NULL DEFAULT '',
    last_activity_day REAL NOT NULL DEFAULT 0,
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL,
    UNIQUE(participant_a,participant_b)
);", parameters, transaction);
        }

        internal static void CreateLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS letters (
    letter_id TEXT PRIMARY KEY,
    thread_id TEXT NOT NULL,
    sender_id TEXT NOT NULL,
    sender_name TEXT NOT NULL DEFAULT '',
    recipient_id TEXT NOT NULL,
    recipient_name TEXT NOT NULL DEFAULT '',
    body TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'in_transit',
    source TEXT NOT NULL DEFAULT 'player',
    reason TEXT NOT NULL DEFAULT '',
    parent_letter_id TEXT NOT NULL DEFAULT '',
    origin_id TEXT NOT NULL DEFAULT '',
    destination_id TEXT NOT NULL DEFAULT '',
    dispatch_day REAL NOT NULL DEFAULT 0,
    delivery_day REAL NOT NULL DEFAULT 0,
    delivered_day REAL NOT NULL DEFAULT 0,
    read_day REAL NOT NULL DEFAULT 0,
    relationship_evaluated INTEGER NOT NULL DEFAULT 0,
    history_written INTEGER NOT NULL DEFAULT 0,
    payload_json TEXT NOT NULL DEFAULT '{}',
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL
);", parameters, transaction);
        }

        internal static void CreateIndexLettersDelivery(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_letters_delivery ON letters(status,delivery_day);", parameters, transaction);
        }

        internal static void CreateIndexLettersRecipient(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_letters_recipient ON letters(recipient_id,status,delivery_day DESC);", parameters, transaction);
        }

        internal static void EnsureColumnLettersTransitPolicyVersion(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "letters", "transit_policy_version", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnLettersBaseTransitDays(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "letters", "base_transit_days", "REAL NOT NULL DEFAULT 0");
        }

        internal static void CreateRelationshipRulerReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_ruler_receipts(timeline_id TEXT NOT NULL,operation_id TEXT NOT NULL,terms_hash TEXT NOT NULL,PRIMARY KEY(timeline_id,operation_id));", parameters, transaction);
        }

        internal static void CreateSharedRelationshipHistoryChapters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS shared_relationship_history_chapters (
history_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
chapter_ordinal INTEGER NOT NULL,state_fingerprint TEXT NOT NULL,
objective_summary TEXT NOT NULL DEFAULT '',a_interpretation TEXT NOT NULL DEFAULT '',
b_interpretation TEXT NOT NULL DEFAULT '',canon_status TEXT NOT NULL DEFAULT 'soft_relational_canon',
source_kind TEXT NOT NULL DEFAULT 'lazy_dialogue_generation',source_basis_json TEXT NOT NULL DEFAULT '{}',
generation_status TEXT NOT NULL DEFAULT 'generated',provider TEXT NOT NULL DEFAULT '',model TEXT NOT NULL DEFAULT '',
created_world_day REAL NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(pair_key,chapter_ordinal));", parameters, transaction);
        }

        internal static void CreateIndexSharedRelationshipHistoryPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_shared_relationship_history_pair ON shared_relationship_history_chapters(pair_key,chapter_ordinal DESC);", parameters, transaction);
        }

        internal static void CreateIndexSharedRelationshipHistoryHeroes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_shared_relationship_history_heroes ON shared_relationship_history_chapters(hero_a_id,hero_b_id,chapter_ordinal DESC);", parameters, transaction);
        }

        internal static void CreateRelationshipPairProvenance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_pair_provenance (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,pair_key TEXT NOT NULL,
source TEXT NOT NULL,active INTEGER NOT NULL DEFAULT 1,required INTEGER NOT NULL DEFAULT 0,
first_day REAL NOT NULL,last_day REAL NOT NULL,details_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,pair_key,source));", parameters, transaction);
        }

        internal static void CreateIndexRelationshipPairProvenanceActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_pair_provenance_active
ON relationship_pair_provenance(campaign_id,timeline_id,source,active,pair_key);", parameters, transaction);
        }

        internal static void CreateIndexRelationshipPairProvenancePair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_pair_provenance_pair
ON relationship_pair_provenance(pair_key,active,required,source);", parameters, transaction);
        }

        internal static void CreatePublicStandingInvalidations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS public_standing_invalidations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,
standing_revision INTEGER NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
pair_cursor TEXT NOT NULL DEFAULT '',affected_pairs INTEGER NOT NULL DEFAULT 0,
processed_pairs INTEGER NOT NULL DEFAULT 0,created_day REAL NOT NULL,
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,last_error TEXT NOT NULL DEFAULT '',
PRIMARY KEY(campaign_id,timeline_id,subject_id));", parameters, transaction);
        }

        internal static void CreateIndexPublicStandingInvalidationsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_public_standing_invalidations_status
ON public_standing_invalidations(status,updated_ts,campaign_id,timeline_id,subject_id);", parameters, transaction);
        }

        internal static void EnsureColumnCharacterPublicStandingCalculationStatus(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_public_standing", "calculation_status", "TEXT NOT NULL DEFAULT 'ready'");
        }

        internal static void EnsureColumnCharacterPublicStandingLastError(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_public_standing", "last_error", "TEXT NOT NULL DEFAULT ''");
        }
    }
}
