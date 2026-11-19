using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class WorldSimulationService
    {
        private static Dictionary<string, object> BuildWorldTestRelationships(
            ReignDbConnection connection, string campaignId, string timelineId, double latestDay, string mainHeroId)
        {
            RelationshipsService.EnsureNotableMbtiSchema(connection);
            RelationshipsService.EnsureRelationshipFlingSchemaCore(connection);
            EnsureWorldTestTelemetrySchema(connection);
            Dictionary<string, object> standingAndPolitical =
                RelationshipsService.BuildWorldTestStandingAndPoliticalMetrics(connection, campaignId,
                    timelineId, latestDay);
            Dictionary<string, object> pairParameters = new Dictionary<string, object>
            {
                ["campaign"] = campaignId,
                ["timeline"] = timelineId,
                ["player"] = mainHeroId ?? string.Empty
            };
            Dictionary<string, object> pairSummary = WorldSimulationStore.CountRelationshipPairChemistry(connection, new Dictionary<string, object>(pairParameters)
                {
                    ["latestDay"] = (int)Math.Floor(latestDay)
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int pairCount = ReadInt(pairSummary, "pair_count", 0);
            List<Dictionary<string, object>> notableProfiles = WorldSimulationStore.SelectNotableMbtiProfiles(connection);
            Dictionary<string, int> notableTypes = notableProfiles
                .GroupBy(x => ReadString(x, "mbti_type", "XXXX"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> notableSync = notableProfiles
                .GroupBy(x => ReadString(x, "native_sync_status", "pending"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
            int nobleNotablePairs = ReadInt(pairSummary, "noble_notable_pairs", 0);
            int missingMbti = ReadInt(pairSummary, "missing_mbti", 0);
            int typeMismatches = ReadInt(WorldSimulationStore.CountRelationshipPairChemistry2(connection, pairParameters).FirstOrDefault(), "count", 0);
            int notableSyncErrors = notableProfiles.Count(x => DialogueService.ContainsAny(ReadString(x, "native_sync_status", ""), "failed", "contradicted"));
            int notableSyncOverdue = notableProfiles.Count(x =>
                DialogueService.ContainsAny(ReadString(x, "native_sync_status", ""), "pending", "claimed")
                && latestDay - ReadDouble(x, "native_action_created_day", latestDay) > 1d);
            Dictionary<string, Dictionary<string, int>> bands = WorldTestRelationshipBands.ToDictionary(
                x => x, x => new Dictionary<string, int> { ["uniqueNpcCount"] = 0, ["directionalEdgeCount"] = 0, ["pairCount"] = 0 },
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> materializedCategories = WorldSimulationStore.SelectHeroCounts(connection, pairParameters);
            Dictionary<string, Dictionary<string, object>> tags =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, int> affinityHistogram = new Dictionary<int, int>();
            foreach (Dictionary<string, object> row in materializedCategories)
            {
                string type = ReadString(row, "category_type", "");
                string key = ReadString(row, "category_key", "");
                Dictionary<string, int> counts = new Dictionary<string, int>
                {
                    ["uniqueNpcCount"] = ReadInt(row, "unique_npc_count", 0),
                    ["directionalEdgeCount"] = ReadInt(row, "directional_edge_count", 0),
                    ["pairCount"] = ReadInt(row, "pair_count", 0)
                };
                if (type.Equals("band", StringComparison.OrdinalIgnoreCase) && bands.ContainsKey(key))
                    bands[key] = counts;
                else if (type.Equals("tag", StringComparison.OrdinalIgnoreCase))
                    tags[key] = new Dictionary<string, object>
                    {
                        ["tag"] = key,
                        ["uniqueNpcCount"] = counts["uniqueNpcCount"],
                        ["directionalEdgeCount"] = counts["directionalEdgeCount"],
                        ["pairCount"] = counts["pairCount"]
                    };
            }
            foreach (Dictionary<string, object> row in WorldSimulationStore.SelectDirections(connection, pairParameters))
            {
                affinityHistogram[ReadInt(row, "affinity", 0)] =
                    ReadInt(row, "directional_edge_count", 0);
            }
            int uniqueNpcCount = ReadInt(WorldSimulationStore.SelectHeroes(connection, pairParameters).FirstOrDefault(), "count", 0);
            List<Dictionary<string, object>> lifecycle = WorldSimulationStore.SelectRelationshipPairLifecycle(connection);
            List<Dictionary<string, object>> lifecycleMarriageActions = WorldSimulationStore.SelectRelationshipDirectorActions(connection)
                .Where(row =>
                {
                    if (!string.IsNullOrWhiteSpace(mainHeroId)
                        && (ReadString(row, "actor_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                            || ReadString(row, "target_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)))
                        return false;
                    Dictionary<string, object> actionPayload =
                        TryParseJsonObject(ReadString(row, "payload_json", "{}")) ?? new Dictionary<string, object>();
                    return ReadString(actionPayload, "source", "").Equals(
                            "mbti_relationship_lifecycle", StringComparison.OrdinalIgnoreCase)
                        && ReadString(actionPayload, "timelineId", timelineId)
                            .Equals(timelineId, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            Func<Dictionary<string, object>, string> lifecycleMarriageRoute = row =>
                ReadString(TryParseJsonObject(ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>(), "route", "");
            List<Dictionary<string, object>> organicMarriageActions =
                lifecycleMarriageActions.Where(row => lifecycleMarriageRoute(row)
                    .Equals("mutual_affinity",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> pregnancyCommitmentMarriageActions =
                lifecycleMarriageActions.Where(row => lifecycleMarriageRoute(row)
                    .Equals("pregnancy_commitment",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            Dictionary<string, Dictionary<string, object>> observedMarriageRows =
                WorldSimulationStore.SelectRelationshipObservedHeroes(connection)
                    .ToDictionary(row => ReadString(row, "hero_id", ""), row => row,
                        StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> observedMarriageProfiles =
                observedMarriageRows.ToDictionary(row => row.Key, row =>
                    TryParseJsonObject(ReadString(row.Value, "profile_json", "{}"))
                        ?? new Dictionary<string, object>(),
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> completedOrganicMarriageReports =
                organicMarriageActions.Where(row => ReadString(row, "status", "")
                    .Equals("completed", StringComparison.OrdinalIgnoreCase)).ToList();
            List<Dictionary<string, object>> completedPregnancyCommitmentMarriageReports =
                pregnancyCommitmentMarriageActions.Where(row => ReadString(row,
                    "status", "").Equals("completed",
                        StringComparison.OrdinalIgnoreCase)).ToList();
            Func<Dictionary<string, object>, bool> reciprocalNativeMarriage = row =>
            {
                string actorId = ReadString(row, "actor_id", "");
                string targetId = ReadString(row, "target_id", "");
                return observedMarriageProfiles.TryGetValue(actorId,
                        out Dictionary<string, object> actorProfile)
                    && observedMarriageProfiles.TryGetValue(targetId,
                        out Dictionary<string, object> targetProfile)
                    && ReadString(actorProfile, "spouseId", "")
                        .Equals(targetId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(targetProfile, "spouseId", "")
                        .Equals(actorId, StringComparison.OrdinalIgnoreCase);
            };
            Func<Dictionary<string, object>, bool> nativeMarriageConfirmationOverdue = row =>
            {
                string actorId = ReadString(row, "actor_id", "");
                string targetId = ReadString(row, "target_id", "");
                if (!observedMarriageRows.TryGetValue(actorId,
                        out Dictionary<string, object> actorObservation)
                    || !observedMarriageRows.TryGetValue(targetId,
                        out Dictionary<string, object> targetObservation))
                    return false;
                Dictionary<string, object> result = TryParseJsonObject(
                    ReadString(row, "result_json", "{}"))
                    ?? new Dictionary<string, object>();
                double reportDay = ReadDouble(result, "worldDay",
                    ReadDouble(row, "world_day", 0d));
                double observedThrough = Math.Min(
                    ReadDouble(actorObservation, "last_observed_day", -1d),
                    ReadDouble(targetObservation, "last_observed_day", -1d));
                return observedThrough >= Math.Floor(reportDay) + 1d;
            };
            Func<Dictionary<string, object>, string> organicPairKey = row =>
            {
                Dictionary<string, object> actionPayload = TryParseJsonObject(
                    ReadString(row, "payload_json", "{}"))
                    ?? new Dictionary<string, object>();
                return FirstNonEmpty(ReadString(actionPayload, "pairKey", ""),
                    RelationshipsService.AmbientPairKey(ReadString(row, "actor_id", ""),
                        ReadString(row, "target_id", "")));
            };
            int romanticMarriages = completedOrganicMarriageReports
                .Where(reciprocalNativeMarriage).Select(organicPairKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int unconfirmedOrganicMarriageReports =
                completedOrganicMarriageReports.Count(row =>
                    !reciprocalNativeMarriage(row)
                    && nativeMarriageConfirmationOverdue(row));
            int duplicateOrganicMarriageReports = Math.Max(0,
                completedOrganicMarriageReports.Count
                - completedOrganicMarriageReports.Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            int pregnancyCommitmentConfirmedMarriages =
                completedPregnancyCommitmentMarriageReports
                    .Where(reciprocalNativeMarriage).Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count();
            int unconfirmedPregnancyCommitmentMarriageReports =
                completedPregnancyCommitmentMarriageReports.Count(row =>
                    !reciprocalNativeMarriage(row)
                    && nativeMarriageConfirmationOverdue(row));
            int duplicatePregnancyCommitmentMarriageReports = Math.Max(0,
                completedPregnancyCommitmentMarriageReports.Count
                - completedPregnancyCommitmentMarriageReports
                    .Select(organicPairKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count());
            int arrangedMarriages = WorldSimulationStore.SelectMarriageEvaluations(connection)
                .Count(row => string.IsNullOrWhiteSpace(mainHeroId)
                    || (!ReadString(row, "hero_a_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)
                        && !ReadString(row, "hero_b_id", "").Equals(mainHeroId, StringComparison.OrdinalIgnoreCase)));
            int affairEligibleLovers = ReadInt(WorldSimulationStore.CountRelationshipPairLifecycle(connection, new Dictionary<string, object>()).FirstOrDefault(), "count", 0);
            Dictionary<string, object> flingParameters =
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                };
            Dictionary<string, object> flingRollTotals = WorldSimulationStore.CountRelationshipFlingRolls(connection, flingParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> flingSparkTotals = WorldSimulationStore.CountRelationshipFlingSparks(connection, flingParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            Dictionary<string, object> flingEncounterTotals = WorldSimulationStore.CountRelationshipFlingEncounters(connection, flingParameters)
                .FirstOrDefault() ?? new Dictionary<string, object>();
            int flingChanceViolations = ReadInt(WorldSimulationStore.CountRelationshipFlingRolls2(connection, flingParameters).FirstOrDefault(), "count", 0);
            int flingSparkChanceViolations = ReadInt(WorldSimulationStore.CountRelationshipFlingSparks2(connection, flingParameters).FirstOrDefault(), "count", 0);
            int flingReceiptViolations = ReadInt(WorldSimulationStore.CountRelationshipFlingEncounters2(connection, flingParameters)
                .FirstOrDefault(), "count", 0);
            int flingCooldownViolations = ReadInt(WorldSimulationStore.SelectOrdered(connection, flingParameters).FirstOrDefault(), "count", 0);
            int flingSameSexConceptionViolations = ReadInt(WorldSimulationStore.CountRelationshipFlingEncounters3(connection, flingParameters).FirstOrDefault(), "count", 0);
            int flingDuplicatePersonDayViolations = ReadInt(WorldSimulationStore.SelectParticipation(connection, flingParameters).FirstOrDefault(), "count", 0);
            Dictionary<string, object> flingTelemetry =
                new Dictionary<string, object>
                {
                    ["judgmentMaximum"] = RelationshipsService.FlingMaximumJudgment,
                    ["chanceAtZeroJudgmentPercent"] = 50,
                    ["chanceAtFortyJudgmentPercent"] = 10,
                    ["sparkChancePercent"] = RelationshipsService.FlingSparkChancePercent,
                    ["cooldownDays"] = RelationshipsService.FlingCooldownDays,
                    ["shortAffairDiscoveryPercent"] =
                        RelationshipsService.FlingDiscoveryChancePercent,
                    ["observedDays"] = ReadInt(flingRollTotals,
                        "observed_days", 0),
                    ["eligibleRolls"] = ReadInt(flingRollTotals, "rolls", 0),
                    ["passes"] = ReadInt(flingRollTotals, "passes", 0),
                    ["unmatched"] = ReadInt(flingRollTotals, "unmatched", 0),
                    ["sparkAttempts"] = ReadInt(flingSparkTotals,
                        "attempts", 0),
                    ["sparkPasses"] = ReadInt(flingSparkTotals,
                        "passes", 0),
                    ["pairs"] = ReadInt(flingEncounterTotals, "pairings", 0),
                    ["sameSexPairs"] = ReadInt(flingEncounterTotals,
                        "same_sex", 0),
                    ["oppositeSexPairs"] = ReadInt(flingEncounterTotals,
                        "opposite_sex", 0),
                    ["shortAffairs"] = ReadInt(flingEncounterTotals,
                        "short_affairs", 0),
                    ["fixedDiscoveries"] = ReadInt(flingEncounterTotals,
                        "fixed_discoveries", 0),
                    ["pregnancyDiscoveries"] = ReadInt(flingEncounterTotals,
                        "pregnancy_discoveries", 0),
                    ["discoveredEncounters"] = ReadInt(flingEncounterTotals,
                        "discovered_encounters", 0),
                    ["conceptionAttempts"] = ReadInt(flingEncounterTotals,
                        "conception_attempts", 0),
                    ["conceptions"] = ReadInt(flingEncounterTotals,
                        "conceptions", 0),
                    ["chanceViolations"] = flingChanceViolations,
                    ["sparkChanceViolations"] =
                        flingSparkChanceViolations,
                    ["receiptViolations"] = flingReceiptViolations,
                    ["cooldownViolations"] = flingCooldownViolations,
                    ["sameSexConceptionViolations"] =
                        flingSameSexConceptionViolations,
                    ["duplicatePersonDayViolations"] =
                        flingDuplicatePersonDayViolations,
                    ["recentEncounters"] = WorldSimulationStore.SelectRelationshipFlingEncounters(connection, flingParameters),
                    ["recentSparks"] = WorldSimulationStore.SelectRelationshipFlingSparks(connection, flingParameters),
                    ["recentRolls"] = WorldSimulationStore.SelectRelationshipFlingRolls(connection, flingParameters)
                };
            Dictionary<string, object> lifecycleCounts = new Dictionary<string, object>
            {
                ["lovers"] = lifecycle.Count(x => ReadInt(x, "lover_active", 0) == 1),
                ["activeAffairs"] = lifecycle.Count(x => ReadInt(x, "affair_active", 0) == 1),
                ["affairEligibleLovers"] = affairEligibleLovers,
                ["flirtationStagePairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals("flirtation",
                        StringComparison.OrdinalIgnoreCase)),
                ["growingAttractionPairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals(
                        "growing_attraction", StringComparison.OrdinalIgnoreCase)),
                ["seriousAttractionPairs"] = lifecycle.Count(x =>
                    ReadString(x, "romance_stage", "").Equals(
                        "serious_attraction", StringComparison.OrdinalIgnoreCase)),
                ["flirtationAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "flirt_attempted", 0) == 1),
                ["flirtationPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "flirt_passed", 0) == 1),
                ["affairJudgmentAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_attempted", 0) == 1),
                ["affairJudgmentPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_passed", 0) == 1),
                ["romanceBonusTenPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "romance_positive_bonus", 0)
                        == RelationshipsService.RomanceFlirtationPositiveChanceBonus),
                ["romanceBonusTwentyPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "romance_positive_bonus", 0)
                        == RelationshipsService.RomanceGrowingPositiveChanceBonus),
                ["restrictedLoverAttempts"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_attempted", 0) == 1),
                ["restrictedLoverPasses"] = lifecycle.Count(x =>
                    ReadInt(x, "affair_passed", 0) == 1),
                ["married"] = lifecycle.Count(x => ReadInt(x, "married", 0) == 1),
                ["estrangedDirections"] = lifecycle.Sum(x => ReadInt(x, "estranged_a_to_b", 0) + ReadInt(x, "estranged_b_to_a", 0)),
                ["divorced"] = lifecycle.Count(x => ReadInt(x, "divorced", 0) == 1),
                ["romanticMarriages"] = romanticMarriages,
                ["romanticMarriageAttempts"] = organicMarriageActions.Count,
                ["romanticMarriageCompletedReports"] = completedOrganicMarriageReports.Count,
                ["romanticMarriageUnconfirmedReports"] = unconfirmedOrganicMarriageReports,
                ["romanticMarriageDuplicateReports"] = duplicateOrganicMarriageReports,
                ["pregnancyCommitmentMarriageAttempts"] =
                    pregnancyCommitmentMarriageActions.Count,
                ["pregnancyCommitmentMarriageCompletedReports"] =
                    completedPregnancyCommitmentMarriageReports.Count,
                ["pregnancyCommitmentConfirmedMarriages"] =
                    pregnancyCommitmentConfirmedMarriages,
                ["pregnancyCommitmentMarriageUnconfirmedReports"] =
                    unconfirmedPregnancyCommitmentMarriageReports,
                ["pregnancyCommitmentMarriageDuplicateReports"] =
                    duplicatePregnancyCommitmentMarriageReports,
                ["romanticMarriageBlockedPairs"] = lifecycle.Count(x =>
                    ReadInt(x, "marriage_blocked", 0) == 1),
                ["arrangedMarriages"] = arrangedMarriages,
                ["affairDiscoveries"] = ReadInt(WorldSimulationStore.CountRumorOccurrences(connection, flingParameters).FirstOrDefault(), "count", 0),
                ["flings"] = flingTelemetry,
                ["pregnancyCommitments"] = TableCount(connection,
                    "relationship_conception_commitments"),
                ["pregnancyCommitmentMarriages"] = ReadInt(WorldSimulationStore.CountRelationshipConceptionCommitments(connection)
                    .FirstOrDefault(), "count", 0),
                ["pregnancyCommitmentBreakups"] = ReadInt(WorldSimulationStore.CountRelationshipConceptionCommitments2(connection)
                    .FirstOrDefault(), "count", 0),
                ["pregnancyCommitmentChanceViolations"] = ReadInt(WorldSimulationStore.CountRelationshipConceptionCommitments3(connection)
                    .FirstOrDefault(), "count", 0),
                ["affairMarriageCommitments"] = TableCount(connection,
                    "relationship_affair_commitments"),
                ["affairMarriageCommitmentMarriages"] = ReadInt(WorldSimulationStore.CountRelationshipAffairCommitments(connection)
                    .FirstOrDefault(), "count", 0),
                ["affairMarriageChanceCapViolations"] = ReadInt(WorldSimulationStore.CountRelationshipAffairCommitments2(connection)
                    .FirstOrDefault(), "count", 0),
                ["incidents"] = TableCount(connection, "relationship_incidents"),
                ["conceptions"] = StatusCounts(connection, "conceptions", "status"),
                ["concealedParentage"] = ReadInt(WorldSimulationStore.CountParentage(connection).FirstOrDefault(), "count", 0)
            };
            Dictionary<string, object> playerParameter = new Dictionary<string, object>
            {
                ["player"] = mainHeroId ?? string.Empty,
                ["latest"] = latestDay
            };
            int pending = ReadInt(WorldSimulationStore.CountRelationshipNativeTargets(connection, playerParameter).FirstOrDefault(), "count", 0);
            int overdue = ReadInt(WorldSimulationStore.CountRelationshipNativeTargets2(connection, playerParameter).FirstOrDefault(), "count", 0);
            int unrepresentedNativeFlags = RelationshipsService.CountUnrepresentedNativeFlags(connection, mainHeroId);
            pending += unrepresentedNativeFlags;
            Dictionary<string, object> latestNativeBatch = WorldSimulationStore.SelectRelationshipNativeSyncBatches(connection, new Dictionary<string, object> { ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            int clientRemaining = ReadInt(latestNativeBatch, "client_remaining", 0);
            int persistedTargetFailures = ReadInt(WorldSimulationStore.CountRelationshipNativeTargets3(connection, playerParameter).FirstOrDefault(), "count", 0);
            int latestClientFailures = ReadInt(latestNativeBatch, "client_failed", 0);
            int nativeFailures = Math.Max(persistedTargetFailures, latestClientFailures);
            int directorFailures = ReadInt(WorldSimulationStore.CountRelationshipDirectorActions(connection, playerParameter).FirstOrDefault(), "count", 0);
            int failed = nativeFailures + directorFailures;
            Dictionary<string, object> openingSeed = WorldSimulationStore.SelectRelationshipOpeningSeedRuns(connection, pairParameters).FirstOrDefault()
                ?? new Dictionary<string, object>();
            openingSeed["directionalCount"] =
                ReadInt(openingSeed, "completed_pairs", 0) * 2;
            openingSeed["averageDelta"] = ReadDouble(openingSeed,
                "average_delta", 0d);
            openingSeed["medianDelta"] = ReadDouble(openingSeed,
                "median_delta", 0d);
            openingSeed["minimumDelta"] = ReadInt(openingSeed,
                "minimum_delta", 0);
            openingSeed["maximumDelta"] = ReadInt(openingSeed,
                "maximum_delta", 0);
            openingSeed["startingBands"] = TryParseJsonObject(ReadString(
                openingSeed, "starting_bands_json", "{}"))
                ?? new Dictionary<string, object>();
            openingSeed["resultingBands"] = TryParseJsonObject(ReadString(
                openingSeed, "resulting_bands_json", "{}"))
                ?? new Dictionary<string, object>();
            string openingSeedStatus = ReadString(openingSeed, "status", "");
            double lastProcessed = RelationshipsService.RelationshipLastProcessedDay(connection, timelineId);
            bool enabled = LatestFeatureFlag(connection, "passiveRelationshipsEnabled", true);
            int eligible = LatestPopulationValue(connection, "eligibleNpcCount");
            string status = !enabled ? "disabled" : pairCount == 0 ? "insufficient_data"
                : failed > 0 || notableSyncErrors > 0 || missingMbti > 0 || typeMismatches > 0
                    || openingSeedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase)
                    || unconfirmedOrganicMarriageReports > 0
                    || unconfirmedPregnancyCommitmentMarriageReports > 0
                    || flingChanceViolations > 0
                    || flingSparkChanceViolations > 0
                    || flingReceiptViolations > 0
                    || flingCooldownViolations > 0
                    || flingSameSexConceptionViolations > 0
                    || flingDuplicatePersonDayViolations > 0
                    ? "error"
                : lastProcessed < 0d ? "insufficient_data"
                : eligible > 1 && latestDay - lastProcessed > 1d || notableSyncOverdue > 0 || overdue > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (nativeFailures > 0) anomalies.Add(nativeFailures + " native relationship projections failed in the latest or still-persisted plan.");
            if (directorFailures > 0) anomalies.Add(directorFailures + " relationship lifecycle actions failed.");
            if (unconfirmedOrganicMarriageReports > 0)
                anomalies.Add(unconfirmedOrganicMarriageReports
                    + " organic marriage completion reports lack reciprocal native spouse confirmation.");
            if (duplicateOrganicMarriageReports > 0)
                anomalies.Add(duplicateOrganicMarriageReports
                    + " duplicate organic marriage completion reports were recorded for existing pairs.");
            if (unconfirmedPregnancyCommitmentMarriageReports > 0)
                anomalies.Add(unconfirmedPregnancyCommitmentMarriageReports
                    + " pregnancy-commitment marriage completion reports lack reciprocal native spouse confirmation.");
            if (duplicatePregnancyCommitmentMarriageReports > 0)
                anomalies.Add(duplicatePregnancyCommitmentMarriageReports
                    + " duplicate pregnancy-commitment marriage completion reports were recorded for existing pairs.");
            if (flingChanceViolations > 0)
                anomalies.Add(flingChanceViolations
                    + " fling roll receipts violate the judgment/chance contract.");
            if (flingSparkChanceViolations > 0)
                anomalies.Add(flingSparkChanceViolations
                    + " fling spark receipts violate the fixed twenty-five-percent contract.");
            if (flingReceiptViolations > 0)
                anomalies.Add(flingReceiptViolations
                    + " fling encounters lack two passed participant receipts or a passed same-location spark receipt.");
            if (flingCooldownViolations > 0)
                anomalies.Add(flingCooldownViolations
                    + " fling participants were reused inside the seven-day cooldown.");
            if (flingSameSexConceptionViolations > 0)
                anomalies.Add(flingSameSexConceptionViolations
                    + " same-sex fling encounters incorrectly created conception work.");
            if (flingDuplicatePersonDayViolations > 0)
                anomalies.Add(flingDuplicatePersonDayViolations
                    + " characters participated in more than one fling on the same day.");
            if (notableSyncErrors > 0) anomalies.Add(notableSyncErrors + " notable MBTI native-trait synchronizations failed or were contradicted.");
            if (missingMbti > 0) anomalies.Add(missingMbti + " relationship pairs contain a missing MBTI type.");
            if (typeMismatches > 0) anomalies.Add(typeMismatches + " relationship pairs disagree with a notable's persisted MBTI assignment.");
            if (notableSyncOverdue > 0) anomalies.Add(notableSyncOverdue + " notable native-trait synchronizations are overdue.");
            if (enabled && eligible > 1 && lastProcessed >= 0 && latestDay - lastProcessed > 1d) anomalies.Add("The daily relationship producer is overdue.");
            if (overdue > 0) anomalies.Add(overdue + " native relationship projections remain unconfirmed after one campaign day.");
            if (openingSeedStatus.Equals("failed", StringComparison.OrdinalIgnoreCase))
                anomalies.Add("Campaign-opening relationship seeding failed: "
                    + ReadString(openingSeed, "last_error", "unknown error"));
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Relationships, Romance, and Family", ["status"] = status,
                ["latestSuccessfulDay"] = lastProcessed, ["pairCount"] = pairCount,
                ["uniqueNpcCount"] = uniqueNpcCount,
                ["directionalEdgeCount"] = pairCount * 2,
                ["newPairsLatestDay"] = ReadInt(pairSummary, "new_pairs", 0),
                ["partyPairs"] = ReadInt(pairSummary, "party_pairs", 0),
                ["settlementPairs"] = ReadInt(pairSummary, "settlement_pairs", 0),
                ["positiveLatestRolls"] = ReadInt(pairSummary, "positive_rolls", 0),
                ["negativeLatestRolls"] = ReadInt(pairSummary, "negative_rolls", 0),
                ["symmetricPairCount"] = ReadInt(pairSummary, "symmetric_pairs", 0),
                ["asymmetricPairCount"] = pairCount - ReadInt(pairSummary, "symmetric_pairs", 0),
                ["affinity"] = AffinitySummaryFromHistogram(affinityHistogram),
                ["bands"] = bands.ToDictionary(x => x.Key, x => (object)x.Value, StringComparer.OrdinalIgnoreCase),
                ["tags"] = tags.Values.OrderByDescending(x => ReadInt(x, "directionalEdgeCount", 0)).ToList(),
                ["nativeSync"] = new Dictionary<string, object>
                {
                    ["pending"] = pending, ["failed"] = nativeFailures,
                    ["aligned"] = Math.Max(0, pairCount - pending - persistedTargetFailures),
                    ["chemistryPending"] = pending,
                    ["unrepresentedNativeFlags"] = unrepresentedNativeFlags,
                    ["overdue"] = overdue,
                    ["planId"] = ReadString(latestNativeBatch, "plan_id", ""),
                    ["planTargetCount"] = ReadInt(latestNativeBatch, "target_count", 0),
                    ["clientApplied"] = ReadInt(latestNativeBatch, "client_applied", 0),
                    ["clientAlreadyAligned"] = ReadInt(latestNativeBatch, "client_already_aligned", 0),
                    ["clientObsolete"] = ReadInt(latestNativeBatch, "client_obsolete", 0),
                    ["clientFailed"] = ReadInt(latestNativeBatch, "client_failed", 0),
                    ["clientRemaining"] = clientRemaining,
                    ["clientDurationMs"] = ReadInt(latestNativeBatch, "client_duration_ms", 0),
                    ["lastClientReportDay"] = ReadDouble(latestNativeBatch, "last_report_day", -1d)
                },
                ["notableMbti"] = new Dictionary<string, object>
                {
                    ["assignedCount"] = notableProfiles.Count,
                    ["nobleNotablePairCount"] = nobleNotablePairs,
                    ["missingPairTypes"] = missingMbti,
                    ["typeMismatches"] = typeMismatches,
                    ["types"] = notableTypes,
                    ["nativeSyncStatuses"] = notableSync
                },
                ["openingSeed"] = openingSeed,
                ["lifecycle"] = lifecycleCounts,
                ["marriageFunnel"] = WorldTestCounterTotals(
                    connection, campaignId, timelineId, "marriages"),
                ["politicalMarriage"] = new Dictionary<string, object>
                {
                    ["rolls"] = TableCount(connection, "marriage_leader_rolls"),
                    ["evaluations"] = TableCount(connection, "marriage_evaluations"),
                    ["romanticMarriages"] = romanticMarriages,
                    ["arrangedMarriages"] = arrangedMarriages,
                    ["statuses"] = StatusCounts(connection, "marriage_evaluations", "status"),
                    ["proposalPolicy"] = Reign.Relationships.ArrangedMarriagePolicy.Version,
                    ["completedPeriods"] = WorldSimulationStore.SelectSqliteMaster2(connection).Any()
                        ? WorldSimulationStore.SelectArrangedMarriagePeriods(connection, new Dictionary<string,object>{{"t",timelineId}})
                        : new List<Dictionary<string,object>>()
                },
                ["publicStanding"] = ReadDictionary(standingAndPolitical,
                    "publicStanding") ?? new Dictionary<string, object>(),
                ["politicalNetwork"] = ReadDictionary(standingAndPolitical,
                    "politicalNetwork") ?? new Dictionary<string, object>(),
                ["effectiveBands"] = standingAndPolitical.TryGetValue(
                    "effectiveBands", out object effectiveBands)
                    ? effectiveBands : new Dictionary<string, int>(),
                ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, object> BuildWorldTestDiplomacy(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay, double elapsedObservedDays,
            Dictionary<string, object> nativePayload)
        {
            List<Dictionary<string, object>> events = ReadWorldTestDiplomacyEvents(campaignId);
            Dictionary<string, object> state = DiplomacyService.ReadDirectorState(campaignId);
            List<Dictionary<string, object>> actions = ReadWorldTestActions(campaignId);
            List<Dictionary<string, object>> diplomaticActions = actions.Where(x =>
            {
                Dictionary<string, object> record = ReadDictionary(x, "record") ?? x;
                return ReadString(record, "source", "").Equals("world_diplomacy_director", StringComparison.OrdinalIgnoreCase);
            }).ToList();
            int failed = diplomaticActions.Count(x => IsTerminalFailureStatus(ReadString(x, "status", "")));
            int pending = diplomaticActions.Count(x => IsPendingStatus(ReadString(x, "status", "")));
            List<Dictionary<string, object>> nativeAgreements =
                ReadDictionaryList(ReadDictionary(nativePayload,
                    "nativeOutcomes") ?? new Dictionary<string, object>(),
                    "agreements");
            List<Dictionary<string, object>> missingAgreementReceipts = events
                .Where(x => ReadBool(x, "accepted", false)
                    && ReadString(x, "executionStatus", "")
                        .Equals("completed", StringComparison.OrdinalIgnoreCase)
                    && latestDay - ReadDouble(x, "worldDay", latestDay) > 0.01d)
                .Where(x => ExpectedAgreementKindForDiplomacyCommand(
                    ReadString(x, "command", "")) != "")
                .Where(x => !WorldTestNativeAgreementExists(nativeAgreements,
                    ExpectedAgreementKindForDiplomacyCommand(
                        ReadString(x, "command", "")),
                    ReadString(x, "actorKingdomId", ""),
                    ReadString(x, "targetKingdomId", "")))
                .ToList();
            double lastDirectorEvent = ReadDouble(state, "lastEvaluatedDay", -1d);
            double lastEvaluation = Math.Max(lastDirectorEvent,
                LatestSuccessfulDiplomacyEvaluationDay(connection, campaignId,
                    timelineId));
            bool enabled = LatestFeatureFlagFromPayload(campaignId, "worldDiplomacyEnabled", true);
            string status = !enabled ? "disabled" : lastEvaluation < 0 ? "insufficient_data"
                : failed > 0 || missingAgreementReceipts.Count > 0 ? "error"
                : latestDay - lastEvaluation > 3d ? "warning" : "healthy";
            Dictionary<string, int> commands = CountBy(events, "command");
            Dictionary<string, int> outcomes = CountBy(events, "outcome");
            Dictionary<string, int> actors = CountBy(events, "actorKingdomName");
            List<string> anomalies = new List<string>();
            if (failed > 0) anomalies.Add(failed + " diplomatic actions failed.");
            if (lastEvaluation >= 0 && latestDay - lastEvaluation > 3d) anomalies.Add("Diplomacy evaluation is past its three-day cadence.");
            if (pending > 0) anomalies.Add(pending + " diplomatic actions remain pending.");
            if (missingAgreementReceipts.Count > 0)
                anomalies.Add(missingAgreementReceipts.Count
                    + " completed treaty actions are absent from the next native agreement heartbeat.");
            return new Dictionary<string, object>
            {
                ["name"] = "NPC Diplomacy", ["status"] = status, ["latestSuccessfulDay"] = lastEvaluation,
                ["eventCount"] = events.Count, ["accepted"] = events.Count(x => ReadBool(x, "accepted", false)),
                ["refused"] = events.Count(x => !ReadBool(x, "accepted", false)), ["pendingActions"] = pending, ["failedActions"] = failed,
                ["undeliveredAnnouncements"] = events.Count(x =>
                    ReadBool(x, "announcementReady", false)
                    && !ReadBool(x, "delivered", false)
                    && !ReadBool(x, "acknowledged", false)),
                ["unacknowledgedAnnouncements"] = events.Count(x =>
                    ReadBool(x, "announcementReady", false)
                    && !ReadBool(x, "acknowledged", false)),
                ["nativeAgreementMismatches"] = missingAgreementReceipts.Count,
                ["missingAgreementReceipts"] = missingAgreementReceipts,
                ["eventsPerSeason"] = elapsedObservedDays > 0
                    ? Math.Round(events.Count / Math.Max(1d, elapsedObservedDays / 31.5d), 2) : 0d,
                ["eventsPerYear"] = elapsedObservedDays > 0
                    ? Math.Round(events.Count / Math.Max(1d, elapsedObservedDays / 126d), 2) : 0d,
                ["commands"] = commands, ["outcomes"] = outcomes, ["actorKingdoms"] = actors,
                ["targetKingdoms"] = CountBy(events, "targetKingdomName"),
                ["intentFamilies"] = CountDiplomacyIntentFamilies(events), ["anomalies"] = anomalies,
                ["recentEvents"] = events.OrderByDescending(x => ReadDouble(x, "worldDay", 0d)).Take(20).ToList()
            };
        }

        private static double LatestSuccessfulDiplomacyEvaluationDay(
            ReignDbConnection connection, string campaignId, string timelineId)
        {
            if (connection == null) return -1d;
            foreach (Dictionary<string, object> row in WorldSimulationStore.SelectWorldTestChunkCounters(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId ?? string.Empty,
                    ["timeline"] = string.IsNullOrWhiteSpace(timelineId)
                        ? "main" : timelineId
                }))
            {
                Dictionary<string, object> counters = TryParseJsonObject(
                    ReadString(row, "counters_json", "{}"))
                    ?? new Dictionary<string, object>();
                if (ReadInt(counters, "completed", 0) > 0
                    && ReadInt(counters, "failures", 0) == 0)
                    return ReadDouble(row, "day_key", -1d);
            }
            return -1d;
        }

        private static string ExpectedAgreementKindForDiplomacyCommand(
            string command)
        {
            switch (Program.NormalizeLookup(command))
            {
                case "sign_trade_agreement": return "trade_agreement";
                case "sign_non_aggression_pact": return "non_aggression_pact";
                case "sign_alliance": return "alliance";
                case "sign_defensive_pact": return "defensive_pact";
                default: return "";
            }
        }

        private static bool WorldTestNativeAgreementExists(
            IEnumerable<Dictionary<string, object>> agreements, string kind,
            string actorKingdomId, string targetKingdomId)
        {
            return (agreements ?? Enumerable.Empty<Dictionary<string, object>>())
                .Any(row => ReadBool(row, "isActive", true)
                    && ReadString(row, "kind", "").Equals(kind,
                        StringComparison.OrdinalIgnoreCase)
                    && ((ReadString(row, "actorKingdomId", "").Equals(
                            actorKingdomId, StringComparison.OrdinalIgnoreCase)
                         && ReadString(row, "targetKingdomId", "").Equals(
                            targetKingdomId, StringComparison.OrdinalIgnoreCase))
                        || (ReadString(row, "actorKingdomId", "").Equals(
                            targetKingdomId, StringComparison.OrdinalIgnoreCase)
                         && ReadString(row, "targetKingdomId", "").Equals(
                            actorKingdomId, StringComparison.OrdinalIgnoreCase))));
        }

        private static Dictionary<string, object> BuildWorldTestRumors(
            ReignDbConnection connection,
            string campaignId,
            string timelineId,
            double latestDay)
        {
            Dictionary<string, object> rumorSummary = WorldSimulationStore.CountRumorOccurrences2(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId,
                    ["day"] = latestDay
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> outcomeSummary = WorldSimulationStore.CountWorldHistoryEvents8(connection, new Dictionary<string, object>
                {
                    ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            int rumorCount = ReadInt(rumorSummary, "rumor_count", 0);
            int expiredActive = ReadInt(rumorSummary, "expired_active", 0);
            double lastOccurrenceDay = ReadDouble(rumorSummary,
                "last_occurrence_day", -1d);
            int sourceOutcomeCount = ReadInt(outcomeSummary,
                "source_count", 0);
            int processedSourceOutcomes = ReadInt(outcomeSummary,
                "processed_count", 0);
            int failedSourceOutcomes = ReadInt(outcomeSummary,
                "failed_count", 0);
            int pendingSourceOutcomeCount = ReadInt(outcomeSummary,
                "pending_count", 0);
            double oldestPendingDay = ReadDouble(outcomeSummary,
                "oldest_pending_day", -1d);
            double lastProcessedSourceDay = ReadDouble(outcomeSummary,
                "last_processed_day", -1d);
            Dictionary<string, int> funnel =
                WorldTestCounterTotals(connection, campaignId, timelineId, "rumors");
            bool enabled = LatestFeatureFlag(connection, "rumorsEnabled", true);
            string status = !enabled ? "disabled" : failedSourceOutcomes > 0 ? "error"
                : pendingSourceOutcomeCount > ReputationService.SocialWorldHistoryBatchSize * 2 ? "warning"
                : sourceOutcomeCount > 0 && processedSourceOutcomes > 0 ? "healthy"
                : rumorCount == 0 ? "insufficient_data"
                : expiredActive > 0 ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (expiredActive > 0) anomalies.Add(expiredActive + " expired rumor occurrences are still marked active.");
            if (pendingSourceOutcomeCount > ReputationService.SocialWorldHistoryBatchSize * 2)
                anomalies.Add(pendingSourceOutcomeCount + " social outcomes are waiting for rumor/reputation processing.");
            if (failedSourceOutcomes > 0)
                anomalies.Add(failedSourceOutcomes + " social outcomes exhausted their processing retries.");
            Dictionary<string, int> statuses = WorldTestGroupedCounts(connection,
                WorldSimulationStore.SelectRumorOccurrencesSql,
                campaignId, timelineId);
            Dictionary<string, int> sources = WorldTestGroupedCounts(connection,
                WorldSimulationStore.SelectRumorOccurrencesSql2,
                campaignId, timelineId);
            Dictionary<string, int> tagStatuses = WorldTestGroupedCounts(connection,
                WorldSimulationStore.SelectRumorSubjectTagsSql,
                campaignId, timelineId);
            Dictionary<string, object> supportingCounts = WorldSimulationStore.SelectRumorSubjectTags(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).FirstOrDefault() ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> reach = WorldSimulationStore.SelectRumorOccurrences(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).Select(row => new Dictionary<string, object>
                {
                    ["rumorId"] = ReadString(row, "rumor_id", ""),
                    ["claim"] = ReadString(row, "claim", ""),
                    ["status"] = ReadString(row, "status", ""),
                    ["archetype"] = ReadString(row, "archetype", ""),
                    ["subjectCount"] = ReadInt(row, "subject_count", 0),
                    ["tagCount"] = ReadInt(row, "tag_count", 0),
                    ["promoted"] = ReadInt(row, "promoted", 0) == 1,
                    ["streakCount"] = ReadInt(row, "streak_count", 0)
                }).ToList();
            return new Dictionary<string, object>
            {
                ["name"] = "Character-Owned Social Reputation", ["status"] = status,
                ["latestSuccessfulDay"] = Math.Max(lastOccurrenceDay, lastProcessedSourceDay),
                ["rumorCount"] = rumorCount,
                ["subjectTagCount"] = ReadInt(supportingCounts, "tag_count", 0),
                ["statuses"] = statuses, ["sources"] = sources,
                ["tagStatuses"] = tagStatuses,
                ["promotionResults"] = ReadInt(supportingCounts,
                    "promotion_count", 0),
                ["promotedOccurrences"] = ReadInt(supportingCounts,
                    "promoted_count", 0),
                ["durableReputations"] = ReadInt(supportingCounts,
                    "reputation_count", 0), ["expiredActive"] = expiredActive,
                ["sourceOutcomeCount"] = sourceOutcomeCount,
                ["processedSourceOutcomes"] = processedSourceOutcomes,
                ["pendingSourceOutcomes"] = pendingSourceOutcomeCount,
                ["failedSourceOutcomes"] = failedSourceOutcomes,
                ["oldestPendingSourceDay"] = oldestPendingDay,
                ["funnel"] = funnel,
                ["eligibleHooks"] = funnel.TryGetValue("eligibleHooks", out int eligibleHooks) ? eligibleHooks : 0,
                ["exposureAttempts"] = funnel.TryGetValue("exposureAttempts", out int exposureAttempts) ? exposureAttempts : 0,
                ["exposurePassed"] = funnel.TryGetValue("exposurePassed", out int exposurePassed) ? exposurePassed : 0,
                ["exposureFailed"] = funnel.TryGetValue("exposureFailed", out int exposureFailed) ? exposureFailed : 0,
                ["duplicateSourcesSuppressed"] = funnel.TryGetValue("duplicateSourcesSuppressed", out int duplicates) ? duplicates : 0,
                ["occurrencesCreated"] = funnel.TryGetValue("occurrencesCreated", out int occurrences) ? occurrences : 0,
                ["promotionPassed"] = funnel.TryGetValue("promotionPassed", out int promotionPassed) ? promotionPassed : 0,
                ["promotionFailed"] = funnel.TryGetValue("promotionFailed", out int promotionFailed) ? promotionFailed : 0,
                ["promotionEligible"] = (funnel.TryGetValue("promotionPassed", out int passedPromotions) ? passedPromotions : 0)
                    + (funnel.TryGetValue("promotionFailed", out int failedPromotions) ? failedPromotions : 0),
                ["reach"] = reach, ["anomalies"] = anomalies
            };
        }

        private static Dictionary<string, int> WorldTestGroupedCounts(
            ReignDbConnection connection, string sql, string campaignId,
            string timelineId)
        {
            return WorldSimulationStore.QuerySql(connection, sql, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                }).Where(row => !string.IsNullOrWhiteSpace(
                    ReadString(row, "key", "")))
                .ToDictionary(row => ReadString(row, "key", ""),
                    row => ReadInt(row, "count", 0),
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> BuildWorldTestRebellions(ReignDbConnection connection, string campaignId, string timelineId, double latestDay)
        {
            List<Dictionary<string, object>> rolls = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "weeklyRolls");
            List<Dictionary<string, object>> movements = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "movements");
            List<Dictionary<string, object>> memberships = LatestNativeArray(connection, campaignId, timelineId, "rebellions", "memberships");
            List<Dictionary<string, object>> recognitionAttempts = WorldSimulationStore.SelectRebellionRecognitionAttempts(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId
                });
            int eligible = rolls.Count(x => ReadBool(x, "wasEligible", false));
            int triggered = rolls.Count(x => ReadBool(x, "triggered", false));
            bool playerIncluded = rolls.Any(x => ReadBool(x, "isPlayerClan", false)) || memberships.Any(x => ReadBool(x, "automaticNpcSelection", false) && ReadBool(x, "isPlayerClan", false));
            double lastRollDay = rolls.Count == 0 ? -1d : rolls.Max(x => ReadDouble(x, "rolledDay", -1d));
            List<int> missingWeekIndexes = MissingRebellionWeekIndexes(rolls);
            bool enabled = LatestFeatureFlag(connection, "rebellionsEnabled", true);
            string status = !enabled ? "disabled" : rolls.Count == 0 ? "insufficient_data"
                : playerIncluded ? "error"
                : latestDay - lastRollDay > 8d || missingWeekIndexes.Count > 0
                    ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (playerIncluded) anomalies.Add("The player appeared in an automatic NPC rebellion roll or side selection.");
            if (lastRollDay >= 0 && latestDay - lastRollDay > 8d) anomalies.Add("Weekly rebellion rolls are overdue by more than one campaign day.");
            if (missingWeekIndexes.Count > 0) anomalies.Add(
                "Weekly rebellion roll sets are missing for week indexes: "
                + string.Join(", ", missingWeekIndexes.Select(value =>
                    value.ToString(CultureInfo.InvariantCulture))) + ".");
            int recognitionQueueFailures = recognitionAttempts.Count(x =>
                ReadString(x, "status", "") == "accepted_queue_failed");
            int recognitionChanceViolations = recognitionAttempts.Count(x =>
                ReadInt(x, "chance_percent", 0) < 1
                || ReadInt(x, "chance_percent", 0)
                    > RebellionService.RebellionRecognitionMaximumChancePercent);
            if (recognitionQueueFailures > 0)
                anomalies.Add("One or more accepted rebel-independence recognition attempts failed to queue their exact negotiated resolution.");
            if (recognitionChanceViolations > 0)
                anomalies.Add("A rebel-independence recognition attempt escaped the hard 1-30 percent acceptance range.");
            if (recognitionQueueFailures > 0 || recognitionChanceViolations > 0)
                status = "error";
            return new Dictionary<string, object>
            {
                ["name"] = "Relationship-Triggered Rebellions", ["status"] = status, ["latestSuccessfulDay"] = lastRollDay,
                ["configuredWeeklyChancePercent"] = RebellionService.RebellionWeeklyOutbreakPercent,
                ["configuredRelationshipThreshold"] = RebellionService.RebellionRelationshipThreshold,
                ["weeklyRollCount"] = rolls.Count, ["eligibleRollCount"] = eligible, ["triggeredRollCount"] = triggered,
                ["observedSuccessPercent"] = eligible == 0 ? 0d : Math.Round(100d * triggered / eligible, 2),
                ["sampleStatus"] = eligible < 30 ? "insufficient_data" : "informational",
                ["missingWeeklyRollSetCount"] = missingWeekIndexes.Count,
                ["missingWeeklyRollWeekIndexes"] = missingWeekIndexes,
                ["playerAutomaticInclusion"] = playerIncluded,
                ["movementCount"] = movements.Count, ["activeCivilWars"] = movements.Count(x => ReadString(x, "stage", "") == "civil_war"),
                ["resolvedCivilWars"] = movements.Count(x => ReadBool(x, "resolutionApplied", false) || ReadString(x, "stage", "") == "resolved"),
                ["resolutionCauses"] = CountBy(movements, "resolutionCause"),
                ["membershipSides"] = CountBy(memberships, "side"),
                ["loneRebellions"] = movements.Count(m => memberships.Count(x => ReadString(x, "movementId", "") == ReadString(m, "movementId", "") && ReadString(x, "side", "") == "rebel") <= 1),
                ["fateOutcomes"] = CountBy(memberships, "fateOutcome"), ["anomalies"] = anomalies,
                ["recognitionAttemptCount"] = recognitionAttempts.Count,
                ["recognitionAcceptedCount"] = recognitionAttempts.Count(x =>
                    ReadString(x, "status", "").StartsWith("accepted",
                        StringComparison.OrdinalIgnoreCase)),
                ["recognitionQueueFailureCount"] = recognitionQueueFailures,
                ["recognitionChanceViolationCount"] = recognitionChanceViolations,
                ["recognitionMaximumChancePercent"] = RebellionService.RebellionRecognitionMaximumChancePercent,
                ["recognitionStatuses"] = CountBy(recognitionAttempts, "status"),
                ["recentRecognitionAttempts"] = recognitionAttempts.Take(20).ToList(),
                ["recentMovements"] = movements.OrderByDescending(x => ReadDouble(x, "updatedDay", 0d)).Take(20).ToList()
            };
        }

        internal static Dictionary<string, object> BuildWorldTestWarEconomy(
            Dictionary<string, object> nativePayload, double latestDay)
        {
            Dictionary<string, object> economy = ReadDictionary(nativePayload,
                "warEconomy") ?? new Dictionary<string, object>();
            bool active = ReadBool(economy, "active", false);
            bool hooksHealthy = ReadBool(economy, "allHooksHealthy", false) && ReadBool(economy, "allModelsHealthy", false);
            bool versioned = ReadInt(economy, "schemaVersion", 0) >= 2;
            var raidFood = ReadDictionary(economy, "raidFood") ?? new Dictionary<string, object>();
            bool raidFoodCurrent = ReadInt(economy, "raidFoodEvidenceVersion", 0) == 1
                && ReadString(raidFood, "schema", "") == "reign-raid-food-v1"
                && ReadBool(raidFood, "hooksHealthy", false) && ReadInt(raidFood, "unsupported", 0) == 0
                && !ReadBool(raidFood, "fixtureTainted", true)
                && !ReadBool(economy, "raidFoodVillagesTruncated", true) && !ReadBool(economy, "raidFoodHoldingsTruncated", true);
            bool fresh = versioned && Math.Abs(latestDay - ReadDouble(economy, "observedDay", -1000)) <= 1
                && DateTime.TryParse(ReadString(economy, "observedUtc", ""), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTime observedUtc)
                && (DateTime.UtcNow - observedUtc.ToUniversalTime()).TotalMinutes >= -1
                && (DateTime.UtcNow - observedUtc.ToUniversalTime()).TotalMinutes <= 10;
            var accounting = ReadDictionary(economy, "accounting") ?? new Dictionary<string, object>();
            int unsupported = ReadInt(accounting, "unsupportedEvents", 0), pending = ReadInt(accounting, "pendingTransactions", 0);
            int mismatches = ReadInt(economy, "patrolConservationMismatches", 0);
            int duplicateCreations = ReadInt(economy,
                "retinueDuplicateCreations", 0);
            double reliefShipped = ReadDouble(economy, "reliefFoodShipped", 0d);
            double reliefDelivered = ReadDouble(economy, "reliefFoodDelivered", 0d);
            double reliefLost = ReadDouble(economy, "reliefFoodLost", 0d);
            bool reliefConserved = ReliefFoodIsConserved(
                reliefShipped, reliefDelivered, reliefLost);
            string patchError = ReadString(economy, "lastPatchError", "");
            string status = economy.Count == 0 || !versioned || !fresh ? "insufficient_data"
                : !active || !hooksHealthy || !string.IsNullOrWhiteSpace(patchError)
                    || mismatches > 0 || !reliefConserved || unsupported > 0 || pending > 0 ? "error"
                : duplicateCreations > 0 || !ReadBool(economy, "behavioralCoverageComplete", false) ? "warning" : "healthy";
            List<string> anomalies = new List<string>();
            if (!ReadBool(economy, "behavioralCoverageComplete", false)) anomalies.Add("Native source, reload and balance acceptance remains incomplete; installed hooks do not certify behavior.");
            if (!versioned) anomalies.Add("Legacy economy evidence is readable but cannot establish current source coverage.");
            if (versioned && !fresh) anomalies.Add("Economy evidence is stale or missing its observation clock.");
            if (unsupported > 0) anomalies.Add(unsupported + " recruitment events lack a supported accounting adapter.");
            if (pending > 0) anomalies.Add(pending + " interrupted economy transactions require reconciliation.");
            if (!active) anomalies.Add("The Reign economy Harmony patch group is inactive.");
            if (active && !hooksHealthy) anomalies.Add("One or more required economy hooks or active model owners are missing or replaced.");
            if (!string.IsNullOrWhiteSpace(patchError)) anomalies.Add("Economy patch error: " + patchError);
            if (mismatches > 0) anomalies.Add(mismatches + " patrol troop conservation mismatches were observed.");
            if (!reliefConserved) anomalies.Add("Realm relief food was not conserved between shipped, delivered, and lost totals.");
            if (duplicateCreations > 0) anomalies.Add(duplicateCreations
                + " mobile-party identifiers received household retinues more than once in this process lifetime.");
            Dictionary<string, object> result = new Dictionary<string, object>(economy,
                StringComparer.OrdinalIgnoreCase)
            {
                ["name"] = "War Economy and Manpower",
                ["status"] = status,
                ["currentSchemaEvidence"] = versioned, ["evidenceFresh"] = fresh,
                ["behavioralCoverageComplete"] = versioned && fresh && raidFoodCurrent
                    && ReadString(raidFood, "nativeAcceptance", "pending") == "passed"
                    && ReadBool(economy, "behavioralCoverageComplete", false),
                ["raidFoodEvidenceCurrent"] = raidFoodCurrent && fresh,
                ["latestSuccessfulDay"] = economy.Count == 0 ? -1d : latestDay,
                ["anomalies"] = anomalies
            };
            return result;
        }

        internal static bool ReliefFoodIsConserved(
            double shipped, double delivered, double lost)
        {
            if (shipped < -0.01d || delivered < -0.01d || lost < -0.01d)
                return false;
            // Native campaign counters are persisted as cumulative single-precision
            // floats. Thousands of individually conserved shipments can therefore
            // acquire a few parts-per-million of independent summation drift.
            // Keep a strict absolute floor for small samples and a 10 ppm relative
            // allowance for long campaigns; genuine missing food remains an error.
            double tolerance = Math.Max(0.1d, Math.Abs(shipped) * 0.00001d);
            return Math.Abs(shipped - delivered - lost) <= tolerance;
        }

        private static List<int> MissingRebellionWeekIndexes(
            IEnumerable<Dictionary<string, object>> rolls)
        {
            List<int> observed = (rolls
                    ?? Enumerable.Empty<Dictionary<string, object>>())
                .Select(row => ReadInt(row, "weekIndex", -1))
                .Where(value => value >= 0).Distinct().OrderBy(value => value)
                .ToList();
            if (observed.Count < 2) return new List<int>();
            HashSet<int> present = new HashSet<int>(observed);
            List<int> missing = new List<int>();
            for (int week = observed[0]; week <= observed[observed.Count - 1]; week++)
                if (!present.Contains(week)) missing.Add(week);
            return missing;
        }
    }
}
