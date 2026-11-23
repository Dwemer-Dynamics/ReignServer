using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    /// <summary>The Court module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class CourtSchema
    {
        internal static void CreateCourtLifeTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_life_turns(
turn_id TEXT PRIMARY KEY,timeline_id TEXT NOT NULL,matter_id TEXT NOT NULL,actor_id TEXT NOT NULL,
phase TEXT NOT NULL,player_text TEXT NOT NULL,reply_text TEXT NOT NULL,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateCourtSessions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_sessions (
session_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,authority TEXT NOT NULL,host_settlement_id TEXT NOT NULL,
state TEXT NOT NULL DEFAULT 'inactive',opened_day REAL NOT NULL DEFAULT 0,closed_day REAL NOT NULL DEFAULT -1,last_agenda_day INTEGER NOT NULL DEFAULT -1,
active_matter_id TEXT NOT NULL DEFAULT '',time_mode INTEGER NOT NULL DEFAULT 0,revision INTEGER NOT NULL DEFAULT 0,interruption_reason TEXT NOT NULL DEFAULT '',
native_snapshot_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexCourtSessionsScope(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_sessions_scope ON court_sessions(campaign_id,timeline_id,state,updated_ts DESC);", parameters, transaction);
        }

        internal static void EnsureColumnCourtSessionsScope(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "court_sessions", "scope", "TEXT NOT NULL DEFAULT 'local'");
        }

        internal static void EnsureColumnCourtSessionsCapitalSettlementId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "court_sessions", "capital_settlement_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateCourtMatters(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_matters (
matter_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,session_id TEXT NOT NULL DEFAULT '',source_key TEXT NOT NULL,
kind TEXT NOT NULL,state TEXT NOT NULL DEFAULT 'queued',title TEXT NOT NULL DEFAULT '',summary TEXT NOT NULL DEFAULT '',confidentiality TEXT NOT NULL DEFAULT 'public',
participants_json TEXT NOT NULL DEFAULT '[]',witnesses_json TEXT NOT NULL DEFAULT '[]',known_by_json TEXT NOT NULL DEFAULT '[]',hidden_from_json TEXT NOT NULL DEFAULT '[]',
settlement_id TEXT NOT NULL DEFAULT '',kingdom_id TEXT NOT NULL DEFAULT '',created_day REAL NOT NULL DEFAULT 0,due_day REAL NOT NULL DEFAULT -1,
scheduled_day REAL NOT NULL DEFAULT -1,resolved_day REAL NOT NULL DEFAULT -1,priority INTEGER NOT NULL DEFAULT 0,is_critical INTEGER NOT NULL DEFAULT 0,
is_major INTEGER NOT NULL DEFAULT 0,is_ambient INTEGER NOT NULL DEFAULT 0,options_json TEXT NOT NULL DEFAULT '[]',default_outcome_json TEXT NOT NULL DEFAULT '{}',
selected_option_id TEXT NOT NULL DEFAULT '',terms_hash TEXT NOT NULL DEFAULT '',resolution_json TEXT NOT NULL DEFAULT '{}',invalid_reason TEXT NOT NULL DEFAULT '',
requires_server INTEGER NOT NULL DEFAULT 1,revision INTEGER NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexCourtMattersScope(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_matters_scope ON court_matters(campaign_id,timeline_id,state,due_day,priority DESC);", parameters, transaction);
        }

        internal static void CreateIndexCourtMattersSource(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_matters_source ON court_matters(campaign_id,timeline_id,source_key,state);", parameters, transaction);
        }

        internal static void CreateCourtAgenda(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_agenda (
agenda_item_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,session_id TEXT NOT NULL,matter_id TEXT NOT NULL,
agenda_day INTEGER NOT NULL,slot INTEGER NOT NULL,is_urgent INTEGER NOT NULL DEFAULT 0,status TEXT NOT NULL DEFAULT 'scheduled',created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,session_id,matter_id,agenda_day));", parameters, transaction);
        }

        internal static void CreateIndexCourtAgendaDay(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_agenda_day ON court_agenda(campaign_id,timeline_id,session_id,agenda_day,slot);", parameters, transaction);
        }

        internal static void CreateCourtOfficeHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_office_history (
assignment_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,office TEXT NOT NULL,hero_id TEXT NOT NULL,
status TEXT NOT NULL DEFAULT 'active',assigned_day REAL NOT NULL,dismissed_day REAL NOT NULL DEFAULT -1,dismissal_reason TEXT NOT NULL DEFAULT '',
revision INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexCourtOfficeActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_office_active ON court_office_history(campaign_id,timeline_id,office,status);", parameters, transaction);
        }

        internal static void CreateCourtRegentHistory(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_regent_history (
assignment_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'active',
assigned_day REAL NOT NULL,dismissed_day REAL NOT NULL DEFAULT -1,dismissal_reason TEXT NOT NULL DEFAULT '',revision INTEGER NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexCourtRegentActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_regent_active ON court_regent_history(campaign_id,timeline_id,status);", parameters, transaction);
        }

        internal static void CreateAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_postings (
posting_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
mission_type TEXT NOT NULL DEFAULT 'public_information',mission_terms_json TEXT NOT NULL DEFAULT '{}',status TEXT NOT NULL DEFAULT 'traveling',
assigned_day REAL NOT NULL,arrival_day REAL NOT NULL,last_report_day REAL NOT NULL DEFAULT -1,recalled_day REAL NOT NULL DEFAULT -1,
revision INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexAmbassadorActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_ambassador_active ON ambassador_postings(campaign_id,timeline_id,status,assigned_day DESC);", parameters, transaction);
        }

        internal static void CreateIntelligenceOperations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS intelligence_operations (
operation_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,operation_type TEXT NOT NULL,target_type TEXT NOT NULL,target_id TEXT NOT NULL,
status TEXT NOT NULL DEFAULT 'planned',gold_cost INTEGER NOT NULL DEFAULT 0,influence_cost REAL NOT NULL DEFAULT 0,started_day REAL NOT NULL,due_day REAL NOT NULL,
risk REAL NOT NULL DEFAULT 0,confidence REAL NOT NULL DEFAULT 0,source_chain_json TEXT NOT NULL DEFAULT '[]',result_json TEXT NOT NULL DEFAULT '{}',
is_exposed INTEGER NOT NULL DEFAULT 0,revision INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexIntelligenceActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_intelligence_active ON intelligence_operations(campaign_id,timeline_id,status,due_day);", parameters, transaction);
        }

        internal static void CreateCourtPlots(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_plots (
plot_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,plot_type TEXT NOT NULL,director_id TEXT NOT NULL DEFAULT '',target_id TEXT NOT NULL DEFAULT '',
participants_json TEXT NOT NULL DEFAULT '[]',known_by_json TEXT NOT NULL DEFAULT '[]',hidden_from_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL DEFAULT 'active',
created_day REAL NOT NULL,due_day REAL NOT NULL DEFAULT -1,progress REAL NOT NULL DEFAULT 0,terms_hash TEXT NOT NULL DEFAULT '',secret_knowledge_id TEXT NOT NULL DEFAULT '',
revision INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexCourtPlotsScope(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_court_plots_scope ON court_plots(campaign_id,timeline_id,status,due_day);", parameters, transaction);
        }

        internal static void CreateCourtCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_commands (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,command_id TEXT NOT NULL,command_type TEXT NOT NULL,response_json TEXT NOT NULL,created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,command_id));", parameters, transaction);
        }

        internal static void CreateCourtAudit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_audit (
audit_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,command_id TEXT NOT NULL DEFAULT '',record_type TEXT NOT NULL,record_id TEXT NOT NULL DEFAULT '',
action TEXT NOT NULL,status TEXT NOT NULL,world_day REAL NOT NULL DEFAULT 0,receipt_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void EnsureColumnObligationsObligationType(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "obligation_type", "TEXT NOT NULL DEFAULT 'promise'");
        }

        internal static void EnsureColumnObligationsDueDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "due_day", "REAL NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnObligationsTermsHash(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "terms_hash", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnObligationsBreachRuleJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "breach_rule_json", "TEXT NOT NULL DEFAULT '{}'");
        }

        internal static void EnsureColumnObligationsResolutionJson(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "resolution_json", "TEXT NOT NULL DEFAULT '{}'");
        }

        internal static void EnsureColumnObligationsTimelineId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "timeline_id", "TEXT NOT NULL DEFAULT 'main'");
        }

        internal static void EnsureColumnObligationsRevision(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "obligations", "revision", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void CreateChildhoodExperiences(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS childhood_experiences (
experience_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,
session_id TEXT NOT NULL,world_day REAL NOT NULL DEFAULT 0,experienced_age REAL NOT NULL DEFAULT 0,
summary TEXT NOT NULL DEFAULT '',participants_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL DEFAULT 'active_child',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexChildhoodExperiencesHero(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_childhood_experiences_hero ON childhood_experiences(campaign_id,timeline_id,hero_id,world_day DESC);", parameters, transaction);
        }

        internal static void CreateChildhoodMaturityReceipts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS childhood_maturity_receipts (
receipt_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,
retained_count INTEGER NOT NULL DEFAULT 0,archived_count INTEGER NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,hero_id));", parameters, transaction);
        }

        internal static void CreateForeignAmbassadorPostings(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS foreign_ambassador_postings (
posting_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,
origin_kingdom_name TEXT NOT NULL DEFAULT '',origin_ruler_id TEXT NOT NULL,origin_ruler_name TEXT NOT NULL DEFAULT '',host_kingdom_id TEXT NOT NULL,
capital_settlement_id TEXT NOT NULL DEFAULT '',shelter_settlement_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT 'traveling',
assigned_day REAL NOT NULL,arrival_day REAL NOT NULL,ended_day REAL NOT NULL DEFAULT -1,end_reason TEXT NOT NULL DEFAULT '',charm INTEGER NOT NULL,
ruler_trust INTEGER NOT NULL DEFAULT 0,authority_revision INTEGER NOT NULL DEFAULT 1,authority_charter_json TEXT NOT NULL DEFAULT '{}',
revision INTEGER NOT NULL DEFAULT 1,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexForeignAmbassadorActive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_foreign_ambassador_active ON foreign_ambassador_postings(campaign_id,timeline_id,origin_kingdom_id,status,assigned_day DESC);", parameters, transaction);
        }

        internal static void CreateAmbassadorOfficialArchive(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_official_archive (
archive_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,
ruler_id_at_turn TEXT NOT NULL,envoy_id TEXT NOT NULL,conversation_session_id TEXT NOT NULL,exchange_id TEXT NOT NULL DEFAULT '',turn_id TEXT NOT NULL,
speaker_id TEXT NOT NULL,speaker_role TEXT NOT NULL,text TEXT NOT NULL,world_day REAL NOT NULL,created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,posting_id,turn_id));", parameters, transaction);
        }

        internal static void CreateIndexAmbassadorArchiveKingdom(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_ambassador_archive_kingdom ON ambassador_official_archive(campaign_id,timeline_id,origin_kingdom_id,world_day,created_ts);", parameters, transaction);
        }

        internal static void CreateAmbassadorReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_referrals (
referral_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,action_id TEXT NOT NULL,
exact_terms_json TEXT NOT NULL,terms_hash TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',outcome TEXT NOT NULL DEFAULT 'refer',
created_day REAL NOT NULL,due_day REAL NOT NULL,resolved_day REAL NOT NULL DEFAULT -1,counter_terms_json TEXT NOT NULL DEFAULT '{}',
authoritative_receipt_json TEXT NOT NULL DEFAULT '{}',revision INTEGER NOT NULL DEFAULT 1,created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexAmbassadorReferralsDue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_ambassador_referrals_due ON ambassador_referrals(campaign_id,timeline_id,status,due_day);", parameters, transaction);
        }

        internal static void CreateAmbassadorDecisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_decisions (
decision_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,conversation_session_id TEXT NOT NULL,
exchange_id TEXT NOT NULL DEFAULT '',requested_action TEXT NOT NULL DEFAULT '',authority_result TEXT NOT NULL,outcome TEXT NOT NULL,
terms_hash TEXT NOT NULL DEFAULT '',referral_id TEXT NOT NULL DEFAULT '',referral_state TEXT NOT NULL DEFAULT '',
authoritative_receipt_json TEXT NOT NULL DEFAULT '{}',created_day REAL NOT NULL,payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateAmbassadorPressureDaily(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_pressure_daily (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,host_kingdom_id TEXT NOT NULL,day_index INTEGER NOT NULL,
net_delta INTEGER NOT NULL DEFAULT 0,updated_ts INTEGER NOT NULL,PRIMARY KEY(campaign_id,timeline_id,origin_kingdom_id,host_kingdom_id,day_index));", parameters, transaction);
        }

        internal static void CreateAmbassadorPressureAudit(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ambassador_pressure_audit (
audit_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,posting_id TEXT NOT NULL,origin_kingdom_id TEXT NOT NULL,
host_kingdom_id TEXT NOT NULL,conversation_session_id TEXT NOT NULL,classification TEXT NOT NULL,requested_delta INTEGER NOT NULL,
applied_delta INTEGER NOT NULL,before_value INTEGER NOT NULL,after_value INTEGER NOT NULL,pressure_channel TEXT NOT NULL,
evidence_turn_ids_json TEXT NOT NULL DEFAULT '[]',world_day REAL NOT NULL,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateInternationalCourtQueue(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS international_court_queue (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,matter_id TEXT NOT NULL,kind TEXT NOT NULL,
severity_rank INTEGER NOT NULL,created_day REAL NOT NULL,payload_json TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
PRIMARY KEY(campaign_id,timeline_id,matter_id));", parameters, transaction);
        }

        internal static void CreateInternationalCourtReferrals(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS international_court_referrals (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,matter_id TEXT NOT NULL,referral_id TEXT NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,referral_id));", parameters, transaction);
        }

        internal static void CreateCourtFamilyAttention(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_family_attention (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,player_id TEXT NOT NULL,hero_id TEXT NOT NULL,
dismissals INTEGER NOT NULL DEFAULT 0,neglected INTEGER NOT NULL DEFAULT 0,decay_finished INTEGER NOT NULL DEFAULT 0,
last_decay_day INTEGER NOT NULL DEFAULT -1,directional_relation INTEGER NOT NULL DEFAULT 0,
adult_migrated INTEGER NOT NULL DEFAULT 0,profile_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(campaign_id,timeline_id,player_id,hero_id));", parameters, transaction);
        }

        internal static void EnsureColumnCourtFamilyAttentionChildRelationOwned(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "court_family_attention", "child_relation_owned", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void CreateCourtFamilyVisits(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_family_visits (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,visit_id TEXT NOT NULL,player_id TEXT NOT NULL,hero_id TEXT NOT NULL,
created_day REAL NOT NULL,deadline REAL NOT NULL,status TEXT NOT NULL DEFAULT 'pending',player_turns INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(campaign_id,timeline_id,visit_id));", parameters, transaction);
        }

        internal static void CreateCourtFamilyVisitTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_family_visit_turns (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,visit_id TEXT NOT NULL,turn_id TEXT NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,visit_id,turn_id));", parameters, transaction);
        }

        internal static void CreateCourtFamilyReconciliations(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS court_family_reconciliations (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,hero_id TEXT NOT NULL,exchange_id TEXT NOT NULL,
player_quote TEXT NOT NULL,npc_quote TEXT NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,hero_id,exchange_id));", parameters, transaction);
        }

        internal static void CreateRoyalCouncilTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS royal_council_turns (
turn_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,session_id TEXT NOT NULL,
speaker_role TEXT NOT NULL,speaker_hero_id TEXT NOT NULL DEFAULT '',speaker_name TEXT NOT NULL DEFAULT '',
player_text TEXT NOT NULL DEFAULT '',reply_text TEXT NOT NULL DEFAULT '',heard_by_json TEXT NOT NULL DEFAULT '[]',
domain_packet_json TEXT NOT NULL DEFAULT '{}',is_opening INTEGER NOT NULL DEFAULT 0,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexRoyalCouncilSession(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_royal_council_session ON royal_council_turns(campaign_id,timeline_id,session_id,created_ts);", parameters, transaction);
        }

        internal static void CreateRulerPetitionTurns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS ruler_petition_turns (
turn_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,transcript_id TEXT NOT NULL,
petition_id TEXT NOT NULL,phase TEXT NOT NULL,petitioner_hero_id TEXT NOT NULL DEFAULT '',
petitioner_name TEXT NOT NULL DEFAULT '',petition_kind TEXT NOT NULL,severity TEXT NOT NULL,
outcome TEXT NOT NULL DEFAULT '',player_text TEXT NOT NULL DEFAULT '',reply_text TEXT NOT NULL,created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void EnsureColumnRulerPetitionTurnsPlayerText(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "ruler_petition_turns", "player_text", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateIndexRulerPetitionTranscript(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_ruler_petition_transcript ON ruler_petition_turns(campaign_id,timeline_id,transcript_id,created_ts);", parameters, transaction);
        }
    }
}
