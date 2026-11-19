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
        internal static Dictionary<string, object> MbtiRelationshipSnapshotApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> dailyInputs = ReadDictionaryList(payload, "dailyInputs");
            if (dailyInputs.Count > 0)
            {
                string envelopeCampaign = ReadString(payload, "campaignId", "default");
                string envelopeTimeline = ReadString(payload, "timelineId", "main");
                List<int> processedDays = new List<int>();
                foreach (Dictionary<string, object> input in dailyInputs
                    .OrderBy(x => ReadDouble(x, "worldDay", 0d)))
                {
                    if (string.IsNullOrWhiteSpace(ReadString(input, "campaignId", "")))
                        input["campaignId"] = envelopeCampaign;
                    if (string.IsNullOrWhiteSpace(ReadString(input, "timelineId", "")))
                        input["timelineId"] = envelopeTimeline;
                    Dictionary<string, object> accepted = PersistAndHydrateRelationshipDailyInput(input);
                    processedDays.Add((int)Math.Floor(ReadDouble(accepted, "worldDay", 0d) + 0.000001d));
                }
                MarkRelationshipCampaignReady(envelopeCampaign);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["accepted"] = true,
                    ["durable"] = true,
                    ["acceptedInputDays"] = processedDays,
                    ["acceptedInputCount"] = processedDays.Count,
                    ["oldestPendingInputDay"] = OldestPendingRelationshipInputDay(envelopeCampaign, envelopeTimeline),
                    ["processingMode"] = "continuous_server_worker",
                    ["nativeSyncPlan"] = null
                };
            }
            Stopwatch timer = Stopwatch.StartNew();
            Interlocked.Exchange(ref PostgreSqlRelationshipStagedRows, 0);
            Interlocked.Exchange(ref PostgreSqlRelationshipWrittenRows, 0);
            Interlocked.Exchange(ref PostgreSqlRelationshipCopyMs, 0);
            Interlocked.Exchange(ref PostgreSqlRelationshipMergeMs, 0);
            Stopwatch payloadPreparationTimer = Stopwatch.StartNew();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string correlationId = Program.EnsureCorrelationId(payload);
            int day = (int)Math.Floor(ReadDouble(payload, "worldDay", 0d) + 0.000001d);
            int cadenceShard = ReadInt(payload, "cadenceShard", -1);
            List<Dictionary<string, object>> cadenceInputs = ReadDictionaryList(payload, "cadenceInputs");
            if (cadenceInputs.Count == 0)
            {
                cadenceInputs.Add(new Dictionary<string, object>
                {
                    ["worldDay"] = ReadDouble(payload, "worldDay", 0d),
                    ["presenceGroups"] = ReadDictionaryList(payload, "presenceGroups"),
                    ["heroes"] = ReadDictionaryList(payload, "heroes")
                });
            }
            cadenceInputs = cadenceInputs
                .OrderBy(input => ReadDouble(input, "worldDay", 0d))
                .ToList();
            int cadenceWindowStartDay = cadenceInputs
                .Min(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d));
            int cadenceWindowDayCount = cadenceInputs
                .Select(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d))
                .Distinct().Count();
            Dictionary<int, List<Dictionary<string, object>>> groupsByDay = cadenceInputs
                .GroupBy(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d))
                .ToDictionary(group => group.Key,
                    group => group.SelectMany(input => ReadDictionaryList(input, "presenceGroups")).ToList());
            Dictionary<int, List<Dictionary<string, object>>> heroRowsByDay = cadenceInputs
                .GroupBy(input => (int)Math.Floor(ReadDouble(input, "worldDay", 0d) + 0.000001d))
                .ToDictionary(group => group.Key,
                    group => group.SelectMany(input => ReadDictionaryList(input, "heroes"))
                        .Select(NormalizeAmbientLifecycleHero).ToList());
            Dictionary<string, Dictionary<string, object>> heroes = heroRowsByDay
                .OrderBy(group => group.Key)
                .SelectMany(group => group.Value)
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "heroStringId", "heroId", "id")))
                .GroupBy(x => ReadFirstString(x, "heroStringId", "heroId", "id"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
            payloadPreparationTimer.Stop();
            long payloadPreparationMs = payloadPreparationTimer.ElapsedMilliseconds;
            long schemaReadinessMs = 0;
            long progressLoadMs = 0;
            long lifecycleLoadMs = 0;
            long heroHydrationMs = 0;
            int heroDocumentCacheHits = 0;
            long matchingMs = 0;
            long personalityLoadMs = 0;
            long commandPreparationMs = 0;
            long transactionBeginMs = 0;
            long commitMs = 0;

            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            using (RelationshipStandingContext standingContext = new RelationshipStandingContext(connection, campaignId, timelineId))
            using (RelationshipLifecycleWriteContext lifecycleWriteContext = new RelationshipLifecycleWriteContext(connection))
            using (ReignDatabase.SqlReadTimingContext sqlReadTiming = new ReignDatabase.SqlReadTimingContext())
            {
                Stopwatch schemaReadinessTimer = Stopwatch.StartNew();
                EnsureMbtiRelationshipSchema(connection);
                WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
                // Component-schema readiness is cached for the lifetime of the
                // server.  Validation fixtures (and a restored/recreated
                // PostgreSQL database) can legitimately replace the backing
                // tables while that process remains alive, so verify the one
                // table this hot path writes before using the schema-ready
                // telemetry helpers.  This is one metadata lookup per
                // relationship day, never one lookup per pair.
                if (!WorldSimulationService.TableExists(connection, "world_test_chunk_counters"))
                    WorldSimulationService.EnsureWorldTestTelemetrySchemaCore(connection);
                schemaReadinessTimer.Stop();
                schemaReadinessMs = schemaReadinessTimer.ElapsedMilliseconds;
                Stopwatch progressLoadTimer = Stopwatch.StartNew();
                Dictionary<string, object> dailyProgress = ReignDatabase.QuerySql(connection, @"
SELECT pair_cursor,processed_pairs FROM relationship_daily_inputs
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day
LIMIT 1;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = day
                }).FirstOrDefault();
                string resumePairCursor = ReadString(dailyProgress,
                    "pair_cursor", "");
                int resumedProcessedPairs = ReadInt(dailyProgress,
                    "processed_pairs", 0);
                int lastProcessedDay = RelationshipLastProcessedDay(connection, timelineId);
                progressLoadTimer.Stop();
                progressLoadMs = progressLoadTimer.ElapsedMilliseconds;
                if (lastProcessedDay >= day)
                {
                    bool continuousWorker = ReadBool(payload, "continuousWorker", false);
                    Dictionary<string, object> repeatedPlan = continuousWorker
                        ? new Dictionary<string, object>
                        {
                            ["targetCount"] = ReadInt(ReignDatabase.QuerySql(connection,
                                "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed');")
                                .FirstOrDefault(), "count", 0)
                        }
                        : BuildRelationshipNativeSyncPlan(connection, campaignId, timelineId, day);
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["idempotent"] = true, ["campaignId"] = campaignId,
                        ["worldDay"] = day, ["noLlmConfirmed"] = true,
                        ["evaluatedPairDays"] = 0,
                        ["cadenceDays"] = RelationshipCadenceDays,
                        ["cadenceShard"] = cadenceShard,
                        ["windowStartDay"] = cadenceWindowStartDay,
                        ["nativeChangesQueued"] = ReadInt(repeatedPlan, "targetCount", 0),
                        ["nativeSyncPlan"] = continuousWorker ? null : repeatedPlan,
                        ["summary"] = "Directional MBTI chemistry was already processed for this campaign day."
                    };
                }

                string runId = "mbti_run_" + Guid.NewGuid().ToString("N");
                Stopwatch lifecycleLoadTimer = Stopwatch.StartNew();
                List<Dictionary<string, object>> independentLifecycleRows =
                    LoadCoPresentLifecycleRows(connection, day, groupsByDay);
                lifecycleLoadTimer.Stop();
                lifecycleLoadMs = lifecycleLoadTimer.ElapsedMilliseconds;
                Stopwatch heroHydrationTimer = Stopwatch.StartNew();
                HashSet<string> requiredHeroIds = new HashSet<string>(groupsByDay.Values
                    .SelectMany(groups => groups)
                    .SelectMany(group => ReadStringList(group, "heroIds"))
                    .Where(id => !string.IsNullOrWhiteSpace(id)), StringComparer.OrdinalIgnoreCase);
                requiredHeroIds.UnionWith(independentLifecycleRows
                    .SelectMany(row => new[]
                    {
                        ReadString(row, "hero_a_id", ""),
                        ReadString(row, "hero_b_id", "")
                    }).Where(id => !string.IsNullOrWhiteSpace(id)));
                var contextualHeroesByDay = new Dictionary<int, Dictionary<string, Dictionary<string, object>>>();
                foreach (int presenceDay in groupsByDay.Keys.OrderBy(value => value))
                {
                    // Explicit rows belong to this day. Other days' deltas and the
                    // current native roster cannot stand in for historical facts.
                    var contextual = RelationshipThroughputOriginalReadPath
                        ? new Dictionary<string, Dictionary<string, object>>(heroes, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                    foreach (var hero in heroRowsByDay[presenceDay])
                    {
                        string id = ReadFirstString(hero, "heroStringId", "heroId", "id");
                        if (!string.IsNullOrWhiteSpace(id)) contextual[id] = hero;
                    }
                    var dayHeroIds = groupsByDay[presenceDay].SelectMany(group => ReadStringList(group, "heroIds"));
                    foreach (var observed in LoadRelationshipHeroObservations(connection, timelineId, presenceDay,
                        dayHeroIds.Where(id => !contextual.ContainsKey(id))))
                    {
                        string document = ReadString(observed, "profile_json", "{}");
                        bool cacheHit = false;
                        var profile = RelationshipThroughputOriginalReadPath
                            ? TryParseJsonObject(document) : RelationshipHeroDocuments.Read(document, out cacheHit);
                        if (cacheHit) heroDocumentCacheHits++;
                        if (profile != null) contextual[ReadString(observed, "hero_id", "")] = NormalizeAmbientLifecycleHero(profile);
                    }
                    contextualHeroesByDay[presenceDay] = contextual;
                    // Fling processing receives the same day-specific profiles.
                    heroRowsByDay[presenceDay] = contextual.Values.ToList();
                }
                heroes = contextualHeroesByDay.OrderBy(entry => entry.Key).SelectMany(entry => entry.Value)
                    .GroupBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.OrdinalIgnoreCase);
                heroHydrationTimer.Stop();
                heroHydrationMs = heroHydrationTimer.ElapsedMilliseconds;

                Stopwatch matchingTimer = Stopwatch.StartNew();
                Dictionary<int, Dictionary<string, HashSet<int>>> coPresenceByDay =
                    groupsByDay.ToDictionary(entry => entry.Key,
                        entry => BuildRelationshipCoPresenceIndex(entry.Value));
                Dictionary<string, Dictionary<string, object>>
                    courtshipPairStates = LoadCourtshipFocusPairStates(
                        connection, groupsByDay, heroes, contextualHeroesByDay);
                HashSet<string> excludedCourtshipFocusPairs =
                    LoadHistoricalCourtshipExclusions(connection, day);
                excludedCourtshipFocusPairs.UnionWith(independentLifecycleRows
                    .Where(IsRomanceContinuationLifecycle)
                    .Select(row => ReadString(row, "pair_key", "")));
                Dictionary<string, List<RelationshipPairDay>> pairDays =
                    new Dictionary<string, List<RelationshipPairDay>>(StringComparer.OrdinalIgnoreCase);
                int romanceContinuationPairDays = 0;
                int courtshipFocusPairDays = 0;
                foreach (int presenceDay in groupsByDay.Keys.OrderBy(value => value))
                {
                    Dictionary<string, Dictionary<string, object>> contextualHeroes = contextualHeroesByDay[presenceDay];
                    Dictionary<string, AmbientPairContext> dailyPairs =
                        ExpandDailyMatchedPairs(groupsByDay[presenceDay],
                            contextualHeroes, campaignId, timelineId,
                            presenceDay, courtshipPairStates,
                            excludedCourtshipFocusPairs);
                    courtshipFocusPairDays += dailyPairs.Values.Count(pair =>
                        pair.CourtshipFocus);
                    foreach (Dictionary<string, object> lifecycleRow
                        in independentLifecycleRows.Where(
                            IsRomanceContinuationLifecycle))
                    {
                        string pairKey = ReadString(lifecycleRow,
                            "pair_key", "");
                        string heroAId = ReadString(lifecycleRow,
                            "hero_a_id", "");
                        string heroBId = ReadString(lifecycleRow,
                            "hero_b_id", "");
                        if (dailyPairs.ContainsKey(pairKey)
                            || ReadInt(lifecycleRow, "last_processed_day", -1)
                                >= presenceDay
                            || !contextualHeroes.ContainsKey(heroAId)
                            || !contextualHeroes.ContainsKey(heroBId)
                            || !RelationshipHeroesShareGroup(
                                coPresenceByDay[presenceDay], heroAId, heroBId))
                            continue;
                        Dictionary<string, object> continuationHeroA =
                            contextualHeroes[heroAId];
                        Dictionary<string, object> continuationHeroB =
                            contextualHeroes[heroBId];
                        dailyPairs[pairKey] = new AmbientPairContext
                        {
                            PairKey = pairKey,
                            HeroAId = heroAId,
                            HeroBId = heroBId,
                            ContextKind = "romance_continuation",
                            ContextId = "daily_copresence",
                            ExposureWeight = 1d,
                            SameClan = !string.IsNullOrWhiteSpace(ReadString(
                                    continuationHeroA, "clanId", ""))
                                && ReadString(continuationHeroA, "clanId", "")
                                    .Equals(ReadString(continuationHeroB,
                                            "clanId", ""),
                                        StringComparison.OrdinalIgnoreCase),
                            SameKingdom = !string.IsNullOrWhiteSpace(ReadString(
                                    continuationHeroA, "kingdomId", ""))
                                && ReadString(continuationHeroA, "kingdomId", "")
                                    .Equals(ReadString(continuationHeroB,
                                            "kingdomId", ""),
                                        StringComparison.OrdinalIgnoreCase)
                        };
                        romanceContinuationPairDays++;
                    }
                    foreach (AmbientPairContext dailyPair in dailyPairs.Values)
                    {
                        if (!pairDays.TryGetValue(dailyPair.PairKey,
                            out List<RelationshipPairDay> scheduledDays))
                        {
                            scheduledDays = new List<RelationshipPairDay>();
                            pairDays[dailyPair.PairKey] = scheduledDays;
                        }
                        scheduledDays.Add(new RelationshipPairDay
                        {
                            Day = presenceDay,
                            Pair = dailyPair,
                            HeroA = contextualHeroes[dailyPair.HeroAId],
                            HeroB = contextualHeroes[dailyPair.HeroBId]
                        });
                    }
                }
                List<RelationshipPairDay> allPairDays = pairDays.Values
                    .SelectMany(value => value)
                    .OrderBy(value => value.Pair.PairKey,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(value => value.Day)
                    .ToList();
                matchingTimer.Stop();
                matchingMs = matchingTimer.ElapsedMilliseconds;
                int parallelWorkers = RelationshipParallelismForWork(
                    allPairDays.Count);
                Stopwatch parallelTimer = Stopwatch.StartNew();
                if (parallelWorkers > 1)
                {
                    Parallel.ForEach(allPairDays,
                        new ParallelOptions
                        {
                            MaxDegreeOfParallelism = parallelWorkers
                        },
                        pairDay =>
                        {
                            pairDay.Dice = BuildDailyPairDice(campaignId,
                                pairDay.Pair, pairDay.Day);
                        });
                }
                else
                {
                    foreach (RelationshipPairDay pairDay in allPairDays)
                    {
                        pairDay.Dice = BuildDailyPairDice(campaignId,
                            pairDay.Pair, pairDay.Day);
                    }
                }
                parallelTimer.Stop();
                RecordRelationshipParallelStage(parallelWorkers,
                    allPairDays.Count, parallelTimer.ElapsedMilliseconds);
                Dictionary<string, AmbientPairContext> pairs = pairDays.ToDictionary(
                    item => item.Key,
                    item => item.Value.OrderBy(value => value.Day).Last().Pair,
                    StringComparer.OrdinalIgnoreCase);
                Stopwatch personalityLoadTimer = Stopwatch.StartNew();
                HashSet<string> needed = new HashSet<string>(pairs.Values.SelectMany(x => new[] { x.HeroAId, x.HeroBId }), StringComparer.OrdinalIgnoreCase);
                Dictionary<string, Dictionary<string, object>> notableMbti =
                    new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                string neededHeroesJson = Json.Serialize(needed.OrderBy(value => value,
                    StringComparer.OrdinalIgnoreCase).ToList());
                List<Dictionary<string, object>> persistedNotableRows =
                    ReignPostgreSqlDialect.IsPostgreSql(connection)
                        ? ReignDatabase.QuerySql(connection, @"SELECT profiles.*
FROM notable_mbti_profiles profiles
JOIN jsonb_array_elements_text(CAST($heroes AS jsonb)) requested
  ON requested.value=profiles.hero_id;",
                            new Dictionary<string, object>
                            {
                                ["heroes"] = neededHeroesJson
                            })
                        : ReignDatabase.QuerySql(connection,
                            "SELECT * FROM notable_mbti_profiles;");
                Dictionary<string, Dictionary<string, object>> persistedNotables =
                    persistedNotableRows
                    .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "hero_id", "")))
                    .ToDictionary(x => ReadString(x, "hero_id", ""), x => x, StringComparer.OrdinalIgnoreCase);
                foreach (string heroId in needed.Where(heroes.ContainsKey))
                {
                    Dictionary<string, object> hero = heroes[heroId];
                    if (persistedNotables.TryGetValue(heroId, out Dictionary<string, object> notableRow))
                    {
                        Dictionary<string, object> profile = RelationshipThroughputOriginalReadPath
                            ? NotableMbtiRowToProfile(notableRow) : CompactNotableRelationshipProfile(notableRow);
                        Dictionary<string, object> expected = ReadDictionary(profile, "nativeTraits") ?? new Dictionary<string, object>();
                        Dictionary<string, object> observed = ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
                        string syncStatus = ReadString(profile, "nativeSyncStatus", "pending");
                        bool hasCompleteObservation = expected.Count == 5 && expected.All(item =>
                            ReadInt(observed, item.Key, int.MinValue) != int.MinValue);
                        bool observedMatches = hasCompleteObservation && expected.All(item =>
                            ReadInt(observed, item.Key, int.MinValue)
                                == Convert.ToInt32(item.Value, CultureInfo.InvariantCulture));
                        bool needsNativeRefresh = !syncStatus.Equals("manual_override", StringComparison.OrdinalIgnoreCase)
                            && hasCompleteObservation
                            && (!observedMatches || !syncStatus.Equals("synchronized", StringComparison.OrdinalIgnoreCase));
                        notableMbti[heroId] = needsNativeRefresh
                            ? EnsureNotableMbtiProfile(connection, campaignId, day, hero, false, true)
                            : profile;
                    }
                    else if (!ReadBool(hero, "isNotable", false))
                    {
                        continue;
                    }
                    else
                    {
                        notableMbti[heroId] = EnsureNotableMbtiProfile(connection, campaignId, day, hero, true, true);
                    }
                }
                Dictionary<string, Dictionary<string, object>> permanentMbtiByHero =
                    LoadPermanentRelationshipMbti(connection, campaignId, needed);
                foreach (KeyValuePair<string, Dictionary<string, object>> notable
                    in notableMbti)
                {
                    string type = ReadString(notable.Value, "type", "XXXX");
                    if (!MbtiDefinitions.TryGetValue(type,
                        out MbtiDefinition definition))
                        continue;
                    permanentMbtiByHero[notable.Key] =
                        new Dictionary<string, object>
                        {
                            ["type"] = type,
                            ["title"] = definition.Title,
                            ["description"] = definition.Description,
                            ["source"] = "notable_mbti_template"
                        };
                    CachePermanentRelationshipMbti(campaignId, notable.Key,
                        permanentMbtiByHero[notable.Key]);
                }
                personalityLoadTimer.Stop();
                personalityLoadMs = personalityLoadTimer.ElapsedMilliseconds;
                int processed = resumedProcessedPairs, evaluatedPairDays = 0,
                    rolledDirections = 0;
                int positiveRolls = 0, negativeRolls = 0, affinityChanges = 0;
                int ordinaryMagnitudePairDays = 0, loverMagnitudePairDays = 0;
                Dictionary<string, object> flingSummary =
                    new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                int newPairCount = 0, repeatEncounterCount = 0;
                int nativeQueued = 0, skippedMissing = 0, skippedSettlementGate = 0;
                HashSet<string> courtPopularitySubjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool courtPopularityChanged = false;
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long decayMs = 0;
                long stateLoadMs = 0;
                long stateLifecycleReadMs = 0, stateChemistryReadMs = 0, stateNativeReadMs = 0, stateStandingReadMs = 0;
                long pairProcessingMs = 0;
                long independentLifecycleMs = 0;
                long finalizationMs = 0;
                long finalFlushMs = 0;
                long flingProcessingMs = 0;
                long courtPopularityMs = 0;
                long nativeCountMs = 0;
                long finalPairFlushMs = 0, finalProvenanceFlushMs = 0, finalTelemetryMs = 0;

                Stopwatch commandPreparationTimer = Stopwatch.StartNew();
                using (ReignDbCommand pairUpsert = CreateRelationshipPairUpsertCommand(connection))
                using (ReignDbCommand nativeTargetUpsert = CreateRelationshipNativeTargetUpsertCommand(connection))
                using (ReignDbCommand nativeTargetDelete = CreatePreparedRelationshipCommand(connection,
                    "DELETE FROM relationship_native_targets WHERE pair_key=$pair;", "$pair"))
                {
                    commandPreparationTimer.Stop();
                    commandPreparationMs = commandPreparationTimer.ElapsedMilliseconds;
                    Stopwatch transactionBeginTimer = Stopwatch.StartNew();
                    ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;");
                    transactionBeginTimer.Stop();
                    transactionBeginMs = transactionBeginTimer.ElapsedMilliseconds;
                    try
                    {
                        // Relationship state is event-driven. Do not periodically
                        // normalize personal affinity toward zero; accumulated
                        // friendship, rivalry, and ruler history remain until an
                        // actual interaction changes them.
                        decayMs = 0;
                        Dictionary<string, object> decay =
                            new Dictionary<string, object>
                            {
                                ["enabled"] = false,
                                ["pairsDecayed"] = 0,
                                ["directionsDecayed"] = 0,
                                ["nativeChangesQueued"] = 0,
                                ["loversEnded"] = 0
                            };
                    Stopwatch stateLoadTimer = Stopwatch.StartNew();
                    // Durable daily inputs preserve actual co-presence. Never
                    // infer missed days from the newest snapshot.
                    int globalReplayStartDay = day;
                    int catchUpDayCount = 0;
                    CompactDormantRelationshipLifecycleRows(connection);
                    HashSet<string> neededPairKeys = new HashSet<string>(
                        pairs.Keys, StringComparer.OrdinalIgnoreCase);
                    neededPairKeys.UnionWith(independentLifecycleRows.Select(row => ReadString(row, "pair_key", "")));
                    Dictionary<string, Dictionary<string, object>> existingLifecycle =
                        LoadRelationshipLifecycleWorkset(connection, neededPairKeys)
                            .ToDictionary(x => ReadString(x, "pair_key", ""), x => x, StringComparer.OrdinalIgnoreCase);
                    if (RelationshipThroughputOriginalReadPath) neededPairKeys.UnionWith(existingLifecycle.Keys);
                    stateLifecycleReadMs = stateLoadTimer.ElapsedMilliseconds;
                    string neededPairsJson = Json.Serialize(neededPairKeys
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList());
                    List<Dictionary<string, object>> existingPairRows =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                            ? ReignDatabase.QuerySql(connection, @"SELECT pairs.*
FROM relationship_pair_chemistry pairs
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=pairs.pair_key;",
                                new Dictionary<string, object>
                                {
                                    ["pairs"] = neededPairsJson
                                })
                            : RelationshipPairStateSnapshot(connection,
                                campaignId).Values.ToList();
                    Dictionary<string, Dictionary<string, object>> existingPairs =
                        existingPairRows.ToDictionary(
                            x => ReadString(x, "pair_key", ""), x => x,
                            StringComparer.OrdinalIgnoreCase);
                    stateChemistryReadMs = stateLoadTimer.ElapsedMilliseconds - stateLifecycleReadMs;
                    List<Dictionary<string, object>> existingNativeTargetRows =
                        ReignPostgreSqlDialect.IsPostgreSql(connection)
                            ? ReignDatabase.QuerySql(connection, @"SELECT targets.*
FROM relationship_native_targets targets
JOIN jsonb_array_elements_text(CAST($pairs AS jsonb)) requested
  ON requested.value=targets.pair_key;",
                                new Dictionary<string, object>
                                {
                                    ["pairs"] = neededPairsJson
                                })
                            : ReignDatabase.QuerySql(connection,
                                "SELECT * FROM relationship_native_targets;");
                    Dictionary<string, Dictionary<string, object>> existingNativeTargets =
                        existingNativeTargetRows
                        .ToDictionary(x => ReadString(x, "pair_key", ""), x => x, StringComparer.OrdinalIgnoreCase);
                    stateNativeReadMs = stateLoadTimer.ElapsedMilliseconds - stateLifecycleReadMs - stateChemistryReadMs;
                    EnsureWorldRelationshipSchema(connection);
                    standingContext.Prefetch(requiredHeroIds);
                    stateLoadTimer.Stop();
                    stateLoadMs = stateLoadTimer.ElapsedMilliseconds;
                    stateStandingReadMs = stateLoadMs - stateLifecycleReadMs - stateChemistryReadMs - stateNativeReadMs;
                    int lifecycleChanges = 0, lifecycleRumors = 0, lifecycleActions = 0;
                    bool usePostgreSqlBatches =
                        ReignPostgreSqlDialect.IsPostgreSql(connection);
                    int relationshipWriteChunkSize = usePostgreSqlBatches
                        ? PostgreSqlRelationshipWriteChunkSize
                        : SqliteRelationshipWriteChunkSize;
                    int telemetryChunkIndex = Math.Max(0,
                        resumedProcessedPairs / relationshipWriteChunkSize);
                    int telemetryChunkPairs = 0;
                    int evaluationUnitsSinceThrottle = 0;
                    string telemetryLastPairKey = "";
                    Dictionary<string, object> telemetryChunkCounters =
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    Dictionary<string, Dictionary<string, object>>
                        pendingCacheRows =
                            new Dictionary<string,
                                Dictionary<string, object>>(
                                    StringComparer.OrdinalIgnoreCase);
                    List<Dictionary<string, object>> pairWriteBatch =
                        new List<Dictionary<string, object>>(
                            relationshipWriteChunkSize);
                    List<Dictionary<string, object>> nativeTargetWriteBatch =
                        new List<Dictionary<string, object>>(
                            relationshipWriteChunkSize);
                    List<Dictionary<string, object>> nativeTargetDeleteBatch =
                        new List<Dictionary<string, object>>(
                            Math.Min(512, relationshipWriteChunkSize));
                    List<Dictionary<string, object>> provenanceWriteBatch =
                        new List<Dictionary<string, object>>(
                            relationshipWriteChunkSize);
                    Stopwatch pairProcessingTimer = Stopwatch.StartNew();
                    foreach (AmbientPairContext pair in pairs.Values
                        .Where(value => string.IsNullOrWhiteSpace(resumePairCursor)
                            || string.Compare(value.PairKey, resumePairCursor,
                                StringComparison.OrdinalIgnoreCase) > 0)
                        .OrderBy(x => x.PairKey, StringComparer.OrdinalIgnoreCase))
                    {
                        List<RelationshipPairDay> scheduledDays = pairDays[pair.PairKey]
                            .OrderBy(value => value.Day).ToList();
                        if (!heroes.TryGetValue(pair.HeroAId, out Dictionary<string, object> heroA)
                            || !heroes.TryGetValue(pair.HeroBId, out Dictionary<string, object> heroB))
                        {
                            skippedMissing++;
                            continue;
                        }

                        existingPairs.TryGetValue(pair.PairKey, out Dictionary<string, object> existing);
                        if (existing != null && ReadInt(existing, "last_day", -1) == day) continue;
                        int previouslyAccountedDay = existing == null
                            ? int.MinValue : ReadInt(existing, "last_day", int.MinValue);
                        scheduledDays = scheduledDays
                            .Where(value => value.Day > previouslyAccountedDay).ToList();
                        if (scheduledDays.Count == 0) continue;
                        RelationshipPairDay latestPairDay = scheduledDays[scheduledDays.Count - 1];
                        heroA = latestPairDay.HeroA;
                        heroB = latestPairDay.HeroB;

                        Dictionary<string, object> axesA;
                        Dictionary<string, object> axesB;
                        string typeA;
                        string typeB;
                        int baseAB;
                        int baseBA;
                        int chanceAB;
                        int chanceBA;
                        string authoritativeNotableTypeA = notableMbti.TryGetValue(
                            pair.HeroAId, out Dictionary<string, object> authoritativeNotableA)
                            ? ReadString(authoritativeNotableA, "type", "")
                            : "";
                        string authoritativeNotableTypeB = notableMbti.TryGetValue(
                            pair.HeroBId, out Dictionary<string, object> authoritativeNotableB)
                            ? ReadString(authoritativeNotableB, "type", "")
                            : "";
                        if (existing != null
                            && MbtiDefinitions.ContainsKey(ReadString(existing, "mbti_a", ""))
                            && MbtiDefinitions.ContainsKey(ReadString(existing, "mbti_b", ""))
                            && (string.IsNullOrWhiteSpace(authoritativeNotableTypeA)
                                || ReadString(existing, "mbti_a", "").Equals(
                                    authoritativeNotableTypeA, StringComparison.OrdinalIgnoreCase))
                            && (string.IsNullOrWhiteSpace(authoritativeNotableTypeB)
                                || ReadString(existing, "mbti_b", "").Equals(
                                    authoritativeNotableTypeB, StringComparison.OrdinalIgnoreCase)))
                        {
                            typeA = ReadString(existing, "mbti_a", "");
                            typeB = ReadString(existing, "mbti_b", "");
                            axesA = new Dictionary<string, object> { ["type"] = typeA, ["source"] = "cached_pair_compatibility" };
                            axesB = new Dictionary<string, object> { ["type"] = typeB, ["source"] = "cached_pair_compatibility" };
                            baseAB = ReadInt(existing, "base_chance_a_to_b", MbtiCompatibility(typeA, typeB));
                            baseBA = ReadInt(existing, "base_chance_b_to_a", MbtiCompatibility(typeB, typeA));
                            chanceAB = AdjustedMbtiCompatibility(baseAB);
                            chanceBA = AdjustedMbtiCompatibility(baseBA);
                        }
                        else
                        {
                            if (!permanentMbtiByHero.TryGetValue(pair.HeroAId,
                                out axesA))
                            {
                                axesA = ResolvePermanentRelationshipMbti(
                                    connection, campaignId, day, heroA,
                                    notableMbti);
                                permanentMbtiByHero[pair.HeroAId] = axesA;
                            }
                            if (!permanentMbtiByHero.TryGetValue(pair.HeroBId,
                                out axesB))
                            {
                                axesB = ResolvePermanentRelationshipMbti(
                                    connection, campaignId, day, heroB,
                                    notableMbti);
                                permanentMbtiByHero[pair.HeroBId] = axesB;
                            }
                            typeA = ReadString(axesA, "type", "XXXX");
                            typeB = ReadString(axesB, "type", "XXXX");
                            if (!MbtiDefinitions.ContainsKey(typeA) || !MbtiDefinitions.ContainsKey(typeB))
                            {
                                skippedMissing++;
                                string error = "Missing immutable MBTI assignment for relationship pair " + pair.PairKey
                                    + " (" + pair.HeroAId + "=" + typeA + ", " + pair.HeroBId + "=" + typeB + ").";
                                ReignDatabase.ExecuteSql(connection,
                                    "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('relationship_mbti_error',$error);",
                                    new Dictionary<string, object> { ["error"] = error });
                                continue;
                            }
                            baseAB = MbtiCompatibility(typeA, typeB);
                            baseBA = MbtiCompatibility(typeB, typeA);
                            chanceAB = AdjustedMbtiCompatibility(baseAB);
                            chanceBA = AdjustedMbtiCompatibility(baseBA);
                        }
                        int startingAffinity = InitialReignAffinity(
                            scheduledDays[0].HeroA, scheduledDays[0].HeroB);
                        bool rebaseLegacyAffinity = existing != null
                            && ReadInt(existing, "compatibility_version", 0)
                                < LegacyAffinityRebaseCutoffVersion;
                        int affinityAB = existing == null || rebaseLegacyAffinity
                            ? startingAffinity : ReadInt(existing, "affinity_a_to_b", 0);
                        int affinityBA = existing == null || rebaseLegacyAffinity
                            ? startingAffinity : ReadInt(existing, "affinity_b_to_a", 0);
                        int signalAB = 0;
                        int signalBA = 0;
                        int rollAB = 0, rollBA = 0, deltaAB = 0, deltaBA = 0;
                        int romancePositiveBonus = 0;
                        int pairRolledDirections = 0;
                        int pairPositiveRolls = 0;
                        int pairNegativeRolls = 0;
                        int socialAB = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, timelineId, pair.HeroAId, pair.HeroBId), "value", 0);
                        int socialBA = ReadInt(ResolveObserverPublicStanding(connection,
                            campaignId, timelineId, pair.HeroBId, pair.HeroAId), "value", 0);
                        int effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
                        int effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
                        existingLifecycle.TryGetValue(pair.PairKey, out Dictionary<string, object> lifecycleRow);
                        lifecycleRow = lifecycleRow
                            ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        string lifecycleShared = SharedRelationshipTag(campaignId, pair.PairKey,
                            effectiveAB, effectiveBA, heroA, heroB);
                        foreach (RelationshipPairDay scheduledDay in scheduledDays)
                        {
                            AmbientPairContext dailyPair = scheduledDay.Pair;
                            Dictionary<string, object> dailyHeroA = scheduledDay.HeroA;
                            Dictionary<string, object> dailyHeroB = scheduledDay.HeroB;
                            DailyPairDice dice = scheduledDay.Dice;
                            if (!dice.SettlementEligible)
                            {
                                skippedSettlementGate++;
                            }
                            else
                            {
                                bool loverMagnitude =
                                    UsesLoverRelationshipMagnitude(lifecycleRow);
                                int magnitudeAB = loverMagnitude
                                    ? dice.LoverMagnitudeAtoB
                                    : dice.OrdinaryMagnitudeAtoB;
                                int magnitudeBA = loverMagnitude
                                    ? dice.LoverMagnitudeBtoA
                                    : dice.OrdinaryMagnitudeBtoA;
                                if (loverMagnitude) loverMagnitudePairDays++;
                                else ordinaryMagnitudePairDays++;
                                romancePositiveBonus =
                                    RomancePositiveChanceBonusForAffinity(
                                        lifecycleRow, affinityAB, affinityBA);
                                int dailyChanceAB = Clamp(chanceAB
                                    + romancePositiveBonus, 1, 99);
                                int dailyChanceBA = Clamp(chanceBA
                                    + romancePositiveBonus, 1, 99);
                                rollAB = dice.RollAtoB;
                                rollBA = dice.RollBtoA;
                                deltaAB = (rollAB <= dailyChanceAB ? 1 : -1)
                                    * magnitudeAB;
                                deltaBA = (rollBA <= dailyChanceBA ? 1 : -1)
                                    * magnitudeBA;
                                int oldAB = affinityAB, oldBA = affinityBA;
                                affinityAB = Clamp(affinityAB + deltaAB, -100, 100);
                                affinityBA = Clamp(affinityBA + deltaBA, -100, 100);
                                if (CourtPopularityQualification(oldAB) != CourtPopularityQualification(affinityAB))
                                    courtPopularitySubjects.Add(pair.HeroBId);
                                if (CourtPopularityQualification(oldBA) != CourtPopularityQualification(affinityBA))
                                    courtPopularitySubjects.Add(pair.HeroAId);
                                affinityChanges += Math.Abs(affinityAB - oldAB) + Math.Abs(affinityBA - oldBA);
                                rolledDirections += 2;
                                pairRolledDirections += 2;
                                if (deltaAB > 0) { positiveRolls++; pairPositiveRolls++; }
                                else { negativeRolls++; pairNegativeRolls++; }
                                if (deltaBA > 0) { positiveRolls++; pairPositiveRolls++; }
                                else { negativeRolls++; pairNegativeRolls++; }
                            }

                            effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
                            effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
                            bool lifecycleEligible = lifecycleRow.Count > 0
                                || ReadString(dailyHeroA, "spouseId", "").Equals(
                                    dailyPair.HeroBId, StringComparison.OrdinalIgnoreCase)
                                || ReadString(dailyHeroB, "spouseId", "").Equals(
                                    dailyPair.HeroAId, StringComparison.OrdinalIgnoreCase)
                                || effectiveAB >= RomanceFlirtationThreshold
                                    && effectiveBA >= RomanceFlirtationThreshold;
                            Dictionary<string, object> lifecycle = lifecycleEligible
                                ? ProcessRelationshipLifecycle(connection, campaignId, dailyPair,
                                    scheduledDay.Day, dailyHeroA, dailyHeroB, effectiveAB, effectiveBA,
                                    lifecycleRow, true, timelineId)
                                : new Dictionary<string, object>
                                {
                                    ["sharedTag"] = SharedRelationshipTag(campaignId,
                                        dailyPair.PairKey, effectiveAB, effectiveBA, dailyHeroA, dailyHeroB)
                                };
                            lifecycleShared = ReadString(lifecycle, "sharedTag", lifecycleShared);
                            lifecycleChanges += new[] { "loverStarted", "loverEnded", "affairStarted", "affairEnded" }
                                .Count(key => ReadBool(lifecycle, key, false));
                            WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "flirtationAttempts",
                                ReadBool(lifecycle, "flirtationAttempted", false)
                                    ? 1 : 0);
                            WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "flirtationsStarted",
                                ReadBool(lifecycle, "flirtationStarted", false)
                                    ? 1 : 0);
                            WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "growingAttractionsStarted",
                                ReadBool(lifecycle,
                                    "growingAttractionStarted", false) ? 1 : 0);
                            WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters,
                                "affairJudgmentAttempts",
                                ReadBool(lifecycle,
                                    "affairJudgmentAttempted", false) ? 1 : 0);
                            lifecycleRumors += ReadBool(lifecycle, "rumorCreated", false) ? 1 : 0;
                            lifecycleActions += new[] { "marriageQueued", "divorceQueued", "conceptionQueued" }
                                .Count(key => ReadBool(lifecycle, key, false));
                        }

                        evaluatedPairDays += scheduledDays.Count;
                        evaluationUnitsSinceThrottle += scheduledDays.Count;
                        AmbientPairContext latestPair = latestPairDay.Pair;
                        effectiveAB = Clamp(affinityAB + socialAB, -100, 100);
                        effectiveBA = Clamp(affinityBA + socialBA, -100, 100);
                        string tagAB = DirectionalRelationshipTag(campaignId, pair.PairKey, "a_to_b", affinityAB, affinityBA);
                        string tagBA = DirectionalRelationshipTag(campaignId, pair.PairKey, "b_to_a", affinityBA, affinityAB);
                        string sharedTag = SharedRelationshipTag(campaignId, pair.PairKey,
                            effectiveAB, effectiveBA, heroA, heroB);
                        if (string.IsNullOrWhiteSpace(lifecycleShared)) lifecycleShared = sharedTag;
                        int projectedNative = ProjectNativeRelation(connection, pair.HeroAId,
                            pair.HeroBId, affinityAB, affinityBA, effectiveAB, effectiveBA);
                        RelationshipPairDay nativeObservation = scheduledDays
                            .LastOrDefault(value => value.Pair.NativeRelationObserved);
                        existingNativeTargets.TryGetValue(pair.PairKey, out Dictionary<string, object> priorNativeTarget);
                        bool observationRequired = RelationshipNativeObservationRequired(existing, priorNativeTarget);
                        int observedNative = nativeObservation != null
                            ? nativeObservation.Pair.NativeRelation
                            : existing != null && ReadInt(existing, "native_action_pending", 0) == 0
                                ? ReadInt(existing, "projected_native_relation", 0)
                                : priorNativeTarget != null
                                    ? ReadInt(priorNativeTarget, "observed_relation", 0)
                                    : 0;
                        bool previousProjectionConfirmed = existing != null
                            && !observationRequired
                            && observedNative == ReadInt(existing, "projected_native_relation", int.MinValue);
                        bool pending = existing != null && !rebaseLegacyAffinity
                            && ReadInt(existing, "native_action_pending", 0) == 1;
                        string actionId = existing == null || rebaseLegacyAffinity
                            ? "" : ReadString(existing, "native_action_id", "");
                        if (projectedNative != observedNative || observationRequired)
                        {
                            bool wasPending = pending;
                            bool sameTargetAlreadyQueued = existingNativeTargets.TryGetValue(pair.PairKey,
                                    out Dictionary<string, object> nativeTarget)
                                && ReadInt(nativeTarget, "target_relation", int.MinValue) == projectedNative
                                && new[] { "pending", "claimed" }.Contains(ReadString(nativeTarget, "status", ""),
                                    StringComparer.OrdinalIgnoreCase);
                            actionId = sameTargetAlreadyQueued
                                ? "native_target:" + pair.PairKey
                                : QueueMbtiNativeRelationAction(connection, latestPair, day,
                                    projectedNative, observedNative, nativeTargetUpsert,
                                    usePostgreSqlBatches
                                        ? nativeTargetWriteBatch : null, observationRequired);
                            pending = true;
                            if (!wasPending) nativeQueued++;
                        }
                        else
                        {
                            // The native heartbeat already agrees with Reign's
                            // directional average. No receipt or historical action
                            // row is needed for an aligned pair.
                            if (existingNativeTargets.ContainsKey(pair.PairKey))
                            {
                                Dictionary<string, object> deleteParameters =
                                    new Dictionary<string, object>
                                    {
                                        ["pair"] = pair.PairKey
                                    };
                                if (usePostgreSqlBatches)
                                    nativeTargetDeleteBatch.Add(deleteParameters);
                                else
                                    ExecutePreparedRelationshipCommand(
                                        nativeTargetDelete, deleteParameters);
                            }
                            pending = false;
                            actionId = "";
                        }

                        int firstPresenceDay = scheduledDays[0].Day;
                        int lastPresenceDay = scheduledDays[scheduledDays.Count - 1].Day;
                        int priorPresenceDay = existing == null ? int.MinValue
                            : ReadInt(existing, "last_presence_day",
                                ReadInt(existing, "last_day", int.MinValue));
                        int consecutive = existing == null
                            ? 0 : ReadInt(existing, "consecutive_days", 0);
                        foreach (RelationshipPairDay scheduledDay in scheduledDays)
                        {
                            consecutive = priorPresenceDay != int.MinValue
                                && scheduledDay.Day == priorPresenceDay + 1
                                    ? consecutive + 1
                                    : 1;
                            priorPresenceDay = scheduledDay.Day;
                        }
                        double weighted = (existing == null ? 0d : ReadDouble(existing, "weighted_exposure", 0d))
                            + scheduledDays.Sum(value => value.Pair.ExposureWeight);
                        int presenceMask = 0;
                        foreach (int presenceDay in scheduledDays.Select(value => value.Day).Distinct())
                        {
                            int bit = presenceDay - cadenceWindowStartDay;
                            if (bit >= 0 && bit < 31) presenceMask |= 1 << bit;
                        }
                        int processingShard = RelationshipCadenceShard(campaignId, pair.PairKey);
                        Dictionary<string, object> pairWriteParameters =
                            new Dictionary<string, object>
                            {
                                ["pair"] = pair.PairKey, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                                ["typeA"] = typeA, ["typeB"] = typeB, ["axesA"] = "{}", ["axesB"] = "{}",
                                ["baseAB"] = baseAB, ["baseBA"] = baseBA, ["chanceAB"] = chanceAB, ["chanceBA"] = chanceBA,
                                ["affinityAB"] = affinityAB, ["affinityBA"] = affinityBA, ["signalAB"] = signalAB, ["signalBA"] = signalBA,
                                ["tagAB"] = tagAB, ["tagBA"] = tagBA, ["shared"] = lifecycleShared,
                                ["first"] = existing == null ? firstPresenceDay : ReadInt(existing, "first_day", firstPresenceDay), ["last"] = day,
                                ["consecutive"] = consecutive, ["weighted"] = weighted,
                                ["kind"] = latestPair.ContextKind, ["context"] = latestPair.ContextId,
                                ["rollAB"] = rollAB, ["rollBA"] = rollBA, ["deltaAB"] = deltaAB, ["deltaBA"] = deltaBA,
                                ["shard"] = processingShard, ["presenceDay"] = lastPresenceDay,
                                ["batchSize"] = scheduledDays.Count, ["presenceMask"] = presenceMask,
                                ["decayPeriod"] = Math.Max(0, day / 5),
                                ["projected"] = projectedNative,
                                ["nativeDay"] = previousProjectionConfirmed
                                    ? (double)day
                                    : existing == null ? -1000d : ReadDouble(existing, "last_native_sync_day", -1000d),
                                ["pending"] = pending ? 1 : 0, ["action"] = actionId, ["version"] = MbtiChemistryVersion, ["ts"] = ts
                            };
                        if (usePostgreSqlBatches)
                            pairWriteBatch.Add(pairWriteParameters);
                        else
                            ExecutePreparedRelationshipCommand(pairUpsert,
                                pairWriteParameters);
                        Dictionary<string, object> committedPairState =
                            existing == null
                                ? new Dictionary<string, object>(
                                    StringComparer.OrdinalIgnoreCase)
                                : CloneRelationshipPairCacheRow(existing);
                        committedPairState["pair_key"] = pair.PairKey;
                        committedPairState["hero_a_id"] = pair.HeroAId;
                        committedPairState["hero_b_id"] = pair.HeroBId;
                        committedPairState["mbti_a"] = typeA;
                        committedPairState["mbti_b"] = typeB;
                        committedPairState["base_chance_a_to_b"] = baseAB;
                        committedPairState["base_chance_b_to_a"] = baseBA;
                        committedPairState["chance_a_to_b"] = chanceAB;
                        committedPairState["chance_b_to_a"] = chanceBA;
                        committedPairState["affinity_a_to_b"] = affinityAB;
                        committedPairState["affinity_b_to_a"] = affinityBA;
                        committedPairState["signal_a_to_b"] = signalAB;
                        committedPairState["signal_b_to_a"] = signalBA;
                        committedPairState["tag_a_to_b"] = tagAB;
                        committedPairState["tag_b_to_a"] = tagBA;
                        committedPairState["shared_tag"] = lifecycleShared;
                        committedPairState["first_day"] = existing == null
                            ? firstPresenceDay
                            : ReadInt(existing, "first_day",
                                firstPresenceDay);
                        committedPairState["last_day"] = day;
                        committedPairState["consecutive_days"] = consecutive;
                        committedPairState["weighted_exposure"] = weighted;
                        committedPairState["last_context_kind"] =
                            latestPair.ContextKind;
                        committedPairState["last_context_id"] =
                            latestPair.ContextId;
                        committedPairState["last_roll_a_to_b"] = rollAB;
                        committedPairState["last_roll_b_to_a"] = rollBA;
                        committedPairState["last_delta_a_to_b"] = deltaAB;
                        committedPairState["last_delta_b_to_a"] = deltaBA;
                        committedPairState["projected_native_relation"] =
                            projectedNative;
                        committedPairState["native_action_pending"] =
                            pending ? 1 : 0;
                        committedPairState["native_action_id"] = actionId;
                        committedPairState["processing_shard"] =
                            processingShard;
                        committedPairState["last_presence_day"] =
                            lastPresenceDay;
                        committedPairState["last_batch_size"] =
                            scheduledDays.Count;
                        committedPairState["last_presence_mask"] =
                            presenceMask;
                        committedPairState["last_decay_period"] =
                            affinityAB != ReadInt(existing,
                                "affinity_a_to_b", affinityAB)
                            || affinityBA != ReadInt(existing,
                                "affinity_b_to_a", affinityBA)
                                ? Math.Max(0, day / 5)
                                : ReadInt(existing,
                                    "last_decay_period", -1);
                        committedPairState["compatibility_version"] =
                            MbtiChemistryVersion;
                        committedPairState["state_revision"] =
                            existing == null ? 1
                                : ReadLong(existing, "state_revision", 1) + 1;
                        committedPairState["updated_ts"] = ts;
                        pendingCacheRows[pair.PairKey] = committedPairState;
                        existingPairs[pair.PairKey] = committedPairState;
                        if (existing == null)
                        {
                            newPairCount++;
                            Dictionary<string, object> provenanceDetails =
                                new Dictionary<string, object>
                                {
                                    ["contextKind"] = latestPair.ContextKind,
                                    ["contextId"] = latestPair.ContextId
                                };
                            if (usePostgreSqlBatches)
                            {
                                provenanceWriteBatch.Add(
                                    new Dictionary<string, object>
                                    {
                                        ["campaign"] = campaignId,
                                        ["timeline"] = timelineId,
                                        ["pair"] = pair.PairKey,
                                        ["source"] = "co_presence",
                                        ["required"] = 0,
                                        ["day"] = (double)day,
                                        ["details"] =
                                            Json.Serialize(provenanceDetails),
                                        ["ts"] = ts
                                    });
                            }
                            else
                            {
                                RecordRelationshipPairProvenance(connection,
                                    campaignId, timelineId, pair.PairKey,
                                    "co_presence", false, day,
                                    provenanceDetails);
                            }
                        }
                        else
                        {
                            repeatEncounterCount++;
                        }
                        Dictionary<string, object> telemetryAfter = new Dictionary<string, object>
                        {
                            ["pairKey"] = pair.PairKey,
                            ["heroAId"] = pair.HeroAId,
                            ["heroBId"] = pair.HeroBId,
                            ["affinityAToB"] = affinityAB,
                            ["affinityBToA"] = affinityBA,
                            ["effectiveAffinityAToB"] = effectiveAB,
                            ["effectiveAffinityBToA"] = effectiveBA,
                            ["tagAToB"] = tagAB,
                            ["tagBToA"] = tagBA,
                            ["sharedTag"] = lifecycleShared,
                            ["mbtiA"] = typeA,
                            ["mbtiB"] = typeB,
                            ["notableA"] = ReadBool(heroA, "isNotable", false),
                            ["notableB"] = ReadBool(heroB, "isNotable", false),
                            ["deltaAToB"] = deltaAB,
                            ["deltaBToA"] = deltaBA,
                            ["contextKind"] = latestPair.ContextKind,
                            ["firstDay"] = existing == null ? firstPresenceDay : ReadInt(existing, "first_day", firstPresenceDay)
                        };
                        // World Test reads the authoritative relationship ledger.
                        // Maintaining its denormalized membership indexes here used
                        // to multiply every affinity change into dozens of SQLite
                        // writes and made diagnostics the simulation bottleneck.
                        WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters, "processedPairs", 1);
                        WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters, "rolledDirections",
                            pairRolledDirections);
                        WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters, "positiveRolls",
                            pairPositiveRolls);
                        WorldSimulationService.IncrementWorldTestObjectCounter(telemetryChunkCounters, "negativeRolls",
                            pairNegativeRolls);
                        telemetryChunkPairs++;
                        telemetryLastPairKey = pair.PairKey;
                        processed++;
                        if (telemetryChunkPairs == relationshipWriteChunkSize)
                        {
                            WorldSimulationService.RecordWorldTestCounterSchemaReady(connection, campaignId, timelineId, day, "relationships",
                                "pair_chunk_" + telemetryChunkIndex.ToString("D6", CultureInfo.InvariantCulture)
                                    + "_through_" + telemetryLastPairKey,
                                telemetryChunkCounters);
                            telemetryChunkIndex++;
                            telemetryChunkPairs = 0;
                            telemetryChunkCounters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        }
                        if (ReadBool(payload, "continuousWorker", false)
                            && processed % relationshipWriteChunkSize == 0)
                        {
                            FlushPostgreSqlRelationshipWriteBatches(connection,
                                pairUpsert, nativeTargetUpsert,
                                nativeTargetDelete, pairWriteBatch,
                                nativeTargetWriteBatch,
                                nativeTargetDeleteBatch);
                            FlushPostgreSqlRelationshipProvenanceBatch(connection,
                                provenanceWriteBatch);
                            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_daily_inputs SET
pair_cursor=$cursor,candidate_pairs=$candidates,processed_pairs=$processed
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day;",
                                new Dictionary<string, object>
                                {
                                    ["cursor"] = pair.PairKey, ["candidates"] = pairs.Count,
                                    ["processed"] = processed, ["campaign"] = campaignId,
                                    ["timeline"] = timelineId, ["day"] = day
                                });
                            ReignDatabase.ExecuteSql(connection, "COMMIT;");
                            StoreCommittedRelationshipPairCacheRows(
                                campaignId, pendingCacheRows);
                            pendingCacheRows.Clear();
                            ThrottleContinuousRelationshipChunk(evaluationUnitsSinceThrottle);
                            evaluationUnitsSinceThrottle = 0;
                            ReignDatabase.ExecuteSql(connection, "BEGIN IMMEDIATE;");
                        }
                    }
                    pairProcessingTimer.Stop();
                    pairProcessingMs = pairProcessingTimer.ElapsedMilliseconds;
                    Stopwatch independentLifecycleTimer = Stopwatch.StartNew();
                    // Active lifecycle pairs are evaluated every day they are
                    // actually co-present, even when the random social matcher
                    // selected different partners. They never receive an MBTI
                    // affinity roll through this path.
                    foreach (Dictionary<string, object> lifecycleRow
                        in independentLifecycleRows)
                    {
                        string lifecyclePairKey = ReadString(lifecycleRow,
                            "pair_key", "");
                        if (pairs.ContainsKey(lifecyclePairKey)) continue;
                        string heroAId = ReadString(lifecycleRow,
                            "hero_a_id", "");
                        string heroBId = ReadString(lifecycleRow,
                            "hero_b_id", "");
                        if (!RelationshipHeroesShareGroup(
                                coPresenceByDay.TryGetValue(day,
                                    out Dictionary<string, HashSet<int>> todayGroups)
                                    ? todayGroups : null,
                                heroAId, heroBId)
                            || !heroes.TryGetValue(heroAId,
                                out Dictionary<string, object> lifecycleHeroA)
                            || !heroes.TryGetValue(heroBId,
                                out Dictionary<string, object> lifecycleHeroB)
                            || !existingPairs.TryGetValue(lifecyclePairKey,
                                out Dictionary<string, object> lifecyclePairState))
                            continue;
                        int lifecycleAffinityAB = ReadInt(lifecyclePairState,
                            "affinity_a_to_b", 0);
                        int lifecycleAffinityBA = ReadInt(lifecyclePairState,
                            "affinity_b_to_a", 0);
                        int lifecycleEffectiveAB = Clamp(lifecycleAffinityAB
                            + ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                                timelineId, heroAId, heroBId), "value", 0), -100, 100);
                        int lifecycleEffectiveBA = Clamp(lifecycleAffinityBA
                            + ReadInt(ResolveObserverPublicStanding(connection, campaignId,
                                timelineId, heroBId, heroAId), "value", 0), -100, 100);
                        AmbientPairContext lifecyclePair =
                            new AmbientPairContext
                            {
                                PairKey = lifecyclePairKey,
                                HeroAId = heroAId,
                                HeroBId = heroBId,
                                ContextKind = "active_lifecycle",
                                ContextId = "daily"
                            };
                        Dictionary<string, object> lifecycle =
                            ProcessRelationshipLifecycle(connection, campaignId,
                                lifecyclePair, day, lifecycleHeroA,
                                lifecycleHeroB, lifecycleEffectiveAB,
                                lifecycleEffectiveBA, lifecycleRow, true,
                                timelineId);
                        string independentShared = ReadString(lifecycle,
                            "sharedTag", ReadString(lifecyclePairState,
                                "shared_tag", "neutral"));
                        if (!independentShared.Equals(ReadString(
                                lifecyclePairState, "shared_tag", ""),
                                StringComparison.OrdinalIgnoreCase))
                        {
                            ReignDatabase.ExecuteSql(connection, @"UPDATE relationship_pair_chemistry
SET shared_tag=$shared,state_revision=state_revision+1,updated_ts=$ts
WHERE pair_key=$pair;",
                                new Dictionary<string, object>
                                {
                                    ["shared"] = independentShared,
                                    ["ts"] = ts,
                                    ["pair"] = lifecyclePairKey
                                });
                            lifecyclePairState["shared_tag"] = independentShared;
                        }
                        lifecycleChanges += new[] { "loverStarted", "loverEnded",
                            "affairStarted", "affairEnded" }
                            .Count(key => ReadBool(lifecycle, key, false));
                        lifecycleRumors += ReadBool(lifecycle,
                            "rumorCreated", false) ? 1 : 0;
                        lifecycleActions += new[] { "marriageQueued",
                            "divorceQueued", "conceptionQueued" }
                            .Count(key => ReadBool(lifecycle, key, false));
                    }
                    independentLifecycleTimer.Stop();
                    independentLifecycleMs = independentLifecycleTimer.ElapsedMilliseconds;
                    Stopwatch finalizationTimer = Stopwatch.StartNew();
                    Stopwatch finalFlushTimer = Stopwatch.StartNew();
                    FlushPostgreSqlRelationshipWriteBatches(connection,
                        pairUpsert, nativeTargetUpsert, nativeTargetDelete,
                        pairWriteBatch, nativeTargetWriteBatch,
                        nativeTargetDeleteBatch);
                    finalPairFlushMs = finalFlushTimer.ElapsedMilliseconds;
                    FlushPostgreSqlRelationshipProvenanceBatch(connection,
                        provenanceWriteBatch);
                    finalProvenanceFlushMs = finalFlushTimer.ElapsedMilliseconds - finalPairFlushMs;
                    Stopwatch flingProcessingTimer = Stopwatch.StartNew();
                    flingSummary = ProcessDailyNpcFlings(connection, campaignId,
                        timelineId, groupsByDay, heroRowsByDay, heroes);
                    flingProcessingTimer.Stop();
                    flingProcessingMs = flingProcessingTimer.ElapsedMilliseconds;
                    List<Dictionary<string, object>> matchingGroups =
                        groupsByDay.TryGetValue(day,
                            out List<Dictionary<string, object>> currentGroups)
                            ? currentGroups
                            : new List<Dictionary<string, object>>();
                    int eligibleNpcCount = matchingGroups.SelectMany(group =>
                            ReadStringList(group, "heroIds"))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count();
                    WorldSimulationService.RecordWorldTestCounterSchemaReady(connection, campaignId,
                        timelineId, day, "relationships",
                        "daily_random_matching_summary",
                        new Dictionary<string, object>
                        {
                            ["socialPools"] = matchingGroups.Count,
                            ["eligibleNpcs"] = eligibleNpcCount,
                            ["selectedPairs"] = pairs.Count,
                            ["romanceContinuationPairDays"] =
                                romanceContinuationPairDays,
                            ["courtshipFocusPairDays"] =
                                courtshipFocusPairDays,
                            ["pairedNpcs"] = pairs.Count * 2,
                            ["unpairedNpcs"] = Math.Max(0,
                                eligibleNpcCount - pairs.Count * 2),
                            ["newPairs"] = newPairCount,
                            ["repeatEncounters"] = repeatEncounterCount,
                            ["rolledDirections"] = rolledDirections,
                            ["positiveDirections"] = positiveRolls,
                            ["negativeDirections"] = negativeRolls,
                            ["affinityMovement"] = affinityChanges,
                            ["magnitudeSides"] = OrdinaryRelationshipMagnitudeSides,
                            ["baseMagnitudeDie"] = "1d15",
                            ["loverMagnitudeDie"] = "1d20",
                            ["ordinaryMagnitudePairDays"] = ordinaryMagnitudePairDays,
                            ["loverMagnitudePairDays"] = loverMagnitudePairDays
                        });
                    if (telemetryChunkPairs > 0)
                    {
                        WorldSimulationService.RecordWorldTestCounterSchemaReady(connection, campaignId, timelineId, day, "relationships",
                            "pair_chunk_" + telemetryChunkIndex.ToString("D6", CultureInfo.InvariantCulture)
                                + "_through_" + telemetryLastPairKey,
                            telemetryChunkCounters);
                    }
                    WorldSimulationService.EnqueueWorldTestRollup(connection, campaignId, timelineId, day,
                        "relationship_day_completed");
                    finalFlushTimer.Stop();
                    finalFlushMs = finalFlushTimer.ElapsedMilliseconds;
                    finalTelemetryMs = finalFlushMs - finalPairFlushMs - finalProvenanceFlushMs - flingProcessingMs;

                    Stopwatch courtPopularityTimer = Stopwatch.StartNew();
                    List<string> changedCourtPopularitySubjects =
                        ReputationService.RecomputeCourtPopularityReputationsBatch(connection,
                            campaignId, timelineId,
                            courtPopularitySubjects, day,
                            "ambient_affinity_threshold|" + day);
                    if (changedCourtPopularitySubjects.Count > 0)
                    {
                        courtPopularityChanged = true;
                        ReputationService.ReconcileSocialRelationshipsForSubjects(connection,
                            campaignId, timelineId,
                            changedCourtPopularitySubjects, day);
                    }
                    courtPopularityTimer.Stop();
                    courtPopularityMs =
                        courtPopularityTimer.ElapsedMilliseconds;
                    Stopwatch nativeCountTimer = Stopwatch.StartNew();
                    bool continuousWorker = ReadBool(payload, "continuousWorker", false);
                    Dictionary<string, object> nativeSyncPlan = continuousWorker
                        ? new Dictionary<string, object>
                        {
                            ["targetCount"] = ReadInt(ReignDatabase.QuerySql(connection,
                                "SELECT COUNT(*) AS count FROM relationship_native_targets WHERE status IN ('pending','claimed','failed');")
                                .FirstOrDefault(), "count", 0)
                        }
                        : BuildRelationshipNativeSyncPlan(connection, campaignId, timelineId, day);
                    nativeQueued = ReadInt(nativeSyncPlan, "targetCount", nativeQueued);
                    nativeCountTimer.Stop();
                    nativeCountMs = nativeCountTimer.ElapsedMilliseconds;
                    finalizationTimer.Stop();
                    finalizationMs = finalizationTimer.ElapsedMilliseconds;
                    Dictionary<string, object> result = new Dictionary<string, object>
                    {
                        ["ok"] = true, ["runId"] = runId, ["campaignId"] = campaignId, ["worldDay"] = day,
                        ["groupCount"] = groupsByDay.Values.Sum(value => value.Count),
                        ["candidatePairs"] = pairs.Count,
                        ["selectedPairs"] = pairs.Count,
                        ["romanceContinuationPairDays"] =
                            romanceContinuationPairDays,
                        ["courtshipFocusPairDays"] =
                            courtshipFocusPairDays,
                        ["processedPairs"] = processed,
                        ["evaluatedPairDays"] = evaluatedPairDays,
                        ["cadenceDays"] = RelationshipCadenceDays,
                        ["cadenceShard"] = cadenceShard,
                        ["windowStartDay"] = cadenceWindowStartDay,
                        ["windowDayCount"] = cadenceWindowDayCount,
                        ["changedDirections"] = affinityChanges, ["rolledDirections"] = rolledDirections,
                        ["positiveRolls"] = positiveRolls, ["negativeRolls"] = negativeRolls,
                        ["nativeChangesQueued"] = nativeQueued,
                        ["socialPools"] = matchingGroups.Count,
                        ["eligibleNpcs"] = eligibleNpcCount,
                        ["pairedNpcs"] = pairs.Count * 2,
                        ["unpairedNpcs"] = Math.Max(0,
                            eligibleNpcCount - pairs.Count * 2),
                        ["newPairs"] = newPairCount,
                        ["repeatEncounters"] = repeatEncounterCount,
                        ["flings"] = flingSummary,
                        ["baseMagnitudeDie"] = "1d15",
                        ["loverMagnitudeDie"] = "1d20",
                        ["ordinaryMagnitudePairDays"] = ordinaryMagnitudePairDays,
                        ["loverMagnitudePairDays"] = loverMagnitudePairDays,
                        ["parallelWorkers"] = parallelWorkers,
                        ["parallelWorkItems"] = allPairDays.Count,
                        ["parallelComputeMs"] =
                            parallelTimer.ElapsedMilliseconds,
                        ["automaticNormalizationEnabled"] =
                            AutomaticRelationshipNormalizationEnabled,
                        ["decayMs"] = decayMs,
                        ["stateLoadMs"] = stateLoadMs,
                        ["sqlSetupMs"] = sqlReadTiming.SetupMs,
                        ["sqlExecuteMs"] = sqlReadTiming.ExecuteMs,
                        ["sqlMaterializeMs"] = sqlReadTiming.MaterializeMs,
                        ["sqlReadQueries"] = sqlReadTiming.Queries,
                        ["sqlReadRows"] = sqlReadTiming.Rows,
                        ["sqlReadProfile"] = sqlReadTiming.CaptureQueryProfiles ? sqlReadTiming.QueryProfiles : null,
                        ["stateLifecycleReadMs"] = stateLifecycleReadMs,
                        ["stateChemistryReadMs"] = stateChemistryReadMs,
                        ["stateNativeReadMs"] = stateNativeReadMs,
                        ["stateStandingReadMs"] = stateStandingReadMs,
                        ["loadedLifecycleRows"] = existingLifecycle.Count,
                        ["loadedChemistryRows"] = existingPairs.Count,
                        ["pairProcessingMs"] = pairProcessingMs,
                        ["finalizationMs"] = finalizationMs,
                        ["finalFlushMs"] = finalFlushMs,
                        ["finalPairFlushMs"] = finalPairFlushMs,
                        ["finalProvenanceFlushMs"] = finalProvenanceFlushMs,
                        ["finalTelemetryMs"] = finalTelemetryMs,
                        ["flingProcessingMs"] = flingProcessingMs,
                        ["independentLifecycleMs"] = independentLifecycleMs,
                        ["standingReadQueries"] = standingContext.StandingReads,
                        ["observerRosterReadQueries"] = standingContext.RosterReads,
                        ["playerRosterReadQueries"] = standingContext.PlayerReads,
                        ["courtPopularityMs"] = courtPopularityMs,
                        ["nativeCountMs"] = nativeCountMs,
                        ["postgresStagedRows"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipStagedRows),
                        ["postgresWrittenRows"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipWrittenRows),
                        ["postgresCopyMs"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipCopyMs),
                        ["postgresMergeMs"] =
                            Interlocked.Read(
                                ref PostgreSqlRelationshipMergeMs),
                        ["courtPopularitySubjects"] =
                            courtPopularitySubjects.Count,
                        ["courtPopularityChangedSubjects"] =
                            changedCourtPopularitySubjects.Count,
                        ["nativeSyncPlan"] = continuousWorker ? null : nativeSyncPlan,
                        ["fiveDayDecay"] = decay, ["lifecycleChanges"] = lifecycleChanges,
                        ["catchUpDays"] = catchUpDayCount,
                        ["lifecycleRumorsCreated"] = lifecycleRumors, ["lifecycleActionsQueued"] = lifecycleActions,
                        ["compatibilityModel"] = "jobcannon_directional_minus_10",
                        ["rollRule"] = "d100 <= adjusted score is positive; otherwise negative. Magnitude is 1d15, or 1d20 when the pair already has the lovers tag.",
                         ["affinityRule"] = "The signed co-location die changes that observer's directional Reign affinity immediately.",
                        ["telemetryPairScans"] = 0,
                         ["skippedReasons"] = new Dictionary<string, object>
                        {
                            ["missing_hero"] = skippedMissing,
                            ["settlement_exposure_gate"] = skippedSettlementGate
                        },
                        ["llmCalls"] = 0, ["noLlmConfirmed"] = true
                    };
                    Stopwatch commitTimer = Stopwatch.StartNew();
                    ReignDatabase.ExecuteSql(connection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES($key,$day);",
                        new Dictionary<string, object>
                        {
                            ["key"] = "mbti_relationship_last_processed_day:" + timelineId,
                            ["day"] = day.ToString(CultureInfo.InvariantCulture)
                        });
                    ReignDatabase.ExecuteSql(connection,
                        "INSERT OR REPLACE INTO schema_meta(key,value) VALUES('mbti_relationship_last_processed_day',$day);",
                        new Dictionary<string, object> { ["day"] = day.ToString(CultureInfo.InvariantCulture) });
                    ReignDatabase.ExecuteSql(connection, "COMMIT;");
                    commitTimer.Stop();
                    commitMs = commitTimer.ElapsedMilliseconds;
                    timer.Stop();
                    long nonParallelMs = Math.Max(0,
                        timer.ElapsedMilliseconds
                        - parallelTimer.ElapsedMilliseconds);
                    long measuredPhaseMs = payloadPreparationMs
                        + schemaReadinessMs + progressLoadMs
                        + lifecycleLoadMs + heroHydrationMs + matchingMs
                        + parallelTimer.ElapsedMilliseconds
                        + personalityLoadMs + commandPreparationMs
                        + transactionBeginMs + decayMs + stateLoadMs
                        + pairProcessingMs + independentLifecycleMs + finalizationMs + commitMs;
                    long unattributedMs = Math.Max(0,
                        timer.ElapsedMilliseconds - measuredPhaseMs);
                    result["durationMs"] = timer.ElapsedMilliseconds;
                    result["serialWriteMs"] = nonParallelMs;
                    result["nonParallelMs"] = nonParallelMs;
                    result["payloadPreparationMs"] = payloadPreparationMs;
                    result["schemaReadinessMs"] = schemaReadinessMs;
                    result["progressLoadMs"] = progressLoadMs;
                    result["lifecycleLoadMs"] = lifecycleLoadMs;
                    result["heroHydrationMs"] = heroHydrationMs;
                    result["heroDocumentCacheHits"] = heroDocumentCacheHits;
                    result["matchingMs"] = matchingMs;
                    result["personalityLoadMs"] = personalityLoadMs;
                    result["commandPreparationMs"] = commandPreparationMs;
                    result["transactionBeginMs"] = transactionBeginMs;
                    result["commitMs"] = commitMs;
                    result["unattributedMs"] = unattributedMs;
                    RecordRelationshipPhaseTimings(result);
                    StoreCommittedRelationshipPairCacheRows(
                        campaignId, pendingCacheRows);
                    pendingCacheRows.Clear();
                    if (courtPopularityChanged
                        && !campaignId.StartsWith("__", StringComparison.Ordinal))
                        ReputationService.SchedulePendingReputationReasonJobs(campaignId);
                    if (continuousWorker && evaluationUnitsSinceThrottle > 0)
                        ThrottleContinuousRelationshipChunk(evaluationUnitsSinceThrottle);
                        return result;
                    }
                    catch
                    {
                        try { ReignDatabase.ExecuteSql(connection, "ROLLBACK;"); } catch { }
                        throw;
                    }
                }
            }
        }
    }
}
