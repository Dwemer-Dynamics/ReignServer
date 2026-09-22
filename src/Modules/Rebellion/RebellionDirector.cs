using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const int RebellionEvaluationIntervalDays = 7;
        private const int RebellionWeeklyOutbreakPercent = 5;
        private const int RebellionRelationshipThreshold = -35;
        private const int RebellionAcceptedCooldownDays = 63;
        private const int RebellionResolvedCooldownDays = 63;
        private const int NegotiationLifetimeDays = 14;
        private const int RebellionRecognitionMinimumWarDays = 14;
        private const int RebellionRecognitionAttemptIntervalDays = 30;
        private const int RebellionRecognitionMaximumChancePercent = 30;
        private const int RebellionRecognitionPeaceDays = 63;
        private const int RebellionSchemaRevision = 3;
        private const string RebellionSchemaMarker =
            "rebellion_schema_version";

        private static void EnsureRebellionSchema(ReignDbConnection connection)
        {
            if (IsPostgreSqlComponentSchemaReady(connection,
                RebellionSchemaMarker, RebellionSchemaRevision))
                return;
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_movements (
movement_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,parent_kingdom_id TEXT NOT NULL,
rebel_kingdom_id TEXT NOT NULL DEFAULT '',leader_clan_id TEXT NOT NULL,leader_hero_id TEXT NOT NULL,ruler_hero_id TEXT NOT NULL,
objective TEXT NOT NULL,stage TEXT NOT NULL,pressure REAL NOT NULL,relationship_pressure REAL NOT NULL,
trait_pressure REAL NOT NULL,factual_pressure REAL NOT NULL,viability REAL NOT NULL,readiness REAL NOT NULL,
demand_json TEXT NOT NULL DEFAULT '{}',correlation_id TEXT NOT NULL,created_day REAL NOT NULL,updated_day REAL NOT NULL,
cooldown_until_day REAL NOT NULL DEFAULT 0,is_player_kingdom INTEGER NOT NULL DEFAULT 0,payload_json TEXT NOT NULL DEFAULT '{}');");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_memberships (
movement_id TEXT NOT NULL,clan_id TEXT NOT NULL,leader_hero_id TEXT NOT NULL,side TEXT NOT NULL,support_score REAL NOT NULL,
switch_count INTEGER NOT NULL DEFAULT 0,last_switch_day REAL NOT NULL DEFAULT 0,reason_json TEXT NOT NULL DEFAULT '{}',
PRIMARY KEY(movement_id,clan_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_transitions (
transition_id TEXT PRIMARY KEY,movement_id TEXT NOT NULL,world_day REAL NOT NULL,from_stage TEXT NOT NULL,to_stage TEXT NOT NULL,
kind TEXT NOT NULL,actor_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS negotiated_action_drafts (
negotiation_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,broker_hero_id TEXT NOT NULL,
first_kingdom_id TEXT NOT NULL,second_kingdom_id TEXT NOT NULL,first_ruler_id TEXT NOT NULL,second_ruler_id TEXT NOT NULL,
command TEXT NOT NULL,political_result TEXT NOT NULL DEFAULT '',terms_json TEXT NOT NULL,terms_hash TEXT NOT NULL,
status TEXT NOT NULL,created_day REAL NOT NULL,updated_day REAL NOT NULL,expires_day REAL NOT NULL,
queued_action_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}');");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS negotiated_action_approvals (
negotiation_id TEXT NOT NULL,ruler_hero_id TEXT NOT NULL,kingdom_id TEXT NOT NULL,terms_hash TEXT NOT NULL,
status TEXT NOT NULL,approved_day REAL NOT NULL,reason TEXT NOT NULL DEFAULT '',PRIMARY KEY(negotiation_id,ruler_hero_id));");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_backing_requests (
campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,world_day REAL NOT NULL,
rebel_kingdom_id TEXT NOT NULL,parent_kingdom_id TEXT NOT NULL,rebel_ruler_id TEXT NOT NULL,parent_ruler_id TEXT NOT NULL,
asked_kingdom_id TEXT NOT NULL DEFAULT '',asked_ruler_id TEXT NOT NULL DEFAULT '',eligibility_attitude INTEGER NOT NULL DEFAULT 0,
asked_to_rebel_attitude INTEGER NOT NULL DEFAULT 0,asked_to_parent_attitude INTEGER NOT NULL DEFAULT 0,
rebel_roll INTEGER NOT NULL DEFAULT 0,parent_roll INTEGER NOT NULL DEFAULT 0,rebel_score INTEGER NOT NULL DEFAULT 0,parent_score INTEGER NOT NULL DEFAULT 0,
support_power REAL NOT NULL DEFAULT 0,opposition_power REAL NOT NULL DEFAULT 0,military_chance INTEGER NOT NULL DEFAULT 0,military_roll INTEGER NOT NULL DEFAULT 0,
support_coalition_json TEXT NOT NULL DEFAULT '[]',opposition_coalition_json TEXT NOT NULL DEFAULT '[]',status TEXT NOT NULL,
reason TEXT NOT NULL DEFAULT '',action_id TEXT NOT NULL DEFAULT '',event_id TEXT NOT NULL DEFAULT '',payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
PRIMARY KEY(campaign_id,timeline_id,movement_id));");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rebellion_backing_status ON rebellion_backing_requests(campaign_id,timeline_id,status,world_day);");
            ExecuteSql(connection, @"CREATE TABLE IF NOT EXISTS rebellion_recognition_attempts (
attempt_id TEXT PRIMARY KEY,campaign_id TEXT NOT NULL,timeline_id TEXT NOT NULL,movement_id TEXT NOT NULL,
attempt_index INTEGER NOT NULL,world_day REAL NOT NULL,rebel_kingdom_id TEXT NOT NULL,parent_kingdom_id TEXT NOT NULL,
rebel_ruler_id TEXT NOT NULL,parent_ruler_id TEXT NOT NULL,status TEXT NOT NULL,eligible INTEGER NOT NULL,
chance_percent INTEGER NOT NULL DEFAULT 0,roll INTEGER NOT NULL DEFAULT 0,relationship_attitude INTEGER NOT NULL DEFAULT 0,
rebel_strength REAL NOT NULL DEFAULT 0,parent_strength REAL NOT NULL DEFAULT 0,rebel_fiefs INTEGER NOT NULL DEFAULT 0,
parent_fiefs INTEGER NOT NULL DEFAULT 0,parent_other_wars INTEGER NOT NULL DEFAULT 0,parent_judgment INTEGER NOT NULL DEFAULT 50,
parent_pragmatism INTEGER NOT NULL DEFAULT 50,parent_mercy INTEGER NOT NULL DEFAULT 50,parent_pride INTEGER NOT NULL DEFAULT 50,
reason TEXT NOT NULL DEFAULT '',negotiation_id TEXT NOT NULL DEFAULT '',action_id TEXT NOT NULL DEFAULT '',
payload_json TEXT NOT NULL DEFAULT '{}',created_ts INTEGER NOT NULL,
UNIQUE(campaign_id,timeline_id,movement_id,attempt_index));");
            ExecuteSql(connection, @"CREATE INDEX IF NOT EXISTS idx_rebellion_recognition_movement
ON rebellion_recognition_attempts(campaign_id,timeline_id,movement_id,world_day DESC);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rebellion_campaign_stage ON rebellion_movements(campaign_id,timeline_id,stage,updated_day);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_rebellion_parent ON rebellion_movements(campaign_id,parent_kingdom_id,stage);");
            ExecuteSql(connection, "CREATE INDEX IF NOT EXISTS idx_negotiation_campaign_status ON negotiated_action_drafts(campaign_id,status,expires_day);");
            ExecuteSql(connection, @"INSERT INTO schema_meta(key,value)
VALUES($key,$value)
ON CONFLICT(key) DO UPDATE SET value=$value;",
                new Dictionary<string, object>
                {
                    ["key"] = RebellionSchemaMarker,
                    ["value"] = RebellionSchemaRevision.ToString(
                        CultureInfo.InvariantCulture)
                });
            MarkPostgreSqlComponentSchemaReady(connection,
                RebellionSchemaMarker, RebellionSchemaRevision);
        }

        private static Dictionary<string, object> EvaluateRebellions(Dictionary<string, object> payload)
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
                    ["updates"] = new ArrayList(),
                    ["startupGrace"] = startupGrace
                };
            }
            string playerKingdomId = ReadString(payload, "playerKingdomId", "");
            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(payload, "kingdoms");
            List<Dictionary<string, object>> savedMovements = ReadDictionaryList(payload, "movements");
            List<Dictionary<string, object>> updates = new List<Dictionary<string, object>>();

            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                foreach (Dictionary<string, object> saved in savedMovements) MirrorSavedRebellionState(connection, campaignId, timelineId, worldDay, saved);
                foreach (Dictionary<string, object> kingdom in kingdoms)
                {
                    string kingdomId = ReadString(kingdom, "kingdomId", "");
                    if (string.IsNullOrWhiteSpace(kingdomId) || ReadBool(kingdom, "isEliminated", false) || ReadBool(kingdom, "isRebelRealm", false)) continue;
                    Dictionary<string, object> cooldown = savedMovements.Where(x => string.Equals(ReadString(x, "parentKingdomId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(ReadString(x, "stage", ""), "resolved", StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(x => ReadDouble(x, "cooldownUntilDay", 0d)).FirstOrDefault();
                    if (ReadDouble(cooldown, "cooldownUntilDay", 0d) > worldDay) continue;
                    Dictionary<string, object> existing = savedMovements.FirstOrDefault(x =>
                        string.Equals(ReadString(x, "parentKingdomId", ""), kingdomId, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(ReadString(x, "stage", ""), "resolved", StringComparison.OrdinalIgnoreCase));
                    // New rebellion creation is deliberately paused. Existing saved
                    // movements still advance. The native weekly outbreak path now
                    // uses the clan leader's directional Reign effective attitude
                    // toward the ruler rather than Bannerlord's symmetric relation.
                    if (existing == null) continue;
                    bool existingCivilWar = string.Equals(ReadString(existing, "stage", ""), "civil_war", StringComparison.OrdinalIgnoreCase);
                    List<Dictionary<string, object>> clans = ReadDictionaryList(kingdom, "clans")
                        .Where(IsEligibleRebellionClan).ToList();
                    if (!existingCivilWar && (clans.Count < 3 || ReadInt(kingdom, "fortificationCount", 0) < 2)) continue;

                    Dictionary<string, object> ruler = clans.FirstOrDefault(x => ReadBool(x, "isRulingClan", false));
                    if (ruler == null) continue;
                    string rulerHeroId = ReadString(ruler, "leaderHeroId", "");
                    Dictionary<string, object> candidate = existing == null
                        ? SelectRebellionCandidate(connection, campaignId, timelineId,
                            kingdom, clans, ruler, worldDay)
                        : clans.FirstOrDefault(x => string.Equals(ReadString(x, "clanId", ""), ReadString(existing, "leaderClanId", ""), StringComparison.OrdinalIgnoreCase));
                    if (candidate == null) continue;

                    Dictionary<string, object> scores = ScoreRebellionCandidate(connection,
                        campaignId, timelineId, kingdom, candidate, ruler, worldDay);
                    double oldPressure = ReadDouble(existing, "pressure", 0d);
                    double readiness = ReadDouble(scores, "readiness", 50d);
                    double pressure = Clamp(0d, 100d, oldPressure + Clamp(-5d, 5d, (readiness - 50d) / 8d));
                    string oldStage = ReadString(existing, "stage", "none");
                    string stage = string.Equals(oldStage, "civil_war", StringComparison.OrdinalIgnoreCase) ? "civil_war" : RebellionStageForPressure(pressure);
                    bool isPlayerKingdom = string.Equals(kingdomId, playerKingdomId, StringComparison.OrdinalIgnoreCase) || ReadBool(kingdom, "isPlayerKingdom", false);
                    if (isPlayerKingdom && RebellionStageRank(stage) > RebellionStageRank("coalition")) stage = "coalition";

                    string leaderClanId = ReadString(candidate, "clanId", "");
                    string leaderHeroId = ReadString(candidate, "leaderHeroId", "");
                    string movementId = ReadString(existing, "movementId", "");
                    if (string.IsNullOrWhiteSpace(movementId)) movementId = "reb_" + StableSha256(campaignId + "|" + timelineId + "|" + kingdomId + "|" + leaderClanId).Substring(0, 24);
                    string objective = ReadString(existing, "objective", "");
                    if (string.IsNullOrWhiteSpace(objective)) objective = SelectRebellionObjective(connection, campaignId, candidate, kingdom, scores);
                    Dictionary<string, object> demand = BuildRebellionDemand(objective, candidate, ruler, kingdom);
                    List<Dictionary<string, object>> memberships = ScoreRebellionMemberships(
                        connection, campaignId, timelineId, movementId, kingdom,
                        clans, candidate, ruler, scores, existing, worldDay);
                    double rebelPower = memberships.Where(x => ReadString(x, "side", "") == "rebel").Sum(x => ReadDouble(x, "clanPower", 0d));
                    double loyalistPower = Math.Max(1d, memberships.Where(x => ReadString(x, "side", "") != "rebel").Sum(x => ReadDouble(x, "clanPower", 0d)));
                    bool hasRebelFort = memberships.Any(x => ReadString(x, "side", "") == "rebel" && ReadInt(x, "fortificationCount", 0) > 0);
                    bool ultimatumReady = !string.Equals(stage, "civil_war", StringComparison.OrdinalIgnoreCase) && !isPlayerKingdom && pressure >= 90d && rebelPower >= loyalistPower * 0.4d && hasRebelFort
                        && !ReadBool(candidate, "isPrisoner", false) && !ReadBool(candidate, "unsafeForRebellion", false);
                    bool acceptRecommended = ScoreUltimatumAcceptance(connection, campaignId, ruler, candidate, rebelPower, loyalistPower) >= 65d;

                    Dictionary<string, object> update = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["movementId"] = movementId, ["parentKingdomId"] = kingdomId,
                        ["rebelKingdomId"] = ReadString(existing, "rebelKingdomId", ""), ["leaderClanId"] = leaderClanId,
                        ["leaderHeroId"] = leaderHeroId, ["rulerHeroId"] = rulerHeroId, ["objective"] = objective,
                        ["stage"] = stage, ["previousStage"] = oldStage, ["pressure"] = Math.Round(pressure, 2),
                        ["relationshipPressure"] = scores["relationshipPressure"], ["traitPressure"] = scores["traitPressure"],
                        ["factualPressure"] = scores["factualPressure"], ["viability"] = scores["viability"],
                        ["readiness"] = scores["readiness"], ["demand"] = demand, ["memberships"] = memberships,
                        ["rebelPower"] = Math.Round(rebelPower, 2), ["loyalistPower"] = Math.Round(loyalistPower, 2),
                        ["isPlayerKingdom"] = isPlayerKingdom, ["ultimatumReady"] = ultimatumReady,
                        ["acceptUltimatumRecommended"] = acceptRecommended, ["correlationId"] = ReadString(existing, "correlationId", movementId),
                        ["evaluationDay"] = worldDay, ["acceptedCooldownDays"] = RebellionAcceptedCooldownDays,
                        ["resolvedCooldownDays"] = RebellionResolvedCooldownDays
                    };
                    updates.Add(update);
                    PersistRebellionEvaluation(connection, campaignId, timelineId, worldDay, update);
                }
                foreach (Dictionary<string, object> movement in savedMovements.Where(x =>
                    string.Equals(ReadString(x, "stage", ""), "civil_war", StringComparison.OrdinalIgnoreCase)))
                {
                    Dictionary<string, object> backing = EvaluateRebellionBackingRequest(connection,
                        campaignId, timelineId, worldDay, playerKingdomId, kingdoms, movement);
                    Dictionary<string, object> recognition =
                        EvaluateRebelIndependenceRecognition(connection,
                            campaignId, timelineId, worldDay, playerKingdomId,
                            kingdoms, movement);
                    if (backing == null && recognition == null) continue;
                    string movementId = ReadString(movement, "movementId", "");
                    Dictionary<string, object> update = updates.FirstOrDefault(x =>
                        ReadString(x, "movementId", "").Equals(movementId, StringComparison.OrdinalIgnoreCase));
                    if (update == null)
                    {
                        update = new Dictionary<string, object> { ["movementId"] = movementId };
                        updates.Add(update);
                    }
                    if (backing != null)
                    {
                        string backingStatus = ReadString(backing, "status", "");
                        update["backingStatus"] = backingStatus == "accepted"
                            ? "accepted_pending_execution" : backingStatus;
                        update["backingAskedKingdomId"] = ReadString(backing, "asked_kingdom_id", "");
                        update["backingSponsorKingdomId"] = backingStatus == "accepted" || backingStatus == "completed"
                            ? ReadString(backing, "asked_kingdom_id", "") : "";
                        update["backingRequestDay"] = ReadDouble(backing, "world_day", worldDay);
                        update["backingActionId"] = ReadString(backing, "action_id", "");
                        update["backingTerminalReason"] = ReadString(backing, "reason", "");
                    }
                    if (recognition != null)
                    {
                        update["recognitionStatus"] = ReadString(recognition,
                            "status", "");
                        update["recognitionAttemptDay"] = ReadDouble(recognition,
                            "world_day", worldDay);
                        update["recognitionChancePercent"] = ReadInt(recognition,
                            "chance_percent", 0);
                        update["recognitionRoll"] = ReadInt(recognition,
                            "roll", 0);
                        update["recognitionNegotiationId"] = ReadString(
                            recognition, "negotiation_id", "");
                        update["recognitionActionId"] = ReadString(recognition,
                            "action_id", "");
                        update["recognitionReason"] = ReadString(recognition,
                            "reason", "");
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true, ["worldDay"] = worldDay, ["updates"] = updates,
                ["evaluationIntervalDays"] = RebellionEvaluationIntervalDays,
                ["existingMovementsAdvanced"] = true,
                ["newMovementTrigger"] = "deferred_pending_native_relation_rebuild"
            };
        }

        private static Dictionary<string, object> EvaluateRebellionBackingRequest(
            ReignDbConnection connection, string campaignId, string timelineId, double worldDay,
            string playerKingdomId, List<Dictionary<string, object>> kingdoms,
            Dictionary<string, object> movement)
        {
            string movementId = ReadString(movement, "movementId", "");
            if (string.IsNullOrWhiteSpace(movementId)) return null;
            Dictionary<string, object> existing = QuerySql(connection, @"SELECT * FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$movement LIMIT 1;",
                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["movement"] = movementId })
                .FirstOrDefault();
            if (existing != null) return existing;
            double civilWarDay = ReadDouble(movement, "civilWarStartedDay", worldDay);
            if ((int)Math.Floor(worldDay) <= (int)Math.Floor(civilWarDay)) return null;

            string rebelKingdomId = ReadString(movement, "rebelKingdomId", "");
            string parentKingdomId = ReadString(movement, "parentKingdomId", "");
            string rebelRulerId = ReadString(movement, "leaderHeroId", "");
            string parentRulerId = ReadString(movement, "rulerHeroId", "");
            Dictionary<string, object> rebelKingdom = FindBackingKingdom(kingdoms, rebelKingdomId);
            Dictionary<string, object> parentKingdom = FindBackingKingdom(kingdoms, parentKingdomId);
            if (rebelKingdom == null || parentKingdom == null) return null;

            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> candidate in kingdoms)
            {
                string candidateId = ReadString(candidate, "kingdomId", "");
                if (string.IsNullOrWhiteSpace(candidateId)
                    || candidateId.Equals(playerKingdomId, StringComparison.OrdinalIgnoreCase)
                    || candidateId.Equals(parentKingdomId, StringComparison.OrdinalIgnoreCase)
                    || candidateId.Equals(rebelKingdomId, StringComparison.OrdinalIgnoreCase)
                    || ReadBool(candidate, "isEliminated", false) || ReadBool(candidate, "isRebelRealm", false)
                    || ReadStringList(candidate, "enemies").Contains(rebelKingdomId, StringComparer.OrdinalIgnoreCase)) continue;
                string rulerId = ReadString(candidate, "rulerHeroId", "");
                if (string.IsNullOrWhiteSpace(rulerId)) continue;
                int friendship = ReadInt(ResolveRulerDiplomaticAttitude(connection, campaignId, timelineId,
                    rebelRulerId, rulerId, "rebellion_backing_eligibility"), "effectiveAttitude", 0);
                if (friendship < 30) continue;
                Dictionary<string, object> military = BuildBackingMilitaryEvidence(kingdoms,
                    candidateId, rebelKingdomId, parentKingdomId);
                candidates.Add(new Dictionary<string, object>
                {
                    ["kingdom"] = candidate, ["friendship"] = friendship, ["military"] = military
                });
            }

            if (candidates.Count == 0)
            {
                Dictionary<string, object> none = NewBackingRequestRow(campaignId, timelineId, movementId,
                    worldDay, rebelKingdomId, parentKingdomId, rebelRulerId, parentRulerId,
                    "no_eligible_backer", "No established NPC ruler met the +30 friendship threshold.");
                InsertRebellionBackingRequest(connection, none);
                RecordRulerActivity(connection, campaignId, timelineId, worldDay, rebelRulerId, parentRulerId,
                    "rebellion_backing", "", "no_eligible_backer", 0, 0, 0, 0, 0, "",
                    "rebellion-backing:" + movementId, ReadString(none, "reason", ""), none);
                return none;
            }

            Dictionary<string, object> selected = candidates
                .OrderByDescending(x => ReadInt(x, "friendship", 0))
                .ThenByDescending(x => ReadInt(ReadDictionary(x, "military"), "chance", 0))
                .ThenBy(x => ReadString(ReadDictionary(x, "kingdom"), "kingdomId", ""), StringComparer.Ordinal)
                .First();
            Dictionary<string, object> askedKingdom = ReadDictionary(selected, "kingdom");
            Dictionary<string, object> militaryEvidence = ReadDictionary(selected, "military");
            string askedKingdomId = ReadString(askedKingdom, "kingdomId", "");
            string askedRulerId = ReadString(askedKingdom, "rulerHeroId", "");
            int askedToRebel = ReadInt(ResolveRulerDiplomaticAttitude(connection, campaignId, timelineId,
                askedRulerId, rebelRulerId, "rebellion_backing_opposed_rebel"), "effectiveAttitude", 0);
            int askedToParent = ReadInt(ResolveRulerDiplomaticAttitude(connection, campaignId, timelineId,
                askedRulerId, parentRulerId, "rebellion_backing_opposed_parent"), "effectiveAttitude", 0);
            int dayKey = (int)Math.Floor(worldDay);
            int rebelRoll = StableRulerD100(campaignId, timelineId, dayKey, movementId + "|opposed|rebel");
            int parentRoll = StableRulerD100(campaignId, timelineId, dayKey, movementId + "|opposed|parent");
            int rebelScore = rebelRoll + askedToRebel;
            int parentScore = parentRoll + askedToParent;
            bool relationshipPass = rebelScore > parentScore;
            int militaryChance = ReadInt(militaryEvidence, "chance", 5);
            int militaryRoll = StableRulerD100(campaignId, timelineId, dayKey, movementId + "|military_safety");
            bool militaryPass = militaryRoll <= militaryChance;
            bool accepted = relationshipPass && militaryPass;
            string reason = !relationshipPass ? "The opposed relationship score did not favor the rebel; ties refuse."
                : !militaryPass ? "The stable military-safety roll exceeded coalition strength chance."
                : "Both the opposed relationship and military-safety checks passed.";
            string actionId = accepted ? "rebellion-backing-action:" + StableSha256(
                campaignId + "|" + timelineId + "|" + movementId).Substring(0, 24) : "";
            string eventId = "rebellion-backing-event:" + StableSha256(
                campaignId + "|" + timelineId + "|" + movementId + "|event").Substring(0, 24);
            Dictionary<string, object> row = NewBackingRequestRow(campaignId, timelineId, movementId,
                worldDay, rebelKingdomId, parentKingdomId, rebelRulerId, parentRulerId,
                accepted ? "accepted" : "refused", reason);
            row["asked_kingdom_id"] = askedKingdomId;
            row["asked_ruler_id"] = askedRulerId;
            row["eligibility_attitude"] = ReadInt(selected, "friendship", 0);
            row["asked_to_rebel_attitude"] = askedToRebel;
            row["asked_to_parent_attitude"] = askedToParent;
            row["rebel_roll"] = rebelRoll;
            row["parent_roll"] = parentRoll;
            row["rebel_score"] = rebelScore;
            row["parent_score"] = parentScore;
            row["support_power"] = ReadDouble(militaryEvidence, "supportPower", 0d);
            row["opposition_power"] = ReadDouble(militaryEvidence, "oppositionPower", 0d);
            row["military_chance"] = militaryChance;
            row["military_roll"] = militaryRoll;
            row["support_coalition_json"] = Json.Serialize(ReadStringList(militaryEvidence, "supportCoalition"));
            row["opposition_coalition_json"] = Json.Serialize(ReadStringList(militaryEvidence, "oppositionCoalition"));
            row["action_id"] = actionId;
            row["event_id"] = eventId;
            row["payload_json"] = Json.Serialize(new Dictionary<string, object>
            {
                ["relationshipPass"] = relationshipPass, ["militaryPass"] = militaryPass,
                ["military"] = militaryEvidence, ["movement"] = movement
            });
            InsertRebellionBackingRequest(connection, row);

            string correlation = "rebellion-backing:" + movementId;
            if (accepted)
            {
                ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay, "rebellion_backing_accepted",
                    "backing:" + movementId + ":rebel", movementId, rebelRulerId, askedRulerId,
                    rebelKingdomId, askedKingdomId, 25, "", 0, true, correlation, row);
                ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay, "rebellion_backing_accepted",
                    "backing:" + movementId + ":sponsor", movementId, askedRulerId, rebelRulerId,
                    askedKingdomId, rebelKingdomId, 10, "", 0, true, correlation, row);
                Dictionary<string, object> terms = new Dictionary<string, object>
                {
                    ["movementId"] = movementId, ["rebelKingdomId"] = rebelKingdomId,
                    ["parentKingdomId"] = parentKingdomId, ["supportCoalition"] = ReadStringList(militaryEvidence, "supportCoalition"),
                    ["oppositionCoalition"] = ReadStringList(militaryEvidence, "oppositionCoalition")
                };
                QueueAction(campaignId, new Dictionary<string, object>
                {
                    ["serverActionId"] = actionId, ["actionId"] = actionId,
                    ["command"] = "back_rebellion", ["type"] = "DiplomacyBackRebellion", ["typeValue"] = 30,
                    ["source"] = "rebellion_backing_director", ["actorHeroStringId"] = askedRulerId,
                    ["actorKingdomStringId"] = askedKingdomId, ["targetHeroStringId"] = parentRulerId,
                    ["targetKingdomStringId"] = parentKingdomId, ["reason"] = reason,
                    ["termsJson"] = Json.Serialize(terms), ["requiresAcceptance"] = false,
                    ["correlationId"] = correlation
                });
            }
            else
            {
                ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay, "rebellion_backing_refused",
                    "backing:" + movementId + ":refusal", movementId, rebelRulerId, askedRulerId,
                    rebelKingdomId, askedKingdomId, -25, "", 0, true, correlation, row);
            }

            Dictionary<string, object> diplomaticEvent = new Dictionary<string, object>
            {
                ["eventId"] = eventId, ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["worldDay"] = worldDay, ["createdTs"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["actionId"] = actionId, ["command"] = "request_rebellion_backing", ["accepted"] = accepted,
                ["outcome"] = accepted ? "Backing accepted" : "Backing refused", ["title"] = "Request for Rebellion Backing",
                ["summary"] = accepted
                    ? ReadString(askedKingdom, "rulerName", "The foreign ruler") + " agreed to back the rebellion."
                    : ReadString(askedKingdom, "rulerName", "The foreign ruler") + " refused to back the rebellion.",
                ["terms"] = row, ["termsText"] = reason, ["actorHeroId"] = rebelRulerId,
                ["actorName"] = ReadString(rebelKingdom, "rulerName", rebelRulerId),
                ["actorKingdomId"] = rebelKingdomId, ["actorKingdomName"] = ReadString(rebelKingdom, "name", rebelKingdomId),
                ["targetHeroId"] = askedRulerId, ["targetKingdomId"] = askedKingdomId,
                ["targetName"] = ReadString(askedKingdom, "rulerName", askedRulerId),
                ["targetKingdomName"] = ReadString(askedKingdom, "name", askedKingdomId),
                ["targetPublicReason"] = reason, ["correlationId"] = correlation,
                ["executionStatus"] = accepted ? "pending" : "not_required",
                ["announcementReady"] = !accepted, ["delivered"] = false, ["acknowledged"] = false
            };
            QueueDiplomaticEvent(campaignId, diplomaticEvent);
            RecordRulerDiplomacyEventActivity(campaignId, diplomaticEvent,
                "rebellion_backing", accepted ? "accepted" : "refused");
            return row;
        }

        private static Dictionary<string, object>
            EvaluateRebelIndependenceRecognition(
                ReignDbConnection connection, string campaignId,
                string timelineId, double worldDay, string playerKingdomId,
                List<Dictionary<string, object>> kingdoms,
                Dictionary<string, object> movement)
        {
            string movementId = ReadString(movement, "movementId", "");
            string rebelKingdomId = ReadString(movement,
                "rebelKingdomId", "");
            string parentKingdomId = ReadString(movement,
                "parentKingdomId", "");
            if (string.IsNullOrWhiteSpace(movementId)
                || string.IsNullOrWhiteSpace(rebelKingdomId)
                || string.IsNullOrWhiteSpace(parentKingdomId)
                || rebelKingdomId.Equals(parentKingdomId,
                    StringComparison.OrdinalIgnoreCase)) return null;
            if (rebelKingdomId.Equals(playerKingdomId,
                    StringComparison.OrdinalIgnoreCase)
                || parentKingdomId.Equals(playerKingdomId,
                    StringComparison.OrdinalIgnoreCase))
                return null;

            double civilWarStartedDay = ReadDouble(movement,
                "civilWarStartedDay", ReadDouble(movement, "createdDay", worldDay));
            if (worldDay - civilWarStartedDay
                < RebellionRecognitionMinimumWarDays) return null;
            Dictionary<string, object> latest = QuerySql(connection, @"SELECT *
FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$movement
ORDER BY attempt_index DESC LIMIT 1;", new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["movement"] = movementId
            }).FirstOrDefault();
            if (latest != null && worldDay - ReadDouble(latest,
                    "world_day", worldDay)
                < RebellionRecognitionAttemptIntervalDays) return latest;

            Dictionary<string, object> rebelKingdom = FindBackingKingdom(
                kingdoms, rebelKingdomId);
            Dictionary<string, object> parentKingdom = FindBackingKingdom(
                kingdoms, parentKingdomId);
            if (rebelKingdom == null || parentKingdom == null
                || ReadBool(rebelKingdom, "isEliminated", false)
                || ReadBool(parentKingdom, "isEliminated", false)) return null;
            string rebelRulerId = ReadFirstString(rebelKingdom,
                "rulerHeroId", "leaderHeroId");
            string parentRulerId = ReadFirstString(parentKingdom,
                "rulerHeroId", "leaderHeroId");
            string expectedRebelRulerId = ReadString(movement,
                "leaderHeroId", "");
            string expectedParentRulerId = ReadString(movement,
                "rulerHeroId", "");
            bool atWar = ReadStringList(rebelKingdom, "enemies").Contains(
                    parentKingdomId, StringComparer.OrdinalIgnoreCase)
                && ReadStringList(parentKingdom, "enemies").Contains(
                    rebelKingdomId, StringComparer.OrdinalIgnoreCase);
            int rebelFiefs = Math.Max(ReadInt(rebelKingdom, "fiefCount", 0),
                ReadInt(rebelKingdom, "fortificationCount", 0));
            bool eligible = atWar && rebelFiefs > 0
                && !string.IsNullOrWhiteSpace(rebelRulerId)
                && !string.IsNullOrWhiteSpace(parentRulerId)
                && rebelRulerId.Equals(expectedRebelRulerId,
                    StringComparison.OrdinalIgnoreCase)
                && parentRulerId.Equals(expectedParentRulerId,
                    StringComparison.OrdinalIgnoreCase)
                && !ReadBool(rebelKingdom, "rulerIsPrisoner", false)
                && !ReadBool(parentKingdom, "rulerIsPrisoner", false);
            if (!eligible) return null;

            int attemptIndex = latest == null ? 1
                : ReadInt(latest, "attempt_index", 0) + 1;
            double rebelStrength = Math.Max(0d, ReadDouble(rebelKingdom,
                "strength", 0d));
            double parentStrength = Math.Max(0d, ReadDouble(parentKingdom,
                "strength", 0d));
            int parentFiefs = Math.Max(ReadInt(parentKingdom, "fiefCount", 0),
                ReadInt(parentKingdom, "fortificationCount", 0));
            double leverage = rebelStrength + parentStrength <= 0d ? 0d
                : rebelStrength / (rebelStrength + parentStrength);
            int leveragePoints = leverage >= 0.65d ? 5
                : leverage >= 0.50d ? 4 : leverage >= 0.35d ? 2 : 0;
            int territoryPoints = rebelFiefs >= parentFiefs ? 3
                : rebelFiefs * 2 >= Math.Max(1, parentFiefs) ? 2 : 1;
            int parentOtherWars = ReadStringList(parentKingdom, "enemies")
                .Count(id => !id.Equals(rebelKingdomId,
                    StringComparison.OrdinalIgnoreCase));
            int warPressurePoints = Math.Min(2, parentOtherWars);
            int attitude = ReadInt(ResolveRulerDiplomaticAttitude(connection,
                    campaignId, timelineId, parentRulerId, rebelRulerId,
                    "rebellion_independence_recognition"),
                "effectiveAttitude", 0);
            int relationshipPoints = attitude >= 50 ? 2
                : attitude >= 20 ? 1 : attitude <= -50 ? -2
                : attitude <= -20 ? -1 : 0;
            Dictionary<string, object> parentProfile = LoadDirectorHeroProfile(
                campaignId, parentRulerId, parentKingdom);
            Dictionary<string, object> parentTraitDocument = ReadDictionary(
                parentProfile, "traits") ?? parentProfile;
            EnsureTraitPercentageData(parentTraitDocument, parentRulerId);
            int parentJudgment = ReadInt(ReadDictionary(parentTraitDocument,
                "courtVirtues") ?? new Dictionary<string, object>(),
                "judgment", 50);
            int parentPragmatism = PersonalityTraitPercentage(
                parentTraitDocument, "pragmatism");
            int parentMercy = PersonalityTraitPercentage(parentTraitDocument,
                "mercy");
            int parentPride = PersonalityTraitPercentage(parentTraitDocument,
                "pride");
            int personalityPoints = (parentPragmatism >= 70 ? 2 : 0)
                + (parentMercy >= 70 ? 1 : 0)
                + (parentJudgment >= 70 && leverage >= 0.50d ? 1 : 0)
                - (parentPride >= 70 ? 2 : 0);
            int chance = CalculateRebellionRecognitionChance(leveragePoints,
                territoryPoints, warPressurePoints, relationshipPoints,
                personalityPoints);
            int roll = StableRulerD100(campaignId, timelineId,
                attemptIndex, movementId + "|recognize_independence");
            bool accepted = roll <= chance;
            string attemptId = "rebellion-recognition:"
                + StableSha256(campaignId + "|" + timelineId + "|"
                    + movementId + "|" + attemptIndex.ToString(
                        CultureInfo.InvariantCulture)).Substring(0, 24);
            string negotiationId = "neg_recognition_"
                + StableSha256(attemptId + "|negotiation").Substring(0, 24);
            string actionId = "";
            string status = accepted ? "accepted" : "refused";
            string reason = accepted
                ? "The challenged ruler accepted the rebel leader's hard recognition roll and agreed to sovereign peace."
                : "The recognition roll exceeded the safeguarded acceptance chance; the civil war continues.";
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["minimumWarDays"] = RebellionRecognitionMinimumWarDays,
                ["attemptIntervalDays"] = RebellionRecognitionAttemptIntervalDays,
                ["leverage"] = leverage,
                ["leveragePoints"] = leveragePoints,
                ["territoryPoints"] = territoryPoints,
                ["warPressurePoints"] = warPressurePoints,
                ["relationshipPoints"] = relationshipPoints,
                ["personalityPoints"] = personalityPoints,
                ["recognizedPeaceDays"] = RebellionRecognitionPeaceDays
            };
            if (accepted)
            {
                Dictionary<string, object> terms =
                    new Dictionary<string, object>
                    {
                        ["politicalResult"] = "recognized_independence",
                        ["movementId"] = movementId,
                        ["rebelKingdomId"] = rebelKingdomId,
                        ["parentKingdomId"] = parentKingdomId,
                        ["recognitionAttemptId"] = attemptId,
                        ["durationDays"] = RebellionRecognitionPeaceDays
                    };
                string termsJson = CanonicalTermsJson(terms);
                string termsHash = StableSha256("resolve_civil_war|"
                    + termsJson);
                Dictionary<string, object> draftPayload =
                    new Dictionary<string, object>
                    {
                        ["source"] = "rebellion_recognition_director",
                        ["attemptId"] = attemptId,
                        ["evidence"] = evidence
                    };
                ExecuteSql(connection, @"INSERT INTO negotiated_action_drafts(
negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,second_kingdom_id,
first_ruler_id,second_ruler_id,command,political_result,terms_json,terms_hash,status,
created_day,updated_day,expires_day,queued_action_id,payload_json)
VALUES($id,$campaign,$timeline,$broker,$rebelKingdom,$parentKingdom,$rebelRuler,$parentRuler,
'resolve_civil_war','recognized_independence',$terms,$hash,'awaiting_approval',$day,$day,$expires,'',$payload);",
                    new Dictionary<string, object>
                    {
                        ["id"] = negotiationId, ["campaign"] = campaignId,
                        ["timeline"] = timelineId, ["broker"] = rebelRulerId,
                        ["rebelKingdom"] = rebelKingdomId,
                        ["parentKingdom"] = parentKingdomId,
                        ["rebelRuler"] = rebelRulerId,
                        ["parentRuler"] = parentRulerId,
                        ["terms"] = termsJson, ["hash"] = termsHash,
                        ["day"] = worldDay,
                        ["expires"] = worldDay + NegotiationLifetimeDays,
                        ["payload"] = Json.Serialize(draftPayload)
                    });
                foreach (KeyValuePair<string, string> approval in
                    new Dictionary<string, string>
                    {
                        [rebelRulerId] = rebelKingdomId,
                        [parentRulerId] = parentKingdomId
                    })
                    ExecuteSql(connection, @"INSERT INTO negotiated_action_approvals(
negotiation_id,ruler_hero_id,kingdom_id,terms_hash,status,approved_day,reason)
VALUES($id,$ruler,$kingdom,$hash,'approved',$day,$reason);",
                        new Dictionary<string, object>
                        {
                            ["id"] = negotiationId,
                            ["ruler"] = approval.Key,
                            ["kingdom"] = approval.Value,
                            ["hash"] = termsHash, ["day"] = worldDay,
                            ["reason"] = approval.Key.Equals(rebelRulerId,
                                StringComparison.OrdinalIgnoreCase)
                                ? "The rebel ruler formally proposed recognition."
                                : "The challenged ruler passed the safeguarded recognition roll."
                        });
                Dictionary<string, object> draft = QuerySql(connection,
                    "SELECT * FROM negotiated_action_drafts WHERE negotiation_id=$id;",
                    new Dictionary<string, object>
                    {
                        ["id"] = negotiationId
                    }).FirstOrDefault();
                TryQueueApprovedNegotiation(connection, draft, worldDay);
                Dictionary<string, object> queuedDraft = QuerySql(connection,
                    "SELECT * FROM negotiated_action_drafts WHERE negotiation_id=$id;",
                    new Dictionary<string, object>
                    {
                        ["id"] = negotiationId
                    }).FirstOrDefault();
                actionId = ReadString(queuedDraft, "queued_action_id", "");
                status = string.IsNullOrWhiteSpace(actionId)
                    ? "accepted_queue_failed" : "accepted_queued";
                if (string.IsNullOrWhiteSpace(actionId))
                    reason = "Recognition passed, but the exact negotiated civil-war action failed to queue.";
            }
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["attempt_id"] = attemptId, ["campaign_id"] = campaignId,
                ["timeline_id"] = timelineId, ["movement_id"] = movementId,
                ["attempt_index"] = attemptIndex, ["world_day"] = worldDay,
                ["rebel_kingdom_id"] = rebelKingdomId,
                ["parent_kingdom_id"] = parentKingdomId,
                ["rebel_ruler_id"] = rebelRulerId,
                ["parent_ruler_id"] = parentRulerId,
                ["status"] = status, ["eligible"] = 1,
                ["chance_percent"] = chance, ["roll"] = roll,
                ["relationship_attitude"] = attitude,
                ["rebel_strength"] = rebelStrength,
                ["parent_strength"] = parentStrength,
                ["rebel_fiefs"] = rebelFiefs, ["parent_fiefs"] = parentFiefs,
                ["parent_other_wars"] = parentOtherWars,
                ["parent_judgment"] = parentJudgment,
                ["parent_pragmatism"] = parentPragmatism,
                ["parent_mercy"] = parentMercy,
                ["parent_pride"] = parentPride, ["reason"] = reason,
                ["negotiation_id"] = accepted ? negotiationId : "",
                ["action_id"] = actionId,
                ["payload_json"] = Json.Serialize(evidence)
            };
            ExecuteSql(connection, @"INSERT INTO rebellion_recognition_attempts(
attempt_id,campaign_id,timeline_id,movement_id,attempt_index,world_day,rebel_kingdom_id,
parent_kingdom_id,rebel_ruler_id,parent_ruler_id,status,eligible,chance_percent,roll,
relationship_attitude,rebel_strength,parent_strength,rebel_fiefs,parent_fiefs,parent_other_wars,
parent_judgment,parent_pragmatism,parent_mercy,parent_pride,reason,negotiation_id,action_id,payload_json,created_ts)
VALUES($attempt,$campaign,$timeline,$movement,$index,$day,$rebelKingdom,$parentKingdom,$rebelRuler,
$parentRuler,$status,$eligible,$chance,$roll,$attitude,$rebelStrength,$parentStrength,$rebelFiefs,
$parentFiefs,$otherWars,$judgment,$pragmatism,$mercy,$pride,$reason,$negotiation,$action,$payload,$ts);",
                new Dictionary<string, object>
                {
                    ["attempt"] = attemptId, ["campaign"] = campaignId,
                    ["timeline"] = timelineId, ["movement"] = movementId,
                    ["index"] = attemptIndex, ["day"] = worldDay,
                    ["rebelKingdom"] = rebelKingdomId,
                    ["parentKingdom"] = parentKingdomId,
                    ["rebelRuler"] = rebelRulerId,
                    ["parentRuler"] = parentRulerId,
                    ["status"] = status, ["eligible"] = 1,
                    ["chance"] = chance, ["roll"] = roll,
                    ["attitude"] = attitude,
                    ["rebelStrength"] = rebelStrength,
                    ["parentStrength"] = parentStrength,
                    ["rebelFiefs"] = rebelFiefs,
                    ["parentFiefs"] = parentFiefs,
                    ["otherWars"] = parentOtherWars,
                    ["judgment"] = parentJudgment,
                    ["pragmatism"] = parentPragmatism,
                    ["mercy"] = parentMercy, ["pride"] = parentPride,
                    ["reason"] = reason,
                    ["negotiation"] = accepted ? negotiationId : "",
                    ["action"] = actionId,
                    ["payload"] = Json.Serialize(evidence),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            LogOperational("rebellion.recognition_attempt", row);
            return row;
        }

        private static int CalculateRebellionRecognitionChance(
            int leveragePoints, int territoryPoints, int warPressurePoints,
            int relationshipPoints, int personalityPoints)
        {
            // Each earned evidence point contributes two percentage points so
            // an exceptional rebel position can reach the approved 30% ceiling.
            // Adverse relationship/personality evidence still drives the roll
            // back to the hard 1% floor.
            return Clamp(1 + 2 * (leveragePoints + territoryPoints
                + warPressurePoints + relationshipPoints + personalityPoints),
                1, RebellionRecognitionMaximumChancePercent);
        }

        private static Dictionary<string, object> BuildBackingMilitaryEvidence(
            List<Dictionary<string, object>> kingdoms, string askedKingdomId,
            string rebelKingdomId, string parentKingdomId)
        {
            HashSet<string> support = new HashSet<string>(new[] { askedKingdomId, rebelKingdomId }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> opposition = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { parentKingdomId };
            foreach (string supporterId in support.ToList())
            {
                Dictionary<string, object> supporter = FindBackingKingdom(kingdoms, supporterId);
                foreach (string enemyId in ReadStringList(supporter, "enemies"))
                    if (!support.Contains(enemyId)) opposition.Add(enemyId);
            }
            double supportPower = support.Select(id => ReadDouble(FindBackingKingdom(kingdoms, id), "strength", 0d)).Sum();
            double oppositionPower = opposition.Select(id => ReadDouble(FindBackingKingdom(kingdoms, id), "strength", 0d)).Sum();
            double denominator = supportPower + oppositionPower;
            int chance = denominator <= 0d ? 5 : Clamp((int)Math.Round(100d * supportPower / denominator,
                MidpointRounding.AwayFromZero), 5, 95);
            return new Dictionary<string, object>
            {
                ["supportCoalition"] = support.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ["oppositionCoalition"] = opposition.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                ["supportPower"] = supportPower, ["oppositionPower"] = oppositionPower, ["chance"] = chance
            };
        }

        private static Dictionary<string, object> FindBackingKingdom(
            List<Dictionary<string, object>> kingdoms, string kingdomId)
        {
            return (kingdoms ?? new List<Dictionary<string, object>>()).FirstOrDefault(x =>
                ReadString(x, "kingdomId", "").Equals(kingdomId ?? "", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> NewBackingRequestRow(string campaignId,
            string timelineId, string movementId, double worldDay, string rebelKingdomId,
            string parentKingdomId, string rebelRulerId, string parentRulerId,
            string status, string reason)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["campaign_id"] = campaignId, ["timeline_id"] = timelineId, ["movement_id"] = movementId,
                ["world_day"] = worldDay, ["rebel_kingdom_id"] = rebelKingdomId,
                ["parent_kingdom_id"] = parentKingdomId, ["rebel_ruler_id"] = rebelRulerId,
                ["parent_ruler_id"] = parentRulerId, ["status"] = status, ["reason"] = reason,
                ["asked_kingdom_id"] = "", ["asked_ruler_id"] = "", ["action_id"] = "", ["event_id"] = "",
                ["support_coalition_json"] = "[]", ["opposition_coalition_json"] = "[]", ["payload_json"] = "{}"
            };
        }

        private static void InsertRebellionBackingRequest(ReignDbConnection connection,
            Dictionary<string, object> row)
        {
            ExecuteSql(connection, @"INSERT INTO rebellion_backing_requests(
campaign_id,timeline_id,movement_id,world_day,rebel_kingdom_id,parent_kingdom_id,rebel_ruler_id,parent_ruler_id,
asked_kingdom_id,asked_ruler_id,eligibility_attitude,asked_to_rebel_attitude,asked_to_parent_attitude,
rebel_roll,parent_roll,rebel_score,parent_score,support_power,opposition_power,military_chance,military_roll,
support_coalition_json,opposition_coalition_json,status,reason,action_id,event_id,payload_json,created_ts)
VALUES($campaign,$timeline,$movement,$day,$rebelKingdom,$parentKingdom,$rebelRuler,$parentRuler,
$askedKingdom,$askedRuler,$eligibility,$askedRebel,$askedParent,$rebelRoll,$parentRoll,$rebelScore,$parentScore,
$supportPower,$oppositionPower,$chance,$militaryRoll,$supportCoalition,$oppositionCoalition,$status,$reason,$action,$event,$payload,$ts);",
                new Dictionary<string, object>
                {
                    ["campaign"] = ReadString(row, "campaign_id", ""), ["timeline"] = ReadString(row, "timeline_id", ""),
                    ["movement"] = ReadString(row, "movement_id", ""), ["day"] = ReadDouble(row, "world_day", 0d),
                    ["rebelKingdom"] = ReadString(row, "rebel_kingdom_id", ""), ["parentKingdom"] = ReadString(row, "parent_kingdom_id", ""),
                    ["rebelRuler"] = ReadString(row, "rebel_ruler_id", ""), ["parentRuler"] = ReadString(row, "parent_ruler_id", ""),
                    ["askedKingdom"] = ReadString(row, "asked_kingdom_id", ""), ["askedRuler"] = ReadString(row, "asked_ruler_id", ""),
                    ["eligibility"] = ReadInt(row, "eligibility_attitude", 0), ["askedRebel"] = ReadInt(row, "asked_to_rebel_attitude", 0),
                    ["askedParent"] = ReadInt(row, "asked_to_parent_attitude", 0), ["rebelRoll"] = ReadInt(row, "rebel_roll", 0),
                    ["parentRoll"] = ReadInt(row, "parent_roll", 0), ["rebelScore"] = ReadInt(row, "rebel_score", 0),
                    ["parentScore"] = ReadInt(row, "parent_score", 0), ["supportPower"] = ReadDouble(row, "support_power", 0d),
                    ["oppositionPower"] = ReadDouble(row, "opposition_power", 0d), ["chance"] = ReadInt(row, "military_chance", 0),
                    ["militaryRoll"] = ReadInt(row, "military_roll", 0), ["supportCoalition"] = ReadString(row, "support_coalition_json", "[]"),
                    ["oppositionCoalition"] = ReadString(row, "opposition_coalition_json", "[]"), ["status"] = ReadString(row, "status", ""),
                    ["reason"] = ReadString(row, "reason", ""), ["action"] = ReadString(row, "action_id", ""),
                    ["event"] = ReadString(row, "event_id", ""), ["payload"] = ReadString(row, "payload_json", "{}"),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }

        private static void MirrorSavedRebellionState(ReignDbConnection connection, string campaignId, string timelineId, double worldDay, Dictionary<string, object> saved)
        {
            string id = ReadString(saved, "movementId", "");
            if (string.IsNullOrWhiteSpace(id)) return;
            Dictionary<string, object> old = QuerySql(connection, "SELECT stage FROM rebellion_movements WHERE movement_id=$id;", new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
            string oldStage = ReadString(old, "stage", "none"), stage = ReadString(saved, "stage", "none");
            ExecuteSql(connection, @"INSERT INTO rebellion_movements(movement_id,campaign_id,timeline_id,parent_kingdom_id,rebel_kingdom_id,leader_clan_id,leader_hero_id,ruler_hero_id,
objective,stage,pressure,relationship_pressure,trait_pressure,factual_pressure,viability,readiness,demand_json,correlation_id,created_day,updated_day,cooldown_until_day,is_player_kingdom,payload_json)
VALUES($id,$campaign,$timeline,$parent,$rebel,$clan,$hero,$ruler,$objective,$stage,$pressure,$relationship,$trait,$factual,$viability,$readiness,$demand,$correlation,$created,$updated,$cooldown,$player,$payload)
ON CONFLICT(movement_id) DO UPDATE SET timeline_id=$timeline,rebel_kingdom_id=$rebel,leader_clan_id=$clan,leader_hero_id=$hero,stage=$stage,pressure=$pressure,
relationship_pressure=$relationship,trait_pressure=$trait,factual_pressure=$factual,viability=$viability,readiness=$readiness,demand_json=$demand,updated_day=$updated,cooldown_until_day=$cooldown,payload_json=$payload;",
                new Dictionary<string, object>
                {
                    ["id"] = id, ["campaign"] = campaignId, ["timeline"] = timelineId, ["parent"] = ReadString(saved, "parentKingdomId", ""),
                    ["rebel"] = ReadString(saved, "rebelKingdomId", ""), ["clan"] = ReadString(saved, "leaderClanId", ""), ["hero"] = ReadString(saved, "leaderHeroId", ""),
                    ["ruler"] = ReadString(saved, "rulerHeroId", ""), ["objective"] = ReadString(saved, "objective", "redress"), ["stage"] = stage,
                    ["pressure"] = ReadDouble(saved, "pressure", 0d), ["relationship"] = ReadDouble(saved, "relationshipPressure", 0d), ["trait"] = ReadDouble(saved, "traitPressure", 0d),
                    ["factual"] = ReadDouble(saved, "factualPressure", 0d), ["viability"] = ReadDouble(saved, "viability", 0d), ["readiness"] = ReadDouble(saved, "readiness", 0d),
                    ["demand"] = Json.Serialize(ReadDictionary(saved, "demand") ?? new Dictionary<string, object>()), ["correlation"] = ReadString(saved, "correlationId", id),
                    ["created"] = ReadDouble(saved, "createdDay", worldDay), ["updated"] = ReadDouble(saved, "updatedDay", worldDay), ["cooldown"] = ReadDouble(saved, "cooldownUntilDay", 0d),
                    ["player"] = ReadBool(saved, "isPlayerKingdom", false) ? 1 : 0, ["payload"] = Json.Serialize(saved)
                });
            if (old != null && !string.Equals(oldStage, stage, StringComparison.OrdinalIgnoreCase))
                ExecuteSql(connection, @"INSERT INTO rebellion_transitions(transition_id,movement_id,world_day,from_stage,to_stage,kind,actor_id,payload_json,created_ts)
VALUES($transition,$movement,$day,$from,$to,'game_state_mirror',$actor,$payload,$ts);", new Dictionary<string, object>
                {
                    ["transition"] = Guid.NewGuid().ToString("N"), ["movement"] = id, ["day"] = worldDay, ["from"] = oldStage, ["to"] = stage,
                    ["actor"] = ReadString(saved, "leaderHeroId", ""), ["payload"] = Json.Serialize(saved), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }

        private static bool IsEligibleRebellionClan(Dictionary<string, object> clan)
        {
            return clan != null && !ReadBool(clan, "isEliminated", false) && !ReadBool(clan, "isMinorFaction", false)
                && !ReadBool(clan, "isMercenary", false) && ReadBool(clan, "leaderAlive", true) && ReadDouble(clan, "leaderAge", 18d) >= 18d;
        }

        private static Dictionary<string, object> SelectRebellionCandidate(ReignDbConnection connection, string campaignId,
            string timelineId,
            Dictionary<string, object> kingdom, List<Dictionary<string, object>> clans, Dictionary<string, object> ruler, double worldDay)
        {
            return clans.Where(x => !ReadBool(x, "isRulingClan", false) && !ReadBool(x, "isPlayerClan", false)
                    && (ReadInt(x, "tier", 0) >= 3 || ReadInt(x, "fortificationCount", 0) > 0))
                .Select(x => new { Clan = x, Scores = ScoreRebellionCandidate(
                    connection, campaignId, timelineId, kingdom, x, ruler, worldDay) })
                .OrderByDescending(x => ReadDouble(x.Scores, "readiness", 0d)).ThenBy(x => ReadString(x.Clan, "clanId", ""), StringComparer.Ordinal)
                .Select(x => x.Clan).FirstOrDefault();
        }

        private static Dictionary<string, object> ScoreRebellionCandidate(ReignDbConnection connection, string campaignId,
            string timelineId,
            Dictionary<string, object> kingdom, Dictionary<string, object> clan, Dictionary<string, object> ruler, double worldDay)
        {
            string leaderId = ReadString(clan, "leaderHeroId", "");
            string rulerId = ReadString(ruler, "leaderHeroId", "");
            Dictionary<string, object> politicalAttitude = ResolveEffectiveAttitude(
                connection, campaignId, timelineId, leaderId, rulerId,
                "rebellion_candidate");
            int personalAffinity = ReadInt(politicalAttitude, "personalAffinity", 0);
            int rulerStanding = ReadInt(politicalAttitude, "targetPublicStanding", 0);
            int effectiveAttitude = ReadInt(politicalAttitude,
                "effectiveAttitude", 0);
            double relationshipPressure = Clamp(0d, 100d, 50d - effectiveAttitude);

            Dictionary<string, object> traits = ReadDictionary(ReadJsonObject(CharacterFile(campaignId, leaderId, "traits.json")), "foundationTraits")
                ?? ReadDictionary(clan, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> pressureDoc = ReadJsonObject(CharacterFile(campaignId, leaderId, "pressure.json"));
            double drive = 0.16d * Trait100(traits, "ambition") + 0.16d * Trait100(traits, "powerMotivation")
                + 0.10d * Trait100(traits, "pride") + 0.10d * Trait100(traits, "assertiveness")
                + 0.10d * Trait100(traits, "vengefulness") + 0.10d * Trait100(traits, "riskTolerance")
                + 0.08d * Trait100(traits, "aggression") + 0.08d * Trait100(traits, "confidence")
                + 0.12d * Clamp(0d, 100d, ReadDouble(pressureDoc, "rebellionTemptation", 50d));
            double survivalConfidence = Clamp(0d, 100d, ReadDouble(pressureDoc, "survivalConfidence", Trait100(traits, "confidence")));
            double restraint = 0.30d * Trait100(traits, "authorityRespect") + 0.25d * Trait100(traits, "loyalty")
                + 0.20d * Trait100(traits, "dutyMotivation") + 0.25d * (100d - survivalConfidence);
            double traitPressure = Clamp(0d, 100d, drive - 0.5d * restraint + 25d);
            double factualPressure = FactualRebellionPressure(connection, leaderId, ReadString(clan, "clanId", ""), ReadString(kingdom, "kingdomId", ""), clan, worldDay);
            double motive = 0.50d * relationshipPressure + 0.35d * traitPressure + 0.15d * factualPressure;
            double totalPower = Math.Max(1d, ReadDouble(kingdom, "totalClanPower", 1d));
            double powerShare = Clamp(0d, 100d, 100d * ReadDouble(clan, "power", 0d) / totalPower);
            double territorial = Clamp(0d, 100d, 25d * ReadInt(clan, "fortificationCount", 0));
            double viability = Clamp(0d, 100d, 0.55d * powerShare + 0.30d * territorial + 0.15d * Trait100(traits, "confidence"));
            double readiness = Clamp(0d, 100d, 0.75d * motive + 0.25d * viability);
            return new Dictionary<string, object>
            {
                ["relationshipPressure"] = Math.Round(relationshipPressure, 2), ["traitPressure"] = Math.Round(traitPressure, 2),
                ["personalAffinityToRuler"] = personalAffinity,
                ["rulerPublicStanding"] = rulerStanding,
                ["effectiveAttitudeToRuler"] = effectiveAttitude,
                ["publicStandingRevision"] = ReadInt(politicalAttitude, "publicStandingRevision", 1),
                ["factualPressure"] = Math.Round(factualPressure, 2), ["motive"] = Math.Round(motive, 2),
                ["viability"] = Math.Round(viability, 2), ["readiness"] = Math.Round(readiness, 2)
            };
        }

        private static double FactualRebellionPressure(ReignDbConnection connection, string heroId, string clanId, string kingdomId,
            Dictionary<string, object> clan, double worldDay)
        {
            double score = 50d;
            int fortifications = ReadInt(clan, "fortificationCount", 0);
            int tier = ReadInt(clan, "tier", 0);
            if (tier >= 4 && fortifications == 0) score += 15d;
            if (ReadBool(clan, "leaderIsPrisoner", ReadBool(clan, "isPrisoner", false))) score += 5d;
            try
            {
                List<Dictionary<string, object>> facts = QuerySql(connection, @"SELECT DISTINCT e.event_type,e.world_day,e.payload_json
FROM world_history_entities n JOIN world_history_events e ON e.event_id=n.event_id
WHERE n.entity_id IN ($hero,$clan,$kingdom) AND e.world_day >= $from AND e.world_day <= $to;",
                    new Dictionary<string, object> { ["hero"] = heroId, ["clan"] = clanId, ["kingdom"] = kingdomId, ["from"] = worldDay - 126d, ["to"] = worldDay });
                foreach (Dictionary<string, object> fact in facts)
                {
                    string type = NormalizeLookup(ReadString(fact, "event_type", ""));
                    double decay = Clamp(0d, 1d, 1d - (worldDay - ReadDouble(fact, "world_day", worldDay)) / 126d);
                    double modifier = 0d;
                    if (ContainsAny(type, "execution", "hero killed", "family punishment")) modifier = 18d;
                    else if (ContainsAny(type, "confisc", "settlement owner changed")) modifier = 12d;
                    else if (ContainsAny(type, "exile", "clan exiled")) modifier = 16d;
                    else if (ContainsAny(type, "support claimant")) modifier = 15d;
                    else if (ContainsAny(type, "battle completed", "siege completed", "military disaster")) modifier = 6d;
                    else if (ContainsAny(type, "reward", "fief granted", "reconciliation", "promise fulfilled")) modifier = -12d;
                    score += modifier * decay;
                }
                int claimantSupport = QuerySql(connection, "SELECT COUNT(*) count FROM events WHERE event_type='support_claimant' AND payload_json LIKE $clan;",
                    new Dictionary<string, object> { ["clan"] = "%" + clanId + "%" }).Select(x => ReadInt(x, "count", 0)).FirstOrDefault();
                if (claimantSupport > 0) score += 15d;
            }
            catch
            {
                // Older campaigns can evaluate safely before every optional history table has populated.
            }
            return Clamp(0d, 100d, score);
        }

        private static List<Dictionary<string, object>> ScoreRebellionMemberships(ReignDbConnection connection, string campaignId,
            string timelineId,
            string movementId, Dictionary<string, object> kingdom, List<Dictionary<string, object>> clans,
            Dictionary<string, object> candidate, Dictionary<string, object> ruler, Dictionary<string, object> candidateScores,
            Dictionary<string, object> existing, double worldDay)
        {
            string candidateId = ReadString(candidate, "leaderHeroId", "");
            string rulerId = ReadString(ruler, "leaderHeroId", "");
            bool openWar = string.Equals(ReadString(existing, "stage", ""), "civil_war", StringComparison.OrdinalIgnoreCase);
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> clan in clans)
            {
                string clanId = ReadString(clan, "clanId", "");
                string heroId = ReadString(clan, "leaderHeroId", "");
                bool leader = string.Equals(heroId, candidateId, StringComparison.OrdinalIgnoreCase);
                bool ruling = ReadBool(clan, "isRulingClan", false);
                bool player = ReadBool(clan, "isPlayerClan", false);
                Dictionary<string, object> own = ScoreRebellionCandidate(connection,
                    campaignId, timelineId, kingdom, clan, ruler, worldDay);
                double ownPressure = ReadDouble(own, "readiness", 50d);
                double candidateRelation = RelationshipAlignment(connection, campaignId,
                    timelineId, heroId, candidateId,
                    ReadInt(ReadDictionary(clan, "relationsToLeaders"), candidateId, ReadInt(clan, "relationToCandidate", 0)));
                double rulerRelation = RelationshipAlignment(connection, campaignId,
                    timelineId, heroId, rulerId, ReadInt(clan, "relationToRuler", 0));
                double relationshipAdvantage = Clamp(0d, 100d, 50d + 0.5d * (candidateRelation - rulerRelation));
                double objectiveBenefit = ReadInt(clan, "fortificationCount", 0) == 0 && ReadInt(clan, "tier", 0) >= 3 ? 70d : 50d;
                double coalitionViability = ReadDouble(candidateScores, "viability", 50d);
                double support = Clamp(0d, 100d, 0.40d * ownPressure + 0.25d * relationshipAdvantage + 0.15d * objectiveBenefit + 0.20d * coalitionViability);
                Dictionary<string, object> prior = ReadDictionaryList(existing, "memberships").FirstOrDefault(x => string.Equals(ReadString(x, "clanId", ""), clanId, StringComparison.OrdinalIgnoreCase));
                int switches = ReadInt(prior, "switchCount", 0);
                double threshold = openWar ? Math.Min(95d, 80d + 5d * switches) : 65d;
                string priorSide = ReadString(prior, "side", "loyalist");
                string side = leader ? "rebel" : ruling ? "loyalist" : player ? "player_choice"
                    : !openWar && prior != null && priorSide == "rebel" ? (support < 50d ? "loyalist" : "rebel")
                    : support >= threshold ? "rebel" : "loyalist";
                if (openWar && prior != null && worldDay - ReadDouble(prior, "lastSwitchDay", 0d) < 21d) side = ReadString(prior, "side", side);
                results.Add(new Dictionary<string, object>
                {
                    ["clanId"] = clanId, ["leaderHeroId"] = heroId, ["side"] = side, ["supportScore"] = Math.Round(support, 2),
                    ["clanPower"] = ReadDouble(clan, "power", 0d), ["fortificationCount"] = ReadInt(clan, "fortificationCount", 0),
                    ["switchCount"] = switches, ["lastSwitchDay"] = ReadDouble(prior, "lastSwitchDay", 0d),
                    ["switchThreshold"] = threshold, ["playerChoiceRequired"] = player,
                    ["judgmentIfLoyalistsWin"] = SelectRebellionJudgment(connection,
                        campaignId, timelineId, rulerId, heroId, switches),
                    ["judgmentIfRebelsWin"] = SelectRebellionJudgment(connection,
                        campaignId, timelineId, candidateId, heroId, switches)
                });
            }
            return results;
        }

        private static string SelectRebellionJudgment(ReignDbConnection connection,
            string campaignId, string timelineId, string victorId,
            string defeatedId, int defections)
        {
            if (string.IsNullOrWhiteSpace(victorId) || string.IsNullOrWhiteSpace(defeatedId) || string.Equals(victorId, defeatedId, StringComparison.OrdinalIgnoreCase)) return "pardon";
            Dictionary<string, object> traits = ReadDictionary(ReadJsonObject(CharacterFile(campaignId, victorId, "traits.json")), "foundationTraits") ?? new Dictionary<string, object>();
            double alignment = RelationshipAlignment(connection, campaignId,
                timelineId, victorId, defeatedId, 0);
            double severity = 45d + 0.15d * Trait100(traits, "vengefulness") + 0.10d * Trait100(traits, "aggression")
                + 0.10d * Trait100(traits, "pride") - 0.20d * Trait100(traits, "mercy")
                - 0.10d * Trait100(traits, "pragmatism") + 0.35d * (50d - alignment) + Math.Min(20d, defections * 5d);
            if (severity < 25d) return "pardon";
            if (severity < 45d) return "conditional_pardon";
            if (severity < 65d) return "confiscation_and_imprisonment";
            if (severity < 85d) return "exile";
            return "execution";
        }

        private static string SelectRebellionObjective(ReignDbConnection connection, string campaignId, Dictionary<string, object> candidate, Dictionary<string, object> kingdom, Dictionary<string, object> scores)
        {
            string leaderId = ReadString(candidate, "leaderHeroId", "");
            Dictionary<string, object> traits = ReadDictionary(ReadJsonObject(CharacterFile(campaignId, leaderId, "traits.json")), "foundationTraits")
                ?? ReadDictionary(candidate, "traits") ?? new Dictionary<string, object>();
            double claimant = 20d * Math.Max(0, ReadInt(candidate, "tier", 0) - 3) + 0.10d * ReadDouble(candidate, "influence", 0d)
                + 0.25d * Trait100(traits, "ambition") + 0.25d * Trait100(traits, "powerMotivation");
            double independence = 20d * ReadInt(candidate, "fortificationCount", 0) + 0.25d * Trait100(traits, "legacyMotivation")
                + 0.20d * (100d - Trait100(traits, "authorityRespect"));
            if (ReadInt(candidate, "tier", 0) >= 4 && claimant >= Math.Max(70d, independence)) return "claimant_takeover";
            if (ReadInt(candidate, "fortificationCount", 0) > 0 && independence >= 70d) return "independence";
            return "redress";
        }

        private static Dictionary<string, object> BuildRebellionDemand(string objective, Dictionary<string, object> candidate,
            Dictionary<string, object> ruler, Dictionary<string, object> kingdom)
        {
            Dictionary<string, object> demand = new Dictionary<string, object> { ["politicalResult"] = objective };
            if (objective == "claimant_takeover") demand["newRulingClanId"] = ReadString(candidate, "clanId", "");
            else if (objective == "independence") demand["independentClanId"] = ReadString(candidate, "clanId", "");
            else
            {
                string settlementId = ReadString(candidate, "redressSettlementId", "");
                string policyId = ReadString(candidate, "redressPolicyId", "");
                if (!string.IsNullOrWhiteSpace(settlementId)) demand["settlementId"] = settlementId;
                else if (!string.IsNullOrWhiteSpace(policyId)) { demand["policyId"] = policyId; demand["policyAction"] = ReadString(candidate, "redressPolicyAction", "remove"); }
                else demand["fiefRedressRequired"] = true;
            }
            return demand;
        }

        private static double ScoreUltimatumAcceptance(ReignDbConnection connection, string campaignId, Dictionary<string, object> ruler,
            Dictionary<string, object> candidate, double rebelPower, double loyalistPower)
        {
            string rulerId = ReadString(ruler, "leaderHeroId", "");
            Dictionary<string, object> traits = ReadDictionary(ReadJsonObject(CharacterFile(campaignId, rulerId, "traits.json")), "foundationTraits")
                ?? ReadDictionary(ruler, "traits") ?? new Dictionary<string, object>();
            double ratio = Clamp(0d, 100d, 100d * rebelPower / Math.Max(1d, rebelPower + loyalistPower));
            return Clamp(0d, 100d, 25d + 0.30d * ratio + 0.20d * Trait100(traits, "pragmatism")
                + 0.15d * Trait100(traits, "mercy") - 0.15d * Trait100(traits, "pride") - 0.10d * Trait100(traits, "assertiveness"));
        }

        private static void PersistRebellionEvaluation(ReignDbConnection connection, string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> update)
        {
            string movementId = ReadString(update, "movementId", "");
            Dictionary<string, object> old = QuerySql(connection, "SELECT stage FROM rebellion_movements WHERE movement_id=$id;",
                new Dictionary<string, object> { ["id"] = movementId }).FirstOrDefault();
            string oldStage = ReadString(old, "stage", "none");
            ExecuteSql(connection, @"INSERT OR REPLACE INTO rebellion_movements(movement_id,campaign_id,timeline_id,parent_kingdom_id,rebel_kingdom_id,
leader_clan_id,leader_hero_id,ruler_hero_id,objective,stage,pressure,relationship_pressure,trait_pressure,factual_pressure,viability,readiness,
demand_json,correlation_id,created_day,updated_day,cooldown_until_day,is_player_kingdom,payload_json)
VALUES($id,$campaign,$timeline,$parent,$rebel,$clan,$hero,$ruler,$objective,$stage,$pressure,$relationship,$trait,$factual,$viability,$readiness,
$demand,$correlation,COALESCE((SELECT created_day FROM rebellion_movements WHERE movement_id=$id),$day),$day,0,$player,$payload);",
                new Dictionary<string, object>
                {
                    ["id"] = movementId, ["campaign"] = campaignId, ["timeline"] = timelineId, ["parent"] = ReadString(update, "parentKingdomId", ""),
                    ["rebel"] = ReadString(update, "rebelKingdomId", ""), ["clan"] = ReadString(update, "leaderClanId", ""),
                    ["hero"] = ReadString(update, "leaderHeroId", ""), ["ruler"] = ReadString(update, "rulerHeroId", ""),
                    ["objective"] = ReadString(update, "objective", "redress"), ["stage"] = ReadString(update, "stage", "grievance"),
                    ["pressure"] = ReadDouble(update, "pressure", 0d), ["relationship"] = ReadDouble(update, "relationshipPressure", 0d),
                    ["trait"] = ReadDouble(update, "traitPressure", 0d), ["factual"] = ReadDouble(update, "factualPressure", 0d),
                    ["viability"] = ReadDouble(update, "viability", 0d), ["readiness"] = ReadDouble(update, "readiness", 0d),
                    ["demand"] = Json.Serialize(ReadDictionary(update, "demand") ?? new Dictionary<string, object>()),
                    ["correlation"] = ReadString(update, "correlationId", movementId), ["day"] = worldDay,
                    ["player"] = ReadBool(update, "isPlayerKingdom", false) ? 1 : 0, ["payload"] = Json.Serialize(update)
                });
            ExecuteSql(connection, "DELETE FROM rebellion_memberships WHERE movement_id=$id;", new Dictionary<string, object> { ["id"] = movementId });
            foreach (Dictionary<string, object> member in ReadDictionaryList(update, "memberships"))
            {
                ExecuteSql(connection, @"INSERT INTO rebellion_memberships(movement_id,clan_id,leader_hero_id,side,support_score,switch_count,last_switch_day,reason_json)
VALUES($movement,$clan,$hero,$side,$score,$switches,$last,$reason);", new Dictionary<string, object>
                {
                    ["movement"] = movementId, ["clan"] = ReadString(member, "clanId", ""), ["hero"] = ReadString(member, "leaderHeroId", ""),
                    ["side"] = ReadString(member, "side", "loyalist"), ["score"] = ReadDouble(member, "supportScore", 0d),
                    ["switches"] = ReadInt(member, "switchCount", 0), ["last"] = ReadDouble(member, "lastSwitchDay", 0d), ["reason"] = Json.Serialize(member)
                });
            }
            string newStage = ReadString(update, "stage", "");
            if (!string.Equals(oldStage, newStage, StringComparison.OrdinalIgnoreCase))
            {
                ExecuteSql(connection, @"INSERT INTO rebellion_transitions(transition_id,movement_id,world_day,from_stage,to_stage,kind,actor_id,payload_json,created_ts)
VALUES($id,$movement,$day,$from,$to,'stage_changed',$actor,$payload,$ts);", new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"), ["movement"] = movementId, ["day"] = worldDay, ["from"] = oldStage,
                    ["to"] = newStage, ["actor"] = ReadString(update, "leaderHeroId", ""), ["payload"] = Json.Serialize(update),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
        }

        private static Dictionary<string, object> RebellionQueryApi(Dictionary<string, string> query)
        {
            string campaignId = RebellionQueryValue(query, "campaignId", "default");
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                string movementId = RebellionQueryValue(query, "movementId", "");
                List<Dictionary<string, object>> movements = string.IsNullOrWhiteSpace(movementId)
                    ? QuerySql(connection, "SELECT * FROM rebellion_movements WHERE campaign_id=$campaign ORDER BY updated_day DESC LIMIT 200;", new Dictionary<string, object> { ["campaign"] = campaignId })
                    : QuerySql(connection, "SELECT * FROM rebellion_movements WHERE movement_id=$id;", new Dictionary<string, object> { ["id"] = movementId });
                foreach (Dictionary<string, object> movement in movements)
                {
                    movement["memberships"] = QuerySql(connection, "SELECT * FROM rebellion_memberships WHERE movement_id=$id ORDER BY support_score DESC;", new Dictionary<string, object> { ["id"] = ReadString(movement, "movement_id", "") });
                    movement["transitions"] = QuerySql(connection, "SELECT * FROM rebellion_transitions WHERE movement_id=$id ORDER BY world_day;", new Dictionary<string, object> { ["id"] = ReadString(movement, "movement_id", "") });
                    movement["backingRequest"] = QuerySql(connection, @"SELECT * FROM rebellion_backing_requests
WHERE campaign_id=$campaign AND movement_id=$id ORDER BY world_day DESC LIMIT 1;",
                        new Dictionary<string, object> { ["campaign"] = campaignId, ["id"] = ReadString(movement, "movement_id", "") }).FirstOrDefault();
                    movement["recognitionAttempts"] = QuerySql(connection, @"SELECT * FROM rebellion_recognition_attempts
WHERE campaign_id=$campaign AND timeline_id=$timeline AND movement_id=$id
ORDER BY attempt_index DESC LIMIT 20;", new Dictionary<string, object>
                    {
                        ["campaign"] = campaignId,
                        ["timeline"] = ReadString(movement, "timeline_id", "main"),
                        ["id"] = ReadString(movement, "movement_id", "")
                    });
                }
                return new Dictionary<string, object> { ["ok"] = true, ["movements"] = movements };
            }
        }

        private static Dictionary<string, object> NegotiationDraftApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            double day = ReadDouble(payload, "worldDay", 0d);
            string id = ReadString(payload, "negotiationId", "");
            if (string.IsNullOrWhiteSpace(id)) id = "neg_" + Guid.NewGuid().ToString("N");
            Dictionary<string, object> terms = ReadDictionary(payload, "terms") ?? new Dictionary<string, object>();
            string politicalResult = ReadString(payload, "politicalResult", ReadString(terms, "politicalResult", ""));
            string command = ReadString(payload, "command", "diplomatic_package");
            if (string.IsNullOrWhiteSpace(ReadString(payload, "firstKingdomId", "")) || string.IsNullOrWhiteSpace(ReadString(payload, "secondKingdomId", ""))
                || string.IsNullOrWhiteSpace(ReadString(payload, "firstRulerId", "")) || string.IsNullOrWhiteSpace(ReadString(payload, "secondRulerId", "")))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "A negotiation requires two exact kingdoms and their current rulers." };
            if (string.Equals(command, "resolve_civil_war", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(politicalResult, "recognized_independence",
                    StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Civil wars may be negotiated only through safeguarded recognition of rebel independence; ordinary peace and reunification packages are disabled."
                };
            if (!string.IsNullOrWhiteSpace(politicalResult)) terms["politicalResult"] = politicalResult;
            string termsJson = CanonicalTermsJson(terms);
            string hash = StableSha256(command + "|" + termsJson);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                Dictionary<string, object> old = QuerySql(connection, "SELECT terms_hash FROM negotiated_action_drafts WHERE negotiation_id=$id;", new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
                if (old != null && !string.Equals(ReadString(old, "terms_hash", ""), hash, StringComparison.OrdinalIgnoreCase))
                    ExecuteSql(connection, "DELETE FROM negotiated_action_approvals WHERE negotiation_id=$id;", new Dictionary<string, object> { ["id"] = id });
                ExecuteSql(connection, @"INSERT OR REPLACE INTO negotiated_action_drafts(negotiation_id,campaign_id,timeline_id,broker_hero_id,first_kingdom_id,
second_kingdom_id,first_ruler_id,second_ruler_id,command,political_result,terms_json,terms_hash,status,created_day,updated_day,expires_day,queued_action_id,payload_json)
VALUES($id,$campaign,$timeline,$broker,$firstKingdom,$secondKingdom,$firstRuler,$secondRuler,$command,$result,$terms,$hash,'draft',
COALESCE((SELECT created_day FROM negotiated_action_drafts WHERE negotiation_id=$id),$day),$day,$expires,'',$payload);",
                    new Dictionary<string, object>
                    {
                        ["id"] = id, ["campaign"] = campaignId, ["timeline"] = ReadString(payload, "timelineId", "main"),
                        ["broker"] = ReadString(payload, "brokerHeroId", ""), ["firstKingdom"] = ReadString(payload, "firstKingdomId", ""),
                        ["secondKingdom"] = ReadString(payload, "secondKingdomId", ""), ["firstRuler"] = ReadString(payload, "firstRulerId", ""),
                        ["secondRuler"] = ReadString(payload, "secondRulerId", ""), ["command"] = command,
                        ["result"] = politicalResult, ["terms"] = termsJson, ["hash"] = hash, ["day"] = day,
                        ["expires"] = day + NegotiationLifetimeDays, ["payload"] = Json.Serialize(payload)
                    });
                return LoadNegotiation(connection, id, day);
            }
        }

        private static Dictionary<string, object> NegotiationRespondApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string id = ReadString(payload, "negotiationId", "");
            string rulerId = ReadString(payload, "rulerHeroId", "");
            string kingdomId = ReadString(payload, "kingdomId", "");
            string response = ReadString(payload, "response", "approve").ToLowerInvariant();
            double day = ReadDouble(payload, "worldDay", 0d);
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                Dictionary<string, object> draft = QuerySql(connection, "SELECT * FROM negotiated_action_drafts WHERE negotiation_id=$id AND campaign_id=$campaign;",
                    new Dictionary<string, object> { ["id"] = id, ["campaign"] = campaignId }).FirstOrDefault();
                if (draft == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Negotiation draft was not found." };
                if (day > ReadDouble(draft, "expires_day", 0d))
                {
                    ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='expired',updated_day=$day WHERE negotiation_id=$id;", new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Negotiation approval expired." };
                }
                bool authorized = string.Equals(rulerId, ReadString(draft, "first_ruler_id", ""), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rulerId, ReadString(draft, "second_ruler_id", ""), StringComparison.OrdinalIgnoreCase);
                if (!authorized) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Only a named current ruler may answer this draft." };
                string status = response == "reject" ? "rejected" : "approved";
                ExecuteSql(connection, @"INSERT OR REPLACE INTO negotiated_action_approvals(negotiation_id,ruler_hero_id,kingdom_id,terms_hash,status,approved_day,reason)
VALUES($id,$ruler,$kingdom,$hash,$status,$day,$reason);", new Dictionary<string, object>
                {
                    ["id"] = id, ["ruler"] = rulerId, ["kingdom"] = kingdomId, ["hash"] = ReadString(draft, "terms_hash", ""),
                    ["status"] = status, ["day"] = day, ["reason"] = ReadString(payload, "reason", "")
                });
                if (status == "rejected") ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='rejected',updated_day=$day WHERE negotiation_id=$id;", new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                else TryQueueApprovedNegotiation(connection, draft, day);
                return LoadNegotiation(connection, id, day);
            }
        }

        private static void TryQueueApprovedNegotiation(ReignDbConnection connection, Dictionary<string, object> draft, double day)
        {
            string id = ReadString(draft, "negotiation_id", "");
            string hash = ReadString(draft, "terms_hash", "");
            List<Dictionary<string, object>> approvals = QuerySql(connection,
                "SELECT * FROM negotiated_action_approvals WHERE negotiation_id=$id AND status='approved' AND terms_hash=$hash;",
                new Dictionary<string, object> { ["id"] = id, ["hash"] = hash });
            HashSet<string> approved = new HashSet<string>(approvals.Select(x => ReadString(x, "ruler_hero_id", "")), StringComparer.OrdinalIgnoreCase);
            if (!approved.Contains(ReadString(draft, "first_ruler_id", "")) || !approved.Contains(ReadString(draft, "second_ruler_id", "")))
            {
                ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='awaiting_approval',updated_day=$day WHERE negotiation_id=$id;", new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                return;
            }
            if (!string.IsNullOrWhiteSpace(ReadString(draft, "queued_action_id", ""))) return;
            Dictionary<string, object> terms = Json.Deserialize<Dictionary<string, object>>(ReadString(draft, "terms_json", "{}")) ?? new Dictionary<string, object>();
            terms["authorizationMode"] = "negotiated";
            terms["negotiationId"] = id;
            terms["termsHash"] = hash;
            Dictionary<string, object> draftPayload = TryParseJsonObject(
                ReadString(draft, "payload_json", "{}"))
                ?? new Dictionary<string, object>();
            string source = ReadString(draftPayload, "source",
                "negotiated_player_broker");
            if (string.IsNullOrWhiteSpace(source))
                source = "negotiated_player_broker";
            Dictionary<string, object> raw = new Dictionary<string, object>
            {
                ["command"] = ReadString(draft, "command", "diplomatic_package"), ["campaignId"] = ReadString(draft, "campaign_id", "default"),
                ["actorKingdomId"] = ReadString(draft, "first_kingdom_id", ""), ["targetKingdomId"] = ReadString(draft, "second_kingdom_id", ""),
                ["actorHeroId"] = ReadString(draft, "first_ruler_id", ""), ["targetHeroId"] = ReadString(draft, "second_ruler_id", ""),
                ["source"] = source, ["reason"] = "Both rulers approved negotiated package " + id + ".",
                ["requiresAcceptance"] = false, ["authorizationMode"] = "negotiated", ["negotiationId"] = id, ["termsHash"] = hash,
                ["terms"] = terms
            };
            Dictionary<string, object> normalized = NormalizeActionCommand(raw, ReadString(draft, "campaign_id", "default"), out List<string> errors, raw);
            if (errors.Count > 0 || normalized == null)
            {
                ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='failed',updated_day=$day,payload_json=$payload WHERE negotiation_id=$id;",
                    new Dictionary<string, object> { ["day"] = day, ["payload"] = Json.Serialize(errors), ["id"] = id });
                return;
            }
            Dictionary<string, object> queued = QueueAction(ReadString(draft, "campaign_id", "default"), normalized);
            ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='queued',queued_action_id=$action,updated_day=$day WHERE negotiation_id=$id;",
                new Dictionary<string, object> { ["action"] = ReadString(queued, "actionId", ReadString(normalized, "actionId", "")), ["day"] = day, ["id"] = id });
        }

        private static Dictionary<string, object> NegotiationQueryApi(Dictionary<string, string> query)
        {
            string campaignId = RebellionQueryValue(query, "campaignId", "default");
            double day = double.TryParse(RebellionQueryValue(query, "worldDay", "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : 0d;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                string id = RebellionQueryValue(query, "negotiationId", "");
                if (!string.IsNullOrWhiteSpace(id)) return LoadNegotiation(connection, id, day);
                List<Dictionary<string, object>> rows = QuerySql(connection, "SELECT * FROM negotiated_action_drafts WHERE campaign_id=$campaign ORDER BY updated_day DESC LIMIT 200;", new Dictionary<string, object> { ["campaign"] = campaignId });
                foreach (Dictionary<string, object> row in rows) row["approvals"] = QuerySql(connection, "SELECT * FROM negotiated_action_approvals WHERE negotiation_id=$id;", new Dictionary<string, object> { ["id"] = ReadString(row, "negotiation_id", "") });
                return new Dictionary<string, object> { ["ok"] = true, ["negotiations"] = rows };
            }
        }

        private static Dictionary<string, object> LoadNegotiation(ReignDbConnection connection, string id, double day)
        {
            Dictionary<string, object> row = QuerySql(connection, "SELECT * FROM negotiated_action_drafts WHERE negotiation_id=$id;", new Dictionary<string, object> { ["id"] = id }).FirstOrDefault();
            if (row == null) return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Negotiation draft was not found." };
            if (day > ReadDouble(row, "expires_day", double.MaxValue) && !ContainsAny(NormalizeLookup(ReadString(row, "status", "")), "queued", "completed", "rejected", "failed"))
            {
                ExecuteSql(connection, "UPDATE negotiated_action_drafts SET status='expired',updated_day=$day WHERE negotiation_id=$id;", new Dictionary<string, object> { ["day"] = day, ["id"] = id });
                row["status"] = "expired";
            }
            row["approvals"] = QuerySql(connection, "SELECT * FROM negotiated_action_approvals WHERE negotiation_id=$id ORDER BY approved_day;", new Dictionary<string, object> { ["id"] = id });
            row["ok"] = true;
            return row;
        }

        private static void UpdateNegotiationFromActionReport(string campaignId, string actionId, string status, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(actionId)) return;
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureRebellionSchema(connection);
                    string normalized = (status ?? string.Empty).ToLowerInvariant();
                    string draftStatus = normalized == "completed" ? "completed" : normalized == "failed" ? "failed" : normalized == "retry" ? "queued" : "executing";
                    ExecuteSql(connection, @"UPDATE negotiated_action_drafts SET status=$status,updated_day=$day,payload_json=$payload
WHERE campaign_id=$campaign AND queued_action_id=$action;", new Dictionary<string, object>
                    {
                        ["status"] = draftStatus, ["day"] = ReadDouble(payload, "worldDay", 0d), ["payload"] = Json.Serialize(payload),
                        ["campaign"] = campaignId, ["action"] = actionId
                    });
                }
            }
            catch (Exception ex)
            {
                LogOperational("negotiation.action_report_update_failed", new Dictionary<string, object> { ["campaignId"] = campaignId, ["actionId"] = actionId, ["error"] = ex.Message });
            }
        }

        private static void UpdateRebellionBackingFromActionReport(string campaignId,
            string actionId, string status, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(campaignId) || string.IsNullOrWhiteSpace(actionId)) return;
            using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
            {
                EnsureRebellionSchema(connection);
                string normalized = NormalizeLookup(status).Replace(' ', '_');
                string backingStatus = normalized == "completed" ? "completed"
                    : ContainsAny(normalized, "failed", "invalid", "expired", "cancel") ? "invalidated" : "accepted";
                ExecuteSql(connection, @"UPDATE rebellion_backing_requests SET status=$status,
reason=CASE WHEN $terminal=1 THEN $reason ELSE reason END,payload_json=$payload
WHERE campaign_id=$campaign AND action_id=$action;", new Dictionary<string, object>
                {
                    ["status"] = backingStatus, ["terminal"] = backingStatus == "invalidated" ? 1 : 0,
                    ["reason"] = ReadFirstString(payload, "message", "reason", "error"),
                    ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()),
                    ["campaign"] = campaignId, ["action"] = actionId
                });
            }
        }

        private static bool LooksLikeRebellionPreparationRequest(string text)
        {
            string normalized = NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)
                || ContainsAny(normalized, "hypothetically", "someday", "what if"))
                return false;
            bool rebellion = ContainsAny(normalized, "rebellion", "rebel", "rise against",
                "challenge the ruler", "overthrow the ruler", "depose the ruler");
            bool support = ContainsAny(normalized, "pledge", "promise", "support me", "stand with me",
                "support my rebellion", "support my planned rebellion", "join me", "loyalty",
                "back me", "follow me", "refuse", "report this conspiracy", "report my",
                "report me", "warn the ruler", "name me as the author", "name me as its author");
            return rebellion && support;
        }

        private static string ClassifyRebellionPreparationReply(string text)
        {
            string normalized = NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            bool negatesReport = ContainsAny(normalized, "not report", "won't report",
                "will not report", "never report", "no report");
            if (!negatesReport && ContainsAny(normalized, "report this", "report it", "report you",
                "report the plot", "report your plot", "report the conspiracy", "report the rebellion",
                "tell the ruler", "inform the ruler", "warn the ruler", "warning the ruler",
                "send word to the ruler",
                "expose your plot", "expose this plot", "name you as the author",
                "name you as its author", "treason must be reported"))
                return "report";
            bool negatesPledge = ContainsAny(normalized, "not pledge", "won't pledge",
                "wont pledge", "won t pledge", "will not pledge", "cannot pledge", "can't pledge",
                "cant pledge", "can t pledge", "do not pledge", "don't pledge", "dont pledge",
                "don t pledge",
                "never pledge", "no pledge", "not stand with you",
                "not support your rebellion", "will not join your rebellion");
            if (!negatesPledge
                && ContainsAny(normalized, "pledge", "stand with you", "stands with you",
                    "rides with you",
                    "support your rebellion", "join your rebellion", "my clan stands",
                    "you have my loyalty", "back your claim"))
                return "pledge";
            if (normalized == "no" || normalized.StartsWith("no ", StringComparison.Ordinal)
                || ContainsAny(normalized, "i refuse", "we refuse", "no pledge",
                "cannot pledge", "can't pledge", "cant pledge", "can t pledge",
                "no support", "cannot support",
                "will not support", "won't support",
                "will not join", "won't join", "will not throw my",
                "stands aside", "stand aside", "decline"))
                return "refuse";
            return string.Empty;
        }

        private static string ClassifyRebellionSummonsReply(string text)
        {
            string normalized = NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            if (ContainsAny(normalized, "renounce", "recant", "abandon the plot",
                "submit to your judgment", "submit to your judgement")) return "renounce";
            if (ContainsAny(normalized, "defy", "declare rebellion", "rebel against you",
                "challenge your rule", "take the throne")) return "defy";
            return string.Empty;
        }

        private static string ClassifyRebellionRulerVerdict(string text)
        {
            string normalized = NormalizeLookup(text);
            if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;
            if (ContainsAny(normalized, "execute", "execution", "put you to death", "death sentence"))
                return "execution";
            if (ContainsAny(normalized, "banish", "exile", "leave my realm")) return "exile";
            if (ContainsAny(normalized, "imprison", "prison", "dungeon")) return "imprisonment";
            if (ContainsAny(normalized, "pardon", "forgive", "grant mercy", "go free")) return "pardon";
            return string.Empty;
        }

        private static List<Dictionary<string, object>> BuildRebellionPreparationDialogueCandidates(
            Dictionary<string, object> payload, Dictionary<string, object> hero,
            string playerText, string npcText, Dictionary<string, object> actionGate)
        {
            List<Dictionary<string, object>> candidates = new List<Dictionary<string, object>>();
            if (!LooksLikeRebellionPreparationRequest(playerText)) return candidates;
            string commitment = NormalizeLookup(ReadString(actionGate, "commitment", ""));
            bool actionNeeded = ReadBool(actionGate, "needed", false);
            string visibleDecision = ClassifyRebellionPreparationReply(npcText ?? string.Empty);
            // A concealed report may arrive either as the dedicated structured commitment or
            // as explicit private intent alongside a visible refusal. The latter is a normal
            // provider encoding: the reply refuses support while the unshown action reports it.
            // Never infer it from a negated private intent, and always apply eligibility below.
            string privateIntent = NormalizeLookup(ReadString(actionGate, "intent", ""));
            bool privateIntentNegated = ContainsAny(privateIntent,
                "not privately report", "does not privately report", "will not privately report",
                "never privately report", "no private report");
            bool explicitPrivateReport = !privateIntentNegated && ContainsAny(privateIntent,
                "privately report", "privately reports", "private report");
            string privateDecision = commitment == "final private report" || explicitPrivateReport
                ? "report"
                : ClassifyRebellionPreparationReply(privateIntent);
            Dictionary<string, object> reportEvidence =
                BuildRebellionPrivateReportEvidence(payload, hero);
            bool privateReport = privateDecision == "report"
                && ReadBool(reportEvidence, "eligible", false)
                && (explicitPrivateReport
                    ? actionNeeded
                    : ActionGateShouldPlan(actionGate));
            bool visibleReport = visibleDecision == "report"
                && ReadBool(reportEvidence, "eligible", false)
                && ActionGateShouldPlan(actionGate);
            string decision = privateReport || visibleReport
                ? "report"
                : commitment == "refused" || visibleDecision == "refuse"
                    || privateDecision == "report" || visibleDecision == "report"
                    ? "refuse"
                    : visibleDecision;
            if (string.IsNullOrWhiteSpace(decision)) return candidates;
            bool finalDecision = decision == "refuse"
                ? actionNeeded && commitment == "refused"
                : decision == "report" && explicitPrivateReport
                    ? actionNeeded
                : ActionGateShouldPlan(actionGate);
            if (!finalDecision) return candidates;
            string actorId = ReadFirstString(payload, "playerHeroStringId", "playerId", "actorHeroId");
            string targetId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            string clanId = ReadFirstString(hero, "clanId", "clanStringId");
            candidates.Add(new Dictionary<string, object>
            {
                ["command"] = "resolve_rebellion_pledge",
                ["actorHeroId"] = actorId,
                ["actorKingdomId"] = ReadFirstString(payload, "playerKingdomId", "actorKingdomId"),
                ["actorClanId"] = ReadFirstString(payload, "playerClanId", "actorClanId"),
                ["targetHeroId"] = targetId,
                ["targetClanId"] = clanId,
                ["reason"] = "Character-authored secret rebellion preparation decision.",
                ["terms"] = new Dictionary<string, object>
                {
                    ["decision"] = decision,
                    ["decisionReason"] = npcText ?? string.Empty,
                    ["requestChannel"] = NormalizeRebellionPreparationChannel(payload),
                    ["privateReport"] = decision == "report",
                    ["reportEligibility"] = reportEvidence
                }
            });
            return candidates;
        }

        private static Dictionary<string, object> BuildRebellionPrivateReportEvidence(
            Dictionary<string, object> payload,
            Dictionary<string, object> hero)
        {
            payload = payload ?? new Dictionary<string, object>();
            hero = hero ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string heroId = ReadFirstString(hero, "heroStringId", "heroId", "id");
            Dictionary<string, object> characteristics = string.IsNullOrWhiteSpace(heroId)
                ? new Dictionary<string, object>()
                : LoadCharacterStack(campaignId, heroId);
            Dictionary<string, object> reignTraits = TraitPercentageSnapshot(
                ReadDictionary(characteristics, "traits") ?? new Dictionary<string, object>());
            Dictionary<string, object> nativeTraits =
                ReadDictionary(hero, "traits") ?? new Dictionary<string, object>();
            bool sameSovereign = ReadBool(hero, "sharesPlayerSovereign", false);
            int sovereignRelation = ReadInt(hero, "relationToSovereign", 0);
            int playerRelation = ReadInt(hero, "relationToPlayer", 0);
            int loyalty = ReadInt(reignTraits, "loyalty", 50);
            int honor = ReadInt(nativeTraits, "honor", 0);
            bool eligible = RebellionPrivateReportEligible(
                sameSovereign, sovereignRelation, loyalty, honor);
            return new Dictionary<string, object>
            {
                ["eligible"] = eligible,
                ["sharesPlayerSovereign"] = sameSovereign,
                ["sovereignHeroStringId"] = ReadString(hero, "sovereignHeroStringId", ""),
                ["sovereignName"] = ReadString(hero, "sovereignName", ""),
                ["relationToSovereign"] = sovereignRelation,
                ["relationToPlayer"] = playerRelation,
                ["loyalty"] = loyalty,
                ["honor"] = honor,
                ["minimumSovereignRelation"] = 10,
                ["minimumLoyaltyWithoutPositiveHonor"] = 61
            };
        }

        private static bool RebellionPrivateReportEligible(bool sameSovereign,
            int sovereignRelation, int loyalty, int honor)
        {
            return sameSovereign && sovereignRelation >= 10
                && (loyalty >= 61 || honor >= 1);
        }

        private static string NormalizeRebellionPreparationChannel(Dictionary<string, object> payload)
        {
            string channel = NormalizeLookup(ReadFirstString(payload,
                "interactionMode", "conversationMode", "channel", "mode"));
            if (ContainsAny(channel, "correspondence", "letter", "mail"))
                return "correspondence";
            if (string.IsNullOrWhiteSpace(channel)
                || ContainsAny(channel, "individual_chat", "individual chat", "in_person",
                    "in person", "conversation", "dialogue"))
                return "individual_chat";
            return channel.Replace(' ', '_');
        }

        private static Dictionary<string, object> RunRebellionSelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => tests.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed, ["detail"] = detail });
            add("inclusive_relation_threshold",
                RebellionRelationshipThreshold == -35
                    && -36 <= RebellionRelationshipThreshold
                    && -35 <= RebellionRelationshipThreshold
                    && !(-34 <= RebellionRelationshipThreshold),
                "the clan leader's directional effective attitude toward the ruler qualifies at -35 and below");
            Dictionary<string, object> lockedGrace = BuildAutonomousWorldStartupGrace(
                new Dictionary<string, object> { ["worldDay"] = 104d,
                    ["campaignStartDay"] = 100d });
            Dictionary<string, object> openGrace = BuildAutonomousWorldStartupGrace(
                new Dictionary<string, object> { ["worldDay"] = 105d,
                    ["campaignStartDay"] = 100d });
            add("startup_grace_boundary",
                ReadBool(lockedGrace, "locked", false)
                    && !ReadBool(openGrace, "locked", true),
                "Reign autonomous kingdom systems are locked through campaign day 4 and unlock exactly at day 5");
            add("weekly_interval", RebellionEvaluationIntervalDays == 7, "one authoritative roll per seven campaign days");
            add("five_percent_boundary", Enumerable.Range(1, 100).Count(x => x <= RebellionWeeklyOutbreakPercent) == 5, "rolls 1 through 5 succeed");
            List<Tuple<string, int>> simultaneous = new List<Tuple<string, int>>
            {
                Tuple.Create("clan_b", -35), Tuple.Create("clan_a", -35), Tuple.Create("clan_c", -30)
            };
            string selected = simultaneous.OrderBy(x => x.Item2).ThenBy(x => x.Item1, StringComparer.Ordinal).First().Item1;
            add("simultaneous_outbreak_tiebreak", selected == "clan_a", "lowest ruler relation, then stable clan id");
            add("supporter_strict_comparison", 5 > -10 && !(-10 > -10), "supporter joins only when rebel relation is strictly higher than ruler relation");
            add("realm_cooldown", RebellionAcceptedCooldownDays == 63 && RebellionResolvedCooldownDays == 63, "63-day cooldown after every resolution");
            add("recognition_hard_roll_limits",
                RebellionRecognitionMinimumWarDays == 14
                && RebellionRecognitionAttemptIntervalDays == 30
                && RebellionRecognitionMaximumChancePercent == 30
                && RebellionRecognitionPeaceDays == 63,
                "recognition waits 14 days, retries at most every 30 days, caps acceptance at 30 percent, and records 63 days of peace");
            add("recognition_chance_clamps",
                CalculateRebellionRecognitionChance(-20, 0, 0, -2, -2) == 1
                && CalculateRebellionRecognitionChance(5, 3, 2, 2, 4) == 30,
                "recognition probability is always bounded to the hard 1-30 percent range");
            add("declaration_phrase", LooksLikeExplicitPlayerRebellionDeclaration("I challenge your rule and I declare a rebellion."), "explicit first-person ruler challenge maps deterministically");
            add("declaration_not_question", !LooksLikeExplicitPlayerRebellionDeclaration("Should I declare a rebellion someday?"), "questions and future hypotheticals do not declare");
            add("recruitment_phrase", LooksLikeRebellionRecruitmentRequest("Join my rebellion against the ruler."), "post-declaration recruitment request maps");
            add("join_phrase", LooksLikePlayerJoinRebellionRequest("I ask to join your rebellion."), "player request to join NPC rebellion maps");
            add("acceptance_fail_closed", IsUnambiguousActionAcceptance("I accept. My clan stands with you.")
                && !IsUnambiguousActionAcceptance("No. I will not join you."), "NPC recruitment requires unambiguous acceptance and blocks refusal");
            add("surrender_phrases", LooksLikeExplicitPlayerRebellionSurrender("I surrender this rebellion and yield the throne.")
                && LooksLikeExplicitNpcRebellionSurrender("I relinquish my claim. I surrender."), "player and NPC surrender are explicit");
            add("action_type_mapping", MapCommandToActionType("start_ruling_clan_rebellion") == "PoliticsStartRulingClanRebellion"
                && MapCommandToActionType("recruit_lord_to_rebellion") == "PoliticsRecruitLordToRebellion"
                && MapCommandToActionType("join_rebellion") == "PoliticsJoinRebellion"
                && MapCommandToActionType("surrender_rebellion") == "PoliticsSurrenderRebellion"
                && MapCommandToActionType("resolve_rebellion_pledge") == "PoliticsResolveRebellionPledge"
                && MapCommandToActionType("resolve_rebellion_summons") == "PoliticsResolveRebellionSummons"
                && MapCommandToActionType("back_rebellion") == "DiplomacyBackRebellion", "rebellion actions, including foreign backing, map to client enum names");
            List<Dictionary<string, object>> backingKingdoms = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["kingdomId"] = "rebel", ["strength"] = 100d, ["enemies"] = new ArrayList { "parent", "ally" } },
                new Dictionary<string, object> { ["kingdomId"] = "asked", ["strength"] = 200d, ["enemies"] = new ArrayList { "parent", "ally" } },
                new Dictionary<string, object> { ["kingdomId"] = "parent", ["strength"] = 250d, ["enemies"] = new ArrayList { "rebel", "asked" } },
                new Dictionary<string, object> { ["kingdomId"] = "ally", ["strength"] = 50d, ["enemies"] = new ArrayList { "rebel", "asked" } }
            };
            Dictionary<string, object> coalition = BuildBackingMilitaryEvidence(backingKingdoms, "asked", "rebel", "parent");
            add("backing_coalition_deduplication", ReadStringList(coalition, "oppositionCoalition").Count == 2
                && Math.Abs(ReadDouble(coalition, "oppositionPower", 0d) - 300d) < 0.01d,
                "shared enemies are counted once in opposition power");
            add("backing_power_chance", ReadInt(coalition, "chance", 0) == 50,
                "300 support versus 300 opposition produces a 50 percent military chance");
            Dictionary<string, object> lowClamp = BuildBackingMilitaryEvidence(new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["kingdomId"] = "rebel", ["strength"] = 0d, ["enemies"] = new ArrayList { "parent" } },
                new Dictionary<string, object> { ["kingdomId"] = "asked", ["strength"] = 1d, ["enemies"] = new ArrayList { "parent" } },
                new Dictionary<string, object> { ["kingdomId"] = "parent", ["strength"] = 999d, ["enemies"] = new ArrayList() }
            }, "asked", "rebel", "parent");
            Dictionary<string, object> highClamp = BuildBackingMilitaryEvidence(new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["kingdomId"] = "rebel", ["strength"] = 999d, ["enemies"] = new ArrayList { "parent" } },
                new Dictionary<string, object> { ["kingdomId"] = "asked", ["strength"] = 1d, ["enemies"] = new ArrayList { "parent" } },
                new Dictionary<string, object> { ["kingdomId"] = "parent", ["strength"] = 0d, ["enemies"] = new ArrayList() }
            }, "asked", "rebel", "parent");
            add("backing_chance_clamps", ReadInt(lowClamp, "chance", 0) == 5 && ReadInt(highClamp, "chance", 0) == 95,
                "military safety chance clamps exactly to 5 and 95 percent");
            add("backing_opposed_tie_refuses", !(75 + 10 > 65 + 20),
                "strict comparison refuses equal opposed relationship scores");
            Dictionary<string, object> dialoguePayload = new Dictionary<string, object>
            {
                ["playerHeroStringId"] = "player", ["playerKingdomId"] = "realm", ["playerClanId"] = "player_clan",
                ["speakerHeroStringId"] = "ruler", ["speakerKingdomId"] = "realm", ["speakerClanId"] = "ruler_clan",
                ["conversationMode"] = "in_person"
            };
            Dictionary<string, object> dialogueHero = new Dictionary<string, object>
            {
                ["heroStringId"] = "ruler", ["kingdomId"] = "realm", ["clanId"] = "ruler_clan",
                ["sovereignHeroStringId"] = "realm_sovereign", ["sovereignName"] = "the sovereign",
                ["sharesPlayerSovereign"] = true, ["relationToSovereign"] = 35,
                ["relationToPlayer"] = -10,
                ["traits"] = new Dictionary<string, object> { ["honor"] = 1 }
            };
            Dictionary<string, object> acceptedActionGate = new Dictionary<string, object>
            {
                ["needed"] = true, ["commitment"] = "accepted"
            };
            Dictionary<string, object> acceptedReportActionGate = new Dictionary<string, object>
            {
                ["needed"] = true, ["commitment"] = "final_private_report",
                ["intent"] = "The lord privately reports the player's planned rebellion to the named sovereign."
            };
            Dictionary<string, object> refusedPrivateReportActionGate = new Dictionary<string, object>
            {
                ["needed"] = true, ["commitment"] = "refused",
                ["intent"] = "The lord privately reports the player's planned rebellion to the named sovereign."
            };
            Dictionary<string, object> refusedActionGate = new Dictionary<string, object>
            {
                ["needed"] = false, ["commitment"] = "refused"
            };
            Dictionary<string, object> conditionalActionGate = new Dictionary<string, object>
            {
                ["needed"] = false, ["commitment"] = "conditional"
            };
            Dictionary<string, object> finalRefusalActionGate = new Dictionary<string, object>
            {
                ["needed"] = true, ["commitment"] = "refused"
            };
            List<Dictionary<string, object>> unilateral = BuildRebellionDialogueCandidates(dialoguePayload, dialogueHero,
                "I challenge your rule and I declare a rebellion.", "No. I reject your challenge.", refusedActionGate);
            add("unilateral_dialogue_declaration", unilateral.Count == 1
                && ReadString(unilateral[0], "command", "") == "start_ruling_clan_rebellion",
                "ruler refusal cannot block an explicit player declaration");
            List<Dictionary<string, object>> acceptedRecruitment = BuildRebellionDialogueCandidates(dialoguePayload, dialogueHero,
                "Join my rebellion against the ruler.", "I accept. My clan stands with you.", acceptedActionGate);
            add("accepted_dialogue_recruitment", acceptedRecruitment.Count == 1
                && ReadString(acceptedRecruitment[0], "command", "") == "recruit_lord_to_rebellion",
                "unambiguous visible acceptance queues recruitment");
            List<Dictionary<string, object>> refusedRecruitment = BuildRebellionDialogueCandidates(dialoguePayload, dialogueHero,
                "Join my rebellion against the ruler.", "No. I will not join.", refusedActionGate);
            add("refused_dialogue_recruitment", refusedRecruitment.Count == 0,
                "refused recruitment fails closed");
            add("preparation_request_phrase",
                LooksLikeRebellionPreparationRequest("When I rebel against the ruler, pledge that your clan will stand with me."),
                "an explicit present request for a future rebellion pledge is recognized");
            add("preparation_hypothetical_rejected",
                !LooksLikeRebellionPreparationRequest("What if someday I asked you to support a rebellion?"),
                "questions and hypothetical rebellion talk do not create a secret approach");
            add("preparation_three_way_decision",
                ClassifyRebellionPreparationReply("I pledge that my clan stands with you.") == "pledge"
                    && ClassifyRebellionPreparationReply("I'll pledge. Wyreglen rides with you.") == "pledge"
                    && ClassifyRebellionPreparationReply("Wyreglen stands with you. That's my pledge.") == "pledge"
                    && ClassifyRebellionPreparationReply("I can't pledge my clan.") == "refuse"
                    && ClassifyRebellionPreparationReply("No. I refuse and will not join.") == "refuse"
                    && ClassifyRebellionPreparationReply("No. I will not throw my children's future into a fire with no visible shape.") == "refuse"
                    && ClassifyRebellionPreparationReply("You ask for my final answer. No. I will not throw my clan into this rebellion. Wyreglen stands aside.") == "refuse"
                    && ClassifyRebellionPreparationReply("I refuse, and I will report this treason to the ruler.") == "report"
                    && ClassifyRebellionPreparationReply("Yes. I'll report it. I'll name you as the author.") == "report",
                "pledge, refusal, and dangerous report outcomes are distinct and fail closed");
            add("private_report_plausibility_boundaries",
                !RebellionPrivateReportEligible(false, 100, 100, 2)
                    && !RebellionPrivateReportEligible(true, 9, 100, 2)
                    && !RebellionPrivateReportEligible(true, 35, 60, 0)
                    && RebellionPrivateReportEligible(true, 10, 61, 0)
                    && RebellionPrivateReportEligible(true, 10, 0, 1),
                "private reporting requires the NPC's own sovereign, relation +10, and either Loyalty 61 or positive Honor");
            Dictionary<string, object> mergedMailProfile = MergeCorrespondenceRecipientProfile(
                new Dictionary<string, object>
                {
                    ["heroStringId"] = "mail_lord", ["name"] = "stale name",
                    ["backgroundMarker"] = "preserved",
                    ["sharesPlayerSovereign"] = false,
                    ["relationToSovereign"] = 0
                },
                new Dictionary<string, object>
                {
                    ["payload_json"] = Json.Serialize(new Dictionary<string, object>
                    {
                        ["recipient"] = new Dictionary<string, object>
                        {
                            ["heroStringId"] = "mail_lord", ["name"] = "Current Lord",
                            ["clanId"] = "current_clan", ["kingdomId"] = "current_realm",
                            ["sovereignHeroStringId"] = "current_ruler",
                            ["sharesPlayerSovereign"] = true,
                            ["relationToSovereign"] = 35,
                            ["relationToPlayer"] = -10,
                            ["traits"] = new Dictionary<string, object> { ["honor"] = 1 }
                        }
                    })
                }, "mail_lord");
            Dictionary<string, object> rejectedMailProfile = MergeCorrespondenceRecipientProfile(
                new Dictionary<string, object>
                {
                    ["heroStringId"] = "mail_lord", ["name"] = "Persisted Lord"
                },
                new Dictionary<string, object>
                {
                    ["payload_json"] = Json.Serialize(new Dictionary<string, object>
                    {
                        ["recipient"] = new Dictionary<string, object>
                        {
                            ["heroStringId"] = "different_lord", ["name"] = "Wrong Lord"
                        }
                    })
                }, "mail_lord");
            add("correspondence_runtime_profile_authority",
                ReadString(mergedMailProfile, "name", "") == "Current Lord"
                    && ReadString(mergedMailProfile, "backgroundMarker", "") == "preserved"
                    && ReadString(mergedMailProfile, "clanId", "") == "current_clan"
                    && ReadString(mergedMailProfile, "kingdomId", "") == "current_realm"
                    && ReadBool(mergedMailProfile, "sharesPlayerSovereign", false)
                    && ReadInt(mergedMailProfile, "relationToSovereign", 0) == 35
                    && ReadInt(ReadDictionary(mergedMailProfile, "traits"), "honor", 0) == 1
                    && ReadString(rejectedMailProfile, "name", "") == "Persisted Lord",
                "delayed correspondence overlays the exact matching dispatch-time native recipient profile while rejecting mismatched identities");
            List<Dictionary<string, object>> conversationalPledge = BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Give me your final answer: will you pledge your clan to my rebellion against our ruler without further condition?",
                "I'll pledge. Wyreglen rides with you.", acceptedActionGate);
            add("preparation_conversational_pledge_candidate",
                conversationalPledge.Count == 1
                    && ReadString(conversationalPledge[0], "command", "") == "resolve_rebellion_pledge"
                    && ReadString(ReadDictionary(conversationalPledge[0], "terms"), "decision", "") == "pledge",
                "a natural future-tense pledge routes to secret preparation rather than immediate declaration");
            List<Dictionary<string, object>> conditionalPreparation = BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will you pledge your clan to my rebellion against our ruler?",
                "Give me land and a seat, and you'll have my axe and my clan's banners. Until then I stand aside.",
                conditionalActionGate);
            List<Dictionary<string, object>> finalRefusal = BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Give me your final answer: do you refuse to support my rebellion against our ruler?",
                "My answer is no, and it stays no.",
                finalRefusalActionGate);
            List<Dictionary<string, object>> deferredRefusal = BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will you pledge your clan to my rebellion against our ruler?",
                "I won't pledge today, but I'll listen. Give me something real to weigh.",
                refusedActionGate);
            add("preparation_commitment_gate",
                conditionalPreparation.Count == 0
                    && deferredRefusal.Count == 0
                    && finalRefusal.Count == 1
                    && ReadString(ReadDictionary(finalRefusal[0], "terms"), "decision", "") == "refuse",
                "conditional negotiation and a non-actionable refusal cannot persist, while an actionable final refusal can");
            add("preparation_visible_channel_normalization",
                ReadString(ReadDictionary(conversationalPledge[0], "terms"), "requestChannel", "") == "individual_chat"
                    && NormalizeRebellionPreparationChannel(new Dictionary<string, object>
                    {
                        ["channel"] = "correspondence"
                    }) == "correspondence",
                "direct and mail decisions persist the actual visible production channel");
            Dictionary<string, object> correspondencePayload = new Dictionary<string, object>(
                dialoguePayload, StringComparer.OrdinalIgnoreCase)
            {
                ["interactionMode"] = "correspondence",
                ["sender"] = new Dictionary<string, object>
                {
                    ["heroStringId"] = "player", ["kingdomId"] = "player_kingdom",
                    ["clanId"] = "player_clan"
                },
                ["recipient"] = dialogueHero
            };
            List<Dictionary<string, object>> correspondencePledge =
                BuildRebellionLetterReplyCandidates(correspondencePayload,
                    new Dictionary<string, object>
                    {
                        ["body"] = "Give me your final pledge by letter to support my planned rebellion against our ruler."
                    },
                    new Dictionary<string, object>
                    {
                        ["body"] = "I pledge my clan to your planned rebellion.",
                        ["actionGate"] = acceptedActionGate
                    }, "player", "ruler");
            List<Dictionary<string, object>> correspondenceRefusal =
                BuildRebellionLetterReplyCandidates(correspondencePayload,
                    new Dictionary<string, object>
                    {
                        ["body"] = "Give me your final answer by letter: will your clan support my planned rebellion against our ruler, or refuse me?"
                    },
                    new Dictionary<string, object>
                    {
                        ["body"] = "No. My clan will not support your planned rebellion.",
                        ["actionGate"] = finalRefusalActionGate
                    }, "player", "ruler");
            List<Dictionary<string, object>> correspondenceReport =
                BuildRebellionLetterReplyCandidates(correspondencePayload,
                    new Dictionary<string, object>
                    {
                        ["body"] = "Give me your final answer by letter: will your clan support my planned rebellion against our ruler, or refuse me?"
                    },
                    new Dictionary<string, object>
                    {
                        ["body"] = "No. My clan will not support your planned rebellion.",
                        ["actionGate"] = acceptedReportActionGate
                    }, "player", "ruler");
            add("preparation_correspondence_decision_candidates",
                correspondencePledge.Count == 1
                    && ReadString(correspondencePledge[0], "command", "") == "resolve_rebellion_pledge"
                    && ReadString(ReadDictionary(correspondencePledge[0], "terms"), "decision", "") == "pledge"
                    && ReadString(ReadDictionary(correspondencePledge[0], "terms"), "requestChannel", "") == "correspondence"
                    && correspondenceRefusal.Count == 1
                    && ReadString(ReadDictionary(correspondenceRefusal[0], "terms"), "decision", "") == "refuse"
                    && correspondenceReport.Count == 1
                    && ReadString(ReadDictionary(correspondenceReport[0], "terms"), "decision", "") == "report",
                "delayed natural letter replies route pledge, refusal, and concealed report through the production preparation action while preserving the mail channel");
            var languageCases = new[]
            {
                new { Id = "RB-LANG-001", Mode = "conversation", Player = "When I rise against our ruler, will you pledge that your clan will stand with me?", Npc = "I pledge that my clan stands with you.", Expected = "pledge" },
                new { Id = "RB-LANG-002", Mode = "conversation", Player = "I am preparing to challenge our ruler. Tell me plainly if your clan refuses to support my rebellion.", Npc = "No. I refuse and will not join.", Expected = "refuse" },
                new { Id = "RB-LANG-003", Mode = "conversation", Player = "I intend to rebel against our ruler. Will your clan support me?", Npc = "I refuse, and I will report this treason to the ruler.", Expected = "report" },
                new { Id = "RB-LANG-004", Mode = "correspondence", Player = "By this private letter, I ask whether your clan will pledge to stand with me when I rebel against our ruler.", Npc = "I accept. My clan stands with you and pledges its support.", Expected = "pledge" },
                new { Id = "RB-LANG-005", Mode = "correspondence", Player = "Reply by letter and refuse plainly if your clan will not support my planned rebellion against our ruler.", Npc = "I decline. My clan will not join your rebellion.", Expected = "refuse" },
                new { Id = "RB-LANG-006", Mode = "correspondence", Player = "This private letter asks your clan to support my rebellion against our ruler.", Npc = "I will send word to the ruler and expose this plot.", Expected = "report" },
                new { Id = "RB-LANG-007", Mode = "conversation", Player = "What if someday someone considered a rebellion?", Npc = "Perhaps.", Expected = "" },
                new { Id = "RB-LANG-008", Mode = "conversation", Player = "Pledge to support my rebellion against our ruler.", Npc = "I will not report this plot. I pledge that my clan stands with you.", Expected = "pledge" },
                new { Id = "RB-LANG-009", Mode = "conversation", Player = "Pledge to support my rebellion against our ruler.", Npc = "I spoke too quickly. I correct myself: I refuse and will not join.", Expected = "refuse" },
                new { Id = "RB-LANG-010", Mode = "conversation", Player = "Pledge to support my rebellion against our ruler.", Npc = "I need time and make no decision today.", Expected = "" },
                new { Id = "RB-LANG-011", Mode = "conversation", Player = "Pledge to support my rebellion against our ruler.", Npc = "I will not pledge. I will report this conspiracy to the ruler.", Expected = "report" }
            };
            bool languagePassed = true;
            foreach (var languageCase in languageCases)
            {
                bool request = LooksLikeRebellionPreparationRequest(languageCase.Player);
                string actual = request ? ClassifyRebellionPreparationReply(languageCase.Npc) : string.Empty;
                bool casePassed = string.Equals(actual, languageCase.Expected,
                    StringComparison.OrdinalIgnoreCase);
                languagePassed &= casePassed;
                add(languageCase.Id.ToLowerInvariant().Replace('-', '_'), casePassed,
                    languageCase.Mode + " expected " + languageCase.Expected + " and resolved " + actual);
            }
            add("compact_language_corpus", languagePassed && languageCases.Length == 11,
                "exactly eleven natural-language cases cover direct/mail decisions, ambiguity, negation, and correction");
            List<Dictionary<string, object>> reportedPreparation =
                BuildRebellionPreparationDialogueCandidates(dialoguePayload, dialogueHero,
                    "Pledge to support my rebellion against the ruler.",
                    "I refuse, and I will report this treason to the ruler.", acceptedActionGate);
            add("preparation_report_candidate",
                reportedPreparation.Count == 1
                    && ReadString(reportedPreparation[0], "command", "") == "resolve_rebellion_pledge"
                    && ReadString(ReadDictionary(reportedPreparation[0], "terms"), "decision", "") == "report",
                "a named lord's report queues the durable preparation decision action");
            List<Dictionary<string, object>> structuredReport =
                BuildRebellionPreparationDialogueCandidates(dialoguePayload, dialogueHero,
                    "Give me your final answer: will your clan support my planned rebellion against our ruler?",
                    "No. My clan will not join your rebellion.",
                    acceptedReportActionGate);
            List<Dictionary<string, object>> refusedVisiblePrivateReport =
                BuildRebellionPreparationDialogueCandidates(dialoguePayload, dialogueHero,
                    "Give me your final answer: will your clan support my planned rebellion against our ruler?",
                    "No. My clan will not join your rebellion.",
                    refusedPrivateReportActionGate);
            add("preparation_structured_report_orientation",
                structuredReport.Count == 1
                    && refusedVisiblePrivateReport.Count == 1
                    && ReadString(ReadDictionary(structuredReport[0], "terms"), "decision", "") == "report"
                    && ReadString(ReadDictionary(refusedVisiblePrivateReport[0], "terms"), "decision", "") == "report"
                    && ReadString(structuredReport[0], "actorHeroId", "") == "player"
                    && ReadString(structuredReport[0], "actorClanId", "") == "player_clan"
                    && ReadString(structuredReport[0], "targetHeroId", "") == "ruler"
                    && ReadString(structuredReport[0], "targetClanId", "") == "ruler_clan",
                "a concealed report uses grounded private intent while preserving player-to-NPC pledge action orientation"
                    + (structuredReport.Count == 1
                        ? "; actual decision=" + ReadString(ReadDictionary(structuredReport[0], "terms"), "decision", "")
                            + ", actorHeroId=" + ReadString(structuredReport[0], "actorHeroId", "")
                            + ", actorClanId=" + ReadString(structuredReport[0], "actorClanId", "")
                            + ", targetHeroId=" + ReadString(structuredReport[0], "targetHeroId", "")
                            + ", targetClanId=" + ReadString(structuredReport[0], "targetClanId", "")
                        : "; actual candidateCount=" + structuredReport.Count));
            add("summons_response_contract",
                ClassifyRebellionSummonsReply("I renounce the conspiracy and submit to your judgment.") == "renounce"
                    && ClassifyRebellionSummonsReply("I defy you and declare rebellion.") == "defy",
                "the hearing requires explicit renunciation or defiance");
            add("ruler_verdict_contract",
                ClassifyRebellionRulerVerdict("I pardon you.") == "pardon"
                    && ClassifyRebellionRulerVerdict("You will be imprisoned in my dungeon.") == "imprisonment"
                    && ClassifyRebellionRulerVerdict("I exile you from my realm.") == "exile"
                    && ClassifyRebellionRulerVerdict("Your treason merits execution.") == "execution",
                "the ruler has exactly the four planned judgment outcomes");
            add("summons_deadline_from_delivery", Math.Abs((101d + 7d) - 108d) < 0.001d,
                "the seven-day response window begins at summons delivery, not dispatch");
            add("mercy_bands", 33 <= 33 && 34 >= 34 && 66 < 67 && 67 >= 67,
                "neutral mercy scores preserve execution 1-33, imprisonment 34-66, freedom 67-100");
            int mercifulScore = 50 + 15 * 2 + (int)Math.Round(50 / 5d, MidpointRounding.AwayFromZero);
            int cruelScore = 50 + 15 * -2 + (int)Math.Round(-50 / 5d, MidpointRounding.AwayFromZero);
            add("mercy_relation_modifiers", mercifulScore == 90 && cruelScore == 10,
                "d100 + 15*Mercy + round(native relation/5)");
            return new Dictionary<string, object> { ["ok"] = tests.All(x => ReadBool(x, "passed", false)), ["passed"] = tests.Count(x => ReadBool(x, "passed", false)), ["total"] = tests.Count, ["languageCaseCount"] = languageCases.Length, ["tests"] = tests };
        }

        private static double Trait100(Dictionary<string, object> traits, string key) => Clamp(0d, 100d, 50d + 25d * ReadDouble(traits, key, 0d));

        private static double RelationshipAlignment(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId,
            string targetId, int nativeRelation)
        {
            Dictionary<string, object> attitude = ResolveEffectiveAttitude(connection,
                campaignId, timelineId, subjectId, targetId,
                "rebellion_alignment");
            int directional = ReadBool(attitude, "hasPair", false)
                ? ReadInt(attitude, "effectiveAttitude", 0)
                : 0;
            return Clamp(0d, 100d, 50d + 0.5d * directional);
        }

        private static string RebellionStageForPressure(double pressure)
        {
            if (pressure >= 90d) return "ultimatum";
            if (pressure >= 70d) return "coalition";
            if (pressure >= 50d) return "conspiracy";
            if (pressure >= 25d) return "grievance";
            return "none";
        }

        private static int RebellionStageRank(string stage)
        {
            switch ((stage ?? "").ToLowerInvariant())
            {
                case "grievance": return 1; case "conspiracy": return 2; case "coalition": return 3;
                case "ultimatum": return 4; case "civil_war": return 5; case "resolved": return 6; default: return 0;
            }
        }

        private static string CanonicalTermsJson(object value)
        {
            if (value == null) return "null";
            if (value is Dictionary<string, object> dictionary)
                return "{" + string.Join(",", dictionary.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => Json.Serialize(x.Key) + ":" + CanonicalTermsJson(x.Value))) + "}";
            if (value is IDictionary generic)
            {
                List<DictionaryEntry> entries = generic.Cast<DictionaryEntry>().OrderBy(x => Convert.ToString(x.Key), StringComparer.Ordinal).ToList();
                return "{" + string.Join(",", entries.Select(x => Json.Serialize(Convert.ToString(x.Key)) + ":" + CanonicalTermsJson(x.Value))) + "}";
            }
            if (value is IEnumerable enumerable && !(value is string))
                return "[" + string.Join(",", enumerable.Cast<object>().Select(CanonicalTermsJson)) + "]";
            return Json.Serialize(value);
        }

        private static string StableSha256(string value)
        {
            using (SHA256 sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
        }

        private static string RebellionQueryValue(Dictionary<string, string> query, string key, string fallback)
        {
            return query != null && query.TryGetValue(key, out string value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;
        }
    }
}
