using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Dialogue module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class DialogueSchema
    {
        internal static void CreateConversationActionOutbox(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TABLE IF NOT EXISTS conversation_action_outbox (action_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,fingerprint TEXT NOT NULL,payload_json TEXT NOT NULL);", parameters, transaction);
        }

        internal static void CreateChatViewFrames(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS chat_view_frames (
turn_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,exchange_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
frame_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chat_view_requests (
request_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,job_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS chat_view_jobs (
job_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,
created_ts INTEGER NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_chat_view_jobs_session ON chat_view_jobs(session_id,timeline_id,created_ts);", parameters, transaction);
        }

        internal static void CreateCommunicationMessages(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS communication_messages (
message_id TEXT PRIMARY KEY, root_id TEXT NOT NULL, parent_id TEXT NOT NULL DEFAULT '',
timeline_id TEXT NOT NULL, logical_id TEXT NOT NULL, session_id TEXT NOT NULL,
speaker_id TEXT NOT NULL, recipient_id TEXT NOT NULL, player_id TEXT NOT NULL,
topic_key TEXT NOT NULL, kind TEXT NOT NULL, status TEXT NOT NULL,
created_day DOUBLE PRECISION NOT NULL, dispatch_day DOUBLE PRECISION NOT NULL DEFAULT 0,
due_day DOUBLE PRECISION NOT NULL DEFAULT 0, acquired_day DOUBLE PRECISION NOT NULL DEFAULT 0,
revision INTEGER NOT NULL DEFAULT 1, attempts INTEGER NOT NULL DEFAULT 0,
created_ts BIGINT NOT NULL DEFAULT 0, accepted_sequence BIGINT NOT NULL DEFAULT 0,
lease_token TEXT NOT NULL DEFAULT '', lease_until BIGINT NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_communication_due ON communication_messages(timeline_id,status,due_day);
CREATE INDEX IF NOT EXISTS idx_communication_session ON communication_messages(timeline_id,logical_id,speaker_id);
CREATE TABLE IF NOT EXISTS communication_history (
receipt_id TEXT PRIMARY KEY,message_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,acquired_day DOUBLE PRECISION NOT NULL,
acquired_ts BIGINT NOT NULL DEFAULT 0,accepted_sequence BIGINT NOT NULL DEFAULT nextval('memory_accepted_turn_sequence'),
payload_json TEXT NOT NULL,UNIQUE(message_id,owner_id));", parameters, transaction);
        }

        internal static void EnsureColumnLettersTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "letters", "timeline_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnLettersDispatchSequence(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "letters", "dispatch_sequence", "BIGINT NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnLettersDeliveryTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "letters", "delivery_timeline_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateMemoryRestoredLetters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_restored_letters (
letter_id TEXT PRIMARY KEY, restore_generation TEXT NOT NULL, content_hash TEXT NOT NULL,
received_day DOUBLE PRECISION NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS memory_restored_letter_context (
state_id TEXT PRIMARY KEY, restore_generation TEXT NOT NULL, timeline_id TEXT NOT NULL,
save_point_id TEXT NOT NULL, world_day DOUBLE PRECISION NOT NULL, head_sequence BIGINT NOT NULL,
head_event_id TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_letters_observer_memory ON letters(sender_id,recipient_id,dispatch_day);", parameters, transaction);
        }

        internal static void CreateConversationBanterEpisodes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_banter_episodes (
session_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,payload_json TEXT NOT NULL,updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_banter_timeline ON conversation_banter_episodes(timeline_id,updated_ts);", parameters, transaction);
        }

        internal static void EnsureColumnObligationsTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "timeline_id", "TEXT NOT NULL DEFAULT 'main'");
        }

        internal static void CreateConversationAcceptedUnits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_accepted_units (
unit_id TEXT PRIMARY KEY, response_hash TEXT NOT NULL, restore_generation TEXT NOT NULL,
receipt_json TEXT NOT NULL, created_ts BIGINT NOT NULL);", parameters, transaction);
        }

        internal static void CreateConversationNegotiations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_negotiations (
record_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,
topic TEXT NOT NULL,object_id TEXT NOT NULL,revision INTEGER NOT NULL,payload_json TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS idx_negotiation_identity ON conversation_negotiations(timeline_id,owner_id,peer_id,topic,object_id);
CREATE TABLE IF NOT EXISTS conversation_negotiation_turns (
receipt_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,
turn_id TEXT NOT NULL,payload_json TEXT NOT NULL);", parameters, transaction);
        }

        internal static void CreateConversationContinuity(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_continuity (
record_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, owner_id TEXT NOT NULL, peer_id TEXT NOT NULL,
kind TEXT NOT NULL, status TEXT NOT NULL, importance REAL NOT NULL, revision INTEGER NOT NULL,
source_event_id TEXT NOT NULL, source_turn_id TEXT NOT NULL, updated_ts INTEGER NOT NULL, payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_continuity_owner ON conversation_continuity(timeline_id,owner_id,kind,status);
CREATE TABLE IF NOT EXISTS conversation_continuity_versions (
version_id TEXT PRIMARY KEY, record_id TEXT NOT NULL, source_event_id TEXT NOT NULL,
revision INTEGER NOT NULL, payload_json TEXT NOT NULL, created_ts INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS conversation_scene_handoffs (
handoff_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, owner_id TEXT NOT NULL, anchor TEXT NOT NULL,
source_turn_id TEXT NOT NULL, world_day REAL NOT NULL, payload_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS conversation_continuity_pending (
pending_id TEXT PRIMARY KEY,record_id TEXT NOT NULL,timeline_id TEXT NOT NULL,owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,
source_event_id TEXT NOT NULL,source_turn_id TEXT NOT NULL,source_hash TEXT NOT NULL,world_day REAL NOT NULL,
status TEXT NOT NULL,payload_json TEXT NOT NULL,created_ts BIGINT NOT NULL,resolved_event_id TEXT NOT NULL DEFAULT '');
CREATE INDEX IF NOT EXISTS idx_continuity_pending_owner ON conversation_continuity_pending(timeline_id,owner_id,status);", parameters, transaction);
        }

        internal static void CreateConversationContinuityRepairs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_continuity_repairs (
manifest_hash TEXT PRIMARY KEY, campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,receipt_json TEXT NOT NULL,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateConversationReferenceEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_reference_edges (
edge_id TEXT PRIMARY KEY,owner_id TEXT NOT NULL,timeline_id TEXT NOT NULL,restore_generation TEXT NOT NULL,
from_type TEXT NOT NULL,from_id TEXT NOT NULL,from_hash TEXT NOT NULL,
to_type TEXT NOT NULL,to_id TEXT NOT NULL,to_hash TEXT NOT NULL,relation TEXT NOT NULL,
source_event_id TEXT NOT NULL,as_of_sequence BIGINT NOT NULL,world_day DOUBLE PRECISION NOT NULL,
created_ts BIGINT NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_conversation_reference_forward ON conversation_reference_edges
(owner_id,timeline_id,restore_generation,from_type,from_id);
CREATE INDEX IF NOT EXISTS idx_conversation_reference_reverse ON conversation_reference_edges
(owner_id,timeline_id,restore_generation,to_type,to_id);", parameters, transaction);
        }

        internal static void CreateConversationSceneState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_scene_state (
hero_id TEXT PRIMARY KEY,
location_override TEXT NOT NULL DEFAULT '',
location_class TEXT NOT NULL DEFAULT '',
clothing_override TEXT NOT NULL DEFAULT '',
source_turn_id TEXT NOT NULL DEFAULT '',
source_mode TEXT NOT NULL DEFAULT '',
world_hour INTEGER NOT NULL DEFAULT -1,
expires_world_hour INTEGER NOT NULL DEFAULT -1,
revision INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateConversationSceneProposals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_scene_proposals (
proposal_id TEXT PRIMARY KEY,
scene_turn_id TEXT NOT NULL,
subject_id TEXT NOT NULL,
speaker_id TEXT NOT NULL DEFAULT '',
location_value TEXT NOT NULL DEFAULT '',
location_class TEXT NOT NULL DEFAULT '',
clothing_value TEXT NOT NULL DEFAULT '',
linked_player INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL DEFAULT 'pending',
world_hour INTEGER NOT NULL,
payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexSceneProposalsTurnSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_scene_proposals_turn_subject ON conversation_scene_proposals(scene_turn_id,subject_id,status);", parameters, transaction);
        }

        internal static void CreateGroupConversationState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS group_conversation_state (
session_id TEXT PRIMARY KEY,
campaign_id TEXT NOT NULL,
mode TEXT NOT NULL DEFAULT '',
participant_ids_json TEXT NOT NULL DEFAULT '[]',
last_speaker_id TEXT NOT NULL DEFAULT '',
last_speaker_name TEXT NOT NULL DEFAULT '',
last_text TEXT NOT NULL DEFAULT '',
open_questions_json TEXT NOT NULL DEFAULT '[]',
topics_json TEXT NOT NULL DEFAULT '[]',
state_json TEXT NOT NULL DEFAULT '{}',
revision INTEGER NOT NULL DEFAULT 0,
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateGroupConversationContributions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS group_conversation_contributions (
contribution_id TEXT PRIMARY KEY,
session_id TEXT NOT NULL,
speaker_id TEXT NOT NULL DEFAULT '',
speaker_name TEXT NOT NULL DEFAULT '',
role TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL DEFAULT '',
addressed_ids_json TEXT NOT NULL DEFAULT '[]',
ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexGroupContributionsSession(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_group_contributions_session ON group_conversation_contributions(session_id,ts,contribution_id);", parameters, transaction);
        }

        internal static void EnsureColumnGroupConversationContributionsSceneTurnId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "scene_turn_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnGroupConversationContributionsAudienceJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "audience_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void EnsureColumnGroupConversationContributionsValidationStatus(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "validation_status", "TEXT NOT NULL DEFAULT 'legacy_unknown'");
        }

        internal static void EnsureColumnGroupConversationContributionsDeliveryOrder(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "delivery_order", "BIGINT NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnGroupConversationContributionsTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "timeline_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnGroupConversationContributionsWorldDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "world_day", "REAL NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnGroupConversationContributionsSourceEventId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_contributions", "source_event_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnGroupConversationStateTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_state", "timeline_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnGroupConversationStateWorldDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "group_conversation_state", "world_day", "REAL NOT NULL DEFAULT 0");
        }

        internal static void CreateIndexGroupContributionsBeat(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_group_contributions_beat ON group_conversation_contributions(session_id,scene_turn_id,ts);", parameters, transaction);
        }

        internal static void CreateConversationMotiveCooldowns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_motive_cooldowns (
cooldown_id TEXT PRIMARY KEY, subject_id TEXT NOT NULL, target_id TEXT NOT NULL, kind TEXT NOT NULL,
session_id TEXT NOT NULL DEFAULT '', world_day REAL NOT NULL DEFAULT 0, created_ts INTEGER NOT NULL,
UNIQUE(subject_id,target_id,kind,session_id));", parameters, transaction);
        }

        internal static void CreateIndexMotiveCooldownPair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_motive_cooldown_pair ON conversation_motive_cooldowns(subject_id,target_id,kind,world_day DESC);", parameters, transaction);
        }

        internal static void CreateCharacterPerformanceEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_performance_events (
receipt_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,timeline_id TEXT NOT NULL,subject_id TEXT NOT NULL,world_day REAL NOT NULL,sequence INTEGER NOT NULL,
kind TEXT NOT NULL,outcome TEXT NOT NULL,battle_type TEXT NOT NULL DEFAULT '',competition_id TEXT NOT NULL,
source TEXT NOT NULL,source_hash TEXT NOT NULL,knowledge_rules_json TEXT NOT NULL DEFAULT '[]',
UNIQUE(event_id,subject_id));", parameters, transaction);
        }

        internal static void CreateIndexPerformanceSubjectDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_performance_subject_day ON character_performance_events(timeline_id,subject_id,world_day);", parameters, transaction);
        }

        internal static void CreateConversationIntoxication(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_intoxication (
hero_id TEXT PRIMARY KEY, state_json TEXT NOT NULL DEFAULT '{}');
CREATE TABLE IF NOT EXISTS conversation_drinking_turns (
hero_id TEXT NOT NULL, turn_key TEXT NOT NULL, receipt_json TEXT NOT NULL,
PRIMARY KEY(hero_id,turn_key));", parameters, transaction);
        }

        internal static void CreateConversationDrinkScene(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_drink_scene (
scene_id TEXT PRIMARY KEY, state_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal const string CreateFinalGauntletScheduleSql = @"CREATE TABLE IF NOT EXISTS final_gauntlet_schedule(
run_id TEXT PRIMARY KEY,
campaign_id TEXT NOT NULL,
state TEXT NOT NULL,
stage_b_scheduled INTEGER NOT NULL DEFAULT 0,
baseline_save_name TEXT NOT NULL DEFAULT '',
manifest_fingerprint TEXT NOT NULL,
catalog_fingerprint TEXT NOT NULL,
settings_fingerprint TEXT NOT NULL,
state_fingerprint TEXT NOT NULL,
pause_requested INTEGER NOT NULL DEFAULT 0,
created_ts INTEGER NOT NULL,
updated_ts INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS idx_final_gauntlet_schedule_campaign
ON final_gauntlet_schedule(campaign_id,state);
CREATE TABLE IF NOT EXISTS final_gauntlet_leases(
run_id TEXT NOT NULL,
case_instance_id TEXT NOT NULL,
controller_id TEXT NOT NULL,
leased_ts INTEGER NOT NULL,
expires_ts INTEGER NOT NULL,
correlation_ledger_json TEXT NOT NULL,
PRIMARY KEY(run_id,case_instance_id));";

        internal const string AlterFinalGauntletScheduleSql = @"ALTER TABLE final_gauntlet_schedule
ADD COLUMN IF NOT EXISTS pause_requested INTEGER NOT NULL DEFAULT 0;";

        internal const string AlterFinalGauntletScheduleSql2 = @"ALTER TABLE final_gauntlet_schedule
ADD COLUMN IF NOT EXISTS baseline_save_name TEXT NOT NULL DEFAULT '';";

        internal const string CreateFinalGauntletRunsSql = @"
CREATE TABLE IF NOT EXISTS final_gauntlet_runs(
    run_id TEXT PRIMARY KEY,
    campaign_id TEXT NOT NULL,
    stage TEXT NOT NULL,
    build_version TEXT NOT NULL,
    state TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS final_gauntlet_cases(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    family TEXT NOT NULL,
    evaluation_kind TEXT NOT NULL,
    execution_kind TEXT NOT NULL,
    mode TEXT NOT NULL,
    requires_provider INTEGER NOT NULL,
    requires_game INTEGER NOT NULL,
    requirement_ids TEXT NOT NULL,
    tags TEXT NOT NULL,
    behavioral_requirement TEXT NOT NULL,
    hard_prohibitions TEXT NOT NULL,
    prerequisite_capabilities TEXT NOT NULL,
    evidence_needs TEXT NOT NULL,
    state TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_attempts(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    attempt_number INTEGER NOT NULL,
    correlation_id TEXT NOT NULL,
    status TEXT NOT NULL,
    error TEXT NOT NULL DEFAULT '',
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id,attempt_number),
    UNIQUE(run_id,correlation_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_assertions(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    assertion_id TEXT NOT NULL,
    passed INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id,assertion_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_evidence(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    evidence_key TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id,evidence_key));
CREATE TABLE IF NOT EXISTS final_gauntlet_review_items(
    run_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    blinded_payload_json TEXT NOT NULL,
    answer_key_json TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,review_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_provider_correlations(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,correlation_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_provider_calls(
    run_id TEXT NOT NULL,
    provider_call_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    request_type TEXT NOT NULL,
    model TEXT NOT NULL,
    physical_attempt INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    status TEXT NOT NULL,
    started_ts INTEGER NOT NULL,
    completed_ts INTEGER NOT NULL DEFAULT 0,
    error TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(run_id,provider_call_id),
    UNIQUE(run_id,ordinal));";

        internal const string AlterFinalGauntletAttemptsSql = "ALTER TABLE final_gauntlet_attempts ADD COLUMN IF NOT EXISTS error TEXT NOT NULL DEFAULT '';";

        internal static void CreatePersonalGossipClock(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS personal_gossip_clock (
timeline_id TEXT PRIMARY KEY,world_day REAL NOT NULL,snapshot_json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS personal_gossip_topics (
topic_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,speaker_id TEXT NOT NULL,player_id TEXT NOT NULL,
session_id TEXT NOT NULL,created_day REAL NOT NULL,status TEXT NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_gossip_topics_speaker ON personal_gossip_topics(timeline_id,speaker_id,created_day);
CREATE TABLE IF NOT EXISTS personal_gossip_messages (
message_id TEXT PRIMARY KEY,topic_id TEXT NOT NULL,root_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
speaker_id TEXT NOT NULL,recipient_id TEXT NOT NULL,player_id TEXT NOT NULL,is_reply INTEGER NOT NULL DEFAULT 0,
hop INTEGER NOT NULL,created_day REAL NOT NULL,due_day REAL NOT NULL,expires_day REAL NOT NULL,
status TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,lease_token TEXT NOT NULL DEFAULT '',
lease_until INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_gossip_due ON personal_gossip_messages(timeline_id,status,due_day);
CREATE INDEX IF NOT EXISTS idx_gossip_caps ON personal_gossip_messages(timeline_id,speaker_id,created_day);
CREATE TABLE IF NOT EXISTS personal_gossip_history (
receipt_id TEXT PRIMARY KEY,message_id TEXT NOT NULL,root_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
owner_id TEXT NOT NULL,peer_id TEXT NOT NULL,acquired_day REAL NOT NULL,raise_session TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL,UNIQUE(message_id,owner_id));
CREATE INDEX IF NOT EXISTS idx_gossip_history_owner ON personal_gossip_history(timeline_id,owner_id,acquired_day DESC);", parameters, transaction);
        }

        internal static void CreatePersonalGossipTestFixtures(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS personal_gossip_test_fixtures (
fixture_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,speaker_id TEXT NOT NULL,marker TEXT NOT NULL,
created_utc TEXT NOT NULL,consumed_topic TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL);", parameters, transaction);
        }

        internal static void CreateConversationInputReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_input_receipts (
input_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, session_id TEXT NOT NULL,
player_id TEXT NOT NULL, source_turn_id TEXT NOT NULL, source_hash TEXT NOT NULL,
restore_generation TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'received',
projection_json TEXT NOT NULL DEFAULT '{}', response_json TEXT NOT NULL DEFAULT '{}',
created_ts BIGINT NOT NULL, updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_conversation_input_session ON conversation_input_receipts(session_id,created_ts);", parameters, transaction);
        }

        internal static void EnsureColumnEventsEventCategory(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "events", "event_category", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnEventsEventSubtype(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "events", "event_subtype", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnMemoriesMemoryDomain(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memories", "memory_domain", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnSummariesMemoryLane(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "summaries", "memory_lane", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateKnowledgeReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS knowledge_receipts (
receipt_id TEXT PRIMARY KEY,event_id TEXT NOT NULL,npc_id TEXT NOT NULL,acquisition_type TEXT NOT NULL DEFAULT 'known',
confidence REAL NOT NULL DEFAULT 1.0,reliability REAL NOT NULL DEFAULT 1.0,acquired_day REAL NOT NULL DEFAULT 0,
source_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'active',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(event_id,npc_id,acquisition_type,source_id));", parameters, transaction);
        }

        internal static void CreateIndexKnowledgeReceiptsNpc(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_knowledge_receipts_npc ON knowledge_receipts(npc_id,status,acquired_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexKnowledgeReceiptsEvent(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_knowledge_receipts_event ON knowledge_receipts(event_id,status);", parameters, transaction);
        }

        internal static void CreateConversationSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_sessions (
session_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,npc_id TEXT NOT NULL,player_id TEXT NOT NULL DEFAULT '',
channel TEXT NOT NULL DEFAULT 'in_person',status TEXT NOT NULL DEFAULT 'open',start_ts INTEGER NOT NULL,end_ts INTEGER NOT NULL DEFAULT 0,
start_world_day REAL NOT NULL DEFAULT 0,end_world_day REAL NOT NULL DEFAULT 0,location_id TEXT NOT NULL DEFAULT '',
participants_json TEXT NOT NULL DEFAULT '[]',close_reason TEXT NOT NULL DEFAULT '',scene_summary_id TEXT NOT NULL DEFAULT '',
legacy_inferred INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateIndexConversationSessionsNpc(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_sessions_npc ON conversation_sessions(npc_id,status,start_ts DESC);", parameters, transaction);
        }

        internal static void CreateConversationTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_turns (
turn_id TEXT PRIMARY KEY,session_id TEXT NOT NULL,event_id TEXT NOT NULL DEFAULT '',turn_order INTEGER NOT NULL,
exchange_id TEXT NOT NULL DEFAULT '',role TEXT NOT NULL,speaker_id TEXT NOT NULL DEFAULT '',speaker_name TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL,channel TEXT NOT NULL DEFAULT 'in_person',world_day REAL NOT NULL DEFAULT 0,ts INTEGER NOT NULL,
location_id TEXT NOT NULL DEFAULT '',participants_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL DEFAULT 'active',
payload_json TEXT NOT NULL DEFAULT '{}',UNIQUE(session_id,turn_order));", parameters, transaction);
        }

        internal static void CreateIndexConversationTurnsSession(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_session ON conversation_turns(session_id,turn_order);", parameters, transaction);
        }

        internal static void CreateIndexConversationTurnsTs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_ts ON conversation_turns(ts DESC);", parameters, transaction);
        }

        internal static void CreateIndexConversationTurnsSpeakerTs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_conversation_turns_speaker_ts ON conversation_turns(speaker_id,ts DESC);", parameters, transaction);
        }

        internal static void CreateConversationTurnFts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE VIRTUAL TABLE IF NOT EXISTS conversation_turn_fts USING fts5(
turn_id UNINDEXED,text,speaker,participants,channel UNINDEXED);", parameters, transaction);
        }

        internal static void CreateMemorySources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_sources (
document_type TEXT NOT NULL,document_id TEXT NOT NULL,source_type TEXT NOT NULL,source_id TEXT NOT NULL,
ordinal INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(document_type,document_id,source_type,source_id));", parameters, transaction);
        }

        internal static void CreateIndexMemorySourcesSource(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_memory_sources_source ON memory_sources(source_type,source_id);", parameters, transaction);
        }

        internal static void CreateConversationRouteAcceptance(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS conversation_route_acceptance (
unit_id TEXT PRIMARY KEY, input_hash TEXT NOT NULL, accepted_reply_hash TEXT NOT NULL,
result_json TEXT NOT NULL, restore_generation TEXT NOT NULL, created_ts BIGINT NOT NULL);", parameters, transaction);
        }

        internal static void CreateMemoryLateInteractionSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
CREATE TABLE IF NOT EXISTS memory_late_interaction_sources (
 turn_id TEXT NOT NULL,view TEXT NOT NULL,source_hash TEXT NOT NULL,text_hash TEXT NOT NULL,
 source_revision TEXT NOT NULL,model_hash TEXT NOT NULL,schema_version TEXT NOT NULL,
 worker_epoch TEXT NOT NULL,updated_ts BIGINT NOT NULL,PRIMARY KEY(turn_id,view));
ALTER TABLE memory_late_interaction_sources ADD COLUMN IF NOT EXISTS transcript_hash TEXT NOT NULL DEFAULT '';
CREATE TABLE IF NOT EXISTS memory_late_interaction_attempts (
 turn_id TEXT PRIMARY KEY,source_revision TEXT NOT NULL,model_hash TEXT NOT NULL,schema_version TEXT NOT NULL,
 attempts INTEGER NOT NULL,next_attempt_ts BIGINT NOT NULL,outcome TEXT NOT NULL);", parameters, transaction);
        }

        internal static void CreateWorldHistoryLieChecks(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_lie_checks (
lie_check_id TEXT PRIMARY KEY,episode_key TEXT NOT NULL UNIQUE,claim_check_id TEXT NOT NULL DEFAULT '',campaign_id TEXT NOT NULL,
timeline_id TEXT NOT NULL,world_day REAL NOT NULL,claimant_id TEXT NOT NULL,target_id TEXT NOT NULL,raw_claim TEXT NOT NULL,
normalized_claim TEXT NOT NULL,objective_verdict TEXT NOT NULL,knowledge_basis TEXT NOT NULL,claimant_roguery REAL,
claimant_charm REAL,claimant_average REAL,target_roguery REAL,target_charm REAL,target_average REAL,formula_version TEXT NOT NULL,
success_chance REAL,roll_value REAL,outcome TEXT NOT NULL,reaction_strategy TEXT NOT NULL DEFAULT '',exploit_opening INTEGER NOT NULL DEFAULT 0,
evidence_event_ids_json TEXT NOT NULL DEFAULT '[]',known_event_ids_json TEXT NOT NULL DEFAULT '[]',matched_json TEXT NOT NULL DEFAULT '[]',
mismatched_json TEXT NOT NULL DEFAULT '[]',prompt_packet_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL); ", parameters, transaction);
        }

        internal static void CreateWorldHistoryClaimBeliefs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS world_history_claim_beliefs (
belief_id TEXT PRIMARY KEY,lie_check_id TEXT NOT NULL,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,believer_id TEXT NOT NULL,
claimant_id TEXT NOT NULL,raw_claim TEXT NOT NULL,normalized_claim TEXT NOT NULL,state TEXT NOT NULL,confidence REAL NOT NULL,
evidence_event_ids_json TEXT NOT NULL DEFAULT '[]',created_day REAL NOT NULL,disproven_day REAL NOT NULL DEFAULT -1,
discovered_event_ids_json TEXT NOT NULL DEFAULT '[]',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL); ", parameters, transaction);
        }

        internal static void CreateIndexWhLieParticipants(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_lie_participants ON world_history_lie_checks(target_id,claimant_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateIndexWhBeliefOwnerState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_wh_belief_owner_state ON world_history_claim_beliefs(believer_id,state,updated_ts DESC);", parameters, transaction);
        }

        internal static void CreateMemoryBackgroundJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_background_jobs (
job_id TEXT PRIMARY KEY,job_type TEXT NOT NULL,owner_id TEXT NOT NULL DEFAULT '',group_key TEXT NOT NULL DEFAULT '',
session_id TEXT NOT NULL DEFAULT '',priority TEXT NOT NULL DEFAULT 'maintenance',status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',result_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexMemoryBackgroundDue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_memory_background_due ON memory_background_jobs(status,next_attempt_ts,lease_until);", parameters, transaction);
        }

        internal static void EnsureDatabaseColumn(ReignDbConnection connection, string table, string column, string definition)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, table, column, definition);
        }

        internal static void CreateMemoryPrecisionState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_precision_state (
state_id TEXT PRIMARY KEY, mode TEXT NOT NULL DEFAULT 'shadow', restore_generation TEXT NOT NULL,
projection_generation INTEGER NOT NULL DEFAULT 1, updated_ts INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS memory_projections (
projection_id TEXT PRIMARY KEY, timeline_id TEXT NOT NULL, owner_id TEXT NOT NULL, peer_id TEXT NOT NULL,
restore_generation TEXT NOT NULL, projection_generation INTEGER NOT NULL, source_hash TEXT NOT NULL,
watermark BIGINT NOT NULL, payload_json TEXT NOT NULL, updated_ts INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS idx_memory_projection_owner ON memory_projections(timeline_id,owner_id,peer_id);", parameters, transaction);
        }

        internal static void EnsureColumnMemoryPrecisionStateSourceRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_precision_state", "source_revision", "BIGINT NOT NULL DEFAULT 0");
        }

        internal static void CreateStatement(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE SEQUENCE IF NOT EXISTS memory_accepted_turn_sequence;", parameters, transaction);
        }

        internal static void EnsureColumnConversationTurnsAcceptedSequence(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "conversation_turns", "accepted_sequence", "BIGINT NOT NULL DEFAULT nextval('memory_accepted_turn_sequence')");
        }

        internal static void CreateIndexAssertionPrecision(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_assertion_precision ON temporal_knowledge_assertions(timeline_id,perspective_owner_id,fact_key,valid_to_ts);", parameters, transaction);
        }

        internal static void CreateStatement2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE OR REPLACE FUNCTION memory_precision_source_changed() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN UPDATE memory_precision_state SET source_revision=source_revision+1 WHERE state_id='current'; RETURN NULL; END $$;", parameters, transaction);
        }

        internal static void CreateMemoryRestoredSourceContext(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_restored_source_context (
state_id TEXT PRIMARY KEY, restore_generation TEXT NOT NULL, timeline_id TEXT NOT NULL,
save_point_id TEXT NOT NULL, world_day DOUBLE PRECISION NOT NULL, head_sequence BIGINT NOT NULL, head_event_id TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS memory_restored_sources (
source_table TEXT NOT NULL, source_id TEXT NOT NULL, restore_generation TEXT NOT NULL, content_hash TEXT NOT NULL,
PRIMARY KEY(source_table,source_id,restore_generation));
CREATE INDEX IF NOT EXISTS idx_memory_restored_sources_generation ON memory_restored_sources(restore_generation);", parameters, transaction);
        }

        internal static void EnsureColumnMemoryRestoredSourceContextLegacyRecoveryVersion(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_restored_source_context", "legacy_recovery_version", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnMemoryRestoredSourceContextSourceGeneration(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_restored_source_context", "source_generation", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnMemoryRestoredSourceContextLoadSessionId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_restored_source_context", "load_session_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateMemoryContinuityBranches(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_continuity_branches (
timeline_id TEXT NOT NULL,owner_id TEXT NOT NULL,source_record_id TEXT NOT NULL,record_id TEXT NOT NULL,
PRIMARY KEY(timeline_id,owner_id,source_record_id));
CREATE INDEX IF NOT EXISTS idx_memory_continuity_branch_record ON memory_continuity_branches(record_id);", parameters, transaction);
        }

        internal static void CreateMemorySpecialistSources(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS memory_specialist_sources (
turn_id TEXT PRIMARY KEY,source_hash TEXT NOT NULL,text_hash TEXT NOT NULL,schema_version TEXT NOT NULL,
speaker_id TEXT NOT NULL,topics_json TEXT NOT NULL,entities_json TEXT NOT NULL,payload_json TEXT NOT NULL,
updated_ts BIGINT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_memory_specialist_speaker ON memory_specialist_sources(speaker_id);
CREATE INDEX IF NOT EXISTS idx_memory_specialist_topics ON memory_specialist_sources USING GIN ((topics_json::jsonb));", parameters, transaction);
        }

        internal static void EnsureColumnMemorySpecialistSourcesPublicTopicsJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_specialist_sources", "public_topics_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void EnsureColumnMemorySpecialistSourcesPublicEntitiesJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_specialist_sources", "public_entities_json", "TEXT NOT NULL DEFAULT '[]'");
        }

        internal static void EnsureColumnMemorySpecialistSourcesSourceRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "memory_specialist_sources", "source_revision", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateEmbeddingDocuments(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS embedding_documents (
source_type TEXT NOT NULL,source_id TEXT NOT NULL,model_version TEXT NOT NULL,provider TEXT NOT NULL,
campaign_id TEXT NOT NULL DEFAULT '',timeline_id TEXT NOT NULL DEFAULT '',content_hash TEXT NOT NULL DEFAULT '',
vector_id TEXT NOT NULL DEFAULT '',dimensions INTEGER NOT NULL DEFAULT 384,status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',PRIMARY KEY(source_type,source_id,model_version,provider));", parameters, transaction);
        }

        internal static void CreateIndexEmbeddingDocumentsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_embedding_documents_status ON embedding_documents(status,updated_ts);", parameters, transaction);
        }

        internal static void CreateEmbeddingJobs(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS embedding_jobs (
source_type TEXT NOT NULL,source_id TEXT NOT NULL,operation TEXT NOT NULL DEFAULT 'upsert',status TEXT NOT NULL DEFAULT 'pending',
attempt_count INTEGER NOT NULL DEFAULT 0,next_attempt_ts INTEGER NOT NULL DEFAULT 0,last_error TEXT NOT NULL DEFAULT '',
enqueued_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,PRIMARY KEY(source_type,source_id));", parameters, transaction);
        }

        internal static void CreateIndexEmbeddingJobsReady(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_embedding_jobs_ready ON embedding_jobs(status,next_attempt_ts,enqueued_ts);", parameters, transaction);
        }

        internal static void CreateStatement3(ReignDbConnection connection, object safe, object table, object upsert, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER IF NOT EXISTS trg_" + safe + "_insert AFTER INSERT ON " + table + " BEGIN " + upsert + " END;", parameters, transaction);
        }

        internal static void CreateStatement4(ReignDbConnection connection, object safe, object updateColumns, object table, object upsert, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER IF NOT EXISTS trg_" + safe + "_update AFTER UPDATE OF " + updateColumns + " ON " + table + " BEGIN " + upsert + " END;", parameters, transaction);
        }

        internal static void CreateStatement5(ReignDbConnection connection, object safe, object table, object sourceType, object idColumn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER IF NOT EXISTS trg_" + safe + "_delete AFTER DELETE ON " + table + " BEGIN "
                + "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',OLD." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now'); END;", parameters, transaction);
        }

        internal static void DropStatement(ReignDbConnection connection, object safe, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_insert ON " + table + ";", parameters, transaction);
        }

        internal static void DropStatement2(ReignDbConnection connection, object safe, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update ON " + table + ";", parameters, transaction);
        }

        internal static void DropStatement3(ReignDbConnection connection, object safe, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update_ineligible ON " + table + ";", parameters, transaction);
        }

        internal static void DropStatement4(ReignDbConnection connection, object safe, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_delete ON " + table + ";", parameters, transaction);
        }

        internal static void DropStatement5(ReignDbConnection connection, object safe, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP FUNCTION IF EXISTS fn_" + safe + "_queue() CASCADE;", parameters, transaction);
        }

        internal static void DropStatement6(ReignDbConnection connection, object safe, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_insert;", parameters, transaction);
        }

        internal static void DropStatement7(ReignDbConnection connection, object safe, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update;", parameters, transaction);
        }

        internal static void DropStatement8(ReignDbConnection connection, object safe, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_update_ineligible;", parameters, transaction);
        }

        internal static void DropStatement9(ReignDbConnection connection, object safe, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DROP TRIGGER IF EXISTS trg_" + safe + "_delete;", parameters, transaction);
        }

        internal static void CreateStatement6(ReignDbConnection connection, object safe, object table, object eligible, object upsert, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_insert AFTER INSERT ON " + table
                + " WHEN " + eligible + " BEGIN " + upsert + " END;", parameters, transaction);
        }

        internal static void CreateStatement7(ReignDbConnection connection, object safe, object updateColumns, object table, object eligible, object upsert, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_update AFTER UPDATE OF " + updateColumns + " ON " + table
                + " WHEN " + eligible + " BEGIN " + upsert + " END;", parameters, transaction);
        }

        internal static void CreateStatement8(ReignDbConnection connection, object safe, object updateColumns, object table, object eligible, object removeValue, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_update_ineligible AFTER UPDATE OF " + updateColumns + " ON " + table
                + " WHEN NOT " + eligible + " BEGIN " + removeValue + " END;", parameters, transaction);
        }

        internal static void CreateStatement9(ReignDbConnection connection, object safe, object table, object sourceType, object idColumn, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe + "_delete AFTER DELETE ON " + table + " BEGIN "
                + "INSERT INTO embedding_jobs(source_type,source_id,operation,status,attempt_count,next_attempt_ts,last_error,enqueued_ts,updated_ts) "
                + "VALUES('" + sourceType + "',OLD." + idColumn + ",'delete','pending',0,0,'',strftime('%s','now'),strftime('%s','now')) "
                + "ON CONFLICT(source_type,source_id) DO UPDATE SET operation='delete',status='pending',attempt_count=0,next_attempt_ts=0,last_error='',enqueued_ts=strftime('%s','now'),updated_ts=strftime('%s','now'); END;", parameters, transaction);
        }

        internal static void CreateStatement10(ReignDbConnection connection, object function, object idColumn, object idColumn2, object eligibility, object source, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
CREATE OR REPLACE FUNCTION " + function + @"() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE
    source_id_value text;
    requested_operation text;
    now_epoch bigint := CAST(EXTRACT(EPOCH FROM clock_timestamp()) AS bigint);
BEGIN
    IF TG_OP='DELETE' THEN
        source_id_value := OLD." + idColumn + @"::text;
        requested_operation := 'delete';
    ELSE
        source_id_value := NEW." + idColumn2 + @"::text;
        IF " + eligibility + @" THEN
            requested_operation := 'upsert';
        ELSIF TG_OP='INSERT' THEN
            -- An ineligible row has never had a semantic document to retire.
            -- Match the SQLite trigger policy by leaving no no-op delete job.
            RETURN NEW;
        ELSE
            requested_operation := 'delete';
        END IF;
    END IF;

    INSERT INTO embedding_jobs(
        source_type,source_id,operation,status,attempt_count,next_attempt_ts,
        last_error,enqueued_ts,updated_ts)
    VALUES('" + source + @"',source_id_value,requested_operation,'pending',0,0,'',
           now_epoch,now_epoch)
    ON CONFLICT(source_type,source_id) DO UPDATE SET
        operation=excluded.operation,status='pending',attempt_count=0,
        next_attempt_ts=0,last_error='',enqueued_ts=excluded.enqueued_ts,
        updated_ts=excluded.updated_ts;
    IF TG_OP='DELETE' THEN
        RETURN OLD;
    END IF;
    RETURN NEW;
END
$$;", parameters, transaction);
        }

        internal static void CreateStatement11(ReignDbConnection connection, object safe, object table, object function, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe
                + "_insert AFTER INSERT ON " + table
                + " FOR EACH ROW EXECUTE FUNCTION " + function + "();", parameters, transaction);
        }

        internal static void CreateStatement12(ReignDbConnection connection, object safe, object table, object function, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe
                + "_update AFTER UPDATE ON " + table
                + " FOR EACH ROW EXECUTE FUNCTION " + function + "();", parameters, transaction);
        }

        internal static void CreateStatement13(ReignDbConnection connection, object safe, object table, object function, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE TRIGGER trg_" + safe
                + "_delete AFTER DELETE ON " + table
                + " FOR EACH ROW EXECUTE FUNCTION " + function + "();", parameters, transaction);
        }

        internal static void CreateSharedActivityEvents(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS shared_activity_events (
activity_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,kind TEXT NOT NULL,
world_day REAL NOT NULL,sequence INTEGER NOT NULL,location_id TEXT NOT NULL,source_id TEXT NOT NULL,
source_hash TEXT NOT NULL,payload_json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS idx_shared_activity_time ON shared_activity_events(timeline_id,player_id,world_day DESC);
CREATE TABLE IF NOT EXISTS shared_activity_observers (
activity_id TEXT NOT NULL,owner_id TEXT NOT NULL,role TEXT NOT NULL,PRIMARY KEY(activity_id,owner_id));
CREATE INDEX IF NOT EXISTS idx_shared_activity_owner ON shared_activity_observers(owner_id,activity_id);
CREATE TABLE IF NOT EXISTS shared_activity_deliveries (
activity_id TEXT NOT NULL,owner_id TEXT NOT NULL,turn_id TEXT NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(activity_id,owner_id));
CREATE TABLE IF NOT EXISTS shared_travel_contact (
interval_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,owner_id TEXT NOT NULL,
party_id TEXT NOT NULL,start_day REAL NOT NULL,end_day REAL NOT NULL,source_id TEXT NOT NULL,
motif INTEGER NOT NULL,provenance TEXT NOT NULL DEFAULT 'modeled_routine');
CREATE INDEX IF NOT EXISTS idx_shared_contact_owner ON shared_travel_contact(timeline_id,owner_id,player_id,end_day DESC);", parameters, transaction);
        }

        internal static void CreateTemporalKnowledgeNodes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS temporal_knowledge_nodes (
node_id TEXT PRIMARY KEY,
node_type TEXT NOT NULL,
label TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,
updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateTemporalKnowledgeAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS temporal_knowledge_assertions (
assertion_id TEXT PRIMARY KEY,
campaign_id TEXT NOT NULL,
fact_key TEXT NOT NULL,
subject_id TEXT NOT NULL DEFAULT '',
predicate TEXT NOT NULL DEFAULT '',
object_id TEXT NOT NULL DEFAULT '',
claim TEXT NOT NULL DEFAULT '',
perspective_owner_id TEXT NOT NULL DEFAULT '',
assertion_kind TEXT NOT NULL DEFAULT 'belief',
confidence REAL NOT NULL DEFAULT 0.5,
truth_status TEXT NOT NULL DEFAULT 'believed',
valid_from_ts INTEGER NOT NULL,
valid_to_ts INTEGER NULL,
observed_ts INTEGER NOT NULL,
supersedes_assertion_id TEXT NOT NULL DEFAULT '',
source_event_id TEXT NOT NULL DEFAULT '',
source_record_id TEXT NOT NULL DEFAULT '',
known_by_json TEXT NOT NULL DEFAULT '[]',
hidden_from_json TEXT NOT NULL DEFAULT '[]',
visibility TEXT NOT NULL DEFAULT 'private',
payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateIndexTemporalAssertionsCurrent(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_temporal_assertions_current ON temporal_knowledge_assertions(campaign_id,perspective_owner_id,fact_key,valid_to_ts,observed_ts);", parameters, transaction);
        }

        internal static void CreateIndexTemporalAssertionsSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_temporal_assertions_subject ON temporal_knowledge_assertions(subject_id,predicate,object_id,valid_to_ts);", parameters, transaction);
        }

        internal static void CreateTemporalKnowledgeEdges(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS temporal_knowledge_edges (
edge_id TEXT PRIMARY KEY,
from_node_id TEXT NOT NULL,
to_node_id TEXT NOT NULL,
edge_type TEXT NOT NULL,
assertion_id TEXT NOT NULL,
valid_from_ts INTEGER NOT NULL,
valid_to_ts INTEGER NULL,
payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateIndexTemporalEdgesNodes(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_temporal_edges_nodes ON temporal_knowledge_edges(from_node_id,to_node_id,valid_to_ts);", parameters, transaction);
        }

        internal const string CreateSourceProbeSql = "CREATE TABLE source_probe(id text PRIMARY KEY,body text NOT NULL);";

        internal static string CreateSql(object schema)
        {
            return "CREATE FUNCTION source_identity_probe() RETURNS text LANGUAGE sql AS $$ SELECT body FROM "
                        + schema + ".source_probe WHERE id='original_turn'; $$;";
        }

        internal const string CreateSentinelSql = "CREATE TABLE sentinel(value integer); INSERT INTO sentinel VALUES(99);";

        internal static string CreateSql2(object schema)
        {
            return "CREATE VIEW foreign_probe AS SELECT body FROM " + schema + ".source_probe;";
        }

        internal const string DropSql = "DROP VIEW foreign_probe;";
    }
}
