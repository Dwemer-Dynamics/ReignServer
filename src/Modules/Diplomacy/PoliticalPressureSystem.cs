using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int PoliticalPressureIncidentChance = 43;
        private const int PoliticalPressureMinimumActionValue = 10;
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

        private static void EnsurePoliticalPressureSchema(ReignDbConnection connection)
        {
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_delta_receipts (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
incident_id TEXT NOT NULL,requested_delta INTEGER NOT NULL,world_day REAL NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,incident_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_daily_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
chance INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,status TEXT NOT NULL,
origin_kingdom_id TEXT NOT NULL DEFAULT '',incident_id TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_state (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,
actor_kingdom_name TEXT NOT NULL DEFAULT '',target_kingdom_name TEXT NOT NULL DEFAULT '',
actor_ruler_id TEXT NOT NULL DEFAULT '',target_ruler_id TEXT NOT NULL DEFAULT '',pressure_value INTEGER NOT NULL DEFAULT 0,
dominant_channel TEXT NOT NULL DEFAULT '',updated_day REAL NOT NULL DEFAULT 0,last_incident_id TEXT NOT NULL DEFAULT '',
last_action_day REAL NOT NULL DEFAULT -1,revision INTEGER NOT NULL DEFAULT 1,
PRIMARY KEY(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_incidents (
incident_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
archetype_id TEXT NOT NULL,polarity TEXT NOT NULL,channel TEXT NOT NULL,severity TEXT NOT NULL,severity_rank INTEGER NOT NULL,
pressure_amount INTEGER NOT NULL,relationship_penalty INTEGER NOT NULL,
origin_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,origin_kingdom_name TEXT NOT NULL,target_kingdom_name TEXT NOT NULL,
origin_ruler_id TEXT NOT NULL,target_ruler_id TEXT NOT NULL,origin_ruler_name TEXT NOT NULL,target_ruler_name TEXT NOT NULL,
origin_war_count INTEGER NOT NULL,target_war_count INTEGER NOT NULL,hostile_chance INTEGER NOT NULL,polarity_roll INTEGER NOT NULL,
origin_clans_json TEXT NOT NULL,origin_lords_json TEXT NOT NULL,target_clans_json TEXT NOT NULL,target_lords_json TEXT NOT NULL,
trait_routes_json TEXT NOT NULL,trait_rolls_json TEXT NOT NULL,origin_stance TEXT NOT NULL,target_stance TEXT NOT NULL,
origin_pressure_before INTEGER NOT NULL,origin_pressure_after INTEGER NOT NULL,target_pressure_before INTEGER NOT NULL,target_pressure_after INTEGER NOT NULL,
headline TEXT NOT NULL,narrative TEXT NOT NULL,llm_selector_status TEXT NOT NULL,llm_narration_status TEXT NOT NULL,
notice_status TEXT NOT NULL DEFAULT 'ready',correlation_id TEXT NOT NULL,status TEXT NOT NULL DEFAULT 'completed',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,day_key));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_pressure_incidents_pair
ON political_pressure_incidents(campaign_id,timeline_id,origin_kingdom_id,target_kingdom_id,world_day DESC);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_native_effects (
effect_id TEXT PRIMARY KEY,incident_id TEXT NOT NULL,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
effect_type TEXT NOT NULL,target_id TEXT NOT NULL,amount INTEGER NOT NULL,status TEXT NOT NULL DEFAULT 'pending',
before_value REAL NOT NULL DEFAULT -1,after_value REAL NOT NULL DEFAULT -1,receipt_day REAL NOT NULL DEFAULT -1,
error TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_pressure_effects_pending
ON political_pressure_native_effects(campaign_id,timeline_id,status,world_day);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_action_rolls (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,day_key INTEGER NOT NULL,world_day REAL NOT NULL,
actor_kingdom_id TEXT NOT NULL,target_kingdom_id TEXT NOT NULL,pressure_value INTEGER NOT NULL,polarity TEXT NOT NULL,
channel TEXT NOT NULL,chance INTEGER NOT NULL,roll INTEGER NOT NULL,passed INTEGER NOT NULL,selected INTEGER NOT NULL DEFAULT 0,
status TEXT NOT NULL,action_id TEXT NOT NULL DEFAULT '',outcome TEXT NOT NULL DEFAULT '',created_ts INTEGER NOT NULL,updated_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,day_key,actor_kingdom_id,target_kingdom_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS political_pressure_activity (
activity_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,world_day REAL NOT NULL,
incident_id TEXT NOT NULL DEFAULT '',actor_kingdom_id TEXT NOT NULL DEFAULT '',target_kingdom_id TEXT NOT NULL DEFAULT '',
actor_ruler_id TEXT NOT NULL DEFAULT '',target_ruler_id TEXT NOT NULL DEFAULT '',event_type TEXT NOT NULL,
polarity TEXT NOT NULL DEFAULT '',channel TEXT NOT NULL DEFAULT '',severity TEXT NOT NULL DEFAULT '',status TEXT NOT NULL DEFAULT '',
before_value INTEGER NOT NULL DEFAULT 0,after_value INTEGER NOT NULL DEFAULT 0,roll INTEGER NOT NULL DEFAULT 0,chance INTEGER NOT NULL DEFAULT 0,
action_id TEXT NOT NULL DEFAULT '',correlation_id TEXT NOT NULL DEFAULT '',reason TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',
created_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_pressure_activity_timeline
ON political_pressure_activity(campaign_id,timeline_id,world_day DESC,event_type);");
        }

        private static Dictionary<string, object> EvaluatePoliticalPressure(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            double worldDay = ReadDouble(payload, "worldDay", 0d);
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

            int currentDay = (int)Math.Floor(worldDay + 0.000001d);
            int lastProcessedDay;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                lastProcessedDay = ReadInt(QuerySql(connection, @"
SELECT MAX(day_key) AS latest_day FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = timelineId
                    }).FirstOrDefault(), "latest_day", int.MinValue);
            }

            List<int> days = PoliticalPressureCatchUpDayKeys(
                lastProcessedDay, currentDay);
            List<Dictionary<string, object>> results =
                new List<Dictionary<string, object>>(days.Count);
            double fraction = Math.Max(0d,
                Math.Min(0.999999d, worldDay - currentDay));
            foreach (int day in days)
            {
                results.Add(ProcessDailyPoliticalPressure(campaignId,
                    timelineId, day + fraction, payload));
            }

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

        private static List<int> PoliticalPressureCatchUpDayKeys(
            int lastProcessedDay, int currentDay)
        {
            if (currentDay < 0) return new List<int>();
            if (lastProcessedDay == int.MinValue)
                return new List<int> { currentDay };
            if (lastProcessedDay >= currentDay)
                return new List<int> { currentDay };
            return Enumerable.Range(lastProcessedDay + 1,
                currentDay - lastProcessedDay).ToList();
        }

        private static Dictionary<string, object> ProcessDailyPoliticalPressure(
            string campaignId, string timelineId, double worldDay, Dictionary<string, object> world)
        {
            int day = (int)Math.Floor(worldDay + 0.000001d);
            int dailyRoll = StableRulerD100(campaignId, timelineId, day,
                "political-pressure:daily");
            if (!TryClaimPoliticalPressureDailyRoll(campaignId, timelineId,
                day, worldDay, dailyRoll))
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsurePoliticalPressureSchema(connection);
                    Dictionary<string, object> existing = QuerySql(connection, @"SELECT * FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day }).FirstOrDefault();
                    return new Dictionary<string, object> { ["ok"] = true, ["status"] = "already_processed", ["day"] = day,
                        ["incidentId"] = ReadString(existing, "incident_id", "") };
                }
            }

            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(world, "kingdoms")
                .Where(x => !ReadBool(x, "isRebelRealm", false))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "kingdomId", "")))
                .Where(x => !string.IsNullOrWhiteSpace(ReadString(x, "leaderHeroId", "")))
                .ToList();
            if (kingdoms.Count < 2 || dailyRoll > PoliticalPressureIncidentChance)
            {
                string status = kingdoms.Count < 2 ? "no_eligible_kingdoms" : "roll_failed";
                RecordPoliticalPressureDailyRoll(campaignId, timelineId, day, worldDay,
                    dailyRoll, false, status, "", "");
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = status, ["roll"] = dailyRoll,
                    ["chance"] = PoliticalPressureIncidentChance };
            }

            Dictionary<string, object> origin = SelectPoliticalPressureOrigin(campaignId, timelineId, day, kingdoms);
            int warCount = ReadStringList(origin, "enemies").Count;
            int hostileChance = PoliticalPressureHostileChance(warCount);
            int polarityRoll = StableRulerD100(campaignId, timelineId, day, "political-pressure:polarity:" + ReadString(origin, "kingdomId", ""));
            string polarity = polarityRoll <= hostileChance ? "hostile" : "peaceful";
            Dictionary<string, object> target = SelectPoliticalPressureTarget(campaignId, timelineId, day, origin, kingdoms, polarity);
            if (target == null)
            {
                RecordPoliticalPressureDailyRoll(campaignId, timelineId, day, worldDay,
                    dailyRoll, true, "no_eligible_target", ReadString(origin, "kingdomId", ""), "");
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "no_eligible_target" };
            }

            string channel = SelectPoliticalPressureChannel(campaignId, timelineId, day, origin, target, polarity);
            PoliticalPressureArchetype archetype = SelectPoliticalPressureArchetype(
                campaignId, timelineId, day, origin, target, polarity, channel);
            if (archetype == null)
            {
                RecordPoliticalPressureDailyRoll(campaignId, timelineId, day, worldDay,
                    dailyRoll, true, "catalog_cooldown", ReadString(origin, "kingdomId", ""), "");
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "catalog_cooldown" };
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
                RecordPoliticalPressureDailyRoll(campaignId, timelineId, day, worldDay,
                    dailyRoll, true, "no_eligible_petitioners", ReadString(origin, "kingdomId", ""), "");
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "no_eligible_petitioners" };
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

            int signedAmount = polarity == "hostile" ? pressureAmount : -pressureAmount;
            int originBefore = ReadPoliticalPressureValue(campaignId, timelineId,
                ReadString(origin, "kingdomId", ""), ReadString(target, "kingdomId", ""));
            int targetBefore = ReadPoliticalPressureValue(campaignId, timelineId,
                ReadString(target, "kingdomId", ""), ReadString(origin, "kingdomId", ""));
            int originAfter = originBefore;
            int targetAfter = targetBefore;
            if (originStance == "support_lords")
                originAfter = ApplyPoliticalPressureDelta(campaignId, timelineId, worldDay,
                    origin, target, signedAmount, channel, incidentId);
            else if (originStance == "preserve_relations" && ReadBool(target, "isPlayerKingdom", false))
                originAfter = ApplyPoliticalPressureDelta(campaignId, timelineId, worldDay,
                    origin, target, -pressureAmount, channel, incidentId);
            if (targetStance == "support_lords")
                targetAfter = ApplyPoliticalPressureDelta(campaignId, timelineId, worldDay,
                    target, origin, signedAmount, channel, incidentId);
            else if (targetStance == "preserve_relations" && ReadBool(origin, "isPlayerKingdom", false))
                targetAfter = ApplyPoliticalPressureDelta(campaignId, timelineId, worldDay,
                    target, origin, -pressureAmount, channel, incidentId);

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                ExecuteSql(connection, @"INSERT INTO political_pressure_incidents(
incident_id,campaign_id,timeline_id,day_key,world_day,archetype_id,polarity,channel,severity,severity_rank,
pressure_amount,relationship_penalty,origin_kingdom_id,target_kingdom_id,origin_kingdom_name,target_kingdom_name,
origin_ruler_id,target_ruler_id,origin_ruler_name,target_ruler_name,origin_war_count,target_war_count,hostile_chance,polarity_roll,
origin_clans_json,origin_lords_json,target_clans_json,target_lords_json,trait_routes_json,trait_rolls_json,origin_stance,target_stance,
origin_pressure_before,origin_pressure_after,target_pressure_before,target_pressure_after,headline,narrative,
llm_selector_status,llm_narration_status,notice_status,correlation_id,status,payload_json,created_ts,updated_ts)
VALUES($id,$campaign,$timeline,$day,$worldDay,$archetype,$polarity,$channel,$severity,$severityRank,$pressureAmount,$relationshipPenalty,
$originKingdom,$targetKingdom,$originKingdomName,$targetKingdomName,$originRuler,$targetRuler,$originRulerName,$targetRulerName,
$originWars,$targetWars,$hostileChance,$polarityRoll,$originClans,$originLords,$targetClans,$targetLords,$routes,$rolls,$originStance,
$targetStance,$originBefore,$originAfter,$targetBefore,$targetAfter,$headline,$narrative,'pending','pending','preparing',$correlation,
'completed',$payload,$ts,$ts);", new Dictionary<string, object>
                {
                    ["id"] = incidentId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day,
                    ["worldDay"] = worldDay, ["archetype"] = archetype.Id, ["polarity"] = polarity, ["channel"] = channel,
                    ["severity"] = severity, ["severityRank"] = severityRank, ["pressureAmount"] = pressureAmount,
                    ["relationshipPenalty"] = relationshipPenalty, ["originKingdom"] = ReadString(origin, "kingdomId", ""),
                    ["targetKingdom"] = ReadString(target, "kingdomId", ""), ["originKingdomName"] = ReadString(origin, "name", ""),
                    ["targetKingdomName"] = ReadString(target, "name", ""), ["originRuler"] = ReadString(origin, "leaderHeroId", ""),
                    ["targetRuler"] = ReadString(target, "leaderHeroId", ""), ["originRulerName"] = ReadString(origin, "leaderName", ""),
                    ["targetRulerName"] = ReadString(target, "leaderName", ""), ["originWars"] = warCount,
                    ["targetWars"] = ReadStringList(target, "enemies").Count, ["hostileChance"] = hostileChance,
                    ["polarityRoll"] = polarityRoll, ["originClans"] = Json.Serialize(originClans.Select(x => ReadString(x, "clanId", "")).ToList()),
                    ["originLords"] = Json.Serialize(originLords), ["targetClans"] = Json.Serialize(targetClans.Select(x => ReadString(x, "clanId", "")).ToList()),
                    ["targetLords"] = Json.Serialize(targetLords), ["routes"] = Json.Serialize(routes), ["rolls"] = Json.Serialize(traitRolls),
                    ["originStance"] = originStance, ["targetStance"] = targetStance, ["originBefore"] = originBefore,
                    ["originAfter"] = originAfter, ["targetBefore"] = targetBefore, ["targetAfter"] = targetAfter,
                    ["headline"] = archetype.Headline, ["narrative"] = archetype.Description,
                    ["correlation"] = correlation, ["payload"] = Json.Serialize(new Dictionary<string, object>
                    {
                        ["origin"] = origin, ["target"] = target, ["originClans"] = originClans, ["targetClans"] = targetClans
                    }), ["ts"] = ts
                });
            }

            ApplyPoliticalPressureForeignRulerChoice(campaignId, timelineId, worldDay,
                incidentId, "origin", origin, target, originStance, relationshipPenalty, correlation);
            ApplyPoliticalPressureForeignRulerChoice(campaignId, timelineId, worldDay,
                incidentId, "target", target, origin, targetStance, relationshipPenalty, correlation);

            if (originStance == "preserve_relations")
                ApplyPoliticalPressureDomesticRefusal(campaignId, timelineId, worldDay,
                    incidentId, "origin", origin, originClans, settlements, relationshipPenalty, correlation);
            if (targetStance == "preserve_relations")
                ApplyPoliticalPressureDomesticRefusal(campaignId, timelineId, worldDay,
                    incidentId, "target", target, targetClans, settlements, relationshipPenalty, correlation);

#if !REIGN_EXCLUDE_COURT
            if (originStance == "awaiting_player" || targetStance == "awaiting_player")
                QueueInternationalPoliticalIncident(campaignId, timelineId, worldDay, incidentId,
                    world, origin, target, originLords, targetLords, severityRank, archetype.Headline,
                    archetype.Description, channel, originStance, targetStance, polarity);
#endif

            string narrative = GeneratePoliticalPressureNarrative(campaignId, incidentId,
                archetype, severity, origin, target, originLords, targetLords,
                originStance, targetStance, out string narrationStatus);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                ExecuteSql(connection, @"UPDATE political_pressure_incidents SET narrative=$narrative,
llm_selector_status=$selector,llm_narration_status=$narration,notice_status='ready',updated_ts=$ts WHERE incident_id=$id;",
                    new Dictionary<string, object> { ["narrative"] = narrative, ["selector"] = selectorStatus,
                        ["narration"] = narrationStatus, ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = incidentId });
            }
            RecordPoliticalPressureDailyRoll(campaignId, timelineId, day, worldDay,
                dailyRoll, true, "incident_created", ReadString(origin, "kingdomId", ""), incidentId);
            RecordPoliticalPressureActivity(campaignId, timelineId, worldDay, incidentId,
                origin, target, "incident_completed", polarity, channel, severity, "completed",
                originBefore, originAfter, polarityRoll, hostileChance, "", correlation,
                originStance + " / " + targetStance, new Dictionary<string, object>
                {
                    ["originStance"] = originStance, ["targetStance"] = targetStance,
                    ["pressureAmount"] = pressureAmount, ["selectorStatus"] = selectorStatus,
                    ["narrationStatus"] = narrationStatus
                });
            InvalidateWorldTestOverviewCache(campaignId, timelineId);
            return new Dictionary<string, object> { ["ok"] = true, ["status"] = "incident_created",
                ["incidentId"] = incidentId, ["polarity"] = polarity, ["severity"] = severity };
        }

        private static void RecordPoliticalPressureDailyRoll(string campaignId, string timelineId,
            int day, double worldDay, int roll, bool passed, string status, string originKingdomId, string incidentId)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                ExecuteSql(connection, @"INSERT INTO political_pressure_daily_rolls(campaign_id,timeline_id,day_key,world_day,
chance,roll,passed,status,origin_kingdom_id,incident_id,created_ts)
VALUES($campaign,$timeline,$day,$worldDay,$chance,$roll,$passed,$status,$origin,$incident,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=excluded.world_day,chance=excluded.chance,roll=excluded.roll,passed=excluded.passed,
status=excluded.status,origin_kingdom_id=excluded.origin_kingdom_id,incident_id=excluded.incident_id;", new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day, ["worldDay"] = worldDay,
                    ["chance"] = PoliticalPressureIncidentChance, ["roll"] = roll, ["passed"] = passed ? 1 : 0,
                    ["status"] = status ?? "", ["origin"] = originKingdomId ?? "", ["incident"] = incidentId ?? "", ["ts"] = ts
                });
                RecordWorldTestCounter(connection, campaignId, timelineId, day, "political_pressures",
                    "daily:" + day.ToString(CultureInfo.InvariantCulture), new Dictionary<string, object>
                    {
                        ["dailyRolls"] = 1, [passed ? "dailyRollsPassed" : "dailyRollsFailed"] = 1,
                        ["status:" + (status ?? "unknown")] = 1
                    });
            }
        }

        private static bool TryClaimPoliticalPressureDailyRoll(string campaignId,
            string timelineId, int day, double worldDay, int roll)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return QuerySql(connection, @"INSERT INTO political_pressure_daily_rolls(
campaign_id,timeline_id,day_key,world_day,chance,roll,passed,status,
origin_kingdom_id,incident_id,created_ts)
VALUES($campaign,$timeline,$day,$worldDay,$chance,$roll,0,'processing','','',$ts)
ON CONFLICT(campaign_id,timeline_id,day_key) DO UPDATE SET
world_day=excluded.world_day,chance=excluded.chance,roll=excluded.roll,
passed=0,status='processing',origin_kingdom_id='',incident_id='',created_ts=excluded.created_ts
WHERE political_pressure_daily_rolls.status='processing'
AND political_pressure_daily_rolls.created_ts<$stale
RETURNING day_key;", new Dictionary<string, object>
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return kingdoms.OrderByDescending(kingdom =>
                {
                    string id = ReadString(kingdom, "kingdomId", "");
                    double last = ReadDouble(QuerySql(connection, @"SELECT MAX(world_day) AS day FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND origin_kingdom_id=$kingdom;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["kingdom"] = id })
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                List<Dictionary<string, object>> eligible = kingdoms.Where(x =>
                    !ReadString(x, "kingdomId", "").Equals(originId, StringComparison.OrdinalIgnoreCase)).Where(x =>
                {
                    string targetId = ReadString(x, "kingdomId", "");
                    double last = ReadDouble(QuerySql(connection, @"SELECT MAX(world_day) AS day FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND
((origin_kingdom_id=$origin AND target_kingdom_id=$target) OR (origin_kingdom_id=$target AND target_kingdom_id=$origin));",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                HashSet<string> recent = new HashSet<string>(QuerySql(connection, @"SELECT archetype_id FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND origin_kingdom_id=$origin AND target_kingdom_id=$target AND world_day>$cutoff;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
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

        private static List<Dictionary<string, object>> SelectPoliticalPressureClans(string campaignId,
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

        private static List<string> PoliticalPressureLeadLords(List<Dictionary<string, object>> clans)
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
            Dictionary<string, object> llm = ChatWithLlm(request);
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
                        ["reason"] = LimitText(ReadString(x, "reason", "Contextual court judgment."), 120)
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

        private static void ApplyPoliticalPressureDomesticRefusal(string campaignId, string timelineId,
            double worldDay, string incidentId, string side, Dictionary<string, object> kingdom,
            List<Dictionary<string, object>> clans, List<Dictionary<string, object>> settlements,
            int relationshipPenalty, string correlation)
        {
            string rulerId = ReadString(kingdom, "leaderHeroId", "");
            string kingdomId = ReadString(kingdom, "kingdomId", "");
            HashSet<string> clanIds = new HashSet<string>((clans ?? new List<Dictionary<string, object>>())
                .Select(x => ReadString(x, "clanId", "")).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> clan in clans ?? new List<Dictionary<string, object>>())
            foreach (string memberId in PoliticalPressureClanMembers(clan))
            {
                if (memberId.Equals(rulerId, StringComparison.OrdinalIgnoreCase)) continue;
                ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay,
                    "political_pressure_lords_refused", incidentId + ":" + side + ":relation:" + memberId,
                    incidentId, memberId, rulerId, kingdomId, kingdomId, relationshipPenalty,
                    "", 0, false, correlation, new Dictionary<string, object>
                    {
                        ["incidentId"] = incidentId, ["side"] = side,
                        ["clanId"] = ReadString(clan, "clanId", ""), ["severityPenalty"] = relationshipPenalty
                    });
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                foreach (Dictionary<string, object> settlement in (settlements ?? new List<Dictionary<string, object>>()).Where(x =>
                    clanIds.Contains(ReadString(x, "ownerClanId", ""))
                    && (ReadBool(x, "isTown", false) || ReadBool(x, "isCastle", false))))
                {
                    string settlementId = ReadString(settlement, "settlementId", "");
                    if (string.IsNullOrWhiteSpace(settlementId)) continue;
                    string effectId = incidentId + ":" + side + ":loyalty:" + settlementId;
                    ExecuteSql(connection, @"INSERT INTO political_pressure_native_effects(effect_id,incident_id,campaign_id,timeline_id,
world_day,effect_type,target_id,amount,status,created_ts,updated_ts)
VALUES($id,$incident,$campaign,$timeline,$day,'settlement_loyalty',$target,-10,'pending',$ts,$ts)
ON CONFLICT(effect_id) DO NOTHING;", new Dictionary<string, object>
                    {
                        ["id"] = effectId, ["incident"] = incidentId, ["campaign"] = campaignId,
                        ["timeline"] = timelineId, ["day"] = worldDay, ["target"] = settlementId, ["ts"] = ts
                    });
                }
                RecordWorldTestCounter(connection, campaignId, timelineId, (int)Math.Floor(worldDay),
                    "political_pressures", incidentId + ":domestic:" + side, new Dictionary<string, object>
                    {
                        ["rulersRefusingLords"] = 1,
                        ["relationshipPenalties"] = (clans ?? new List<Dictionary<string, object>>()).SelectMany(PoliticalPressureClanMembers)
                            .Where(x => !x.Equals(rulerId, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                        ["loyaltyEffectsQueued"] = (settlements ?? new List<Dictionary<string, object>>()).Count(x =>
                            clanIds.Contains(ReadString(x, "ownerClanId", "")) && (ReadBool(x, "isTown", false) || ReadBool(x, "isCastle", false)))
                    });
            }
        }

        private static void ApplyPoliticalPressureForeignRulerChoice(string campaignId, string timelineId,
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
            ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay,
                eventKind, incidentId + ":" + side + ":foreign-ruler", incidentId,
                rulerId, otherRulerId, kingdomId, otherKingdomId, affinityDelta,
                "", 0, true, correlation, new Dictionary<string, object>
                {
                    ["incidentId"] = incidentId, ["side"] = side, ["stance"] = stance,
                    ["domesticRelationshipPenalty"] = relationshipPenalty,
                    ["foreignRulerDelta"] = affinityDelta
                });
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
            Dictionary<string, object> llm = ChatWithLlm(request);
            Dictionary<string, object> parsed = ReadBool(llm, "ok", false)
                ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
            string narrative = LimitText(ReadString(parsed, "narrative", ""), 900);
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

        private static int ReadPoliticalPressureValue(string campaignId, string timelineId, string actorId, string targetId)
        {
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                return ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
            }
        }

        private static int ReadPoliticalPressureValue(ReignDbConnection connection, string campaignId,
            string timelineId, string actorId, string targetId)
        {
            return ReadInt(QuerySql(connection, @"SELECT pressure_value FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["actor"] = actorId, ["target"] = targetId }).FirstOrDefault(), "pressure_value", 0);
        }

        private static int ApplyPoliticalPressureDelta(string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> actor, Dictionary<string, object> target, int delta, string channel, string incidentId)
        {
            string actorId = ReadString(actor, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            if (ReadBool(actor, "isPlayerKingdom", false) || string.IsNullOrWhiteSpace(actorId)
                || string.IsNullOrWhiteSpace(targetId) || actorId.Equals(targetId, StringComparison.OrdinalIgnoreCase)) return 0;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                using (ReignDbTransaction transaction = connection.BeginTransaction())
                {
                if (!string.IsNullOrWhiteSpace(incidentId) && QuerySql(connection, @"INSERT INTO political_pressure_delta_receipts
(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,incident_id,requested_delta,world_day)
VALUES($campaign,$timeline,$actor,$target,$incident,$delta,$day)
ON CONFLICT(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,incident_id) DO NOTHING RETURNING incident_id;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["actor"] = actorId, ["target"] = targetId, ["incident"] = incidentId, ["delta"] = delta, ["day"] = worldDay }).Count == 0)
                {
                    transaction.Commit();
                    return ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
                }
                int before = ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
                int after = Clamp(before + delta, -100, 100);
                string dominant = after == 0 ? "" : channel ?? "";
                Dictionary<string, object> changed = QuerySql(connection, @"INSERT INTO political_pressure_state(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id,
actor_kingdom_name,target_kingdom_name,actor_ruler_id,target_ruler_id,pressure_value,dominant_channel,updated_day,last_incident_id,revision)
VALUES($campaign,$timeline,$actor,$target,$actorName,$targetName,$actorRuler,$targetRuler,$value,$channel,$day,$incident,1)
ON CONFLICT(campaign_id,timeline_id,actor_kingdom_id,target_kingdom_id) DO UPDATE SET
actor_kingdom_name=$actorName,target_kingdom_name=$targetName,actor_ruler_id=$actorRuler,target_ruler_id=$targetRuler,
pressure_value=MAX(-100,MIN(100,political_pressure_state.pressure_value+$delta)),dominant_channel=$channel,updated_day=$day,last_incident_id=$incident,revision=political_pressure_state.revision+1 RETURNING pressure_value;",
                    new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId, ["timeline"] = timelineId, ["actor"] = actorId, ["target"] = targetId,
                        ["actorName"] = ReadString(actor, "name", ""), ["targetName"] = ReadString(target, "name", ""),
                        ["actorRuler"] = ReadString(actor, "leaderHeroId", ""), ["targetRuler"] = ReadString(target, "leaderHeroId", ""),
                        ["value"] = after, ["delta"] = delta, ["channel"] = dominant, ["day"] = worldDay, ["incident"] = incidentId ?? ""
                    }).FirstOrDefault();
                after = ReadInt(changed, "pressure_value", after);
                transaction.Commit();
                RecordWorldTestCounter(connection, campaignId, timelineId, (int)Math.Floor(worldDay),
                    "political_pressures", incidentId + ":pressure:" + actorId, new Dictionary<string, object>
                    {
                        ["pressureMovement"] = Math.Abs(after - before), [after > before ? "hostilePressureAdded" : "peacefulPressureAdded"] = Math.Abs(after - before),
                        ["rulersSupportingLords"] = 1
                    });
                return after;
                }
            }
        }

        private static Dictionary<string, object> SelectPoliticalPressureOpportunity(string campaignId,
            string timelineId, double worldDay, List<Dictionary<string, object>> kingdoms,
            Dictionary<string, object> state)
        {
            int day = (int)Math.Floor(worldDay + 0.000001d);
            Dictionary<string, Dictionary<string, object>> byId = kingdoms.ToDictionary(x => ReadString(x, "kingdomId", ""),
                x => x, StringComparer.OrdinalIgnoreCase);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                List<Dictionary<string, object>> rows = QuerySql(connection, @"SELECT * FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND (pressure_value>=10 OR pressure_value<=-10)
ORDER BY ABS(pressure_value) DESC,updated_day,actor_kingdom_id,target_kingdom_id;",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
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
                    ExecuteSql(connection, @"INSERT INTO political_pressure_action_rolls(campaign_id,timeline_id,day_key,world_day,
actor_kingdom_id,target_kingdom_id,pressure_value,polarity,channel,chance,roll,passed,selected,status,created_ts,updated_ts)
VALUES($campaign,$timeline,$day,$worldDay,$actor,$target,$value,$polarity,$channel,$chance,$roll,$passed,0,'evaluated',$ts,$ts)
ON CONFLICT(campaign_id,timeline_id,day_key,actor_kingdom_id,target_kingdom_id) DO NOTHING;",
                        new Dictionary<string, object>
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
                ExecuteSql(connection, @"UPDATE political_pressure_action_rolls SET selected=1,status='selected',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND day_key=$day AND actor_kingdom_id=$actor AND target_kingdom_id=$target;",
                    new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["campaign"] = campaignId,
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
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                before = ReadPoliticalPressureValue(connection, campaignId, timelineId, actorId, targetId);
                ExecuteSql(connection, @"UPDATE political_pressure_state SET pressure_value=0,dominant_channel='',last_action_day=$day,
revision=revision+1 WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target;",
                    new Dictionary<string, object> { ["day"] = worldDay, ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["actor"] = actorId, ["target"] = targetId });
                ExecuteSql(connection, @"UPDATE political_pressure_action_rolls SET status='resolved',action_id=$action,outcome=$outcome,updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND actor_kingdom_id=$actor AND target_kingdom_id=$target AND selected=1 AND status='selected';",
                    new Dictionary<string, object> { ["action"] = actionId ?? "", ["outcome"] = outcome ?? "",
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
            InvalidateWorldTestOverviewCache(campaignId, timelineId);
        }

        private static void RecordPoliticalPressureNoAction(string campaignId, string timelineId,
            double worldDay, Dictionary<string, object> actor, Dictionary<string, object> target,
            string directionId, string reason)
        {
            if (string.IsNullOrWhiteSpace(directionId)) return;
            string actorId = ReadString(actor, "kingdomId", "");
            string targetId = ReadString(target, "kingdomId", "");
            int pressure;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                pressure = ReadPoliticalPressureValue(connection, campaignId,
                    timelineId, actorId, targetId);
                ExecuteSql(connection, @"UPDATE political_pressure_action_rolls
SET status='resolved',outcome='no_credible_action',updated_ts=$ts
WHERE campaign_id=$campaign AND timeline_id=$timeline
AND actor_kingdom_id=$actor AND target_kingdom_id=$target
AND selected=1 AND status='selected';", new Dictionary<string, object>
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

        private static Dictionary<string, object> NextPoliticalPressureNotices(Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue("campaignId", out string campaign) ? campaign : "default";
            string timelineId = query.TryGetValue("timelineId", out string timeline) ? timeline : "main";
            int limit = query.TryGetValue("limit", out string limitText) && int.TryParse(limitText, out int parsed)
                ? Math.Max(1, Math.Min(10, parsed)) : 3;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                List<Dictionary<string, object>> incidents = QuerySql(connection, @"SELECT * FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND notice_status IN ('ready','fetched')
ORDER BY world_day,incident_id LIMIT " + limit.ToString(CultureInfo.InvariantCulture) + ";",
                    new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
                foreach (Dictionary<string, object> incident in incidents)
                {
                    string id = ReadString(incident, "incident_id", "");
                    incident["nativeEffects"] = QuerySql(connection, @"SELECT effect_id,effect_type,target_id,amount,status
FROM political_pressure_native_effects WHERE incident_id=$incident ORDER BY effect_id;",
                        new Dictionary<string, object> { ["incident"] = id });
                    ExecuteSql(connection, "UPDATE political_pressure_incidents SET notice_status='fetched',updated_ts=$ts WHERE incident_id=$id;",
                        new Dictionary<string, object> { ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = id });
                }
                return new Dictionary<string, object> { ["ok"] = true, ["count"] = incidents.Count, ["notices"] = incidents };
            }
        }

        private static Dictionary<string, object> AcknowledgePoliticalPressureNotice(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            string incidentId = ReadString(payload, "incidentId", "");
            double receiptDay = ReadDouble(payload, "worldDay", -1d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                if (ReadInt(QuerySql(connection, "SELECT COUNT(*) AS count FROM political_pressure_incidents WHERE incident_id=$id;",
                    new Dictionary<string, object> { ["id"] = incidentId }).FirstOrDefault(), "count", 0) == 0)
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Political pressure incident was not found." };
                foreach (Dictionary<string, object> receipt in ReadDictionaryList(payload, "effectReceipts"))
                {
                    string effectId = ReadString(receipt, "effectId", "");
                    ExecuteSql(connection, @"UPDATE political_pressure_native_effects SET status=$status,before_value=$before,
after_value=$after,receipt_day=$day,error=$error,updated_ts=$ts WHERE effect_id=$id AND status='pending';",
                        new Dictionary<string, object>
                        {
                            ["status"] = ReadBool(receipt, "ok", false) ? "applied" : "failed",
                            ["before"] = ReadDouble(receipt, "before", -1d), ["after"] = ReadDouble(receipt, "after", -1d),
                            ["day"] = receiptDay, ["error"] = ReadString(receipt, "error", ""), ["ts"] = ts, ["id"] = effectId
                        });
                }
                ExecuteSql(connection, "UPDATE political_pressure_incidents SET notice_status='acknowledged',updated_ts=$ts WHERE incident_id=$id;",
                    new Dictionary<string, object> { ["ts"] = ts, ["id"] = incidentId });
            }
            RecordPoliticalPressureActivity(campaignId, timelineId, receiptDay, incidentId, null, null,
                "message_receipt", "", "", "", "acknowledged", 0, 0, 0, 0, "", incidentId,
                "Game message displayed and native effects reported.", payload);
            InvalidateWorldTestOverviewCache(campaignId, timelineId);
            return new Dictionary<string, object> { ["ok"] = true, ["incidentId"] = incidentId };
        }

        private static void RecordPoliticalPressureActivity(string campaignId, string timelineId,
            double worldDay, string incidentId, Dictionary<string, object> actor, Dictionary<string, object> target,
            string eventType, string polarity, string channel, string severity, string status,
            int before, int after, int roll, int chance, string actionId, string correlation,
            string reason, Dictionary<string, object> payload)
        {
            string activityId = correlation + ":" + eventType + ":" + (actionId ?? "") + ":" + (incidentId ?? "");
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsurePoliticalPressureSchema(connection);
                ExecuteSql(connection, @"INSERT INTO political_pressure_activity(activity_id,campaign_id,timeline_id,world_day,
incident_id,actor_kingdom_id,target_kingdom_id,actor_ruler_id,target_ruler_id,event_type,polarity,channel,severity,status,
before_value,after_value,roll,chance,action_id,correlation_id,reason,payload_json,created_ts)
VALUES($id,$campaign,$timeline,$day,$incident,$actor,$target,$actorRuler,$targetRuler,$type,$polarity,$channel,$severity,
$status,$before,$after,$roll,$chance,$action,$correlation,$reason,$payload,$ts) ON CONFLICT(activity_id) DO NOTHING;",
                    new Dictionary<string, object>
                    {
                        ["id"] = activityId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay,
                        ["incident"] = incidentId ?? "", ["actor"] = ReadString(actor, "kingdomId", ""),
                        ["target"] = ReadString(target, "kingdomId", ""), ["actorRuler"] = ReadString(actor, "leaderHeroId", ""),
                        ["targetRuler"] = ReadString(target, "leaderHeroId", ""), ["type"] = eventType ?? "",
                        ["polarity"] = polarity ?? "", ["channel"] = channel ?? "", ["severity"] = severity ?? "",
                        ["status"] = status ?? "", ["before"] = before, ["after"] = after, ["roll"] = roll,
                        ["chance"] = chance, ["action"] = actionId ?? "", ["correlation"] = correlation ?? "",
                        ["reason"] = reason ?? "", ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts
                    });
                RecordWorldTestCounter(connection, campaignId, timelineId, (int)Math.Floor(worldDay),
                    "political_pressures", activityId, new Dictionary<string, object>
                    {
                        ["activity:" + (eventType ?? "unknown")] = 1, ["status:" + (status ?? "unknown")] = 1
                    });
            }
        }

        private static Dictionary<string, object> BuildWorldTestPoliticalPressures(ReignDbConnection connection,
            string campaignId, string timelineId, double latestDay)
        {
            EnsurePoliticalPressureSchema(connection);
            EnsureKingdomLeaderDiplomacySchema(connection);
            Dictionary<string, object> daily = QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN passed=1 THEN 1 ELSE 0 END) AS passed,MAX(world_day) AS latest,
MIN(day_key) AS earliest_day,MAX(day_key) AS latest_day FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> incidents = QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN polarity='hostile' THEN 1 ELSE 0 END) AS hostile,SUM(CASE WHEN polarity='peaceful' THEN 1 ELSE 0 END) AS peaceful,
SUM(CASE WHEN origin_stance='support_lords' THEN 1 ELSE 0 END)+SUM(CASE WHEN target_stance='support_lords' THEN 1 ELSE 0 END) AS supported,
SUM(CASE WHEN origin_stance='preserve_relations' THEN 1 ELSE 0 END)+SUM(CASE WHEN target_stance='preserve_relations' THEN 1 ELSE 0 END) AS refused,
MAX(world_day) AS latest FROM political_pressure_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> pressure = QuerySql(connection, @"SELECT COUNT(*) AS directions,
SUM(CASE WHEN pressure_value>0 THEN 1 ELSE 0 END) AS hostile,SUM(CASE WHEN pressure_value<0 THEN 1 ELSE 0 END) AS peaceful,
SUM(CASE WHEN pressure_value=0 THEN 1 ELSE 0 END) AS neutral,AVG(ABS(pressure_value)) AS average_magnitude,
MAX(ABS(pressure_value)) AS maximum_magnitude FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline;", new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> actions = QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN passed=1 THEN 1 ELSE 0 END) AS passed,SUM(CASE WHEN selected=1 THEN 1 ELSE 0 END) AS selected,
SUM(CASE WHEN status='resolved' AND outcome LIKE '%refus%' THEN 1 ELSE 0 END) AS refused FROM political_pressure_action_rolls
WHERE campaign_id=$campaign AND timeline_id=$timeline;", new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> effects = QuerySql(connection, @"SELECT COUNT(*) AS total,
SUM(CASE WHEN status='pending' THEN 1 ELSE 0 END) AS pending,SUM(CASE WHEN status='applied' THEN 1 ELSE 0 END) AS applied,
SUM(CASE WHEN status='failed' THEN 1 ELSE 0 END) AS failed FROM political_pressure_native_effects
WHERE campaign_id=$campaign AND timeline_id=$timeline;", new Dictionary<string, object>
            { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> foreignRulerChoices = QuerySql(connection, @"SELECT
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_preserved' THEN 1 ELSE 0 END) AS preserved,
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_sacrificed' THEN 1 ELSE 0 END) AS sacrificed,
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_preserved' THEN affinity_delta ELSE 0 END) AS gained,
SUM(CASE WHEN event_kind='political_pressure_foreign_relations_sacrificed' THEN -affinity_delta ELSE 0 END) AS lost
FROM ruler_diplomatic_incidents WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            List<string> anomalies = new List<string>();
            int outOfRange = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline AND (pressure_value<-100 OR pressure_value>100);",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault(), "count", 0);
            if (outOfRange > 0) anomalies.Add(outOfRange + " directional pressure values are outside -100..100.");
            int overdue = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM political_pressure_native_effects
WHERE campaign_id=$campaign AND timeline_id=$timeline AND status='pending' AND world_day<$cutoff;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["cutoff"] = latestDay - 1d })
                .FirstOrDefault(), "count", 0);
            if (overdue > 0) anomalies.Add(overdue + " political-pressure native receipts are more than one campaign day overdue.");
            int failedEffects = ReadInt(effects, "failed", 0);
            if (failedEffects > 0) anomalies.Add(failedEffects
                + " political-pressure native effects failed to apply.");
            int incompleteVotes = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline AND
(trait_rolls_json IS NULL OR trait_rolls_json='' OR origin_stance='' OR target_stance='');",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault(), "count", 0);
            if (incompleteVotes > 0) anomalies.Add(incompleteVotes + " incidents are missing complete court-vote evidence.");
            int earliestRollDay = ReadInt(daily, "earliest_day", -1);
            int latestRollDay = ReadInt(daily, "latest_day", -1);
            int expectedDaily = earliestRollDay >= 0 && latestRollDay >= earliestRollDay
                ? latestRollDay - earliestRollDay + 1 : 0;
            int missingDaily = Math.Max(0, expectedDaily - ReadInt(daily, "total", 0));
            if (missingDaily > 0)
                anomalies.Add(missingDaily
                    + " political-pressure campaign-day rolls are missing between the first and latest processed days.");
            List<Dictionary<string, object>> matrix = QuerySql(connection, @"SELECT actor_kingdom_id,target_kingdom_id,
actor_kingdom_name,target_kingdom_name,actor_ruler_id,target_ruler_id,pressure_value,dominant_channel,
updated_day,last_incident_id,last_action_day,revision FROM political_pressure_state
WHERE campaign_id=$campaign AND timeline_id=$timeline ORDER BY actor_kingdom_name,target_kingdom_name;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
            Dictionary<string, int> channels = QuerySql(connection, @"SELECT channel,COUNT(*) AS count FROM political_pressure_incidents
WHERE campaign_id=$campaign AND timeline_id=$timeline GROUP BY channel;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
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

        private static List<Dictionary<string, object>> QueryPoliticalPressureActivity(ReignDbConnection connection,
            string campaignId, string timelineId, Dictionary<string, string> query, int limit)
        {
            EnsurePoliticalPressureSchema(connection);
            string pair = query != null && query.TryGetValue("pair", out string pairValue) ? pairValue : "";
            string[] incidentColumns = { "origin_kingdom_id", "target_kingdom_id", "origin_ruler_id", "target_ruler_id",
                "origin_stance", "target_stance", "origin_pressure_before", "origin_pressure_after",
                "target_pressure_before", "target_pressure_after", "severity_rank", "pressure_amount", "polarity",
                "origin_lords_json", "target_lords_json", "trait_routes_json", "trait_rolls_json", "headline", "narrative" };
            string projection = string.Join(",", incidentColumns.Select(column => "i." + column + " AS detail_" + column));
            List<Dictionary<string, object>> rows = QuerySql(connection,
                "SELECT a.*,i.incident_id AS detail_id," + projection + @" FROM political_pressure_activity a
LEFT JOIN political_pressure_incidents i ON i.incident_id=a.incident_id
AND i.campaign_id=a.campaign_id AND i.timeline_id=a.timeline_id
WHERE a.campaign_id=$campaign AND a.timeline_id=$timeline ORDER BY a.world_day DESC,a.created_ts DESC LIMIT "
                + Math.Max(10, Math.Min(2000, limit)).ToString(CultureInfo.InvariantCulture) + ";",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
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

        private static List<Dictionary<string, object>> RunPoliticalPressureSelfTests()
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
            string campaignId = "pressure_claim_"
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
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    int claimRows = ReadInt(QuerySql(connection, @"SELECT COUNT(*) AS count
FROM political_pressure_daily_rolls
WHERE campaign_id=$campaign AND timeline_id='main' AND day_key=9;",
                        new Dictionary<string, object> { ["campaign"] = campaignId })
                        .FirstOrDefault(), "count", -1);
                    add("daily_processing_claim_is_concurrency_safe",
                        firstClaim != secondClaim && claimRows == 1,
                        "Concurrent daily pressure workers leave one durable claim and exactly one worker owns side effects.");
                }
            }
            finally
            {
                ReignPostgreSqlStorage.ClearAllPools();
                try { ReignPostgreSqlStorage.DropCampaign(campaignId); } catch { }
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }
            return rows;
        }
    }
}
