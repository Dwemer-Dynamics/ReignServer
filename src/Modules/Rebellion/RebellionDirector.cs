using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class RebellionService
    {
        private const int RebellionEvaluationIntervalDays = 7;
        internal const int RebellionWeeklyOutbreakPercent = 5;
        internal const int RebellionRelationshipThreshold = -35;
        private const int RebellionAcceptedCooldownDays = 63;
        private const int RebellionResolvedCooldownDays = 63;
        private const int NegotiationLifetimeDays = 14;
        private const int RebellionRecognitionMinimumWarDays = 14;
        private const int RebellionRecognitionAttemptIntervalDays = 30;
        internal const int RebellionRecognitionMaximumChancePercent = 30;
        private const int RebellionRecognitionPeaceDays = 63;
        private const int RebellionSchemaRevision = 3;
        private const int ResolvedProjectionBatchSize = 32;
        private const string RebellionSchemaMarker =
            "rebellion_schema_version";

        internal static void EnsureRebellionSchema(ReignDbConnection connection)
        {
            if (ReignDatabase.IsPostgreSqlComponentSchemaReady(connection,
                RebellionSchemaMarker, RebellionSchemaRevision))
                return;
            RebellionSchema.CreateRebellionMovements(connection);
            RebellionSchema.CreateRebellionMemberships(connection);
            RebellionSchema.CreateRebellionTransitions(connection);
            RebellionSchema.CreateNegotiatedActionDrafts(connection);
            RebellionSchema.CreateNegotiatedActionApprovals(connection);
            RebellionSchema.CreateRebellionBackingRequests(connection);
            RebellionSchema.CreateIndexRebellionBackingStatus(connection);
            RebellionSchema.CreateRebellionRecognitionAttempts(connection);
            RebellionSchema.CreateIndexRebellionRecognitionMovement(connection);
            RebellionSchema.CreateIndexRebellionCampaignStage(connection);
            RebellionSchema.CreateIndexRebellionParent(connection);
            RebellionSchema.CreateIndexNegotiationCampaignStatus(connection);
            RebellionStore.UpsertSchemaMeta(connection, new Dictionary<string, object>
                {
                    ["key"] = RebellionSchemaMarker,
                    ["value"] = RebellionSchemaRevision.ToString(
                        CultureInfo.InvariantCulture)
                });
            ReignDatabase.MarkPostgreSqlComponentSchemaReady(connection,
                RebellionSchemaMarker, RebellionSchemaRevision);
        }

        internal static Dictionary<string, object> EvaluateRebellions(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            bool owned = payload.TryGetValue("sharedActivityGeneration", out object expectedGeneration);
            double worldDay = ReadDouble(payload, "worldDay", 0d);
            if (ReadString(payload, "intent", "") == "mirror_resolved")
                return MirrorResolvedRebellions(payload, campaignId, timelineId, expectedGeneration);
            Dictionary<string, object> startupGrace =
                DiplomacyService.BuildAutonomousWorldStartupGrace(payload);
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

            // Save Sync replaces the campaign under the outer data write gate.
            // Ordinary timeline activation shares that gate, so fence it separately.
            using var timelineScope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaignId);
            if (owned && !HasCurrentRebellionEvaluationOwner(payload, expectedGeneration))
                return StaleRebellionEvaluationOwner();
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                if (owned && !RebellionEvaluationOwnerMatches(connection, payload, expectedGeneration))
                    return StaleRebellionEvaluationOwner();
                if (!owned && WorldSimulationService.TableExists(connection, "world_history_timelines"))
                {
                    string activeTimeline = ReadString(WorldSimulationStore.SelectWorldHistoryTimelines2(connection)
                        .FirstOrDefault(), "timeline_id", "");
                    if (activeTimeline.Length > 0 && activeTimeline != timelineId)
                        return StaleRebellionEvaluationOwner();
                }
                EnsureRebellionSchema(connection);
                var acceptedMovements = new List<Dictionary<string, object>>();
                foreach (Dictionary<string, object> saved in savedMovements)
                {
                    if (MirrorSavedRebellionState(connection, campaignId, timelineId, worldDay, saved))
                        acceptedMovements.Add(saved);
                }
                savedMovements = acceptedMovements;
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
                    if (PersistRebellionEvaluation(connection, campaignId, timelineId, worldDay, update))
                        updates.Add(update);
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

            var response = new Dictionary<string, object>
            {
                ["ok"] = true, ["worldDay"] = worldDay, ["updates"] = updates,
                ["evaluationIntervalDays"] = RebellionEvaluationIntervalDays,
                ["existingMovementsAdvanced"] = true,
                ["newMovementTrigger"] = "deferred_pending_native_relation_rebuild"
            };
            if (owned)
                response["ownership"] = RebellionEvaluationOwnership(campaignId, timelineId, expectedGeneration);
            return response;
        }

        private static Dictionary<string, object> MirrorResolvedRebellions(Dictionary<string, object> payload,
            string campaignId, string timelineId, object expectedGeneration)
        {
            if (!TryReadResolvedProjection(payload, out var rows))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Expected a batch of verified rebellion resolutions." };

            using var scope = WorldSimulationService.EnterWorldHistoryTimelineScope(campaignId);
            if (!HasCurrentRebellionEvaluationOwner(payload, expectedGeneration))
                return StaleRebellionEvaluationOwner();
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaignId, trackCampaignMutations: true);
            if (connection == null || !RebellionEvaluationOwnerMatches(connection, payload, expectedGeneration))
                return StaleRebellionEvaluationOwner();
            EnsureRebellionSchema(connection);

            using var transaction = connection.BeginTransaction();
            foreach (Dictionary<string, object> movement in rows)
            {
                double resolutionDay = ReadDouble(movement, "updatedDay", double.NaN);
                if (!MirrorSavedRebellionState(connection, campaignId, timelineId, resolutionDay, movement, transaction))
                    return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The rebellion resolution conflicts with its existing projection." };
            }
            transaction.Commit();
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["status"] = "resolved_mirrored",
                ["ownership"] = RebellionEvaluationOwnership(campaignId, timelineId, expectedGeneration),
                ["movementIds"] = rows.Select(row => ReadString(row, "movementId", "")).ToArray()
            };
        }

        private static bool TryReadResolvedProjection(Dictionary<string, object> payload,
            out List<Dictionary<string, object>> rows)
        {
            rows = new List<Dictionary<string, object>>();
            double worldDay = ReadDouble(payload, "worldDay", double.NaN);
            if (!double.IsFinite(worldDay) || !payload.TryGetValue("movements", out object raw)
                || raw is not IList batch || batch.Count == 0 || batch.Count > ResolvedProjectionBatchSize)
                return false;

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object value in batch)
            {
                if (value is not Dictionary<string, object> row || !IsVerifiedRebellionResolution(row)
                    || !row.TryGetValue("movementId", out object rawId) || rawId is not string id
                    || string.IsNullOrWhiteSpace(id) || !ids.Add(id))
                    return false;
                double day = ReadDouble(row, "updatedDay", double.NaN);
                double cooldown = ReadDouble(row, "cooldownUntilDay", double.NaN);
                if (!double.IsFinite(day) || day < 0d || day > worldDay
                    || !double.IsFinite(cooldown) || cooldown < day)
                    return false;
                rows.Add(row);
            }
            return true;
        }

        private static bool IsVerifiedRebellionResolution(Dictionary<string, object> row) => row != null
            && string.Equals(ReadString(row, "stage", ""), "resolved", StringComparison.OrdinalIgnoreCase)
            && string.Equals(ReadString(row, "executionPhase", ""), "verified", StringComparison.OrdinalIgnoreCase)
            && row.TryGetValue("resolutionApplied", out object applied) && applied is true
            && row.TryGetValue("judgmentsPending", out object pending) && pending is false;

        private static Dictionary<string, object> RebellionEvaluationOwnership(string campaignId,
            string timelineId, object generation) => new Dictionary<string, object>
            {
                ["campaignId"] = campaignId, ["timelineId"] = timelineId,
                ["sharedActivityGeneration"] = generation
            };

        private static Dictionary<string, object> StaleRebellionEvaluationOwner() =>
            new Dictionary<string, object> { ["ok"] = false, ["error"] = "The rebellion evaluation belongs to a previous save or timeline." };

        private static bool HasCurrentRebellionEvaluationOwner(Dictionary<string, object> payload, object expectedGeneration)
        {
            if (expectedGeneration is not string
                || !payload.TryGetValue("campaignId", out object campaign) || campaign is not string campaignId
                || string.IsNullOrWhiteSpace(campaignId)
                || !payload.TryGetValue("timelineId", out object timeline) || timeline is not string timelineId
                || string.IsNullOrWhiteSpace(timelineId))
                return false;

            // Checking an old callback must not register a campaign or run its migrations.
            using var connection = ReignPostgreSqlStorage.TryOpenRegisteredCampaignConnection(campaignId);
            return connection != null
                && WorldSimulationService.TableExists(connection, "world_history_timelines")
                && RebellionEvaluationOwnerMatches(connection, payload, expectedGeneration);
        }

        private static bool RebellionEvaluationOwnerMatches(ReignDbConnection connection,
            Dictionary<string, object> payload, object expectedGeneration)
            => payload.TryGetValue("campaignId", out object campaign) && campaign is string campaignId
                && !string.IsNullOrWhiteSpace(campaignId)
                && payload.TryGetValue("timelineId", out object timeline) && timeline is string timelineId
                && !string.IsNullOrWhiteSpace(timelineId)
                && DialogueService.ExactSharedActivityGenerationMatches(connection, expectedGeneration)
                && string.Equals(timelineId, ReadString(WorldSimulationStore.SelectWorldHistoryTimelines2(connection)
                    .FirstOrDefault(), "timeline_id", ""), StringComparison.Ordinal);

        private static Dictionary<string, object> EvaluateRebellionBackingRequest(
            ReignDbConnection connection, string campaignId, string timelineId, double worldDay,
            string playerKingdomId, List<Dictionary<string, object>> kingdoms,
            Dictionary<string, object> movement)
        {
            string movementId = ReadString(movement, "movementId", "");
            if (string.IsNullOrWhiteSpace(movementId)) return null;
            Dictionary<string, object> existing = RebellionStore.SelectRebellionBackingRequests2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["movement"] = movementId })
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
                int friendship = ReadInt(RelationshipsService.ResolveRulerDiplomaticAttitude(connection, campaignId, timelineId,
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
                DiplomacyService.RecordRulerActivity(connection, campaignId, timelineId, worldDay, rebelRulerId, parentRulerId,
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
            int askedToRebel = ReadInt(RelationshipsService.ResolveRulerDiplomaticAttitude(connection, campaignId, timelineId,
                askedRulerId, rebelRulerId, "rebellion_backing_opposed_rebel"), "effectiveAttitude", 0);
            int askedToParent = ReadInt(RelationshipsService.ResolveRulerDiplomaticAttitude(connection, campaignId, timelineId,
                askedRulerId, parentRulerId, "rebellion_backing_opposed_parent"), "effectiveAttitude", 0);
            int dayKey = (int)Math.Floor(worldDay);
            int rebelRoll = DiplomacyService.StableRulerD100(campaignId, timelineId, dayKey, movementId + "|opposed|rebel");
            int parentRoll = DiplomacyService.StableRulerD100(campaignId, timelineId, dayKey, movementId + "|opposed|parent");
            int rebelScore = rebelRoll + askedToRebel;
            int parentScore = parentRoll + askedToParent;
            bool relationshipPass = rebelScore > parentScore;
            int militaryChance = ReadInt(militaryEvidence, "chance", 5);
            int militaryRoll = DiplomacyService.StableRulerD100(campaignId, timelineId, dayKey, movementId + "|military_safety");
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
                DiplomacyService.ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay, "rebellion_backing_accepted",
                    "backing:" + movementId + ":rebel", movementId, rebelRulerId, askedRulerId,
                    rebelKingdomId, askedKingdomId, 25, "", 0, true, correlation, row);
                DiplomacyService.ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay, "rebellion_backing_accepted",
                    "backing:" + movementId + ":sponsor", movementId, askedRulerId, rebelRulerId,
                    askedKingdomId, rebelKingdomId, 10, "", 0, true, correlation, row);
                Dictionary<string, object> terms = new Dictionary<string, object>
                {
                    ["movementId"] = movementId, ["rebelKingdomId"] = rebelKingdomId,
                    ["parentKingdomId"] = parentKingdomId, ["supportCoalition"] = ReadStringList(militaryEvidence, "supportCoalition"),
                    ["oppositionCoalition"] = ReadStringList(militaryEvidence, "oppositionCoalition")
                };
                ActionPipelineService.QueueAction(campaignId, new Dictionary<string, object>
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
                DiplomacyService.ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay, "rebellion_backing_refused",
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
            DiplomacyService.QueueDiplomaticEvent(campaignId, diplomaticEvent);
            DiplomacyService.RecordRulerDiplomacyEventActivity(campaignId, diplomaticEvent,
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
            Dictionary<string, object> latest = RebellionStore.SelectRebellionRecognitionAttempts2(connection, new Dictionary<string, object>
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
            int attitude = ReadInt(RelationshipsService.ResolveRulerDiplomaticAttitude(connection,
                    campaignId, timelineId, parentRulerId, rebelRulerId,
                    "rebellion_independence_recognition"),
                "effectiveAttitude", 0);
            int relationshipPoints = attitude >= 50 ? 2
                : attitude >= 20 ? 1 : attitude <= -50 ? -2
                : attitude <= -20 ? -1 : 0;
            Dictionary<string, object> parentProfile = RelationshipsService.LoadDirectorHeroProfile(
                campaignId, parentRulerId, parentKingdom);
            Dictionary<string, object> parentTraitDocument = ReadDictionary(
                parentProfile, "traits") ?? parentProfile;
            CharactersService.EnsureTraitPercentageData(parentTraitDocument, parentRulerId);
            int parentJudgment = ReadInt(ReadDictionary(parentTraitDocument,
                "courtVirtues") ?? new Dictionary<string, object>(),
                "judgment", 50);
            int parentPragmatism = CharactersService.PersonalityTraitPercentage(
                parentTraitDocument, "pragmatism");
            int parentMercy = CharactersService.PersonalityTraitPercentage(parentTraitDocument,
                "mercy");
            int parentPride = CharactersService.PersonalityTraitPercentage(parentTraitDocument,
                "pride");
            int personalityPoints = (parentPragmatism >= 70 ? 2 : 0)
                + (parentMercy >= 70 ? 1 : 0)
                + (parentJudgment >= 70 && leverage >= 0.50d ? 1 : 0)
                - (parentPride >= 70 ? 2 : 0);
            int chance = CalculateRebellionRecognitionChance(leveragePoints,
                territoryPoints, warPressurePoints, relationshipPoints,
                personalityPoints);
            int roll = DiplomacyService.StableRulerD100(campaignId, timelineId,
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
                RebellionStore.InsertNegotiatedActionDrafts(connection, new Dictionary<string, object>
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
                    RebellionStore.InsertNegotiatedActionApprovals(connection, new Dictionary<string, object>
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
                Dictionary<string, object> draft = RebellionStore.SelectNegotiatedActionDrafts4(connection, new Dictionary<string, object>
                    {
                        ["id"] = negotiationId
                    }).FirstOrDefault();
                TryQueueApprovedNegotiation(connection, draft, worldDay);
                Dictionary<string, object> queuedDraft = RebellionStore.SelectNegotiatedActionDrafts4(connection, new Dictionary<string, object>
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
            RebellionStore.InsertRebellionRecognitionAttempts(connection, new Dictionary<string, object>
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
            OperationsService.LogOperational("rebellion.recognition_attempt", row);
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
            RebellionStore.InsertRebellionBackingRequests(connection, new Dictionary<string, object>
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

        private static bool MirrorSavedRebellionState(ReignDbConnection connection, string campaignId, string timelineId,
            double worldDay, Dictionary<string, object> saved, ReignDbTransaction resolutionTransaction = null)
        {
            string id = ReadString(saved, "movementId", "");
            if (string.IsNullOrWhiteSpace(id)) return resolutionTransaction == null;
            Dictionary<string, object> old = RebellionStore.SelectRebellionMovements3(connection,
                new Dictionary<string, object> { ["id"] = id }, resolutionTransaction).FirstOrDefault();
            string oldStage = ReadString(old, "stage", "none"), stage = ReadString(saved, "stage", "none");
            bool sameTimeline = old != null && ReadString(old, "timeline_id", "") == timelineId;
            bool oldResolved = string.Equals(oldStage, "resolved", StringComparison.OrdinalIgnoreCase);
            bool newResolved = string.Equals(stage, "resolved", StringComparison.OrdinalIgnoreCase);
            // Save Sync can restore a pre-resolution row. An older evaluation in the
            // same timeline must never reopen a resolved row or evaluate its backing.
            if (resolutionTransaction == null && sameTimeline && oldResolved)
                return newResolved;
            if (resolutionTransaction == null && IsVerifiedRebellionResolution(saved))
                return true;
            if (resolutionTransaction != null && sameTimeline && oldResolved
                && IsVerifiedRebellionResolution(Json.Deserialize<Dictionary<string, object>>(ReadString(old, "payload_json", "{}"))))
                return ReadDouble(old, "updated_day", double.NaN) == worldDay;

            RebellionStore.UpsertRebellionMovements(connection, new Dictionary<string, object>
                {
                    ["id"] = id, ["campaign"] = campaignId, ["timeline"] = timelineId, ["parent"] = ReadString(saved, "parentKingdomId", ""),
                    ["rebel"] = ReadString(saved, "rebelKingdomId", ""), ["clan"] = ReadString(saved, "leaderClanId", ""), ["hero"] = ReadString(saved, "leaderHeroId", ""),
                    ["ruler"] = ReadString(saved, "rulerHeroId", ""), ["objective"] = ReadString(saved, "objective", "redress"), ["stage"] = stage,
                    ["pressure"] = ReadDouble(saved, "pressure", 0d), ["relationship"] = ReadDouble(saved, "relationshipPressure", 0d), ["trait"] = ReadDouble(saved, "traitPressure", 0d),
                    ["factual"] = ReadDouble(saved, "factualPressure", 0d), ["viability"] = ReadDouble(saved, "viability", 0d), ["readiness"] = ReadDouble(saved, "readiness", 0d),
                    ["demand"] = Json.Serialize(ReadDictionary(saved, "demand") ?? new Dictionary<string, object>()), ["correlation"] = ReadString(saved, "correlationId", id),
                    ["created"] = ReadDouble(saved, "createdDay", worldDay), ["updated"] = ReadDouble(saved, "updatedDay", worldDay), ["cooldown"] = ReadDouble(saved, "cooldownUntilDay", 0d),
                    ["player"] = ReadBool(saved, "isPlayerKingdom", false) ? 1 : 0, ["payload"] = Json.Serialize(saved)
                }, resolutionTransaction);
            if ((old != null || resolutionTransaction != null) && !string.Equals(oldStage, stage, StringComparison.OrdinalIgnoreCase))
                RebellionStore.InsertRebellionTransitions(connection, new Dictionary<string, object>
                {
                    ["transition"] = Guid.NewGuid().ToString("N"), ["movement"] = id, ["day"] = worldDay, ["from"] = oldStage, ["to"] = stage,
                    ["actor"] = ReadString(saved, "leaderHeroId", ""), ["payload"] = Json.Serialize(saved), ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }, resolutionTransaction);
            return true;
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
            Dictionary<string, object> politicalAttitude = RelationshipsService.ResolveEffectiveAttitude(
                connection, campaignId, timelineId, leaderId, rulerId,
                "rebellion_candidate");
            int personalAffinity = ReadInt(politicalAttitude, "personalAffinity", 0);
            int rulerStanding = ReadInt(politicalAttitude, "targetPublicStanding", 0);
            int effectiveAttitude = ReadInt(politicalAttitude,
                "effectiveAttitude", 0);
            double relationshipPressure = Clamp(0d, 100d, 50d - effectiveAttitude);

            Dictionary<string, object> traits = ReadDictionary(CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, leaderId, "traits.json")), "foundationTraits")
                ?? ReadDictionary(clan, "traits") ?? new Dictionary<string, object>();
            Dictionary<string, object> pressureDoc = CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, leaderId, "pressure.json"));
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
                List<Dictionary<string, object>> facts = RebellionStore.SelectWorldHistoryEntities(connection, new Dictionary<string, object> { ["hero"] = heroId, ["clan"] = clanId, ["kingdom"] = kingdomId, ["from"] = worldDay - 126d, ["to"] = worldDay });
                foreach (Dictionary<string, object> fact in facts)
                {
                    string type = ActionPipelineService.NormalizeLookup(ReadString(fact, "event_type", ""));
                    double decay = Clamp(0d, 1d, 1d - (worldDay - ReadDouble(fact, "world_day", worldDay)) / 126d);
                    double modifier = 0d;
                    if (DialogueService.ContainsAny(type, "execution", "hero killed", "family punishment")) modifier = 18d;
                    else if (DialogueService.ContainsAny(type, "confisc", "settlement owner changed")) modifier = 12d;
                    else if (DialogueService.ContainsAny(type, "exile", "clan exiled")) modifier = 16d;
                    else if (DialogueService.ContainsAny(type, "support claimant")) modifier = 15d;
                    else if (DialogueService.ContainsAny(type, "battle completed", "siege completed", "military disaster")) modifier = 6d;
                    else if (DialogueService.ContainsAny(type, "reward", "fief granted", "reconciliation", "promise fulfilled")) modifier = -12d;
                    score += modifier * decay;
                }
                int claimantSupport = RebellionStore.CountEvents(connection, new Dictionary<string, object> { ["clan"] = "%" + clanId + "%" }).Select(x => ReadInt(x, "count", 0)).FirstOrDefault();
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
            Dictionary<string, object> traits = ReadDictionary(CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, victorId, "traits.json")), "foundationTraits") ?? new Dictionary<string, object>();
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
            Dictionary<string, object> traits = ReadDictionary(CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, leaderId, "traits.json")), "foundationTraits")
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
            Dictionary<string, object> traits = ReadDictionary(CampaignFileService.ReadJsonObject(CampaignFileService.CharacterFile(campaignId, rulerId, "traits.json")), "foundationTraits")
                ?? ReadDictionary(ruler, "traits") ?? new Dictionary<string, object>();
            double ratio = Clamp(0d, 100d, 100d * rebelPower / Math.Max(1d, rebelPower + loyalistPower));
            return Clamp(0d, 100d, 25d + 0.30d * ratio + 0.20d * Trait100(traits, "pragmatism")
                + 0.15d * Trait100(traits, "mercy") - 0.15d * Trait100(traits, "pride") - 0.10d * Trait100(traits, "assertiveness"));
        }

        private static bool PersistRebellionEvaluation(ReignDbConnection connection, string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> update)
        {
            string movementId = ReadString(update, "movementId", "");
            Dictionary<string, object> old = RebellionStore.SelectRebellionMovements3(connection, new Dictionary<string, object> { ["id"] = movementId }).FirstOrDefault();
            string oldStage = ReadString(old, "stage", "none");
            if (ReadString(old, "timeline_id", "") == timelineId
                && string.Equals(oldStage, "resolved", StringComparison.OrdinalIgnoreCase)) return false;
            RebellionStore.UpsertRebellionMovements2(connection, new Dictionary<string, object>
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
            RebellionStore.DeleteRebellionMemberships(connection, new Dictionary<string, object> { ["id"] = movementId });
            foreach (Dictionary<string, object> member in ReadDictionaryList(update, "memberships"))
            {
                RebellionStore.InsertRebellionMemberships(connection, new Dictionary<string, object>
                {
                    ["movement"] = movementId, ["clan"] = ReadString(member, "clanId", ""), ["hero"] = ReadString(member, "leaderHeroId", ""),
                    ["side"] = ReadString(member, "side", "loyalist"), ["score"] = ReadDouble(member, "supportScore", 0d),
                    ["switches"] = ReadInt(member, "switchCount", 0), ["last"] = ReadDouble(member, "lastSwitchDay", 0d), ["reason"] = Json.Serialize(member)
                });
            }
            string newStage = ReadString(update, "stage", "");
            if (!string.Equals(oldStage, newStage, StringComparison.OrdinalIgnoreCase))
            {
                RebellionStore.InsertRebellionTransitions2(connection, new Dictionary<string, object>
                {
                    ["id"] = Guid.NewGuid().ToString("N"), ["movement"] = movementId, ["day"] = worldDay, ["from"] = oldStage,
                    ["to"] = newStage, ["actor"] = ReadString(update, "leaderHeroId", ""), ["payload"] = Json.Serialize(update),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
            }
            return true;
        }

        internal static double Trait100(Dictionary<string, object> traits, string key) => Clamp(0d, 100d, 50d + 25d * ReadDouble(traits, key, 0d));

        private static double RelationshipAlignment(ReignDbConnection connection,
            string campaignId, string timelineId, string subjectId,
            string targetId, int nativeRelation)
        {
            Dictionary<string, object> attitude = RelationshipsService.ResolveEffectiveAttitude(connection,
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
