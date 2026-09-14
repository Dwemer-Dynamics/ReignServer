using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int FlingMaximumJudgment = 40;
        private const int FlingMaximumChancePercent = 50;
        private const int FlingSparkChancePercent = 25;
        private const int FlingCooldownDays = 7;
        private const int FlingDiscoveryChancePercent = 20;

        private static void EnsureRelationshipFlingSchemaCore(
            ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_profiles (
hero_id TEXT PRIMARY KEY,judgment INTEGER NOT NULL,source TEXT NOT NULL DEFAULT '',
updated_ts INTEGER NOT NULL);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day INTEGER NOT NULL,
hero_id TEXT NOT NULL,location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
judgment INTEGER NOT NULL,chance_percent INTEGER NOT NULL,roll INTEGER NOT NULL,
passed INTEGER NOT NULL,status TEXT NOT NULL,encounter_id TEXT NOT NULL DEFAULT '',
created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,world_day,hero_id));" );
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_rolls_hero
ON relationship_fling_rolls(timeline_id,hero_id,world_day DESC);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_sparks (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day INTEGER NOT NULL,
pair_key TEXT NOT NULL,hero_a_id TEXT NOT NULL,hero_b_id TEXT NOT NULL,
location_kind TEXT NOT NULL DEFAULT '',location_id TEXT NOT NULL DEFAULT '',
chance_percent INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,
encounter_id TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,world_day,pair_key));" );
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_sparks_day
ON relationship_fling_sparks(timeline_id,world_day DESC);" );
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS relationship_fling_encounters (
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
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_day
ON relationship_fling_encounters(timeline_id,world_day DESC);" );
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_a
ON relationship_fling_encounters(timeline_id,hero_a_id,world_day DESC);" );
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_relationship_fling_encounters_b
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
            virtues = CalculateCourtVirtues(percentages);
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

            Dictionary<string, object> campaignTraits = ReadJsonObject(
                CharacterFile(campaignId, heroId, "traits.json"));
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
            ExecuteSql(connection, @"INSERT INTO relationship_fling_profiles(
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
            if (!TableExists(connection, "relationship_fling_profiles")
                || !TableExists(connection, "relationship_fling_rolls")
                || !TableExists(connection, "relationship_fling_sparks")
                || !TableExists(connection, "relationship_fling_encounters"))
                EnsureRelationshipFlingSchemaCore(connection);
            EnsureRelationshipDirectorSchema(connection);
            if (!TableExists(connection, "rumor_occurrences"))
                EnsureSocialReputationSchema(connection);
            timelineId = string.IsNullOrWhiteSpace(timelineId)
                ? "main" : timelineId;
            Dictionary<string, object> aggregate =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> persistedJudgments =
                QuerySql(connection, "SELECT * FROM relationship_fling_profiles;")
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
                LoadCharacterProfileLibrary();
            Dictionary<string, int> lastFlingByHero = QuerySql(connection, @"
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
                QuerySql(connection, @"SELECT pair_key
FROM relationship_pair_lifecycle WHERE lover_active=1;")
                    .Select(row => ReadString(row, "pair_key", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            HashSet<string> pendingMothers = new HashSet<string>(
                QuerySql(connection, @"SELECT mother_id FROM conceptions
WHERE status='pending_game';")
                    .Select(row => ReadString(row, "mother_id", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> socialCatalog =
                ReadActiveSocialCatalog(connection);
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
                    QuerySql(connection, @"SELECT *
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
                        ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_fling_sparks(
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
                            QuerySql(connection, @"SELECT * FROM relationship_fling_sparks
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
                            ExecuteSql(connection, @"UPDATE relationship_fling_rolls
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
                        ExecuteSql(connection, @"UPDATE relationship_fling_sparks
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
                        ExecuteSql(connection, @"UPDATE relationship_fling_rolls
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
                IncrementWorldTestObjectCounter(aggregate, "rollWriteCommands", rollWriteCommands);
                Dictionary<string, object> dayCounters =
                    BuildFlingDayCounters(connection, campaignId, timelineId,
                        day, unavailableJudgment, cooldownExcluded,
                        duplicateSuppressed);
                RecordWorldTestCounterSchemaReady(connection, campaignId,
                    timelineId, day, "relationships", "flings_daily_summary",
                    dayCounters);
                foreach (KeyValuePair<string, object> counter in dayCounters)
                    IncrementWorldTestObjectCounter(aggregate, counter.Key,
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
                    ExecuteSql(connection, @"INSERT OR IGNORE INTO relationship_fling_rolls(
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
            Dictionary<string, object> rolls = QuerySql(connection, @"
SELECT COUNT(*) AS rolls,SUM(passed) AS passes,
SUM(CASE WHEN status='passed_unmatched' THEN 1 ELSE 0 END) AS unmatched
FROM relationship_fling_rolls WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> sparks = QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE campaign_id=$campaign
AND timeline_id=$timeline AND world_day=$day;", parameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> encounters = QuerySql(connection, @"
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
            string encounterId = "fling_" + DeterministicSocialId(
                string.Join("|", campaignId, timelineId,
                    day.ToString(CultureInfo.InvariantCulture),
                    locationKind, locationId, pairKey, "fling_encounter_v1"));
            Dictionary<string, object> prior = QuerySql(connection, @"
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
            ExecuteSql(connection, @"INSERT INTO relationship_fling_encounters(
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
                    BuildRelationshipSocialOccurrence(pair, day, "affair",
                        first.Hero, second.Hero, spouseA, spouseB, false,
                        false,
                        "A brief fling involving a married participant was discovered.",
                        timelineId);
                occurrencePayload["sourceEventId"] = encounterId;
                occurrencePayload["exposureSeedKey"] = encounterId
                    + "|fixed_twenty_percent_discovery";
                occurrencePayload["exposureChanceOverride"] = fixedChance;
                Dictionary<string, object> occurrence =
                    RegisterSocialOccurrence(connection, campaignId,
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
                    BuildRelationshipSocialOccurrence(pair, day, "affair",
                        first.Hero, second.Hero, spouseA, spouseB, false,
                        false,
                        "A married woman's pregnancy created an additional chance for the brief affair to be discovered.",
                        timelineId);
                occurrencePayload["sourceEventId"] = encounterId;
                occurrencePayload["exposureSeedKey"] = encounterId
                    + "|pregnancy_discovery";
                Dictionary<string, object> occurrence =
                    RegisterSocialOccurrence(connection, campaignId,
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
            ExecuteSql(connection, @"UPDATE relationship_fling_encounters SET
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
            ExecuteSql(connection, @"INSERT OR IGNORE INTO conception_attempts(
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
                + DeterministicSocialId(encounterId + "|conception");
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
            ExecuteSql(connection, @"INSERT OR IGNORE INTO conceptions(
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
                    OpenCampaignConnection(campaignId))
                {
                    EnsureRelationshipDirectorSchema(connection);
                    EnsureMbtiRelationshipSchema(connection);
                    EnsureSocialReputationSchema(connection);
                    EnsureWorldTestTelemetrySchema(connection);
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
                        ExecuteSql(connection, @"INSERT INTO relationship_personalities(
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
                    int rollCount = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline, ["day"] = day
                        }).FirstOrDefault(), "count", 0);
                    int encounterCount = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_encounters
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline, ["day"] = day
                        }).FirstOrDefault(), "count", 0);
                    Dictionary<string, object> sparkCount =
                        QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks
WHERE timeline_id=$timeline AND world_day=$day;",
                            new Dictionary<string, object>
                            {
                                ["timeline"] = timeline, ["day"] = day
                            }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    int lifecycleCount = ReadInt(QuerySql(connection,
                        "SELECT COUNT(*) AS count FROM relationship_pair_lifecycle;")
                        .FirstOrDefault(), "count", 0);
                    int chemistryCount = ReadInt(QuerySql(connection,
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
                        ReadInt(QuerySql(connection, @"
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
                    Dictionary<string, object> quietRoll = QuerySql(connection,
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
                        QuerySql(connection, @"
SELECT COUNT(*) AS attempts,SUM(passed) AS passes
FROM relationship_fling_sparks WHERE timeline_id=$timeline
AND world_day=$day;", new Dictionary<string, object>
                        {
                            ["timeline"] = timeline,
                            ["day"] = failedSparkDay
                        }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                    int nextDayRolls = ReadInt(QuerySql(connection, @"
SELECT COUNT(*) AS count FROM relationship_fling_rolls
WHERE timeline_id=$timeline AND world_day=$day;",
                        new Dictionary<string, object>
                        {
                            ["timeline"] = timeline,
                            ["day"] = failedSparkDay + 1
                        }).FirstOrDefault(), "count", 0);
                    int failedSparkEncounters = ReadInt(QuerySql(connection, @"
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
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return results;
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
