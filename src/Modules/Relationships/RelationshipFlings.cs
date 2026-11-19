using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        internal const int FlingMaximumJudgment = 40;
        private const int FlingMaximumChancePercent = 50;
        internal const int FlingSparkChancePercent = 25;
        internal const int FlingCooldownDays = 7;
        internal const int FlingDiscoveryChancePercent = 20;

        internal static void EnsureRelationshipFlingSchemaCore(
            ReignDbConnection connection)
        {
            if (IsRelationshipMaintenanceTableReady(connection, "relationship_fling_profiles")
                && IsRelationshipMaintenanceTableReady(connection, "relationship_fling_rolls")
                && IsRelationshipMaintenanceTableReady(connection, "relationship_fling_sparks")
                && IsRelationshipMaintenanceTableReady(connection, "relationship_fling_encounters")) return;
            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_profiles (
hero_id TEXT PRIMARY KEY,judgment INTEGER NOT NULL,source TEXT NOT NULL DEFAULT '',
updated_ts INTEGER NOT NULL);" );
            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day INTEGER NOT NULL,
hero_id TEXT NOT NULL,location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
judgment INTEGER NOT NULL,chance_percent INTEGER NOT NULL,roll INTEGER NOT NULL,
passed INTEGER NOT NULL,status TEXT NOT NULL,encounter_id TEXT NOT NULL DEFAULT '',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,world_day,hero_id));" );
            Program.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_rolls_hero
ON relationship_fling_rolls(timeline_id,hero_id,world_day DESC);" );
            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_sparks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day INTEGER NOT NULL,
pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
chance_percent INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,
encounter_id TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,world_day,pair_key));" );
            Program.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_sparks_day
ON relationship_fling_sparks(timeline_id,world_day DESC);" );
            Program.ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_encounters (
encounter_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,
world_day INTEGER NOT NULL,pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
same_sex INTEGER NOT NULL DEFAULT 0,short_affair INTEGER NOT NULL DEFAULT 0,
fixed_discovery_chance REAL NOT NULL DEFAULT 0,fixed_discovery_roll REAL NOT NULL DEFAULT -1,
fixed_discovered INTEGER NOT NULL DEFAULT 0,pregnancy_discovery_chance REAL NOT NULL DEFAULT 0,
pregnancy_discovery_roll REAL NOT NULL DEFAULT -1,pregnancy_discovered INTEGER NOT NULL DEFAULT 0,
discovery_applied INTEGER NOT NULL DEFAULT 0,rumor_id TEXT NOT NULL DEFAULT '',
conception_attempt_id TEXT NOT NULL DEFAULT '',conception_id TEXT NOT NULL DEFAULT '',
conception_chance REAL NOT NULL DEFAULT 0,conception_roll REAL NOT NULL DEFAULT -1,
conceived INTEGER NOT NULL DEFAULT 0,conception_action_id TEXT NOT NULL DEFAULT '',
status TEXT NOT NULL DEFAULT 'processing',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);" );
            Program.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_day
ON relationship_fling_encounters(timeline_id,world_day DESC);" );
            Program.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_a
ON relationship_fling_encounters(timeline_id,hero_a_id,world_day DESC);" );
            Program.ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_b
ON relationship_fling_encounters(timeline_id,hero_b_id,world_day DESC);" );
        }

        private static int FlingChancePercent(int judgment)
        {
            if (judgment < 0 || judgment > FlingMaximumJudgment) return 0;
            return Clamp(FlingMaximumChancePercent - judgment,
                0, FlingMaximumChancePercent);
        }

        private static bool FlingCooldownComplete(int lastFlingDay, int day)
        {
            return lastFlingDay < 0 || day - lastFlingDay >= FlingCooldownDays;
        }

        private static int FlingSparkRoll(string campaignId,
            string timelineId, int day, FlingCandidate first,
            FlingCandidate second)
        {
            string locationKey = (first?.LocationKind ?? "") + "|"
                + (first?.LocationId ?? "");
            string pairKey = AmbientPairKey(first?.HeroId ?? "",
                second?.HeroId ?? "");
            return StableDie(string.Join("|", campaignId, timelineId,
                day.ToString(CultureInfo.InvariantCulture), locationKey,
                pairKey, "fling_spark_v1"), 100);
        }

        private static double NpcConceptionChance(double motherAge,
            int existingChildren)
        {
            if (motherAge < 18d || motherAge > 45d) return 0d;
            int divisor = Math.Max(1, existingChildren + 1);
            return Math.Max(0d, (1.2d - (motherAge - 18d) * 0.04d)
                / (divisor * divisor) * 0.12d);
        }

        private static bool TryExtractFlingJudgment(
            Dictionary<string, object> document, out int judgment)
        {
            judgment = 0;
            if (document == null || document.Count == 0) return false;
            Dictionary<string, object> nested = ReadDictionary(document,
                "traits");
            if (nested != null && !ReferenceEquals(nested, document)
                && TryExtractFlingJudgment(nested, out judgment))
                return true;
            Dictionary<string, object> virtues = ReadDictionary(document,
                "courtVirtues");
            if (virtues != null && virtues.ContainsKey("judgment"))
            {
                judgment = Clamp(ReadInt(virtues, "judgment", 50), 0, 100);
                return true;
            }
            Dictionary<string, object> percentages = ReadDictionary(document,
                "traitPercentages");
            if (percentages == null || percentages.Count == 0) return false;
            virtues = CharactersService.CalculateCourtVirtues(percentages);
            if (virtues == null || !virtues.ContainsKey("judgment")) return false;
            judgment = Clamp(ReadInt(virtues, "judgment", 50), 0, 100);
            return true;
        }

        private static bool TryResolveFlingJudgment(
            ReignDbConnection connection,
            string campaignId,
            string heroId,
            Dictionary<string, object> hero,
            Dictionary<string, Dictionary<string, object>> persisted,
            Dictionary<string, Dictionary<string, object>> notableDocuments,
            Dictionary<string, Dictionary<string, object>> runtimeDocuments,
            Dictionary<string, Dictionary<string, object>> shippedDocuments,
            out int judgment,
            out string source)
        {
            judgment = 0;
            source = "";
            if (persisted.TryGetValue(heroId,
                out Dictionary<string, object> cached))
            {
                judgment = Clamp(ReadInt(cached, "judgment", 50), 0, 100);
                source = ReadString(cached, "source", "cached");
                return true;
            }

            Dictionary<string, object> campaignTraits = Program.ReadJsonObject(
                Program.CharacterFile(campaignId, heroId, "traits.json"));
            if (TryExtractFlingJudgment(hero, out judgment))
                source = "daily_snapshot";
            else if (TryExtractFlingJudgment(campaignTraits, out judgment))
                source = "campaign_traits";
            else if (notableDocuments.TryGetValue(heroId,
                    out Dictionary<string, object> notable)
                && TryExtractFlingJudgment(notable, out judgment))
                source = "notable_profile";
            else if (shippedDocuments.TryGetValue(heroId,
                    out Dictionary<string, object> shipped)
                && TryExtractFlingJudgment(shipped, out judgment))
                source = "profile_library";
            else if (runtimeDocuments.TryGetValue(heroId,
                    out Dictionary<string, object> runtime)
                && TryExtractFlingJudgment(runtime, out judgment))
                source = "relationship_personality";
            else
                return false;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Program.ExecuteSql(connection, @"INSERT INTO relationship_fling_profiles(
hero_id,judgment,source,updated_ts) VALUES($hero,$judgment,$source,$ts)
ON CONFLICT(hero_id) DO UPDATE SET judgment=$judgment,source=$source,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["hero"] = heroId, ["judgment"] = judgment,
                    ["source"] = source, ["ts"] = ts
                });
            persisted[heroId] = new Dictionary<string, object>
            {
                ["hero_id"] = heroId, ["judgment"] = judgment,
                ["source"] = source
            };
            return true;
        }

        private static bool FlingHeroEligible(
            Dictionary<string, object> hero)
        {
            return hero != null
                && ReadBool(hero, "isAlive", false)
                && ReadBool(hero, "isActive", true)
                && ReadBool(hero, "isAdult",
                    ReadDouble(hero, "age", 0d) >= 18d)
                && ReadDouble(hero, "age", 0d) >= 18d
                && !ReadBool(hero, "isPlayer", false)
                && !ReadBool(hero, "isPrisoner", false);
        }

        private static bool FlingPairEligible(FlingCandidate first,
            FlingCandidate second, HashSet<string> activeLoverPairs)
        {
            if (first == null || second == null
                || first.HeroId.Equals(second.HeroId,
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!MbtiRomanceEligible(first.Hero, second.Hero)) return false;
            string pairKey = AmbientPairKey(first.HeroId, second.HeroId);
            if (activeLoverPairs != null
                && activeLoverPairs.Contains(pairKey)) return false;
            if (ReadString(first.Hero, "spouseId", "").Equals(
                    second.HeroId, StringComparison.OrdinalIgnoreCase)
                || ReadString(second.Hero, "spouseId", "").Equals(
                    first.HeroId, StringComparison.OrdinalIgnoreCase))
                return false;
            HashSet<string> firstAncestors = new HashSet<string>(
                ReadStringList(first.Hero, "marriageAncestorIds"),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> secondAncestors = new HashSet<string>(
                ReadStringList(second.Hero, "marriageAncestorIds"),
                StringComparer.OrdinalIgnoreCase);
            if (firstAncestors.Contains(second.HeroId)
                || secondAncestors.Contains(first.HeroId)
                || firstAncestors.Overlaps(secondAncestors)) return false;
            return true;
        }

        private static int FlingLocationPriority(string kind)
        {
            switch ((kind ?? "").Trim().ToLowerInvariant())
            {
                case "player_party": return 0;
                case "army": return 1;
                case "settlement": return 2;
                case "party": return 3;
                default: return 4;
            }
        }

        private static List<Tuple<FlingCandidate, FlingCandidate>>
            PairFlingCandidates(string seed, List<FlingCandidate> candidates,
                HashSet<string> activeLoverPairs)
        {
            List<FlingCandidate> remaining = (candidates
                ?? new List<FlingCandidate>())
                .OrderBy(candidate => StableUnit(seed + "|candidate|"
                    + candidate.HeroId))
                .ThenBy(candidate => candidate.HeroId,
                    StringComparer.OrdinalIgnoreCase).ToList();
            List<Tuple<FlingCandidate, FlingCandidate>> pairs =
                new List<Tuple<FlingCandidate, FlingCandidate>>();
            while (remaining.Count > 1)
            {
                FlingCandidate first = remaining[0];
                remaining.RemoveAt(0);
                FlingCandidate partner = remaining
                    .Where(candidate => FlingPairEligible(first, candidate,
                        activeLoverPairs))
                    .OrderBy(candidate => StableUnit(seed + "|edge|"
                        + AmbientPairKey(first.HeroId, candidate.HeroId)))
                    .ThenBy(candidate => candidate.HeroId,
                        StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (partner == null) continue;
                remaining.Remove(partner);
                pairs.Add(Tuple.Create(first, partner));
            }
            return pairs;
        }

        private static Dictionary<string, object> ProcessDailyNpcFlings(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            Dictionary<int, List<Dictionary<string, object>>> groupsByDay,
            Dictionary<int, List<Dictionary<string, object>>> heroRowsByDay,
            Dictionary<string, Dictionary<string, object>> latestHeroes)
        {
            Stopwatch preparationTimer = Stopwatch.StartNew();
            EnsureRelationshipLifecycleSchema(connection);
            if (!WorldSimulationService.TableExists(connection, "relationship_fling_profiles")
                || !WorldSimulationService.TableExists(connection, "relationship_fling_rolls")
                || !WorldSimulationService.TableExists(connection, "relationship_fling_sparks")
                || !WorldSimulationService.TableExists(connection, "relationship_fling_encounters"))
                EnsureRelationshipFlingSchemaCore(connection);
            EnsureRelationshipDirectorSchema(connection);
            if (!WorldSimulationService.TableExists(connection, "rumor_occurrences"))
                ReputationService.EnsureSocialReputationSchema(connection);
            timelineId = string.IsNullOrWhiteSpace(timelineId)
                ? "main" : timelineId;
            Dictionary<string, object> aggregate =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> persistedJudgments =
                Program.QuerySql(connection, "SELECT * FROM relationship_fling_profiles;")
                    .Where(row => !string.IsNullOrWhiteSpace(
                        ReadString(row, "hero_id", "")))
                    .ToDictionary(row => ReadString(row, "hero_id", ""),
                        row => row, StringComparer.OrdinalIgnoreCase);
            groupsByDay = groupsByDay ?? new Dictionary<int, List<Dictionary<string, object>>>();
            Dictionary<int, Dictionary<string, Dictionary<string, object>>> contextualHeroesByDay =
                new Dictionary<int, Dictionary<string, Dictionary<string, object>>>();
            HashSet<string> unresolvedHeroes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (int day in groupsByDay.Keys)
            {
                Dictionary<string, Dictionary<string, object>> contextual =
                    new Dictionary<string, Dictionary<string, object>>(latestHeroes
                        ?? new Dictionary<string, Dictionary<string, object>>(), StringComparer.OrdinalIgnoreCase);
                if (heroRowsByDay != null && heroRowsByDay.TryGetValue(day, out List<Dictionary<string, object>> dayHeroes))
                    foreach (Dictionary<string, object> hero in dayHeroes)
                    {
                        string id = ReadFirstString(hero, "heroStringId", "heroId", "id");
                        if (!string.IsNullOrWhiteSpace(id)) contextual[id] = hero;
                    }
                contextualHeroesByDay[day] = contextual;
                foreach (string id in groupsByDay[day].SelectMany(group => ReadStringList(group, "heroIds")))
                    if (!persistedJudgments.ContainsKey(id) && contextual.TryGetValue(id, out Dictionary<string, object> hero)
                        && FlingHeroEligible(hero)) unresolvedHeroes.Add(id);
            }
            List<Dictionary<string, object>> notableRows = QueryRelationshipHeroRows(connection,
                "SELECT hero_id,percentages_json FROM notable_mbti_profiles", "hero_id", unresolvedHeroes);
            List<Dictionary<string, object>> personalityRows = QueryRelationshipHeroRows(connection,
                "SELECT hero_id,traits_json FROM relationship_personalities", "hero_id", unresolvedHeroes);
            aggregate["profileDocumentsLoaded"] = notableRows.Count + personalityRows.Count;
            aggregate["profileCharactersParsed"] = notableRows.Sum(row => ReadString(row, "percentages_json", "").Length)
                + personalityRows.Sum(row => ReadString(row, "traits_json", "").Length);
            Dictionary<string, Dictionary<string, object>> notableDocuments =
                notableRows
                    .Where(row => !string.IsNullOrWhiteSpace(
                        ReadString(row, "hero_id", "")))
                    .ToDictionary(row => ReadString(row, "hero_id", ""),
                        row => new Dictionary<string, object>
                        {
                            ["traitPercentages"] = TryParseJsonObject(
                                ReadString(row, "percentages_json", "{}"))
                                ?? new Dictionary<string, object>()
                        }, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> runtimeDocuments =
                personalityRows
                    .Where(row => !string.IsNullOrWhiteSpace(
                        ReadString(row, "hero_id", "")))
                    .ToDictionary(row => ReadString(row, "hero_id", ""),
                        row => TryParseJsonObject(ReadString(row,
                            "traits_json", "{}"))
                            ?? new Dictionary<string, object>(),
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> shippedDocuments =
                CharactersService.LoadCharacterProfileLibrary();
            Dictionary<string, int> lastFlingByHero = Program.QuerySql(connection, @"
SELECT hero_id,MAX(world_day) AS world_day FROM (
 SELECT hero_a_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE timeline_id=$timeline AND status='completed'
 UNION ALL
 SELECT hero_b_id AS hero_id,world_day FROM relationship_fling_encounters
 WHERE timeline_id=$timeline AND status='completed'
) flings GROUP BY hero_id;", new Dictionary<string, object>
                {
                    ["timeline"] = timelineId
                }).Where(row => !string.IsNullOrWhiteSpace(
                    ReadString(row, "hero_id", "")))
                .ToDictionary(row => ReadString(row, "hero_id", ""),
                    row => ReadInt(row, "world_day", -1),
                    StringComparer.OrdinalIgnoreCase);
            HashSet<string> activeLoverPairs = new HashSet<string>(
                Program.QuerySql(connection, @"SELECT pair_key
FROM relationship_pair_lifecycle WHERE lover_active=1;")
                    .Select(row => ReadString(row, "pair_key", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> pendingMothers = new HashSet<string>(
                Program.QuerySql(connection, @"SELECT mother_id FROM conceptions
WHERE status='pending_game';")
                    .Select(row => ReadString(row, "mother_id", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> socialCatalog =
                ReputationService.ReadActiveSocialCatalog(connection);
            aggregate["preparationMs"] = preparationTimer.ElapsedMilliseconds;

            foreach (int day in (groupsByDay
                ?? new Dictionary<int, List<Dictionary<string, object>>>())
                .Keys.OrderBy(value => value))
            {
                Dictionary<string, Dictionary<string, object>> contextualHeroes =
                    contextualHeroesByDay[day];
                List<Dictionary<string, object>> pendingRolls = new List<Dictionary<string, object>>();
                int rollWriteCommands = 0;

                Dictionary<string, Dictionary<string, object>> existingRolls =
                    Program.QuerySql(connection, @"SELECT *
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["day"] = day
                        })
                        .Where(row => !string.IsNullOrWhiteSpace(
                            ReadString(row, "hero_id", "")))
                        .ToDictionary(row => ReadString(row, "hero_id", ""),
                            row => row, StringComparer.OrdinalIgnoreCase);
                HashSet<string> assignedToday = new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);
                int unavailableJudgment = 0;
                int cooldownExcluded = 0;
                int duplicateSuppressed = 0;
                List<Dictionary<string, object>> orderedGroups =
                    groupsByDay[day]
                        .OrderBy(group => FlingLocationPriority(
                            ReadString(group, "kind", "")))
                        .ThenBy(group => ReadString(group, "id", ""),
                            StringComparer.OrdinalIgnoreCase).ToList();
                foreach (Dictionary<string, object> group in orderedGroups)
                {
                    string locationKind = ReadString(group, "kind", "");
                    string locationId = ReadString(group, "id", "");
                    string locationKey = locationKind + "|" + locationId;
                    List<FlingCandidate> passed = new List<FlingCandidate>();
                    foreach (string heroId in ReadStringList(group, "heroIds")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value,
                            StringComparer.OrdinalIgnoreCase))
                    {
                        if (!assignedToday.Add(heroId)
                            || !contextualHeroes.TryGetValue(heroId,
                                out Dictionary<string, object> hero)
                            || !FlingHeroEligible(hero)) continue;
                        if (!TryResolveFlingJudgment(connection, campaignId,
                            heroId, hero, persistedJudgments,
                            notableDocuments, runtimeDocuments,
                            shippedDocuments, out int judgment,
                            out string judgmentSource))
                        {
                            unavailableJudgment++;
                            continue;
                        }
                        if (judgment > FlingMaximumJudgment) continue;
                        int lastFlingDay = lastFlingByHero.TryGetValue(heroId,
                            out int recordedDay) ? recordedDay : -1;
                        if (existingRolls.TryGetValue(heroId,
                            out Dictionary<string, object> existingRoll))
                        {
                            duplicateSuppressed++;
                            if (ReadInt(existingRoll, "passed", 0) != 0
                                && ReadString(existingRoll, "status", "")
                                    .Equals("passed_unmatched",
                                        StringComparison.OrdinalIgnoreCase)
                                && FlingCooldownComplete(lastFlingDay, day))
                            {
                                passed.Add(new FlingCandidate
                                {
                                    HeroId = heroId, Hero = hero,
                                    LocationKind = ReadString(existingRoll,
                                        "location_kind", locationKind),
                                    LocationId = ReadString(existingRoll,
                                        "location_id", locationId),
                                    Judgment = ReadInt(existingRoll,
                                        "judgment", judgment),
                                    Chance = ReadInt(existingRoll,
                                        "chance_percent",
                                        FlingChancePercent(judgment)),
                                    Roll = ReadInt(existingRoll, "roll", 100)
                                });
                            }
                            continue;
                        }
                        if (!FlingCooldownComplete(lastFlingDay, day))
                        {
                            cooldownExcluded++;
                            continue;
                        }
                        int chance = FlingChancePercent(judgment);
                        int roll = StableDie(string.Join("|", campaignId,
                            timelineId, day.ToString(CultureInfo.InvariantCulture),
                            heroId, locationKey, "fling_gate_v1"), 100);
                        bool passedRoll = roll <= chance;
                        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        pendingRolls.Add(new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["day"] = day,
                            ["hero"] = heroId, ["kind"] = locationKind,
                            ["location"] = locationId,
                            ["judgment"] = judgment, ["chance"] = chance,
                            ["roll"] = roll, ["passed"] = passedRoll ? 1 : 0,
                            ["status"] = passedRoll
                                ? "passed_unmatched" : "failed_roll",
                            ["ts"] = ts
                        });
                        existingRolls[heroId] = new Dictionary<string, object>
                        {
                            ["hero_id"] = heroId,
                            ["location_kind"] = locationKind,
                            ["location_id"] = locationId,
                            ["judgment"] = judgment,
                            ["chance_percent"] = chance,
                            ["roll"] = roll,
                            ["passed"] = passedRoll ? 1 : 0,
                            ["status"] = passedRoll
                                ? "passed_unmatched" : "failed_roll"
                        };
                        if (!passedRoll) continue;
                        passed.Add(new FlingCandidate
                        {
                            HeroId = heroId, Hero = hero,
                            LocationKind = locationKind,
                            LocationId = locationId, Judgment = judgment,
                            Chance = chance, Roll = roll
                        });
                    }

                    // Pairing can update/read these receipts. Flush at that
                    // dependency boundary, and otherwise combine quiet groups.
                    if (passed.Count > 1 || pendingRolls.Count >= PostgreSqlRelationshipWriteChunkSize)
                        rollWriteCommands += FlushRelationshipFlingRolls(connection, pendingRolls);
                    string pairingSeed = string.Join("|", campaignId,
                        timelineId, day.ToString(CultureInfo.InvariantCulture),
                        locationKey, "fling_pairing_v1");
                    foreach (Tuple<FlingCandidate, FlingCandidate> pair
                        in PairFlingCandidates(pairingSeed, passed,
                            activeLoverPairs))
                    {
                        string pairKey = AmbientPairKey(pair.Item1.HeroId,
                            pair.Item2.HeroId);
                        int sparkRoll = FlingSparkRoll(campaignId,
                            timelineId, day, pair.Item1, pair.Item2);
                        bool sparkPassed = sparkRoll
                            <= FlingSparkChancePercent;
                        long sparkTs = DateTimeOffset.UtcNow
                            .ToUnixTimeSeconds();
                        Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_fling_sparks(
campaign_id,timeline_id,world_day,pair_key,hero_a_id,hero_b_id,
location_kind,location_id,chance_percent,roll,passed,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$pair,$a,$b,$kind,$location,$chance,
$roll,$passed,$ts,$ts);", new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["day"] = day,
                            ["pair"] = pairKey,
                            ["a"] = pair.Item1.HeroId,
                            ["b"] = pair.Item2.HeroId,
                            ["kind"] = pair.Item1.LocationKind,
                            ["location"] = pair.Item1.LocationId,
                            ["chance"] = FlingSparkChancePercent,
                            ["roll"] = sparkRoll,
                            ["passed"] = sparkPassed ? 1 : 0,
                            ["ts"] = sparkTs
                        });
                        Dictionary<string, object> sparkReceipt =
                            Program.QuerySql(connection, @"SELECT * FROM relationship_fling_sparks
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND world_day=$day AND pair_key=$pair LIMIT 1;",
                                new Dictionary<string, object>
                                {
                                    ["campaign"] = campaignId,
                                    ["timeline"] = timelineId,
                                    ["day"] = day, ["pair"] = pairKey
                                }).FirstOrDefault()
                            ?? new Dictionary<string, object>();
                        if (ReadInt(sparkReceipt, "passed", 0) == 0)
                        {
                            Program.ExecuteSql(connection, @"UPDATE relationship_fling_rolls
SET status='spark_failed',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND world_day=$day
AND hero_id IN ($a,$b);", new Dictionary<string, object>
                            {
                                ["ts"] = sparkTs,
                                ["campaign"] = campaignId,
                                ["timeline"] = timelineId, ["day"] = day,
                                ["a"] = pair.Item1.HeroId,
                                ["b"] = pair.Item2.HeroId
                            });
                            continue;
                        }
                        Dictionary<string, object> encounter =
                            ProcessFlingEncounter(connection, campaignId,
                                timelineId, day, pair.Item1, pair.Item2,
                                pendingMothers, socialCatalog);
                        string encounterId = ReadString(encounter,
                            "encounterId", "");
                        if (ReadBool(encounter, "duplicate", false))
                            duplicateSuppressed++;
                        if (string.IsNullOrWhiteSpace(encounterId)) continue;
                        lastFlingByHero[pair.Item1.HeroId] = day;
                        lastFlingByHero[pair.Item2.HeroId] = day;
                        Program.ExecuteSql(connection, @"UPDATE relationship_fling_sparks
SET encounter_id=$encounter,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND world_day=$day AND pair_key=$pair;",
                            new Dictionary<string, object>
                            {
                                ["encounter"] = encounterId,
                                ["ts"] = DateTimeOffset.UtcNow
                                    .ToUnixTimeSeconds(),
                                ["campaign"] = campaignId,
                                ["timeline"] = timelineId,
                                ["day"] = day, ["pair"] = pairKey
                            });
                        Program.ExecuteSql(connection, @"UPDATE relationship_fling_rolls
SET status='paired',encounter_id=$encounter,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND world_day=$day
AND hero_id IN ($a,$b);", new Dictionary<string, object>
                        {
                            ["encounter"] = encounterId,
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["day"] = day,
                            ["a"] = pair.Item1.HeroId,
                            ["b"] = pair.Item2.HeroId
                        });
                    }
                }

                rollWriteCommands += FlushRelationshipFlingRolls(connection, pendingRolls);
                WorldSimulationService.IncrementWorldTestObjectCounter(aggregate, "rollWriteCommands", rollWriteCommands);
                Dictionary<string, object> dayCounters =
                    BuildFlingDayCounters(connection, campaignId, timelineId,
                        day, unavailableJudgment, cooldownExcluded,
                        duplicateSuppressed);
                WorldSimulationService.RecordWorldTestCounterSchemaReady(connection, campaignId,
                    timelineId, day, "relationships", "flings_daily_summary",
                    dayCounters);
                foreach (KeyValuePair<string, object> counter in dayCounters)
                    WorldSimulationService.IncrementWorldTestObjectCounter(aggregate, counter.Key,
                        Convert.ToInt32(counter.Value,
                            CultureInfo.InvariantCulture));
            }
            return aggregate;
        }

        private static int FlushRelationshipFlingRolls(ReignDbConnection connection,
            List<Dictionary<string, object>> rows)
        {
            if (rows.Count == 0) return 0;
            int commands = 1;
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                ExecutePostgreSqlJsonCommand(connection, @"
INSERT INTO relationship_fling_rolls(campaign_id,timeline_id,world_day,hero_id,
location_kind,location_id,judgment,chance_percent,roll,passed,status,created_ts,updated_ts)
SELECT campaign,timeline,day,hero,kind,location,judgment,chance,roll,passed,status,ts,ts
FROM jsonb_to_recordset(@rows) AS r(campaign text,timeline text,day integer,hero text,
kind text,location text,judgment integer,chance integer,roll integer,passed integer,status text,ts bigint)
ON CONFLICT(campaign_id,timeline_id,world_day,hero_id) DO NOTHING;",
                    PostgreSqlRelationshipRowsJson(rows));
            else
            {
                commands = rows.Count;
                foreach (Dictionary<string, object> row in rows)
                    Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_fling_rolls(
campaign_id,timeline_id,world_day,hero_id,location_kind,location_id,
judgment,chance_percent,roll,passed,status,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$hero,$kind,$location,$judgment,$chance,
$roll,$passed,$status,$ts,$ts);", row);
            }
            rows.Clear();
            return commands;
        }

        private static Dictionary<string, object> BuildFlingDayCounters(
            ReignDbConnection connection, string campaignId,
            string timelineId, int day, int unavailableJudgment,
            int cooldownExcluded, int duplicateSuppressed)
        {
            Dictionary<string, object> parameters =
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = day
                };
            Dictionary<string, object> rolls = Program.QuerySql(connection, @"
SELECT COUNT(*) AS rolls,SUM(passed) AS passes,
SUM(CASE WHEN status='passed_unmatched' THEN 1 ELSE 0 END) AS unmatched
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> sparks = Program.QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> encounters = Program.QuerySql(connection, @"
SELECT COUNT(*) AS pairings,SUM(same_sex) AS same_sex,
SUM(CASE WHEN same_sex=0 THEN 1 ELSE 0 END) AS opposite_sex,
SUM(short_affair) AS short_affairs,
SUM(CASE WHEN short_affair=1 THEN 1 ELSE 0 END) AS fixed_discovery_attempts,
SUM(fixed_discovered) AS fixed_discoveries,
SUM(CASE WHEN pregnancy_discovery_roll>=0 THEN 1 ELSE 0 END) AS pregnancy_discovery_attempts,
SUM(pregnancy_discovered) AS pregnancy_discoveries,
SUM(CASE WHEN conception_attempt_id<>'' THEN 1 ELSE 0 END) AS conception_attempts,
SUM(conceived) AS conceptions,
SUM(CASE WHEN conception_action_id<>'' THEN 1 ELSE 0 END) AS conception_actions
FROM relationship_fling_encounters WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day AND status='completed';",
                parameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["flingEligible"] = ReadInt(rolls, "rolls", 0),
                ["flingRolls"] = ReadInt(rolls, "rolls", 0),
                ["flingPasses"] = ReadInt(rolls, "passes", 0),
                ["flingUnmatched"] = ReadInt(rolls, "unmatched", 0),
                ["flingSparkAttempts"] = ReadInt(sparks, "attempts", 0),
                ["flingSparkPasses"] = ReadInt(sparks, "passes", 0),
                ["flingPairs"] = ReadInt(encounters, "pairings", 0),
                ["flingSameSexPairs"] = ReadInt(encounters, "same_sex", 0),
                ["flingOppositeSexPairs"] = ReadInt(encounters,
                    "opposite_sex", 0),
                ["flingShortAffairs"] = ReadInt(encounters,
                    "short_affairs", 0),
                ["flingFixedDiscoveryAttempts"] = ReadInt(encounters,
                    "fixed_discovery_attempts", 0),
                ["flingDiscoveries"] = ReadInt(encounters,
                    "fixed_discoveries", 0),
                ["flingPregnancyDiscoveryAttempts"] = ReadInt(encounters,
                    "pregnancy_discovery_attempts", 0),
                ["flingPregnancyDiscoveries"] = ReadInt(encounters,
                    "pregnancy_discoveries", 0),
                ["flingConceptionAttempts"] = ReadInt(encounters,
                    "conception_attempts", 0),
                ["flingConceptions"] = ReadInt(encounters,
                    "conceptions", 0),
                ["flingConceptionActions"] = ReadInt(encounters,
                    "conception_actions", 0),
                ["flingUnavailableJudgment"] = unavailableJudgment,
                ["flingCooldownExcluded"] = cooldownExcluded,
                ["flingDuplicateSuppressions"] = duplicateSuppressed
            };
        }

        private static Dictionary<string, object> ProcessFlingEncounter(
            ReignDbConnection connection, string campaignId,
            string timelineId, int day, FlingCandidate first,
            FlingCandidate second, HashSet<string> pendingMothers,
            Dictionary<string, object> socialCatalog)
        {
            string pairKey = AmbientPairKey(first.HeroId, second.HeroId);
            string locationKind = first.LocationKind;
            string locationId = first.LocationId;
            string encounterId = "fling_" + ReputationService.DeterministicSocialId(
                string.Join("|", campaignId, timelineId,
                    day.ToString(CultureInfo.InvariantCulture),
                    locationKind, locationId, pairKey, "fling_encounter_v1"));
            Dictionary<string, object> prior = Program.QuerySql(connection, @"
SELECT encounter_id FROM relationship_fling_encounters
WHERE encounter_id=$id LIMIT 1;", new Dictionary<string, object>
                {
                    ["id"] = encounterId
                }).FirstOrDefault();
            if (prior != null)
            {
                return new Dictionary<string, object>
                {
                    ["encounterId"] = encounterId, ["duplicate"] = true
                };
            }

            bool sameSex = ReadBool(first.Hero, "isFemale", false)
                == ReadBool(second.Hero, "isFemale", false);
            string spouseA = ReadString(first.Hero, "spouseId", "");
            string spouseB = ReadString(second.Hero, "spouseId", "");
            bool shortAffair = !string.IsNullOrWhiteSpace(spouseA)
                || !string.IsNullOrWhiteSpace(spouseB);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Program.ExecuteSql(connection, @"INSERT INTO relationship_fling_encounters(
encounter_id,campaign_id,timeline_id,world_day,pair_key,hero_a_id,hero_b_id,
location_kind,location_id,same_sex,short_affair,status,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$day,$pair,$a,$b,$kind,$location,$sameSex,
$shortAffair,'processing',$ts,$ts);", new Dictionary<string, object>
            {
                ["id"] = encounterId, ["campaign"] = campaignId,
                ["timeline"] = timelineId, ["day"] = day,
                ["pair"] = pairKey, ["a"] = first.HeroId,
                ["b"] = second.HeroId, ["kind"] = locationKind,
                ["location"] = locationId, ["sameSex"] = sameSex ? 1 : 0,
                ["shortAffair"] = shortAffair ? 1 : 0, ["ts"] = ts
            });

            AmbientPairContext pair = new AmbientPairContext
            {
                PairKey = pairKey, HeroAId = first.HeroId,
                HeroBId = second.HeroId, ContextKind = locationKind,
                ContextId = locationId, ExposureWeight = 1d
            };
            double fixedChance = shortAffair
                ? FlingDiscoveryChancePercent / 100d : 0d;
            double fixedRoll = -1d;
            bool fixedDiscovered = false;
            string rumorId = "";
            if (shortAffair)
            {
                Dictionary<string, object> occurrencePayload =
                    ReputationService.BuildRelationshipSocialOccurrence(pair, day, "affair",
                        first.Hero, second.Hero, spouseA, spouseB, false,
                        false,
                        "A brief fling involving a married participant was discovered.",
                        timelineId);
                occurrencePayload["sourceEventId"] = encounterId;
                occurrencePayload["exposureSeedKey"] = encounterId
                    + "|fixed_twenty_percent_discovery";
                occurrencePayload["exposureChanceOverride"] = fixedChance;
                Dictionary<string, object> occurrence =
                    ReputationService.RegisterSocialOccurrence(connection, campaignId,
                        occurrencePayload, socialCatalog,
                        worldTestSchemaReady: true,
                        useExistingTransaction: true);
                fixedRoll = ReadDouble(occurrence, "roll",
                    ReadDouble(occurrence, "exposureRoll", -1d));
                fixedDiscovered = ReadBool(occurrence, "exposed", false);
                rumorId = ReadString(occurrence, "occurrenceId", "");
            }

            Dictionary<string, object> conception = sameSex
                ? new Dictionary<string, object>()
                : TryCreateFlingConception(connection, campaignId,
                    timelineId, encounterId, day, first, second,
                    pendingMothers);
            bool conceived = ReadBool(conception, "success", false);
            string motherId = ReadString(conception, "motherId", "");
            bool marriedWomanConceived = conceived
                && ReadBool(conception, "motherMarriedElsewhere", false);
            double pregnancyDiscoveryChance = 0d;
            double pregnancyDiscoveryRoll = -1d;
            bool pregnancyDiscovered = false;
            if (marriedWomanConceived)
            {
                Dictionary<string, object> occurrencePayload =
                    ReputationService.BuildRelationshipSocialOccurrence(pair, day, "affair",
                        first.Hero, second.Hero, spouseA, spouseB, false,
                        false,
                        "A married woman's pregnancy created an additional chance for the brief affair to be discovered.",
                        timelineId);
                occurrencePayload["sourceEventId"] = encounterId;
                occurrencePayload["exposureSeedKey"] = encounterId
                    + "|pregnancy_discovery";
                Dictionary<string, object> occurrence =
                    ReputationService.RegisterSocialOccurrence(connection, campaignId,
                        occurrencePayload, socialCatalog,
                        worldTestSchemaReady: true,
                        useExistingTransaction: true);
                pregnancyDiscoveryChance = ReadDouble(occurrence,
                    "chance", ReadDouble(occurrence,
                        "exposureChance", 0d));
                pregnancyDiscoveryRoll = ReadDouble(occurrence,
                    "roll", ReadDouble(occurrence,
                        "exposureRoll", -1d));
                pregnancyDiscovered = ReadBool(occurrence, "exposed", false);
                rumorId = FirstNonEmpty(rumorId,
                    ReadString(occurrence, "occurrenceId", ""));
            }

            bool discoveryApplied = fixedDiscovered || pregnancyDiscovered;
            Dictionary<string, object> incidentPayload =
                new Dictionary<string, object>
                {
                    ["encounterId"] = encounterId,
                    ["timelineId"] = timelineId,
                    ["locationKind"] = locationKind,
                    ["locationId"] = locationId,
                    ["judgmentA"] = first.Judgment,
                    ["judgmentB"] = second.Judgment,
                    ["chanceA"] = first.Chance,
                    ["chanceB"] = second.Chance,
                    ["rollA"] = first.Roll, ["rollB"] = second.Roll,
                    ["sameSex"] = sameSex, ["shortAffair"] = shortAffair,
                    ["fixedDiscoveryChance"] = fixedChance,
                    ["fixedDiscoveryRoll"] = fixedRoll,
                    ["fixedDiscovered"] = fixedDiscovered,
                    ["pregnancyDiscoveryChance"] =
                        pregnancyDiscoveryChance,
                    ["pregnancyDiscoveryRoll"] = pregnancyDiscoveryRoll,
                    ["pregnancyDiscovered"] = pregnancyDiscovered,
                    ["conceptionAttemptId"] = ReadString(conception,
                        "attemptId", ""),
                    ["conceptionId"] = ReadString(conception,
                        "conceptionId", ""),
                    ["conceived"] = conceived,
                    ["motherId"] = motherId
                };
            RecordRelationshipIncident(connection, pair,
                shortAffair ? "short_affair_fling" : "fling", day,
                shortAffair
                    ? "Two co-located low-judgment characters had a brief fling involving a married participant."
                    : "Two co-located low-judgment characters had a brief fling.",
                incidentPayload, rumorId);
            Program.ExecuteSql(connection, @"UPDATE relationship_fling_encounters SET
fixed_discovery_chance=$fixedChance,fixed_discovery_roll=$fixedRoll,
fixed_discovered=$fixedFound,pregnancy_discovery_chance=$pregnancyChance,
pregnancy_discovery_roll=$pregnancyRoll,pregnancy_discovered=$pregnancyFound,
discovery_applied=$discoveryApplied,rumor_id=$rumor,
conception_attempt_id=$attempt,conception_id=$conception,
conception_chance=$conceptionChance,conception_roll=$conceptionRoll,
conceived=$conceived,conception_action_id=$action,status='completed',
payload_json=$payload,updated_ts=$ts WHERE encounter_id=$id;",
                new Dictionary<string, object>
                {
                    ["fixedChance"] = fixedChance,
                    ["fixedRoll"] = fixedRoll,
                    ["fixedFound"] = fixedDiscovered ? 1 : 0,
                    ["pregnancyChance"] = pregnancyDiscoveryChance,
                    ["pregnancyRoll"] = pregnancyDiscoveryRoll,
                    ["pregnancyFound"] = pregnancyDiscovered ? 1 : 0,
                    ["discoveryApplied"] = discoveryApplied ? 1 : 0,
                    ["rumor"] = rumorId,
                    ["attempt"] = ReadString(conception, "attemptId", ""),
                    ["conception"] = ReadString(conception,
                        "conceptionId", ""),
                    ["conceptionChance"] = ReadDouble(conception,
                        "chance", 0d),
                    ["conceptionRoll"] = ReadDouble(conception, "roll", -1d),
                    ["conceived"] = conceived ? 1 : 0,
                    ["action"] = ReadString(conception, "actionId", ""),
                    ["payload"] = Json.Serialize(incidentPayload),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["id"] = encounterId
                });
            return new Dictionary<string, object>
            {
                ["encounterId"] = encounterId, ["duplicate"] = false,
                ["conceived"] = conceived,
                ["discovered"] = discoveryApplied
            };
        }

        private static Dictionary<string, object> TryCreateFlingConception(
            ReignDbConnection connection, string campaignId,
            string timelineId, string encounterId, int day,
            FlingCandidate first, FlingCandidate second,
            HashSet<string> pendingMothers)
        {
            FlingCandidate mother = ReadBool(first.Hero, "isFemale", false)
                && !ReadBool(second.Hero, "isFemale", false) ? first
                : ReadBool(second.Hero, "isFemale", false)
                    && !ReadBool(first.Hero, "isFemale", false) ? second
                    : null;
            FlingCandidate father = ReferenceEquals(mother, first)
                ? second : ReferenceEquals(mother, second) ? first : null;
            if (mother == null || father == null
                || ReadBool(mother.Hero, "isPregnant", false)
                || pendingMothers.Contains(mother.HeroId))
                return new Dictionary<string, object>();
            double age = ReadDouble(mother.Hero, "age", 0d);
            double chance = NpcConceptionChance(age,
                ReadInt(mother.Hero, "childrenCount", 0));
            if (chance <= 0d) return new Dictionary<string, object>();
            string attemptId = "npc_fling_" + encounterId;
            double roll = StableUnit(attemptId + "|conception");
            bool success = roll < chance;
            string motherSpouse = ReadString(mother.Hero, "spouseId", "");
            string fatherSpouse = ReadString(father.Hero, "spouseId", "");
            bool motherMarriedElsewhere = !string.IsNullOrWhiteSpace(
                    motherSpouse)
                && !motherSpouse.Equals(father.HeroId,
                    StringComparison.OrdinalIgnoreCase);
            bool fatherMarriedElsewhere = !string.IsNullOrWhiteSpace(
                    fatherSpouse)
                && !fatherSpouse.Equals(mother.HeroId,
                    StringComparison.OrdinalIgnoreCase);
            // A fling is never a marriage. Its child is born outside the
            // biological parents' marriage even when neither was previously
            // married; an existing husband remains the legal father.
            bool illegitimate = true;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Dictionary<string, object> attemptPayload =
                new Dictionary<string, object>
                {
                    ["source"] = "relationship_fling",
                    ["timelineId"] = timelineId,
                    ["encounterId"] = encounterId,
                    ["isIllegitimate"] = illegitimate,
                    ["motherMarriedElsewhere"] = motherMarriedElsewhere,
                    ["fatherMarriedElsewhere"] = fatherMarriedElsewhere
                };
            Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO conception_attempts(
attempt_id,source,event_id,mother_id,father_id,mother_age,chance,roll,success,
world_day,payload_json,created_ts,status)
VALUES($id,'npc_fling',$event,$mother,$father,$age,$chance,$roll,$success,
$day,$payload,$ts,'resolved');", new Dictionary<string, object>
            {
                ["id"] = attemptId, ["event"] = encounterId,
                ["mother"] = mother.HeroId, ["father"] = father.HeroId,
                ["age"] = age, ["chance"] = chance, ["roll"] = roll,
                ["success"] = success ? 1 : 0, ["day"] = day,
                ["payload"] = Json.Serialize(attemptPayload), ["ts"] = ts
            });
            if (!success)
            {
                return new Dictionary<string, object>
                {
                    ["attemptId"] = attemptId, ["motherId"] = mother.HeroId,
                    ["chance"] = chance, ["roll"] = roll,
                    ["success"] = false,
                    ["motherMarriedElsewhere"] = motherMarriedElsewhere
                };
            }

            string conceptionId = "conception_fling_"
                + ReputationService.DeterministicSocialId(encounterId + "|conception");
            string legalFatherId = motherMarriedElsewhere
                ? motherSpouse : father.HeroId;
            double secrecy = illegitimate ? 0.75d : 0d;
            Dictionary<string, object> conceptionPayload =
                new Dictionary<string, object>(attemptPayload)
                {
                    ["motherId"] = mother.HeroId,
                    ["biologicalFatherId"] = father.HeroId,
                    ["legalFatherId"] = legalFatherId,
                    ["conceptionDay"] = day, ["dueDay"] = day + 36d,
                    ["secrecy"] = secrecy
                };
            Program.ExecuteSql(connection, @"INSERT OR IGNORE INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,
conception_day,due_day,status,secrecy,payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$bio,$legal,$day,$due,'pending_game',$secrecy,
$payload,$ts,$ts);", new Dictionary<string, object>
            {
                ["id"] = conceptionId, ["attempt"] = attemptId,
                ["mother"] = mother.HeroId, ["bio"] = father.HeroId,
                ["legal"] = legalFatherId, ["day"] = day,
                ["due"] = day + 36d, ["secrecy"] = secrecy,
                ["payload"] = Json.Serialize(conceptionPayload), ["ts"] = ts
            });
            string actionId = QueueDirectorAction(connection,
                "start_conception", day, mother.HeroId, father.HeroId,
                new Dictionary<string, object>(conceptionPayload)
                {
                    ["conceptionId"] = conceptionId
                });
            pendingMothers.Add(mother.HeroId);
            return new Dictionary<string, object>
            {
                ["attemptId"] = attemptId, ["conceptionId"] = conceptionId,
                ["actionId"] = actionId, ["motherId"] = mother.HeroId,
                ["fatherId"] = father.HeroId,
                ["legalFatherId"] = legalFatherId,
                ["chance"] = chance, ["roll"] = roll, ["success"] = true,
                ["isIllegitimate"] = illegitimate,
                ["motherMarriedElsewhere"] = motherMarriedElsewhere,
                ["fatherMarriedElsewhere"] = fatherMarriedElsewhere
            };
        }

        private sealed class FlingCandidate
        {
            public string HeroId = "";
            public Dictionary<string, object> Hero =
                new Dictionary<string, object>();
            public string LocationKind = "";
            public string LocationId = "";
            public int Judgment;
            public int Chance;
            public int Roll;
        }
    }
}
