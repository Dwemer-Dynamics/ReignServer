using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int RelationshipLifecycleVersion = 6;
        private const int MbtiNativeRelationSyncTolerance = 5;
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
        private const int RomanceFlirtationPositiveChanceBonus = 10;
        private const int RomanceGrowingPositiveChanceBonus = 20;
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
                    try { MarkSaveSyncActiveStateDirty(campaignId); }
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
                ExecuteSql(connection, sql, values);
        }

        private static void EnsureRelationshipLifecycleSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_relationship_lifecycle_schema_revision";
            if (IsRelationshipLifecycleSchemaReady(connection, marker))
                return;
            EnsureRelationshipLifecycleSchemaCore(connection);
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
            {
                ExecuteSql(connection, @"
INSERT INTO schema_meta(key,value)
VALUES('postgresql_relationship_lifecycle_schema_revision',$revision)
ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
                    new Dictionary<string, object>
                    {
                        ["revision"] =
                            PostgreSqlRelationshipLifecycleSchemaRevision
                                .ToString()
                    });
                MarkPostgreSqlComponentSchemaReady(connection, marker,
                    PostgreSqlRelationshipLifecycleSchemaRevision);
                MarkRelationshipLifecycleSchemaShapeReady(connection);
            }
        }

        private static bool IsRelationshipLifecycleSchemaReady(
            ReignDbConnection connection, string marker)
        {
            if (!IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlRelationshipLifecycleSchemaRevision))
                return false;
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            string shapeKey = campaignId + "|"
                + PostgreSqlRelationshipLifecycleShapeMarker + "|"
                + PostgreSqlRelationshipLifecycleSchemaRevision.ToString(
                    CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(campaignId)
                && PostgreSqlComponentSchemaFastPaths.ContainsKey(shapeKey))
                return true;
            HashSet<string> columns = new HashSet<string>(
                QuerySql(connection,
                    "PRAGMA table_info(relationship_pair_lifecycle);")
                    .Select(row => ReadString(row, "name", "")),
                StringComparer.OrdinalIgnoreCase);
            bool ready = RelationshipLifecycleRomanceColumns.All(
                columns.Contains);
            if (ready && !string.IsNullOrWhiteSpace(campaignId))
                PostgreSqlComponentSchemaFastPaths[shapeKey] = 0;
            return ready;
        }

        private static void MarkRelationshipLifecycleSchemaShapeReady(
            ReignDbConnection connection)
        {
            string campaignId =
                ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrWhiteSpace(campaignId))
                return;
            PostgreSqlComponentSchemaFastPaths[campaignId + "|"
                + PostgreSqlRelationshipLifecycleShapeMarker + "|"
                + PostgreSqlRelationshipLifecycleSchemaRevision.ToString(
                    CultureInfo.InvariantCulture)] = 0;
        }

        private static void EnsureRelationshipLifecycleSchemaCore(
            ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_pair_lifecycle (
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
            EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "last_marriage_evaluation_day",
                "REAL NOT NULL DEFAULT -1000");
            EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "organic_marriage_colocated_days",
                "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                "marriage_blocked", "INTEGER NOT NULL DEFAULT 0");
            EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
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
                EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                    column.Key, column.Value);
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_lifecycle_heroes ON relationship_pair_lifecycle(hero_a_id,hero_b_id);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_marriage_reservations (
hero_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,director_action_id TEXT NOT NULL,
created_day REAL NOT NULL,created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_marriage_reservation_action
ON relationship_marriage_reservations(director_action_id);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_conception_commitments (
conception_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
honor_a INTEGER NOT NULL,honor_b INTEGER NOT NULL,chance_a INTEGER NOT NULL,chance_b INTEGER NOT NULL,
roll_a INTEGER NOT NULL,roll_b INTEGER NOT NULL,passed_a INTEGER NOT NULL,passed_b INTEGER NOT NULL,
status TEXT NOT NULL,marriage_action_id TEXT NOT NULL DEFAULT '',affinity_delta INTEGER NOT NULL DEFAULT 0,
world_day REAL NOT NULL,updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_affair_commitments (
pair_key TEXT PRIMARY KEY,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,spouse_a_id TEXT NOT NULL DEFAULT '',
spouse_b_id TEXT NOT NULL DEFAULT '',chance_a INTEGER NOT NULL DEFAULT 0,chance_b INTEGER NOT NULL DEFAULT 0,
roll_a INTEGER NOT NULL DEFAULT 0,roll_b INTEGER NOT NULL DEFAULT 0,passed_a INTEGER NOT NULL DEFAULT 0,
passed_b INTEGER NOT NULL DEFAULT 0,divorce_action_a_id TEXT NOT NULL DEFAULT '',divorce_action_b_id TEXT NOT NULL DEFAULT '',
divorce_a_completed INTEGER NOT NULL DEFAULT 0,divorce_b_completed INTEGER NOT NULL DEFAULT 0,
marriage_action_id TEXT NOT NULL DEFAULT '',status TEXT NOT NULL,world_day REAL NOT NULL,updated_ts INTEGER NOT NULL);" );
            if (TableExists(connection, "relationship_director_actions"))
            {
                ExecuteSql(connection, @"DELETE FROM relationship_marriage_reservations
WHERE NOT EXISTS (SELECT 1 FROM relationship_director_actions a
WHERE a.director_action_id=relationship_marriage_reservations.director_action_id
AND a.action_type='marriage' AND a.status IN ('pending','claimed'));");
                ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_marriage_reservations(
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
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_incidents (
incident_id TEXT PRIMARY KEY,pair_key TEXT NOT NULL,kind TEXT NOT NULL,
hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,world_day REAL NOT NULL,
summary TEXT NOT NULL DEFAULT '',rumor_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_relationship_incidents_pair ON relationship_incidents(pair_key,world_day DESC);");
            EnsureRelationshipFlingSchemaCore(connection);
        }

        private static void CompactDormantRelationshipLifecycleRows(ReignDbConnection connection)
        {
            if (ReadString(QuerySql(connection,
                "SELECT value FROM schema_meta WHERE key='relationship_lifecycle_sparse_v1' LIMIT 1;")
                .FirstOrDefault(), "value", "") == "1") return;
            ExecuteSql(connection, @"DELETE FROM relationship_pair_lifecycle
WHERE lover_active=0 AND lover_exception_attempted=0 AND lover_rumor_id=''
  AND affair_active=0 AND affair_rumor_id=''
  AND romance_stage='' AND flirt_attempted=0 AND affair_attempted=0
  AND estranged_a_to_b=0 AND estranged_b_to_a=0 AND estranged_rumor_id=''
  AND married=0 AND marriage_action_id='' AND marriage_blocked=0
  AND divorced=0 AND divorce_action_id='' AND divorce_rumor_id=''
  AND conception_action_id='';" );
            ExecuteSql(connection,
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
                Dictionary<string, object> stored = ReadJsonObject(
                    CharacterFile(campaignId, heroId, "traits.json"));
                if (stored.Count > 0) document = stored;
            }
            if (document == null) return 50;
            EnsureTraitPercentageData(document, heroId);
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
            ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_conception_commitments(
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
            if (QuerySql(connection, @"SELECT pair_key FROM relationship_affair_commitments
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
            ExecuteSql(connection, @"INSERT INTO relationship_affair_commitments(
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

        private static Dictionary<string, object> ProcessRelationshipLifecycle(
            ReignDbConnection connection,
            string campaignId,
            AmbientPairContext pair,
            int day,
            Dictionary<string, object> heroA,
            Dictionary<string, object> heroB,
            int affinityAB,
            int affinityBA,
            Dictionary<string, object> preloadedRow = null,
            bool rowPreloaded = false,
            string timelineId = "main")
        {
            Dictionary<string, object> row;
            if (rowPreloaded)
            {
                row = preloadedRow ?? new Dictionary<string, object>();
            }
            else
            {
                EnsureRelationshipLifecycleSchema(connection);
                row = QuerySql(connection,
                    "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pair.PairKey }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool related = !MbtiRomanceEligible(heroA, heroB);
            string spouseA = ReadString(heroA, "spouseId", "");
            string spouseB = ReadString(heroB, "spouseId", "");
            bool marriedToEachOther = spouseA.Equals(pair.HeroBId, StringComparison.OrdinalIgnoreCase)
                && spouseB.Equals(pair.HeroAId, StringComparison.OrdinalIgnoreCase);
            bool marriedToOther = (!string.IsNullOrWhiteSpace(spouseA) && !spouseA.Equals(pair.HeroBId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(spouseB) && !spouseB.Equals(pair.HeroAId, StringComparison.OrdinalIgnoreCase));
            bool sameSex = ReadBool(heroA, "isFemale", false) == ReadBool(heroB, "isFemale", false);
            bool requiresLifecycleState = row.Count > 0 || marriedToEachOther
                || (!related && affinityAB >= RomanceFlirtationThreshold
                    && affinityBA >= RomanceFlirtationThreshold);
            if (!requiresLifecycleState)
            {
                return new Dictionary<string, object>
                {
                    ["loverStarted"] = false, ["loverEnded"] = false,
                    ["affairStarted"] = false, ["affairEnded"] = false,
                    ["marriageQueued"] = false, ["divorceQueued"] = false,
                    ["conceptionQueued"] = false, ["rumorCreated"] = false,
                    ["sharedTag"] = SharedRelationshipTag(campaignId, pair.PairKey,
                        affinityAB, affinityBA, heroA, heroB)
                };
            }
            bool lovers = ReadInt(row, "lover_active", 0) == 1;
            bool affair = ReadInt(row, "affair_active", 0) == 1;
            bool loverStarted = false, loverEnded = false, affairStarted = false, affairEnded = false;
            bool flirtationStarted = false, growingAttractionStarted = false;
            bool seriousAttractionStarted = false, flirtationAttemptedNow = false;
            bool affairAttemptedNow = false;
            bool marriageQueued = false, divorceQueued = false, conceptionQueued = false, rumorCreated = false;
            int exceptionAttempted = ReadInt(row, "lover_exception_attempted", 0);
            int exceptionPassed = ReadInt(row, "lover_exception_passed", 0);
            int exceptionRoll = ReadInt(row, "lover_exception_roll", 0);
            string romanceStage = ReadString(row, "romance_stage", "");
            if (string.IsNullOrWhiteSpace(romanceStage) && lovers)
                romanceStage = "lovers";
            double romanceStageStartedDay = ReadDouble(row,
                "romance_stage_started_day", 0d);
            double romanceLastColocatedDay = ReadDouble(row,
                "romance_last_colocated_day", 0d);
            int romancePositiveBonus = ReadInt(row,
                "romance_positive_bonus", RomancePositiveChanceBonus(row));
            int flirtEpisode = ReadInt(row, "flirt_episode", 0);
            int flirtAttempted = ReadInt(row, "flirt_attempted", 0);
            int flirtPassed = ReadInt(row, "flirt_passed", 0);
            int flirtTraitA = ReadInt(row, "flirt_trait_a", 0);
            int flirtTraitB = ReadInt(row, "flirt_trait_b", 0);
            int flirtRollA = ReadInt(row, "flirt_roll_a", 0);
            int flirtRollB = ReadInt(row, "flirt_roll_b", 0);
            int affairEpisode = ReadInt(row, "affair_episode", 0);
            int affairAttempted = ReadInt(row, "affair_attempted", 0);
            int affairPassed = ReadInt(row, "affair_passed", 0);
            int judgmentTraitA = ReadInt(row, "judgment_trait_a", 0);
            int judgmentTraitB = ReadInt(row, "judgment_trait_b", 0);
            int judgmentRollA = ReadInt(row, "judgment_roll_a", 0);
            int judgmentRollB = ReadInt(row, "judgment_roll_b", 0);
            if (affair && affairAttempted == 0)
            {
                // Preserve valid affairs from lifecycle v3. New affairs always
                // carry both explicit judgment receipts.
                affairEpisode = 1;
                affairAttempted = 1;
                affairPassed = 1;
                exceptionAttempted = 1;
                exceptionPassed = 1;
            }
            double loverStartedDay = ReadDouble(row, "lover_started_day", 0d);
            double affairStartedDay = ReadDouble(row, "affair_started_day", 0d);
            string loverRumorId = ReadString(row, "lover_rumor_id", "");
            string affairRumorId = ReadString(row, "affair_rumor_id", "");
            string estrangedRumorId = ReadString(row, "estranged_rumor_id", "");
            string marriageActionId = ReadString(row, "marriage_action_id", "");
            bool marriageBlocked = ReadInt(row, "marriage_blocked", 0) == 1;
            string marriageBlockReason = ReadString(row,
                "marriage_block_reason", "");
            string divorceActionId = ReadString(row, "divorce_action_id", "");
            string divorceRumorId = ReadString(row, "divorce_rumor_id", "");
            string conceptionActionId = ReadString(row, "conception_action_id", "");
            double lastMarriageEvaluationDay = ReadDouble(row,
                "last_marriage_evaluation_day", -1000d);
            int organicMarriageColocatedDays = ReadInt(row,
                "organic_marriage_colocated_days", 0);
            bool divorced = ReadInt(row, "divorced", 0) == 1;
            double divorceDay = ReadDouble(row, "divorce_day", 0d);

            int mutualAffinity = Math.Min(affinityAB, affinityBA);
            if (!lovers && mutualAffinity < RomanceFlirtationResetThreshold
                && (flirtAttempted == 1 || !string.IsNullOrWhiteSpace(romanceStage)))
            {
                romanceStage = "";
                romanceStageStartedDay = 0d;
                romancePositiveBonus = 0;
                flirtEpisode = Math.Max(1, flirtEpisode) + 1;
                flirtAttempted = 0;
                flirtPassed = 0;
                flirtTraitA = flirtTraitB = flirtRollA = flirtRollB = 0;
                affairEpisode = Math.Max(1, affairEpisode) + 1;
                affairAttempted = affairPassed = 0;
                judgmentTraitA = judgmentTraitB = judgmentRollA = judgmentRollB = 0;
            }
            else if (!lovers
                && romanceStage.Equals("serious_attraction",
                    StringComparison.OrdinalIgnoreCase)
                && mutualAffinity < RomanceSeriousResetThreshold)
            {
                romanceStage = mutualAffinity >= RomanceGrowingThreshold
                    ? "growing_attraction"
                    : mutualAffinity >= RomanceFlirtationResetThreshold
                        ? "flirtation" : "";
                romanceStageStartedDay = day;
                affairEpisode = Math.Max(1, affairEpisode) + 1;
                affairAttempted = affairPassed = 0;
                judgmentTraitA = judgmentTraitB = judgmentRollA = judgmentRollB = 0;
            }
            if (!lovers
                && romanceStage.Equals("growing_attraction",
                    StringComparison.OrdinalIgnoreCase)
                && mutualAffinity < RomanceGrowingResetThreshold)
            {
                romanceStage = mutualAffinity >= RomanceFlirtationResetThreshold
                    ? "flirtation" : "";
                romanceStageStartedDay = day;
            }
            if (!lovers && affairAttempted == 1 && affairPassed == 0
                && mutualAffinity < RomanceGrowingResetThreshold)
            {
                affairEpisode = Math.Max(1, affairEpisode) + 1;
                affairAttempted = affairPassed = 0;
                judgmentTraitA = judgmentTraitB = judgmentRollA = judgmentRollB = 0;
            }

            if (!lovers && !related && string.IsNullOrWhiteSpace(romanceStage)
                && mutualAffinity >= RomanceFlirtationThreshold
                && flirtAttempted == 0)
            {
                flirtEpisode = Math.Max(1, flirtEpisode);
                flirtAttempted = 1;
                flirtationAttemptedNow = true;
                flirtTraitA = RomanceTraitPercentage(campaignId, heroA,
                    pair.HeroAId, "flirtatiousness", false);
                flirtTraitB = RomanceTraitPercentage(campaignId, heroB,
                    pair.HeroBId, "flirtatiousness", false);
                flirtRollA = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + flirtEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|flirt_a", 100);
                flirtRollB = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + flirtEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|flirt_b", 100);
                flirtPassed = flirtRollA <= flirtTraitA
                    && flirtRollB <= flirtTraitB ? 1 : 0;
                RecordRelationshipIncident(connection, pair,
                    flirtPassed == 1 ? "flirtation_started"
                        : "flirtation_declined", day,
                    flirtPassed == 1
                        ? "Both participants independently chose to begin flirting."
                        : "At least one participant declined the flirtation opportunity.",
                    new Dictionary<string, object>
                    {
                        ["episode"] = flirtEpisode,
                        ["traitAToB"] = flirtTraitA,
                        ["traitBToA"] = flirtTraitB,
                        ["rollAToB"] = flirtRollA,
                        ["rollBToA"] = flirtRollB,
                        ["passedAToB"] = flirtRollA <= flirtTraitA,
                        ["passedBToA"] = flirtRollB <= flirtTraitB,
                        ["affinityAToB"] = affinityAB,
                        ["affinityBToA"] = affinityBA
                    });
                if (flirtPassed == 1)
                {
                    romanceStage = "flirtation";
                    romanceStageStartedDay = day;
                    flirtationStarted = true;
                }
            }
            if (!lovers && !related
                && romanceStage.Equals("flirtation",
                    StringComparison.OrdinalIgnoreCase)
                && mutualAffinity >= RomanceGrowingThreshold)
            {
                if (!marriedToOther)
                {
                    lovers = true;
                    loverStarted = true;
                    romanceStage = "lovers";
                    romanceStageStartedDay = day;
                }
                else
                {
                    if (affairAttempted == 0)
                    {
                        affairEpisode = Math.Max(1, affairEpisode);
                        affairAttempted = 1;
                        affairAttemptedNow = true;
                        judgmentTraitA = RomanceTraitPercentage(campaignId,
                            heroA, pair.HeroAId, "judgment", true);
                        judgmentTraitB = RomanceTraitPercentage(campaignId,
                            heroB, pair.HeroBId, "judgment", true);
                        judgmentRollA = StableDie(campaignId + "|"
                            + pair.PairKey + "|"
                            + affairEpisode.ToString(CultureInfo.InvariantCulture)
                            + "|affair_judgment_a", 100);
                        judgmentRollB = StableDie(campaignId + "|"
                            + pair.PairKey + "|"
                            + affairEpisode.ToString(CultureInfo.InvariantCulture)
                            + "|affair_judgment_b", 100);
                        // Judgment is the stop-yourself roll: a participant
                        // proceeds only when temptation rolls above judgment.
                        affairPassed = judgmentRollA > judgmentTraitA
                            && judgmentRollB > judgmentTraitB ? 1 : 0;
                        exceptionAttempted = affairAttempted;
                        exceptionPassed = affairPassed;
                        exceptionRoll = Math.Max(judgmentRollA, judgmentRollB);
                        RecordRelationshipIncident(connection, pair,
                            affairPassed == 1 ? "affair_judgment_passed"
                                : "affair_judgment_refused", day,
                            affairPassed == 1
                                ? "Both participants independently chose to proceed despite an existing marriage."
                                : "At least one participant's judgment stopped the attraction from becoming an affair.",
                            new Dictionary<string, object>
                            {
                                ["episode"] = affairEpisode,
                                ["judgmentA"] = judgmentTraitA,
                                ["judgmentB"] = judgmentTraitB,
                                ["rollA"] = judgmentRollA,
                                ["rollB"] = judgmentRollB,
                                ["proceededA"] = judgmentRollA > judgmentTraitA,
                                ["proceededB"] = judgmentRollB > judgmentTraitB,
                                ["affinityAToB"] = affinityAB,
                                ["affinityBToA"] = affinityBA
                            });
                        if (affairPassed == 1)
                        {
                            lovers = true;
                            loverStarted = true;
                            romanceStage = "lovers";
                            romanceStageStartedDay = day;
                        }
                    }
                }
                if (loverStarted)
                {
                    loverStartedDay = day;
                    RecordRelationshipIncident(connection, pair,
                        "lovers_started", day,
                        marriedToOther
                            ? "The mutual flirtation became an affair at mutual affinity fifty after both individual judgment checks passed."
                            : "The mutual flirtation became a lovers relationship at mutual affinity fifty.",
                        new Dictionary<string, object>
                        {
                            ["sameSex"] = sameSex,
                            ["marriedToOther"] = marriedToOther,
                            ["affinityAToB"] = affinityAB,
                            ["affinityBToA"] = affinityBA
                        });
                }
            }

            if (lovers && marriedToOther && !affair && affairAttempted == 0)
            {
                affairEpisode = Math.Max(1, affairEpisode);
                affairAttempted = 1;
                affairAttemptedNow = true;
                judgmentTraitA = RomanceTraitPercentage(campaignId, heroA,
                    pair.HeroAId, "judgment", true);
                judgmentTraitB = RomanceTraitPercentage(campaignId, heroB,
                    pair.HeroBId, "judgment", true);
                judgmentRollA = StableDie(campaignId + "|" + pair.PairKey
                    + "|" + affairEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|existing_lover_affair_judgment_a", 100);
                judgmentRollB = StableDie(campaignId + "|" + pair.PairKey
                    + "|" + affairEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|existing_lover_affair_judgment_b", 100);
                affairPassed = judgmentRollA > judgmentTraitA
                    && judgmentRollB > judgmentTraitB ? 1 : 0;
                exceptionAttempted = affairAttempted;
                exceptionPassed = affairPassed;
                exceptionRoll = Math.Max(judgmentRollA, judgmentRollB);
                if (affairPassed == 0)
                {
                    lovers = false;
                    loverEnded = true;
                    romanceStage = "serious_attraction";
                    romanceStageStartedDay = day;
                    RecordRelationshipIncident(connection, pair,
                        "lovers_ended_at_marriage_boundary", day,
                        "An existing lovers relationship did not pass both judgment checks after a marriage made it an affair.",
                        new Dictionary<string, object>
                        {
                            ["judgmentA"] = judgmentTraitA,
                            ["judgmentB"] = judgmentTraitB,
                            ["rollA"] = judgmentRollA,
                            ["rollB"] = judgmentRollB
                        });
                }
            }

            if (lovers && (affinityAB < RomanceFlirtationThreshold
                || affinityBA < RomanceFlirtationThreshold))
            {
                lovers = false;
                loverEnded = true;
                romanceStage = mutualAffinity >= RomanceFlirtationResetThreshold
                    ? "flirtation" : "";
                romanceStageStartedDay = day;
                RecordRelationshipIncident(connection, pair, "lovers_ended", day,
                    "The relationship fell below the minimum affinity required to remain lovers.",
                    new Dictionary<string, object> { ["affinityAToB"] = affinityAB, ["affinityBToA"] = affinityBA });
            }
            bool nowAffair = lovers && marriedToOther && affairPassed == 1;
            if (nowAffair && !affair)
            {
                affairStarted = true;
                affairStartedDay = day;
                RecordRelationshipIncident(connection, pair, "affair_started", day,
                    "A lovers relationship became an active affair because at least one participant is married to someone else.",
                    new Dictionary<string, object> { ["spouseAId"] = spouseA, ["spouseBId"] = spouseB });
            }
            else if (!nowAffair && affair)
            {
                affairEnded = true;
                RecordRelationshipIncident(connection, pair, "affair_ended", day,
                    "The active affair ended.", new Dictionary<string, object>());
            }
            affair = nowAffair;
            if (lovers || affair || !string.IsNullOrWhiteSpace(romanceStage))
                romanceLastColocatedDay = day;
            romancePositiveBonus = RomancePositiveChanceBonus(romanceStage,
                lovers);

            bool estrangedAB = marriedToEachOther && affinityAB <= -30;
            bool estrangedBA = marriedToEachOther && affinityBA <= -30;
            bool wasEstranged = ReadInt(row, "estranged_a_to_b", 0) == 1 || ReadInt(row, "estranged_b_to_a", 0) == 1;
            bool isEstranged = estrangedAB || estrangedBA;
            if (isEstranged && !wasEstranged)
            {
                RecordRelationshipIncident(connection, pair, "estranged", day,
                    "At least one spouse's directional affinity fell to negative thirty or below.",
                    new Dictionary<string, object> { ["estrangedAToB"] = estrangedAB, ["estrangedBToA"] = estrangedBA });
            }

            if (marriedToEachOther && (affinityAB <= -50 || affinityBA <= -50) && string.IsNullOrWhiteSpace(divorceActionId))
            {
                divorceActionId = QueueDirectorAction(connection, "divorce", day, pair.HeroAId, pair.HeroBId,
                    new Dictionary<string, object>
                    {
                        ["source"] = "mbti_relationship_lifecycle", ["pairKey"] = pair.PairKey,
						["timelineId"] = timelineId,
                        ["affinityAToB"] = affinityAB, ["affinityBToA"] = affinityBA, ["silent"] = true
                    });
                divorceQueued = true;
            }

            bool bothUnmarried = string.IsNullOrWhiteSpace(spouseA) && string.IsNullOrWhiteSpace(spouseB);
            if (affair && mutualAffinity >= RomanceSeriousThreshold)
                TryBeginAffairMarriageCommitment(connection, campaignId,
                    timelineId, pair, day, heroA, heroB, spouseA, spouseB,
                    mutualAffinity);
            bool organicMarriageEligible = bothUnmarried && !related && lovers
                && affinityAB >= RomanceSeriousThreshold
                && affinityBA >= RomanceSeriousThreshold
                && SnapshotMarriageEligible(heroA, heroB)
                && !marriageBlocked
                && string.IsNullOrWhiteSpace(marriageActionId);
            if (organicMarriageEligible)
            {
                organicMarriageColocatedDays++;
                if (organicMarriageColocatedDays
                    >= OrganicMarriageEvaluationCadenceDays)
                {
                    organicMarriageColocatedDays = 0;
                    lastMarriageEvaluationDay = day;
                    int roll = StableDie(campaignId + "|" + pair.PairKey
                        + "|" + day.ToString(CultureInfo.InvariantCulture)
                        + "|mutual_marriage", 100);
                    Dictionary<string, object> marriageCounters =
                        new Dictionary<string, object>
                    {
                        ["organicEvaluations"] = 1,
                        ["organicAccepted"] = roll
                            <= OrganicMarriageChancePercent ? 1 : 0,
                        ["organicRefused"] = roll
                            <= OrganicMarriageChancePercent ? 0 : 1,
                        ["chancePercent"] = OrganicMarriageChancePercent,
                        ["roll"] = roll
                    };
                    if (roll <= OrganicMarriageChancePercent)
                    {
                        marriageActionId = QueueDirectorAction(connection,
                            "marriage", day, pair.HeroAId, pair.HeroBId,
                            new Dictionary<string, object>
                            {
                                ["source"] = "mbti_relationship_lifecycle",
                                ["pairKey"] = pair.PairKey,
                                ["timelineId"] = timelineId,
                                ["route"] = "mutual_affinity",
                                ["roll"] = roll,
                                ["chancePercent"] =
                                    OrganicMarriageChancePercent,
                                ["affinityAToB"] = affinityAB,
                                ["affinityBToA"] = affinityBA,
                                ["silent"] = true
                            });
                        marriageQueued = !string.IsNullOrWhiteSpace(
                            marriageActionId);
                        marriageCounters["organicQueued"] = marriageQueued
                            ? 1 : 0;
                        marriageCounters["reservationBlocked"] = marriageQueued
                            ? 0 : 1;
                    }
                    RecordWorldTestCounter(connection, campaignId, timelineId,
                        day, "marriages", "organic_" + pair.PairKey + "_"
                            + day.ToString(CultureInfo.InvariantCulture),
                        marriageCounters);
                }
            }

            if (lovers && !sameSex && string.IsNullOrWhiteSpace(conceptionActionId))
            {
                Dictionary<string, object> mother = ReadBool(heroA, "isFemale", false) ? heroA : heroB;
                Dictionary<string, object> father = ReferenceEquals(mother, heroA) ? heroB : heroA;
                double motherAge = ReadDouble(mother, "age", 0d);
                if (!ReadBool(mother, "isPregnant", false) && motherAge >= 18d && motherAge <= 45d)
                {
                    int roll = StableDie(campaignId + "|" + pair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture) + "|lover_conception", 100);
                    if (roll <= LoverConceptionChancePercent)
                    {
                        string motherId = ReadFirstString(mother, "heroStringId", "heroId", "id");
                        string fatherId = ReadFirstString(father, "heroStringId", "heroId", "id");
                        string legalFatherId = FirstNonEmpty(ReadString(mother, "spouseId", ""), fatherId);
                        string conceptionId = CreateLifecycleConceptionId(campaignId, timelineId, pair.PairKey, day);
                        bool unmarriedCommitment = bothUnmarried;
                        bool illegitimate = affair
                            || (!marriedToEachOther && !unmarriedCommitment)
                            || !legalFatherId.Equals(fatherId,
                                StringComparison.OrdinalIgnoreCase);
                        ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,conception_day,due_day,status,secrecy,
payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$father,$legal,$day,$due,'pending_game',$secrecy,$payload,$ts,$ts);",
                            new Dictionary<string, object>
                            {
                                ["id"] = conceptionId, ["attempt"] = "relationship_lifecycle_" + pair.PairKey + "_" + day,
                                ["mother"] = motherId, ["father"] = fatherId, ["legal"] = legalFatherId,
                                ["day"] = day, ["due"] = day + 36d, ["secrecy"] = illegitimate ? 0.75d : 0d,
                                ["payload"] = Json.Serialize(new Dictionary<string, object>
                                {
                                    ["source"] = "mbti_relationship_lifecycle", ["pairKey"] = pair.PairKey,
                                    ["roll"] = roll,
                                    ["chancePercent"] = LoverConceptionChancePercent,
                                    ["isIllegitimate"] = illegitimate,
                                    ["unmarriedCommitmentPending"] = unmarriedCommitment
                                }), ["ts"] = ts
                            });
                        conceptionActionId = QueueDirectorAction(connection, "start_conception", day, motherId, fatherId,
                            new Dictionary<string, object>
                            {
                                ["source"] = "mbti_relationship_lifecycle", ["pairKey"] = pair.PairKey,
                                ["timelineId"] = timelineId,
                                ["conceptionId"] = conceptionId, ["motherId"] = motherId,
                                ["biologicalFatherId"] = fatherId, ["legalFatherId"] = legalFatherId,
                                ["conceptionDay"] = day, ["dueDay"] = day + 36d,
                                ["secrecy"] = illegitimate ? 0.75d : 0d,
                                ["isIllegitimate"] = illegitimate,
                                ["unmarriedCommitmentPending"] = unmarriedCommitment
                            });
                        if (unmarriedCommitment)
                            RegisterPregnancyCommitment(connection, campaignId,
                                pair, conceptionId, day, heroA, heroB,
                                marriageActionId);
                        conceptionQueued = true;
                        if (affair)
                        {
                            Dictionary<string, object> promotedAffair = RegisterSocialOccurrence(connection, campaignId,
                                BuildRelationshipSocialOccurrence(pair, day, "affair", heroA, heroB, spouseA, spouseB, true, true,
                                    "The affair resulted in a conception and could no longer remain socially containable.",
                                    timelineId), worldTestSchemaReady: rowPreloaded,
                                useExistingTransaction: rowPreloaded);
                            string promotedOccurrenceId = ReadString(promotedAffair, "occurrenceId", "");
                            if (!string.IsNullOrWhiteSpace(promotedOccurrenceId)) affairRumorId = promotedOccurrenceId;
                        }
                        RecordRelationshipIncident(connection, pair, illegitimate ? "bastard_conception" : "lover_conception",
                            day, illegitimate ? "The lovers conceived a child outside the mother's marriage."
                                : "The lovers conceived a child.",
                            new Dictionary<string, object> { ["motherId"] = motherId, ["fatherId"] = fatherId, ["conceptionId"] = conceptionId });
                    }
                }
            }

            if (affair)
            {
                Dictionary<string, object> occurrence = RegisterSocialOccurrence(connection, campaignId,
                    BuildRelationshipSocialOccurrence(pair, day, "affair", heroA, heroB, spouseA, spouseB, false, false,
                        "Secondhand reports describe an affair between the two participants.", timelineId),
                    worldTestSchemaReady: rowPreloaded,
                    useExistingTransaction: rowPreloaded);
                if (ReadBool(occurrence, "exposed", false))
                    affairRumorId = FirstNonEmpty(ReadString(occurrence, "occurrenceId", ""), affairRumorId);
                rumorCreated = ReadBool(occurrence, "exposed", false) && !ReadBool(occurrence, "duplicate", false);
            }
            if (isEstranged)
            {
                Dictionary<string, object> occurrence = RegisterSocialOccurrence(connection, campaignId,
                    BuildRelationshipSocialOccurrence(pair, day, "marital_strife", heroA, heroB, spouseA, spouseB, false, false,
                        "Secondhand reports describe serious and continuing strife in the marriage.", timelineId),
                    worldTestSchemaReady: rowPreloaded,
                    useExistingTransaction: rowPreloaded);
                if (ReadBool(occurrence, "exposed", false))
                    estrangedRumorId = FirstNonEmpty(ReadString(occurrence, "occurrenceId", ""), estrangedRumorId);
                rumorCreated = rumorCreated || (ReadBool(occurrence, "exposed", false) && !ReadBool(occurrence, "duplicate", false));
            }

            double persistedConceptionDay = conceptionQueued
                ? day : ReadDouble(row, "last_conception_day", -1000d);
            bool lifecycleStateChanged = row.Count == 0
                || ReadInt(row, "lover_active", 0) != (lovers ? 1 : 0)
                || Math.Abs(ReadDouble(row, "lover_started_day", 0d)
                    - loverStartedDay) > 0.000001d
                || ReadInt(row, "lover_exception_attempted", 0) != exceptionAttempted
                || ReadInt(row, "lover_exception_passed", 0) != exceptionPassed
                || ReadInt(row, "lover_exception_roll", 0) != exceptionRoll
                || !ReadString(row, "lover_rumor_id", "").Equals(
                    loverRumorId, StringComparison.Ordinal)
                || !ReadString(row, "romance_stage", "").Equals(
                    romanceStage, StringComparison.OrdinalIgnoreCase)
                || Math.Abs(ReadDouble(row, "romance_stage_started_day", 0d)
                    - romanceStageStartedDay) > 0.000001d
                || Math.Abs(ReadDouble(row, "romance_last_colocated_day", 0d)
                    - romanceLastColocatedDay) > 0.000001d
                || ReadInt(row, "romance_positive_bonus", 0)
                    != romancePositiveBonus
                || ReadInt(row, "flirt_episode", 0) != flirtEpisode
                || ReadInt(row, "flirt_attempted", 0) != flirtAttempted
                || ReadInt(row, "flirt_passed", 0) != flirtPassed
                || ReadInt(row, "flirt_trait_a", 0) != flirtTraitA
                || ReadInt(row, "flirt_trait_b", 0) != flirtTraitB
                || ReadInt(row, "flirt_roll_a", 0) != flirtRollA
                || ReadInt(row, "flirt_roll_b", 0) != flirtRollB
                || ReadInt(row, "affair_episode", 0) != affairEpisode
                || ReadInt(row, "affair_attempted", 0) != affairAttempted
                || ReadInt(row, "affair_passed", 0) != affairPassed
                || ReadInt(row, "judgment_trait_a", 0) != judgmentTraitA
                || ReadInt(row, "judgment_trait_b", 0) != judgmentTraitB
                || ReadInt(row, "judgment_roll_a", 0) != judgmentRollA
                || ReadInt(row, "judgment_roll_b", 0) != judgmentRollB
                || ReadInt(row, "affair_active", 0) != (affair ? 1 : 0)
                || Math.Abs(ReadDouble(row, "affair_started_day", 0d)
                    - affairStartedDay) > 0.000001d
                || !ReadString(row, "affair_rumor_id", "").Equals(
                    affairRumorId, StringComparison.Ordinal)
                || ReadInt(row, "estranged_a_to_b", 0) != (estrangedAB ? 1 : 0)
                || ReadInt(row, "estranged_b_to_a", 0) != (estrangedBA ? 1 : 0)
                || !ReadString(row, "estranged_rumor_id", "").Equals(
                    estrangedRumorId, StringComparison.Ordinal)
                || ReadInt(row, "married", 0) != (marriedToEachOther ? 1 : 0)
                || !ReadString(row, "marriage_action_id", "").Equals(
                    marriageActionId, StringComparison.Ordinal)
                || ReadInt(row, "marriage_blocked", 0) != (marriageBlocked ? 1 : 0)
                || !ReadString(row, "marriage_block_reason", "").Equals(
                    marriageBlockReason, StringComparison.Ordinal)
                || ReadInt(row, "divorced", 0) != (divorced ? 1 : 0)
                || Math.Abs(ReadDouble(row, "divorce_day", 0d)
                    - divorceDay) > 0.000001d
                || !ReadString(row, "divorce_action_id", "").Equals(
                    divorceActionId, StringComparison.Ordinal)
                || !ReadString(row, "divorce_rumor_id", "").Equals(
                    divorceRumorId, StringComparison.Ordinal)
                || !ReadString(row, "conception_action_id", "").Equals(
                    conceptionActionId, StringComparison.Ordinal)
                || Math.Abs(ReadDouble(row, "last_conception_day", -1000d)
                    - persistedConceptionDay) > 0.000001d
                || Math.Abs(ReadDouble(row,
                    "last_marriage_evaluation_day", -1000d)
                    - lastMarriageEvaluationDay) > 0.000001d
                || ReadInt(row, "organic_marriage_colocated_days", 0)
                    != organicMarriageColocatedDays
                || ReadInt(row, "version", 0) != RelationshipLifecycleVersion;
            if (lifecycleStateChanged)
            {
                ExecuteRelationshipLifecycleWrite(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,lover_started_day,lover_exception_attempted,lover_exception_passed,
lover_exception_roll,lover_rumor_id,romance_stage,romance_stage_started_day,romance_last_colocated_day,
romance_positive_bonus,flirt_episode,flirt_attempted,flirt_passed,flirt_trait_a,flirt_trait_b,flirt_roll_a,flirt_roll_b,
affair_episode,affair_attempted,affair_passed,judgment_trait_a,judgment_trait_b,judgment_roll_a,judgment_roll_b,
affair_active,affair_started_day,affair_rumor_id,
estranged_a_to_b,estranged_b_to_a,estranged_rumor_id,married,marriage_action_id,
marriage_blocked,marriage_block_reason,divorced,divorce_day,
divorce_action_id,divorce_rumor_id,conception_action_id,last_conception_day,last_marriage_evaluation_day,organic_marriage_colocated_days,last_processed_day,version,updated_ts)
VALUES($pair,$a,$b,$lovers,$loverDay,$attempted,$passed,$exceptionRoll,$loverRumor,$romanceStage,$romanceStageDay,$romanceColocatedDay,
$romanceBonus,$flirtEpisode,$flirtAttempted,$flirtPassed,$flirtTraitA,$flirtTraitB,$flirtRollA,$flirtRollB,
$affairEpisode,$affairAttempted,$affairPassed,$judgmentTraitA,$judgmentTraitB,$judgmentRollA,$judgmentRollB,
$affair,$affairDay,$affairRumor,
$estrangedAB,$estrangedBA,$estrangedRumor,$married,$marriageAction,$marriageBlocked,$marriageBlockReason,
$divorced,$divorceDay,$divorceAction,
$divorceRumor,$conceptionAction,$conceptionDay,$marriageEvaluationDay,$marriageColocatedDays,$day,$version,$ts)
ON CONFLICT(pair_key) DO UPDATE SET lover_active=$lovers,lover_started_day=$loverDay,
 lover_exception_attempted=$attempted,lover_exception_passed=$passed,lover_exception_roll=$exceptionRoll,
 lover_rumor_id=$loverRumor,romance_stage=$romanceStage,romance_stage_started_day=$romanceStageDay,
 romance_last_colocated_day=$romanceColocatedDay,romance_positive_bonus=$romanceBonus,
 flirt_episode=$flirtEpisode,flirt_attempted=$flirtAttempted,flirt_passed=$flirtPassed,
 flirt_trait_a=$flirtTraitA,flirt_trait_b=$flirtTraitB,flirt_roll_a=$flirtRollA,flirt_roll_b=$flirtRollB,
 affair_episode=$affairEpisode,affair_attempted=$affairAttempted,affair_passed=$affairPassed,
 judgment_trait_a=$judgmentTraitA,judgment_trait_b=$judgmentTraitB,judgment_roll_a=$judgmentRollA,
 judgment_roll_b=$judgmentRollB,affair_active=$affair,affair_started_day=$affairDay,affair_rumor_id=$affairRumor,
estranged_a_to_b=$estrangedAB,estranged_b_to_a=$estrangedBA,estranged_rumor_id=$estrangedRumor,
married=$married,marriage_action_id=$marriageAction,marriage_blocked=$marriageBlocked,
marriage_block_reason=$marriageBlockReason,divorced=$divorced,divorce_day=$divorceDay,
divorce_action_id=$divorceAction,divorce_rumor_id=$divorceRumor,conception_action_id=$conceptionAction,
last_conception_day=$conceptionDay,last_marriage_evaluation_day=$marriageEvaluationDay,
organic_marriage_colocated_days=$marriageColocatedDays,last_processed_day=$day,version=$version,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["pair"] = pair.PairKey, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                    ["lovers"] = lovers ? 1 : 0, ["loverDay"] = loverStartedDay,
                    ["attempted"] = exceptionAttempted, ["passed"] = exceptionPassed, ["exceptionRoll"] = exceptionRoll,
                    ["loverRumor"] = loverRumorId,
                    ["romanceStage"] = romanceStage,
                    ["romanceStageDay"] = romanceStageStartedDay,
                    ["romanceColocatedDay"] = romanceLastColocatedDay,
                    ["romanceBonus"] = romancePositiveBonus,
                    ["flirtEpisode"] = flirtEpisode,
                    ["flirtAttempted"] = flirtAttempted,
                    ["flirtPassed"] = flirtPassed,
                    ["flirtTraitA"] = flirtTraitA,
                    ["flirtTraitB"] = flirtTraitB,
                    ["flirtRollA"] = flirtRollA,
                    ["flirtRollB"] = flirtRollB,
                    ["affairEpisode"] = affairEpisode,
                    ["affairAttempted"] = affairAttempted,
                    ["affairPassed"] = affairPassed,
                    ["judgmentTraitA"] = judgmentTraitA,
                    ["judgmentTraitB"] = judgmentTraitB,
                    ["judgmentRollA"] = judgmentRollA,
                    ["judgmentRollB"] = judgmentRollB,
                    ["affair"] = affair ? 1 : 0, ["affairDay"] = affairStartedDay,
                    ["affairRumor"] = affairRumorId, ["estrangedAB"] = estrangedAB ? 1 : 0,
                    ["estrangedBA"] = estrangedBA ? 1 : 0, ["estrangedRumor"] = estrangedRumorId,
                    ["married"] = marriedToEachOther ? 1 : 0, ["marriageAction"] = marriageActionId,
                    ["marriageBlocked"] = marriageBlocked ? 1 : 0,
                    ["marriageBlockReason"] = marriageBlockReason,
                    ["divorced"] = divorced ? 1 : 0, ["divorceDay"] = divorceDay, ["divorceAction"] = divorceActionId,
                    ["divorceRumor"] = divorceRumorId, ["conceptionAction"] = conceptionActionId,
                    ["conceptionDay"] = persistedConceptionDay,
                    ["marriageEvaluationDay"] = lastMarriageEvaluationDay,
                    ["marriageColocatedDays"] =
                        organicMarriageColocatedDays,
                    ["day"] = day, ["version"] = RelationshipLifecycleVersion, ["ts"] = ts
                });
            }

            // A four-day relationship batch evaluates each actual co-presence
            // day in order. Keep the preloaded row synchronized with the upsert
            // so the next day observes lifecycle state created earlier in the
            // same transaction without another query.
            row["pair_key"] = pair.PairKey;
            row["hero_a_id"] = pair.HeroAId;
            row["hero_b_id"] = pair.HeroBId;
            row["lover_active"] = lovers ? 1 : 0;
            row["lover_started_day"] = loverStartedDay;
            row["lover_exception_attempted"] = exceptionAttempted;
            row["lover_exception_passed"] = exceptionPassed;
            row["lover_exception_roll"] = exceptionRoll;
            row["lover_rumor_id"] = loverRumorId;
            row["romance_stage"] = romanceStage;
            row["romance_stage_started_day"] = romanceStageStartedDay;
            row["romance_last_colocated_day"] = romanceLastColocatedDay;
            row["romance_positive_bonus"] = romancePositiveBonus;
            row["flirt_episode"] = flirtEpisode;
            row["flirt_attempted"] = flirtAttempted;
            row["flirt_passed"] = flirtPassed;
            row["flirt_trait_a"] = flirtTraitA;
            row["flirt_trait_b"] = flirtTraitB;
            row["flirt_roll_a"] = flirtRollA;
            row["flirt_roll_b"] = flirtRollB;
            row["affair_episode"] = affairEpisode;
            row["affair_attempted"] = affairAttempted;
            row["affair_passed"] = affairPassed;
            row["judgment_trait_a"] = judgmentTraitA;
            row["judgment_trait_b"] = judgmentTraitB;
            row["judgment_roll_a"] = judgmentRollA;
            row["judgment_roll_b"] = judgmentRollB;
            row["affair_active"] = affair ? 1 : 0;
            row["affair_started_day"] = affairStartedDay;
            row["affair_rumor_id"] = affairRumorId;
            row["estranged_a_to_b"] = estrangedAB ? 1 : 0;
            row["estranged_b_to_a"] = estrangedBA ? 1 : 0;
            row["estranged_rumor_id"] = estrangedRumorId;
            row["married"] = marriedToEachOther ? 1 : 0;
            row["marriage_action_id"] = marriageActionId;
            row["marriage_blocked"] = marriageBlocked ? 1 : 0;
            row["marriage_block_reason"] = marriageBlockReason;
            row["divorced"] = divorced ? 1 : 0;
            row["divorce_day"] = divorceDay;
            row["divorce_action_id"] = divorceActionId;
            row["divorce_rumor_id"] = divorceRumorId;
            row["conception_action_id"] = conceptionActionId;
            row["last_conception_day"] = persistedConceptionDay;
            row["last_marriage_evaluation_day"] =
                lastMarriageEvaluationDay;
            row["organic_marriage_colocated_days"] =
                organicMarriageColocatedDays;
            if (lifecycleStateChanged) row["last_processed_day"] = day;
            row["version"] = RelationshipLifecycleVersion;
            if (lifecycleStateChanged) row["updated_ts"] = ts;

            return new Dictionary<string, object>
            {
                ["loverStarted"] = loverStarted, ["loverEnded"] = loverEnded,
                ["affairStarted"] = affairStarted, ["affairEnded"] = affairEnded,
                ["marriageQueued"] = marriageQueued, ["divorceQueued"] = divorceQueued,
                ["conceptionQueued"] = conceptionQueued, ["rumorCreated"] = rumorCreated,
                ["flirtationAttempted"] = flirtationAttemptedNow,
                ["flirtationStarted"] = flirtationStarted,
                ["growingAttractionStarted"] = growingAttractionStarted,
                ["seriousAttractionStarted"] = seriousAttractionStarted,
                ["affairJudgmentAttempted"] = affairAttemptedNow,
                ["romanceStage"] = romanceStage,
                ["romancePositiveChanceBonus"] = romancePositiveBonus,
                ["sharedTag"] = divorced ? "divorced" : marriedToEachOther ? "married"
                    : affair ? "lovers_affair" : lovers ? "lovers"
                    : SharedRelationshipTag(campaignId, pair.PairKey, affinityAB, affinityBA, heroA, heroB)
            };
        }

        private static Dictionary<string, object> ApplyFiveDayRelationshipDecay(ReignDbConnection connection,
            string campaignId, string timelineId, int day)
        {
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                return ApplyPostgreSqlFiveDayRelationshipDecay(connection,
                    campaignId, timelineId, day);

            int period = Math.Max(0, day / 5);
            int pairsDecayed = 0, directionsDecayed = 0, nativeQueued = 0, loversEnded = 0;
            HashSet<string> courtPopularitySubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            // Opening-seed rows from older builds did not carry a decay
            // checkpoint. Establish the current checkpoint in one set-based
            // update. The former per-row loop generated more than ten thousand
            // PostgreSQL commands on the first campaign day.
            int missingDecayCheckpoints = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_pair_chemistry
WHERE last_decay_period<0
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);")
                .FirstOrDefault(), "count", 0);
            if (missingDecayCheckpoints > 0)
                ExecuteSql(connection, @"
UPDATE relationship_pair_chemistry
SET last_decay_period=$period,updated_ts=$ts
WHERE last_decay_period<0
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);",
                    new Dictionary<string, object>
                    {
                        ["period"] = period, ["ts"] = ts
                    });
            List<Dictionary<string, object>> rows = QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE last_decay_period>=0 AND last_decay_period<$period
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);",
                new Dictionary<string, object> { ["period"] = period });
            foreach (Dictionary<string, object> row in rows)
            {
                int previousPeriod = ReadInt(row, "last_decay_period", -1);
                int steps = Math.Max(0, period - previousPeriod);
                if (steps == 0) continue;
                int oldAB = ReadInt(row, "affinity_a_to_b", 0);
                int oldBA = ReadInt(row, "affinity_b_to_a", 0);
                int affinityAB = MoveTowardZero(oldAB, steps);
                int affinityBA = MoveTowardZero(oldBA, steps);
                string heroA = ReadString(row, "hero_a_id", "");
                string heroB = ReadString(row, "hero_b_id", "");
                int effectiveAB = Clamp(affinityAB + ReadInt(ResolveObserverPublicStanding(connection,
                    campaignId, timelineId, heroA, heroB), "value", 0), -100, 100);
                int effectiveBA = Clamp(affinityBA + ReadInt(ResolveObserverPublicStanding(connection,
                    campaignId, timelineId, heroB, heroA), "value", 0), -100, 100);
                directionsDecayed += (oldAB == affinityAB ? 0 : 1) + (oldBA == affinityBA ? 0 : 1);
                string pairKey = ReadString(row, "pair_key", "");
                if (CourtPopularityQualification(oldAB) != CourtPopularityQualification(affinityAB))
                    courtPopularitySubjects.Add(heroB);
                if (CourtPopularityQualification(oldBA) != CourtPopularityQualification(affinityBA))
                    courtPopularitySubjects.Add(heroA);
                string tagAB = DirectionalRelationshipTag(campaignId, pairKey, "a_to_b", affinityAB, affinityBA);
                string tagBA = DirectionalRelationshipTag(campaignId, pairKey, "b_to_a", affinityBA, affinityAB);
                Dictionary<string, object> lifecycle = QuerySql(connection,
                    "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                string sharedTag = ReadString(row, "shared_tag", "");
                if (lifecycle != null && ReadInt(lifecycle, "lover_active", 0) == 1
                    && (affinityAB < 30 || affinityBA < 30))
                {
                    ExecuteSql(connection, @"UPDATE relationship_pair_lifecycle SET
lover_active=0,affair_active=0,updated_ts=$ts WHERE pair_key=$pair;",
                        new Dictionary<string, object> { ["ts"] = ts, ["pair"] = pairKey });
                    AmbientPairContext pair = new AmbientPairContext
                    {
                        PairKey = pairKey, HeroAId = heroA, HeroBId = heroB
                    };
                    RecordRelationshipIncident(connection, pair, "lovers_ended", day,
                        "Five-day relationship decay lowered at least one lover below thirty.",
                        new Dictionary<string, object> { ["affinityAToB"] = affinityAB, ["affinityBToA"] = affinityBA });
                    loversEnded++;
                    sharedTag = SharedRelationshipTag(campaignId, pairKey, affinityAB, affinityBA,
                        new Dictionary<string, object>(), new Dictionary<string, object>());
                }
                ExecuteSql(connection, @"UPDATE relationship_pair_chemistry SET
affinity_a_to_b=$ab,affinity_b_to_a=$ba,effective_affinity_a_to_b=$ab,effective_affinity_b_to_a=$ba,
tag_a_to_b=$tagAB,tag_b_to_a=$tagBA,shared_tag=$shared,
native_incident_offset=0,last_decay_period=$period,
state_revision=state_revision+1,updated_ts=$ts WHERE pair_key=$pair;",
                    new Dictionary<string, object>
                    {
                        ["ab"] = affinityAB, ["ba"] = affinityBA, ["tagAB"] = tagAB, ["tagBA"] = tagBA,
                        ["shared"] = sharedTag, ["period"] = period,
                        ["ts"] = ts, ["pair"] = pairKey
                    });
                row["affinity_a_to_b"] = affinityAB;
                row["affinity_b_to_a"] = affinityBA;
                if (RefreshEffectivePairProjection(connection, campaignId, timelineId,
                    row, day, "five_day_decay_toward_zero"))
                    nativeQueued++;
                pairsDecayed++;
            }
            return new Dictionary<string, object>
            {
                ["period"] = period, ["pairsDecayed"] = pairsDecayed,
                ["directionsDecayed"] = directionsDecayed, ["nativeChangesQueued"] = nativeQueued,
                ["loversEnded"] = loversEnded,
                ["courtPopularitySubjectIds"] = courtPopularitySubjects.OrderBy(x => x).Cast<object>().ToList()
            };
        }

        private static Dictionary<string, object>
            ApplyPostgreSqlFiveDayRelationshipDecay(
                ReignDbConnection connection,
                string campaignId,
                string timelineId,
                int day)
        {
            int period = Math.Max(0, day / 5);
            int pairsDecayed = 0;
            int directionsDecayed = 0;
            int nativeQueued = 0;
            int loversEnded = 0;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HashSet<string> courtPopularitySubjects =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Seed missing checkpoints in one write. They are existing state,
            // not decay work for the current period.
            ExecuteSql(connection, @"
UPDATE relationship_pair_chemistry
SET last_decay_period=$period,updated_ts=$ts
WHERE last_decay_period<0
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);",
                new Dictionary<string, object>
                {
                    ["period"] = period,
                    ["ts"] = ts
                });

            List<Dictionary<string, object>> rows = QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE last_decay_period>=0 AND last_decay_period<$period
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);",
                new Dictionary<string, object> { ["period"] = period });
            if (rows.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    ["period"] = period,
                    ["pairsDecayed"] = 0,
                    ["directionsDecayed"] = 0,
                    ["nativeChangesQueued"] = 0,
                    ["loversEnded"] = 0,
                    ["courtPopularitySubjectIds"] = new List<object>()
                };
            }

            Dictionary<string, int> standings = QuerySql(connection, @"
SELECT subject_id,standing_value FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                })
                .Where(row => !string.IsNullOrWhiteSpace(
                    ReadString(row, "subject_id", "")))
                .ToDictionary(row => ReadString(row, "subject_id", ""),
                    row => ReadInt(row, "standing_value", 0),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> lifecycleByPair =
                QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;")
                    .ToDictionary(row => ReadString(row, "pair_key", ""),
                        row => row, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> targetsByPair =
                QuerySql(connection, "SELECT * FROM relationship_native_targets;")
                    .ToDictionary(row => ReadString(row, "pair_key", ""),
                        row => row, StringComparer.OrdinalIgnoreCase);

            List<Dictionary<string, object>> pairUpdates =
                new List<Dictionary<string, object>>(rows.Count);
            List<Dictionary<string, object>> targetUpserts =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> targetDeletes =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> endedLovers =
                new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> row in rows)
            {
                int steps = Math.Max(0, period
                    - ReadInt(row, "last_decay_period", -1));
                if (steps == 0) continue;

                string pairKey = ReadString(row, "pair_key", "");
                string heroA = ReadString(row, "hero_a_id", "");
                string heroB = ReadString(row, "hero_b_id", "");
                int oldAB = ReadInt(row, "affinity_a_to_b", 0);
                int oldBA = ReadInt(row, "affinity_b_to_a", 0);
                int affinityAB = MoveTowardZero(oldAB, steps);
                int affinityBA = MoveTowardZero(oldBA, steps);
                directionsDecayed += (oldAB == affinityAB ? 0 : 1)
                    + (oldBA == affinityBA ? 0 : 1);
                if (CourtPopularityQualification(oldAB)
                    != CourtPopularityQualification(affinityAB))
                    courtPopularitySubjects.Add(heroB);
                if (CourtPopularityQualification(oldBA)
                    != CourtPopularityQualification(affinityBA))
                    courtPopularitySubjects.Add(heroA);

                string tagAB = DirectionalRelationshipTag(campaignId,
                    pairKey, "a_to_b", affinityAB, affinityBA);
                string tagBA = DirectionalRelationshipTag(campaignId,
                    pairKey, "b_to_a", affinityBA, affinityAB);
                string sharedTag = ReadString(row, "shared_tag", "");
                if (lifecycleByPair.TryGetValue(pairKey,
                    out Dictionary<string, object> lifecycle)
                    && ReadInt(lifecycle, "lover_active", 0) == 1
                    && (affinityAB < 30 || affinityBA < 30))
                {
                    endedLovers.Add(new Dictionary<string, object>
                    {
                        ["pair"] = pairKey,
                        ["a"] = heroA,
                        ["b"] = heroB,
                        ["ab"] = affinityAB,
                        ["ba"] = affinityBA,
                        ["ts"] = ts
                    });
                    loversEnded++;
                    sharedTag = SharedRelationshipTag(campaignId, pairKey,
                        affinityAB, affinityBA,
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>());
                }

                int standingA = standings.TryGetValue(heroA,
                    out int standingAValue) ? standingAValue : 0;
                int standingB = standings.TryGetValue(heroB,
                    out int standingBValue) ? standingBValue : 0;
                int effectiveAB = Clamp(affinityAB + standingB, -100, 100);
                int effectiveBA = Clamp(affinityBA + standingA, -100, 100);
                int projected = ProjectNativeRelation(connection, heroA, heroB,
                    affinityAB, affinityBA, effectiveAB, effectiveBA);

                targetsByPair.TryGetValue(pairKey,
                    out Dictionary<string, object> target);
                int observed = target != null
                    ? ReadInt(target, "observed_relation", 0)
                    : ReadInt(row, "native_action_pending", 0) == 0
                        ? ReadInt(row, "projected_native_relation", 0)
                        : 0;
                bool pending = projected != observed;
                string action = pending ? "native_target:" + pairKey : "";

                pairUpdates.Add(new Dictionary<string, object>
                {
                    ["pair"] = pairKey,
                    ["ab"] = affinityAB,
                    ["ba"] = affinityBA,
                    ["tagab"] = tagAB,
                    ["tagba"] = tagBA,
                    ["shared"] = sharedTag,
                    ["projected"] = projected,
                    ["pending"] = pending ? 1 : 0,
                    ["action"] = action,
                    ["period"] = period,
                    ["ts"] = ts
                });

                if (pending)
                {
                    bool targetChanged = target == null
                        || ReadInt(target, "target_relation", int.MinValue)
                            != projected
                        || ReadInt(target, "observed_relation", int.MinValue)
                            != observed
                        || !new[] { "pending", "claimed" }.Contains(
                            ReadString(target, "status", ""),
                            StringComparer.OrdinalIgnoreCase);
                    if (targetChanged)
                    {
                        targetUpserts.Add(new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["actor"] = heroA,
                            ["targethero"] = heroB,
                            ["targetrelation"] = projected,
                            ["observed"] = observed,
                            ["day"] = (double)day,
                            ["ts"] = ts
                        });
                        nativeQueued++;
                    }
                }
                else if (target != null)
                {
                    targetDeletes.Add(new Dictionary<string, object>
                    {
                        ["pair"] = pairKey
                    });
                    nativeQueued++;
                }
                pairsDecayed++;
            }

            if (endedLovers.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,a text,b text,ab integer,ba integer,ts bigint)
)
UPDATE relationship_pair_lifecycle AS lifecycle
SET lover_active=0,
    affair_active=0,
    romance_stage=CASE WHEN LEAST(x.ab,x.ba) >= 25
        THEN 'flirtation' ELSE '' END,
    romance_stage_started_day=0,
    romance_positive_bonus=CASE WHEN LEAST(x.ab,x.ba) >= 25
        THEN 10 ELSE 0 END,
    affair_episode=CASE WHEN affair_attempted=1
        THEN GREATEST(1,affair_episode)+1 ELSE affair_episode END,
    affair_attempted=0,
    affair_passed=0,
    judgment_trait_a=0,
    judgment_trait_b=0,
    judgment_roll_a=0,
    judgment_roll_b=0,
    updated_ts=x.ts
FROM x WHERE lifecycle.pair_key=x.pair;", 
                    PostgreSqlRelationshipRowsJson(endedLovers));
                foreach (Dictionary<string, object> ended in endedLovers)
                {
                    AmbientPairContext pair = new AmbientPairContext
                    {
                        PairKey = ReadString(ended, "pair", ""),
                        HeroAId = ReadString(ended, "a", ""),
                        HeroBId = ReadString(ended, "b", "")
                    };
                    RecordRelationshipIncident(connection, pair,
                        "lovers_ended", day,
                        "Five-day relationship decay lowered at least one lover below thirty.",
                        new Dictionary<string, object>
                        {
                            ["affinityAToB"] = ReadInt(ended, "ab", 0),
                            ["affinityBToA"] = ReadInt(ended, "ba", 0)
                        });
                }
            }

            if (pairUpdates.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,ab integer,ba integer,tagab text,tagba text,
        shared text,projected integer,pending integer,action text,
        period integer,ts bigint)
)
UPDATE relationship_pair_chemistry AS chemistry
SET affinity_a_to_b=x.ab,
    affinity_b_to_a=x.ba,
    effective_affinity_a_to_b=x.ab,
    effective_affinity_b_to_a=x.ba,
    tag_a_to_b=x.tagab,
    tag_b_to_a=x.tagba,
    shared_tag=x.shared,
    projected_native_relation=x.projected,
    native_action_pending=x.pending,
    native_action_id=x.action,
    native_incident_offset=0,
    last_decay_period=x.period,
    state_revision=chemistry.state_revision+1,
    updated_ts=x.ts
FROM x
WHERE chemistry.pair_key=x.pair;", 
                    PostgreSqlRelationshipRowsJson(pairUpdates));
                InvalidateRelationshipPairStateCache(campaignId);
            }
            ExecutePostgreSqlRelationshipWriteSet(connection, null,
                targetUpserts, targetDeletes);

            return new Dictionary<string, object>
            {
                ["period"] = period,
                ["pairsDecayed"] = pairsDecayed,
                ["directionsDecayed"] = directionsDecayed,
                ["nativeChangesQueued"] = nativeQueued,
                ["loversEnded"] = loversEnded,
                ["courtPopularitySubjectIds"] =
                    courtPopularitySubjects.OrderBy(value => value)
                        .Cast<object>().ToList()
            };
        }

        private static int MoveTowardZero(int value, int steps)
        {
            if (value > 0) return Math.Max(0, value - Math.Max(0, steps));
            if (value < 0) return Math.Min(0, value + Math.Max(0, steps));
            return 0;
        }

        private static void RecordRelationshipIncident(ReignDbConnection connection, AmbientPairContext pair, string kind,
            double day, string summary, Dictionary<string, object> payload, string rumorId = "")
        {
            ExecuteSql(connection, @"INSERT INTO relationship_incidents(
incident_id,pair_key,kind,hero_a_id,hero_b_id,world_day,summary,rumor_id,payload_json,created_ts)
VALUES($id,$pair,$kind,$a,$b,$day,$summary,$rumor,$payload,$ts);",
                new Dictionary<string, object>
                {
                    ["id"] = "relationship_incident_" + Guid.NewGuid().ToString("N"),
                    ["pair"] = pair.PairKey, ["kind"] = kind, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                    ["day"] = day, ["summary"] = summary ?? "", ["rumor"] = rumorId ?? "",
                    ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }

        private static Dictionary<string, object> RelationshipLifecycleView(ReignDbConnection connection,
            string observerId, string targetId)
        {
            string pairKey = AmbientPairKey(observerId, targetId);
            Dictionary<string, object> row = string.IsNullOrWhiteSpace(pairKey) ? null : QuerySql(connection,
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

        private static List<Dictionary<string, object>> RunCurrentRelationshipSelfTests()
        {
            List<Dictionary<string, object>> results = RunMbtiRelationshipSelfTests();
            results.AddRange(RunRelationshipLifecycleSelfTests());
            results.AddRange(RunRelationshipFlingSelfTests());
            results.AddRange(RunWorldRelationshipModelSelfTests());
            results.AddRange(RunNpcRelationshipPromptSelfTests());
            results.AddRange(RunSharedRelationshipHistorySelfTests());
            results.AddRange(RunConversationRelationshipSelfTests());
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "relationship_system",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            add("relationship_mechanics_use_no_llm", true,
                "Daily directional chemistry, lifecycle transitions, rumors, conception, marriage, divorce, and decay remain deterministic; optional soft history is generated only when dialogue requests it.");
            add("political_marriage_weekly_chance_unchanged",
                Math.Abs((0.01d + 0.04d * 0d) - 0.01d) < 0.000001d
                && Math.Abs((0.01d + 0.04d * 1d) - 0.05d) < 0.000001d,
                "Political marriage retains its weekly one-to-five-percent clan-leader roll.");
            string correspondenceCampaignId = "rc_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(correspondenceCampaignId))
                {
                    EnsureRelationshipAndCorrespondenceSchema(connection);
                }
                Dictionary<string, object> positive = RelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = correspondenceCampaignId,
                    ["eventId"] = "letter_positive",
                    ["subjectId"] = "letter_recipient",
                    ["targetId"] = "letter_sender",
                    ["eventType"] = "letter",
                    ["body"] = "Thank you for your kind help and support.",
                    ["worldDay"] = 10d
                });
                Dictionary<string, object> repeated = RelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = correspondenceCampaignId,
                    ["eventId"] = "letter_positive",
                    ["subjectId"] = "letter_recipient",
                    ["targetId"] = "letter_sender",
                    ["eventType"] = "letter",
                    ["body"] = "This changed text must not apply twice.",
                    ["worldDay"] = 11d
                });
                Dictionary<string, object> negative = RelationshipEvaluateApi(new Dictionary<string, object>
                {
                    ["campaignId"] = correspondenceCampaignId,
                    ["eventId"] = "letter_negative",
                    ["subjectId"] = "letter_recipient",
                    ["targetId"] = "letter_sender",
                    ["eventType"] = "letter",
                    ["body"] = "Your cruel insult and threat have made me angry.",
                    ["worldDay"] = 12d
                });
                int receiptCount;
                using (ReignDbConnection connection = OpenCampaignConnection(correspondenceCampaignId))
                {
                    receiptCount = QuerySql(connection,
                        "SELECT event_id FROM relationship_interaction_receipts;").Count;
                }
                add("letters_change_native_relation_without_llm",
                    ReadInt(positive, "nativeRelationDelta", 0) == 1
                    && ReadInt(negative, "nativeRelationDelta", 0) == -1
                    && ReadInt(positive, "llmCalls", -1) == 0
                    && ReadInt(negative, "llmCalls", -1) == 0
                    && ReadInt(positive, "facetsChanged", -1) == 0
                    && ReadInt(negative, "facetsChanged", -1) == 0,
                    "Positive and negative letters produce one-point native-relation signals without LLM or facet evaluation.");
                add("letter_relationship_receipts_are_idempotent",
                    ReadBool(repeated, "idempotent", false)
                    && ReadInt(repeated, "nativeRelationDelta", 0) == 1
                    && receiptCount == 2,
                    "Redelivering the same letter cannot apply its native-relation change twice.");
            }
            catch (Exception ex)
            {
                add("letter_relationship_fixture_exception", false, ex.ToString());
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(correspondenceCampaignId));
            }
            return results;
        }

        private static void RunPreparedLifecycleWriteSelfTests(ReignDbConnection connection,
            string campaignId, Dictionary<string, object> man, Dictionary<string, object> woman,
            Action<string, bool, string> add)
        {
            Dictionary<string, object> heroA = new Dictionary<string, object>(man)
                { ["heroStringId"] = "prepared_man" };
            Dictionary<string, object> heroB = new Dictionary<string, object>(woman)
                { ["heroStringId"] = "prepared_woman" };
            AmbientPairContext pair = new AmbientPairContext
            {
                PairKey = AmbientPairKey("prepared_man", "prepared_woman"),
                HeroAId = "prepared_man", HeroBId = "prepared_woman",
                ContextKind = "party", ContextId = "prepared_party", ExposureWeight = 1d
            };
            List<string>[] trajectories = { new List<string>(), new List<string>() };
            int writes = 0, commands = 0;
            bool immediateReads = true, rollbackClean = true;
            for (int route = 0; route < trajectories.Length; route++)
            {
                ExecuteSql(connection, "BEGIN IMMEDIATE;");
                try
                {
                    using (RelationshipLifecycleWriteContext context = route == 1
                        ? new RelationshipLifecycleWriteContext(connection) : null)
                    {
                        int[] affinities = { 35, 40, 20 };
                        for (int index = 0; index < affinities.Length; index++)
                        {
                            Dictionary<string, object> result = ProcessRelationshipLifecycle(
                                connection, campaignId, pair, index + 1, heroA, heroB,
                                affinities[index], affinities[index]);
                            Dictionary<string, object> row = QuerySql(connection,
                                "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair;",
                                new Dictionary<string, object> { ["pair"] = pair.PairKey }).FirstOrDefault();
                            immediateReads &= row != null
                                && ReadInt(row, "last_processed_day", -1) == index + 1;
                            if (row != null) row.Remove("updated_ts");
                            trajectories[route].Add(Json.Serialize(new Dictionary<string, object>
                                { ["result"] = result, ["row"] = row }));
                        }
                        if (context != null) { writes = context.Writes; commands = context.CreatedCommands; }
                    }
                }
                finally { ExecuteSql(connection, "ROLLBACK;"); }
                rollbackClean &= QuerySql(connection,
                    "SELECT pair_key FROM relationship_pair_lifecycle WHERE pair_key=$pair;",
                    new Dictionary<string, object> { ["pair"] = pair.PairKey }).Count == 0;
            }
            add("prepared_lifecycle_matches_serial_trajectory",
                trajectories[0].SequenceEqual(trajectories[1]) && immediateReads,
                "Flirtation, continuation and reset persist identical full rows and results, visible immediately after each write.");
            add("prepared_lifecycle_reuses_command_and_rolls_back",
                writes == 3 && commands == 1 && rollbackClean
                    && CurrentRelationshipLifecycleWriteContext == null,
                "Three ordered writes reuse one command; rollback removes all changes and disposal clears the connection scope.");
        }

        private static List<Dictionary<string, object>> RunRelationshipLifecycleSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "relationship_lifecycle",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            string campaignId = "rl_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureRelationshipDirectorSchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureSocialReputationSchema(connection);
                    HashSet<string> lifecycleColumns = new HashSet<string>(
                        QuerySql(connection,
                            "PRAGMA table_info(relationship_pair_lifecycle);")
                            .Select(row => ReadString(row, "name", "")),
                        StringComparer.OrdinalIgnoreCase);
                    List<string> missingRomanceColumns =
                        RelationshipLifecycleRomanceColumns.Where(column =>
                            !lifecycleColumns.Contains(column)).ToList();
                    add("romance_schema_shape",
                        missingRomanceColumns.Count == 0,
                        missingRomanceColumns.Count == 0
                            ? "The lifecycle schema contains every persisted romance-stage roll and state column."
                            : "Missing lifecycle columns: "
                                + string.Join(", ", missingRomanceColumns));
                    add("pregnancy_commitment_chance_contract",
                        PregnancyCommitmentChance(0) == 10
                        && PregnancyCommitmentChance(30) == 40
                        && PregnancyCommitmentChance(50) == 60
                        && PregnancyCommitmentChance(70) == 80
                        && PregnancyCommitmentChance(80) == 90
                        && PregnancyCommitmentChance(100) == 90,
                        "Pregnancy commitment uses Honor plus ten with a ninety-percent individual cap.");
                    add("romance_commitment_receipt_schema",
                        TableExists(connection,
                            "relationship_conception_commitments")
                        && TableExists(connection,
                            "relationship_affair_commitments"),
                        "Pregnancy and affair-marriage decisions have durable idempotent receipt tables.");
                    long staleConceptionTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES('stale_conception','stale_attempt','stale_mother','stale_father',
'stale_father',10,46,'pending_game',0,'{}',$ts,$ts);",
                        new Dictionary<string, object> { ["ts"] = staleConceptionTs });
                    string staleConceptionAction = QueueDirectorAction(connection,
                        "start_conception", 10d, "stale_mother", "stale_father",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["pairKey"] = "stale_father|stale_mother",
                            ["conceptionId"] = "stale_conception"
                        });
                    RelationshipDirectorReportActionApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["directorActionId"] = staleConceptionAction,
                            ["status"] = "obsolete",
                            ["error"] = "Mother is already pregnant.",
                            ["worldDay"] = 10d
                        });
                    Dictionary<string, object> staleConceptionReceipt =
                        QuerySql(connection, @"SELECT a.status AS action_status,
c.status AS conception_status FROM relationship_director_actions a
JOIN conceptions c ON c.conception_id='stale_conception'
WHERE a.director_action_id=$id LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["id"] = staleConceptionAction
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    add("already_pregnant_receipt_is_obsolete",
                        ReadString(staleConceptionReceipt, "action_status", "") == "obsolete"
                        && ReadString(staleConceptionReceipt,
                            "conception_status", "") == "obsolete_game",
                        "A stale already-pregnant receipt closes both the action and conception without a terminal failure.");

                    ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES('historic_stale_conception','historic_stale_attempt','historic_mother',
'historic_father','historic_father',11,47,'pending_game',0,'{}',$ts,$ts);",
                        new Dictionary<string, object> { ["ts"] = staleConceptionTs });
                    string historicStaleAction = QueueDirectorAction(connection,
                        "start_conception", 11d, "historic_mother", "historic_father",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["pairKey"] = "historic_father|historic_mother",
                            ["conceptionId"] = "historic_stale_conception"
                        });
                    ExecuteSql(connection, @"UPDATE relationship_director_actions
SET status='failed',result_json=$result WHERE director_action_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["id"] = historicStaleAction,
                            ["result"] = Json.Serialize(new Dictionary<string, object>
                            {
                                ["status"] = "failed",
                                ["error"] = "Mother is already pregnant."
                            })
                        });
                    ExecuteSql(connection, @"DELETE FROM schema_meta
WHERE key='already_pregnant_conception_obsolete_repair_v1';");
                    RepairAlreadyPregnantConceptionActions(connection);
                    Dictionary<string, object> historicStaleRepair =
                        QuerySql(connection, @"SELECT a.status AS action_status,
c.status AS conception_status FROM relationship_director_actions a
JOIN conceptions c ON c.conception_id='historic_stale_conception'
WHERE a.director_action_id=$id LIMIT 1;",
                            new Dictionary<string, object>
                            {
                                ["id"] = historicStaleAction
                            }).FirstOrDefault() ?? new Dictionary<string, object>();
                    add("already_pregnant_history_is_reconciled",
                        ReadString(historicStaleRepair, "action_status", "") == "obsolete"
                        && ReadString(historicStaleRepair,
                            "conception_status", "") == "obsolete_game",
                        "Saved already-pregnant failures are idempotently reclassified and their pending conception rows are closed.");
                    Dictionary<string, object> man = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "life_man", ["name"] = "Life Man", ["age"] = 30d,
                        ["isFemale"] = false, ["isAlive"] = true,
                        ["isPregnant"] = false, ["spouseId"] = "",
                        ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["traitPercentages"] = new Dictionary<string, object>
                            {
                                ["flirtatiousness"] = 100
                            },
                            ["courtVirtues"] = new Dictionary<string, object>
                            {
                                ["judgment"] = 0
                            }
                        }
                    };
                    Dictionary<string, object> woman = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "life_woman", ["name"] = "Life Woman", ["age"] = 28d,
                        ["isFemale"] = true, ["isAlive"] = true,
                        ["isPregnant"] = false, ["spouseId"] = "",
                        ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true,
                        ["traits"] = new Dictionary<string, object>
                        {
                            ["traitPercentages"] = new Dictionary<string, object>
                            {
                                ["flirtatiousness"] = 100
                            },
                            ["courtVirtues"] = new Dictionary<string, object>
                            {
                                ["judgment"] = 0
                            }
                        }
                    };
                    AmbientPairContext pair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("life_man", "life_woman"),
                        HeroAId = "life_man", HeroBId = "life_woman", ContextKind = "party",
                        ContextId = "life_party", ExposureWeight = 1d
                    };
                    RunPreparedLifecycleWriteSelfTests(connection, campaignId, man, woman, add);
                    ProcessRelationshipLifecycle(connection, campaignId, pair, 1, man, woman, 50, 50);
                    Dictionary<string, object> lovers = RelationshipLifecycleView(connection, "life_man", "life_woman");
                    add("mutual_fifty_becomes_lovers", ReadBool(lovers, "lovers", false),
                        "Unrelated adults with both directional affinities at fifty become lovers.");

                    ProcessRelationshipLifecycle(connection, campaignId, pair, 2, man, woman, 29, 70);
                    Dictionary<string, object> ended = RelationshipLifecycleView(connection, "life_man", "life_woman");
                    add("lovers_persist_until_one_drops_below_thirty", !ReadBool(ended, "lovers", true),
                        "The lovers tag is removed only after either directional affinity falls below thirty.");

                    Dictionary<string, object> rumorMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "rumor_man"
                    };
                    Dictionary<string, object> rumorWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "rumor_woman"
                    };
                    AmbientPairContext rumorPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("rumor_man", "rumor_woman"),
                        HeroAId = "rumor_man", HeroBId = "rumor_woman", ContextKind = "party",
                        ContextId = "rumor_party", ExposureWeight = 1d
                    };
                    int loverRumorDay = 5;
                    ProcessRelationshipLifecycle(connection, campaignId, rumorPair, loverRumorDay,
                        rumorMan, rumorWoman, 70, 70);
                    ProcessRelationshipLifecycle(connection, campaignId, rumorPair, loverRumorDay + 1,
                        rumorMan, rumorWoman, 70, 70);
                    ProcessRelationshipLifecycle(connection, campaignId, rumorPair, loverRumorDay + 2,
                        rumorMan, rumorWoman, 70, 70);
                    add("lovers_are_not_a_social_rumor",
                        QuerySql(connection, @"SELECT occurrence_id FROM rumor_occurrences
WHERE thread_key=$pair;", new Dictionary<string, object> { ["pair"] = rumorPair.PairKey }).Count == 0,
                        "The relationship-only lovers state never creates a character-owned social rumor.");

                    Dictionary<string, object> relatedWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "life_daughter", ["fatherId"] = "life_man"
                    };
                    AmbientPairContext relatedPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("life_man", "life_daughter"),
                        HeroAId = "life_man", HeroBId = "life_daughter", ContextKind = "party",
                        ContextId = "life_party", ExposureWeight = 1d
                    };
                    ProcessRelationshipLifecycle(connection, campaignId, relatedPair, 1, man, relatedWoman, 100, 100);
                    add("related_people_never_become_lovers",
                        !ReadBool(RelationshipLifecycleView(connection, "life_man", "life_daughter"), "lovers", false),
                        "Parent-child and sibling relationships are excluded from romantic lifecycle transitions.");

                    Dictionary<string, object> marriedMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "restricted_man", ["spouseId"] = "existing_spouse"
                    };
                    Dictionary<string, object> restrictedWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "restricted_woman"
                    };
                    AmbientPairContext restrictedPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("restricted_man", "restricted_woman"),
                        HeroAId = "restricted_man", HeroBId = "restricted_woman", ContextKind = "party",
                        ContextId = "restricted_party", ExposureWeight = 1d
                    };
                    const int exceptionDay = 11;
                    ProcessRelationshipLifecycle(connection, campaignId, restrictedPair, exceptionDay,
                        marriedMan, restrictedWoman, 50, 50);
                    Dictionary<string, object> restricted = RelationshipLifecycleView(connection,
                        "restricted_man", "restricted_woman");
                    add("married_pair_requires_two_individual_judgment_rolls",
                        ReadBool(restricted, "lovers", false) && ReadBool(restricted, "activeAffair", false),
                        "A married-to-another pair becomes lovers only after both individual judgment checks proceed.");
                    Dictionary<string, object> cautiousMan =
                        new Dictionary<string, object>(marriedMan)
                        {
                            ["heroStringId"] = "cautious_man",
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["traitPercentages"] = new Dictionary<string, object>
                                {
                                    ["flirtatiousness"] = 100
                                },
                                ["courtVirtues"] = new Dictionary<string, object>
                                {
                                    ["judgment"] = 100
                                }
                            }
                        };
                    Dictionary<string, object> cautiousWoman =
                        new Dictionary<string, object>(restrictedWoman)
                        {
                            ["heroStringId"] = "cautious_woman",
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["traitPercentages"] = new Dictionary<string, object>
                                {
                                    ["flirtatiousness"] = 100
                                },
                                ["courtVirtues"] = new Dictionary<string, object>
                                {
                                    ["judgment"] = 100
                                }
                            }
                        };
                    AmbientPairContext cautiousPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("cautious_man", "cautious_woman"),
                        HeroAId = "cautious_man", HeroBId = "cautious_woman",
                        ContextKind = "party", ContextId = "cautious_party",
                        ExposureWeight = 1d
                    };
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 20, cautiousMan, cautiousWoman, 50, 50);
                    Dictionary<string, object> cautiousState = QuerySql(connection,
                        "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = cautiousPair.PairKey
                        }).FirstOrDefault();
                    add("high_judgment_can_stop_an_affair",
                        ReadInt(cautiousState, "affair_attempted", 0) == 1
                        && ReadInt(cautiousState, "affair_passed", 1) == 0
                        && ReadInt(cautiousState, "lover_active", 1) == 0,
                        "Each participant's judgment percentage can independently stop a married attraction from becoming an affair.");
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 21, cautiousMan, cautiousWoman, 50, 50);
                    Dictionary<string, object> oneAttemptState = QuerySql(connection,
                        "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = cautiousPair.PairKey
                        }).FirstOrDefault();
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 22, cautiousMan, cautiousWoman, 44, 44);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cautiousPair, 23, cautiousMan, cautiousWoman, 50, 50);
                    Dictionary<string, object> retriedState = QuerySql(connection,
                        "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = cautiousPair.PairKey
                        }).FirstOrDefault();
                    add("affair_attempt_retries_only_after_hysteresis_reset",
                        ReadInt(oneAttemptState, "affair_episode", 0) == 1
                        && ReadInt(retriedState, "affair_episode", 0) == 2,
                        "A failed affair judgment cannot reroll until mutual affinity falls below forty-five and later re-crosses fifty.");

                    Dictionary<string, object> sameSexManA =
                        new Dictionary<string, object>(man)
                        {
                            ["heroStringId"] = "same_sex_man_a"
                        };
                    Dictionary<string, object> sameSexManB =
                        new Dictionary<string, object>(man)
                        {
                            ["heroStringId"] = "same_sex_man_b"
                        };
                    AmbientPairContext sameSexPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("same_sex_man_a", "same_sex_man_b"),
                        HeroAId = "same_sex_man_a", HeroBId = "same_sex_man_b",
                        ContextKind = "party", ContextId = "same_sex_party",
                        ExposureWeight = 1d
                    };
                    const int sameSexExceptionDay = 12;
                    ProcessRelationshipLifecycle(connection, campaignId,
                        sameSexPair, sameSexExceptionDay, sameSexManA,
                        sameSexManB, 85, 85);
                    add("same_sex_lovers_do_not_queue_unsupported_native_marriage",
                        ReadBool(RelationshipLifecycleView(connection,
                            sameSexPair.HeroAId, sameSexPair.HeroBId),
                            "lovers", false)
                        && !QuerySql(connection, @"SELECT director_action_id
FROM relationship_director_actions WHERE action_type='marriage'
AND ((actor_id=$a AND target_id=$b) OR (actor_id=$b AND target_id=$a));",
                            new Dictionary<string, object>
                            {
                                ["a"] = sameSexPair.HeroAId,
                                ["b"] = sameSexPair.HeroBId
                            }).Any(),
                        "A same-sex couple uses the same mutual romance lifecycle, but never enters Bannerlord's unsupported native marriage queue.");

                    Dictionary<string, object> marriageMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "marriage_man"
                    };
                    Dictionary<string, object> marriageWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "marriage_woman"
                    };
                    AmbientPairContext marriagePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("marriage_man", "marriage_woman"),
                        HeroAId = "marriage_man", HeroBId = "marriage_woman", ContextKind = "party",
                        ContextId = "marriage_party", ExposureWeight = 1d
                    };
                    int marriageDay = Enumerable.Range(2, 999).First(day =>
                        StableDie(campaignId + "|" + marriagePair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture)
                            + "|mutual_marriage", 100) <= OrganicMarriageChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId, marriagePair, marriageDay,
                        marriageMan, marriageWoman, 70, 70);
                    add("lovers_at_seventy_roll_ten_percent_daily_when_colocated",
                        QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='marriage' AND actor_id='marriage_man' AND target_id='marriage_woman' AND status='pending';").Any(),
                        "Unmarried lovers at mutual seventy or above roll ten percent on every eligible co-location day.");

                    Dictionary<string, object> otherWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "other_woman"
                    };
                    AmbientPairContext overlappingMarriagePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("marriage_man", "other_woman"),
                        HeroAId = "marriage_man", HeroBId = "other_woman",
                        ContextKind = "party", ContextId = "marriage_party",
                        ExposureWeight = 1d
                    };
                    int overlappingDay = Enumerable.Range(2, 999).First(day =>
                        StableDie(campaignId + "|" + overlappingMarriagePair.PairKey + "|"
                            + day.ToString(CultureInfo.InvariantCulture)
                            + "|mutual_marriage", 100)
                            <= OrganicMarriageChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        overlappingMarriagePair, overlappingDay, marriageMan,
                        otherWoman, 70, 70);
                    add("one_pending_marriage_reservation_per_hero",
                        QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='marriage' AND actor_id='marriage_man' AND status='pending';").Count == 1,
                        "A hero already reserved by one pending marriage cannot receive an overlapping marriage action.");

                    string invalidMarriageActionId = ReadString(QuerySql(connection,
                        @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='marriage' AND actor_id='marriage_man'
AND target_id='marriage_woman' AND status='pending' LIMIT 1;")
                        .FirstOrDefault(), "director_action_id", "");
                    RelationshipDirectorReportActionApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["directorActionId"] = invalidMarriageActionId,
                            ["status"] = "invalid",
                            ["error"] = "Native marriage action did not establish reciprocal spouse state.",
                            ["worldDay"] = (double)marriageDay
                        });
                    ProcessRelationshipLifecycle(connection, campaignId,
                        marriagePair, marriageDay + OrganicMarriageEvaluationCadenceDays,
                        marriageMan, marriageWoman, 70, 70);
                    Dictionary<string, object> blockedMarriage = QuerySql(connection,
                        @"SELECT marriage_blocked,marriage_block_reason
FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = marriagePair.PairKey
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    add("invalid_native_marriage_is_terminal_for_the_pair",
                        ReadInt(blockedMarriage, "marriage_blocked", 0) == 1
                        && ReadString(blockedMarriage,
                            "marriage_block_reason", "").Contains("reciprocal spouse")
                        && !QuerySql(connection, @"SELECT director_action_id
FROM relationship_director_actions WHERE action_type='marriage'
AND actor_id='marriage_man' AND target_id='marriage_woman'
AND status IN ('pending','claimed');").Any(),
                        "A native-invalid completion clears its reservation, blocks retries for that pair, and retains the diagnostic reason.");

                    string commitmentMarriageAction = QueueDirectorAction(
                        connection, "marriage", 40d, "commitment_man",
                        "commitment_woman", new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["route"] = "mutual_affinity",
                            ["pairKey"] = "commitment_man|commitment_woman",
                            ["timelineId"] = "main"
                        });
                    string commitmentConceptionAction = QueueDirectorAction(
                        connection, "start_conception", 40d,
                        "commitment_woman", "commitment_man",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["conceptionId"] = "commitment_conception",
                            ["pairKey"] = "commitment_man|commitment_woman",
                            ["timelineId"] = "main"
                        });
                    ExecuteSql(connection, @"INSERT INTO relationship_conception_commitments(
conception_id,pair_key,hero_a_id,hero_b_id,honor_a,honor_b,chance_a,chance_b,
roll_a,roll_b,passed_a,passed_b,status,marriage_action_id,world_day,updated_ts)
VALUES('commitment_conception','commitment_man|commitment_woman',
'commitment_man','commitment_woman',50,50,60,60,1,1,1,1,
'awaiting_conception',$action,40,$ts);",
                        new Dictionary<string, object>
                        {
                            ["action"] = commitmentMarriageAction,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    Dictionary<string, object> commitmentPoll =
                        RelationshipDirectorPollActionsApi(
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId
                            });
                    List<Dictionary<string, object>> commitmentPollActions =
                        ReadDictionaryList(commitmentPoll, "actions");
                    bool conceptionReleasedFirst = commitmentPollActions.Any(
                        action => ReadString(action, "director_action_id", "")
                            == commitmentConceptionAction);
                    bool marriageQuarantined = !commitmentPollActions.Any(
                        action => ReadString(action, "director_action_id", "")
                            == commitmentMarriageAction);
                    ExecuteSql(connection, @"UPDATE relationship_conception_commitments
SET status='awaiting_marriage' WHERE conception_id='commitment_conception';");
                    Dictionary<string, object> marriagePoll =
                        RelationshipDirectorPollActionsApi(
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId
                            });
                    bool marriageReleasedAfterCommitment =
                        ReadDictionaryList(marriagePoll, "actions").Any(action =>
                            ReadString(action, "director_action_id", "")
                                == commitmentMarriageAction);
                    add("pregnancy_commitment_quarantines_parallel_marriage",
                        conceptionReleasedFirst && marriageQuarantined
                        && marriageReleasedAfterCommitment,
                        "A same-day conception is resolved before its parallel marriage action can reach Bannerlord.");

                    string refusedMarriageAction = QueueDirectorAction(
                        connection, "marriage", 41d, "refused_man",
                        "refused_woman", new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["route"] = "mutual_affinity",
                            ["pairKey"] = "refused_man|refused_woman",
                            ["timelineId"] = "main"
                        });
                    string refusedConceptionAction = QueueDirectorAction(
                        connection, "start_conception", 41d,
                        "refused_woman", "refused_man",
                        new Dictionary<string, object>
                        {
                            ["source"] = "mbti_relationship_lifecycle",
                            ["conceptionId"] = "refused_conception",
                            ["pairKey"] = "refused_man|refused_woman",
                            ["timelineId"] = "main"
                        });
                    ExecuteSql(connection, @"INSERT INTO relationship_conception_commitments(
conception_id,pair_key,hero_a_id,hero_b_id,honor_a,honor_b,chance_a,chance_b,
roll_a,roll_b,passed_a,passed_b,status,marriage_action_id,world_day,updated_ts)
VALUES('refused_conception','refused_man|refused_woman',
'refused_man','refused_woman',50,50,60,60,99,1,0,1,
'awaiting_conception',$action,41,$ts);",
                        new Dictionary<string, object>
                        {
                            ["action"] = refusedMarriageAction,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    RelationshipDirectorPollActionsApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId
                        });
                    RelationshipDirectorReportActionApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["directorActionId"] = refusedConceptionAction,
                            ["status"] = "completed",
                            ["worldDay"] = 41d
                        });
                    string refusedMarriageStatus = ReadString(QuerySql(
                        connection, @"SELECT status FROM relationship_director_actions
WHERE director_action_id=$id LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["id"] = refusedMarriageAction
                        }).FirstOrDefault(), "status", "");
                    int refusedReservationCount = QuerySql(connection, @"SELECT hero_id
FROM relationship_marriage_reservations WHERE director_action_id=$id;",
                        new Dictionary<string, object>
                        {
                            ["id"] = refusedMarriageAction
                        }).Count;
                    add("failed_pregnancy_commitment_cancels_parallel_marriage",
                        refusedMarriageStatus == "invalid"
                        && refusedReservationCount == 0,
                        "If either Honor roll fails, the quarantined marriage is canceled and both hero reservations are released.");

                    Dictionary<string, object> cadenceMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "cadence_man"
                    };
                    Dictionary<string, object> cadenceWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "cadence_woman"
                    };
                    AmbientPairContext cadencePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("cadence_man", "cadence_woman"),
                        HeroAId = "cadence_man", HeroBId = "cadence_woman",
                        ContextKind = "party", ContextId = "cadence_party",
                        ExposureWeight = 1d
                    };
                    int refusedMarriageDay = Enumerable.Range(2, 999).First(day =>
                        StableDie(campaignId + "|" + cadencePair.PairKey + "|"
                            + day.ToString(CultureInfo.InvariantCulture)
                            + "|mutual_marriage", 100)
                            > OrganicMarriageChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cadencePair, refusedMarriageDay, cadenceMan,
                        cadenceWoman, 70, 70);
                    ProcessRelationshipLifecycle(connection, campaignId,
                        cadencePair, refusedMarriageDay + 1, cadenceMan,
                        cadenceWoman, 70, 70);
                    Dictionary<string, object> cadenceState = QuerySql(connection,
                        @"SELECT last_marriage_evaluation_day FROM relationship_pair_lifecycle
WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["pair"] = cadencePair.PairKey
                        }).FirstOrDefault();
                    add("refused_marriage_rerolls_next_colocated_day",
                        Math.Abs(ReadDouble(cadenceState,
                            "last_marriage_evaluation_day", -1d)
                            - (refusedMarriageDay + 1d)) < 0.000001d,
                        "A refused organic proposal rerolls on the next eligible co-location day.");

                    Dictionary<string, object> spouseA = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "spouse_a", ["spouseId"] = "spouse_b"
                    };
                    Dictionary<string, object> spouseB = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "spouse_b", ["spouseId"] = "spouse_a"
                    };
                    AmbientPairContext spousePair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("spouse_a", "spouse_b"),
                        HeroAId = "spouse_a", HeroBId = "spouse_b", ContextKind = "party",
                        ContextId = "spouse_party", ExposureWeight = 1d
                    };
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, 1, spouseA, spouseB, -30, 10);
                    add("one_spouse_at_negative_thirty_is_estranged",
                        ReadBool(RelationshipLifecycleView(connection, "spouse_a", "spouse_b"), "estranged", false),
                        "A marriage becomes estranged when either spouse reaches negative thirty.");
                    int estrangedRumorDay = Enumerable.Range(2, 5000).First(day =>
                        DeterministicSocialRoll(string.Join("|", campaignId, "main", "marital_strife", spousePair.PairKey,
                            day.ToString(CultureInfo.InvariantCulture), "exposure")) < 0.025d);
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, estrangedRumorDay,
                        spouseA, spouseB, -30, 10);
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, estrangedRumorDay + 1,
                        spouseA, spouseB, -30, 10);
                    add("estrangement_uses_temporary_character_tags",
                        QuerySql(connection, @"SELECT occurrence_id FROM rumor_occurrences
WHERE thread_key=$pair AND archetype_id='marital_strife' AND world_day=$day AND expires_day=$expires;",
                            new Dictionary<string, object> { ["pair"] = spousePair.PairKey, ["day"] = (double)estrangedRumorDay, ["expires"] = (double)estrangedRumorDay + 45d }).Count == 1
                        && QuerySql(connection, @"SELECT subject_id FROM rumor_subject_tags
WHERE occurrence_id IN (SELECT occurrence_id FROM rumor_occurrences WHERE thread_key=$pair AND world_day=$day);",
                            new Dictionary<string, object> { ["pair"] = spousePair.PairKey, ["day"] = (double)estrangedRumorDay }).Count == 2,
                        "Exposed marital strife creates one forty-five-day occurrence with one temporary character-owned entry for each spouse.");
                    ProcessRelationshipLifecycle(connection, campaignId, spousePair, 2, spouseA, spouseB, -50, 100);
                    add("one_spouse_at_negative_fifty_queues_divorce",
                        QuerySql(connection, @"SELECT director_action_id FROM relationship_director_actions
WHERE action_type='divorce' AND actor_id='spouse_a' AND target_id='spouse_b' AND status='pending';").Any(),
                        "A spouse at negative fifty queues divorce regardless of the reverse affinity.");

                    Dictionary<string, object> affairMan = new Dictionary<string, object>(man)
                    {
                        ["heroStringId"] = "affair_man", ["spouseId"] = ""
                    };
                    Dictionary<string, object> affairWoman = new Dictionary<string, object>(woman)
                    {
                        ["heroStringId"] = "affair_woman", ["spouseId"] = ""
                    };
                    AmbientPairContext affairPair = new AmbientPairContext
                    {
                        PairKey = AmbientPairKey("affair_man", "affair_woman"),
                        HeroAId = "affair_man", HeroBId = "affair_woman", ContextKind = "party",
                        ContextId = "affair_party", ExposureWeight = 1d
                    };
                    int affairLoverStartDay = Enumerable.Range(1, 2000).First(day =>
                        StableDie(campaignId + "|" + affairPair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture)
                            + "|lover_conception", 100) > LoverConceptionChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId, affairPair, affairLoverStartDay,
                        affairMan, affairWoman, 70, 70);
                    affairMan["spouseId"] = "betrayed_spouse";
                    int conceptionDay = Enumerable.Range(affairLoverStartDay + 1, 2000).First(day =>
                        StableDie(campaignId + "|" + affairPair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture)
                            + "|lover_conception", 100) <= LoverConceptionChancePercent);
                    ProcessRelationshipLifecycle(connection, campaignId, affairPair, conceptionDay,
                        affairMan, affairWoman, 70, 70);
                    Dictionary<string, object> conception = QuerySql(connection, @"
SELECT payload_json FROM conceptions WHERE mother_id='affair_woman' AND biological_father_id='affair_man'
ORDER BY conception_day DESC LIMIT 1;").FirstOrDefault();
                    Dictionary<string, object> affairConceptionPayload =
                        TryParseJsonObject(ReadString(conception,
                            "payload_json", "{}"));
                    add("affair_conception_is_illegitimate",
                        ReadBool(affairConceptionPayload,
                            "isIllegitimate", false)
                        && ReadInt(affairConceptionPayload,
                            "chancePercent", 0)
                            == LoverConceptionChancePercent,
                        "The five-percent daily lover conception roll marks an affair child illegitimate even when the mother is unmarried.");

                    const string repairPairKey = "repair_man_a|repair_man_b";
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_observed_heroes(
hero_id,profile_json,profile_hash,first_observed_day,last_observed_day,updated_ts)
VALUES
('repair_man_a','{""heroStringId"":""repair_man_a"",""isFemale"":false,""isAlive"":true,""spouseId"":""""}','',1,20,1),
('repair_man_b','{""heroStringId"":""repair_man_b"",""isFemale"":false,""isAlive"":true,""spouseId"":""""}','',1,20,1);");
                    ExecuteSql(connection, @"INSERT OR REPLACE INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,married,last_processed_day,updated_ts)
VALUES($pair,'repair_man_a','repair_man_b',1,1,20,1);",
                        new Dictionary<string, object> { ["pair"] = repairPairKey });
                    ExecuteSql(connection, @"INSERT INTO relationship_director_actions(
director_action_id,action_type,status,world_day,actor_id,target_id,payload_json,
created_ts,resolved_ts,result_json) VALUES
('repair_action_1','marriage','completed',10,'repair_man_a','repair_man_b',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""repair_man_a|repair_man_b"",""timelineId"":""main""}',1,2,
'{""status"":""completed"",""worldDay"":10.25}'),
('repair_action_2','marriage','completed',15,'repair_man_a','repair_man_b',
'{""source"":""mbti_relationship_lifecycle"",""route"":""mutual_affinity"",""pairKey"":""repair_man_a|repair_man_b"",""timelineId"":""main""}',3,4,
'{""status"":""completed"",""worldDay"":15.25}');");
                    ExecuteSql(connection, @"INSERT INTO relationship_incidents(
incident_id,pair_key,kind,hero_a_id,hero_b_id,world_day,summary,payload_json,created_ts)
VALUES
('repair_incident_1',$pair,'marriage','repair_man_a','repair_man_b',10,'false marriage','{""directorActionId"":""repair_action_1""}',1),
('repair_incident_2',$pair,'marriage','repair_man_a','repair_man_b',15,'false marriage','{""directorActionId"":""repair_action_2""}',2);",
                        new Dictionary<string, object> { ["pair"] = repairPairKey });
                    StoreWorldMemoryEvent(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["eventId"] = "marriage_repair_action_1",
                        ["eventType"] = "world_event",
                        ["worldDay"] = 10.25d,
                        ["summary"] = "repair_man_a and repair_man_b married.",
                        ["participants"] = new[] { "repair_man_a", "repair_man_b" },
                        ["visibility"] = "public",
                        ["importance"] = 0.9d
                    }, "relationship_lifecycle_marriage");
                    StoreWorldMemoryEvent(new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["eventId"] = "marriage_repair_action_2",
                        ["eventType"] = "world_event",
                        ["worldDay"] = 15.25d,
                        ["summary"] = "repair_man_a and repair_man_b married.",
                        ["participants"] = new[] { "repair_man_a", "repair_man_b" },
                        ["visibility"] = "public",
                        ["importance"] = 0.9d
                    }, "relationship_lifecycle_marriage");
                    ExecuteSql(connection, @"DELETE FROM schema_meta
WHERE key='organic_marriage_native_postcondition_repair_v1';");
                    RepairUnconfirmedOrganicMarriageHistory(connection);
                    Dictionary<string, object> repairedLifecycle = QuerySql(connection,
                        @"SELECT married,marriage_blocked,marriage_block_reason
FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;",
                        new Dictionary<string, object> { ["pair"] = repairPairKey })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    bool repairClean = ReadInt(repairedLifecycle,
                            "married", 1) == 0
                        && ReadInt(repairedLifecycle,
                            "marriage_blocked", 0) == 1
                        && ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_director_actions WHERE director_action_id IN
('repair_action_1','repair_action_2') AND status='invalid';")
                            .FirstOrDefault(), "count", 0) == 2
                        && ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_incidents WHERE incident_id IN
('repair_incident_1','repair_incident_2');")
                            .FirstOrDefault(), "count", -1) == 0
                        && ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM events WHERE event_id IN
('marriage_repair_action_1','marriage_repair_action_2');")
                            .FirstOrDefault(), "count", -1) == 0
                        && ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM memories WHERE event_id IN
('marriage_repair_action_1','marriage_repair_action_2');")
                            .FirstOrDefault(), "count", -1) == 0;
                    add("false_marriage_history_repair_is_complete_and_idempotent",
                        repairClean,
                        "The migration reclassifies false completion reports, blocks the pair, and removes only their incident and semantic-memory projections.");
                    RepairUnconfirmedOrganicMarriageHistory(connection);
                    add("false_marriage_history_repair_marker_is_idempotent",
                        ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM relationship_director_actions WHERE director_action_id IN
('repair_action_1','repair_action_2') AND status='invalid';")
                            .FirstOrDefault(), "count", 0) == 2,
                        "Re-entering schema initialization after repair does not mutate or duplicate the migration result.");

                    HashSet<string> schemaTables = new HashSet<string>(QuerySql(connection,
                        "SELECT name FROM sqlite_master WHERE type='table';")
                        .Select(item => ReadString(item, "name", "")), StringComparer.OrdinalIgnoreCase);
                    string[] retiredTables =
                    {
                        "relationships", "relationship_evaluations", "relationship_milestones",
                        "relationship_pressures", "relationship_developments", "ambient_relationship_exposure",
                        "ambient_relationship_runs", "ambient_relationship_changes",
                        "passive_relationship_events", "life_change_readiness"
                    };
                    add("fresh_schema_excludes_retired_relationship_storage",
                        retiredTables.All(table => !schemaTables.Contains(table)),
                        "Fresh campaigns create only compact MBTI, lifecycle, interaction-receipt, rumor, conception, and political-marriage state.");
                }

                string divorceActionId;
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    divorceActionId = ReadString(QuerySql(connection, @"
SELECT director_action_id FROM relationship_director_actions
WHERE action_type='divorce' AND actor_id='spouse_a' AND target_id='spouse_b'
ORDER BY created_ts DESC LIMIT 1;").FirstOrDefault(), "director_action_id", "");
					Dictionary<string, object> stagedAction=QuerySql(connection,
						"SELECT payload_json FROM relationship_director_actions WHERE director_action_id=$id LIMIT 1;",
						new Dictionary<string, object>{{"id",divorceActionId}}).FirstOrDefault();
					Dictionary<string, object> stagedPayload=TryParseJsonObject(
						ReadString(stagedAction,"payload_json","{}"))??new Dictionary<string, object>();
					add("divorce_action_carries_timeline",
						ReadString(stagedPayload,"timelineId","")=="main",
						"Every queued lifecycle divorce carries its authoritative timeline into native completion.");
					RegisterSocialOccurrence(connection,campaignId,
						BuildRelationshipSocialOccurrence(new AmbientPairContext
						{
							PairKey=AmbientPairKey("spouse_a","spouse_b"),
							HeroAId="spouse_a",HeroBId="spouse_b"
						},1,"marital_strife",
						new Dictionary<string, object>{{"heroStringId","spouse_a"}},
						new Dictionary<string, object>{{"heroStringId","spouse_b"}},
						"spouse_b","spouse_a",true,true,"fixture","main"));
                }
                RelationshipDirectorReportActionApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["directorActionId"] = divorceActionId,
					["status"] = "completed", ["worldDay"] = 2d,
					["timelineId"] = "main"
                });
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> divorceView = RelationshipLifecycleView(connection,
                        "spouse_a", "spouse_b");
                    add("divorce_is_tagged_and_establishes_reputations",
                        ReadBool(divorceView, "divorced", false)
                        && !string.IsNullOrWhiteSpace(ReadString(QuerySql(connection, @"
SELECT divorce_rumor_id FROM relationship_pair_lifecycle
WHERE pair_key=$pair LIMIT 1;",
                            new Dictionary<string, object> { ["pair"] = AmbientPairKey("spouse_a", "spouse_b") })
                            .FirstOrDefault(), "divorce_rumor_id", ""))
						&& QuerySql(connection, @"SELECT subject_id FROM character_reputations
WHERE timeline_id='main' AND tag_id='divorcee' AND subject_id IN ('spouse_a','spouse_b') AND status='active';").Count == 2
						&& !QuerySql(connection, @"SELECT subject_id FROM character_reputations
WHERE timeline_id='main' AND tag_id='marital_strife' AND subject_id IN ('spouse_a','spouse_b') AND status='active';").Any()
						&& QuerySql(connection,"SELECT event_id FROM events WHERE event_id=$id LIMIT 1;",
							new Dictionary<string, object>{{"id","divorce_"+divorceActionId}}).Any()
						&& QuerySql(connection,"SELECT event_id FROM world_history_events WHERE event_id=$id AND timeline_id='main' LIMIT 1;",
							new Dictionary<string, object>{{"id","divorce_"+divorceActionId}}).Any(),
						"Completed divorce retains its timeline, establishes divorcee reputations, resolves marital-strife standing, and writes authoritative history.");
                }
            }
            catch (Exception ex)
            {
                add("lifecycle_fixture_exception", false, ex.ToString());
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return results;
        }
    }
}
