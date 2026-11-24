using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Reign.Core.Contracts.Diplomacy;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private const int PoliticalPressureIncidentChance = 43;
        private static readonly string[] PoliticalPressureVirtues =
        {
            "compassion", "boldness", "honor", "loyalty", "responsibility", "courage", "judgment"
        };

        private sealed class PoliticalPressureArchetype
        {
            public string Id;
            public string Polarity;
            public string Channel;
            public string Headline;
            public string Description;
        }

        private static readonly List<PoliticalPressureArchetype> PoliticalPressureCatalog =
            BuildPoliticalPressureCatalog();

        internal static void EnsurePoliticalPressureSchema(ReignDbConnection connection)
        {
            DiplomacySchema.CreatePoliticalPressureDeltaReceipts(connection);
            DiplomacySchema.CreatePoliticalPressureDailyRolls(connection);
            DiplomacySchema.CreatePoliticalPressureState(connection);
            DiplomacySchema.CreatePoliticalPressureIncidents(connection);
            DiplomacySchema.CreateIndexPressureIncidentsPair(connection);
            DiplomacySchema.CreatePoliticalPressureNativeEffects(connection);
            DiplomacySchema.CreateIndexPressureEffectsPending(connection);
            DiplomacySchema.CreatePoliticalPressureActionRolls(connection);
            DiplomacySchema.CreatePoliticalPressureActivity(connection);
            DiplomacySchema.CreateIndexPressureActivityTimeline(connection);
        }

        internal static Dictionary<string, object> EvaluatePoliticalPressure(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = payload.TryGetValue("worldDay", out object suppliedDay) ? suppliedDay switch
            {
                int day => day,
                long day => day,
                decimal day => (double)day,
                double day => day,
                _ => double.NaN
            } : double.NaN;
            if (!double.IsFinite(worldDay) || worldDay < 0d || worldDay >= int.MaxValue - 1d)
                return PressureDayDeferred("invalid_day", -1);
            Dictionary<string, object> startupGrace =
                BuildAutonomousWorldStartupGrace(payload);
            if (ReadBool(startupGrace, "locked", false))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "startup_grace_period",
                    ["worldDay"] = worldDay,
                    ["startupGrace"] = startupGrace
                };
            }

            int currentDay = (int)Math.Floor(worldDay);

            int lastProcessedDay;
            int firstUnheld;
            IReadOnlyList<int> unfinished;
            using (var registered = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaignId))
            {
                if (registered == null) return PressureDayDeferred("scope_changed", currentDay);
            }
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                EnsureKingdomLeaderDiplomacySchema(connection);
#if !REIGN_EXCLUDE_COURT
                CourtService.EnsureInternationalCourtLifeSchema(connection);
#endif
                var key = TestDict("campaign", campaignId, "timeline", timelineId, "day", currentDay);
                var witness = ReadPressureStorageWitness(connection, null, campaignId, timelineId);
                if (!PressureWitnessMatchesWire(witness, payload))
                    return PressureDayDeferred("scope_changed", currentDay);
                lastProcessedDay = DiplomacyStore.ReadLastTerminalPoliticalPressureDay(connection, key);
                key["storage"] = witness.StorageId;
                key["generation"] = witness.RestoreGeneration;
                unfinished = DiplomacyStore.FindUnfinishedPoliticalPressureDays(connection, null, key);
                key["start"] = lastProcessedDay == int.MinValue
                    ? unfinished.Count > 0 ? unfinished[0] : currentDay
                    : Math.Min(currentDay, lastProcessedDay + 1);
                firstUnheld = DiplomacyStore.FindFirstUnheldPoliticalPressureDay(connection, key);
            }

            var days = unfinished.Concat(PoliticalPressureCatchUpDayKeys(firstUnheld - 1, currentDay))
                .Distinct().OrderBy(day => day).Take(32).ToList();

            var processed = new List<object>();
            var results = new List<object>();
            var heldDays = new List<object>();
            double fraction = Math.Clamp(worldDay - currentDay, 0d, 0.999999d);
            string status = "processed";
            bool ok = true;
            foreach (int day in days)
            {
                var result = ProcessDailyPoliticalPressure(campaignId, timelineId, day, day + fraction, payload);
                results.Add(result);
                if (!ReadBool(result, "ok", false))
                {
                    if (ReadBool(result, "recoveryHeld", false))
                    {
                        heldDays.Add(day);
                        if (day < currentDay) continue;
                    }
                    status = ReadString(result, "status", "recovery_required");
                    ok = false;
                    break;
                }
                processed.Add(day);
            }
            if (ok && !processed.Contains(currentDay))
            {
                status = "catch_up_pending";
                ok = false;
            }
            else if (ok && processed.Count > 1) status = "caught_up";

            return TestDict("ok", ok, "status", status, "worldDay", worldDay,
                "lastProcessedDayBefore", lastProcessedDay, "processedDayCount", processed.Count,
                "processedDays", processed, "heldDays", heldDays, "results", results);
        }

        private static List<int> PoliticalPressureCatchUpDayKeys(
            int lastProcessedDay, int currentDay)
        {
            if (currentDay < 0) return new List<int>();
            if (lastProcessedDay == int.MinValue)
                return new List<int> { currentDay };
            if (lastProcessedDay >= currentDay)
                return new List<int> { currentDay };
            return Enumerable.Range(lastProcessedDay + 1,
                Math.Min(32, currentDay - lastProcessedDay)).ToList();
        }

        private static Dictionary<string, object> ProcessDailyPoliticalPressure(string campaign, string timeline,
            int day, double worldDay, Dictionary<string, object> request)
        {
            int roll = StableRulerD100(campaign, timeline, day, "political-pressure:daily");
            var disposition = ClaimPressureDay(campaign, timeline, day, worldDay, roll, request, out var claim);
            if (disposition != null) return disposition;

            if (claim.PreparedJson.Length == 0)
            {
                var original = TryParseJsonObject(claim.InputJson);
                if (original == null) return PressureDayDeferred("recovery_required", day);
                var prepared = PreparePressureDay(claim, original);
                claim = SealPressureDay(claim, prepared);
                if (claim == null) return PressureDayDeferred("processing", day);
            }
            var plan = TryParseJsonObject(claim.PreparedJson);
            return IsOwnedPressurePlan(claim, plan)
                ? PublishPressureDay(claim, plan) : PressureDayDeferred("recovery_required", day);
        }

        private static Dictionary<string, object> PreparePressureDay(PressureDayClaim claim, Dictionary<string, object> world)
        {
            string campaignId = claim.Campaign, timelineId = claim.Timeline;
            int day = claim.Day, dailyRoll = claim.Roll;
            double worldDay = claim.WorldDay;

            Dictionary<string, object> WithoutIncident(string status, bool passed, string originKingdom)
            {
                var plan = PressurePlan(claim, status);
                plan["passed"] = passed;
                plan["originKingdom"] = originKingdom;
                return plan;
            }

            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(world, "kingdoms")
                .Where(x => !ReadBool(x, "isRebelRealm", false))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "kingdomId", "")))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "leaderHeroId", "")))
                .ToList();
            if (kingdoms.Count < 2 || dailyRoll > PoliticalPressureIncidentChance)
            {
                string status = kingdoms.Count < 2 ? "no_eligible_kingdoms" : "roll_failed";
                return WithoutIncident(status, false, "");
            }

            Dictionary<string, object> origin = SelectPoliticalPressureOrigin(campaignId, timelineId, day, kingdoms);
            int warCount = ReadStringList(origin, "enemies").Count;
            int hostileChance = PoliticalPressureHostileChance(warCount);
            int polarityRoll = StableRulerD100(campaignId, timelineId, day, "political-pressure:polarity:" + ReadString(origin, "kingdomId", ""));
            string polarity = polarityRoll <= hostileChance ? "hostile" : "peaceful";
            Dictionary<string, object> target = SelectPoliticalPressureTarget(campaignId, timelineId, day, origin, kingdoms, polarity);
            if (target == null)
            {
                return WithoutIncident("no_eligible_target", true, ReadString(origin, "kingdomId", ""));
            }

            string channel = SelectPoliticalPressureChannel(campaignId, timelineId, day, origin, target, polarity);
            PoliticalPressureArchetype archetype = SelectPoliticalPressureArchetype(
                campaignId, timelineId, day, origin, target, polarity, channel);
            if (archetype == null)
            {
                return WithoutIncident("catalog_cooldown", true, ReadString(origin, "kingdomId", ""));
            }

            int severityRank = SelectPoliticalPressureSeverity(campaignId, timelineId, day,
                ReadString(origin, "kingdomId", "") + ":" + ReadString(target, "kingdomId", ""));
            string severity = PoliticalPressureSeverityName(severityRank);
            int pressureAmount = PoliticalPressureAmount(severityRank);
            int relationshipPenalty = PoliticalPressureRelationshipPenalty(severityRank);
            List<Dictionary<string, object>> clans = ReadDictionaryList(world, "clans");
            List<Dictionary<string, object>> settlements = ReadDictionaryList(world, "settlements");
            List<Dictionary<string, object>> originClans = SelectPoliticalPressureClans(
                campaignId, timelineId, day, clans, ReadString(origin, "kingdomId", ""), ReadString(origin, "leaderHeroId", ""), severityRank, "origin");
            List<Dictionary<string, object>> targetClans = SelectPoliticalPressureClans(
                campaignId, timelineId, day, clans, ReadString(target, "kingdomId", ""), ReadString(target, "leaderHeroId", ""), severityRank, "target");
            if (originClans.Count == 0 || targetClans.Count == 0)
            {
                return WithoutIncident("no_eligible_petitioners", true, ReadString(origin, "kingdomId", ""));
            }
            List<string> originLords = PoliticalPressureLeadLords(originClans);
            List<string> targetLords = PoliticalPressureLeadLords(targetClans);
            string incidentId = "political-pressure:" + timelineId + ":" + day.ToString(CultureInfo.InvariantCulture);
            string correlation = incidentId;

            Dictionary<string, object> routes = SelectPoliticalPressureTraitRoutes(campaignId,
                incidentId, archetype, severity, origin, target, originLords, targetLords,
                out string selectorStatus);
            List<Dictionary<string, object>> traitRolls = new List<Dictionary<string, object>>();
            string originStance = ReadBool(origin, "isPlayerKingdom", false) ? "awaiting_player" : ResolvePoliticalPressureStance(campaignId, timelineId, day,
                incidentId, "origin", origin, ReadDictionary(routes, "origin"), traitRolls);
            string targetStance = ReadBool(target, "isPlayerKingdom", false) ? "awaiting_player" : ResolvePoliticalPressureStance(campaignId, timelineId, day,
                incidentId, "target", target, ReadDictionary(routes, "target"), traitRolls);

            string narrative = GeneratePoliticalPressureNarrative(campaignId, incidentId,
                archetype, severity, origin, target, originLords, targetLords,
                originStance, targetStance, out string narrationStatus);
            var prepared = PressurePlan(claim, "incident_created");
            foreach (var term in TestDict("passed", true, "incident", incidentId,
                "origin", origin, "target", target, "originClans", originClans, "targetClans", targetClans,
                "originLords", originLords, "targetLords", targetLords, "settlements", settlements,
                "archetype", archetype.Id, "headline", archetype.Headline, "narrative", narrative,
                "polarity", polarity, "channel", channel, "severity", severity, "severityRank", severityRank,
                "pressureAmount", pressureAmount, "relationshipPenalty", relationshipPenalty,
                "originWars", warCount, "hostileChance", hostileChance, "polarityRoll", polarityRoll,
                "routes", routes, "rolls", traitRolls, "selector", selectorStatus, "narration", narrationStatus,
                "originStance", originStance, "targetStance", targetStance))
                prepared[term.Key] = term.Value;
#if !REIGN_EXCLUDE_COURT
            if (originStance == "awaiting_player" || targetStance == "awaiting_player")
                prepared["court"] = CourtService.BuildInternationalPoliticalIncidentPayload(incidentId, worldDay,
                    origin, target, originLords, targetLords, severityRank, archetype.Headline,
                    archetype.Description, channel, originStance, targetStance, polarity);
#endif
            return prepared;
        }

        private static bool TryClaimPoliticalPressureDailyRoll(string campaignId,
            string timelineId, int day, double worldDay, int roll)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return DiplomacyStore.UpsertPoliticalPressureDailyRolls2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["day"] = day, ["worldDay"] = worldDay,
                    ["chance"] = PoliticalPressureIncidentChance, ["roll"] = roll,
                    ["ts"] = ts, ["stale"] = ts - 300
                }).Count == 1;
            }
        }

        private static Dictionary<string, object> SelectPoliticalPressureOrigin(string campaignId,
            string timelineId, int day, List<Dictionary<string, object>> kingdoms)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return kingdoms.OrderByDescending(kingdom =>
                {
                    string id = ReadString(kingdom, "kingdomId", "");
                    double last = ReadDouble(DiplomacyStore.SelectPoliticalPressureIncidents(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["kingdom"] = id })
                        .FirstOrDefault(), "day", -1000d);
                    return (day - last) * 1000d + StableRulerD100(campaignId, timelineId, day, "pressure-origin:" + id);
                }).ThenBy(x => ReadString(x, "kingdomId", ""), StringComparer.OrdinalIgnoreCase).First();
            }
        }

        private static Dictionary<string, object> SelectPoliticalPressureTarget(string campaignId,
            string timelineId, int day, Dictionary<string, object> origin,
            List<Dictionary<string, object>> kingdoms, string polarity)
        {
            string originId = ReadString(origin, "kingdomId", "");
            HashSet<string> enemies = new HashSet<string>(ReadStringList(origin, "enemies"), StringComparer.OrdinalIgnoreCase);
            int warCount = enemies.Count;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                List<Dictionary<string, object>> eligible = kingdoms.Where(x =>
                    !ReadString(x, "kingdomId", "").Equals(originId, StringComparison.OrdinalIgnoreCase)).Where(x =>
                {
                    string targetId = ReadString(x, "kingdomId", "");
                    double last = ReadDouble(DiplomacyStore.SelectPoliticalPressureIncidents2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                            ["origin"] = originId, ["target"] = targetId }).FirstOrDefault(), "day", -1000d);
                    return day - last >= 2d;
                }).ToList();
                if (eligible.Count == 0) return null;
                if (warCount >= 2 && polarity == "hostile")
                {
                    List<Dictionary<string, object>> existingEnemies = eligible.Where(x => enemies.Contains(ReadString(x, "kingdomId", ""))).ToList();
                    if (existingEnemies.Count > 0) eligible = existingEnemies;
                }
                return eligible.OrderByDescending(target =>
                {
                    string targetId = ReadString(target, "kingdomId", "");
                    int weight = 100;
                    if (enemies.Contains(targetId)) weight += polarity == "peaceful" ? (warCount >= 2 ? 500 : 250) : 250;
                    if (PoliticalPressurePairSharesBorder(originId, targetId, origin, target)) weight += 150;
                    int current = ReadPoliticalPressureValue(connection, campaignId, timelineId, originId, targetId);
                    if ((polarity == "hostile" && current > 0) || (polarity == "peaceful" && current < 0)) weight += Math.Abs(current) * 2;
                    return weight * 1000 + StableRulerD100(campaignId, timelineId, day, "pressure-target:" + originId + ":" + targetId + ":" + polarity);
                }).ThenBy(x => ReadString(x, "kingdomId", ""), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
        }

        private static bool PoliticalPressurePairSharesBorder(string originId, string targetId,
            Dictionary<string, object> origin, Dictionary<string, object> target)
        {
            // The compact ruler rows do not carry pair facts. Border ownership is
            // still represented in the incident catalog selection through the
            // client relation rows; this fallback keeps target selection stable.
            return ReadStringList(origin, "borderKingdomIds").Contains(targetId, StringComparer.OrdinalIgnoreCase)
                || ReadStringList(target, "borderKingdomIds").Contains(originId, StringComparer.OrdinalIgnoreCase);
        }

        private static int PoliticalPressureHostileChance(int warCount)
        {
            if (warCount <= 0) return 65;
            if (warCount == 1) return 50;
            if (warCount == 2) return 35;
            return 20;
        }

        private static string SelectPoliticalPressureChannel(string campaignId, string timelineId,
            int day, Dictionary<string, object> origin, Dictionary<string, object> target, string polarity)
        {
            bool atWar = ReadStringList(origin, "enemies").Contains(ReadString(target, "kingdomId", ""), StringComparer.OrdinalIgnoreCase);
            string[] channels = polarity == "hostile"
                ? (atWar ? new[] { "punitive", "war", "coercion" } : new[] { "war", "coercion", "treaty_strain" })
                : (atWar ? new[] { "peace", "peace", "aid_exchange" } : new[] { "trade", "security", "aid_exchange" });
            int index = (StableRulerD100(campaignId, timelineId, day,
                "pressure-channel:" + ReadString(origin, "kingdomId", "") + ":" + ReadString(target, "kingdomId", "")) - 1) % channels.Length;
            return channels[index];
        }

        private static PoliticalPressureArchetype SelectPoliticalPressureArchetype(string campaignId,
            string timelineId, int day, Dictionary<string, object> origin, Dictionary<string, object> target,
            string polarity, string channel)
        {
            string originId = ReadString(origin, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                HashSet<string> recent = new HashSet<string>(DiplomacyStore.SelectPoliticalPressureIncidents3(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["origin"] = originId, ["target"] = targetId, ["cutoff"] = day - 14d })
                    .Select(x => ReadString(x, "archetype_id", "")), StringComparer.OrdinalIgnoreCase);
                List<PoliticalPressureArchetype> rows = PoliticalPressureCatalog.Where(x =>
                    x.Polarity == polarity && x.Channel == channel && !recent.Contains(x.Id)).ToList();
                if (rows.Count == 0) rows = PoliticalPressureCatalog.Where(x => x.Polarity == polarity && !recent.Contains(x.Id)).ToList();
                if (rows.Count == 0) return null;
                int index = (StableRulerD100(campaignId, timelineId, day,
                    "pressure-archetype:" + originId + ":" + targetId + ":" + polarity + ":" + channel) - 1) % rows.Count;
                return rows.OrderBy(x => x.Id, StringComparer.OrdinalIgnoreCase).ElementAt(index);
            }
        }

        private static int SelectPoliticalPressureSeverity(string campaignId, string timelineId, int day, string key)
        {
            int roll = StableRulerD100(campaignId, timelineId, day, "pressure-severity:" + key);
            if (roll <= 40) return 1;
            if (roll <= 75) return 2;
            if (roll <= 95) return 3;
            return 4;
        }

        private static string PoliticalPressureSeverityName(int rank)
        {
            return rank <= 1 ? "minor" : rank == 2 ? "moderate" : rank == 3 ? "major" : "crisis";
        }

        private static int PoliticalPressureAmount(int rank)
        {
            return rank <= 1 ? 6 : rank == 2 ? 10 : rank == 3 ? 16 : 24;
        }

        private static int PoliticalPressureRelationshipPenalty(int rank)
        {
            return rank <= 1 ? -10 : rank == 2 ? -15 : rank == 3 ? -20 : -25;
        }

        private static int PoliticalPressureForeignRulerDelta(string stance, int relationshipPenalty)
        {
            int magnitude = Clamp(Math.Abs(Math.Min(0, relationshipPenalty)), 0, 100);
            if (stance == "preserve_relations") return magnitude;
            if (stance == "support_lords") return -magnitude;
            return 0;
        }

        internal static List<Dictionary<string, object>> SelectPoliticalPressureClans(string campaignId,
            string timelineId, int day, List<Dictionary<string, object>> clans, string kingdomId, string rulerHeroId,
            int severityRank, string side)
        {
            int count = severityRank >= 4 ? 3 : severityRank >= 2 ? 2 : 1;
            if (string.IsNullOrWhiteSpace(kingdomId) || string.IsNullOrWhiteSpace(rulerHeroId))
                return new List<Dictionary<string, object>>();
            return clans.Where(x => ReadString(x, "kingdomId", "").Equals(kingdomId, StringComparison.OrdinalIgnoreCase))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "clanId", "")))
                .Where(x => !string.IsNullOrWhiteSpace(ReadFirstString(x, "leaderHeroId", "leaderId"))
                    && !ReadFirstString(x, "leaderHeroId", "leaderId").Equals(rulerHeroId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => ReadInt(x, "fiefCount", 0) * 20 + ReadInt(x, "tier", 0) * 5
                    + StableRulerD100(campaignId, timelineId, day, "pressure-clan:" + side + ":" + ReadString(x, "clanId", "")))
                .ThenBy(x => ReadString(x, "clanId", ""), StringComparer.OrdinalIgnoreCase).Take(count).ToList();
        }

        internal static List<string> PoliticalPressureLeadLords(List<Dictionary<string, object>> clans)
        {
            return (clans ?? new List<Dictionary<string, object>>()).Select(x =>
                    FirstNonEmpty(ReadFirstString(x, "leaderName", "name"),
                        ReadFirstString(x, "leaderHeroId", "leaderId")))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<string> PoliticalPressureClanMembers(Dictionary<string, object> clan)
        {
            List<string> members = new List<string>();
            foreach (Dictionary<string, object> row in ReadDictionaryList(clan, "members"))
            {
                if (!ReadBool(row, "isAlive", true) || ReadBool(row, "isChild", false)) continue;
                string id = ReadFirstString(row, "heroId", "heroStringId");
                if (!string.IsNullOrWhiteSpace(id)) members.Add(id);
            }
            if (members.Count == 0)
            {
                string leader = ReadFirstString(clan, "leaderHeroId", "leaderId");
                if (!string.IsNullOrWhiteSpace(leader)) members.Add(leader);
            }
            return members.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static Dictionary<string, object> SelectPoliticalPressureTraitRoutes(string campaignId,
            string incidentId, PoliticalPressureArchetype archetype, string severity,
            Dictionary<string, object> origin, Dictionary<string, object> target,
            List<string> originLords, List<string> targetLords, out string status)
        {
            Dictionary<string, object> fallback = DefaultPoliticalPressureTraitRoutes(archetype, origin, target);
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["requestType"] = "political_pressure_trait_selector", ["campaignId"] = campaignId,
                ["correlationId"] = incidentId + ":traits", ["eventId"] = incidentId,
                ["reasoningDisabled"] = true, ["temperature"] = 0d, ["maxTokens"] = 220,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" },
                ["system"] = "You are Reign's bounded political-pressure trait classifier. Choose exactly three distinct IDs from compassion,boldness,honor,loyalty,responsibility,courage,judgment for each ruler. For every trait return highFavors as support_lords or preserve_relations and a reason under 12 words. Use only supplied facts. Do not output trait scores, dice, calculations, actions, or outcomes. Return JSON with origin and target objects, each containing a traits array.",
                ["prompt"] = Json.Serialize(new Dictionary<string, object>
                {
                    ["polarity"] = archetype.Polarity, ["channel"] = archetype.Channel, ["severity"] = severity,
                    ["incident"] = archetype.Description,
                    ["origin"] = CompactPoliticalPressureRulerContext(origin, target, originLords),
                    ["target"] = CompactPoliticalPressureRulerContext(target, origin, targetLords)
                })
            };
            Dictionary<string, object> llm = RequestPoliticalPressurePreparation(request);
            Dictionary<string, object> parsed = ReadBool(llm, "ok", false)
                ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
            if (ValidatePoliticalPressureTraitRoutes(parsed))
            {
                status = "llm";
                return NormalizePoliticalPressureTraitRoutes(parsed);
            }
            status = ReadBool(llm, "ok", false) ? "invalid_fallback" : "unavailable_fallback";
            return fallback;
        }

        private static Dictionary<string, object> CompactPoliticalPressureRulerContext(
            Dictionary<string, object> ruler, Dictionary<string, object> other, List<string> lords)
        {
            double ownStrength = Math.Max(1d, ReadDouble(ruler, "strength", 1d));
            double otherStrength = Math.Max(1d, ReadDouble(other, "strength", 1d));
            return new Dictionary<string, object>
            {
                ["kingdomId"] = ReadString(ruler, "kingdomId", ""), ["rulerId"] = ReadString(ruler, "leaderHeroId", ""),
                ["warCount"] = ReadStringList(ruler, "enemies").Count,
                ["relativeStrength"] = ownStrength >= otherStrength * 1.25d ? "stronger" : ownStrength * 1.25d <= otherStrength ? "weaker" : "similar",
                ["petitioningLords"] = lords ?? new List<string>()
            };
        }

        private static Dictionary<string, object> DefaultPoliticalPressureTraitRoutes(
            PoliticalPressureArchetype archetype, Dictionary<string, object> origin, Dictionary<string, object> target)
        {
            return new Dictionary<string, object>
            {
                ["origin"] = DefaultPoliticalPressureTraitRoute(archetype, origin, target),
                ["target"] = DefaultPoliticalPressureTraitRoute(archetype, target, origin)
            };
        }

        private static Dictionary<string, object> DefaultPoliticalPressureTraitRoute(
            PoliticalPressureArchetype archetype, Dictionary<string, object> ruler, Dictionary<string, object> other)
        {
            bool hostile = archetype.Polarity == "hostile";
            bool prudentSupport = ReadDouble(ruler, "strength", 1d) >= ReadDouble(other, "strength", 1d)
                && ReadStringList(ruler, "enemies").Count <= 1;
            List<object> traits;
            if (hostile)
                traits = new List<object>
                {
                    PoliticalPressureTrait("compassion", "preserve_relations", "Escalation can cause suffering."),
                    PoliticalPressureTrait("courage", "support_lords", "Defiance risks retaliation."),
                    PoliticalPressureTrait("judgment", prudentSupport ? "support_lords" : "preserve_relations", prudentSupport ? "The strategic balance favors action." : "The strategic balance favors restraint.")
                };
            else
                traits = new List<object>
                {
                    PoliticalPressureTrait("responsibility", "support_lords", "Settlement serves the realm's obligations."),
                    PoliticalPressureTrait("compassion", "support_lords", "Cooperation limits avoidable harm."),
                    PoliticalPressureTrait("judgment", "support_lords", "Cooperation offers a practical advantage.")
                };
            return new Dictionary<string, object> { ["traits"] = traits };
        }

        private static Dictionary<string, object> PoliticalPressureTrait(string id, string highFavors, string reason)
        {
            return new Dictionary<string, object> { ["id"] = id, ["highFavors"] = highFavors, ["reason"] = reason };
        }

        private static bool ValidatePoliticalPressureTraitRoutes(Dictionary<string, object> routes)
        {
            if (routes == null) return false;
            foreach (string side in new[] { "origin", "target" })
            {
                Dictionary<string, object> route = ReadDictionary(routes, side);
                List<Dictionary<string, object>> traits = ReadDictionaryList(route, "traits");
                if (traits.Count != 3) return false;
                if (traits.Select(x => ReadString(x, "id", "")).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) return false;
                if (traits.Any(x => !PoliticalPressureVirtues.Contains(ReadString(x, "id", ""), StringComparer.OrdinalIgnoreCase))) return false;
                if (traits.Any(x => !new[] { "support_lords", "preserve_relations" }.Contains(ReadString(x, "highFavors", ""), StringComparer.OrdinalIgnoreCase))) return false;
            }
            return true;
        }

        private static Dictionary<string, object> NormalizePoliticalPressureTraitRoutes(Dictionary<string, object> routes)
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            foreach (string side in new[] { "origin", "target" })
            {
                result[side] = new Dictionary<string, object>
                {
                    ["traits"] = ReadDictionaryList(ReadDictionary(routes, side), "traits").Select(x => (object)new Dictionary<string, object>
                    {
                        ["id"] = ReadString(x, "id", "").Trim().ToLowerInvariant(),
                        ["highFavors"] = ReadString(x, "highFavors", "").Trim().ToLowerInvariant(),
                        ["reason"] = DialogueService.LimitText(ReadString(x, "reason", "Contextual court judgment."), 120)
                    }).ToList()
                };
            }
            return result;
        }

        private static string ResolvePoliticalPressureStance(string campaignId, string timelineId,
            int day, string incidentId, string side, Dictionary<string, object> ruler,
            Dictionary<string, object> route, List<Dictionary<string, object>> output)
        {
            Dictionary<string, object> virtues = LoadDirectorTraits(campaignId, ruler);
            int supportVotes = 0;
            foreach (Dictionary<string, object> trait in ReadDictionaryList(route, "traits"))
            {
                string id = ReadString(trait, "id", "");
                string highFavors = ReadString(trait, "highFavors", "preserve_relations");
                int score = Clamp((int)Math.Round(ReadDouble(virtues, id, 50d), MidpointRounding.AwayFromZero), 0, 100);
                int roll = StableRulerD100(campaignId, timelineId, day,
                    incidentId + ":" + side + ":" + ReadString(ruler, "leaderHeroId", "") + ":" + id);
                string vote = roll <= score ? highFavors
                    : highFavors == "support_lords" ? "preserve_relations" : "support_lords";
                if (vote == "support_lords") supportVotes++;
                output.Add(new Dictionary<string, object>
                {
                    ["side"] = side, ["rulerId"] = ReadString(ruler, "leaderHeroId", ""), ["trait"] = id,
                    ["score"] = score, ["roll"] = roll, ["highFavors"] = highFavors, ["vote"] = vote,
                    ["reason"] = ReadString(trait, "reason", "")
                });
            }
            return supportVotes >= 2 ? "support_lords" : "preserve_relations";
        }

        private static void ApplyPoliticalPressureDomesticRefusal(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaignId, string timelineId,
            double worldDay, string incidentId, string side, Dictionary<string, object> kingdom,
            List<Dictionary<string, object>> clans, List<Dictionary<string, object>> settlements,
            int relationshipPenalty, string correlation)
        {
            string rulerId = ReadString(kingdom, "leaderHeroId", "");
            string kingdomId = ReadString(kingdom, "kingdomId", "");
            HashSet<string> clanIds = new HashSet<string>((clans ?? new List<Dictionary<string, object>>())
                .Select(x => ReadString(x, "clanId", "")).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            var affected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> clan in clans)
            foreach (string memberId in PoliticalPressureClanMembers(clan))
            {
                if (memberId.Equals(rulerId, StringComparison.OrdinalIgnoreCase) || !affected.Add(memberId)) continue;
                ApplyRulerDiplomaticIncidentInTransaction(connection, transaction, campaignId, timelineId, worldDay,
                    "political_pressure_lords_refused", incidentId + ":" + side + ":relation:" + memberId,
                    incidentId, memberId, rulerId, kingdomId, kingdomId, relationshipPenalty,
                    false, correlation, new Dictionary<string, object>
                    {
                        ["incidentId"] = incidentId, ["side"] = side,
                        ["clanId"] = ReadString(clan, "clanId", ""), ["severityPenalty"] = relationshipPenalty
                    });
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<string> targets = settlements
                .Where(x => clanIds.Contains(ReadString(x, "ownerClanId", ""))
                    && (ReadBool(x, "isTown", false) || ReadBool(x, "isCastle", false)))
                .Select(x => ReadString(x, "settlementId", "")).Where(x => !string.IsNullOrEmpty(x))
                .Distinct(StringComparer.Ordinal).ToList();
            foreach (string settlement in targets)
            {
                string effect = incidentId + ":" + side + ":loyalty:" + settlement;
                var terms = TestDict("id", effect, "incident", incidentId, "campaign", campaignId,
                    "timeline", timelineId, "day", worldDay, "target", settlement, "ts", ts);
                DiplomacyStore.InsertOrIgnorePoliticalPressureNativeEffects(connection, terms, transaction);
                if (!DiplomacyStore.HasExactPoliticalPressureLoyaltyEffect(connection, transaction, terms))
                    throw new InvalidOperationException("Political pressure loyalty effect conflicts with the sealed incident.");
            }
            WorldSimulationService.WriteWorldTestCounterSchemaReady(connection, campaignId, timelineId,
                (int)Math.Floor(worldDay), "political_pressures", incidentId + ":domestic:" + side, TestDict(
                    "rulersRefusingLords", 1, "relationshipPenalties", affected.Count,
                    "loyaltyEffectsQueued", targets.Count), transaction);
        }

        private static void ApplyPoliticalPressureForeignRulerChoice(ReignDbConnection connection, ReignDbTransaction transaction,
            string campaignId, string timelineId,
            double worldDay, string incidentId, string side, Dictionary<string, object> kingdom,
            Dictionary<string, object> otherKingdom, string stance, int relationshipPenalty, string correlation)
        {
            int affinityDelta = PoliticalPressureForeignRulerDelta(stance, relationshipPenalty);
            if (affinityDelta == 0) return;
            string rulerId = ReadString(kingdom, "leaderHeroId", "");
            string otherRulerId = ReadString(otherKingdom, "leaderHeroId", "");
            string kingdomId = ReadString(kingdom, "kingdomId", "");
            string otherKingdomId = ReadString(otherKingdom, "kingdomId", "");
            string eventKind = affinityDelta > 0
                ? "political_pressure_foreign_relations_preserved"
                : "political_pressure_foreign_relations_sacrificed";
            ApplyRulerDiplomaticIncidentInTransaction(connection, transaction, campaignId, timelineId, worldDay,
                eventKind, incidentId + ":" + side + ":foreign-ruler", incidentId,
                rulerId, otherRulerId, kingdomId, otherKingdomId, affinityDelta,
                true, correlation, new Dictionary<string, object>
                {
                    ["incidentId"] = incidentId, ["side"] = side, ["stance"] = stance,
                    ["domesticRelationshipPenalty"] = relationshipPenalty,
                    ["foreignRulerDelta"] = affinityDelta
                });
        }

        private static Dictionary<string, object> RequestPoliticalPressurePreparation(Dictionary<string, object> request)
        {
            // Two optional provider stages must both leave time to seal the five-minute day claim.
            // Bound a local settings snapshot, then use the ordinary deterministic fallback on timeout.
            var settings = new Dictionary<string, object>(SettingsService.LoadSettings());
            settings["providerRequestTimeoutMs"] = Math.Clamp(ReadInt(settings, "providerRequestTimeoutMs", 80000), 15000, 80000);
            settings["providerQueueTimeoutMs"] = Math.Clamp(ReadInt(settings, "providerQueueTimeoutMs", 15000), 1000, 15000);
            request["singleProviderAttempt"] = true;
            return PersistenceService.WithCampaignReadLeaseSuspended(
                () => LlmProviderService.ChatWithLlm(request, null, null, settings));
        }

        private static string GeneratePoliticalPressureNarrative(string campaignId, string incidentId,
            PoliticalPressureArchetype archetype, string severity, Dictionary<string, object> origin,
            Dictionary<string, object> target, List<string> originLords, List<string> targetLords,
            string originStance, string targetStance, out string status)
        {
            Dictionary<string, object> request = new Dictionary<string, object>
            {
                ["requestType"] = "political_pressure_narration", ["campaignId"] = campaignId,
                ["correlationId"] = incidentId + ":narration", ["eventId"] = incidentId,
                ["reasoningDisabled"] = true, ["temperature"] = 0.45d, ["maxTokens"] = 220,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" },
                ["system"] = "Write a concise Bannerlord political world notice using only supplied facts. Return JSON {headline,narrative}. Narrative is one to three sentences. Do not invent wars, deaths, ownership, treaties, dice, relationship values, or outcomes.",
                ["prompt"] = Json.Serialize(new Dictionary<string, object>
                {
                    ["catalogHeadline"] = archetype.Headline, ["catalogFacts"] = archetype.Description,
                    ["polarity"] = archetype.Polarity, ["channel"] = archetype.Channel, ["severity"] = severity,
                    ["originKingdom"] = ReadString(origin, "name", ""), ["targetKingdom"] = ReadString(target, "name", ""),
                    ["originRuler"] = ReadString(origin, "leaderName", ""), ["targetRuler"] = ReadString(target, "leaderName", ""),
                    ["originLords"] = originLords, ["targetLords"] = targetLords,
                    ["originDecision"] = originStance, ["targetDecision"] = targetStance
                })
            };
            Dictionary<string, object> llm = RequestPoliticalPressurePreparation(request);
            Dictionary<string, object> parsed = ReadBool(llm, "ok", false)
                ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
            string narrative = DialogueService.LimitText(ReadString(parsed, "narrative", ""), 900);
            if (!string.IsNullOrWhiteSpace(narrative))
            {
                status = "llm";
                return narrative;
            }
            status = ReadBool(llm, "ok", false) ? "invalid_fallback" : "unavailable_fallback";
            return archetype.Description + " " + ReadString(origin, "leaderName", "The first ruler") + " "
                + PoliticalPressureStanceText(originStance) + ", while " + ReadString(target, "leaderName", "the other ruler")
                + " " + PoliticalPressureStanceText(targetStance) + ".";
        }

        private static string PoliticalPressureStanceText(string stance)
        {
            return stance == "awaiting_player" ? "has not yet given a ruling"
                : stance == "support_lords" ? "publicly supported the petitioning lords" : "refused the petition to preserve foreign relations";
        }

        internal static int ReadPoliticalPressureValue(string campaignId, string timelineId, string actorId, string targetId)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
            }
        }

        internal static int ReadPoliticalPressureValue(ReignDbConnection connection, string campaignId,
            string timelineId, string actorId, string targetId)
        {
            return ReadInt(DiplomacyStore.SelectPoliticalPressureState(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["actor"] = actorId, ["target"] = targetId }).FirstOrDefault(), "pressure_value", 0);
        }

        private sealed record PoliticalPressureMovement(int Before, int After, bool Applied);

        internal static int ApplyPoliticalPressureDelta(string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> actor, Dictionary<string, object> target, int delta, string channel, string incidentId)
        {
            return ApplyMeasuredPoliticalPressureDelta(campaignId, timelineId, worldDay, actor, target,
                delta, channel, incidentId).After;
        }

        private static PoliticalPressureMovement ApplyMeasuredPoliticalPressureDelta(string campaignId, string timelineId,
            double worldDay, Dictionary<string, object> actor, Dictionary<string, object> target, int delta,
            string channel, string incidentId)
        {
            string actorId = ReadString(actor, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            if (ReadBool(actor, "isPlayerKingdom", false) || string.IsNullOrWhiteSpace(actorId)
                || string.IsNullOrWhiteSpace(targetId) || actorId.Equals(targetId, StringComparison.OrdinalIgnoreCase))
                return new PoliticalPressureMovement(0, 0, false);

            using var connection = ReignDatabase.OpenCampaignConnection(campaignId);
            string ownedCampaign = ReignPostgreSqlStorage.CampaignIdForConnection(connection);
            if (string.IsNullOrEmpty(ownedCampaign))
                throw new InvalidOperationException("The pressure direction needs an owned campaign connection.");
            EnsurePoliticalPressureSchema(connection);
            WorldSimulationService.EnsureWorldTestTelemetrySchema(connection);
            using var suppressRetries = new ReignDatabase.SqlStatementRetrySuppression(connection);
            using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted);
            var movement = ApplyPoliticalPressureDeltaInTransaction(connection, transaction, campaignId, timelineId,
                worldDay, actor, target, delta, channel, incidentId);
            if (movement.Applied) PersistenceService.MarkSaveSyncActiveStateDirty(ownedCampaign);
            transaction.Commit();
            if (movement.Applied) WorldSimulationService.SignalWorldTestRollupWorker();
            return movement;
        }

        // The caller owns the campaign connection and transaction. Each direction is locked before measurement.
        private static PoliticalPressureMovement ApplyPoliticalPressureDeltaInTransaction(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> actor, Dictionary<string, object> target, int delta, string channel, string incidentId)
        {
            string actorId = ReadString(actor, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            var direction = TestDict("campaign", campaignId, "timeline", timelineId, "actor", actorId, "target", targetId,
                "incident", incidentId ?? "", "day", worldDay, "delta", delta);
            if (!string.IsNullOrWhiteSpace(incidentId)
                && DiplomacyStore.InsertOrIgnorePoliticalPressureDeltaReceipts(connection, direction, transaction).Count == 0)
            {
                int current = ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
                return new PoliticalPressureMovement(current, current, false);
            }

            DiplomacyStore.EnsurePoliticalPressureDirection(connection, direction, transaction);
            int before = DiplomacyStore.LockPoliticalPressureDirection(connection, direction, transaction);
            int after = (int)Math.Clamp((long)before + delta, -100, 100);
            direction["actorName"] = ReadString(actor, "name", "");
            direction["targetName"] = ReadString(target, "name", "");
            direction["actorRuler"] = ReadString(actor, "leaderHeroId", "");
            direction["targetRuler"] = ReadString(target, "leaderHeroId", "");
            direction["value"] = after;
            direction["channel"] = after == 0 ? "" : channel ?? "";
            var row = DiplomacyStore.UpsertPoliticalPressureState(connection, direction, transaction).Single();
            if (ReadInt(row, "pressure_value", int.MinValue) != after)
                throw new InvalidOperationException("The stored pressure update does not match the measured movement.");

            int magnitude = Math.Abs(after - before);
            WorldSimulationService.WriteWorldTestCounterSchemaReady(connection, campaignId, timelineId, (int)Math.Floor(worldDay),
                "political_pressures", incidentId + ":pressure:" + actorId,
                TestDict("pressureMovement", magnitude, after > before ? "hostilePressureAdded" : "peacefulPressureAdded", magnitude,
                    "rulersSupportingLords", 1), transaction);
            return new PoliticalPressureMovement(before, after, true);
        }

        private static Dictionary<string, object> SelectPoliticalPressureOpportunity(string campaignId,
            string timelineId, double worldDay, List<Dictionary<string, object>> kingdoms,
            Dictionary<string, object> state)
        {
            int day = (int)Math.Floor(worldDay + 0.000001d);
            Dictionary<string, Dictionary<string, object>> byId = kingdoms.ToDictionary(x => ReadString(x, "kingdomId", ""),
                x => x, StringComparer.OrdinalIgnoreCase);
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                List<Dictionary<string, object>> rows = DiplomacyStore.SelectPoliticalPressureState2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
                List<Dictionary<string, object>> passed = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> row in rows)
                {
                    string actorId = ReadString(row, "actor_kingdom_id", "");
                    string targetId = ReadString(row, "target_kingdom_id", "");
                    if (!byId.TryGetValue(actorId, out Dictionary<string, object> actor)
                        || !byId.TryGetValue(targetId, out Dictionary<string, object> target)
                        || ReadBool(actor, "isPlayerKingdom", false)) continue;
                    Dictionary<string, object> rulerState = GetDirectorRulerState(state, actor, worldDay);
                    if (ReadDouble(rulerState, "cooldownUntilDay", -1d) > worldDay) continue;
                    Dictionary<string, object> pairCooldowns = ReadDictionary(rulerState, "pairCooldowns") ?? new Dictionary<string, object>();
                    if (ReadDouble(pairCooldowns, targetId, -1d) > worldDay) continue;
                    int value = ReadInt(row, "pressure_value", 0);
                    int chance = Math.Max(0, Math.Min(50, (int)Math.Round(Math.Abs(value) * 0.5d, MidpointRounding.AwayFromZero)));
                    int roll = StableRulerD100(campaignId, timelineId, day, "pressure-action:" + actorId + ":" + targetId);
                    bool didPass = roll <= chance;
                    long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    DiplomacyStore.InsertOrIgnorePoliticalPressureActionRolls(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day, ["worldDay"] = worldDay,
                            ["actor"] = actorId, ["target"] = targetId, ["value"] = value,
                            ["polarity"] = value > 0 ? "hostile" : "peaceful", ["channel"] = ReadString(row, "dominant_channel", ""),
                            ["chance"] = chance, ["roll"] = roll, ["passed"] = didPass ? 1 : 0, ["ts"] = ts
                        });
                    if (!didPass) continue;
                    row["ruler"] = actor; row["target"] = target; row["chance"] = chance; row["roll"] = roll;
                    passed.Add(row);
                }
                Dictionary<string, object> selected = passed.OrderByDescending(x => Math.Abs(ReadInt(x, "pressure_value", 0)))
                    .ThenBy(x => ReadDouble(x, "updated_day", 0d)).ThenBy(x => ReadString(x, "actor_kingdom_id", ""), StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => ReadString(x, "target_kingdom_id", ""), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (selected == null) return null;
                string selectedActor = ReadString(selected, "actor_kingdom_id", "");
                string selectedTarget = ReadString(selected, "target_kingdom_id", "");
                DiplomacyStore.UpdatePoliticalPressureActionRolls(connection, new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId,
                        ["timeline"] = timelineId, ["day"] = day, ["actor"] = selectedActor, ["target"] = selectedTarget });
                int pressure = ReadInt(selected, "pressure_value", 0);
                string polarity = pressure > 0 ? "negative" : "positive";
                string channel = ReadString(selected, "dominant_channel", "");
                bool atWar = ReadStringList(ReadDictionary(selected, "ruler"), "enemies").Contains(selectedTarget, StringComparer.OrdinalIgnoreCase);
                string mode = pressure > 0 ? "expansion" : atWar ? "peace" : channel == "trade" ? "prosperity" : "security";
                return new Dictionary<string, object>
                {
                    ["ruler"] = ReadDictionary(selected, "ruler"), ["mode"] = mode, ["targetKingdomId"] = selectedTarget,
                    ["politicalPressureId"] = selectedActor + "->" + selectedTarget,
                    ["politicalPressureValue"] = pressure, ["politicalPressurePolarity"] = polarity,
                    ["politicalPressureChannel"] = channel, ["finalChance"] = ReadInt(selected, "chance", 0) / 100d,
                    ["roll"] = ReadInt(selected, "roll", 0) / 100d, ["priority"] = Math.Abs(pressure)
                };
            }
        }

        private static void CompletePoliticalPressureDiplomacy(string campaignId, string timelineId,
            double worldDay, Dictionary<string, object> actor, Dictionary<string, object> target,
            string directionId, bool accepted, string actionId, string eventId, string outcome)
        {
            if (string.IsNullOrWhiteSpace(directionId)) return;
            string actorId = ReadString(actor, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            int before;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                before = ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
                DiplomacyStore.UpdatePoliticalPressureState(connection, new Dictionary<string, object> { ["day"] = worldDay, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["actor"] = actorId, ["target"] = targetId });
                DiplomacyStore.UpdatePoliticalPressureActionRolls2(connection, new Dictionary<string, object> { ["action"] = actionId ?? "", ["outcome"] = outcome ?? "",
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId,
                        ["timeline"] = timelineId, ["actor"] = actorId, ["target"] = targetId });
            }
            int after = 0;
            if (!accepted)
            {
                ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay,
                    "political_pressure_proposal_refused", "pressure-refusal:" + eventId,
                    eventId, ReadString(actor, "leaderHeroId", ""), ReadString(target, "leaderHeroId", ""),
                    actorId, targetId, -25, "", 0, true, "political-pressure-refusal:" + eventId);
                after = ApplyPoliticalPressureDelta(campaignId, timelineId, worldDay,
                    actor, target, 8, "coercion", "pressure-refusal:" + eventId);
            }
            RecordPoliticalPressureActivity(campaignId, timelineId, worldDay, "", actor, target,
                "pressure_diplomacy_resolved", before > 0 ? "hostile" : "peaceful", "", "",
                accepted ? "accepted" : "refused", before, after, 0, 0, actionId,
                "political-pressure-action:" + eventId, outcome, new Dictionary<string, object>
                {
                    ["directionId"] = directionId, ["eventId"] = eventId, ["pressureConsumed"] = Math.Abs(before),
                    ["hostileBacklash"] = accepted ? 0 : 8, ["relationshipPenalty"] = accepted ? 0 : -25
                });
            WorldSimulationService.InvalidateWorldTestOverviewCache(campaignId, timelineId);
        }

        private static void RecordPoliticalPressureNoAction(string campaignId, string timelineId,
            double worldDay, Dictionary<string, object> actor, Dictionary<string, object> target,
            string directionId, string reason)
        {
            if (string.IsNullOrWhiteSpace(directionId)) return;
            string actorId = ReadString(actor, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            int pressure;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                pressure = ReadPoliticalPressureValue(connection, campaignId,
                    timelineId, actorId, targetId);
                DiplomacyStore.UpdatePoliticalPressureActionRolls3(connection, new Dictionary<string, object>
                {
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["actor"] = actorId, ["target"] = targetId
                });
            }
            RecordPoliticalPressureActivity(campaignId, timelineId, worldDay, "", actor, target,
                "pressure_diplomacy_no_action", "", "", "", "no_credible_action",
                pressure, pressure,
                0, 0, "", "political-pressure-no-action:" + directionId, reason, null);
        }

        internal static Dictionary<string, object> NextPoliticalPressureNotices(Dictionary<string, string> query)
        {
            PoliticalPressureNoticesRequest request = PoliticalPressureNoticesRequest.FromQuery(query);
            if (query?.ContainsKey("sharedActivityGeneration") == true)
                return WorldSimulationService.ReadOwnedNoticeSnapshot(query, (connection, transaction, campaign, timeline) =>
                    ReadPoliticalPressureNotices(connection, campaign, timeline, request.Limit, transaction));
            string campaignId = request.CampaignId;
            string timelineId = request.TimelineId;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return ReadPoliticalPressureNotices(connection, campaignId, timelineId, request.Limit);
            }
        }

        private static Dictionary<string, object> ReadPoliticalPressureNotices(ReignDbConnection connection,
            string campaignId, string timelineId, int limit, ReignDbTransaction ownedTransaction = null)
        {
            bool owned = ownedTransaction != null;
            var scope = new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId };
            List<Dictionary<string, object>> incidents = owned && !WorldSimulationService.TableExists(connection, "political_pressure_incidents")
                ? new List<Dictionary<string, object>>()
                : DiplomacyStore.SelectPoliticalPressureIncidents4(connection, limit.ToString(CultureInfo.InvariantCulture), scope, ownedTransaction);
            if (owned && incidents.Count != 0 && !WorldSimulationService.TableExists(connection, "political_pressure_native_effects"))
                return null;
            foreach (Dictionary<string, object> incident in incidents)
            {
                incident["news_description"] = PoliticalPressureCatalog.FirstOrDefault(a => a.Id == ReadString(incident, "archetype_id", ""))?.Description ?? "";
                string id = ReadString(incident, "incident_id", "");
                long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (owned)
                {
                    var notice = new Dictionary<string, object>(scope) { ["incident"] = id, ["ts"] = ts };
                    incident["nativeEffects"] = DiplomacyStore.ReadOwnedPoliticalPressureEffects(connection, notice, ownedTransaction);
                    DiplomacyStore.MarkOwnedPoliticalPressureNoticeFetched(connection, notice, ownedTransaction);
                }
                else
                {
                    incident["nativeEffects"] = DiplomacyStore.SelectPoliticalPressureNativeEffects(connection, new Dictionary<string, object> { ["incident"] = id });
                    DiplomacyStore.UpdatePoliticalPressureIncidents2(connection, new Dictionary<string, object> { ["ts"] = ts, ["id"] = id });
                }
            }
            // Current directed state, including zeros after consumed diplomacy; incident history is not readiness.
            var readinessRows = owned && !WorldSimulationService.TableExists(connection, "political_pressure_state")
                ? new List<Dictionary<string, object>>()
                : DiplomacyStore.SelectPoliticalPressureState3(connection, scope, ownedTransaction);
            return new PoliticalPressureNoticesResponse(incidents,
                new PoliticalPressureReadiness(campaignId, timelineId, readinessRows)).ToDictionary();
        }

        internal static Dictionary<string, object> AcknowledgePoliticalPressureNotice(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            PoliticalPressureAcknowledgeRequest request = PoliticalPressureAcknowledgeRequest.FromDictionary(payload);
            string campaignId = request.CampaignId;
            string timelineId = request.TimelineId;
            string incidentId = request.IncidentId;
            double receiptDay = request.WorldDay;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(timelineId)
                || string.IsNullOrWhiteSpace(incidentId))
                return PoliticalPressureAcknowledgeResponse.NotFound().ToDictionary();
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaignId);
            using (ReignDbConnection connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(
                campaignId, trackCampaignMutations: true))
            {
                if (connection == null) return PoliticalPressureAcknowledgeResponse.NotFound().ToDictionary();
                using var transaction = connection.BeginTransaction();
                AcquirePostgreSqlTransactionMutationLock(connection, transaction);
                var scope = new Dictionary<string, object>
                {
                    ["incident"] = incidentId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["ts"] = ts
                };
                if (!WorldSimulationService.NoticeAcknowledgementOwnerMatches(connection, transaction, payload, campaignId, timelineId)
                    || !WorldSimulationService.TableExists(connection, "political_pressure_incidents")
                    || !WorldSimulationService.TableExists(connection, "political_pressure_activity")
                    || !DiplomacyStore.LockPoliticalPressureAcknowledgement(connection, scope, transaction))
                    return PoliticalPressureAcknowledgeResponse.NotFound().ToDictionary();
                if (request.EffectReceipts.Count != 0)
                {
                    if (!WorldSimulationService.TableExists(connection, "political_pressure_native_effects"))
                        return PoliticalPressureAcknowledgeResponse.NotFound().ToDictionary();
                    var effects = new HashSet<string>(DiplomacyStore
                        .LockPoliticalPressureAcknowledgementEffects(connection, scope, transaction)
                        .Select(effect => ReadString(effect, "effect_id", "")), StringComparer.Ordinal);
                    if (request.EffectReceipts.Any(receipt =>
                        string.IsNullOrWhiteSpace(receipt.EffectId) || !effects.Remove(receipt.EffectId)))
                        return PoliticalPressureAcknowledgeResponse.NotFound().ToDictionary();
                }
                foreach (PoliticalPressureEffectReceipt receipt in request.EffectReceipts)
                {
                    DiplomacyStore.UpdatePoliticalPressureNativeEffects(connection, new Dictionary<string, object>(scope)
                        {
                            ["status"] = receipt.Ok ? "applied" : "failed",
                            ["before"] = receipt.Before, ["after"] = receipt.After,
                            ["day"] = receiptDay, ["error"] = receipt.Error, ["id"] = receipt.EffectId
                        }, transaction);
                }
                DiplomacyStore.UpdatePoliticalPressureIncidents3(connection, scope, transaction);
                RecordPoliticalPressureActivity(campaignId, timelineId, receiptDay, incidentId, null, null,
                    "message_receipt", "", "", "", "acknowledged", 0, 0, 0, 0, "", incidentId,
                    "Game message displayed and native effects reported.", payload, connection, transaction);
                transaction.Commit();
            }
            WorldSimulationService.InvalidateWorldTestOverviewCache(campaignId, timelineId);
            return PoliticalPressureAcknowledgeResponse.Acknowledged(incidentId).ToDictionary();
        }

        private static void RecordPoliticalPressureActivity(string campaignId, string timelineId,
            double worldDay, string incidentId, Dictionary<string, object> actor, Dictionary<string, object> target,
            string eventType, string polarity, string channel, string severity, string status,
            int before, int after, int roll, int chance, string actionId, string correlation,
            string reason, Dictionary<string, object> payload, ReignDbConnection existingConnection = null,
            ReignDbTransaction transaction = null)
        {
            string activityId = correlation + ":" + eventType + ":" + (actionId ?? "") + ":" + (incidentId ?? "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection ownedConnection = existingConnection == null
                ? ReignDatabase.OpenCampaignConnection(campaignId) : null)
            {
                ReignDbConnection connection = existingConnection ?? ownedConnection;
                if (ownedConnection != null) EnsurePoliticalPressureSchema(connection);
                DiplomacyStore.InsertOrIgnorePoliticalPressureActivity(connection, new Dictionary<string, object>
                    {
                        ["id"] = activityId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay,
                        ["incident"] = incidentId ?? "", ["actor"] = ReadString(actor, "kingdomId", ""),
                        ["target"] = ReadString(target, "kingdomId", ""), ["actorRuler"] = ReadString(actor, "leaderHeroId", ""),
                        ["targetRuler"] = ReadString(target, "leaderHeroId", ""), ["type"] = eventType ?? "",
                        ["polarity"] = polarity ?? "", ["channel"] = channel ?? "", ["severity"] = severity ?? "",
                        ["status"] = status ?? "", ["before"] = before, ["after"] = after, ["roll"] = roll,
                        ["chance"] = chance, ["action"] = actionId ?? "", ["correlation"] = correlation ?? "",
                        ["reason"] = reason ?? "", ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts
                    }, transaction);
                var counters = TestDict("activity:" + (eventType ?? "unknown"), 1, "status:" + (status ?? "unknown"), 1);
                if (transaction != null)
                    WorldSimulationService.WriteWorldTestCounterSchemaReady(connection, campaignId, timelineId,
                        (int)Math.Floor(worldDay), "political_pressures", activityId, counters, transaction);
                else
                    WorldSimulationService.RecordWorldTestCounter(connection, campaignId, timelineId,
                        (int)Math.Floor(worldDay), "political_pressures", activityId, counters);
            }
        }

        internal static Dictionary<string, object> BuildWorldTestPoliticalPressures(ReignDbConnection connection,
            string campaignId, string timelineId, double latestDay)
        {
            EnsurePoliticalPressureSchema(connection);
            EnsureKingdomLeaderDiplomacySchema(connection);
            Dictionary<string, object> daily = DiplomacyStore.CountPoliticalPressureDailyRolls(connection, new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> incidents = DiplomacyStore.CountPoliticalPressureIncidents2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> pressure = DiplomacyStore.CountPoliticalPressureState(connection, new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> actions = DiplomacyStore.CountPoliticalPressureActionRolls(connection, new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> effects = DiplomacyStore.CountPoliticalPressureNativeEffects(connection, new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> foreignRulerChoices = DiplomacyStore.SelectRulerDiplomaticIncidents2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            List<string> anomalies = new List<string>();
            int outOfRange = ReadInt(DiplomacyStore.CountPoliticalPressureState2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault(), "count", 0);
            if (outOfRange > 0) anomalies.Add(outOfRange + " directional pressure values are outside -100..100.");
            int overdue = ReadInt(DiplomacyStore.CountPoliticalPressureNativeEffects2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["cutoff"] = latestDay - 1d })
                .FirstOrDefault(), "count", 0);
            if (overdue > 0) anomalies.Add(overdue + " political-pressure native receipts are more than one campaign day overdue.");
            int failedEffects = ReadInt(effects, "failed", 0);
            if (failedEffects > 0) anomalies.Add(failedEffects
                + " political-pressure native effects failed to apply.");
            int incompleteVotes = ReadInt(DiplomacyStore.CountPoliticalPressureIncidents3(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault(), "count", 0);
            if (incompleteVotes > 0) anomalies.Add(incompleteVotes + " incidents are missing complete court-vote evidence.");
            int earliestRollDay = ReadInt(daily, "earliest_day", -1);
            int latestRollDay = ReadInt(daily, "latest_day", -1);
            int expectedDaily = earliestRollDay >= 0 && latestRollDay >= earliestRollDay
                ? latestRollDay - earliestRollDay + 1 : 0;
            int missingDaily = Math.Max(0, expectedDaily - ReadInt(daily, "total", 0));
            if (missingDaily > 0)
                anomalies.Add(missingDaily
                    + " political-pressure campaign-day rolls are missing between the first and latest processed days.");
            List<Dictionary<string, object>> matrix = DiplomacyStore.SelectPoliticalPressureState4(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
            Dictionary<string, int> channels = DiplomacyStore.SelectPoliticalPressureIncidents5(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .ToDictionary(x => ReadString(x, "channel", "unknown"), x => ReadInt(x, "count", 0), StringComparer.OrdinalIgnoreCase);
            int totalDaily = ReadInt(daily, "total", 0);
            return new Dictionary<string, object>
            {
                ["name"] = "Political Pressures", ["status"] = totalDaily == 0 ? "insufficient_data" : anomalies.Count > 0 ? "warning" : "healthy",
                ["latestSuccessfulDay"] = Math.Max(ReadDouble(daily, "latest", -1d), ReadDouble(incidents, "latest", -1d)),
                ["dailyRolls"] = totalDaily, ["dailyRollsPassed"] = ReadInt(daily, "passed", 0),
                ["dailyRollsExpected"] = expectedDaily,
                ["dailyRollsMissing"] = missingDaily,
                ["dailyRollCoveragePercent"] = expectedDaily == 0 ? 100d
                    : Math.Round(100d * totalDaily / expectedDaily, 2),
                ["incidentCount"] = ReadInt(incidents, "total", 0), ["hostileIncidents"] = ReadInt(incidents, "hostile", 0),
                ["peacefulIncidents"] = ReadInt(incidents, "peaceful", 0), ["supportedStances"] = ReadInt(incidents, "supported", 0),
                ["refusedStances"] = ReadInt(incidents, "refused", 0), ["directionCount"] = ReadInt(pressure, "directions", 0),
                ["hostileDirections"] = ReadInt(pressure, "hostile", 0), ["peacefulDirections"] = ReadInt(pressure, "peaceful", 0),
                ["neutralDirections"] = ReadInt(pressure, "neutral", 0), ["averageMagnitude"] = ReadDouble(pressure, "average_magnitude", 0d),
                ["maximumMagnitude"] = ReadInt(pressure, "maximum_magnitude", 0), ["actionRolls"] = ReadInt(actions, "total", 0),
                ["actionRollsPassed"] = ReadInt(actions, "passed", 0), ["actionsSelected"] = ReadInt(actions, "selected", 0),
                ["actionsRefused"] = ReadInt(actions, "refused", 0), ["nativeEffectsPending"] = ReadInt(effects, "pending", 0),
                ["nativeEffectsApplied"] = ReadInt(effects, "applied", 0), ["nativeEffectsFailed"] = ReadInt(effects, "failed", 0),
                ["foreignRelationsPreserved"] = ReadInt(foreignRulerChoices, "preserved", 0),
                ["foreignRelationsSacrificed"] = ReadInt(foreignRulerChoices, "sacrificed", 0),
                ["foreignRulerAffinityGained"] = ReadInt(foreignRulerChoices, "gained", 0),
                ["foreignRulerAffinityLost"] = ReadInt(foreignRulerChoices, "lost", 0),
                ["catalogSize"] = PoliticalPressureCatalog.Count, ["channels"] = channels, ["matrix"] = matrix, ["anomalies"] = anomalies
            };
        }

        internal static List<Dictionary<string, object>> QueryPoliticalPressureActivity(ReignDbConnection connection,
            string campaignId, string timelineId, Dictionary<string, string> query, int limit)
        {
            EnsurePoliticalPressureSchema(connection);
            string pair = query != null && query.TryGetValue("pair", out string pairValue) ? pairValue : "";
            string[] incidentColumns = { "origin_kingdom_id", "target_kingdom_id", "origin_ruler_id", "target_ruler_id",
                "origin_stance", "target_stance", "origin_pressure_before", "origin_pressure_after",
                "target_pressure_before", "target_pressure_after", "severity_rank", "pressure_amount", "polarity",
                "origin_lords_json", "target_lords_json", "trait_routes_json", "trait_rolls_json", "headline", "narrative" };
            string projection = string.Join(",", incidentColumns.Select(column => "i." + column + " AS detail_" + column));
            List<Dictionary<string, object>> rows = DiplomacyStore.SelectPoliticalPressureActivity(connection, projection, Math.Max(10, Math.Min(2000, limit)).ToString(CultureInfo.InvariantCulture),                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
            foreach (Dictionary<string, object> row in rows)
            {
                Dictionary<string, object> incident = null;
                if (!string.IsNullOrWhiteSpace(ReadString(row, "detail_id", "")))
                {
                    incident = new Dictionary<string, object> { ["incident_id"] = row["detail_id"] };
                    foreach (string column in incidentColumns) incident[column] = row["detail_" + column];
                }
                row["incident"] = incident;
                row.Remove("detail_id");
                foreach (string column in incidentColumns) row.Remove("detail_" + column);
            }
            if (!string.IsNullOrWhiteSpace(pair))
            {
                string[] parts = pair.Split(new[] { "->", "|" }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2) rows = rows.Where(x => ReadString(x, "actor_kingdom_id", "").Equals(parts[0], StringComparison.OrdinalIgnoreCase)
                    && ReadString(x, "target_kingdom_id", "").Equals(parts[1], StringComparison.OrdinalIgnoreCase)).ToList();
            }
            return rows;
        }

        private static List<PoliticalPressureArchetype> BuildPoliticalPressureCatalog()
        {
            string[] definitions =
            {
                "hostile|war|Border Retaliation|Border lords demand a forceful answer to repeated frontier provocations.",
                "hostile|war|Contested Pastures|Noble households dispute grazing rights and demand royal protection.",
                "hostile|war|Raid Accusations|Clans blame the neighboring realm for sheltering cross-border raiders.",
                "hostile|war|Military Demonstration|Frontier nobles petition for troops and an explicit warning.",
                "hostile|coercion|Caravan Seizure|Merchants and their patrons demand compensation for seized caravans.",
                "hostile|coercion|Punitive Tariffs|Powerful clans demand retaliation against discriminatory tolls.",
                "hostile|coercion|Fugitive Dispute|A protected fugitive becomes the center of a diplomatic confrontation.",
                "hostile|coercion|Mercenary Sponsorship|Lords accuse the neighboring court of supporting hostile mercenaries.",
                "hostile|treaty_strain|Broken Commercial Promise|Clan leaders accuse the other realm of violating promised access.",
                "hostile|treaty_strain|Treaty Insult|A public slight is presented as proof that restraint has failed.",
                "hostile|treaty_strain|Smuggling Crisis|Border magnates demand stronger action against protected smugglers.",
                "hostile|treaty_strain|Envoy Humiliated|The treatment of an envoy provokes demands for a formal response.",
                "hostile|punitive|Harsh Settlement Demands|War leaders insist that sacrifices must be repaid through punitive terms.",
                "hostile|punitive|Occupied Lands|Dispossessed clans demand the return of settlements or compensation.",
                "hostile|punitive|Prisoner Mistreatment|Noble families demand punishment for the treatment of captured kin.",
                "hostile|punitive|Indemnity Petition|The court demands that the enemy finance the cost of the conflict.",
                "peaceful|peace|War Weariness|Clans report that continued war is exhausting their lands and households.",
                "peaceful|peace|Peace Delegation|Influential nobles organize a delegation seeking negotiated peace.",
                "peaceful|peace|Captured Kin|Families on both sides press for reconciliation and prisoner exchange.",
                "peaceful|peace|Treasury Exhaustion|Court officials warn that another season of war threatens the realm.",
                "peaceful|trade|Market Access|Merchant clans petition for protected access to foreign markets.",
                "peaceful|trade|Caravan Road Compact|Border lords seek common rules for safer caravan roads.",
                "peaceful|trade|Seasonal Fair|Neighboring nobles support a shared fair and temporary commercial privileges.",
                "peaceful|trade|Tariff Reconciliation|Landowners and merchants request negotiations to lower punitive tolls.",
                "peaceful|security|Shared Border Patrols|Frontier clans request cooperation against bandits and raiders.",
                "peaceful|security|Non-Aggression Petition|Nobles seek guarantees that border forces will remain restrained.",
                "peaceful|security|Common Threat|Both courts face a danger that encourages defensive cooperation.",
                "peaceful|security|Mutual Recognition|Leading clans urge formal recognition of borders and titles.",
                "peaceful|aid_exchange|Famine Relief|A neighboring population's hardship prompts calls for measured assistance.",
                "peaceful|aid_exchange|Prisoner Exchange|Noble families petition for an organized exchange of captives.",
                "peaceful|aid_exchange|Pilgrimage Access|Religious and noble leaders request protected cross-border passage.",
                "peaceful|aid_exchange|Material Assistance|Clans advocate a limited subsidy or supply arrangement.",
            };
            string[] variants =
            {
                "The petition begins privately among senior clan leaders.",
                "The dispute spreads through a public court gathering.",
                "Settlement notables add their voices to the noble petition.",
                "Several households coordinate their demands before the ruler."
            };
            List<PoliticalPressureArchetype> result = new List<PoliticalPressureArchetype>();
            int definitionIndex = 0;
            foreach (string definition in definitions)
            {
                string[] parts = definition.Split('|');
                for (int variant = 0; variant < variants.Length; variant++)
                {
                    result.Add(new PoliticalPressureArchetype
                    {
                        Id = "pressure_" + parts[0] + "_" + parts[1] + "_" + definitionIndex.ToString("00", CultureInfo.InvariantCulture)
                            + "_" + (variant + 1).ToString(CultureInfo.InvariantCulture),
                        Polarity = parts[0], Channel = parts[1], Headline = parts[2], Description = parts[3] + " " + variants[variant]
                    });
                }
                definitionIndex++;
            }
            return result;
        }

        private static void CheckMeasuredPoliticalPressure(string campaignId, Action<string, bool, string> add)
        {
            const string timeline = "measured_pressure";
            var actor = TestDict("kingdomId", "actor");
            var target = TestDict("kingdomId", "target");
            PoliticalPressureMovement Apply(int delta, string incident) =>
                ApplyMeasuredPoliticalPressureDelta(campaignId, timeline, 12, actor, target, delta, "coercion", incident);

            var initial = Apply(95, "initial");
            var first = System.Threading.Tasks.Task.Run(() => Apply(10, "first"));
            var second = System.Threading.Tasks.Task.Run(() => Apply(10, "second"));
            System.Threading.Tasks.Task.WaitAll(first, second);
            var moves = new[] { first.Result, second.Result };
            var replay = Apply(-100, "first");
            using var reader = ReignDatabase.OpenCampaignConnection(campaignId);
            List<int> CounterMagnitudes(string branch, int day) => WorldSimulationStore.SelectWorldTestChunkCounters4(reader,
                TestDict("campaign", campaignId, "timeline", branch, "day", day, "subsystem", "political_pressures"))
                .Select(row => ReadInt(TryParseJsonObject(ReadString(row, "counters_json", "")), "pressureMovement", -1))
                .OrderBy(value => value).ToList();
            add("pressure_clamp_and_telemetry_measure_locked_preimage", initial.Before == 0 && initial.After == 95
                && moves.All(move => move.Applied && move.After == 100)
                && moves.Select(move => move.Before).OrderBy(value => value).SequenceEqual(new[] { 95, 100 })
                && !replay.Applied && replay.Before == 100 && replay.After == 100
                && CounterMagnitudes(timeline, 12).SequenceEqual(new[] { 0, 5, 95 }),
                "Concurrent writes measure the clamped movement, and replay adds neither a movement nor a counter.");

            const string newDirection = "new_pressure_direction";
            PoliticalPressureMovement Start(int delta, string incident) =>
                ApplyMeasuredPoliticalPressureDelta(campaignId, newDirection, 12, actor, target, delta, "coercion", incident);
            var createA = System.Threading.Tasks.Task.Run(() => Start(4, "create_a"));
            var createB = System.Threading.Tasks.Task.Run(() => Start(7, "create_b"));
            System.Threading.Tasks.Task.WaitAll(createA, createB);
            var ordered = new[] { createA.Result, createB.Result }.OrderBy(move => move.Before).ToArray();
            add("pressure_first_writers_serialize_an_absent_direction", ordered.All(move => move.Applied)
                && ordered[0].Before == 0 && ordered[1].Before == ordered[0].After && ordered[1].After == 11
                && CounterMagnitudes(newDirection, 12).SequenceEqual(new[] { 4, 7 }),
                "The first two independent writers share one initialized directed row and both counters measure their own changes.");

            const string rollback = "rolled_back_pressure";
            using (var writer = ReignDatabase.OpenCampaignConnection(campaignId))
            using (var transaction = writer.BeginTransaction())
            {
                ApplyPoliticalPressureDeltaInTransaction(writer, transaction, campaignId, rollback,
                    12, actor, target, 7, "coercion", "retry");
            }
            bool unchanged = ReadPoliticalPressureValue(reader, campaignId, rollback, "actor", "target") == 0
                && CounterMagnitudes(rollback, 12).Count == 0;
            var retry = ApplyMeasuredPoliticalPressureDelta(campaignId, rollback, 12, actor, target, 7, "coercion", "retry");
            add("pressure_rollback_keeps_receipt_and_telemetry_uncommitted", unchanged && retry.Applied
                && retry.Before == 0 && retry.After == 7 && CounterMagnitudes(rollback, 12).SequenceEqual(new[] { 7 }),
                "Disposing the surrounding transaction rolls back state, idempotency receipt, and counter together.");
        }

        internal static List<Dictionary<string, object>> RunPoliticalPressureSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, summary) => rows.Add(new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = passed, ["suite"] = "political_pressures", ["caseId"] = id,
                ["name"] = id, ["summary"] = summary, ["durationMs"] = 0
            });
            add("catalog_is_large", PoliticalPressureCatalog.Count >= 120,
                "The initial political-pressure catalog contains at least 120 versioned archetypes.");
            add("war_load_polarity", PoliticalPressureHostileChance(0) == 65 && PoliticalPressureHostileChance(1) == 50
                && PoliticalPressureHostileChance(2) == 35 && PoliticalPressureHostileChance(3) == 20,
                "Peace favors hostile incidents while multiple wars favor peaceful incidents.");
            add("severity_contract", PoliticalPressureAmount(1) == 6 && PoliticalPressureAmount(4) == 24
                && PoliticalPressureRelationshipPenalty(1) == -10 && PoliticalPressureRelationshipPenalty(4) == -25,
                "Severity maps to the approved pressure and domestic relationship consequences.");
            add("foreign_ruler_choice_mirrors_domestic_stakes",
                PoliticalPressureForeignRulerDelta("preserve_relations", -10) == 10
                && PoliticalPressureForeignRulerDelta("preserve_relations", -25) == 25
                && PoliticalPressureForeignRulerDelta("support_lords", -10) == -10
                && PoliticalPressureForeignRulerDelta("support_lords", -25) == -25,
                "Every pressure choice moves the choosing ruler's foreign relationship by the same magnitude as the domestic refusal stake.");
            add("opposite_pressure_cancels", Clamp(40 - 16, -100, 100) == 24 && Clamp(10 - 24, -100, 100) == -14,
                "Opposite signed pressure cancels the existing direction before crossing zero.");
            add("trait_catalog_exact", PoliticalPressureVirtues.Length == 7
                && PoliticalPressureVirtues.Contains("compassion") && PoliticalPressureVirtues.Contains("judgment"),
                "Trait helpers are restricted to Reign's seven court virtues.");
            add("campaign_day_catch_up_is_lossless",
                PoliticalPressureCatchUpDayKeys(100, 103)
                    .SequenceEqual(new[] { 101, 102, 103 })
                && PoliticalPressureCatchUpDayKeys(int.MinValue, 5)
                    .SequenceEqual(new[] { 5 })
                && PoliticalPressureCatchUpDayKeys(7, 7)
                    .SequenceEqual(new[] { 7 }),
                "The independent pressure producer processes every elapsed campaign day and remains idempotent on the current day.");
            string campaignId = "world_test_selftest_pressure_"
                + Guid.NewGuid().ToString("N").Substring(0, 10);
            try
            {
                bool firstClaim = false;
                bool secondClaim = false;
                System.Threading.Tasks.Task.WaitAll(
                    System.Threading.Tasks.Task.Run(() => firstClaim =
                        TryClaimPoliticalPressureDailyRoll(campaignId, "main", 9, 9.5d, 17)),
                    System.Threading.Tasks.Task.Run(() => secondClaim =
                        TryClaimPoliticalPressureDailyRoll(campaignId, "main", 9, 9.5d, 17)));
                using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
                {
                    int claimRows = ReadInt(DiplomacyStore.CountPoliticalPressureDailyRolls2(connection, new Dictionary<string, object> { ["campaign"] = campaignId })
                        .FirstOrDefault(), "count", -1);
                    add("daily_processing_claim_is_concurrency_safe",
                        firstClaim != secondClaim && claimRows == 1,
                        "Concurrent daily pressure workers leave one durable claim and exactly one worker owns side effects.");
                    DiplomacyStore.InsertPoliticalPressureState(connection, new Dictionary<string, object> { ["campaign"] = campaignId });
                    var readinessResponse = NextPoliticalPressureNotices(new Dictionary<string, string> { ["campaignId"] = campaignId, ["timelineId"] = "main" });
                    var readinessSnapshot = ReadDictionary(readinessResponse, "readinessPressure");
                    var readinessPairs = ReadDictionaryList(readinessSnapshot, "pairs");
                    add("readiness_uses_current_scoped_directed_state", ReadBool(readinessSnapshot, "complete", false)
                        && readinessPairs.Count == 2 && readinessPairs.Any(x => ReadString(x, "target_kingdom_id", "") == "hostile" && ReadInt(x, "pressure_value", -1) == 60)
                        && readinessPairs.Any(x => ReadString(x, "target_kingdom_id", "") == "resolved" && ReadInt(x, "pressure_value", -1) == 0),
                        "Readiness polling sees current hostile and consumed-zero directions from only the selected campaign timeline, without replaying incident effects.");
                }
                CheckMeasuredPoliticalPressure(campaignId, add);
#if !REIGN_EXCLUDE_VERIFICATION
                if (ReignPostgreSqlStorage.Options.IsValidation) CheckResumablePressureProduction(campaignId, add);
#endif
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaignId));
            }
            return rows;
        }
    }
}
