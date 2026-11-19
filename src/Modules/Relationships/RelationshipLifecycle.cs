using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private const int RelationshipLifecycleVersion = 6;
        private const int PostgreSqlRelationshipLifecycleSchemaRevision = 8;
        private const string PostgreSqlRelationshipLifecycleShapeMarker =
            "postgresql_relationship_lifecycle_schema_shape";
        private static readonly string[] RelationshipLifecycleRomanceColumns =
        {
            "romance_stage", "romance_stage_started_day",
            "romance_last_colocated_day", "romance_positive_bonus",
            "flirt_episode", "flirt_attempted", "flirt_passed",
            "flirt_trait_a", "flirt_trait_b", "flirt_roll_a",
            "flirt_roll_b", "affair_episode", "affair_attempted",
            "affair_passed", "judgment_trait_a", "judgment_trait_b",
            "judgment_roll_a", "judgment_roll_b",
            "organic_marriage_colocated_days"
        };
        private const int RomanceFlirtationThreshold = 30;
        private const int RomanceFlirtationResetThreshold = 25;
        private const int RomanceGrowingThreshold = 50;
        private const int RomanceGrowingResetThreshold = 45;
        private const int RomanceSeriousThreshold = 70;
        private const int RomanceSeriousResetThreshold = 60;
        internal const int RomanceFlirtationPositiveChanceBonus = 10;
        internal const int RomanceGrowingPositiveChanceBonus = 20;
        private const int OrganicMarriageEvaluationCadenceDays = 1;
        private const int OrganicMarriageChancePercent = 10;
        private const int LoverConceptionChancePercent = 5;
        private const int PregnancyCommitmentHonorBonus = 10;
        private const int PregnancyCommitmentMaximumChancePercent = 90;
        private const int PregnancyCommitmentMarriageAffinityBonus = 30;
        private const int PregnancyCommitmentBreakupAffinityPenalty = -70;
        private const int AffairMarriageMaximumChancePercent = 30;

        [ThreadStatic]
        private static RelationshipLifecycleWriteContext CurrentRelationshipLifecycleWriteContext;

        // Reuse command normalization and parameter objects for one connection/day.
        // Every write still executes immediately: later pairs, native actions and
        // rollback observe exactly the same transaction order as ExecuteSql.
        private sealed class RelationshipLifecycleWriteContext : IDisposable
        {
            private readonly ReignDbConnection connection;
            private readonly RelationshipLifecycleWriteContext previous;
            private ReignDbCommand command;
            private string commandSql;
            public int CreatedCommands { get; private set; }
            public int Writes { get; private set; }

            public RelationshipLifecycleWriteContext(ReignDbConnection connection)
            {
                this.connection = connection;
                previous = CurrentRelationshipLifecycleWriteContext;
                CurrentRelationshipLifecycleWriteContext = this;
            }

            public bool Matches(ReignDbConnection candidate)
            {
                return ReferenceEquals(connection, candidate);
            }

            public void Execute(string sql, Dictionary<string, object> values)
            {
                for (int attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (command == null || commandSql != sql)
                        {
                            command?.Dispose();
                            command = CreatePreparedRelationshipCommand(connection,
                                sql, values.Keys.ToArray());
                            commandSql = sql;
                            CreatedCommands++;
                        }
                        ExecutePreparedRelationshipCommand(command, values);
                        Writes++;
                        break;
                    }
                    catch (Exception ex) when (IsStalePostgreSqlPreparedPlan(ex)
                        && attempt < 2)
                    {
                        command?.Dispose();
                        command = null;
                        ResetPostgreSqlPreparedPlans(connection);
                    }
                }
                InvalidateRelationshipPairCacheForMutation(connection, sql, values);
                string campaignId = ReignPostgreSqlStorage.CampaignIdForConnection(connection);
                if (!string.IsNullOrWhiteSpace(campaignId))
                {
                    try { PersistenceService.MarkSaveSyncActiveStateDirty(campaignId); }
                    catch { }
                }
            }

            public void Dispose()
            {
                try { command?.Dispose(); }
                finally { CurrentRelationshipLifecycleWriteContext = previous; }
            }
        }

        private static void ExecuteRelationshipLifecycleWrite(ReignDbConnection connection,
            string sql, Dictionary<string, object> values)
        {
            RelationshipLifecycleWriteContext context = CurrentRelationshipLifecycleWriteContext;
            if (context != null && context.Matches(connection)
                && ReignPostgreSqlDialect.IsPostgreSql(connection))
                context.Execute(sql, values);
            else
                ReignDatabase.ExecuteSql(connection, sql, values);
        }

        internal static void EnsureRelationshipLifecycleSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_relationship_lifecycle_schema_revision";
            if (IsRelationshipLifecycleSchemaReady(connection, marker))
                return;
            EnsureRelationshipLifecycleSchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ReignDatabase.ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_lifecycle_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlRelationshipLifecycleSchemaRevision
                                .ToString()
                    });
                ReignDatabase.MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlRelationshipLifecycleSchemaRevision);
                MarkRelationshipLifecycleSchemaShapeReady(connection);
            }
        }

        private static bool IsRelationshipLifecycleSchemaReady(
            ReignDbConnection connection, string marker)
        {
            if (!ReignDatabase.IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlRelationshipLifecycleSchemaRevision))
                return false;
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            string shapeKey = campaignId + "|"
                + PostgreSqlRelationshipLifecycleShapeMarker + "|"
                + PostgreSqlRelationshipLifecycleSchemaRevision.ToString(
                    CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(campaignId)
                && Program.PostgreSqlComponentSchemaFastPaths.ContainsKey(shapeKey))
                return true;
            HashSet<string> columns = new HashSet<string>(
                ReignDatabase.QuerySql(connection,
                    "PRAGMA table_info(relationship_pair_lifecycle);")
                    .Select(row => ReadString(row, "name", "")),
                StringComparer.OrdinalIgnoreCase);
            bool ready = RelationshipLifecycleRomanceColumns.All(
                columns.Contains);
            if (ready && !string.IsNullOrWhiteSpace(campaignId))
                Program.PostgreSqlComponentSchemaFastPaths[shapeKey] = 0;
            return ready;
        }

        private static void MarkRelationshipLifecycleSchemaShapeReady(
            ReignDbConnection connection)
        {
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrWhiteSpace(campaignId))
                return;
            Program.PostgreSqlComponentSchemaFastPaths[campaignId + "|"
                + PostgreSqlRelationshipLifecycleShapeMarker + "|"
                + PostgreSqlRelationshipLifecycleSchemaRevision.ToString(
                    CultureInfo.InvariantCulture)] = 0;
        }

        private static void EnsureRelationshipLifecycleSchemaCore(
            ReignDbConnection connection)
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
updated_ts INTEGER NOT NULL);" );
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "last_marriage_evaluation_day",
                "REAL NOT NULL DEFAULT -1000");
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "organic_marriage_colocated_days",
                "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "marriage_blocked", "INTEGER NOT NULL DEFAULT 0");
            ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "marriage_block_reason", "TEXT NOT NULL DEFAULT ''");
            foreach (KeyValuePair<string, string> column in new Dictionary<string, string>
            {
                ["romance_stage"] = "TEXT NOT NULL DEFAULT ''",
                ["romance_stage_started_day"] = "REAL NOT NULL DEFAULT 0",
                ["romance_last_colocated_day"] = "REAL NOT NULL DEFAULT 0",
                ["romance_positive_bonus"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_episode"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_attempted"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_passed"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_trait_a"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_trait_b"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_roll_a"] = "INTEGER NOT NULL DEFAULT 0",
                ["flirt_roll_b"] = "INTEGER NOT NULL DEFAULT 0",
                ["affair_episode"] = "INTEGER NOT NULL DEFAULT 0",
                ["affair_attempted"] = "INTEGER NOT NULL DEFAULT 0",
                ["affair_passed"] = "INTEGER NOT NULL DEFAULT 0",
                ["judgment_trait_a"] = "INTEGER NOT NULL DEFAULT 0",
                ["judgment_trait_b"] = "INTEGER NOT NULL DEFAULT 0",
                ["judgment_roll_a"] = "INTEGER NOT NULL DEFAULT 0",
                ["judgment_roll_b"] = "INTEGER NOT NULL DEFAULT 0"
            })
                ReignDatabase.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                    column.Key, column.Value);
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_lifecycle_heroes ON relationship_pair_lifecycle(hero_a_id,hero_b_id);");
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_marriage_reservations (
hero_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,director_action_id TEXT NOT NULL,
created_day REAL NOT NULL,created_ts INTEGER NOT NULL);" );
            ReignDatabase.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_marriage_reservation_action
ON relationship_marriage_reservations(director_action_id);" );
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_conception_commitments (
conception_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
honor_a INTEGER NOT NULL,honor_b INTEGER NOT NULL,chance_a INTEGER NOT NULL,chance_b INTEGER NOT NULL,
roll_a INTEGER NOT NULL,roll_b INTEGER NOT NULL,passed_a INTEGER NOT NULL,passed_b INTEGER NOT NULL,
status TEXT NOT NULL,marriage_action_id TEXT NOT NULL DEFAULT '',affinity_delta INTEGER NOT NULL DEFAULT 0,
world_day REAL NOT NULL,updated_ts INTEGER NOT NULL);" );
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_affair_commitments (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,spouse_a_id TEXT NOT NULL DEFAULT '',
spouse_b_id TEXT NOT NULL DEFAULT '',chance_a INTEGER NOT NULL DEFAULT 0,chance_b INTEGER NOT NULL DEFAULT 0,
roll_a INTEGER NOT NULL DEFAULT 0,roll_b INTEGER NOT NULL DEFAULT 0,passed_a INTEGER NOT NULL DEFAULT 0,
passed_b INTEGER NOT NULL DEFAULT 0,divorce_action_a_id TEXT NOT NULL DEFAULT '',divorce_action_b_id TEXT NOT NULL DEFAULT '',
divorce_a_completed INTEGER NOT NULL DEFAULT 0,divorce_b_completed INTEGER NOT NULL DEFAULT 0,
marriage_action_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL,world_day REAL NOT NULL,updated_ts INTEGER NOT NULL);" );
            if (WorldSimulationService.TableExists(connection, "relationship_director_actions"))
            {
                ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE NOT EXISTS (SELECT 1 FROM relationship_director_actions a
WHERE a.director_action_id=relationship_marriage_reservations.director_action_id
AND a.action_type='marriage' AND a.status IN ('pending','claimed'));");
                ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_marriage_reservations(
hero_id,pair_key,director_action_id,created_day,created_ts)
SELECT actor_id,
CASE WHEN lower(actor_id)<=lower(target_id) THEN actor_id||'|'||target_id
ELSE target_id||'|'||actor_id END,
director_action_id,world_day,created_ts
FROM relationship_director_actions
WHERE action_type='marriage' AND status IN ('pending','claimed')
UNION ALL
SELECT target_id,
CASE WHEN lower(actor_id)<=lower(target_id) THEN actor_id||'|'||target_id
ELSE target_id||'|'||actor_id END,
director_action_id,world_day,created_ts
FROM relationship_director_actions
WHERE action_type='marriage' AND status IN ('pending','claimed');");
            }
            ReignDatabase.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_incidents (
incident_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,kind TEXT NOT NULL,
hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,world_day REAL NOT NULL,
summary TEXT NOT NULL DEFAULT '',rumor_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);" );
            ReignDatabase.ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_incidents_pair ON relationship_incidents(pair_key,world_day DESC);");
            EnsureRelationshipFlingSchemaCore(connection);
        }

        private static void CompactDormantRelationshipLifecycleRows(ReignDbConnection connection)
        {
            if (ReadString(ReignDatabase.QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='relationship_lifecycle_sparse_v1' LIMIT 1;")
                .FirstOrDefault(), "value", "") == "1") return;
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM relationship_pair_lifecycle
WHERE lover_active=0 AND lover_exception_attempted=0 AND lover_rumor_id=''
  AND affair_active=0 AND affair_rumor_id=''
  AND romance_stage='' AND flirt_attempted=0 AND affair_attempted=0
  AND estranged_a_to_b=0 AND estranged_b_to_a=0 AND estranged_rumor_id=''
  AND married=0 AND marriage_action_id='' AND marriage_blocked=0
  AND divorced=0 AND divorce_action_id='' AND divorce_rumor_id=''
  AND conception_action_id='';" );
            ReignDatabase.ExecuteSql(connection,
                "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_lifecycle_sparse_v1','1');");
        }

        private static int RomancePositiveChanceBonus(
            Dictionary<string, object> lifecycleRow)
        {
            if (lifecycleRow == null) return 0;
            return RomancePositiveChanceBonus(ReadString(lifecycleRow,
                    "romance_stage", ""),
                ReadInt(lifecycleRow, "lover_active", 0) == 1);
        }

        private static int RomancePositiveChanceBonus(string stage,
            bool lovers)
        {
            if (lovers) return RomanceGrowingPositiveChanceBonus;
            string normalized = (stage ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "growing_attraction"
                || normalized == "serious_attraction")
                return RomanceGrowingPositiveChanceBonus;
            return normalized == "flirtation"
                ? RomanceFlirtationPositiveChanceBonus : 0;
        }

        private static int RomancePositiveChanceBonusForAffinity(
            Dictionary<string, object> lifecycleRow, int affinityAB,
            int affinityBA)
        {
            if (lifecycleRow == null) return 0;
            int mutual = Math.Min(affinityAB, affinityBA);
            if (ReadInt(lifecycleRow, "lover_active", 0) == 1)
                return mutual >= RomanceFlirtationThreshold
                    ? RomanceGrowingPositiveChanceBonus : 0;
            string stage = ReadString(lifecycleRow, "romance_stage", "")
                .Trim().ToLowerInvariant();
            if (stage == "serious_attraction")
                return mutual >= RomanceSeriousResetThreshold
                    ? RomanceGrowingPositiveChanceBonus : 0;
            if (stage == "growing_attraction")
                return mutual >= RomanceGrowingResetThreshold
                    ? RomanceGrowingPositiveChanceBonus : 0;
            if (stage == "flirtation")
                return mutual >= RomanceFlirtationResetThreshold
                    ? RomanceFlirtationPositiveChanceBonus : 0;
            return 0;
        }

        private static bool IsRomanceContinuationLifecycle(
            Dictionary<string, object> row)
        {
            if (row == null) return false;
            return ReadInt(row, "lover_active", 0) == 1
                || ReadInt(row, "affair_active", 0) == 1
                || !string.IsNullOrWhiteSpace(ReadString(row,
                    "romance_stage", ""));
        }

        private static int RomanceTraitPercentage(string campaignId,
            Dictionary<string, object> hero, string heroId, string key,
            bool courtVirtue)
        {
            hero = hero ?? new Dictionary<string, object>();
            Dictionary<string, object> document = null;
            Dictionary<string, object> nested = ReadDictionary(hero, "traits");
            if (ReadDictionary(hero, "traitPercentages") != null
                || ReadDictionary(hero, "courtVirtues") != null
                || ReadDictionary(hero, "foundationTraits") != null)
                document = hero;
            else if (nested != null
                && (ReadDictionary(nested, "traitPercentages") != null
                    || ReadDictionary(nested, "courtVirtues") != null
                    || ReadDictionary(nested, "foundationTraits") != null))
                document = nested;
            if (document == null)
            {
                Dictionary<string, object> stored = Program.ReadJsonObject(
                    Program.CharacterFile(campaignId, heroId, "traits.json"));
                if (stored.Count > 0) document = stored;
            }
            if (document == null) return 50;
            CharactersService.EnsureTraitPercentageData(document, heroId);
            Dictionary<string, object> source = ReadDictionary(document,
                courtVirtue ? "courtVirtues" : "traitPercentages")
                ?? new Dictionary<string, object>();
            return Clamp(ReadInt(source, key, 50), 0, 100);
        }

        private static void RegisterPregnancyCommitment(
            ReignDbConnection connection, string campaignId,
            AmbientPairContext pair, string conceptionId, int day,
            Dictionary<string, object> heroA,
            Dictionary<string, object> heroB,
            string existingMarriageActionId)
        {
            int honorA = RomanceTraitPercentage(campaignId, heroA,
                pair.HeroAId, "honor", true);
            int honorB = RomanceTraitPercentage(campaignId, heroB,
                pair.HeroBId, "honor", true);
            int chanceA = PregnancyCommitmentChance(honorA);
            int chanceB = PregnancyCommitmentChance(honorB);
            int rollA = StableDie(campaignId + "|" + conceptionId
                + "|pregnancy_commitment_a", 100);
            int rollB = StableDie(campaignId + "|" + conceptionId
                + "|pregnancy_commitment_b", 100);
            ReignDatabase.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_conception_commitments(
conception_id,pair_key,hero_a_id,hero_b_id,honor_a,honor_b,chance_a,chance_b,
roll_a,roll_b,passed_a,passed_b,status,marriage_action_id,world_day,updated_ts)
VALUES($id,$pair,$a,$b,$honorA,$honorB,$chanceA,$chanceB,$rollA,$rollB,
$passedA,$passedB,'awaiting_conception',$marriageAction,$day,$ts);",
                new Dictionary<string, object>
                {
                    ["id"] = conceptionId, ["pair"] = pair.PairKey,
                    ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                    ["honorA"] = honorA, ["honorB"] = honorB,
                    ["chanceA"] = chanceA, ["chanceB"] = chanceB,
                    ["rollA"] = rollA, ["rollB"] = rollB,
                    ["passedA"] = rollA <= chanceA ? 1 : 0,
                    ["passedB"] = rollB <= chanceB ? 1 : 0,
                    ["marriageAction"] = existingMarriageActionId ?? "",
                    ["day"] = day,
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }

        private static int PregnancyCommitmentChance(int honor)
        {
            return Math.Min(PregnancyCommitmentMaximumChancePercent,
                Clamp(honor, 0, 100) + PregnancyCommitmentHonorBonus);
        }

        private static int AffairLeaveSpouseChance(string campaignId,
            Dictionary<string, object> hero, string heroId,
            int mutualAffinity)
        {
            int honor = RomanceTraitPercentage(campaignId, hero, heroId,
                "honor", true);
            int judgment = RomanceTraitPercentage(campaignId, hero, heroId,
                "judgment", true);
            return Clamp(10 + Math.Max(0, mutualAffinity
                    - RomanceSeriousThreshold) / 3
                + Math.Max(0, 100 - honor) / 10
                + Math.Max(0, 100 - judgment) / 20,
                5, AffairMarriageMaximumChancePercent);
        }

        private static void TryBeginAffairMarriageCommitment(
            ReignDbConnection connection, string campaignId,
            string timelineId, AmbientPairContext pair, int day,
            Dictionary<string, object> heroA,
            Dictionary<string, object> heroB, string spouseA,
            string spouseB, int mutualAffinity)
        {
            if (ReignDatabase.QuerySql(connection, @"SELECT pair_key FROM relationship_affair_commitments
WHERE pair_key=$pair LIMIT 1;", new Dictionary<string, object>
                { ["pair"] = pair.PairKey }).Any()) return;
            bool aMarried = !string.IsNullOrWhiteSpace(spouseA)
                && !spouseA.Equals(pair.HeroBId,
                    StringComparison.OrdinalIgnoreCase);
            bool bMarried = !string.IsNullOrWhiteSpace(spouseB)
                && !spouseB.Equals(pair.HeroAId,
                    StringComparison.OrdinalIgnoreCase);
            if (!aMarried && !bMarried) return;
            int chanceA = aMarried ? AffairLeaveSpouseChance(campaignId,
                heroA, pair.HeroAId, mutualAffinity) : 100;
            int chanceB = bMarried ? AffairLeaveSpouseChance(campaignId,
                heroB, pair.HeroBId, mutualAffinity) : 100;
            int rollA = aMarried ? StableDie(campaignId + "|"
                + pair.PairKey + "|affair_leave_a", 100) : 0;
            int rollB = bMarried ? StableDie(campaignId + "|"
                + pair.PairKey + "|affair_leave_b", 100) : 0;
            bool passedA = !aMarried || rollA <= chanceA;
            bool passedB = !bMarried || rollB <= chanceB;
            string divorceA = "", divorceB = "";
            string status = passedA && passedB
                ? "awaiting_divorce" : "refused";
            if (passedA && passedB)
            {
                if (aMarried)
                    divorceA = QueueDirectorAction(connection, "divorce", day,
                        pair.HeroAId, spouseA,
                        new Dictionary<string, object>
                        {
                            ["source"] = "relationship_affair_commitment",
                            ["pairKey"] = pair.PairKey,
                            ["timelineId"] = timelineId, ["silent"] = true
                        });
                if (bMarried)
                    divorceB = QueueDirectorAction(connection, "divorce", day,
                        pair.HeroBId, spouseB,
                        new Dictionary<string, object>
                        {
                            ["source"] = "relationship_affair_commitment",
                            ["pairKey"] = pair.PairKey,
                            ["timelineId"] = timelineId, ["silent"] = true
                        });
                if ((aMarried && string.IsNullOrWhiteSpace(divorceA))
                    || (bMarried && string.IsNullOrWhiteSpace(divorceB)))
                    status = "divorce_queue_failed";
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_affair_commitments(
pair_key,hero_a_id,hero_b_id,spouse_a_id,spouse_b_id,chance_a,chance_b,roll_a,roll_b,
passed_a,passed_b,divorce_action_a_id,divorce_action_b_id,status,world_day,updated_ts)
VALUES($pair,$a,$b,$spouseA,$spouseB,$chanceA,$chanceB,$rollA,$rollB,$passedA,$passedB,
$divorceA,$divorceB,$status,$day,$ts);",
                new Dictionary<string, object>
                {
                    ["pair"] = pair.PairKey, ["a"] = pair.HeroAId,
                    ["b"] = pair.HeroBId, ["spouseA"] = spouseA,
                    ["spouseB"] = spouseB, ["chanceA"] = chanceA,
                    ["chanceB"] = chanceB, ["rollA"] = rollA,
                    ["rollB"] = rollB, ["passedA"] = passedA ? 1 : 0,
                    ["passedB"] = passedB ? 1 : 0,
                    ["divorceA"] = divorceA, ["divorceB"] = divorceB,
                    ["status"] = status, ["day"] = day, ["ts"] = ts
                });
            RecordRelationshipIncident(connection, pair,
                passedA && passedB ? "affair_marriage_commitment_started"
                    : "affair_marriage_commitment_refused", day,
                passedA && passedB
                    ? "Every married participant chose to leave their spouse before attempting marriage with the affair partner."
                    : "At least one married participant refused to leave their spouse for the affair partner.",
                new Dictionary<string, object>
                {
                    ["chanceA"] = chanceA, ["chanceB"] = chanceB,
                    ["rollA"] = rollA, ["rollB"] = rollB,
                    ["passedA"] = passedA, ["passedB"] = passedB,
                    ["maximumChancePercent"] = AffairMarriageMaximumChancePercent
                });
        }

        internal static Dictionary<string, object> RelationshipLifecycleView(ReignDbConnection connection,
            string observerId, string targetId)
        {
            string pairKey = AmbientPairKey(observerId, targetId);
            Dictionary<string, object> row = string.IsNullOrWhiteSpace(pairKey) ? null : ReignDatabase.QuerySql(connection,
                "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;",
                new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
            if (row == null) return new Dictionary<string, object>();
            bool observerIsA = ReadString(row, "hero_a_id", "").Equals(observerId, StringComparison.OrdinalIgnoreCase);
            List<string> tags = new List<string>();
            if (ReadInt(row, "lover_active", 0) == 1) tags.Add("lovers");
            if (ReadInt(row, "affair_active", 0) == 1) tags.Add("active_affair");
            if (ReadInt(row, observerIsA ? "estranged_a_to_b" : "estranged_b_to_a", 0) == 1) tags.Add("estranged");
            if (ReadInt(row, "married", 0) == 1) tags.Add("married");
            if (ReadInt(row, "divorced", 0) == 1) tags.Add("divorced");
            return new Dictionary<string, object>
            {
                ["pairKey"] = pairKey, ["tags"] = tags,
                ["lovers"] = ReadInt(row, "lover_active", 0) == 1,
                ["activeAffair"] = ReadInt(row, "affair_active", 0) == 1,
                ["romanceStage"] = ReadString(row, "romance_stage", ""),
                ["romancePositiveChanceBonus"] = ReadInt(row,
                    "romance_positive_bonus", 0),
                ["flirtationAttempted"] = ReadInt(row,
                    "flirt_attempted", 0) == 1,
                ["flirtationPassed"] = ReadInt(row,
                    "flirt_passed", 0) == 1,
                ["affairJudgmentAttempted"] = ReadInt(row,
                    "affair_attempted", 0) == 1,
                ["affairJudgmentPassed"] = ReadInt(row,
                    "affair_passed", 0) == 1,
                ["estranged"] = tags.Contains("estranged"), ["married"] = tags.Contains("married"),
                ["divorced"] = tags.Contains("divorced"), ["loverRumorCreated"] = !string.IsNullOrWhiteSpace(ReadString(row, "lover_rumor_id", "")),
                ["affairRumorCreated"] = !string.IsNullOrWhiteSpace(ReadString(row, "affair_rumor_id", "")),
                ["estrangedRumorCreated"] = !string.IsNullOrWhiteSpace(ReadString(row, "estranged_rumor_id", ""))
            };
        }
    }
}
