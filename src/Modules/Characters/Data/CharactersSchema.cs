using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    /// <summary>The Characters module's schema statements (tables, indexes and columns); every statement runs through <see cref="ReignDatabase"/>.</summary>
    internal static partial class CharactersSchema
    {
        internal static void CreateCharacterEditorHeads(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_heads (
hero_id TEXT PRIMARY KEY,revision_token TEXT NOT NULL DEFAULT '',latest_revision_id TEXT NOT NULL DEFAULT '',updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateCharacterEditorRevisions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_revisions (
revision_id TEXT PRIMARY KEY,hero_id TEXT NOT NULL,section TEXT NOT NULL DEFAULT 'all',description TEXT NOT NULL DEFAULT '',
before_json TEXT NOT NULL DEFAULT '{}',after_json TEXT NOT NULL DEFAULT '{}',native_changes_json TEXT NOT NULL DEFAULT '{}',
reconciliation_json TEXT NOT NULL DEFAULT '{}',rollback_of TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateIndexEditorRevisionsHero(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_editor_revisions_hero ON character_editor_revisions(hero_id,created_ts DESC);", parameters, transaction);
        }

        internal static void CreateCharacterEditorNativeCommands(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_native_commands (
command_id TEXT PRIMARY KEY,revision_id TEXT NOT NULL,hero_id TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
changes_json TEXT NOT NULL DEFAULT '{}',inverse_json TEXT NOT NULL DEFAULT '{}',dangerous INTEGER NOT NULL DEFAULT 0,
confirmation TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,claimed_ts INTEGER NOT NULL DEFAULT 0,resolved_ts INTEGER NOT NULL DEFAULT 0,
result_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void ApplyNativeEditorOwnershipMigration(ReignDbConnection connection)
        {
            CreateCharacterEditorNativeCommands(connection);
            CreateIndexEditorNativeCommandsStatus(connection);
            RelationshipsSchema.CreateNotableMbtiProfiles(connection);
            RelationshipsSchema.CreateIndexNotableMbtiType(connection);
            RelationshipsSchema.CreateIndexNotableMbtiSync(connection);
            EnsureManualEditorCommandOriginColumns(connection);
            EnsureAutomaticNotableOriginColumns(connection);
            ReignDatabase.EnsureDatabaseColumn(connection, "notable_mbti_profiles",
                "startup_native_session_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static bool NativeEditorOwnershipShapeReady(ReignDbConnection connection,
            ReignDbTransaction transaction = null)
        {
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection)) return false;
            var columns = ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM information_schema.columns
WHERE table_schema=current_schema() AND data_type='text' AND is_nullable='NO' AND column_default=(quote_literal('')||'::text')
  AND ((table_name='character_editor_native_commands' AND column_name IN (
    'manual_origin_timeline_id','manual_origin_lineage_id','manual_origin_json','automatic_origin_json'))
    OR (table_name='notable_mbti_profiles' AND column_name='startup_native_session_id'));",
                null, transaction).SingleOrDefault();
            return ReadInt(columns, "count", 0) == 5;
        }

        internal static void EnsureManualEditorCommandOriginColumns(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_editor_native_commands",
                "manual_origin_timeline_id", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.EnsureDatabaseColumn(connection, "character_editor_native_commands",
                "manual_origin_lineage_id", "TEXT NOT NULL DEFAULT ''");
            ReignDatabase.EnsureDatabaseColumn(connection, "character_editor_native_commands",
                "manual_origin_json", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureAutomaticNotableOriginColumns(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "character_editor_native_commands",
                "automatic_origin_json", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void CreateIndexEditorNativeCommandsStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_editor_native_commands_status ON character_editor_native_commands(status,created_ts);", parameters, transaction);
        }

        internal static void CreateCharacterEditorReconciliation(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_editor_reconciliation (
job_id TEXT PRIMARY KEY,revision_id TEXT NOT NULL,hero_id TEXT NOT NULL,section TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
summary TEXT NOT NULL DEFAULT '',result_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);", parameters, transaction);
        }

        internal static void CreateDynamicCharacteristics(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS dynamic_characteristics (
characteristic_id TEXT PRIMARY KEY,
owner_id TEXT NOT NULL,
category TEXT NOT NULL DEFAULT 'personal_history',
topic_key TEXT NOT NULL DEFAULT '',
text TEXT NOT NULL,
normalized_key TEXT NOT NULL,
status TEXT NOT NULL DEFAULT 'active',
confidence REAL NOT NULL DEFAULT 0.65,
importance REAL NOT NULL DEFAULT 0.5,
provenance TEXT NOT NULL DEFAULT 'npc_generated_personal_history',
compatibility_status TEXT NOT NULL DEFAULT 'compatible_soft_canon',
rejection_reason TEXT NOT NULL DEFAULT '',
source_event_id TEXT NOT NULL DEFAULT '',
source_session_id TEXT NOT NULL DEFAULT '',
source_exchange_id TEXT NOT NULL DEFAULT '',
source_turn_ids_json TEXT NOT NULL DEFAULT '[]',
source_mode TEXT NOT NULL DEFAULT '',
location_id TEXT NOT NULL DEFAULT '',
world_day REAL NOT NULL DEFAULT 0,
known_by_json TEXT NOT NULL DEFAULT '[]',
first_ts INTEGER NOT NULL,
last_ts INTEGER NOT NULL,
repeat_count INTEGER NOT NULL DEFAULT 1,
payload_json TEXT NOT NULL DEFAULT '{}');", parameters, transaction);
        }

        internal static void CreateIndexDynamicCharacteristicOwnerKey(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE UNIQUE INDEX IF NOT EXISTS idx_dynamic_characteristic_owner_key ON dynamic_characteristics(owner_id,normalized_key);", parameters, transaction);
        }

        internal static void CreateIndexDynamicCharacteristicOwnerStatus(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_dynamic_characteristic_owner_status ON dynamic_characteristics(owner_id,status,last_ts DESC);", parameters, transaction);
        }

        internal static void CreateIndexDynamicCharacteristicTopic(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_dynamic_characteristic_topic ON dynamic_characteristics(owner_id,topic_key,status);", parameters, transaction);
        }

        internal static void CreateAcquaintances(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS acquaintances (
observer_id TEXT NOT NULL,
subject_id TEXT NOT NULL,
identity_state TEXT NOT NULL DEFAULT 'encountered_unknown',
canonical_name TEXT NOT NULL DEFAULT '',
claimed_name TEXT NOT NULL DEFAULT '',
aliases_json TEXT NOT NULL DEFAULT '[]',
verification_source TEXT NOT NULL DEFAULT '',
source_entity_id TEXT NOT NULL DEFAULT '',
confidence REAL NOT NULL DEFAULT 0,
first_met_day REAL NOT NULL DEFAULT 0,
last_met_day REAL NOT NULL DEFAULT 0,
encounter_count INTEGER NOT NULL DEFAULT 0,
last_encounter_id TEXT NOT NULL DEFAULT '',
recognition_attempts INTEGER NOT NULL DEFAULT 0,
last_recognition_result TEXT NOT NULL DEFAULT '',
last_recognition_encounter_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',
updated_ts INTEGER NOT NULL DEFAULT 0,
PRIMARY KEY(observer_id,subject_id));", parameters, transaction);
        }

        internal static void CreateIndexAcquaintancesSubject(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_acquaintances_subject ON acquaintances(subject_id,identity_state);", parameters, transaction);
        }

        internal static void CreateIndexAcquaintancesObserver(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_acquaintances_observer ON acquaintances(observer_id,identity_state);", parameters, transaction);
        }

        internal static void CreateIdentityEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS identity_evidence (
evidence_id TEXT PRIMARY KEY,
observer_id TEXT NOT NULL,
subject_id TEXT NOT NULL,
evidence_type TEXT NOT NULL,
claimed_name TEXT NOT NULL DEFAULT '',
canonical_name TEXT NOT NULL DEFAULT '',
source TEXT NOT NULL DEFAULT '',
source_entity_id TEXT NOT NULL DEFAULT '',
encounter_id TEXT NOT NULL DEFAULT '',
confidence REAL NOT NULL DEFAULT 0,
world_day REAL NOT NULL DEFAULT 0,
payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL DEFAULT 0);", parameters, transaction);
        }

        internal static void CreateIndexIdentityEvidencePair(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_identity_evidence_pair ON identity_evidence(observer_id,subject_id,created_ts DESC);", parameters, transaction);
        }

        internal static void CreateIdentityRoster(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS identity_roster (
hero_id TEXT PRIMARY KEY,canonical_name TEXT NOT NULL DEFAULT '',clan_id TEXT NOT NULL DEFAULT '',
kingdom_id TEXT NOT NULL DEFAULT '',is_lord INTEGER NOT NULL DEFAULT 0,is_ruler INTEGER NOT NULL DEFAULT 0,
family_ids_json TEXT NOT NULL DEFAULT '[]',updated_ts INTEGER NOT NULL DEFAULT 0);", parameters, transaction);
        }

        internal static void EnsureColumnIdentityRosterIsAlive(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_alive", "INTEGER NOT NULL DEFAULT 1");
        }

        internal static void EnsureColumnIdentityRosterIsAdult(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_adult", "INTEGER NOT NULL DEFAULT 1");
        }

        internal static void EnsureColumnIdentityRosterIsPlayer(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_player", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterSex(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "sex", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnIdentityRosterClanTier(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "clan_tier", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterCurrentCharm(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "current_charm", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterOccupation(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "occupation", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnIdentityRosterIsNotable(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_notable", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterIsWanderer(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_wanderer", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterIsClanLeader(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_clan_leader", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterIsMercenaryClan(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "is_mercenary_clan", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnIdentityRosterGovernorSettlementId(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "governor_settlement_id", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnIdentityRosterGovernorSettlementName(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "identity_roster", "governor_settlement_name", "TEXT NOT NULL DEFAULT ''");
        }

        internal static void EnsureColumnAcquaintancesLastRecognitionDay(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "acquaintances", "last_recognition_day", "REAL NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnAcquaintancesLastRecognitionProbability(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "acquaintances", "last_recognition_probability", "REAL NOT NULL DEFAULT 0");
        }

        internal static void EnsureColumnAcquaintancesLastRecognitionRoll(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "acquaintances", "last_recognition_roll", "REAL NOT NULL DEFAULT -1");
        }

        internal static void EnsureColumnAcquaintancesRecognitionRuleVersion(ReignDbConnection connection)
        {
            ReignDatabase.EnsureDatabaseColumn(connection, "acquaintances", "recognition_rule_version", "INTEGER NOT NULL DEFAULT 0");
        }

        internal static void CreateIndexIdentityRosterClan(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_identity_roster_clan ON identity_roster(clan_id,hero_id);", parameters, transaction);
        }

        internal static void CreateIndexIdentityRosterKingdom(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_identity_roster_kingdom ON identity_roster(kingdom_id,is_lord,hero_id);", parameters, transaction);
        }

        internal static void CreateCharacterNpcLivesState(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS character_npc_lives_state(
timeline_id TEXT PRIMARY KEY, value_json TEXT NOT NULL);", parameters, transaction);
        }
    }
}
