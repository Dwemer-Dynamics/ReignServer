using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static List<Dictionary<string, object>>
            RunRelationshipFlingSelfTests()
        {
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) =>
                results.Add(new Dictionary<string, object>
                {
                    ["ok"] = true, ["passed"] = passed,
                    ["suite"] = "relationship_flings", ["caseId"] = id,
                    ["name"] = id, ["summary"] = summary,
                    ["durationMs"] = 0
                });
            add("fling_judgment_chance_linear_contract",
                FlingChancePercent(0) == 50
                    && FlingChancePercent(10) == 40
                    && FlingChancePercent(20) == 30
                    && FlingChancePercent(39) == 11
                    && FlingChancePercent(40) == 10
                    && FlingChancePercent(41) == 0,
                "Judgment zero through forty scales linearly from fifty to ten percent; higher judgment is ineligible.");
            add("fling_cooldown_boundary",
                !FlingCooldownComplete(100, 101)
                    && !FlingCooldownComplete(100, 106)
                    && FlingCooldownComplete(100, 107)
                    && FlingCooldownComplete(-1, 100),
                "A successful fling blocks the following six daily polls and permits re-entry on day seven.");
            add("fling_spark_rate_contract",
                FlingSparkChancePercent == 25,
                "A passed low-judgment pair has a twenty-five percent durable spark chance before a fling occurs.");
            add("fling_conception_curve_contract",
                Math.Abs(NpcConceptionChance(18d, 0) - 0.144d)
                    < 0.0000001d
                    && NpcConceptionChance(30d, 1)
                        < NpcConceptionChance(30d, 0)
                    && NpcConceptionChance(17.9d, 0) == 0d
                    && NpcConceptionChance(45.1d, 0) == 0d,
                "Fling conception reuses the age and existing-children-adjusted NPC conception curve.");

            Dictionary<string, object> ordinaryState =
                new Dictionary<string, object>();
            bool ordinaryFirst = !UsesLoverRelationshipMagnitude(
                ordinaryState);
            ordinaryState["lover_active"] = 1;
            add("lover_magnitude_switches_after_tag_transition",
                ordinaryFirst
                    && UsesLoverRelationshipMagnitude(ordinaryState),
                "A tag-creating pair-day uses the ordinary d15 and the same mutable lifecycle row selects d20 on the next day.");

            Func<string, Dictionary<string, object>> adult = id =>
                new Dictionary<string, object>
                {
                    ["heroStringId"] = id, ["age"] = 30d,
                    ["isAdult"] = true, ["isAlive"] = true,
                    ["isActive"] = true, ["isFemale"] = false,
                    ["spouseId"] = "",
                    ["traits"] = new Dictionary<string, object>
                    {
                        ["courtVirtues"] = new Dictionary<string, object>
                        {
                            ["judgment"] = 0
                        }
                    }
                };
            Dictionary<string, object> active = adult("active");
            Dictionary<string, object> inactive = adult("inactive");
            inactive["isActive"] = false;
            Dictionary<string, object> prisoner = adult("prisoner");
            prisoner["isPrisoner"] = true;
            Dictionary<string, object> child = adult("child");
            child["age"] = 12d;
            child["isAdult"] = false;
            Dictionary<string, object> player = adult("player");
            player["isPlayer"] = true;
            add("fling_hero_exclusions",
                FlingHeroEligible(active)
                    && !FlingHeroEligible(inactive)
                    && !FlingHeroEligible(prisoner)
                    && !FlingHeroEligible(child)
                    && !FlingHeroEligible(player),
                "Children, inactive/dead characters, prisoners, and the player never enter the daily fling pool.");

            FlingCandidate spouseOne = new FlingCandidate
            {
                HeroId = "spouse_one", Hero = adult("spouse_one")
            };
            FlingCandidate spouseTwo = new FlingCandidate
            {
                HeroId = "spouse_two", Hero = adult("spouse_two")
            };
            spouseOne.Hero["spouseId"] = spouseTwo.HeroId;
            spouseTwo.Hero["spouseId"] = spouseOne.HeroId;
            FlingCandidate ancestorOne = new FlingCandidate
            {
                HeroId = "ancestor_one", Hero = adult("ancestor_one")
            };
            FlingCandidate ancestorTwo = new FlingCandidate
            {
                HeroId = "ancestor_two", Hero = adult("ancestor_two")
            };
            ancestorOne.Hero["marriageAncestorIds"] =
                new List<object> { "shared_ancestor" };
            ancestorTwo.Hero["marriageAncestorIds"] =
                new List<object> { "shared_ancestor" };
            HashSet<string> loverPair = new HashSet<string>(
                new[] { AmbientPairKey(ancestorOne.HeroId,
                    ancestorTwo.HeroId) }, StringComparer.OrdinalIgnoreCase);
            add("fling_pair_exclusions",
                !FlingPairEligible(spouseOne, spouseTwo,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    && !FlingPairEligible(ancestorOne, ancestorTwo,
                        new HashSet<string>(StringComparer.OrdinalIgnoreCase))
                    && !FlingPairEligible(ancestorOne, ancestorTwo,
                        loverPair),
                "Current spouses together, close relatives, and an existing lover pair together are not treated as flings.");

            List<FlingCandidate> deterministicCandidates =
                Enumerable.Range(0, 6).Select(index =>
                    new FlingCandidate
                    {
                        HeroId = "det_" + index.ToString(
                            CultureInfo.InvariantCulture),
                        Hero = adult("det_" + index.ToString(
                            CultureInfo.InvariantCulture))
                    }).ToList();
            List<Tuple<FlingCandidate, FlingCandidate>> forward =
                PairFlingCandidates("deterministic_pairing",
                    deterministicCandidates,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            List<Tuple<FlingCandidate, FlingCandidate>> reversed =
                PairFlingCandidates("deterministic_pairing",
                    deterministicCandidates.AsEnumerable().Reverse().ToList(),
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            Func<List<Tuple<FlingCandidate, FlingCandidate>>, List<string>>
                pairKeys = pairs => pairs.Select(pair => AmbientPairKey(
                        pair.Item1.HeroId, pair.Item2.HeroId))
                    .OrderBy(value => value,
                        StringComparer.OrdinalIgnoreCase).ToList();
            add("fling_pairing_is_deterministic_and_disjoint",
                pairKeys(forward).SequenceEqual(pairKeys(reversed),
                    StringComparer.OrdinalIgnoreCase)
                    && forward.SelectMany(pair => new[]
                        {
                            pair.Item1.HeroId, pair.Item2.HeroId
                        }).Distinct(StringComparer.OrdinalIgnoreCase).Count()
                        == forward.Count * 2,
                "Stable randomized pairing is input-order independent and uses each character at most once.");

            string campaignId = "fl_" + Guid.NewGuid().ToString("N")
                .Substring(0, 12);
            try
            {
                using (ReignDbConnection connection =
                    Program.OpenCampaignConnection(campaignId))
                {
                    EnsureRelationshipDirectorSchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    ReputationService.EnsureSocialReputationSchema(connection);
                    WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
                    const int day = 50;
                    const string timeline = "fling_test_timeline";
                    List<Dictionary<string, object>> passingHeroes =
                        new List<Dictionary<string, object>>();
                    for (int index = 0; passingHeroes.Count < 3
                        && index < 100000; index++)
                    {
                        string heroId = "fling_fixture_" + index.ToString(
                            CultureInfo.InvariantCulture);
                        int roll = StableDie(string.Join("|", campaignId,
                            timeline, day.ToString(CultureInfo.InvariantCulture),
                            heroId, "settlement|fixture_town",
                            "fling_gate_v1"), 100);
                        if (roll > 50) continue;
                        passingHeroes.Add(adult(heroId));
                        if (passingHeroes.Count < 3) continue;
                        List<FlingCandidate> fixtureCandidates =
                            passingHeroes.Select(hero => new FlingCandidate
                            {
                                HeroId = ReadString(hero, "heroStringId", ""),
                                Hero = hero, LocationKind = "settlement",
                                LocationId = "fixture_town"
                            }).ToList();
                        string fixturePairingSeed = string.Join("|",
                            campaignId, timeline,
                            day.ToString(CultureInfo.InvariantCulture),
                            "settlement|fixture_town",
                            "fling_pairing_v1");
                        Tuple<FlingCandidate, FlingCandidate> fixturePair =
                            PairFlingCandidates(fixturePairingSeed,
                                fixtureCandidates, new HashSet<string>(
                                    StringComparer.OrdinalIgnoreCase))
                                .FirstOrDefault();
                        if (fixturePair != null
                            && FlingSparkRoll(campaignId, timeline, day,
                                fixturePair.Item1, fixturePair.Item2)
                                <= FlingSparkChancePercent)
                            break;
                        passingHeroes.Clear();
                    }
                    Dictionary<string, Dictionary<string, object>> heroes =
                        passingHeroes.ToDictionary(hero => ReadString(hero,
                            "heroStringId", ""), hero => hero,
                            StringComparer.OrdinalIgnoreCase);
                    Dictionary<int, List<Dictionary<string, object>>> groups =
                        new Dictionary<int, List<Dictionary<string, object>>>
                        {
                            [day] = new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["kind"] = "settlement",
                                    ["id"] = "fixture_town",
                                    ["heroIds"] = heroes.Keys.Cast<object>()
                                        .ToList()
                                }
                            }
                        };
                    Dictionary<int, List<Dictionary<string, object>>> rows =
                        new Dictionary<int, List<Dictionary<string, object>>>
                        {
                            [day] = passingHeroes
                        };
                    Dictionary<string, object> firstRun =
                        ProcessDailyNpcFlings(connection, campaignId,
                            timeline, groups, rows, heroes);
                    Dictionary<string, object> cachedProfileChild = adult("ineligible_child");
                    cachedProfileChild["age"] = 12d;
                    cachedProfileChild["isAdult"] = false;
                    heroes["ineligible_child"] = cachedProfileChild;
                    groups[day][0]["heroIds"] = heroes.Keys.Cast<object>().ToList();
                    foreach (string heroId in heroes.Keys)
                        Program.ExecuteSql(connection, @"INSERT INTO relationship_personalities(
hero_id,mbti_type,title,description,source,assignment_day,template_version,traits_json,created_ts,updated_ts)
VALUES($hero,'INTJ','Fixture','Fixture','self_test',50,1,$traits,1,1);",
                            new Dictionary<string, object> { ["hero"] = heroId,
                                ["traits"] = Json.Serialize(new Dictionary<string, object>
                                { ["courtVirtues"] = new Dictionary<string, object> { ["judgment"] = 99 },
                                    ["padding"] = new string('x', 16000) }) });
                    Dictionary<string, object> secondRun =
                        ProcessDailyNpcFlings(connection, campaignId,
                            timeline, groups, rows, heroes);
                    add("fling_warm_profiles_skip_cached_and_ineligible_documents",
                        ReadInt(secondRun, "profileDocumentsLoaded", -1) == 0
                            && ReadInt(secondRun, "profileCharactersParsed", -1) == 0,
                        "Persisted judgments retain authority; neither their large personality documents nor an ineligible child's document is loaded.");
                    add("fling_roll_batch_precedes_spark_dependencies",
                        ReadInt(firstRun, "rollWriteCommands", -1) == (ReignPostgreSqlDialect.IsPostgreSql(connection) ? 1 : 3),
                        "All three roll receipts are written together before spark/encounter updates consume them.");
                    int rollCount = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline, ["day"] = day
                        }).FirstOrDefault(), "count", 0);
                    int encounterCount = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline, ["day"] = day
                        }).FirstOrDefault(), "count", 0);
                    Dictionary<string, object> sparkCount =
                        Program.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks
WHERE timeline_id=$timeline AND world_day=$day;",
                            new Dictionary<string, object>
                            {
                                ["timeline"] = timeline, ["day"] = day
                            }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    int lifecycleCount = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_lifecycle;")
                        .FirstOrDefault(), "count", 0);
                    int chemistryCount = ReadInt(Program.QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_chemistry;")
                        .FirstOrDefault(), "count", 0);
                    add("fling_ledger_replay_is_idempotent",
                        passingHeroes.Count == 3 && rollCount == 3
                            && ReadInt(sparkCount, "attempts", 0) == 1
                            && ReadInt(sparkCount, "passes", 0) == 1
                            && encounterCount == 1
                            && ReadInt(firstRun,
                                "flingSparkAttempts", 0) == 1
                            && ReadInt(firstRun,
                                "flingSparkPasses", 0) == 1
                            && ReadInt(firstRun, "flingPairs", 0) == 1
                            && ReadInt(firstRun, "flingUnmatched", 0) == 1
                            && ReadInt(secondRun,
                                "flingDuplicateSuppressions", 0) == 3,
                        "Direct replay preserves one roll per character and one encounter while reporting suppressed duplicate work. "
                            + string.Format(CultureInfo.InvariantCulture,
                                "Observed heroes={0}, rolls={1}, sparkAttempts={2}, sparkPasses={3}, encounters={4}, firstPairs={5}, firstUnmatched={6}, replaySuppressions={7}.",
                                passingHeroes.Count, rollCount,
                                ReadInt(sparkCount, "attempts", 0),
                                ReadInt(sparkCount, "passes", 0),
                                encounterCount,
                                ReadInt(firstRun, "flingPairs", 0),
                                ReadInt(firstRun, "flingUnmatched", 0),
                                ReadInt(secondRun,
                                    "flingDuplicateSuppressions", 0)));
                    add("fling_does_not_create_romance_or_affinity",
                        lifecycleCount == 0 && chemistryCount == 0,
                        "A fling receipt never creates lover/affair lifecycle state or changes directional affinity.");
                    add("same_sex_fling_has_no_conception",
                        ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE timeline_id=$timeline AND same_sex=1
AND (conception_attempt_id<>'' OR conception_id<>'' OR conceived=1);",
                            new Dictionary<string, object>
                            {
                                ["timeline"] = timeline
                            }).FirstOrDefault(), "count", 0) == 0,
                        "Same-sex fling encounters never create conception attempts or actions.");

                    // Quiet groups must reach the ledger even when no spark path
                    // forces a flush. A same-day replay may not reroll them.
                    const int quietDay = 55;
                    Dictionary<string, object> quietHero = adult("quiet_single_hero");
                    Dictionary<string, Dictionary<string, object>> quietHeroes = new Dictionary<string, Dictionary<string, object>>
                    { ["quiet_single_hero"] = quietHero };
                    Dictionary<int, List<Dictionary<string, object>>> quietGroups = new Dictionary<int, List<Dictionary<string, object>>>
                    { [quietDay] = new List<Dictionary<string, object>> { new Dictionary<string, object>
                        { ["kind"] = "settlement", ["id"] = "quiet_town", ["heroIds"] = new[] { "quiet_single_hero" } } } };
                    Dictionary<string, object> quietResult = ProcessDailyNpcFlings(connection, campaignId, timeline,
                        quietGroups, null, quietHeroes);
                    Dictionary<string, object> quietReplay = ProcessDailyNpcFlings(connection, campaignId, timeline,
                        quietGroups, null, quietHeroes);
                    Dictionary<string, object> quietRoll = Program.QuerySql(connection,
                        "SELECT * FROM relationship_fling_rolls WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object> { ["timeline"] = timeline, ["day"] = quietDay }).SingleOrDefault();
                    int expectedQuietRoll = StableDie(string.Join("|", campaignId, timeline,
                        quietDay.ToString(CultureInfo.InvariantCulture), "quiet_single_hero", "settlement|quiet_town", "fling_gate_v1"), 100);
                    add("fling_quiet_group_flush_and_replay",
                        quietRoll != null && ReadInt(quietRoll, "roll", -1) == expectedQuietRoll
                            && ReadInt(quietResult, "rollWriteCommands", -1) == 1
                            && ReadInt(quietReplay, "flingDuplicateSuppressions", -1) == 1,
                        "A solitary eligible NPC retains its original deterministic roll and one receipt without requiring a spark or encounter.");

                    const int failedSparkDay = 60;
                    List<Dictionary<string, object>> failedSparkHeroes = null;
                    for (int index = 0; index < 100000; index++)
                    {
                        Dictionary<string, object> firstHero = adult(
                            "failed_spark_a_" + index.ToString(
                                CultureInfo.InvariantCulture));
                        Dictionary<string, object> secondHero = adult(
                            "failed_spark_b_" + index.ToString(
                                CultureInfo.InvariantCulture));
                        string firstId = ReadString(firstHero,
                            "heroStringId", "");
                        string secondId = ReadString(secondHero,
                            "heroStringId", "");
                        Func<string, int, int> gateRoll = (heroId,
                            fixtureDay) => StableDie(string.Join("|",
                                campaignId, timeline,
                                fixtureDay.ToString(
                                    CultureInfo.InvariantCulture), heroId,
                                "settlement|spark_failure_town",
                                "fling_gate_v1"), 100);
                        if (gateRoll(firstId, failedSparkDay) > 50
                            || gateRoll(secondId, failedSparkDay) > 50
                            || gateRoll(firstId, failedSparkDay + 1) > 50
                            || gateRoll(secondId, failedSparkDay + 1) > 50)
                            continue;
                        FlingCandidate firstCandidate = new FlingCandidate
                        {
                            HeroId = firstId, Hero = firstHero,
                            LocationKind = "settlement",
                            LocationId = "spark_failure_town"
                        };
                        FlingCandidate secondCandidate = new FlingCandidate
                        {
                            HeroId = secondId, Hero = secondHero,
                            LocationKind = "settlement",
                            LocationId = "spark_failure_town"
                        };
                        if (FlingSparkRoll(campaignId, timeline,
                            failedSparkDay, firstCandidate, secondCandidate)
                            <= FlingSparkChancePercent) continue;
                        failedSparkHeroes = new List<Dictionary<string, object>>
                        {
                            firstHero, secondHero
                        };
                        break;
                    }
                    Dictionary<string, Dictionary<string, object>>
                        failedSparkHeroMap = (failedSparkHeroes
                            ?? new List<Dictionary<string, object>>())
                            .ToDictionary(hero => ReadString(hero,
                                "heroStringId", ""), hero => hero,
                                StringComparer.OrdinalIgnoreCase);
                    Dictionary<int, List<Dictionary<string, object>>>
                        failedSparkGroups = new Dictionary<int,
                            List<Dictionary<string, object>>>();
                    Dictionary<int, List<Dictionary<string, object>>>
                        failedSparkRows = new Dictionary<int,
                            List<Dictionary<string, object>>>();
                    foreach (int fixtureDay in new[]
                        {
                            failedSparkDay, failedSparkDay + 1
                        })
                    {
                        failedSparkGroups[fixtureDay] = new List<
                            Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["kind"] = "settlement",
                                ["id"] = "spark_failure_town",
                                ["heroIds"] = failedSparkHeroMap.Keys
                                    .Cast<object>().ToList()
                            }
                        };
                        failedSparkRows[fixtureDay] = failedSparkHeroes
                            ?? new List<Dictionary<string, object>>();
                    }
                    ProcessDailyNpcFlings(connection, campaignId, timeline,
                        failedSparkGroups, failedSparkRows,
                        failedSparkHeroMap);
                    Dictionary<string, object> failedSparkCounts =
                        Program.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE timeline_id=$timeline
AND world_day=$day;", new Dictionary<string, object>
                        {
                            ["timeline"] = timeline,
                            ["day"] = failedSparkDay
                        }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    int nextDayRolls = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline,
                            ["day"] = failedSparkDay + 1
                        }).FirstOrDefault(), "count", 0);
                    int failedSparkEncounters = ReadInt(Program.QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline,
                            ["day"] = failedSparkDay
                        }).FirstOrDefault(), "count", 0);
                    add("failed_spark_does_not_consume_cooldown",
                        failedSparkHeroes != null
                            && ReadInt(failedSparkCounts,
                                "attempts", 0) == 1
                            && ReadInt(failedSparkCounts, "passes", 0) == 0
                            && failedSparkEncounters == 0
                            && nextDayRolls == 2,
                        "A failed spark creates no encounter and both participants return to the eligible daily pool on the following day.");
                }
            }
            catch (Exception ex)
            {
                add("fling_fixture_exception", false, ex.ToString());
            }
            finally
            {
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); }
                catch { }
                PersistenceService.TryDeleteDirectory(Program.CampaignDirectory(campaignId));
            }
            return results;
        }
    }
}
