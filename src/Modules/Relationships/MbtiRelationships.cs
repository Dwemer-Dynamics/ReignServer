using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Reign.Relationships;
using ReignBeta.Shared;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private const int MbtiChemistryVersion = 7;
        private const int LegacyAffinityRebaseCutoffVersion = 6;
        internal const int RelationshipCadenceDays = 1;
        internal const int RelationshipCadenceShardCount = 1;
        private const int OrdinaryRelationshipMagnitudeSides = 15;
        private const int LoverRelationshipMagnitudeSides = 20;
        private const bool AutomaticRelationshipNormalizationEnabled = false;
        private const int OpeningRelationshipSeedVersion = 2;
        private const double OpeningRelationshipSeedScale = 0.75d;
        private const int PostgreSqlRelationshipWriteChunkSize = 5000;
        private const int SqliteRelationshipWriteChunkSize = 500;
        private const int PostgreSqlRelationshipSchemaRevision = 5;
        private static long PostgreSqlRelationshipStagedRows;
        private static long PostgreSqlRelationshipWrittenRows;
        private static long PostgreSqlRelationshipCopyMs;
        private static long PostgreSqlRelationshipMergeMs;
        private static readonly ConcurrentDictionary<string,
            ConcurrentDictionary<string, Dictionary<string, object>>>
            RelationshipPairStateCache =
                new ConcurrentDictionary<string,
                    ConcurrentDictionary<string, Dictionary<string, object>>>(
                        StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string,
            ConcurrentDictionary<string, Dictionary<string, object>>>
            RelationshipPermanentMbtiCache =
                new ConcurrentDictionary<string,
                    ConcurrentDictionary<string, Dictionary<string, object>>>(
                        StringComparer.OrdinalIgnoreCase);
        private static readonly string[] MbtiTypes =
        {
            "INTJ","INTP","ENTJ","ENTP","INFJ","INFP","ENFJ","ENFP",
            "ISTJ","ISFJ","ESTJ","ESFJ","ISTP","ISFP","ESTP","ESFP"
        };

        private static int AdjustedMbtiCompatibility(int baseScore)
        {
            return RelationshipCompatibilityPolicy.AdjustedScore(baseScore);
        }

        internal static void EnsureMbtiRelationshipSchema(ReignDbConnection connection)
        {
            const string marker =
                "postgresql_relationship_schema_revision";
            if (ReignDatabase.IsPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlRelationshipSchemaRevision))
            {
                // Component revisions are independent. A current parent
                // marker must not suppress a newly revised dependent schema,
                // especially after Save Sync restores an older snapshot.
                EnsureRelationshipLifecycleSchema(connection);
                EnsureSharedRelationshipHistorySchema(connection);
                EnsureRelationshipDirectorSchema(connection);
                return;
            }

            EnsureMbtiRelationshipSchemaCore(connection);
            if (!ReignPostgreSqlDialect.IsPostgreSql(connection))
                return;
            EnsureWorldRelationshipSchema(connection);

            // The SQLite-to-PostgreSQL compatibility migration originally
            // generated a second index for several indexes that the explicit
            // relationship schema already owns. Every affinity update then
            // maintained both copies. Remove only the known redundant indexes;
            // the explicit idx_* indexes remain authoritative.
            string[] redundantIndexes =
            {
                "relationship_pair_chemistry_hero_a_id_hero_b_id_idx",
                "relationship_pair_chemistry_hero_b_id_hero_a_id_idx",
                "relationship_pair_chemistry_last_day_idx",
                "relationship_pair_chemistry_processing_shard_last_day_idx",
                "idx_pair_chemistry_day",
                "idx_pair_chemistry_shard",
                "relationship_pair_provenance_campaign_id_timeline_id_source_idx",
                "relationship_pair_provenance_pair_key_active_required_sourc_idx",
                "relationship_native_targets_status_world_day_pair_key_idx",
                "relationship_daily_inputs_timeline_id_status_day_key_idx"
            };
            RelationshipsStore.ExecuteSql(connection, string.Join(" ",
                redundantIndexes.Select(index =>
                    RelationshipsSchema.DropDynamicSql(index))));
            // Frequently updated current-state rows should retain page space for
            // PostgreSQL HOT updates. last_day and processing_shard are diagnostic
            // columns, not selective simulation lookups; removing their indexes
            // lets an ordinary affinity update avoid all secondary-index writes.
            RelationshipsSchema.AlterRelationshipPairChemistry(connection);
            RelationshipsStore.UpsertSchemaMeta5(connection, new Dictionary<string, object>
                {
                    ["revision"] =
                        PostgreSqlRelationshipSchemaRevision.ToString(
                            CultureInfo.InvariantCulture)
                });
            ReignDatabase.MarkPostgreSqlComponentSchemaReady(connection, marker,
                PostgreSqlRelationshipSchemaRevision);
        }

        private static void EnsureMbtiRelationshipSchemaCore(
            ReignDbConnection connection)
        {
            EnsureNotableMbtiSchema(connection);
            RelationshipsSchema.CreateRelationshipPairChemistry(connection);
            RelationshipsSchema.CreateIndexPairChemistryDay(connection);
            RelationshipsSchema.CreateIndexPairChemistryHeroA(connection);
            RelationshipsSchema.CreateIndexPairChemistryHeroB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistrySocialModifierAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistrySocialModifierBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryEffectiveAffinityAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryEffectiveAffinityBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryProcessingShard(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryLastPresenceDay(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryLastBatchSize(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryLastPresenceMask(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedVersion(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedBaseline(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedChanceAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedChanceBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedSignRollAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedSignRollBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedMagnitudeRollAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedMagnitudeRollBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedDeltaAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedDeltaBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedAffinityAToB(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedAffinityBToA(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedEligible(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryOpeningSeedProvenance(connection);
            RelationshipsSchema.CreateIndexPairChemistryShard(connection);
            RelationshipsSchema.CreateRelationshipNativeSyncBatches(connection);
            RelationshipsSchema.CreateIndexNativeSyncBatchDay(connection);
            RelationshipsSchema.CreateRelationshipDailyInputs(connection);
            RelationshipsSchema.CreateIndexRelationshipDailyInputsStatus(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsPairCursor(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsCandidatePairs(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsProcessedPairs(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsStartedTs(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsLastError(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsCadenceShard(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsWindowStartDay(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsWindowDayCount(connection);
            RelationshipsSchema.EnsureColumnRelationshipDailyInputsPairDayEvaluations(connection);
            RelationshipsSchema.CreateRelationshipObservedHeroes(connection);
            RelationshipsSchema.CreateRelationshipHeroObservations(connection);
            RelationshipsSchema.CreateRelationshipPersonalities(connection);
            RelationshipsSchema.CreateRelationshipOpeningSeedRuns(connection);
            RelationshipsSchema.EnsureColumnRelationshipOpeningSeedRunsParallelWorkers(connection);
            RelationshipsSchema.EnsureColumnRelationshipOpeningSeedRunsParallelComputeMs(connection);
            RelationshipsSchema.EnsureColumnRelationshipOpeningSeedRunsPairsPerSecond(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryLastDecayPeriod(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryNativeIncidentOffset(connection);
            RelationshipsSchema.EnsureColumnRelationshipPairChemistryStateRevision(connection);
            EnsureRelationshipLifecycleSchema(connection);
            EnsureSharedRelationshipHistorySchema(connection);
            EnsureRelationshipDirectorSchema(connection);
            RelationshipsSchema.EnsureColumnRelationshipNativeTargetsRequiresObservation(connection);
            CompactNoisyNativeRelationBacklog(connection);
            int version = ReadInt(RelationshipsStore.SelectSchemaMeta5(connection).FirstOrDefault(), "value", 0);
            if (version >= MbtiChemistryVersion) return;

            RelationshipsStore.Savepoint2(connection);
            try
            {
                // The old ambient rows were derived diagnostics and six-facet daily
                // drift, not authoritative history. They are intentionally retired.
                foreach (string retiredTable in new[]
                {
                    "ambient_relationship_changes", "ambient_relationship_exposure", "ambient_relationship_runs"
                })
                {
                    if (RelationshipsStore.SelectSqliteMaster4(connection, new Dictionary<string, object> { ["name"] = retiredTable }).Any())
                    {
                        RelationshipsStore.DeleteDynamic(connection, retiredTable);
                    }
                }
                if (version == 1)
                {
                    // Version 1 incorrectly treated each daily d10 as a slow
                    // accumulator. Preserve that already-rolled history by
                    // applying the remaining signal directly before retiring it.
                    RelationshipsStore.UpdateRelationshipPairChemistry8(connection);
                }
                if (version < 4)
                {
                    // Reign's two directional affinities are authoritative. Older
                    // builds could add a separate native-only incident offset,
                    // making Bannerlord relation disagree with their average.
                    RelationshipsStore.UpdateRelationshipPairChemistry9(connection);
                    if (RelationshipsStore.SelectSqliteMaster5(connection).Any())
                    {
                        RelationshipsStore.UpdateRelationshipDirectorActions3(connection, new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
                    }
                }
                if (version < 5)
                {
                    // Directional chemistry uses the immutable MBTI codes and
                    // calculated scores. The expanded axis objects were write-only
                    // copies repeated once per pair (several megabytes in a normal
                    // campaign), so retain the authoritative codes and remove them.
                    RelationshipsStore.UpdateRelationshipPairChemistry10(connection);
                }
                if (version < 6)
                {
                    // Public Standing is resolved from its subject row at decision
                    // time. Retire copied per-pair modifiers and cached effective
                    // values so later standing revisions require no pair fanout.
                    RelationshipsStore.UpdateRelationshipPairChemistry11(connection);
                }
                // Version 7 widens the immutable MBTI chance range without
                // rewriting relationship affinity or narrative history. Each
                // pair refreshes its stored chance lazily on its next ordinary
                // relationship evaluation.
                RelationshipsStore.UpsertSchemaMeta6(connection, new Dictionary<string, object> { ["version"] = MbtiChemistryVersion.ToString(CultureInfo.InvariantCulture) });
                RelationshipsStore.UpsertSchemaMeta7(connection);
                RelationshipsStore.Release2(connection);
            }
            catch
            {
                try { RelationshipsStore.Rollback3(connection); } catch { }
                try { RelationshipsStore.Release2(connection); } catch { }
                throw;
            }
        }

        private static Dictionary<string, object> PersistAndHydrateRelationshipDailyInput(
            Dictionary<string, object> input)
        {
            string campaignId = ReadString(input, "campaignId", "default");
            string timelineId = ReadString(input, "timelineId", "main");
            double worldDay = ReadDouble(input, "worldDay", 0d);
            int day = (int)Math.Floor(worldDay + 0.000001d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> groups = ReadDictionaryList(input, "presenceGroups");
            List<Dictionary<string, object>> compactHeroes = ReadDictionaryList(input, "heroes");
            List<Dictionary<string, object>> changedHeroes = compactHeroes
                .Select(NormalizeAmbientLifecycleHero).ToList();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                RelationshipsStore.Begin(connection);
                try
                {
                    bool accepted = RelationshipsStore.UpsertRelationshipDailyInputs(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["dayKey"] = day, ["day"] = worldDay,
                        ["groups"] = Json.Serialize(groups), ["heroes"] = Json.Serialize(compactHeroes),
                        ["count"] = groups.Count, ["ts"] = ts
                    }).Count > 0;
                    List<Dictionary<string, object>> observedHeroRows =
                        new List<Dictionary<string, object>>(
                            changedHeroes.Count);
                    foreach (Dictionary<string, object> hero in accepted ? changedHeroes : Enumerable.Empty<Dictionary<string, object>>())
                    {
                        string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
                        if (string.IsNullOrWhiteSpace(heroId)) continue;
                        string profileJson = Json.Serialize(hero);
                        RelationshipHeroDocuments.Remember(profileJson, hero);
                        observedHeroRows.Add(
                            new Dictionary<string, object>
                            {
                                ["hero"] = heroId,
                                ["profile"] = profileJson,
                                ["hash"] = Sha256Hex(profileJson),
                                ["day"] = day,
                                ["ts"] = ts
                            });
                    }
                    if (ReignPostgreSqlDialect.IsPostgreSql(connection)
                        && observedHeroRows.Count > 0)
                    {
                        WriteRelationshipHeroObservations(connection, timelineId, observedHeroRows);
                    }
                    else
                    {
                        foreach (Dictionary<string, object> values
                            in observedHeroRows)
                        {
                            RelationshipsStore.UpsertRelationshipObservedHeroes(connection, values);
                        }
                    }
                    RelationshipsStore.Commit(connection);
                }
                catch
                {
                    try { RelationshipsStore.Rollback(connection); } catch { }
                    throw;
                }
                // The continuous ingestion API only acknowledges durability; its
                // caller never consumes a second database-hydrated copy of the
                // roster. Return the normalized rows already in memory. Missing
                // lifecycle heroes are resolved by the worker from the durable
                // observed-hero table when it actually processes the day.
                List<Dictionary<string, object>> hydratedHeroes = changedHeroes;
                Dictionary<string, object> hydrated =
                    new Dictionary<string, object>(input, StringComparer.OrdinalIgnoreCase)
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["worldDay"] = worldDay,
                        ["presenceGroups"] = groups,
                        ["heroes"] = hydratedHeroes
                    };
                MarkRelationshipCampaignReady(campaignId);
                return hydrated;
            }
        }

        internal static Dictionary<string, object> CampaignOpeningRelationshipSeedApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            Stopwatch timer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string generationId = ReadString(payload, "generationId", "");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            int day = (int)Math.Floor(worldDay + 0.000001d);
            List<Dictionary<string, object>> heroRows = ReadDictionaryList(payload, "heroes");
            List<Dictionary<string, object>> groups = ReadDictionaryList(payload, "presenceGroups");
            Dictionary<string, Dictionary<string, object>> heroes = heroRows
                .Where(row => !string.IsNullOrWhiteSpace(
                    ReadFirstString(row, "heroStringId", "heroId", "id")))
                .GroupBy(row => ReadFirstString(row, "heroStringId", "heroId", "id"),
                    StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, OpeningSeedCandidate> candidates =
                BuildOpeningSeedCandidates(heroes, groups);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            int completed = 0, skipped = 0, failed = 0, missingMbti = 0;
            int positive = 0, negative = 0;
            int openingParallelWorkers = 1;
            long openingParallelComputeMs = 0;
            List<int> openingDeltas = new List<int>();
            Dictionary<string, int> startingBands =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> resultingBands =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                using (RelationshipStandingContext standingContext = new RelationshipStandingContext(connection, campaignId, timelineId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureWorldRelationshipSchema(connection);
                    Dictionary<string, object> priorRun = RelationshipsStore.SelectRelationshipOpeningSeedRuns2(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["version"] = OpeningRelationshipSeedVersion
                        }).FirstOrDefault();
                    bool alreadyComplete = ReadString(priorRun, "status", "")
                        .Equals("completed", StringComparison.OrdinalIgnoreCase);
                    if (alreadyComplete)
                    {
                        timer.Stop();
                        Dictionary<string, object> plan =
                            BuildRelationshipNativeSyncPlan(connection, campaignId,
                                timelineId, day);
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true, ["idempotent"] = true,
                            ["runId"] = "opening_seed_" + campaignId + "_"
                                + timelineId,
                            ["processedPairs"] = ReadInt(priorRun,
                                "completed_pairs", 0),
                            ["changedDirections"] = 0,
                            ["nativeChangesQueued"] = ReadInt(plan,
                                "targetCount", 0),
                            ["durationMs"] = timer.ElapsedMilliseconds,
                            ["nativeSyncPlan"] = plan,
                            ["openingSeedVersion"] =
                                OpeningRelationshipSeedVersion
                        };
                    }
                    RelationshipsStore.UpsertRelationshipOpeningSeedRuns(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["version"] = OpeningRelationshipSeedVersion,
                            ["generation"] = generationId, ["day"] = worldDay,
                            ["candidates"] = candidates.Count, ["ts"] = ts
                        });
                    Dictionary<string, Dictionary<string, object>> existingPairs =
                        RelationshipsStore.SelectRelationshipPairChemistry9(connection)
                        .Where(row => !string.IsNullOrWhiteSpace(
                            ReadString(row, "pair_key", "")))
                        .ToDictionary(row => ReadString(row, "pair_key", ""),
                            row => row, StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, Dictionary<string, object>>
                        existingNativeTargets = RelationshipsStore.SelectRelationshipNativeTargets4(connection)
                        .Where(row => !string.IsNullOrWhiteSpace(
                            ReadString(row, "pair_key", "")))
                        .ToDictionary(row => ReadString(row, "pair_key", ""),
                            row => row, StringComparer.OrdinalIgnoreCase);
                    using (ReignDbCommand openingPairUpsert =
                        CreateOpeningRelationshipPairUpsertCommand(connection))
                    using (ReignDbCommand provenanceUpsert =
                        CreateOpeningRelationshipProvenanceUpsertCommand(connection))
                    using (ReignDbCommand nativeTargetUpsert =
                        CreateRelationshipNativeTargetUpsertCommand(connection))
                    using (ReignDbCommand nativeTargetDelete =
                        CreatePreparedRelationshipCommand(connection,
                            RelationshipsStore.DeleteRelationshipNativeTargetsSql2,
                            "$pair"))
                    {
                        bool usePostgreSqlBulk =
                            ReignPostgreSqlDialect.IsPostgreSql(connection);
                        List<Dictionary<string, object>> openingPairRows =
                            new List<Dictionary<string, object>>();
                        List<Dictionary<string, object>> provenanceRows =
                            new List<Dictionary<string, object>>();
                        List<Dictionary<string, object>> nativeTargetRows =
                            new List<Dictionary<string, object>>();
                        List<string> nativeTargetDeletes = new List<string>();
                        RelationshipsStore.Begin(connection);
                        try
                        {
                            standingContext.Prefetch(heroes.Keys);
                            Dictionary<string, string> openingMbtiByHero =
                                RelationshipsStore.SelectRelationshipPersonalities5(connection)
                                .Where(row => !string.IsNullOrWhiteSpace(
                                        ReadString(row, "hero_id", ""))
                                    && MbtiDefinitions.ContainsKey(
                                        ReadString(row, "mbti_type", "")))
                                .GroupBy(row => ReadString(row, "hero_id", ""),
                                    StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(group => group.Key,
                                    group => ReadString(group.OrderBy(row =>
                                            ReadInt(row, "priority", 99))
                                        .First(), "mbti_type", "XXXX"),
                                    StringComparer.OrdinalIgnoreCase);
                            foreach (string heroId in candidates.Values
                                .SelectMany(candidate => new[]
                                {
                                    candidate.HeroAId, candidate.HeroBId
                                })
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .OrderBy(value => value,
                                    StringComparer.OrdinalIgnoreCase))
                            {
                                Dictionary<string, object> openingHero =
                                    heroes[heroId];
                                string resolvedType;
                                if (ReadBool(openingHero, "isNotable", false))
                                {
                                    // A generic relationship-personality row
                                    // can be observed before the campaign's
                                    // seeded notable assignment exists. Never
                                    // let that provisional value become the
                                    // opening pair's permanent compatibility.
                                    Dictionary<string, object> notableProfile =
                                        EnsureNotableMbtiProfile(connection,
                                            campaignId, day, openingHero, false,
                                            true);
                                    resolvedType = ReadString(notableProfile,
                                        "type", "XXXX");
                                }
                                else
                                {
                                    resolvedType = openingMbtiByHero.TryGetValue(
                                            heroId, out string persistedType)
                                        ? persistedType
                                        : ResolveOpeningSeedMbti(connection,
                                            campaignId, day, openingHero);
                                }
                                openingMbtiByHero[heroId] = resolvedType;
                                if (!MbtiDefinitions.ContainsKey(resolvedType))
                                {
                                    missingMbti++;
                                    throw new InvalidOperationException(
                                        "Campaign opening relationship preparation is missing an immutable MBTI assignment for "
                                        + heroId + " (" + resolvedType + ").");
                                }
                            }
                            List<OpeningSeedCandidate> orderedCandidates =
                                candidates.Values.OrderBy(value => value.PairKey,
                                    StringComparer.OrdinalIgnoreCase).ToList();
                            OpeningSeedComputation[] openingComputations =
                                new OpeningSeedComputation[orderedCandidates.Count];
                            openingParallelWorkers = RelationshipParallelismForWork(
                                orderedCandidates.Count, true);
                            Stopwatch openingComputeTimer = Stopwatch.StartNew();
                            Action<int> calculateOpeningSeed = index =>
                            {
                                OpeningSeedCandidate value =
                                    orderedCandidates[index];
                                Dictionary<string, object> valueHeroA =
                                    heroes[value.HeroAId];
                                Dictionary<string, object> valueHeroB =
                                    heroes[value.HeroBId];
                                string valueTypeA =
                                    openingMbtiByHero[value.HeroAId];
                                string valueTypeB =
                                    openingMbtiByHero[value.HeroBId];
                                int valueChanceAB = AdjustedMbtiCompatibility(
                                    MbtiCompatibility(valueTypeA, valueTypeB));
                                int valueChanceBA = AdjustedMbtiCompatibility(
                                    MbtiCompatibility(valueTypeB, valueTypeA));
                                int valueBaseline = OpeningSeedBaseline(
                                    valueHeroA, valueHeroB, value);
                                bool valueEligible =
                                    OpeningSeedHeroEligible(valueHeroA)
                                    && OpeningSeedHeroEligible(valueHeroB);
                                OpeningSeedComputation computed =
                                    new OpeningSeedComputation
                                    {
                                        TypeA = valueTypeA,
                                        TypeB = valueTypeB,
                                        ChanceAB = valueChanceAB,
                                        ChanceBA = valueChanceBA,
                                        Baseline = valueBaseline,
                                        Eligible = valueEligible
                                    };
                                if (valueEligible)
                                {
                                    computed.SignAB = OpeningSeedDie(campaignId,
                                        value.PairKey, "a_to_b", "sign");
                                    computed.SignBA = OpeningSeedDie(campaignId,
                                        value.PairKey, "b_to_a", "sign");
                                    computed.MagnitudeAB = OpeningSeedDie(
                                        campaignId, value.PairKey, "a_to_b",
                                        "magnitude");
                                    computed.MagnitudeBA = OpeningSeedDie(
                                        campaignId, value.PairKey, "b_to_a",
                                        "magnitude");
                                    computed.DeltaAB = OpeningRelationshipDelta(
                                        valueChanceAB, computed.SignAB,
                                        computed.MagnitudeAB);
                                    computed.DeltaBA = OpeningRelationshipDelta(
                                        valueChanceBA, computed.SignBA,
                                        computed.MagnitudeBA);
                                }
                                openingComputations[index] = computed;
                            };
                            if (openingParallelWorkers > 1)
                            {
                                Parallel.For(0, orderedCandidates.Count,
                                    new ParallelOptions
                                    {
                                        MaxDegreeOfParallelism =
                                            openingParallelWorkers
                                    }, calculateOpeningSeed);
                            }
                            else
                            {
                                for (int index = 0;
                                    index < orderedCandidates.Count; index++)
                                    calculateOpeningSeed(index);
                            }
                            openingComputeTimer.Stop();
                            openingParallelComputeMs =
                                openingComputeTimer.ElapsedMilliseconds;
                            RecordRelationshipParallelStage(
                                openingParallelWorkers,
                                orderedCandidates.Count,
                                openingComputeTimer.ElapsedMilliseconds);
                            for (int candidateIndex = 0;
                                candidateIndex < orderedCandidates.Count;
                                candidateIndex++)
                            {
                                OpeningSeedCandidate candidate =
                                    orderedCandidates[candidateIndex];
                                OpeningSeedComputation opening =
                                    openingComputations[candidateIndex];
                                existingPairs.TryGetValue(candidate.PairKey,
                                    out Dictionary<string, object> existing);
                                if (ReadInt(existing, "opening_seed_version", 0)
                                    >= OpeningRelationshipSeedVersion)
                                {
                                    skipped++;
                                    continue;
                                }
                                string typeA = opening.TypeA;
                                string typeB = opening.TypeB;
                                int chanceAB = opening.ChanceAB;
                                int chanceBA = opening.ChanceBA;
                                int baseline = opening.Baseline;
                                bool seedEligible = opening.Eligible;
                                int signAB = opening.SignAB;
                                int signBA = opening.SignBA;
                                int magnitudeAB = opening.MagnitudeAB;
                                int magnitudeBA = opening.MagnitudeBA;
                                int deltaAB = opening.DeltaAB;
                                int deltaBA = opening.DeltaBA;
                                if (seedEligible)
                                {
                                    positive += (deltaAB > 0 ? 1 : 0)
                                        + (deltaBA > 0 ? 1 : 0);
                                    negative += (deltaAB < 0 ? 1 : 0)
                                        + (deltaBA < 0 ? 1 : 0);
                                }
                                int affinityAB = Clamp(baseline + deltaAB, -100, 100);
                                int affinityBA = Clamp(baseline + deltaBA, -100, 100);
                                openingDeltas.Add(deltaAB);
                                openingDeltas.Add(deltaBA);
                                IncrementOpeningSeedBand(startingBands,
                                    RelationshipBand(baseline));
                                IncrementOpeningSeedBand(startingBands,
                                    RelationshipBand(baseline));
                                IncrementOpeningSeedBand(resultingBands,
                                    RelationshipBand(affinityAB));
                                IncrementOpeningSeedBand(resultingBands,
                                    RelationshipBand(affinityBA));
                                string provenance = string.Join(",",
                                    candidate.Provenance.OrderBy(value => value,
                                        StringComparer.OrdinalIgnoreCase));
                                int standingA = ReadInt(ResolveObserverPublicStanding(connection,
                                    campaignId, timelineId, candidate.HeroBId, candidate.HeroAId), "value", 0);
                                int standingB = ReadInt(ResolveObserverPublicStanding(connection,
                                    campaignId, timelineId, candidate.HeroAId, candidate.HeroBId), "value", 0);
                                int effectiveAB = Clamp(affinityAB + standingB,
                                    -100, 100);
                                int effectiveBA = Clamp(affinityBA + standingA,
                                    -100, 100);
                                int projected = ProjectNativeRelation(connection,
                                    candidate.HeroAId, candidate.HeroBId, affinityAB,
                                    affinityBA, effectiveAB, effectiveBA);
                                existingNativeTargets.TryGetValue(candidate.PairKey,
                                    out Dictionary<string, object> existingTarget);
                                int observed = existingTarget != null
                                    ? ReadInt(existingTarget, "observed_relation", 0)
                                    : existing != null
                                        && ReadInt(existing,
                                            "native_action_pending", 0) == 0
                                            ? ReadInt(existing,
                                                "projected_native_relation", 0)
                                            : 0;
                                bool pending = projected != observed;
                                Dictionary<string, object> openingPairParameters =
                                    OpeningRelationshipPairParameters(campaignId,
                                        candidate, typeA, typeB, chanceAB,
                                        chanceBA, affinityAB, affinityBA,
                                        standingA, standingB, effectiveAB,
                                        effectiveBA, projected, pending, day,
                                        seedEligible, baseline, signAB, signBA,
                                        magnitudeAB, magnitudeBA, deltaAB,
                                        deltaBA, provenance, ts);
                                if (usePostgreSqlBulk)
                                    openingPairRows.Add(openingPairParameters);
                                else
                                    ExecutePreparedRelationshipCommand(
                                        openingPairUpsert,
                                        openingPairParameters);
                                foreach (string source in candidate.Provenance)
                                {
                                    Dictionary<string, object>
                                        provenanceParameters =
                                            new Dictionary<string, object>
                                        {
                                            ["campaign"] = campaignId,
                                            ["timeline"] = timelineId,
                                            ["pair"] = candidate.PairKey,
                                            ["source"] = source,
                                            ["required"] = source == "family"
                                                || source == "marriage"
                                                || source == "kingdom_leadership"
                                                || source == "ruler_network" ? 1 : 0,
                                            ["day"] = worldDay,
                                            ["details"] = Json.Serialize(
                                                new Dictionary<string, object>
                                                {
                                                    ["openingSeedVersion"] =
                                                        OpeningRelationshipSeedVersion
                                                }),
                                            ["ts"] = ts
                                        };
                                    if (usePostgreSqlBulk)
                                        provenanceRows.Add(provenanceParameters);
                                    else
                                        ExecutePreparedRelationshipCommand(
                                            provenanceUpsert,
                                            provenanceParameters);
                                }
                                if (pending)
                                {
                                    Dictionary<string, object>
                                        nativeTargetParameters =
                                            new Dictionary<string, object>
                                        {
                                            ["pair"] = candidate.PairKey,
                                            ["actor"] = candidate.HeroAId,
                                            ["targetHero"] = candidate.HeroBId,
                                            ["targetRelation"] = projected,
                                            ["observed"] = observed,
                                            ["day"] = day,
                                            ["ts"] = ts
                                        };
                                    if (usePostgreSqlBulk)
                                        nativeTargetRows.Add(
                                            nativeTargetParameters);
                                    else
                                        QueueMbtiNativeRelationAction(connection,
                                            new AmbientPairContext
                                            {
                                                PairKey = candidate.PairKey,
                                                HeroAId = candidate.HeroAId,
                                                HeroBId = candidate.HeroBId
                                            }, day, projected, observed,
                                            nativeTargetUpsert);
                                }
                                else if (existingTarget != null)
                                {
                                    if (usePostgreSqlBulk)
                                        nativeTargetDeletes.Add(candidate.PairKey);
                                    else
                                        ExecutePreparedRelationshipCommand(
                                            nativeTargetDelete,
                                            new Dictionary<string, object>
                                            {
                                                ["pair"] = candidate.PairKey
                                            });
                                }
                                completed++;
                            }
                            if (usePostgreSqlBulk)
                                ExecuteOpeningRelationshipPostgreSqlBulk(
                                    connection, openingPairRows, provenanceRows,
                                    nativeTargetRows, nativeTargetDeletes);
                            RelationshipsStore.Commit(connection);
                            if (usePostgreSqlBulk)
                                InstallOpeningRelationshipPairStateCache(
                                    campaignId, existingPairs, openingPairRows);
                            else
                                InvalidateRelationshipPairStateCache(campaignId);
                        }
                        catch
                        {
                            try { RelationshipsStore.Rollback(connection); } catch { }
                            throw;
                        }
                    }
                    Dictionary<string, object> openingRunParameters =
                        OpeningSeedRunParameters(campaignId, timelineId,
                            candidates, completed, skipped, failed, positive,
                            negative, missingMbti, timer.ElapsedMilliseconds,
                            "", ts, openingDeltas, startingBands,
                            resultingBands);
                    openingRunParameters["parallelWorkers"] =
                        openingParallelWorkers;
                    openingRunParameters["parallelComputeMs"] =
                        openingParallelComputeMs;
                    RelationshipsStore.UpdateRelationshipOpeningSeedRuns(connection, openingRunParameters);
                }

                Dictionary<string, object> dailyPayload =
                    new Dictionary<string, object>(payload,
                        StringComparer.OrdinalIgnoreCase)
                    {
                        ["cadenceShard"] = ((day % RelationshipCadenceShardCount)
                            + RelationshipCadenceShardCount)
                            % RelationshipCadenceShardCount
                    };
                Dictionary<string, object> dailyResult =
                    PersistAndHydrateRelationshipDailyInput(dailyPayload);
                bool suppressVerificationWorker =
                    ReadBool(payload, "verificationSuppressWorkerSignal",
                        false)
                    && campaignId.StartsWith("opening_seed_",
                        StringComparison.OrdinalIgnoreCase);
                if (!suppressVerificationWorker)
                {
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        RelationshipsStore.UpdateRelationshipDailyInputs6(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId,
                                ["timeline"] = timelineId,
                                ["day"] = day
                            });
                    }
                }
                dailyResult = new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["accepted"] = true,
                    ["durable"] = true,
                    ["acceptedInputDays"] = new List<int> { day },
                    ["acceptedInputCount"] = 1,
                    ["oldestPendingInputDay"] =
                        OldestPendingRelationshipInputDay(campaignId,
                            timelineId),
                    ["processingMode"] = suppressVerificationWorker
                        ? "verification_deferred"
                        : "continuous_worker_after_readiness_seal",
                    ["nativeSyncPlan"] = null
                };
                if (!ReadBool(dailyResult, "ok", false))
                    throw new InvalidOperationException(ReadString(dailyResult,
                        "error", "Opening-day relationship ingestion failed."));
                timer.Stop();
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    RelationshipsStore.UpdateRelationshipOpeningSeedRuns2(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["version"] = OpeningRelationshipSeedVersion,
                            ["duration"] = timer.ElapsedMilliseconds,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                    Dictionary<string, object> nativePlan =
                        BuildRelationshipNativeSyncPlan(connection, campaignId,
                            timelineId, day);
                    dailyResult["nativeSyncPlan"] = nativePlan;
                    dailyResult["nativeChangesQueued"] =
                        ReadInt(nativePlan, "targetCount", 0);
                }
                dailyResult["openingSeedVersion"] = OpeningRelationshipSeedVersion;
                dailyResult["openingCandidatePairs"] = candidates.Count;
                dailyResult["openingCompletedPairs"] = completed;
                dailyResult["openingSkippedPairs"] = skipped;
                dailyResult["openingPositiveDirections"] = positive;
                dailyResult["openingNegativeDirections"] = negative;
                dailyResult["durationMs"] = timer.ElapsedMilliseconds;
                return dailyResult;
            }
            catch (Exception ex)
            {
                timer.Stop();
                failed = Math.Max(1, failed);
                try
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureMbtiRelationshipSchema(connection);
                        RelationshipsStore.UpdateRelationshipOpeningSeedRuns3(connection, new Dictionary<string, object>
                            {
                                ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["version"] = OpeningRelationshipSeedVersion,
                                ["failed"] = failed, ["missing"] = missingMbti,
                                ["duration"] = timer.ElapsedMilliseconds,
                                ["error"] = DialogueService.LimitText(ex.Message, 1000),
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                    }
                }
                catch { }
                return new Dictionary<string, object>
                {
                    ["ok"] = false, ["retryable"] = true,
                    ["error"] = ex.Message, ["failedPairs"] = failed,
                    ["missingMbti"] = missingMbti
                };
            }
        }

        private static Dictionary<string, object> OpeningSeedRunParameters(
            string campaignId, string timelineId,
            Dictionary<string, OpeningSeedCandidate> candidates, int completed,
            int skipped, int failed, int positive, int negative, int missing,
            long duration, string error, long ts, List<int> deltas,
            Dictionary<string, int> startingBands,
            Dictionary<string, int> resultingBands)
        {
            List<int> ordered = (deltas ?? new List<int>()).OrderBy(value => value)
                .ToList();
            double median = ordered.Count == 0 ? 0d
                : ordered.Count % 2 == 1 ? ordered[ordered.Count / 2]
                : (ordered[ordered.Count / 2 - 1]
                    + ordered[ordered.Count / 2]) / 2d;
            return new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["version"] = OpeningRelationshipSeedVersion,
                ["completed"] = completed, ["skipped"] = skipped,
                ["failed"] = failed,
                ["coLocation"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("co_presence")),
                ["spouses"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("marriage")),
                ["family"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("family")),
                ["leadership"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("kingdom_leadership")),
                ["rulers"] = candidates.Values.Count(value =>
                    value.Provenance.Contains("ruler_network")),
                ["positive"] = positive, ["negative"] = negative,
                ["missing"] = missing, ["duration"] = duration,
                ["average"] = ordered.Count == 0 ? 0d : ordered.Average(),
                ["median"] = median,
                ["minimum"] = ordered.Count == 0 ? 0 : ordered.First(),
                ["maximum"] = ordered.Count == 0 ? 0 : ordered.Last(),
                ["startingBands"] = Json.Serialize(startingBands
                    ?? new Dictionary<string, int>()),
                ["resultingBands"] = Json.Serialize(resultingBands
                    ?? new Dictionary<string, int>()),
                ["error"] = error, ["ts"] = ts
            };
        }

        private static void IncrementOpeningSeedBand(
            Dictionary<string, int> bands, string band)
        {
            if (bands.TryGetValue(band, out int count)) bands[band] = count + 1;
            else bands[band] = 1;
        }

        private static string ResolveOpeningSeedMbti(ReignDbConnection connection,
            string campaignId, int day, Dictionary<string, object> hero)
        {
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            Dictionary<string, object> stored = RelationshipsStore.SelectRelationshipPersonalities6(connection, new Dictionary<string, object> { ["hero"] = heroId }).FirstOrDefault();
            string type = ReadString(stored, "mbti_type", "");
            if (MbtiDefinitions.ContainsKey(type)) return type;
            Dictionary<string, object> resolved = ResolvePermanentRelationshipMbti(
                connection, campaignId, day, hero,
                new Dictionary<string, Dictionary<string, object>>());
            return ReadString(resolved, "type", "XXXX");
        }

        private static int OpeningSeedDie(string campaignId, string pairKey,
            string direction, string kind)
        {
            return StableDie((campaignId ?? "default") + "|" + pairKey + "|"
                + direction + "|" + kind + "|opening_relationship_seed_v"
                + OpeningRelationshipSeedVersion.ToString(CultureInfo.InvariantCulture),
                100);
        }

        private static int OpeningRelationshipDelta(int chance, int signRoll,
            int magnitudeRoll)
        {
            int scale = signRoll <= chance ? chance : 100 - chance;
            int magnitude = Math.Max(1, RoundAwayFromZero(
                scale * magnitudeRoll / 100d
                    * OpeningRelationshipSeedScale));
            return signRoll <= chance ? magnitude : -magnitude;
        }

        private static bool OpeningSeedHeroEligible(Dictionary<string, object> hero)
        {
            return ReadBool(hero, "isAlive", false)
                && ReadBool(hero, "isAdult", ReadDouble(hero, "age", 0d) >= 18d)
                && !ReadBool(hero, "isPlayer", false)
                && !ReadBool(hero, "isPrisoner", false);
        }

        private static int OpeningSeedBaseline(Dictionary<string, object> heroA,
            Dictionary<string, object> heroB, OpeningSeedCandidate candidate)
        {
            if (candidate.Provenance.Contains("marriage")) return 50;
            if (candidate.Provenance.Contains("family")) return 20;
            bool sameKingdom = !string.IsNullOrWhiteSpace(
                    ReadString(heroA, "kingdomId", ""))
                && ReadString(heroA, "kingdomId", "").Equals(
                    ReadString(heroB, "kingdomId", ""),
                    StringComparison.OrdinalIgnoreCase);
            bool rulerLeader = sameKingdom
                && (ReadBool(heroA, "isRuler", false)
                    && ReadBool(heroB, "isClanLeader", false)
                    || ReadBool(heroB, "isRuler", false)
                    && ReadBool(heroA, "isClanLeader", false));
            return rulerLeader ? 10 : 0;
        }

        private static Dictionary<string, OpeningSeedCandidate>
            BuildOpeningSeedCandidates(
                Dictionary<string, Dictionary<string, object>> heroes,
                List<Dictionary<string, object>> groups)
        {
            Dictionary<string, OpeningSeedCandidate> result =
                new Dictionary<string, OpeningSeedCandidate>(
                    StringComparer.OrdinalIgnoreCase);
            Action<string, string, string> add = (first, second, source) =>
            {
                if (string.IsNullOrWhiteSpace(first)
                    || string.IsNullOrWhiteSpace(second)
                    || first.Equals(second, StringComparison.OrdinalIgnoreCase)
                    || !heroes.ContainsKey(first) || !heroes.ContainsKey(second))
                    return;
                string key = AmbientPairKey(first, second);
                if (!result.TryGetValue(key, out OpeningSeedCandidate candidate))
                {
                    bool firstIsA = key.StartsWith(first + "|",
                        StringComparison.OrdinalIgnoreCase);
                    candidate = new OpeningSeedCandidate
                    {
                        PairKey = key,
                        HeroAId = firstIsA ? first : second,
                        HeroBId = firstIsA ? second : first
                    };
                    result[key] = candidate;
                }
                candidate.Provenance.Add(source);
            };
            foreach (Dictionary<string, object> group in groups)
            {
                List<string> ids = ReadStringList(group, "heroIds")
                    .Where(id => heroes.TryGetValue(id,
                        out Dictionary<string, object> hero)
                        && OpeningSeedHeroEligible(hero))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
                for (int first = 0; first < ids.Count; first++)
                    for (int second = first + 1; second < ids.Count; second++)
                        add(ids[first], ids[second], "co_presence");
            }
            foreach (Dictionary<string, object> hero in heroes.Values)
            {
                string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
                add(heroId, ReadString(hero, "spouseId", ""), "marriage");
                add(heroId, ReadString(hero, "fatherId", ""), "family");
                add(heroId, ReadString(hero, "motherId", ""), "family");
            }
            foreach (IGrouping<string, Dictionary<string, object>> siblings in heroes.Values
                .SelectMany(hero => new[]
                {
                    new { Parent = ReadString(hero, "fatherId", ""), Hero = hero },
                    new { Parent = ReadString(hero, "motherId", ""), Hero = hero }
                }).Where(item => !string.IsNullOrWhiteSpace(item.Parent))
                .GroupBy(item => item.Parent, item => item.Hero,
                    StringComparer.OrdinalIgnoreCase))
            {
                List<string> ids = siblings.Select(hero => ReadFirstString(hero,
                        "heroStringId", "heroId", "id"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (int first = 0; first < ids.Count; first++)
                    for (int second = first + 1; second < ids.Count; second++)
                        add(ids[first], ids[second], "family");
            }
            List<Dictionary<string, object>> leaders = heroes.Values.Where(hero =>
                OpeningSeedHeroEligible(hero)
                && (ReadBool(hero, "isClanLeader", false)
                    || ReadBool(hero, "isRuler", false))).ToList();
            foreach (IGrouping<string, Dictionary<string, object>> kingdom in leaders
                .Where(hero => !string.IsNullOrWhiteSpace(
                    ReadString(hero, "kingdomId", "")))
                .GroupBy(hero => ReadString(hero, "kingdomId", ""),
                    StringComparer.OrdinalIgnoreCase))
            {
                List<string> ids = kingdom.Select(hero => ReadFirstString(hero,
                        "heroStringId", "heroId", "id"))
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                for (int first = 0; first < ids.Count; first++)
                    for (int second = first + 1; second < ids.Count; second++)
                        add(ids[first], ids[second], "kingdom_leadership");
            }
            List<string> rulers = leaders.Where(hero => ReadBool(hero,
                    "isRuler", false)).Select(hero => ReadFirstString(hero,
                    "heroStringId", "heroId", "id"))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            for (int first = 0; first < rulers.Count; first++)
                for (int second = first + 1; second < rulers.Count; second++)
                    add(rulers[first], rulers[second], "ruler_network");
            return result;
        }

        private sealed class OpeningSeedCandidate
        {
            public string PairKey = "";
            public string HeroAId = "";
            public string HeroBId = "";
            public HashSet<string> Provenance = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> NormalizeAmbientLifecycleHero(
            Dictionary<string, object> row)
        {
            row = row ?? new Dictionary<string, object>();
            if (row.ContainsKey("heroStringId")) return row;
            double age = ReadDouble(row, "a", 0d);
            return new Dictionary<string, object>
            {
                ["heroStringId"] = ReadString(row, "i", ""),
                ["age"] = age,
                ["isAdult"] = age >= 18d,
                ["isFemale"] = ReadInt(row, "f", 0) == 1,
                ["isAlive"] = true,
                ["isPregnant"] = ReadInt(row, "p", 0) == 1,
                ["spouseId"] = ReadString(row, "s", ""),
                ["fatherId"] = ReadString(row, "fa", ""),
                ["motherId"] = ReadString(row, "mo", ""),
                ["childrenCount"] = ReadInt(row, "ch", 0),
                ["marriageAncestorIds"] = ReadStringList(row, "an")
                    .Cast<object>().ToList(),
                ["clanId"] = ReadString(row, "c", ""),
                ["kingdomId"] = ReadString(row, "k", ""),
                ["isLord"] = ReadInt(row, "n", 0) == 1,
                ["isNotable"] = ReadInt(row, "o", 0) == 1,
                ["isRuler"] = ReadInt(row, "r", 0) == 1,
                ["isClanLeader"] = ReadInt(row, "l", 0) == 1,
                ["nativeCanMarry"] = ReadInt(row, "m", 0) == 1,
                ["nativeMarriageClanSuitable"] = ReadInt(row, "u", 0) == 1
            };
        }

        private static void MarkRelationshipDailyInputProcessed(
            string campaignId, string timelineId, int day)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                RelationshipsStore.UpdateRelationshipDailyInputs7(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                PruneRelationshipHeroObservations(connection, timelineId, day);
                RelationshipsStore.DeleteRelationshipDailyInputs(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day
                    });
            }
        }

        private static double OldestPendingRelationshipInputDay(
            string campaignId, string timelineId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                return ReadDouble(RelationshipsStore.SelectRelationshipDailyInputs8(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).FirstOrDefault(), "day", -1d);
            }
        }
    }
}
