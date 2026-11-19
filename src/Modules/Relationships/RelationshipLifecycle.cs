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
                RelationshipsStore.ExecuteSql(connection, sql, values);
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
                RelationshipsStore.UpsertSchemaMeta14(connection, new Dictionary<string, object>
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
                RelationshipsStore.Pragma2(connection)
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
            RelationshipsSchema.CreateRelationshipPairLifecycle(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairLifecycleLastMarriageEvaluationDay(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairLifecycleOrganicMarriageColocatedDays(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairLifecycleMarriageBlocked(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairLifecycleMarriageBlockReason(connection);
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
                RelationshipsSchema.EnsureDatabaseColumn(connection, "relationship_pair_lifecycle",
                    column.Key, column.Value);
            RelationshipsSchema.CreateIndexRelationshipLifecycleHeroes(connection);
            RelationshipsSchema.CreateRelationshipMarriageReservations(connection);
            RelationshipsSchema.CreateIndexRelationshipMarriageReservationAction(connection);
            RelationshipsSchema.CreateRelationshipConceptionCommitments(connection);
            RelationshipsSchema.CreateRelationshipAffairCommitments(connection);
            if (WorldSimulationService.TableExists(connection, "relationship_director_actions"))
            {
                RelationshipsStore.DeleteRelationshipMarriageReservations4(connection);
                RelationshipsStore.InsertOrIgnoreRelationshipMarriageReservations2(connection);
            }
            RelationshipsSchema.CreateRelationshipIncidents(connection);
            RelationshipsSchema.CreateIndexRelationshipIncidentsPair(connection);
            EnsureRelationshipFlingSchemaCore(connection);
        }

        private static void CompactDormantRelationshipLifecycleRows(ReignDbConnection connection)
        {
            if (ReadString(RelationshipsStore.SelectSchemaMeta8(connection)
                .FirstOrDefault(), "value", "") == "1") return;
            RelationshipsStore.DeleteRelationshipPairLifecycle(connection);
            RelationshipsStore.UpsertSchemaMeta15(connection);
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
            RelationshipsStore.InsertOrIgnoreRelationshipConceptionCommitments(connection, new Dictionary<string, object>
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
            if (RelationshipsStore.SelectRelationshipAffairCommitments2(connection, new Dictionary<string, object>
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
            RelationshipsStore.InsertRelationshipAffairCommitments(connection, new Dictionary<string, object>
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
            Dictionary<string, object> row = string.IsNullOrWhiteSpace(pairKey) ? null : RelationshipsStore.SelectRelationshipPairLifecycle3(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
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
