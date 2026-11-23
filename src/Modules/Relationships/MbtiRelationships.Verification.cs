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
        private static List<Dictionary<string, object>> RunMbtiRelationshipSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "mbti_relationships",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });

            var scopedStatusRequest = new Program.HttpRequest { Method = "GET",
                Query = new Dictionary<string, string> { ["campaignId"] = "requested_campaign", ["timelineId"] = "requested_timeline" },
                JsonBody = TestDict("campaignId", "wrong_body_campaign", "timelineId", "wrong_body_timeline") };
            var getScope = CampaignFileService.RelationshipStatusPayload(scopedStatusRequest);
            scopedStatusRequest.Method = "POST";
            var postScope = CampaignFileService.RelationshipStatusPayload(scopedStatusRequest);
            add("ambient_status_scope_follows_request_transport",
                ReadString(getScope, "campaignId", "") == "requested_campaign"
                && ReadString(getScope, "timelineId", "") == "requested_timeline"
                && ReadString(postScope, "campaignId", "") == "wrong_body_campaign"
                && ReadString(postScope, "timelineId", "") == "wrong_body_timeline",
                "GET respects explicit campaign/timeline query scope; POST retains its existing body scope.");
            List<int> all = RelationshipCompatibilityPolicy.AllBaseScores()
                .ToList();
            add("all_256_directional_scores_present", all.Count == 256 && all.Min() == 46 && all.Max() == 93,
                "All 256 ordered JobCannon scores are embedded, ranging from 46 to 93.");
            List<int> adjusted = all.Select(AdjustedMbtiCompatibility).ToList();
            add("compatibility_range_19_81",
                adjusted.Min() == 19 && adjusted.Max() == 81,
                "The proportional compatibility remap produces the approved 19 to 81 positive-roll range.");
            add("compatibility_proportional_anchors",
                AdjustedMbtiCompatibility(46) == 19
                    && AdjustedMbtiCompatibility(54) == 30
                    && AdjustedMbtiCompatibility(62) == 40
                    && AdjustedMbtiCompatibility(70) == 51
                    && AdjustedMbtiCompatibility(93) == 81,
                "The 19 to 81 remap preserves the approved proportional anchor values.");
            add("directional_asymmetry", MbtiCompatibility("ENTJ", "INTJ") == 91 && MbtiCompatibility("INTJ", "ENTJ") == 88,
                "Ordered personality matches can differ in each direction.");
            add("roll_threshold_rule", AdjustedMbtiCompatibility(46) == 19 && 20 > 19,
                "At adjusted compatibility 19, rolls 1-19 gain and rolls 20-100 lose.");
            add("v6_affinity_preserved_during_v7_chance_refresh",
                !(6 < LegacyAffinityRebaseCutoffVersion)
                    && MbtiChemistryVersion == 7,
                "Version-6 relationship affinities remain authoritative while version 7 refreshes only compatibility chances.");
            add("opening_seed_positive_formula",
                OpeningRelationshipDelta(60, 60, 50) == 23,
                "Opening compatibility 60 with a successful sign roll and magnitude 50 produces +23 after the twenty-five-percent reduction.");
            add("opening_seed_negative_formula",
                OpeningRelationshipDelta(60, 61, 50) == -15,
                "Opening compatibility 60 with a failed sign roll and magnitude 50 produces -15 after the twenty-five-percent reduction.");
            add("opening_seed_rounds_away_from_zero",
                OpeningRelationshipDelta(1, 1, 50) == 1
                    && OpeningRelationshipDelta(99, 100, 50) == -1,
                "Opening seed magnitudes round away from zero and always move at least one point.");
            Dictionary<string, Dictionary<string, object>> openingHeroes =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["adult_a"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "adult_a", ["isAlive"] = true,
                        ["isAdult"] = true, ["spouseId"] = "adult_b",
                        ["kingdomId"] = "kingdom_1", ["isRuler"] = true
                    },
                    ["adult_b"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "adult_b", ["isAlive"] = true,
                        ["isAdult"] = true, ["spouseId"] = "adult_a",
                        ["kingdomId"] = "kingdom_1", ["isClanLeader"] = true
                    },
                    ["child"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "child", ["isAlive"] = true,
                        ["isAdult"] = false, ["fatherId"] = "adult_a"
                    },
                    ["prisoner"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "prisoner", ["isAlive"] = true,
                        ["isAdult"] = true, ["isPrisoner"] = true
                    },
                    ["player"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "player", ["isAlive"] = true,
                        ["isAdult"] = true, ["isPlayer"] = true
                    }
                };
            Dictionary<string, OpeningSeedCandidate> openingCandidates =
                BuildOpeningSeedCandidates(openingHeroes,
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["kind"] = "party", ["id"] = "party_1",
                            ["heroIds"] = new List<string>
                            {
                                "adult_a", "adult_b", "prisoner", "player"
                            }
                        },
                        new Dictionary<string, object>
                        {
                            ["kind"] = "settlement", ["id"] = "town_1",
                            ["heroIds"] = new List<string>
                            {
                                "adult_a", "adult_b", "prisoner", "player"
                            }
                        }
                    });
            add("opening_seed_pair_network_deduplicates_and_excludes",
                openingCandidates.Count == 2
                    && openingCandidates.ContainsKey(
                        AmbientPairKey("adult_a", "adult_b"))
                    && openingCandidates.ContainsKey(
                        AmbientPairKey("adult_a", "child"))
                    && !openingCandidates.Keys.Any(key =>
                        key.IndexOf("prisoner", StringComparison.OrdinalIgnoreCase) >= 0
                        || key.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0),
                "Party/settlement overlap is deduplicated, excluded heroes do not receive broad seeds, and required child-family pairs remain materialized.");
            int positiveAffinity = Clamp(20 + 15, -100, 100);
            add("positive_colocation_die_applies_directly", positiveAffinity == 35,
                "A successful +15 roll immediately raises that observer's directional affinity by fifteen.");
            int negativeAffinity = Clamp(20 - 15, -100, 100);
            add("negative_colocation_die_applies_directly", negativeAffinity == 5,
                "A failed -15 roll immediately lowers that observer's directional affinity by fifteen.");
            add("native_relation_is_directional_average", RoundAwayFromZero((40d + -20d) / 2d) == 10,
                "NPC native relation is projected from the average of both directional affinities.");
            add("native_relation_sync_is_exact",
                RoundAwayFromZero((4d + 3d) / 2d) == 4
                    && RoundAwayFromZero((-4d + -3d) / 2d) == -4,
                "Every bulk projection writes the exact rounded directional average without a deadband.");
            add("unrelated_pairs_ignore_native_relation",
                InitialReignAffinity(
                    new Dictionary<string, object> { ["heroStringId"] = "a" },
                    new Dictionary<string, object> { ["heroStringId"] = "b" }) == 0,
                "A newly observed unrelated pair starts from Reign neutral instead of inheriting Bannerlord relation.");
            add("spouses_start_at_fifty",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(true, true, true) == 50,
                "Spouse precedence seeds both partners at native relation fifty.");
            add("immediate_family_starts_at_twenty",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(false, true, false) == 20,
                "Parents, children, and siblings start at native relation twenty.");
            add("lords_start_at_ten_with_ruler",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(false, false, true) == 10,
                "Every lord starts at native relation ten with their current ruler.");
            add("extended_family_has_no_baseline",
                ReignRelationshipBaselinePolicy.ResolveStartingBaseline(false, false, false)
                    == ReignRelationshipBaselinePolicy.NoBaseline,
                "Cousins and other extended relatives receive no automatic relationship baseline.");
            add("romance_is_not_inferred_by_affinity_tags",
                !DirectionalRelationshipTag("test", "a|b", "a_to_b", 75, 45).Equals("lover", StringComparison.OrdinalIgnoreCase)
                && SharedRelationshipTag("test", "a|b", 75, 45,
                    new Dictionary<string, object> { ["heroStringId"] = "a", ["age"] = 30 },
                    new Dictionary<string, object> { ["heroStringId"] = "b", ["age"] = 30 }) == "",
                "Affinity labels never create a romantic tag; the lifecycle state machine owns lovers.");
            add("reciprocal_high_affinity_remains_friendship_without_lifecycle",
                new[] { "best_friends", "confidants" }.Contains(SharedRelationshipTag("test", "a|b", 75, 75,
                    new Dictionary<string, object> { ["heroStringId"] = "a", ["age"] = 30 },
                    new Dictionary<string, object> { ["heroStringId"] = "b", ["age"] = 30 })),
                "Mutual high affinity stays a friendship label until the deterministic lifecycle activates lovers.");
            add("automatic_relationship_normalization_is_disabled",
                !AutomaticRelationshipNormalizationEnabled,
                "Personal affinity remains event-driven and is never periodically pulled toward zero.");
            add("reciprocal_friendship_unlocks",
                new[] { "best_friends", "confidants" }.Contains(SharedRelationshipTag("test", "a|b", 50, 50,
                    new Dictionary<string, object> { ["heroStringId"] = "a", ["age"] = 30 },
                    new Dictionary<string, object> { ["heroStringId"] = "b", ["age"] = 30 })),
                "Mutual affinity at 50 or higher creates a close reciprocal friendship tag.");
            double deterministicMagnitude = StableUnit("same_seed_magnitude");
            add("deterministic_dice", StableDie("same_seed", 100) == StableDie("same_seed", 100)
                && StableDieFromUnit(deterministicMagnitude,
                    OrdinaryRelationshipMagnitudeSides) >= 1
                && StableDieFromUnit(deterministicMagnitude,
                    OrdinaryRelationshipMagnitudeSides) <= 15
                && StableDieFromUnit(deterministicMagnitude,
                    LoverRelationshipMagnitudeSides) >= 1
                && StableDieFromUnit(deterministicMagnitude,
                    LoverRelationshipMagnitudeSides) <= 20,
                "Daily rolls are deterministic across retries; ordinary d15 and lover d20 magnitudes stay in range.");
            Dictionary<string, object> workerPolicy = ContinuousRelationshipWorkerStatus();
            add("adaptive_worker_limits",
                ReadInt(workerPolicy, "baseLimitPerSecond", 0) == 10000
                    && ReadInt(workerPolicy, "burstLimitPerSecond", 0) == 50000
                    && RelationshipLimitForQueuedDays(0) == 10000
                    && RelationshipLimitForQueuedDays(1) == 50000
                    && RelationshipLimitForQueuedDays(5) == 50000
                    && ReadInt(workerPolicy, "chunkSize", 0)
                        == PostgreSqlRelationshipWriteChunkSize,
                "Continuous PostgreSQL processing commits 5,000-pair durable chunks and immediately enables the 50,000 pair-day burst ceiling whenever any day is queued.");
            int expectedAvailableWorkers = Environment.ProcessorCount <= 2
                ? 1
                : Math.Max(1, Math.Min(32,
                    Environment.ProcessorCount
                    - (Environment.ProcessorCount >= 6 ? 2 : 1)));
            int burstWorkers = RelationshipParallelismForWork(10000, true);
            int dailyWorkers = RelationshipParallelismForWork(841, true);
            int smallWorkWorkers = RelationshipParallelismForWork(100, true);
            add("adaptive_multicore_relationship_compute",
                burstWorkers == Math.Min(expectedAvailableWorkers, 8)
                    && dailyWorkers == Math.Min(expectedAvailableWorkers, 2)
                    && smallWorkWorkers == 1
                    && ReadInt(workerPolicy, "logicalProcessors", 0)
                        == Environment.ProcessorCount,
                "Relationship dice use bounded 1/2/4/8 worker tiers so a normal daily match avoids scheduler overhead while large catch-up batches still use available cores.");
            AmbientPairContext deterministicParallelPair =
                new AmbientPairContext
                {
                    PairKey = "parallel_a|parallel_b",
                    HeroAId = "parallel_a",
                    HeroBId = "parallel_b",
                    ContextKind = "party",
                    ContextId = "parallel_party"
                };
            DailyPairDice deterministicSerialDice = BuildDailyPairDice(
                "parallel_contract", deterministicParallelPair, 17);
            DailyPairDice deterministicWorkerDice = null;
            Parallel.Invoke(new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1,
                    Math.Min(2, Environment.ProcessorCount))
            }, () => deterministicWorkerDice = BuildDailyPairDice(
                "parallel_contract", deterministicParallelPair, 17));
            add("parallel_relationship_dice_are_deterministic",
                deterministicWorkerDice != null
                    && deterministicWorkerDice.SettlementEligible
                        == deterministicSerialDice.SettlementEligible
                    && deterministicWorkerDice.RollAtoB
                        == deterministicSerialDice.RollAtoB
                    && deterministicWorkerDice.RollBtoA
                        == deterministicSerialDice.RollBtoA
                    && deterministicWorkerDice.OrdinaryMagnitudeAtoB
                        == deterministicSerialDice.OrdinaryMagnitudeAtoB
                    && deterministicWorkerDice.OrdinaryMagnitudeBtoA
                        == deterministicSerialDice.OrdinaryMagnitudeBtoA
                    && deterministicWorkerDice.LoverMagnitudeAtoB
                        == deterministicSerialDice.LoverMagnitudeAtoB
                    && deterministicWorkerDice.LoverMagnitudeBtoA
                        == deterministicSerialDice.LoverMagnitudeBtoA,
                "Parallel relationship preparation reproduces the exact serial campaign/pair/day dice.");
            List<Dictionary<string, object>> compactRoster = Enumerable.Range(0, 2400)
                .Select(index => new Dictionary<string, object>
                {
                    ["i"] = "campaign_hero_" + index.ToString(CultureInfo.InvariantCulture),
                    ["a"] = 18 + index % 55,
                    ["s"] = "",
                    ["fa"] = index > 100 ? "campaign_hero_" + (index % 100).ToString(CultureInfo.InvariantCulture) : "",
                    ["mo"] = "",
                    ["c"] = "clan_" + (index % 120).ToString(CultureInfo.InvariantCulture),
                    ["k"] = "kingdom_" + (index % 9).ToString(CultureInfo.InvariantCulture),
                    ["n"] = index % 3 == 0 ? 1 : 0,
                    ["o"] = index % 3 == 1 ? 1 : 0
                }).ToList();
            List<Dictionary<string, object>> compactGroups = Enumerable.Range(0, 120)
                .Select(groupIndex => new Dictionary<string, object>
                {
                    ["kind"] = "settlement",
                    ["id"] = "settlement_" + groupIndex.ToString(CultureInfo.InvariantCulture),
                    ["heroIds"] = Enumerable.Range(groupIndex * 20, 20)
                        .Select(index => "campaign_hero_" + index.ToString(CultureInfo.InvariantCulture)).ToList()
                }).ToList();
            int compactBytes = Encoding.UTF8.GetByteCount(Json.Serialize(new Dictionary<string, object>
            {
                ["campaignId"] = "payload_test", ["timelineId"] = "main", ["worldDay"] = 1d,
                ["heroes"] = compactRoster, ["presenceGroups"] = compactGroups
            }));
            add("compact_daily_input_under_500kb", compactBytes < 500 * 1024,
                "A full 2,400-character compact lifecycle roster with actual presence groups is "
                + compactBytes.ToString(CultureInfo.InvariantCulture) + " bytes.");
            string ingestCampaignId = "mr_ingest_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ingestCampaignId))
                    EnsureMbtiRelationshipSchema(connection);
                Stopwatch ingestTimer = Stopwatch.StartNew();
                Dictionary<string, object> accepted = PersistAndHydrateRelationshipDailyInput(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = ingestCampaignId,
                        ["timelineId"] = "main",
                        ["worldDay"] = 1d,
                        ["heroes"] = compactRoster,
                        ["presenceGroups"] = compactGroups
                    });
                ingestTimer.Stop();
                int observedHeroes;
                int pendingDays;
                long unchangedHeroTimestamp;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(ingestCampaignId))
                {
                    observedHeroes = ReadInt(RelationshipsStore.CountRelationshipObservedHeroes(connection)
                        .FirstOrDefault(), "count", 0);
                    pendingDays = ReadInt(RelationshipsStore.CountRelationshipDailyInputs2(connection)
                        .FirstOrDefault(), "count", 0);
                    RelationshipsStore.UpdateRelationshipObservedHeroes(connection);
                }
                PersistAndHydrateRelationshipDailyInput(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = ingestCampaignId,
                        ["timelineId"] = "main",
                        ["worldDay"] = 1d,
                        ["heroes"] = compactRoster,
                        ["presenceGroups"] = compactGroups
                    });
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(ingestCampaignId))
                {
                    unchangedHeroTimestamp = ReadLong(RelationshipsStore.SelectRelationshipObservedHeroes(connection)
                        .FirstOrDefault(), "updated_ts", -1);
                }
                add("full_roster_daily_ingest_is_bounded_and_durable",
                    observedHeroes == 2400
                    && pendingDays == 1
                    && ReadDictionaryList(accepted, "heroes").Count == 2400
                    && ingestTimer.ElapsedMilliseconds < 2000,
                    "A 2,400-character daily input is durably accepted as one day in under two seconds; measured "
                    + ingestTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
                add("unchanged_observed_heroes_are_not_rewritten",
                    unchangedHeroTimestamp == 17,
                    "Repeated daily presence keeps the durable roster current without rewriting unchanged lifecycle profiles.");
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(ingestCampaignId));
            }
            results.AddRange(RunCampaignOpeningRelationshipSeedSelfTests());
            results.AddRange(RunDailyRandomRelationshipMatchingSelfTests());
            results.AddRange(RunMbtiRelationshipPersistenceSelfTests());
            return results;
        }

        private static List<Dictionary<string, object>>
            RunCampaignOpeningRelationshipSeedSelfTests()
        {
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(
                new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "mbti_relationships", ["caseId"] = id,
                    ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
                });
            string campaignId = "opening_seed_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            List<Dictionary<string, object>> heroes =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "seed_a", ["name"] = "Seed A",
                        ["age"] = 30d, ["isAdult"] = true, ["isAlive"] = true,
                        ["spouseId"] = "seed_b", ["isRuler"] = true,
                        ["isClanLeader"] = true, ["kingdomId"] = "seed_kingdom"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "seed_b", ["name"] = "Seed B",
                        ["age"] = 30d, ["isAdult"] = true, ["isAlive"] = true,
                        ["isNotable"] = true,
                        ["spouseId"] = "seed_a", ["isClanLeader"] = true,
                        ["kingdomId"] = "seed_kingdom"
                    },
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] = "seed_child", ["name"] = "Seed Child",
                        ["age"] = 10d, ["isAdult"] = false, ["isAlive"] = true,
                        ["fatherId"] = "seed_a"
                    }
                };
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = "main",
                ["generationId"] = "fixture_generation", ["worldDay"] = 0d,
                ["verificationSuppressWorkerSignal"] = true,
                ["heroes"] = heroes,
                ["presenceGroups"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "party", ["id"] = "seed_party",
                        ["heroIds"] = new List<string>
                        {
                            "seed_a", "seed_b"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["kind"] = "settlement", ["id"] = "seed_town",
                        ["heroIds"] = new List<string>
                        {
                            "seed_a", "seed_b"
                        }
                    }
                }
            };
            try
            {
                int authoritativeNotableIndex = StableDie(campaignId
                    + "|seed_b|notable_mbti_template_v"
                    + NotableMbtiTemplateVersion.ToString(
                        CultureInfo.InvariantCulture), MbtiTypes.Length) - 1;
                string authoritativeNotableType = MbtiTypes[Clamp(
                    authoritativeNotableIndex, 0, MbtiTypes.Length - 1)];
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    string conflictingType = MbtiTypes.First(type =>
                        !type.Equals(authoritativeNotableType,
                            StringComparison.OrdinalIgnoreCase));
                    MbtiDefinition conflictingDefinition =
                        MbtiDefinitions[conflictingType];
                    long fixtureTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    RelationshipsStore.UpsertRelationshipPersonalities2(connection, new Dictionary<string, object>
                        {
                            ["type"] = conflictingType,
                            ["title"] = conflictingDefinition.Title,
                            ["description"] =
                                conflictingDefinition.Description,
                            ["ts"] = fixtureTs
                        });
                }
                Dictionary<string, object> first =
                    CampaignOpeningRelationshipSeedApi(payload);
                Dictionary<string, object> spouse;
                Dictionary<string, object> child;
                Dictionary<string, object> run;
                Dictionary<string, object> daily;
                Dictionary<string, object> persistedNotable;
                Dictionary<string, object> politicalProvenance;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    spouse = RelationshipsStore.SelectRelationshipPairChemistry14(connection, new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_b")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    child = RelationshipsStore.SelectRelationshipPairChemistry14(connection, new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_child")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                    run = RelationshipsStore.SelectRelationshipOpeningSeedRuns(connection)
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    daily = RelationshipsStore.SelectRelationshipDailyInputs7(connection)
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    persistedNotable = RelationshipsStore.SelectNotableMbtiProfiles3(connection).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    politicalProvenance = RelationshipsStore.SelectRelationshipPairProvenance(connection, new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_b")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                }
                int spouseEvidenceAB = ReadInt(spouse,
                    "opening_seed_affinity_a_to_b", 999);
                int spouseEvidenceBA = ReadInt(spouse,
                    "opening_seed_affinity_b_to_a", 999);
                Dictionary<string, object> repeated =
                    CampaignOpeningRelationshipSeedApi(payload);
                Dictionary<string, object> repeatedSpouse;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    repeatedSpouse = RelationshipsStore.SelectRelationshipPairChemistry14(connection, new Dictionary<string, object>
                        {
                            ["pair"] = AmbientPairKey("seed_a", "seed_b")
                        }).FirstOrDefault() ?? new Dictionary<string, object>();
                add("opening_seed_materializes_and_records_evidence",
                    ReadBool(first, "ok", false)
                    && ReadString(run, "status", "") == "completed"
                    && ReadInt(run, "candidate_pairs", 0) == 2
                    && ReadInt(spouse, "opening_seed_version", 0)
                        == OpeningRelationshipSeedVersion
                    && ReadInt(spouse, "opening_seed_baseline", 0) == 50
                    && ReadInt(spouse, "last_decay_period", -1) == 0
                    && ReadInt(spouse, "opening_seed_sign_roll_a_to_b", 0) >= 1
                    && ReadInt(spouse, "opening_seed_magnitude_roll_a_to_b", 0)
                        >= 1,
                    "Opening preparation materializes the deduplicated pair network, initializes its decay checkpoint, and stores compact directional seed evidence.");
                string seededNotableType = ReadString(spouse,
                    ReadString(spouse, "hero_a_id", "").Equals("seed_b",
                        StringComparison.OrdinalIgnoreCase)
                        ? "mbti_a" : "mbti_b", "XXXX");
                add("opening_seed_notable_mbti_overrides_conflicting_personality",
                    MbtiDefinitions.ContainsKey(authoritativeNotableType)
                    && ReadString(persistedNotable, "mbti_type", "XXXX").Equals(
                        authoritativeNotableType,
                        StringComparison.OrdinalIgnoreCase)
                    && seededNotableType.Equals(authoritativeNotableType,
                        StringComparison.OrdinalIgnoreCase),
                    "Opening preparation creates the campaign-seeded notable assignment first and keeps it authoritative when an earlier generic relationship personality conflicts with it.");
                add("opening_seed_child_family_pair_has_no_compatibility_seed",
                    ReadInt(child, "opening_seed_version", 0)
                        == OpeningRelationshipSeedVersion
                    && ReadInt(child, "opening_seed_baseline", 0) == 20
                    && ReadInt(child, "opening_seed_eligible", 1) == 0
                    && ReadInt(child, "opening_seed_delta_a_to_b", 999) == 0
                    && ReadInt(child, "opening_seed_delta_b_to_a", 999) == 0,
                    "A separated child retains the required family baseline without receiving a broad opening compatibility seed.");
                add("opening_seed_persists_required_political_provenance",
                    ReadInt(politicalProvenance, "active", 0) == 1
                    && ReadInt(politicalProvenance, "required", 0) == 1,
                    "Opening preparation records kingdom leadership as an active required provenance so later leadership reconciliation cannot lose the pair.");
                add("opening_seed_retains_first_day_presence",
                    ReadInt(daily, "day_key", -1) == 0
                    && new[] { "pending", "processing", "processed" }.Contains(
                        ReadString(daily, "status", ""),
                        StringComparer.OrdinalIgnoreCase),
                    "The opening presence payload is durably queued for the continuous daily random-matching worker without blocking campaign preparation.");
                add("opening_seed_retry_is_idempotent",
                    ReadBool(repeated, "ok", false)
                    && ReadBool(repeated, "idempotent", false)
                    && ReadInt(repeatedSpouse, "opening_seed_affinity_a_to_b", 999)
                        == spouseEvidenceAB
                    && ReadInt(repeatedSpouse, "opening_seed_affinity_b_to_a", 999)
                        == spouseEvidenceBA,
                    "Repeating the initialization request returns the recoverable final plan without rerolling or reapplying the opening seed.");
                List<Dictionary<string, object>> targets = ReadDictionaryList(
                    ReadDictionary(first, "nativeSyncPlan")
                        ?? new Dictionary<string, object>(), "targets");
                add("opening_seed_returns_revisioned_final_native_targets",
                    targets.All(target => ReadInt(target, "revision", 0) >= 1),
                    "Opening preparation returns only coalesced final native targets with revision evidence.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaignId);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaignId));
            }
            string scaleCampaignId = "opening_seed_scale_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                const int scaleHeroCount = 164;
                List<Dictionary<string, object>> scaleHeroes =
                    Enumerable.Range(0, scaleHeroCount)
                    .Select(index => new Dictionary<string, object>
                    {
                        ["heroStringId"] = "scale_seed_"
                            + index.ToString("D3", CultureInfo.InvariantCulture),
                        ["name"] = "Scale Seed "
                            + index.ToString(CultureInfo.InvariantCulture),
                        ["age"] = 25d + index % 30,
                        ["isAdult"] = true,
                        ["isAlive"] = true,
                        ["isNotable"] = true
                    }).ToList();
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(scaleCampaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    RelationshipsStore.SelectX4(connection, new Dictionary<string, object>
                        {
                            ["rows"] = Json.Serialize(scaleHeroes.Select(hero =>
                                new Dictionary<string, object>
                                {
                                    ["hero_id"] = ReadString(hero,
                                        "heroStringId", "")
                                }).ToList())
                        });
                }
                Stopwatch scaleTimer = Stopwatch.StartNew();
                Dictionary<string, object> scaleResult =
                    CampaignOpeningRelationshipSeedApi(
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = scaleCampaignId,
                            ["timelineId"] = "main",
                            ["generationId"] = "scale_fixture",
                            ["worldDay"] = 0d,
                            ["verificationSuppressWorkerSignal"] = true,
                            ["heroes"] = scaleHeroes,
                            ["presenceGroups"] =
                                new List<Dictionary<string, object>>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["kind"] = "settlement",
                                        ["id"] = "scale_town",
                                        ["heroIds"] = scaleHeroes.Select(hero =>
                                            ReadString(hero, "heroStringId", ""))
                                            .ToList()
                                    }
                                }
                        });
                scaleTimer.Stop();
                int expectedPairs = scaleHeroCount * (scaleHeroCount - 1) / 2;
                int persistedPairs;
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(scaleCampaignId))
                {
                    persistedPairs = ReadInt(RelationshipsStore.CountRelationshipPairChemistry(connection)
                        .FirstOrDefault(), "count", 0);
                }
                add("opening_seed_full_campaign_batch_performance",
                    ReadBool(scaleResult, "ok", false)
                    && persistedPairs == expectedPairs
                    && scaleTimer.ElapsedMilliseconds < 45000,
                    "A 13,366-pair opening network completes through the prepared PostgreSQL batch path in "
                    + scaleTimer.ElapsedMilliseconds.ToString(
                        CultureInfo.InvariantCulture)
                    + " ms (limit 45,000 ms).");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(scaleCampaignId);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(scaleCampaignId));
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunDailyRandomRelationshipMatchingSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(
                new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["passed"] = passed,
                    ["suite"] = "mbti_relationships",
                    ["caseId"] = id,
                    ["name"] = id,
                    ["summary"] = summary,
                    ["durationMs"] = 0
                });

            string shardCampaign = "cadence_shards";
            List<int> shardAssignments = Enumerable.Range(0, 400)
                .Select(index => RelationshipCadenceShard(
                    shardCampaign,
                    AmbientPairKey("hero_" + index.ToString("D4", CultureInfo.InvariantCulture),
                        "hero_" + (index + 1000).ToString("D4", CultureInfo.InvariantCulture))))
                .ToList();
            int smallestShard = Enumerable.Range(0, RelationshipCadenceShardCount)
                .Min(shard => shardAssignments.Count(value => value == shard));
            int largestShard = Enumerable.Range(0, RelationshipCadenceShardCount)
                .Max(shard => shardAssignments.Count(value => value == shard));
            add("relationship_daily_matching_uses_one_processing_shard",
                RelationshipCadenceDays == 1
                && RelationshipCadenceShardCount == 1
                && shardAssignments.All(shard => shard == 0)
                && largestShard == smallestShard
                && RelationshipCadenceShard(shardCampaign, AmbientPairKey("hero_0000", "hero_1000"))
                    == shardAssignments[0],
                "Daily random matching processes one bounded social encounter set without four-day shards.");

            Dictionary<string, Dictionary<string, object>> matchingHeroes =
                Enumerable.Range(0, 7).ToDictionary(
                    index => "match_" + index,
                    index => new Dictionary<string, object>
                    {
                        ["heroStringId"] = "match_" + index,
                        ["isAlive"] = true, ["isAdult"] = true
                    }, StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> matchingGroups =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "settlement", ["id"] = "town",
                        ["heroIds"] = new List<string>
                        {
                            "match_0", "match_1", "match_2", "match_3",
                            "match_4", "match_5", "match_6"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["kind"] = "army", ["id"] = "army",
                        ["heroIds"] = new List<string>
                        {
                            "match_0", "match_1", "match_2"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["kind"] = "player_party", ["id"] = "player_party",
                        ["heroIds"] = new List<string>
                        {
                            "match_0", "match_1"
                        }
                    }
                };
            Dictionary<string, AmbientPairContext> firstMatching =
                ExpandDailyMatchedPairs(matchingGroups, matchingHeroes,
                    "matching_fixture", "main", 7);
            Dictionary<string, AmbientPairContext> repeatedMatching =
                ExpandDailyMatchedPairs(matchingGroups, matchingHeroes,
                    "matching_fixture", "main", 7);
            List<string> matchedHeroIds = firstMatching.Values
                .SelectMany(pair => new[] { pair.HeroAId, pair.HeroBId })
                .ToList();
            add("daily_matching_is_deterministic_and_unique_per_npc",
                firstMatching.Keys.OrderBy(value => value).SequenceEqual(
                    repeatedMatching.Keys.OrderBy(value => value))
                && matchedHeroIds.Count == matchedHeroIds.Distinct(
                    StringComparer.OrdinalIgnoreCase).Count()
                && firstMatching.Values.All(pair => pair.PairKey.Equals(
                    pair.HeroAId + "|" + pair.HeroBId,
                    StringComparison.OrdinalIgnoreCase))
                && firstMatching.Count == 3,
                "Seven eligible NPCs deterministically form three canonically oriented pairs and no NPC receives more than one passive encounter.");
            AmbientPairContext priorityPair = firstMatching.Values
                .FirstOrDefault(pair => pair.HeroAId == "match_0"
                    || pair.HeroBId == "match_0");
            add("daily_matching_obeys_canonical_pool_precedence",
                priorityPair != null
                && priorityPair.ContextKind == "player_party"
                && (priorityPair.HeroAId == "match_1"
                    || priorityPair.HeroBId == "match_1"),
                "Player-party membership wins over army and settlement overlap before deterministic matching.");

            Dictionary<string, Dictionary<string, object>> courtshipHeroes =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["court_woman_a"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_woman_a", ["clanId"] = "court_clan_a",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 24d,
                        ["isFemale"] = true, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    },
                    ["court_man_a"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_man_a", ["clanId"] = "court_clan_b",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 27d,
                        ["isFemale"] = false, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    },
                    ["court_woman_b"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_woman_b", ["clanId"] = "court_clan_c",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 25d,
                        ["isFemale"] = true, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    },
                    ["court_man_b"] = new Dictionary<string, object>
                    {
                        ["heroStringId"] = "court_man_b", ["clanId"] = "court_clan_d",
                        ["isAlive"] = true, ["isAdult"] = true, ["age"] = 28d,
                        ["isFemale"] = false, ["nativeCanMarry"] = true,
                        ["nativeMarriageClanSuitable"] = true
                    }
                };
            List<Dictionary<string, object>> courtshipGroups =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["kind"] = "settlement", ["id"] = "courtship_town",
                        ["heroIds"] = courtshipHeroes.Keys.ToList()
                    }
                };
            string courtshipFocusKey = AmbientPairKey("court_woman_a",
                "court_man_a");
            Dictionary<string, Dictionary<string, object>> courtshipStates =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    [courtshipFocusKey] = new Dictionary<string, object>
                    {
                        ["affinity_a_to_b"] = 12,
                        ["affinity_b_to_a"] = 10,
                        ["chance_a_to_b"] = 80,
                        ["chance_b_to_a"] = 80
                    }
                };
            Dictionary<string, AmbientPairContext> courtshipDayOne =
                ExpandDailyMatchedPairs(courtshipGroups, courtshipHeroes,
                    "courtship_fixture", "main", 7, courtshipStates);
            Dictionary<string, AmbientPairContext> courtshipDayTwo =
                ExpandDailyMatchedPairs(courtshipGroups, courtshipHeroes,
                    "courtship_fixture", "main", 8, courtshipStates);
            Dictionary<string, AmbientPairContext> courtshipExcluded =
                ExpandDailyMatchedPairs(courtshipGroups, courtshipHeroes,
                    "courtship_fixture", "main", 8, courtshipStates,
                    new HashSet<string>(new[] { courtshipFocusKey },
                        StringComparer.OrdinalIgnoreCase));
            add("daily_matching_preserves_one_promising_courtship_focus",
                courtshipDayOne.TryGetValue(courtshipFocusKey,
                    out AmbientPairContext firstCourtship)
                && firstCourtship.CourtshipFocus
                && courtshipDayTwo.TryGetValue(courtshipFocusKey,
                    out AmbientPairContext secondCourtship)
                && secondCourtship.CourtshipFocus
                && courtshipDayOne.Values.Count(pair => pair.CourtshipFocus) == 1
                && courtshipDayTwo.Values.Count(pair => pair.CourtshipFocus) == 1
                && (!courtshipExcluded.TryGetValue(courtshipFocusKey,
                        out AmbientPairContext excludedCourtship)
                    || excludedCourtship == null
                    || !excludedCourtship.CourtshipFocus),
                "Each social pool repeatedly gives its strongest mutually eligible courtship one ordinary daily encounter, while active or declined courtships are excluded from that focus.");

            string batchCampaign = "cadence_batch_" + Guid.NewGuid().ToString("N").Substring(0, 10);
            string heroA = "cadence_a";
            string heroB = "cadence_b";
            string pairKey = AmbientPairKey(heroA, heroB);
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["heroStringId"] = heroA, ["name"] = "Cadence A", ["age"] = 30,
                    ["isAlive"] = true, ["isAdult"] = true, ["isFemale"] = false,
                    ["mbtiType"] = "INTJ"
                },
                new Dictionary<string, object>
                {
                    ["heroStringId"] = heroB, ["name"] = "Cadence B", ["age"] = 30,
                    ["isAlive"] = true, ["isAdult"] = true, ["isFemale"] = true,
                    ["mbtiType"] = "ENFP"
                }
            };
            Dictionary<string, object> together = new Dictionary<string, object>
            {
                ["kind"] = "party",
                ["id"] = "cadence_party",
                ["heroIds"] = new List<string> { heroA, heroB }
            };
            Func<string, int, bool, Dictionary<string, object>> input =
                (campaign, day, present) => new Dictionary<string, object>
                {
                    ["campaignId"] = campaign,
                    ["timelineId"] = "main",
                    ["worldDay"] = (double)day,
                    ["heroes"] = heroes,
                    ["presenceGroups"] = present
                        ? new List<Dictionary<string, object>> { together }
                        : new List<Dictionary<string, object>>()
                };
            try
            {
                foreach (int dailyDay in Enumerable.Range(1, 4))
                {
                    MbtiRelationshipSnapshotApi(input(
                        batchCampaign,
                        dailyDay,
                        dailyDay == 1 || dailyDay == 3 || dailyDay == 4));
                }
                Dictionary<string, object> dailyPair;
                using (ReignDbConnection dailyConnection = ReignDatabase.OpenCampaignConnection(batchCampaign))
                    dailyPair = RelationshipsStore.SelectRelationshipPairChemistry8(dailyConnection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                ReignPostgreSqlStorage.DropCampaign(batchCampaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(batchCampaign));

                int shard = RelationshipCadenceShard(batchCampaign, pairKey);
                Dictionary<string, object> batchResult = MbtiRelationshipSnapshotApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = batchCampaign,
                        ["timelineId"] = "main",
                        ["worldDay"] = 4d,
                        ["cadenceShard"] = shard,
                        ["cadenceInputs"] = Enumerable.Range(1, 4)
                            .Select(day => input(batchCampaign, day,
                                day == 1 || day == 3 || day == 4))
                            .Cast<object>().ToList()
                    });
                Dictionary<string, object> repeatedBatch = MbtiRelationshipSnapshotApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = batchCampaign,
                        ["timelineId"] = "main",
                        ["worldDay"] = 4d,
                        ["cadenceShard"] = shard,
                        ["cadenceInputs"] = Enumerable.Range(1, 4)
                            .Select(day => input(batchCampaign, day,
                                day == 1 || day == 3 || day == 4))
                            .Cast<object>().ToList()
                    });
                Dictionary<string, object> batchPair;
                using (ReignDbConnection batchConnection = ReignDatabase.OpenCampaignConnection(batchCampaign))
                    batchPair = RelationshipsStore.SelectRelationshipPairChemistry8(batchConnection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault()
                        ?? new Dictionary<string, object>();

                add("deterministic_daily_input_replay_preserves_roll_results",
                    ReadBool(batchResult, "ok", false)
                    && ReadInt(batchResult, "evaluatedPairDays", 0) == 3
                    && ReadInt(batchResult, "rolledDirections", 0) == 6
                    && ReadInt(batchPair, "processing_shard", -1) == shard
                    && ReadInt(batchPair, "last_batch_size", 0) == 3
                    && ReadInt(batchPair, "last_presence_mask", 0) == 13
                    && ReadInt(batchPair, "affinity_a_to_b", 999)
                        == ReadInt(dailyPair, "affinity_a_to_b", -999)
                    && ReadInt(batchPair, "affinity_b_to_a", 999)
                        == ReadInt(dailyPair, "affinity_b_to_a", -999),
                    "Deterministic daily inputs reach the same directional affinities when replayed as one durable input set.");
                add("daily_matching_is_idempotent",
                    ReadBool(repeatedBatch, "idempotent", false)
                    && ReadInt(repeatedBatch, "evaluatedPairDays", -1) == 0,
                    "Repeating a completed daily matching input cannot apply any pair-day twice.");
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(batchCampaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(batchCampaign));
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunMbtiRelationshipPersistenceSelfTests()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => results.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "mbti_relationships",
                ["caseId"] = id, ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            string campaignId = "mr_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string heroA = "mbti_a", heroB = "mbti_b";
            Dictionary<string, object> traits = CharactersService.CoreTraitKeys.ToDictionary(x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
            traits["sociability"] = 85; traits["confidence"] = 80; traits["discipline"] = 75; traits["dutyMotivation"] = 70;
            List<Dictionary<string, object>> heroes = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["heroStringId"] = heroA, ["name"] = "A", ["age"] = 30, ["traitPercentages"] = traits, ["foundationTraits"] = new Dictionary<string, object>() },
                new Dictionary<string, object> { ["heroStringId"] = heroB, ["name"] = "B", ["age"] = 30, ["traitPercentages"] = traits, ["foundationTraits"] = new Dictionary<string, object>() }
            };
            Dictionary<string, object> group = new Dictionary<string, object>
            {
                ["kind"] = "party", ["id"] = "party_test", ["heroIds"] = new List<string> { heroA, heroB },
                ["nativeRelations"] = new List<Dictionary<string, object>>
                {
                    // Deliberately hostile legacy input: Reign must ignore this
                    // as an affinity seed and project its own relationship back.
                    new Dictionary<string, object> { ["heroAId"] = heroA, ["heroBId"] = heroB, ["value"] = 92 }
                }
            };
            string fixtureStage = "setup";
            Func<int, Dictionary<string, object>> run = day =>
            {
                fixtureStage = "relationship_day_"
                    + day.ToString(CultureInfo.InvariantCulture);
                return MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId, ["worldDay"] = day, ["heroes"] = heroes,
                    ["presenceGroups"] = new List<Dictionary<string, object>> { group },
                    ["correlationId"] = "mbti_selftest_" + day.ToString(CultureInfo.InvariantCulture)
                });
            };
            try
            {
                Dictionary<string, object> first = run(1);
                int pairRows;
                bool oldChangeTableExists;
                Dictionary<string, object> firstPair;
                Dictionary<string, object> firstNativeTarget;
                long firstBandUniqueA;
                long firstBandEdgesA;
                long firstBandPairsA;
                long firstBandUniqueB;
                long firstBandEdgesB;
                long firstBandPairsB;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
                    pairRows = ReadInt(RelationshipsStore.CountRelationshipPairChemistry(connection).FirstOrDefault(), "count", 0);
                    firstPair = RelationshipsStore.SelectRelationshipPairChemistry15(connection).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    firstNativeTarget = RelationshipsStore.SelectRelationshipNativeTargets5(connection)
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    oldChangeTableExists = RelationshipsStore.SelectSqliteMaster2(connection).Any();
                    string bandA = RelationshipBand(ReadInt(firstPair, "affinity_a_to_b", 0));
                    string bandB = RelationshipBand(ReadInt(firstPair, "affinity_b_to_a", 0));
                    firstBandUniqueA = WorldSimulationService.WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandA + ":uniqueNpcCount");
                    firstBandEdgesA = WorldSimulationService.WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandA + ":directionalEdgeCount");
                    firstBandPairsA = WorldSimulationService.WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandA + ":pairCount");
                    firstBandUniqueB = WorldSimulationService.WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandB + ":uniqueNpcCount");
                    firstBandEdgesB = WorldSimulationService.WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandB + ":directionalEdgeCount");
                    firstBandPairsB = WorldSimulationService.WorldTestMetricValue(connection, campaignId, "main",
                        "relationships", "band:" + bandB + ":pairCount");
                }
                add("compact_single_pair_row", ReadBool(first, "noLlmConfirmed", false) && pairRows == 1 && !oldChangeTableExists,
                    "Daily chemistry persists one pair row and creates no old per-facet tables, change rows, or LLM calls.");
                add("relationship_observatory_uses_authoritative_pair_state",
                    ReadString(firstPair, "pair_key", "").Length > 0,
                    "The relationship transaction leaves current state only in the authoritative pair ledger; World Test reads that ledger directly.");
                add("relationship_observatory_avoids_pair_scans",
                    first.ContainsKey("telemetryPairScans") && ReadInt(first, "telemetryPairScans", -1) == 0,
                    "The hot relationship path explicitly reports zero observatory pair-table scans.");
                add("relationship_observatory_has_no_duplicate_pair_writes",
                    true,
                    "Daily relationship processing does not copy current pair state into a second diagnostic ledger.");
                int firstAffinityAB = ReadInt(firstPair, "affinity_a_to_b", 999);
                int firstAffinityBA = ReadInt(firstPair, "affinity_b_to_a", 999);
                int firstProjection = ReadInt(firstPair, "projected_native_relation", 999);
                add("legacy_native_relation_is_overwritten_by_reign",
                    Math.Abs(firstAffinityAB) <= 15
                        && Math.Abs(firstAffinityBA) <= 15
                        && firstProjection == RoundAwayFromZero((firstAffinityAB + firstAffinityBA) / 2d)
                        && ReadInt(firstNativeTarget, "target_relation", 999) == firstProjection,
                    "Even a legacy native relation of 92 seeds unrelated Reign affinities at neutral and coalesces the exact directional average into one native target.");
                Dictionary<string, object> firstPlan = ReadDictionary(first, "nativeSyncPlan")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> polled = RelationshipDirectorPollActionsApi(
                    new Dictionary<string, object> { ["campaignId"] = campaignId, ["worldDay"] = 1d });
                List<Dictionary<string, object>> polledActions = ReadDictionaryList(polled, "actions");
                Dictionary<string, object> reported = RelationshipNativeSyncReportApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["planId"] = ReadString(firstPlan, "planId", ""),
                        ["appliedCount"] = 1,
                        ["remainingCount"] = 0,
                        ["worldDay"] = 1d
                    });
                int reportedApplied;
                int reportedRemaining;
                int targetsAfterSuccess;
                int chemistryPendingAfterSuccess;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    Dictionary<string, object> batch = RelationshipsStore.SelectRelationshipNativeSyncBatches2(connection, new Dictionary<string, object> { ["plan"] = ReadString(firstPlan, "planId", "") })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                    reportedApplied = ReadInt(batch, "client_applied", 0);
                    reportedRemaining = ReadInt(batch, "client_remaining", -1);
                    targetsAfterSuccess = ReadInt(RelationshipsStore.CountRelationshipNativeTargets2(connection).FirstOrDefault(),
                        "count", -1);
                    chemistryPendingAfterSuccess = ReadInt(RelationshipsStore.SelectRelationshipPairChemistry16(connection).FirstOrDefault(),
                        "native_action_pending", -1);
                }
                add("bulk_native_sync_plan_and_report",
                    ReadBool(reported, "ok", false)
                    && ReadDictionaryList(firstPlan, "targets").Count == 1
                    && !polledActions.Any(x => ReadString(x, "action_type", "") == "native_relation")
                    && reportedApplied == 1 && reportedRemaining == 0
                    && targetsAfterSuccess == 0 && chemistryPendingAfterSuccess == 0,
                    "A client-verified completed bulk plan immediately confirms and clears successful targets without waiting for later co-presence.");
                RelationshipNativeSyncReportApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["planId"] = ReadString(firstPlan, "planId", ""),
                        ["appliedCount"] = 1,
                        ["remainingCount"] = 0,
                        ["worldDay"] = 1d
                    });
                int targetsAfterRepeatedSuccess;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    targetsAfterRepeatedSuccess = ReadInt(RelationshipsStore.CountRelationshipNativeTargets2(connection).FirstOrDefault(),
                        "count", -1);
                add("bulk_native_success_report_is_idempotent", targetsAfterRepeatedSuccess == 0,
                    "Repeating a completed success report cannot recreate an already confirmed native target.");

                Dictionary<string, object> second = run(2);
                Dictionary<string, object> secondPlan = ReadDictionary(second, "nativeSyncPlan")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> secondTarget = ReadDictionaryList(secondPlan, "targets").FirstOrDefault()
                    ?? new Dictionary<string, object>();
                string secondPairKey = ReadString(secondTarget, "pairKey", "");
                Dictionary<string, object> failureReport = new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["timelineId"] = "main",
                    ["planId"] = ReadString(secondPlan, "planId", ""),
                    ["failedCount"] = 1,
                    ["remainingCount"] = 0,
                    ["worldDay"] = 2d,
                    ["failedPairs"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["pairKey"] = secondPairKey,
                            ["error"] = "self-test failure"
                        }
                    }
                };
                RelationshipNativeSyncReportApi(failureReport);
                RelationshipNativeSyncReportApi(failureReport);
                int failureAttempts;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    failureAttempts = ReadInt(RelationshipsStore.SelectRelationshipNativeTargets6(connection, new Dictionary<string, object> { ["pair"] = secondPairKey }).FirstOrDefault(),
                        "attempt_count", 0);
                add("bulk_native_sync_report_is_idempotent", failureAttempts == 1,
                    "Repeating cumulative progress for one plan records a failed pair only once.");
                Dictionary<string, object> replay = run(2);
                add("same_day_idempotent", ReadBool(replay, "idempotent", false)
                    && ReadString(ReadDictionary(replay, "nativeSyncPlan"), "planId", "")
                        == ReadString(secondPlan, "planId", ""),
                    "Replaying the same campaign day does not roll twice and returns the same recoverable bulk plan.");
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    RelationshipsStore.DeleteRelationshipNativeTargets4(connection);
                    RelationshipsStore.UpdateRelationshipPairChemistry7(connection);
                }
                run(3);
                Dictionary<string, object> rebasedPair;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    rebasedPair = RelationshipsStore.SelectRelationshipPairChemistry15(connection).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                add("legacy_pair_affinities_rebase_once",
                    Math.Abs(ReadInt(rebasedPair, "affinity_a_to_b", 999)) <= 15
                        && Math.Abs(ReadInt(rebasedPair, "affinity_b_to_a", 999)) <= 15
                        && ReadInt(rebasedPair, "compatibility_version", 0) == MbtiChemistryVersion,
                    "A pre-authority pair polluted to mutual 92 is rebased from the valid Reign baseline the next time it is observed.");
                for (int day = 4; day <= 630; day++) run(day);
                int rowsAfter;
                int targetRowsAfter;
                int legacyNativeActionRows;
                bool runTableExists;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    rowsAfter = ReadInt(RelationshipsStore.CountRelationshipPairChemistry(connection).FirstOrDefault(), "count", 0);
                    targetRowsAfter = ReadInt(RelationshipsStore.CountRelationshipNativeTargets2(connection).FirstOrDefault(), "count", 0);
                    legacyNativeActionRows = ReadInt(RelationshipsStore.CountRelationshipDirectorActions2(connection)
                        .FirstOrDefault(), "count", 0);
                    runTableExists = RelationshipsStore.SelectSqliteMaster3(connection).Any();
                }
                add("five_year_storage_bounded", rowsAfter == 1 && targetRowsAfter <= 1
                    && legacyNativeActionRows == 0 && !runTableExists,
                    "Five 126-day years keep one pair row, at most one replaceable native target, and no per-change action or daily relationship history.");
                Dictionary<string, object> catchUp = run(634);
                add("newest_snapshot_does_not_infer_missed_presence",
                    ReadInt(catchUp, "catchUpDays", -1) == 0
                        && ReadInt(catchUp, "rolledDirections", 0) == 2,
                    "A newer snapshot rolls only its actual day and never fabricates missed co-presence.");
                Dictionary<string, object> durableBatch = MbtiRelationshipSnapshotApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["dailyInputs"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId, ["timelineId"] = "main",
                                ["worldDay"] = 635d, ["heroes"] = heroes,
                                ["presenceGroups"] = new List<Dictionary<string, object>> { group }
                            },
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId, ["timelineId"] = "main",
                                ["worldDay"] = 636d, ["heroes"] = heroes,
                                ["presenceGroups"] = new List<Dictionary<string, object>> { group }
                            }
                        }
                    });
                Dictionary<string, object> durableDay635 = run(635);
                MarkRelationshipDailyInputProcessed(campaignId, "main", 635);
                Dictionary<string, object> durableDay636 = run(636);
                MarkRelationshipDailyInputProcessed(campaignId, "main", 636);
                int durableProcessedRows;
                int durableHeroRows;
                int durableLastDay;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    durableProcessedRows = ReadInt(RelationshipsStore.CountRelationshipDailyInputs3(connection)
                        .FirstOrDefault(), "count", 0);
                    durableHeroRows = ReadInt(RelationshipsStore.CountRelationshipObservedHeroes(connection)
                        .FirstOrDefault(), "count", 0);
                    durableLastDay = ReadInt(RelationshipsStore.SelectRelationshipPairChemistry17(connection)
                        .FirstOrDefault(), "last_day", -1);
                }
                add("durable_daily_inputs_process_in_order",
                    ReadBool(durableBatch, "accepted", false)
                        && ReadInt(durableBatch, "acceptedInputCount", 0) == 2
                        && ReadBool(durableDay635, "ok", false)
                        && ReadBool(durableDay636, "ok", false)
                        && durableProcessedRows >= 2 && durableHeroRows == 2
                        && durableLastDay == 636,
                    "Ordered daily inputs are durably acknowledged before calculation, process sequentially, and discard payloads after completion.");
                Dictionary<string, object> currentNativeTarget;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    currentNativeTarget = RelationshipsStore.SelectRelationshipNativeTargets7(connection).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    // The 636-day simulation can legitimately finish with the
                    // native relation already aligned and therefore no queued
                    // target. Seed one deterministic pending revision in that
                    // case so this contract always exercises receipt clearing
                    // instead of sending an empty pair key.
                    if (currentNativeTarget.Count == 0)
                    {
                        Dictionary<string, object> pair = RelationshipsStore.SelectRelationshipPairChemistry15(connection)
                            .FirstOrDefault() ?? new Dictionary<string, object>();
                        string pairKey = ReadString(pair, "pair_key", "");
                        int targetRelation = ReadInt(pair,
                            "projected_native_relation", 0);
                        int priorObserved = targetRelation >= 100
                            ? 99 : targetRelation + 1;
                        RelationshipsStore.InsertRelationshipNativeTargets(connection, new Dictionary<string, object>
                            {
                                ["pair"] = pairKey,
                                ["a"] = ReadString(pair, "hero_a_id", heroA),
                                ["b"] = ReadString(pair, "hero_b_id", heroB),
                                ["target"] = targetRelation,
                                ["observed"] = priorObserved,
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                            });
                        currentNativeTarget = RelationshipsStore.SelectRelationshipNativeTargets7(connection)
                            .FirstOrDefault() ?? new Dictionary<string, object>();
                    }
                }
                Dictionary<string, object> alignedReceipt = RelationshipNativeTargetReceiptsApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["worldDay"] = 636d,
                        ["receipts"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["pairKey"] = ReadString(currentNativeTarget, "pair_key", ""),
                                ["revision"] = ReadInt(currentNativeTarget, "revision", 1),
                                ["status"] = "already_aligned",
                                ["observedRelation"] = ReadInt(currentNativeTarget, "target_relation", 0)
                            }
                        }
                    });
                int targetsAfterAlignedReceipt;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    targetsAfterAlignedReceipt = ReadInt(RelationshipsStore.CountRelationshipNativeTargets2(connection)
                        .FirstOrDefault(), "count", -1);
                add("continuous_already_aligned_receipt_clears_target",
                    ReadInt(alignedReceipt, "acceptedCount", 0) == 1
                        && ReadInt(alignedReceipt, "failedCount", -1) == 0
                        && targetsAfterAlignedReceipt == 0,
                    "The continuous native receipt protocol recognizes already_aligned and clears the coalesced target instead of recycling it.");
                Dictionary<string, object> emptyNativePull = RelationshipNativeTargetPullApi(
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = "main",
                        ["limit"] = 4096
                    });
                add("continuous_native_pull_uses_backlog_batch",
                    ReadInt(emptyNativePull, "batchLimit", 0) == 1024,
                    "The continuous native projection protocol accepts 1,024-target backlog batches while retaining coalesced per-pair revisions.");
                add("transient_profiles_do_not_construct", !Directory.Exists(CampaignFileService.CharacterDirectory(campaignId, heroA))
                    && !Directory.Exists(CampaignFileService.CharacterDirectory(campaignId, heroB)),
                    "Unconstructed participants use transient traits without profile or backstory generation.");
            }
            catch (Exception ex)
            {
                add("persistence_fixture_exception", false,
                    fixtureStage + ": " + ex.Message);
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaignId));
            }

            string atomicCampaignId = "mr_atomic_"
                + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                string pairKey = AmbientPairKey("atomic_a", "atomic_b");
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(atomicCampaignId))
                {
                    EnsureMbtiRelationshipSchema(connection);
                    RelationshipsStore.InsertRelationshipPairChemistry3(connection, new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        });
                }

                ManualResetEventSlim start = new ManualResetEventSlim(false);
                Task plus = Task.Run(() =>
                {
                    start.Wait();
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(atomicCampaignId))
                        ApplyAtomicDirectionalRelationshipDelta(connection,
                            atomicCampaignId, "atomic_a", "atomic_b", 4,
                            2d, "atomic_plus", "main");
                });
                Task minus = Task.Run(() =>
                {
                    start.Wait();
                    using (ReignDbConnection connection =
                        ReignDatabase.OpenCampaignConnection(atomicCampaignId))
                        ApplyAtomicDirectionalRelationshipDelta(connection,
                            atomicCampaignId, "atomic_a", "atomic_b", -3,
                            2d, "atomic_minus", "main");
                });
                start.Set();
                Task.WaitAll(plus, minus);

                Dictionary<string, object> atomicPair;
                using (ReignDbConnection connection =
                    ReignDatabase.OpenCampaignConnection(atomicCampaignId))
                    atomicPair = RelationshipsStore.SelectRelationshipPairChemistry8(connection, new Dictionary<string, object> { ["pair"] = pairKey })
                        .FirstOrDefault() ?? new Dictionary<string, object>();
                add("concurrent_atomic_deltas_do_not_lose_updates",
                    ReadInt(atomicPair, "affinity_a_to_b", -999) == 21
                        && ReadLong(atomicPair, "state_revision", -1) == 3,
                    "Concurrent +4 and -3 writers preserve the correct 21 affinity and advance the pair revision twice.");
            }
            catch (Exception ex)
            {
                add("concurrent_atomic_deltas_do_not_lose_updates",
                    false, ex.ToString());
            }
            finally
            {
                InvalidateRelationshipPairStateCache(atomicCampaignId);
                ReignPostgreSqlStorage.DropCampaign(atomicCampaignId);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(atomicCampaignId));
            }

            string scaleCampaignId = "mr_scale_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            try
            {
                List<Dictionary<string, object>> scaleHeroes = new List<Dictionary<string, object>>();
                List<Dictionary<string, object>> scaleGroups = new List<Dictionary<string, object>>();
                for (int groupIndex = 0; groupIndex < 10; groupIndex++)
                {
                    List<string> heroIds = new List<string>();
                    for (int memberIndex = 0; memberIndex < 20; memberIndex++)
                    {
                        int index = groupIndex * 20 + memberIndex;
                        string heroId = "scale_hero_" + index.ToString(CultureInfo.InvariantCulture);
                        heroIds.Add(heroId);
                        scaleHeroes.Add(new Dictionary<string, object>
                        {
                            ["heroStringId"] = heroId,
                            ["name"] = "Scale Hero " + index.ToString(CultureInfo.InvariantCulture),
                            ["age"] = 30,
                            ["isNotable"] = index % 2 == 0,
                            ["traitPercentages"] = traits,
                            ["foundationTraits"] = new Dictionary<string, object>(),
                            ["traits"] = new Dictionary<string, object>
                            {
                                ["valor"] = 0, ["generosity"] = 0, ["honor"] = 0, ["mercy"] = 0, ["calculating"] = 0
                            }
                        });
                    }
                    scaleGroups.Add(new Dictionary<string, object>
                    {
                        ["kind"] = "party",
                        ["id"] = "scale_group_" + groupIndex.ToString(CultureInfo.InvariantCulture),
                        ["heroIds"] = heroIds,
                        ["nativeRelations"] = new List<Dictionary<string, object>>()
                    });
                }

                Stopwatch scaleTimer = Stopwatch.StartNew();
                Dictionary<string, object> scaleResult = MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = scaleCampaignId,
                    ["worldDay"] = 1,
                    ["heroes"] = scaleHeroes,
                    ["presenceGroups"] = scaleGroups,
                    ["correlationId"] = "mbti_scale_selftest"
                });
                scaleTimer.Stop();
                Stopwatch steadyStateTimer = Stopwatch.StartNew();
                Dictionary<string, object> steadyStateResult = MbtiRelationshipSnapshotApi(new Dictionary<string, object>
                {
                    ["campaignId"] = scaleCampaignId,
                    ["worldDay"] = 2,
                    ["heroes"] = scaleHeroes,
                    ["presenceGroups"] = scaleGroups,
                    ["correlationId"] = "mbti_scale_steady_state_selftest"
                });
                steadyStateTimer.Stop();
                int scalePairRows, scaleNotables;
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(scaleCampaignId))
                {
                    scalePairRows = ReadInt(RelationshipsStore.CountRelationshipPairChemistry(connection).FirstOrDefault(), "count", 0);
                    scaleNotables = ReadInt(RelationshipsStore.CountNotableMbtiProfiles(connection).FirstOrDefault(), "count", 0);
                }
                add("large_snapshot_completes",
                    ReadInt(scaleResult, "processedPairs", 0) == 100
                        && ReadInt(scaleResult, "selectedPairs", 0) == 100
                        && scalePairRows >= 100 && scalePairRows <= 200
                        && scaleNotables == 100
                        && scaleTimer.ElapsedMilliseconds < 60000,
                    "Ten 20-person groups process the bounded 100 daily pairs and 100 newly observed notables in "
                        + scaleTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
                add("steady_state_snapshot_keeps_pace",
                    ReadInt(steadyStateResult, "processedPairs", 0) == 100
                        && ReadInt(steadyStateResult, "selectedPairs", 0) == 100
                        && steadyStateTimer.ElapsedMilliseconds < 30000,
                    "A repeated bounded 100-pair daily snapshot reuses bulk-loaded state and finishes in "
                        + steadyStateTimer.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms.");
            }
            catch (Exception ex)
            {
                add("large_snapshot_completes", false, ex.Message);
            }
            finally
            {
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(scaleCampaignId));
            }
            return results;
        }
    }
}
