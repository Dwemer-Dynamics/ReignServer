using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Reign.Core.Contracts.Relationships;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private const int ClanConflictIncidentChance = 8;
        private const int ClanConflictAdditionalLordsChance = 80;
        private const int ClanConflictRelationBiasDivisor = 5;
        private const int ClanConflictRelationBiasCap = 20;

        private sealed class ClanConflictArchetype
        {
            public string Id;
            public string Category;
            public string Headline;
            public string Description;
            public string TraitA;
            public string TraitAHighSide;
            public string TraitB;
            public string TraitBHighSide;
            public string JudgmentHighSide;
        }

        private sealed class ClanConflictEffectSpec
        {
            public string EffectId;
            public string Kind;
            public string ObserverId;
            public string TargetId;
            public int Amount;
        }

        private static readonly List<ClanConflictArchetype> ClanConflictCatalog =
            BuildClanConflictCatalog();

        internal static void EnsureClanConflictSchema(ReignDbConnection connection)
        {
            RelationshipsSchema.CreateClanConflictDaySweeps(connection);
            RelationshipsSchema.EnsureColumnClanConflictDaySweepsEligibleKingdomIdsJson(connection);
            RelationshipsSchema.CreateClanConflictDailyRolls(connection);
            RelationshipsSchema.CreateClanConflictIncidents(connection);
            RelationshipsSchema.EnsureColumnClanConflictIncidentsInvolvedANamesJson(connection);
            RelationshipsSchema.EnsureColumnClanConflictIncidentsInvolvedBNamesJson(connection);
            RelationshipsSchema.CreateIndexClanConflictIncidentsTimeline(connection);
            RelationshipsSchema.CreateClanConflictRelationshipEffects(connection);
            RelationshipsSchema.CreateIndexClanConflictEffectsPending(connection);
        }

        internal static Dictionary<string, object> EvaluatePoliticalIncidentsCombined(
            Dictionary<string, object> payload)
        {
            Dictionary<string, object> pressure = DiplomacyService.EvaluatePoliticalPressure(payload);
            Dictionary<string, object> conflicts = EvaluateClanConflicts(payload);
            Dictionary<string, object> result = new Dictionary<string, object>(pressure,
                StringComparer.OrdinalIgnoreCase);
            result["ok"] = ReadBool(pressure, "ok", false) && ReadBool(conflicts, "ok", false);
            result["politicalPressure"] = pressure;
            result["clanConflicts"] = conflicts;
            if (!ReadBool(conflicts, "ok", false))
                result["error"] = FirstNonEmpty(ReadString(conflicts, "error", ""),
                    ReadString(pressure, "error", ""));
            return result;
        }

        private static Dictionary<string, object> EvaluateClanConflicts(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            Dictionary<string, object> startupGrace = DiplomacyService.BuildAutonomousWorldStartupGrace(payload);
            if (ReadBool(startupGrace, "locked", false))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["status"] = "startup_grace_period",
                    ["worldDay"] = worldDay, ["startupGrace"] = startupGrace
                };
            }

            int currentDay = (int)Math.Floor(worldDay + 0.000001d);
            int lastProcessedDay;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                lastProcessedDay = ReadInt(RelationshipsStore.SelectClanConflictDaySweeps2(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId
                    }).FirstOrDefault(), "latest_day", int.MinValue);
            }

            List<int> days = ClanConflictCatchUpDayKeys(lastProcessedDay, currentDay);
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            double fraction = Math.Max(0d, Math.Min(0.999999d, worldDay - currentDay));
            foreach (int day in days)
                results.Add(ProcessClanConflictDay(campaignId, timelineId,
                    day + fraction, payload));
            ResumePendingClanConflictEffects(campaignId, timelineId);
            WorldSimulationService.InvalidateWorldTestOverviewCache(campaignId, timelineId);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = days.Count > 1 ? "caught_up" : "processed",
                ["worldDay"] = worldDay,
                ["lastProcessedDayBefore"] = lastProcessedDay,
                ["processedDayCount"] = days.Count,
                ["processedDays"] = days.Cast<object>().ToList(),
                ["results"] = results.Cast<object>().ToList()
            };
        }

        private static List<int> ClanConflictCatchUpDayKeys(int lastProcessedDay, int currentDay)
        {
            if (currentDay < 0) return new List<int>();
            if (lastProcessedDay == int.MinValue) return new List<int> { currentDay };
            if (lastProcessedDay >= currentDay) return new List<int> { currentDay };
            return Enumerable.Range(lastProcessedDay + 1,
                currentDay - lastProcessedDay).ToList();
        }

        private static Dictionary<string, object> ProcessClanConflictDay(
            string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> world)
        {
            int day = (int)Math.Floor(worldDay + 0.000001d);
            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(world, "kingdoms")
                .Where(x => !ReadBool(x, "isPlayerKingdom", false))
                .Where(x => !ReadBool(x, "isRebelRealm", false))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "kingdomId", "")))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "leaderHeroId", "")))
                .OrderBy(x => ReadString(x, "kingdomId", ""), StringComparer.OrdinalIgnoreCase)
                .ToList();
            List<Dictionary<string, object>> clans = ReadDictionaryList(world, "clans");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                RelationshipsStore.UpsertClanConflictDaySweeps(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["worldDay"] = worldDay,
                        ["eligible"] = kingdoms.Count,
                        ["eligibleIds"] = Json.Serialize(kingdoms.Select(x =>
                            ReadString(x, "kingdomId", "")).ToList()),
                        ["ts"] = ts
                    });
            }

            int incidents = 0;
            foreach (Dictionary<string, object> kingdom in kingdoms)
            {
                Dictionary<string, object> result = ProcessClanConflictKingdom(
                    campaignId, timelineId, day, worldDay, kingdom, clans);
                if (!string.IsNullOrWhiteSpace(ReadString(result, "incidentId", ""))) incidents++;
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                RelationshipsStore.UpdateClanConflictDaySweeps(connection, new Dictionary<string, object>
                    {
                        ["processed"] = kingdoms.Count,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day
                    });
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["day"] = day,
                ["eligibleKingdoms"] = kingdoms.Count, ["incidents"] = incidents
            };
        }

        private static Dictionary<string, object> ProcessClanConflictKingdom(
            string campaignId, string timelineId, int day, double worldDay,
            Dictionary<string, object> kingdom, List<Dictionary<string, object>> allClans)
        {
            string kingdomId = ReadString(kingdom, "kingdomId", "");
            Dictionary<string, object> existing;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                existing = RelationshipsStore.SelectClanConflictDailyRolls3(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["kingdom"] = kingdomId
                    }).FirstOrDefault();
            }
            string rulingClanId = ReadString(kingdom, "rulingClanId", "");
            List<Dictionary<string, object>> eligibleClans = allClans
                .Where(x => ReadString(x, "kingdomId", "").Equals(kingdomId,
                    StringComparison.OrdinalIgnoreCase))
                .Where(x => !ReadBool(x, "isMercenary", false))
                .Where(x => !ReadBool(x, "isRebelClan", false))
                .Where(x => !ReadString(x, "clanId", "").Equals(rulingClanId,
                    StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "clanId", "")))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "leaderHeroId", "")))
                .Where(x => ReadBool(x, "leaderIsAlive", true)
                    && !ReadBool(x, "leaderIsChild", false))
                .OrderBy(x => ReadString(x, "clanId", ""), StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (existing != null)
            {
                string incidentId = ReadString(existing, "incident_id", "");
                bool resumeCreation = string.IsNullOrWhiteSpace(incidentId)
                    && ReadBool(existing, "passed", false)
                    && eligibleClans.Count >= 2;
                if (resumeCreation)
                {
                    incidentId = CreateClanConflictIncident(campaignId, timelineId,
                        day, ReadDouble(existing, "world_day", worldDay), kingdom, eligibleClans);
                    RecordClanConflictIncidentForRoll(campaignId, timelineId,
                        day, kingdomId, incidentId);
                }
                if (!string.IsNullOrWhiteSpace(incidentId))
                    CompleteClanConflictIncident(campaignId, timelineId, incidentId);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = resumeCreation ? "incident_resumed" : "already_processed",
                    ["kingdomId"] = kingdomId, ["incidentId"] = incidentId
                };
            }
            int roll = DiplomacyService.StableRulerD100(campaignId, timelineId, day,
                "clan-conflict:daily:" + kingdomId);
            bool passed = roll <= ClanConflictIncidentChance;
            string status = !passed ? "roll_failed"
                : eligibleClans.Count < 2 ? "no_eligible_pair" : "processing";
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                RelationshipsStore.InsertOrIgnoreClanConflictDailyRolls(connection, new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["worldDay"] = worldDay,
                        ["kingdom"] = kingdomId, ["kingdomName"] = ReadString(kingdom, "name", ""),
                        ["ruler"] = ReadString(kingdom, "leaderHeroId", ""),
                        ["chance"] = ClanConflictIncidentChance, ["roll"] = roll,
                        ["passed"] = passed ? 1 : 0, ["status"] = status,
                        ["eligible"] = eligibleClans.Count, ["ts"] = ts
                    });
            }
            if (!passed || eligibleClans.Count < 2)
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["status"] = status,
                    ["kingdomId"] = kingdomId, ["roll"] = roll,
                    ["chance"] = ClanConflictIncidentChance
                };

            string incident = CreateClanConflictIncident(campaignId, timelineId,
                day, worldDay, kingdom, eligibleClans);
            RecordClanConflictIncidentForRoll(campaignId, timelineId,
                day, kingdomId, incident);
            CompleteClanConflictIncident(campaignId, timelineId, incident);
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["status"] = "incident_created",
                ["kingdomId"] = kingdomId, ["incidentId"] = incident
            };
        }

        private static void RecordClanConflictIncidentForRoll(string campaignId,
            string timelineId, int day, string kingdomId, string incidentId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                RelationshipsStore.UpdateClanConflictDailyRolls(connection, new Dictionary<string, object>
                    {
                        ["incident"] = incidentId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["kingdom"] = kingdomId
                    });
            }
        }

        private static string CreateClanConflictIncident(string campaignId,
            string timelineId, int day, double worldDay,
            Dictionary<string, object> kingdom,
            List<Dictionary<string, object>> eligibleClans)
        {
            string kingdomId = ReadString(kingdom, "kingdomId", "");
            List<Tuple<Dictionary<string, object>, Dictionary<string, object>>> pairs =
                new List<Tuple<Dictionary<string, object>, Dictionary<string, object>>>();
            for (int i = 0; i < eligibleClans.Count; i++)
            for (int j = i + 1; j < eligibleClans.Count; j++)
                pairs.Add(Tuple.Create(eligibleClans[i], eligibleClans[j]));
            int pairIndex = StableClanConflictIndex(campaignId, timelineId, day,
                "pair:" + kingdomId, pairs.Count);
            Dictionary<string, object> clanA = pairs[pairIndex].Item1;
            Dictionary<string, object> clanB = pairs[pairIndex].Item2;
            if (DiplomacyService.StableRulerD100(campaignId, timelineId, day,
                "side-swap:" + kingdomId) <= 50)
            {
                Dictionary<string, object> swap = clanA; clanA = clanB; clanB = swap;
            }
            string incidentId = "clan-conflict:" + timelineId + ":" + day.ToString(
                CultureInfo.InvariantCulture) + ":" + kingdomId;
            bool addLords = DiplomacyService.StableRulerD100(campaignId, timelineId, day,
                incidentId + ":additional-lords") <= ClanConflictAdditionalLordsChance;
            List<string> involvedA = SelectClanConflictParticipants(campaignId,
                timelineId, day, incidentId, clanA, "a", addLords);
            List<string> involvedB = SelectClanConflictParticipants(campaignId,
                timelineId, day, incidentId, clanB, "b", addLords);
            List<string> involvedNamesA = ClanConflictParticipantNames(clanA, involvedA);
            List<string> involvedNamesB = ClanConflictParticipantNames(clanB, involvedB);
            ClanConflictArchetype archetype = ClanConflictCatalog[
                StableClanConflictIndex(campaignId, timelineId, day,
                    incidentId + ":archetype", ClanConflictCatalog.Count)];
            string rulerId = ReadString(kingdom, "leaderHeroId", "");
            double averageA;
            double averageB;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureMbtiRelationshipSchema(connection);
                averageA = ClanConflictAverageAffinity(connection, rulerId, involvedA);
                averageB = ClanConflictAverageAffinity(connection, rulerId, involvedB);
            }
            List<Dictionary<string, object>> votes = ResolveClanConflictVotes(
                campaignId, timelineId, day, incidentId, kingdom, archetype,
                averageA, averageB);
            string winningSide = votes.Count(x => ReadString(x, "vote", "") == "a") >= 2
                ? "a" : "b";
            int charm = ReadInt(ReadDictionary(kingdom, "rulerSkills")
                ?? new Dictionary<string, object>(), "charm", 0);
            int charmChanceBp = ClanConflictCharmChanceBasisPoints(charm);
            int charmRoll = StableClanConflictD10000(campaignId, timelineId,
                day, incidentId + ":charm");
            bool mediated = charmRoll <= charmChanceBp;
            string winnerName = winningSide == "a" ? ReadString(clanA, "name", "")
                : ReadString(clanB, "name", "");
            string narrative = archetype.Description + " "
                + ReadString(kingdom, "leaderName", "The ruler")
                + (mediated
                    ? " reconciled the petitioners after hearing both cases."
                    : " ruled for " + winnerName + ", and the dispute hardened into a feud.");
            List<ClanConflictEffectSpec> effects = BuildClanConflictEffects(
                campaignId, timelineId, day, incidentId, rulerId,
                involvedA, involvedB, winningSide, mediated);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                RelationshipsStore.InsertOrIgnoreClanConflictIncidents(connection, new Dictionary<string, object>
                    {
                        ["id"] = incidentId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = day, ["worldDay"] = worldDay, ["kingdom"] = kingdomId,
                        ["kingdomName"] = ReadString(kingdom, "name", ""), ["ruler"] = rulerId,
                        ["rulerName"] = ReadString(kingdom, "leaderName", ""),
                        ["clanA"] = ReadString(clanA, "clanId", ""), ["clanAName"] = ReadString(clanA, "name", ""),
                        ["clanB"] = ReadString(clanB, "clanId", ""), ["clanBName"] = ReadString(clanB, "name", ""),
                        ["leaderA"] = ReadString(clanA, "leaderHeroId", ""), ["leaderB"] = ReadString(clanB, "leaderHeroId", ""),
                        ["involvedA"] = Json.Serialize(involvedA), ["involvedB"] = Json.Serialize(involvedB),
                        ["involvedNamesA"] = Json.Serialize(involvedNamesA),
                        ["involvedNamesB"] = Json.Serialize(involvedNamesB),
                        ["archetype"] = archetype.Id, ["category"] = archetype.Category,
                        ["headline"] = archetype.Headline, ["narrative"] = narrative,
                        ["votes"] = Json.Serialize(votes), ["averageA"] = averageA, ["averageB"] = averageB,
                        ["winner"] = winningSide, ["charm"] = charm, ["chance"] = charmChanceBp,
                        ["roll"] = charmRoll, ["mediated"] = mediated ? 1 : 0,
                        ["effectCount"] = effects.Count,
                        ["payload"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["additionalLordsRolled"] = addLords,
                            ["relationBiasDivisor"] = ClanConflictRelationBiasDivisor,
                            ["relationBiasCap"] = ClanConflictRelationBiasCap,
                            ["catalogVersion"] = 1
                        }), ["ts"] = ts
                    });
                foreach (ClanConflictEffectSpec effect in effects)
                {
                    RelationshipsStore.InsertOrIgnoreClanConflictRelationshipEffects(connection, new Dictionary<string, object>
                    {
                        ["effect"] = effect.EffectId, ["incident"] = incidentId,
                        ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["day"] = worldDay, ["kind"] = effect.Kind,
                        ["observer"] = effect.ObserverId, ["target"] = effect.TargetId,
                        ["amount"] = effect.Amount, ["ts"] = ts
                    });
                }
            }
            return incidentId;
        }

        private static List<string> SelectClanConflictParticipants(string campaignId,
            string timelineId, int day, string incidentId,
            Dictionary<string, object> clan, string side, bool addLords)
        {
            string leader = ReadString(clan, "leaderHeroId", "");
            List<string> result = new List<string>();
            if (!string.IsNullOrWhiteSpace(leader)) result.Add(leader);
            if (!addLords) return result;
            int desired = StableClanConflictDie(campaignId, timelineId, day,
                incidentId + ":extra-count:" + side, 5);
            List<string> candidates = ReadDictionaryList(clan, "members")
                .Where(x => ReadBool(x, "isAlive", true))
                .Where(x => !ReadBool(x, "isChild", false))
                .Where(x => ReadBool(x, "isLord", true))
                .Select(x => ReadFirstString(x, "heroId", "heroStringId"))
                .Where(x => !string.IsNullOrWhiteSpace(x)
                    && !x.Equals(leader, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => StableClanConflictIndex(campaignId, timelineId, day,
                    incidentId + ":extra:" + side + ":" + x, int.MaxValue))
                .ThenBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Take(desired).ToList();
            result.AddRange(candidates);
            return result;
        }

        private static List<string> ClanConflictParticipantNames(
            Dictionary<string, object> clan, IEnumerable<string> participantIds)
        {
            Dictionary<string, string> names = ReadDictionaryList(clan, "members")
                .Select(x => new
                {
                    Id = ReadFirstString(x, "heroId", "heroStringId"),
                    Name = ReadString(x, "name", "")
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => FirstNonEmpty(x.First().Name, x.Key),
                    StringComparer.OrdinalIgnoreCase);
            string leaderId = ReadString(clan, "leaderHeroId", "");
            if (!string.IsNullOrWhiteSpace(leaderId))
                names[leaderId] = FirstNonEmpty(ReadString(clan, "leaderName", ""), leaderId);
            return (participantIds ?? Enumerable.Empty<string>())
                .Select(x => names.TryGetValue(x, out string name) ? name : x)
                .ToList();
        }

        private static List<Dictionary<string, object>> ResolveClanConflictVotes(
            string campaignId, string timelineId, int day, string incidentId,
            Dictionary<string, object> kingdom, ClanConflictArchetype archetype,
            double averageA, double averageB)
        {
            Dictionary<string, object> virtues = DiplomacyService.LoadDirectorTraits(campaignId, kingdom);
            List<Tuple<string, string, string>> routes = new List<Tuple<string, string, string>>
            {
                Tuple.Create(archetype.TraitA, archetype.TraitAHighSide, "The first case-specific virtue."),
                Tuple.Create(archetype.TraitB, archetype.TraitBHighSide, "The second case-specific virtue."),
                Tuple.Create("judgment", archetype.JudgmentHighSide, "Practical judgment of the competing claims.")
            };
            List<Dictionary<string, object>> votes = new List<Dictionary<string, object>>();
            foreach (Tuple<string, string, string> route in routes)
            {
                string trait = route.Item1;
                string highSide = route.Item2;
                double favored = highSide == "a" ? averageA : averageB;
                double other = highSide == "a" ? averageB : averageA;
                int modifier = ClanConflictRelationModifier(favored, other);
                int baseScore = Clamp((int)Math.Round(ReadDouble(virtues, trait, 50d),
                    MidpointRounding.AwayFromZero), 0, 100);
                int threshold = Clamp(baseScore + modifier, 0, 100);
                int roll = DiplomacyService.StableRulerD100(campaignId, timelineId, day,
                    incidentId + ":vote:" + trait);
                string vote = roll <= threshold ? highSide : highSide == "a" ? "b" : "a";
                votes.Add(new Dictionary<string, object>
                {
                    ["trait"] = trait, ["baseScore"] = baseScore,
                    ["highFavors"] = highSide, ["favoredAverageRelation"] = favored,
                    ["otherAverageRelation"] = other, ["relationModifier"] = modifier,
                    ["effectiveThreshold"] = threshold, ["roll"] = roll,
                    ["vote"] = vote, ["reason"] = route.Item3
                });
            }
            return votes;
        }

        private static int ClanConflictRelationModifier(double favoredAverage,
            double otherAverage)
        {
            return Clamp((int)Math.Round((favoredAverage - otherAverage)
                / ClanConflictRelationBiasDivisor, MidpointRounding.AwayFromZero),
                -ClanConflictRelationBiasCap, ClanConflictRelationBiasCap);
        }

        private static int ClanConflictCharmChanceBasisPoints(int charm)
        {
            int bounded = Clamp(charm, 50, 200);
            return Clamp(500 + (int)Math.Round((bounded - 50) * (2500d / 150d),
                MidpointRounding.AwayFromZero), 500, 3000);
        }

        private static List<ClanConflictEffectSpec> BuildClanConflictEffects(
            string campaignId, string timelineId, int day, string incidentId,
            string rulerId, List<string> involvedA, List<string> involvedB,
            string winningSide, bool mediated)
        {
            List<ClanConflictEffectSpec> effects = new List<ClanConflictEffectSpec>();
            if (mediated)
            {
                List<string> all = new[] { rulerId }.Concat(involvedA).Concat(involvedB)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
                for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                {
                    int amount = StableClanConflictDie(campaignId, timelineId, day,
                        incidentId + ":mediation:" + all[i] + ":" + all[j], 25);
                    AddClanConflictMutualEffects(effects, incidentId, "mediation_mutual",
                        all[i], all[j], amount);
                }
                return effects;
            }

            foreach (string a in involvedA)
            foreach (string b in involvedB)
            {
                int amount = -StableClanConflictDie(campaignId, timelineId, day,
                    incidentId + ":feud:" + a + ":" + b, 25);
                AddClanConflictMutualEffects(effects, incidentId, "feud_mutual",
                    a, b, amount);
            }
            List<string> winners = winningSide == "a" ? involvedA : involvedB;
            List<string> losers = winningSide == "a" ? involvedB : involvedA;
            foreach (string winner in winners)
                effects.Add(ClanConflictEffect(incidentId, "winner_to_ruler", winner,
                    rulerId, StableClanConflictDie(campaignId, timelineId, day,
                        incidentId + ":winner-ruler:" + winner, 15)));
            foreach (string loser in losers)
                effects.Add(ClanConflictEffect(incidentId, "loser_to_ruler", loser,
                    rulerId, -StableClanConflictDie(campaignId, timelineId, day,
                        incidentId + ":loser-ruler:" + loser, 15)));
            return effects;
        }

        private static void AddClanConflictMutualEffects(List<ClanConflictEffectSpec> effects,
            string incidentId, string kind, string first, string second, int amount)
        {
            effects.Add(ClanConflictEffect(incidentId, kind, first, second, amount));
            effects.Add(ClanConflictEffect(incidentId, kind, second, first, amount));
        }

        private static ClanConflictEffectSpec ClanConflictEffect(string incidentId,
            string kind, string observer, string target, int amount)
        {
            return new ClanConflictEffectSpec
            {
                EffectId = incidentId + ":effect:" + kind + ":" + observer + ":" + target,
                Kind = kind, ObserverId = observer, TargetId = target, Amount = amount
            };
        }

        private static void ResumePendingClanConflictEffects(string campaignId,
            string timelineId)
        {
            List<string> incidentIds;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                incidentIds = RelationshipsStore.SelectClanConflictRelationshipEffects3(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                }).Select(x => ReadString(x, "incident_id", ""))
                  .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            }
            foreach (string incidentId in incidentIds)
                CompleteClanConflictIncident(campaignId, timelineId, incidentId);
        }

        private static void CompleteClanConflictIncident(string campaignId,
            string timelineId, string incidentId)
        {
            Dictionary<string, object> incident;
            List<Dictionary<string, object>> effects;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                incident = RelationshipsStore.SelectClanConflictIncidents3(connection, new Dictionary<string, object>
                {
                    ["incident"] = incidentId
                }).FirstOrDefault();
                if (incident == null) return;
                effects = RelationshipsStore.SelectClanConflictRelationshipEffects4(connection, new Dictionary<string, object> { ["incident"] = incidentId });
            }
            string kingdomId = ReadString(incident, "kingdom_id", "");
            double worldDay = ReadDouble(incident, "world_day", 0d);
            foreach (Dictionary<string, object> effect in effects)
            {
                string effectId = ReadString(effect, "effect_id", "");
                string observer = ReadString(effect, "observer_id", "");
                string target = ReadString(effect, "target_id", "");
                int amount = ReadInt(effect, "amount", 0);
                int before = 0;
                int after = 0;
                try
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        EnsureClanConflictSchema(connection);
                        before = ReadClanConflictAffinity(connection, observer, target);
                        RelationshipsStore.UpdateClanConflictRelationshipEffects(connection, new Dictionary<string, object>
                            {
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["effect"] = effectId
                            });
                    }
                    DiplomacyService.ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay,
                        "clan_conflict_" + ReadString(effect, "effect_kind", "relationship"),
                        effectId, incidentId, observer, target, kingdomId, kingdomId,
                        amount, "", 0, false, incidentId,
                        new Dictionary<string, object>
                        {
                            ["incidentId"] = incidentId,
                            ["effectId"] = effectId,
                            ["effectKind"] = ReadString(effect, "effect_kind", ""),
                            ["amount"] = amount
                        });
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        after = ReadClanConflictAffinity(connection, observer, target);
                        RelationshipsStore.UpdateClanConflictRelationshipEffects2(connection, new Dictionary<string, object>
                            {
                                ["before"] = before, ["after"] = after,
                                ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["effect"] = effectId
                            });
                    }
                }
                catch (Exception ex)
                {
                    using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                    {
                        RelationshipsStore.UpdateClanConflictRelationshipEffects3(connection, new Dictionary<string, object>
                            {
                                ["error"] = ex.Message, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                                ["effect"] = effectId
                            });
                    }
                }
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                Dictionary<string, object> counts = RelationshipsStore.SelectClanConflictRelationshipEffects5(connection, new Dictionary<string, object> { ["incident"] = incidentId }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
                int failed = ReadInt(counts, "failed", 0);
                int pending = ReadInt(counts, "pending", 0);
                string status = failed > 0 ? "failed" : pending > 0 ? "effects_pending" : "completed";
                string notice = status == "completed" ? "ready" : "pending";
                RelationshipsStore.UpdateClanConflictIncidents(connection, new Dictionary<string, object>
                {
                    ["status"] = status, ["notice"] = notice, ["failed"] = failed,
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["incident"] = incidentId
                });
            }
        }

        private static double ClanConflictAverageAffinity(ReignDbConnection connection,
            string rulerId, List<string> involved)
        {
            if (involved == null || involved.Count == 0) return 0d;
            return involved.Average(x => ReadClanConflictAffinity(connection, rulerId, x));
        }

        private static int ReadClanConflictAffinity(ReignDbConnection connection,
            string observerId, string targetId)
        {
            string pairKey = AmbientPairKey(observerId, targetId);
            Dictionary<string, object> row = RelationshipsStore.SelectRelationshipPairChemistry7(connection, new Dictionary<string, object>
            {
                ["pair"] = pairKey
            }).FirstOrDefault();
            if (row == null) return 0;
            return ReadString(row, "hero_a_id", "").Equals(observerId,
                StringComparison.OrdinalIgnoreCase)
                ? ReadInt(row, "affinity_a_to_b", 0)
                : ReadInt(row, "affinity_b_to_a", 0);
        }

        internal static Dictionary<string, object> NextClanConflictNotices(
            Dictionary<string, string> query)
        {
            ClanConflictNoticesRequest request = ClanConflictNoticesRequest.FromQuery(query);
            if (query?.ContainsKey("sharedActivityGeneration") == true)
                return WorldSimulationService.ReadOwnedNoticeSnapshot(query, (connection, transaction, campaign, timeline) =>
                    ReadClanConflictNotices(connection, campaign, timeline, request.Limit, transaction));
            string campaignId = request.CampaignId;
            string timelineId = request.TimelineId;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureClanConflictSchema(connection);
                return ReadClanConflictNotices(connection, campaignId, timelineId, request.Limit);
            }
        }

        private static Dictionary<string, object> ReadClanConflictNotices(ReignDbConnection connection,
            string campaignId, string timelineId, int limit, ReignDbTransaction ownedTransaction = null)
        {
            bool owned = ownedTransaction != null;
            var scope = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId };
            var notices = owned && !WorldSimulationService.TableExists(connection, "clan_conflict_incidents")
                ? new List<Dictionary<string, object>>()
                : RelationshipsStore.SelectClanConflictIncidents4(connection, limit.ToString(CultureInfo.InvariantCulture), scope, ownedTransaction);
            foreach (Dictionary<string, object> notice in notices)
            {
                notice["news_description"] = ClanConflictCatalog.FirstOrDefault(a => a.Id == ReadString(notice, "archetype_id", ""))?.Description ?? "";
                string id = ReadString(notice, "incident_id", "");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (owned)
                {
                    var ownedNotice = new Dictionary<string, object>(scope) { ["incident"] = id, ["ts"] = ts };
                    RelationshipsStore.MarkOwnedClanConflictNoticeFetched(connection, ownedNotice, ownedTransaction);
                }
                else
                    RelationshipsStore.UpdateClanConflictIncidents2(connection, new Dictionary<string, object>
                        {
                            ["ts"] = ts, ["incident"] = id
                        });
            }
            return new ClanConflictNoticesResponse(notices).ToDictionary();
        }

        internal static Dictionary<string, object> AcknowledgeClanConflictNotice(
            Dictionary<string, object> payload)
        {
            ClanConflictAcknowledgeRequest request = ClanConflictAcknowledgeRequest.FromDictionary(payload);
            string campaignId = request.CampaignId;
            string timelineId = request.TimelineId;
            string incidentId = request.IncidentId;
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(incidentId))
                return ClanConflictAcknowledgeResponse.NotFound().ToDictionary();
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaignId);
            using (ReignDbConnection connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(
                campaignId, trackCampaignMutations: true))
            {
                if (connection == null) return ClanConflictAcknowledgeResponse.NotFound().ToDictionary();
                using var transaction = connection.BeginTransaction();
                AcquirePostgreSqlTransactionMutationLock(connection, transaction);
                if (!WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, transaction, payload, campaignId, timelineId)
                    || !WorldSimulationService.TableExists(connection, "clan_conflict_incidents")
                    || !RelationshipsStore.AcknowledgeClanConflictIncident(connection, new Dictionary<string, object>
                    {
                        ["incident"] = incidentId, ["campaign"] = campaignId,
                        ["timeline"] = timelineId, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    }, transaction))
                    return ClanConflictAcknowledgeResponse.NotFound().ToDictionary();
                transaction.Commit();
            }
            WorldSimulationService.InvalidateWorldTestOverviewCache(campaignId, timelineId);
            return ClanConflictAcknowledgeResponse.Acknowledged(incidentId).ToDictionary();
        }

        internal static List<string> ReadJsonStringList(string json)
        {
            Dictionary<string, object> wrapper = TryParseJsonObject("{\"items\":" + (json ?? "[]") + "}")
                ?? new Dictionary<string, object>();
            return ReadStringList(wrapper, "items");
        }

        private static int StableClanConflictIndex(string campaignId, string timelineId,
            int day, string subject, int count)
        {
            if (count <= 1) return 0;
            uint value = (uint)DiplomacyService.StableDirectorSeed(campaignId + "|" + timelineId,
                day, subject ?? "");
            return (int)(value % (uint)count);
        }

        private static int StableClanConflictD10000(string campaignId,
            string timelineId, int day, string subject)
        {
            uint value = (uint)DiplomacyService.StableDirectorSeed(campaignId + "|" + timelineId,
                day, subject ?? "");
            return 1 + (int)(value % 10000u);
        }

        private static int StableClanConflictDie(string campaignId, string timelineId,
            int day, string subject, int sides)
        {
            return 1 + StableClanConflictIndex(campaignId, timelineId, day,
                subject, Math.Max(1, sides));
        }

        private static List<ClanConflictArchetype> BuildClanConflictCatalog()
        {
            string[] definitions =
            {
                "land|Boundary Stones|The clans dispute where an old estate boundary truly lies.|responsibility|a|honor|b|a",
                "land|Pasture Rights|Both houses claim seasonal grazing rights over the same uplands.|responsibility|b|compassion|a|b",
                "land|Village Stewardship|The clans contest which household should protect a vulnerable village.|loyalty|a|responsibility|b|a",
                "inheritance|Disputed Inheritance|A dead noble's property has produced competing family claims.|honor|a|compassion|b|a",
                "inheritance|Ward's Estate|Both clans claim lawful stewardship over a young heir's lands.|responsibility|a|honor|b|b",
                "inheritance|Dowry Lands|An old marriage settlement has become a dispute over valuable holdings.|loyalty|b|honor|a|a",
                "debts|Unpaid War Debt|One clan demands repayment for soldiers and supplies committed in war.|responsibility|a|loyalty|b|a",
                "debts|Ransom Obligation|The houses disagree over who must bear the cost of a noble ransom.|compassion|a|responsibility|b|b",
                "debts|Broken Guarantee|A pledged guarantee failed and both clans demand compensation.|honor|a|responsibility|b|a",
                "raids|Raid Blame|Each clan blames the other's retainers for an unlawful raid.|honor|b|loyalty|a|b",
                "raids|Retaliatory Seizure|A retaliatory seizure has expanded a private quarrel.|courage|a|boldness|b|a",
                "raids|Sheltered Outlaws|One house accuses the other of sheltering raiders and fugitives.|loyalty|a|honor|b|b",
                "command|Battlefield Command|Two commanders dispute responsibility for a costly battlefield order.|courage|a|responsibility|b|a",
                "command|Garrison Authority|Both clans claim the right to direct a strategic garrison.|loyalty|b|responsibility|a|b",
                "command|Army Precedence|The houses contest which leader should hold senior command.|boldness|a|honor|b|a",
                "trade|Caravan Losses|A caravan venture ended in loss and the noble investors trade accusations.|responsibility|a|compassion|b|b",
                "trade|Market Toll|Both clans claim authority over a profitable market toll.|honor|b|responsibility|a|a",
                "trade|Workshop Charter|Competing charters grant both houses the same commercial privilege.|responsibility|b|honor|a|b",
                "marriage|Broken Betrothal|A failed betrothal has become a public dispute over honor and promises.|honor|a|compassion|b|a",
                "marriage|Guardianship Claim|The clans contest guardianship of a politically important ward.|loyalty|a|responsibility|b|b",
                "marriage|Marriage Portion|Both houses interpret an old marriage portion in their own favor.|compassion|b|honor|a|a",
                "prisoners|Prisoner Treatment|One clan condemns the other's treatment of captured kin.|compassion|a|honor|b|a",
                "prisoners|Exchange Priority|The clans demand priority for different prisoners in an exchange.|loyalty|a|compassion|b|b",
                "prisoners|Escaped Captive|An escaped captive has prompted accusations of negligence and collusion.|responsibility|b|honor|a|a",
                "honors|Royal Favor|The houses contest which service deserves public royal recognition.|loyalty|a|honor|b|b",
                "honors|Ceremonial Rank|A dispute over ceremonial precedence has insulted both households.|honor|a|boldness|b|a",
                "honors|Victory Credit|Each clan claims decisive credit for the same victory.|courage|b|loyalty|a|b",
                "resources|Forest Rights|Both clans claim timber and hunting rights in the same forest.|responsibility|a|compassion|b|a",
                "resources|Water Access|A contested water source threatens the estates of both houses.|compassion|a|responsibility|b|b",
                "resources|Mine Revenue|The clans dispute their shares of revenue from a productive mine.|honor|b|responsibility|a|a",
                "precedence|Seat at Court|A quarrel over court precedence has become a test of royal authority.|boldness|a|honor|b|b",
                "precedence|Public Accusation|One clan has publicly accused the other of disloyal conduct.|loyalty|a|honor|b|a"
            };
            string[] variants =
            {
                "The two clan leaders bring the matter before the ruler in formal court.",
                "Senior relatives and sworn retainers press both versions of the case.",
                "The quarrel has spread from private correspondence into the royal hall.",
                "Both households demand a binding judgment before the dispute worsens."
            };
            List<ClanConflictArchetype> result = new List<ClanConflictArchetype>();
            int index = 0;
            foreach (string definition in definitions)
            {
                string[] parts = definition.Split('|');
                for (int variant = 0; variant < variants.Length; variant++)
                {
                    result.Add(new ClanConflictArchetype
                    {
                        Id = "clan_conflict_" + index.ToString("00", CultureInfo.InvariantCulture)
                            + "_" + (variant + 1).ToString(CultureInfo.InvariantCulture),
                        Category = parts[0], Headline = parts[1],
                        Description = parts[2] + " " + variants[variant],
                        TraitA = parts[3], TraitAHighSide = parts[4],
                        TraitB = parts[5], TraitBHighSide = parts[6],
                        JudgmentHighSide = parts[7]
                    });
                }
                index++;
            }
            return result;
        }
    }
}
