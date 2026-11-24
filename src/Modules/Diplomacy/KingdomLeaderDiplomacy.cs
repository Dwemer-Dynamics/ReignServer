using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
        private static readonly int[] RulerBreakthroughThresholds = { 30, 50, 70, 85 };
        internal static readonly bool DailyRulerRelationshipRollsEnabled = false;

        internal static void EnsureKingdomLeaderDiplomacySchema(ReignDbConnection connection)
        {
            DiplomacySchema.CreateRulerRelationshipRolls(connection);
            DiplomacySchema.CreateIndexRulerRelationshipRollsPair(connection);
            DiplomacySchema.CreateRulerRelationshipCrossings(connection);
            DiplomacySchema.CreateRulerDiplomacyOpportunities(connection);
            DiplomacySchema.CreateIndexRulerOpportunityQueue(connection);
            DiplomacySchema.CreateRulerDiplomacyActivity(connection);
            DiplomacySchema.CreateIndexRulerActivityPair(connection);
            DiplomacySchema.CreateRulerDiplomaticIncidents(connection);
            DiplomacySchema.CreateIndexRulerIncidentsPair(connection);
            DiplomacySchema.CreateRulerWarOrigins(connection);
            DiplomacySchema.CreateRulerTreatyObligations(connection);
            DiplomacySchema.CreateRulerAgreementHistory(connection);
        }

        internal static void ProcessDailyRulerRelationships(ReignDbConnection connection,
            string campaignId, string timelineId, double worldDay,
            Dictionary<string, object> heartbeat)
        {
            EnsureKingdomLeaderDiplomacySchema(connection);
            int day = (int)Math.Floor(worldDay + 0.000001d);
            int season = (int)Math.Floor(worldDay / 31.5d);
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            List<Dictionary<string, object>> allRulers = ReadDictionaryList(heartbeat, "politicalLeaders")
                .Where(row => RelationshipsService.ReadPoliticalFlag(row, "is_ruler", "isRuler")
                    && ReadBool(row, "isAlive", ReadInt(row, "is_alive", 1) != 0))
                .GroupBy(row => ReadFirstString(row, "hero_id", "heroStringId", "heroId"),
                    StringComparer.OrdinalIgnoreCase).Select(group => group.First())
                .Where(row => !string.IsNullOrWhiteSpace(ReadFirstString(row,
                    "hero_id", "heroStringId", "heroId"))).ToList();
            List<Dictionary<string, object>> npcRulers = allRulers
                .Where(row => !RelationshipsService.ReadPoliticalFlag(row, "is_player", "isPlayer"))
                .Where(row => !string.IsNullOrWhiteSpace(FirstNonEmpty(
                    ReadString(row, "kingdom_id", ""), ReadString(row, "kingdomId", ""))))
                .ToList();

            lock (RelationshipsService.CampaignRelationshipWriteLock(campaignId))
            {
                DiplomacyStore.Begin(connection);
                try
                {
                    HashSet<string> activeRulerIds = new HashSet<string>(npcRulers.Select(row =>
                        ReadFirstString(row, "hero_id", "heroStringId", "heroId")),
                        StringComparer.OrdinalIgnoreCase);
                    foreach (Dictionary<string, object> obsolete in DiplomacyStore.SelectRulerDiplomacyOpportunities(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }))
                    {
                        string observer = ReadString(obsolete, "observer_id", "");
                        string target = ReadString(obsolete, "target_id", "");
                        if (activeRulerIds.Contains(observer) && activeRulerIds.Contains(target)) continue;
                        CompleteRulerOpportunity(connection, campaignId, timelineId,
                            ReadString(obsolete, "opportunity_id", ""), worldDay,
                            "invalidated", "ruler_replacement_or_kingdom_elimination", "");
                    }
                    DiplomacyStore.UpdateRulerDiplomacyOpportunities(connection, new Dictionary<string, object> { ["campaign"] = campaignId,
                            ["timeline"] = timelineId, ["day"] = worldDay, ["ts"] = ts });

                    // Ruler affinity now moves only through recorded political and
                    // diplomatic incidents. Keep opportunity invalidation/expiry
                    // maintenance active while the independent daily dice pass is
                    // disabled so re-enabling it later cannot revive stale work.
                    if (!DailyRulerRelationshipRollsEnabled)
                    {
                        DiplomacyStore.Commit(connection);
                        return;
                    }

                    foreach (Dictionary<string, object> observerRow in npcRulers)
                    foreach (Dictionary<string, object> targetRow in npcRulers)
                    {
                        string observer = ReadFirstString(observerRow, "hero_id", "heroStringId", "heroId");
                        string target = ReadFirstString(targetRow, "hero_id", "heroStringId", "heroId");
                        if (observer.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                        if (ReadInt(DiplomacyStore.CountRulerRelationshipRolls(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day,
                            ["observer"] = observer, ["target"] = target
                        }).FirstOrDefault(), "count", 0) != 0) continue;

                        string observerKingdom = FirstNonEmpty(ReadString(observerRow, "kingdom_id", ""), ReadString(observerRow, "kingdomId", ""));
                        string targetKingdom = FirstNonEmpty(ReadString(targetRow, "kingdom_id", ""), ReadString(targetRow, "kingdomId", ""));
                        string pairKey = RelationshipsService.AmbientPairKey(observer, target);
                        Dictionary<string, object> beforePair = DiplomacyStore.SelectRelationshipPairChemistry(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                        if (beforePair == null)
                        {
                            RelationshipsService.ApplyAuthoritativeRelationshipDelta(connection, campaignId, observer,
                                target, 1, worldDay, "ruler_diplomacy_contact", timelineId);
                            RelationshipsService.ApplyAtomicDirectionalRelationshipDelta(connection, campaignId, observer,
                                target, -1, worldDay, "ruler_diplomacy_contact_initialization", timelineId);
                            beforePair = DiplomacyStore.SelectRelationshipPairChemistry(connection, new Dictionary<string, object> { ["pair"] = pairKey }).FirstOrDefault();
                        }
                        RelationshipsService.RecordRelationshipPairProvenance(connection, campaignId, timelineId,
                            pairKey, "ruler_diplomacy_contact", true, worldDay,
                            new Dictionary<string, object> { ["observerId"] = observer, ["targetId"] = target });
                        bool observerIsA = ReadString(beforePair, "hero_a_id", "")
                            .Equals(observer, StringComparison.OrdinalIgnoreCase);
                        string affinityColumn = observerIsA ? "affinity_a_to_b" : "affinity_b_to_a";
                        string chanceColumn = observerIsA ? "base_chance_a_to_b" : "base_chance_b_to_a";
                        int beforeAffinity = ReadInt(beforePair, affinityColumn, 0);
                        int beforeEffective = beforeAffinity;
                        int compatibilityChance = Clamp(ReadInt(beforePair, chanceColumn, 50), 0, 100);
                        int compatibilityRoll = StableRulerD100(campaignId, timelineId, day,
                            observer + "|" + target + "|compatibility");
                        int magnitude = 1 + (StableRulerD100(campaignId, timelineId, day,
                            observer + "|" + target + "|magnitude") - 1) % 3;
                        int delta = compatibilityRoll <= compatibilityChance ? magnitude : -magnitude;
                        Dictionary<string, object> afterPair = RelationshipsService.ApplyAtomicDirectionalRelationshipDelta(
                            connection, campaignId, observer, target, delta, worldDay,
                            "ruler_diplomacy_remote", timelineId) ?? beforePair;
                        int afterAffinity = ReadInt(afterPair, affinityColumn, beforeAffinity + delta);
                        int afterEffective = afterAffinity;
                        bool coLocated = RulersCoLocated(observerRow, targetRow);
                        int finalEffective = afterEffective;
                        string correlation = "ruler-roll:" + timelineId + ":" + day.ToString(CultureInfo.InvariantCulture)
                            + ":" + observer + ":" + target;
                        DiplomacyStore.InsertRulerRelationshipRolls(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                                ["day"] = day, ["worldDay"] = worldDay, ["observer"] = observer, ["target"] = target,
                                ["observerKingdom"] = observerKingdom, ["targetKingdom"] = targetKingdom,
                                ["chance"] = compatibilityChance, ["roll"] = compatibilityRoll, ["magnitude"] = magnitude,
                                ["delta"] = delta, ["beforeAffinity"] = beforeAffinity, ["afterAffinity"] = afterAffinity,
                                ["beforeEffective"] = beforeEffective, ["afterEffective"] = afterEffective,
                                ["coLocated"] = coLocated ? 1 : 0, ["correlation"] = correlation, ["ts"] = ts });
                        RecordRulerActivity(connection, campaignId, timelineId, worldDay, observer, target,
                            "remote_roll", delta > 0 ? "positive" : "negative", "completed", 0,
                            beforeEffective, afterEffective, compatibilityRoll, compatibilityChance, "", correlation,
                            "directional MBTI compatibility d100 and remote 1d3");
                        if (coLocated)
                        {
                            int presenceRoll = StableRulerD100(campaignId, timelineId,
                                day, observer + "|" + target + "|presence_compatibility");
                            int presenceMagnitude = 1 + (StableRulerD100(campaignId,
                                timelineId, day, observer + "|" + target
                                    + "|presence_magnitude") - 1) % 10;
                            int presenceDelta = presenceRoll <= compatibilityChance
                                ? presenceMagnitude : -presenceMagnitude;
                            Dictionary<string, object> presencePair =
                                RelationshipsService.ApplyAtomicDirectionalRelationshipDelta(connection,
                                    campaignId, observer, target, presenceDelta,
                                    worldDay, "ruler_diplomacy_co_presence", timelineId)
                                ?? afterPair;
                            int presenceAfterAffinity = ReadInt(presencePair,
                                affinityColumn, afterAffinity + presenceDelta);
                            finalEffective = presenceAfterAffinity;
                            RecordRulerActivity(connection, campaignId, timelineId,
                                worldDay, observer, target, "co_presence_roll",
                                presenceDelta > 0 ? "positive" : "negative",
                                "completed", 0, afterEffective, finalEffective,
                                presenceRoll, compatibilityChance, "", correlation,
                                "ordinary co-presence 1d10 in addition to remote 1d3",
                                new Dictionary<string, object>
                                {
                                    ["magnitude"] = presenceMagnitude,
                                    ["delta"] = presenceDelta
                                });
                        }
                        WorldSimulationService.RecordWorldTestCounter(connection, campaignId, timelineId,
                            day, "kingdom_leaders", correlation,
                            new Dictionary<string, object>
                            {
                                ["remoteRollsCompleted"] = 1,
                                [delta > 0 ? "positiveDirections" : "negativeDirections"] = 1,
                                ["affinityMovement"] = Math.Abs(delta),
                                ["coLocatedDirections"] = coLocated ? 1 : 0
                            });
                        DetectRulerBreakthroughs(connection, campaignId, timelineId, season, worldDay,
                            observer, target, observerKingdom, targetKingdom, beforeEffective, finalEffective,
                            correlation, ts);
                    }
                    DiplomacyStore.Commit(connection);
                }
                catch
                {
                    try { DiplomacyStore.Rollback(connection); } catch { }
                    throw;
                }
            }
        }

        private static void DetectRulerBreakthroughs(ReignDbConnection connection,
            string campaignId, string timelineId, int season, double worldDay,
            string observer, string target, string observerKingdom, string targetKingdom,
            int beforeEffective, int afterEffective, string rollCorrelation, long ts,
            ReignDbTransaction transaction = null)
        {
            foreach (int threshold in RulerBreakthroughThresholds)
            {
                string polarity = beforeEffective < threshold && afterEffective >= threshold
                    ? "positive" : beforeEffective > -threshold && afterEffective <= -threshold
                        ? "negative" : "";
                if (string.IsNullOrEmpty(polarity)) continue;
                int chance = threshold == 30 ? 10 : threshold == 50 ? 20 : threshold == 70 ? 35 : 50;
                int bonusRoll = StableRulerD100(campaignId, timelineId, season,
                    observer + "|" + target + "|" + polarity + "|" + threshold.ToString(CultureInfo.InvariantCulture));
                string correlation = rollCorrelation + ":threshold:" + polarity + ":" + threshold.ToString(CultureInfo.InvariantCulture);
                string opportunityId = bonusRoll <= chance ? "ruler-opportunity:" + campaignId + ":" + timelineId + ":"
                    + season.ToString(CultureInfo.InvariantCulture) + ":" + observer + ":" + target + ":" + polarity + ":" + threshold.ToString(CultureInfo.InvariantCulture) : "";
                bool alreadyRecorded = ReadInt(DiplomacyStore.CountRulerRelationshipCrossings(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["season"] = season,
                    ["observer"] = observer, ["target"] = target, ["polarity"] = polarity,
                    ["threshold"] = threshold
                }, transaction).FirstOrDefault(), "count", 0) > 0;
                if (alreadyRecorded) continue;
                DiplomacyStore.InsertOrIgnoreRulerRelationshipCrossings(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["season"] = season,
                    ["observer"] = observer, ["target"] = target, ["observerKingdom"] = observerKingdom,
                    ["targetKingdom"] = targetKingdom, ["polarity"] = polarity, ["threshold"] = threshold,
                    ["day"] = worldDay, ["before"] = beforeEffective, ["after"] = afterEffective,
                    ["chance"] = chance, ["roll"] = bonusRoll, ["passed"] = bonusRoll <= chance ? 1 : 0,
                    ["opportunity"] = opportunityId, ["correlation"] = correlation, ["ts"] = ts
                }, transaction);
                RecordRulerActivity(connection, campaignId, timelineId, worldDay, observer, target,
                    "threshold_crossing", polarity, bonusRoll <= chance ? "bonus_passed" : "bonus_failed",
                    threshold, beforeEffective, afterEffective, bonusRoll, chance, "", correlation,
                    "genuine effective-attitude crossing", transaction: transaction);
                var counters = new Dictionary<string, object>
                {
                    ["breakthrough:" + polarity + ":" + threshold.ToString(CultureInfo.InvariantCulture)] = 1,
                    ["bonusAttempts"] = 1,
                    [bonusRoll <= chance ? "bonusPassed" : "bonusFailed"] = 1
                };
                int day = (int)Math.Floor(worldDay + 0.000001d);
                if (transaction == null)
                    WorldSimulationService.RecordWorldTestCounter(connection, campaignId, timelineId,
                        day, "kingdom_leaders", correlation, counters);
                else
                    WorldSimulationService.WriteWorldTestCounterSchemaReady(connection, campaignId, timelineId,
                        day, "kingdom_leaders", correlation, counters, transaction);
                if (bonusRoll > chance) continue;
                DiplomacyStore.InsertOrIgnoreRulerDiplomacyOpportunities(connection, new Dictionary<string, object>
                {
                    ["id"] = opportunityId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["observer"] = observer, ["target"] = target, ["observerKingdom"] = observerKingdom,
                    ["targetKingdom"] = targetKingdom, ["polarity"] = polarity, ["threshold"] = threshold,
                    ["season"] = season, ["day"] = worldDay, ["effective"] = afterEffective,
                    ["chance"] = chance, ["roll"] = bonusRoll, ["expiry"] = (season + 1) * 31.5d,
                    ["correlation"] = correlation, ["ts"] = ts
                }, transaction);
                RecordRulerActivity(connection, campaignId, timelineId, worldDay, observer, target,
                    "opportunity", polarity, "queued", threshold, afterEffective, afterEffective,
                    bonusRoll, chance, "", correlation, "relationship-generated diplomacy opportunity",
                    transaction: transaction);
            }
        }

        internal static int StableRulerD100(string campaignId, string timelineId, int dayOrSeason, string subject)
        {
            uint value = (uint)StableDirectorSeed(campaignId + "|" + timelineId, dayOrSeason, subject ?? "");
            return 1 + (int)(value % 100u);
        }

        private static bool RulersCoLocated(Dictionary<string, object> first, Dictionary<string, object> second)
        {
            string firstSettlement = ReadFirstString(first, "currentSettlementId", "settlementId", "governorSettlementId");
            string secondSettlement = ReadFirstString(second, "currentSettlementId", "settlementId", "governorSettlementId");
            if (!string.IsNullOrWhiteSpace(firstSettlement) && firstSettlement.Equals(secondSettlement, StringComparison.OrdinalIgnoreCase)) return true;
            string firstParty = ReadFirstString(first, "currentPartyId", "partyId");
            string secondParty = ReadFirstString(second, "currentPartyId", "partyId");
            return !string.IsNullOrWhiteSpace(firstParty) && firstParty.Equals(secondParty, StringComparison.OrdinalIgnoreCase);
        }

        internal static void RecordRulerActivity(ReignDbConnection connection, string campaignId,
            string timelineId, double worldDay, string observer, string target, string eventType,
            string polarity, string status, int threshold, int before, int after, int roll, int chance,
            string actionId, string correlationId, string reason, Dictionary<string, object> payload = null,
            ReignDbTransaction transaction = null)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string id = "ruler-activity:" + campaignId + ":" + timelineId + ":" + eventType + ":"
                + FirstNonEmpty(correlationId, actionId, worldDay.ToString("R", CultureInfo.InvariantCulture)) + ":" + observer + ":" + target;
            DiplomacyStore.InsertOrIgnoreRulerDiplomacyActivity(connection, new Dictionary<string, object>
            {
                ["id"] = id, ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay,
                ["observer"] = observer ?? "", ["target"] = target ?? "", ["event"] = eventType ?? "",
                ["polarity"] = polarity ?? "", ["status"] = status ?? "", ["threshold"] = threshold,
                ["before"] = before, ["after"] = after, ["roll"] = roll, ["chance"] = chance,
                ["action"] = actionId ?? "", ["correlation"] = correlationId ?? "", ["reason"] = DialogueService.LimitText(reason ?? "", 500),
                ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts
            }, transaction);
        }

        private static void CompleteRulerOpportunity(ReignDbConnection connection, string campaignId,
            string timelineId, string opportunityId, double worldDay, string status, string outcome, string actionId)
        {
            Dictionary<string, object> row = DiplomacyStore.SelectRulerDiplomacyOpportunities2(connection, new Dictionary<string, object> { ["id"] = opportunityId, ["campaign"] = campaignId, ["timeline"] = timelineId }).FirstOrDefault();
            if (row == null) return;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            DiplomacyStore.UpdateRulerDiplomacyOpportunities2(connection, new Dictionary<string, object>
            {
                ["status"] = status, ["day"] = worldDay, ["outcome"] = outcome ?? "",
                ["action"] = actionId ?? "", ["ts"] = ts, ["id"] = opportunityId
            });
            RecordRulerActivity(connection, campaignId, timelineId, worldDay,
                ReadString(row, "observer_id", ""), ReadString(row, "target_id", ""),
                "opportunity", ReadString(row, "polarity", ""), status,
                ReadInt(row, "threshold", 0), ReadInt(row, "effective_attitude", 0),
                ReadInt(row, "effective_attitude", 0), ReadInt(row, "bonus_roll", 0),
                ReadInt(row, "bonus_chance", 0), actionId, ReadString(row, "correlation_id", ""), outcome);
        }

        internal static Dictionary<string, object> BuildWorldTestKingdomLeaders(
            ReignDbConnection connection, string campaignId, string timelineId,
            double latestDay, Dictionary<string, object> nativePayload)
        {
            EnsureKingdomLeaderDiplomacySchema(connection);
            RebellionService.EnsureRebellionSchema(connection);
            int day = (int)Math.Floor(latestDay + 0.000001d);
            List<Dictionary<string, object>> rulers = ReadDictionaryList(nativePayload, "politicalLeaders")
                .Where(row => RelationshipsService.ReadPoliticalFlag(row, "is_ruler", "isRuler"))
                .GroupBy(row => ReadFirstString(row, "hero_id", "heroStringId", "heroId"),
                    StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
            List<Dictionary<string, object>> npcRulers = rulers.Where(row =>
                !RelationshipsService.ReadPoliticalFlag(row, "is_player", "isPlayer")).ToList();
            int expectedEdges = npcRulers.Count * Math.Max(0, npcRulers.Count - 1);
            int expectedRolls = DailyRulerRelationshipRollsEnabled
                ? expectedEdges : 0;
            Dictionary<string, object> rollSummary = DiplomacyStore.CountRulerRelationshipRolls2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> crossingSummary = DiplomacyStore.CountRulerRelationshipCrossings2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> opportunityRows = DiplomacyStore.SelectRulerDiplomacyOpportunities3(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId });
            Dictionary<string, int> opportunityStatuses = opportunityRows.GroupBy(row =>
                ReadString(row, "status", "unknown"), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> breakthroughsByThreshold = DiplomacyStore.SelectRulerRelationshipCrossings(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .ToDictionary(row => "±" + ReadInt(row, "threshold", 0).ToString(CultureInfo.InvariantCulture),
                    row => ReadInt(row, "count", 0), StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> activitySummary = DiplomacyStore.SelectRulerDiplomacyActivity(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> matrix = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> observerRow in rulers)
            foreach (Dictionary<string, object> targetRow in rulers)
            {
                string observer = ReadFirstString(observerRow, "hero_id", "heroStringId", "heroId");
                string target = ReadFirstString(targetRow, "hero_id", "heroStringId", "heroId");
                if (observer.Equals(target, StringComparison.OrdinalIgnoreCase)) continue;
                bool playerContext = RelationshipsService.ReadPoliticalFlag(observerRow, "is_player", "isPlayer")
                    || RelationshipsService.ReadPoliticalFlag(targetRow, "is_player", "isPlayer");
                Dictionary<string, object> attitude = RelationshipsService.ResolveRulerDiplomaticAttitude(connection,
                    campaignId, timelineId, observer, target, "world_test_kingdom_leaders");
                Dictionary<string, object> latestRoll = DiplomacyStore.SelectRulerRelationshipRolls(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["observer"] = observer, ["target"] = target }).FirstOrDefault() ?? new Dictionary<string, object>();
                Dictionary<string, object> queued = opportunityRows.FirstOrDefault(row =>
                    ReadString(row, "observer_id", "").Equals(observer, StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "target_id", "").Equals(target, StringComparison.OrdinalIgnoreCase)
                    && DialogueService.ContainsAny(ReadString(row, "status", ""), "queued", "waiting_cooldown"));
                Dictionary<string, object> lastActivity = DiplomacyStore.SelectRulerDiplomacyActivity2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                        ["observer"] = observer, ["target"] = target }).FirstOrDefault() ?? new Dictionary<string, object>();
                int effective = ReadInt(attitude, "effectiveAttitude", 0);
                matrix.Add(new Dictionary<string, object>
                {
                    ["observerId"] = observer, ["targetId"] = target,
                    ["observerName"] = ReadFirstString(observerRow, "canonical_name", "name", "heroName"),
                    ["targetName"] = ReadFirstString(targetRow, "canonical_name", "name", "heroName"),
                    ["observerKingdomId"] = FirstNonEmpty(ReadString(observerRow, "kingdom_id", ""), ReadString(observerRow, "kingdomId", "")),
                    ["targetKingdomId"] = FirstNonEmpty(ReadString(targetRow, "kingdom_id", ""), ReadString(targetRow, "kingdomId", "")),
                    ["playerContext"] = playerContext,
                    ["remoteRollExpected"] = DailyRulerRelationshipRollsEnabled
                        && !playerContext,
                    ["contextLabel"] = playerContext
                        ? "event-driven — player context"
                        : DailyRulerRelationshipRollsEnabled
                            ? "daily remote 1d3"
                            : "event-driven — daily remote roll disabled",
                    ["attitudeBasis"] = "personal_affinity_only",
                    ["publicStandingApplied"] = false,
                    ["personalAffinity"] = ReadInt(attitude, "personalAffinity", 0),
                    ["publicStanding"] = ReadInt(attitude, "targetPublicStanding", 0),
                    ["effectiveAttitude"] = effective, ["band"] = RulerRelationshipBand(effective),
                    ["lastRoll"] = ReadInt(latestRoll, "compatibility_roll", 0),
                    ["lastMagnitude"] = ReadInt(latestRoll, "magnitude", 0),
                    ["lastDelta"] = ReadInt(latestRoll, "delta", 0),
                    ["trend"] = ReadInt(latestRoll, "delta", 0) > 0 ? "positive" : ReadInt(latestRoll, "delta", 0) < 0 ? "negative" : "flat",
                    ["highestThreshold"] = HighestRulerThreshold(connection, campaignId, timelineId, observer, target),
                    ["queuedOpportunity"] = queued != null,
                    ["opportunityPolarity"] = ReadString(queued, "polarity", ""),
                    ["latestEventType"] = ReadString(lastActivity, "event_type", ""),
                    ["latestOutcome"] = ReadString(lastActivity, "status", "")
                });
            }
            int storedCompleted = ReadInt(rollSummary, "completed", 0);
            int completed = DailyRulerRelationshipRollsEnabled
                ? storedCompleted : 0;
            int missing = Math.Max(0, expectedRolls - completed);
            int provenanceMissing = DiplomacyStore.SelectRulerRelationshipRolls2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = day }).Count;
            List<Dictionary<string, object>> anomalies = new List<Dictionary<string, object>>();
            if (missing > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "missing_directional_rolls", ["count"] = missing, ["severity"] = "warning" });
            if (provenanceMissing > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "missing_ruler_diplomacy_contact", ["count"] = provenanceMissing, ["severity"] = "error" });
            int stale = opportunityRows.Count(row => DialogueService.ContainsAny(ReadString(row, "status", ""), "queued", "waiting_cooldown")
                && ReadDouble(row, "expiry_day", latestDay) < latestDay);
            if (stale > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "stale_opportunities", ["count"] = stale, ["severity"] = "error" });
            HashSet<string> playerRulerIds = new HashSet<string>(rulers
                .Where(row => RelationshipsService.ReadPoliticalFlag(row, "is_player", "isPlayer"))
                .Select(row => ReadFirstString(row, "hero_id", "heroStringId", "heroId")),
                StringComparer.OrdinalIgnoreCase);
            int playerRemoteRolls = DiplomacyStore.SelectRulerRelationshipRolls3(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .Count(row => playerRulerIds.Contains(ReadString(row, "observer_id", ""))
                    || playerRulerIds.Contains(ReadString(row, "target_id", "")));
            if (playerRemoteRolls > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "remote_roll_involving_player", ["count"] = playerRemoteRolls, ["severity"] = "error" });
            int polarityMismatches = 0;
            foreach (Dictionary<string, object> activity in DiplomacyStore.SelectRulerDiplomacyActivity3(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }))
            {
                Dictionary<string, object> activityPayload = TryParseJsonObject(
                    ReadString(activity, "payload_json", "{}")) ?? new Dictionary<string, object>();
                Dictionary<string, object> classifiedCandidate = new Dictionary<string, object>
                {
                    ["command"] = ReadString(activityPayload, "command", ""),
                    ["terms"] = ReadDictionary(activityPayload, "terms") ?? new Dictionary<string, object>()
                };
                if (!ClassifyRulerDiplomacyPolarity(classifiedCandidate).Equals(
                    ReadString(activity, "polarity", ""), StringComparison.OrdinalIgnoreCase))
                    polarityMismatches++;
            }
            if (polarityMismatches > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "bonus_action_polarity_mismatch", ["count"] = polarityMismatches, ["severity"] = "error" });
            int duplicateFeedback = DiplomacyStore.SelectRulerDiplomaticIncidents(connection, new Dictionary<string, object>
                { ["campaign"] = campaignId, ["timeline"] = timelineId }).Count;
            if (duplicateFeedback > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "duplicate_feedback_incident", ["count"] = duplicateFeedback, ["severity"] = "error" });
            int overdueProjection = ReadInt(DiplomacyStore.CountRelationshipNativeTargets(connection, new Dictionary<string, object> { ["day"] = latestDay }).FirstOrDefault(), "count", 0);
            if (overdueProjection > 0) anomalies.Add(new Dictionary<string, object> { ["code"] = "projection_receipt_backlog", ["count"] = overdueProjection, ["severity"] = "warning" });
            int belligerentPacts = DiplomacyStore.SelectRulerAgreementHistory(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).Count;
            if (belligerentPacts > 0) anomalies.Add(new Dictionary<string, object>
                { ["code"] = "active_pact_between_belligerents", ["count"] = belligerentPacts, ["severity"] = "error" });
            int unfulfilledObligations = DiplomacyStore.SelectRulerTreatyObligations(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).Count;
            if (unfulfilledObligations > 0) anomalies.Add(new Dictionary<string, object>
                { ["code"] = "unfulfilled_defensive_obligation", ["count"] = unfulfilledObligations, ["severity"] = "error" });
            int recursiveCascades = DiplomacyStore.SelectRulerWarOrigins(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).Count;
            if (recursiveCascades > 0) anomalies.Add(new Dictionary<string, object>
                { ["code"] = "recursive_defensive_pact_cascade", ["count"] = recursiveCascades, ["severity"] = "error" });
            int missingBackingRolls = DiplomacyStore.SelectRebellionBackingRequests(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId }).Count;
            if (missingBackingRolls > 0) anomalies.Add(new Dictionary<string, object>
                { ["code"] = "rebellion_backing_missing_rolls", ["count"] = missingBackingRolls, ["severity"] = "error" });
            int staleBacking = DiplomacyStore.SelectRebellionBackingRequests2(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = latestDay }).Count;
            if (staleBacking > 0) anomalies.Add(new Dictionary<string, object>
                { ["code"] = "stale_rebellion_backing_receipt", ["count"] = staleBacking, ["severity"] = "warning" });
            string status = anomalies.Any(row => ReadString(row, "severity", "") == "error") ? "error"
                : anomalies.Count > 0 ? "warning" : expectedEdges == 0 ? "insufficient_data" : "healthy";
            Dictionary<string, object> incidentSummary = DiplomacyStore.CountRulerDiplomaticIncidents(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            Dictionary<string, object> backingSummary = DiplomacyStore.CountRebellionBackingRequests(connection, new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId })
                .FirstOrDefault() ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["status"] = status,
                ["attitudeBasis"] = "personal_affinity_only",
                ["publicStandingApplied"] = false,
                ["dailyRulerRelationshipRollsEnabled"] =
                    DailyRulerRelationshipRollsEnabled,
                ["remoteMagnitudeDie"] = DailyRulerRelationshipRollsEnabled
                    ? "1d3" : "disabled",
                ["activeKingdoms"] = npcRulers.Select(row => FirstNonEmpty(
                    ReadString(row, "kingdom_id", ""), ReadString(row, "kingdomId", ""))).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                ["rulers"] = rulers, ["expectedNpcPairs"] = npcRulers.Count * Math.Max(0, npcRulers.Count - 1) / 2,
                ["expectedDirectionalEdges"] = expectedEdges,
                ["remoteRollsExpectedToday"] = expectedRolls,
                ["remoteRollsCompletedToday"] = completed, ["remoteRollsMissingToday"] = missing,
                ["legacyRemoteRollsToday"] = DailyRulerRelationshipRollsEnabled
                    ? 0 : storedCompleted,
                ["legacyRemoteAffinityMovementToday"] =
                    DailyRulerRelationshipRollsEnabled ? 0
                        : ReadInt(rollSummary, "movement", 0),
                ["remoteRollsDuplicatedToday"] = 0, ["coLocatedDirectionsToday"] = ReadInt(rollSummary, "colocated", 0),
                ["positiveRollDirectionsToday"] = DailyRulerRelationshipRollsEnabled
                    ? ReadInt(rollSummary, "positive", 0) : 0,
                ["negativeRollDirectionsToday"] = DailyRulerRelationshipRollsEnabled
                    ? ReadInt(rollSummary, "negative", 0) : 0,
                ["totalAffinityMovementToday"] = DailyRulerRelationshipRollsEnabled
                    ? ReadInt(rollSummary, "movement", 0) : 0,
                ["averageMagnitudeToday"] = completed == 0 ? 0d : ReadDouble(rollSummary, "movement", 0d) / completed,
                ["bonusRollsAttempted"] = ReadInt(crossingSummary, "attempts", 0),
                ["bonusRollsPassed"] = ReadInt(crossingSummary, "passed", 0),
                ["bonusRollsFailed"] = ReadInt(crossingSummary, "failed", 0),
                ["breakthroughsByThreshold"] = breakthroughsByThreshold,
                ["opportunityStatuses"] = opportunityStatuses,
                ["noCredibleAction"] = opportunityRows.Count(row => ReadString(row,
                    "outcome", "").Equals("no_credible_action", StringComparison.OrdinalIgnoreCase)),
                ["positiveBonusActionsResolved"] = opportunityRows.Count(row =>
                    ReadString(row, "polarity", "").Equals("positive", StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "outcome", "").Equals("action_resolved", StringComparison.OrdinalIgnoreCase)),
                ["negativeBonusActionsResolved"] = opportunityRows.Count(row =>
                    ReadString(row, "polarity", "").Equals("negative", StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "outcome", "").Equals("action_resolved", StringComparison.OrdinalIgnoreCase)),
                ["diplomacyIncidentsApplied"] = ReadInt(activitySummary, "feedback", 0),
                ["relationshipIncidentSummary"] = incidentSummary,
                ["rebellionBackingSummary"] = backingSummary,
                ["projectionReceiptsPending"] = ReadInt(DiplomacyStore.CountRelationshipNativeTargets2(connection)
                    .FirstOrDefault(), "count", 0),
                ["latestRulerRollDay"] = DailyRulerRelationshipRollsEnabled
                    ? ReadDouble(rollSummary, "latest_day", -1d) : -1d,
                ["latestLegacyRulerRollDay"] = DailyRulerRelationshipRollsEnabled
                    ? -1d : ReadDouble(rollSummary, "latest_day", -1d),
                ["latestBreakthroughDay"] = ReadDouble(crossingSummary, "latest_day", -1d),
                ["latestDiplomacyDay"] = ReadDouble(activitySummary, "latest_diplomacy", -1d),
                ["latestFeedbackDay"] = ReadDouble(activitySummary, "latest_feedback", -1d),
                ["matrix"] = matrix, ["anomalies"] = anomalies
            };
        }

        private static int HighestRulerThreshold(ReignDbConnection connection, string campaignId,
            string timelineId, string observer, string target)
        {
            Dictionary<string, object> row = DiplomacyStore.SelectRulerRelationshipCrossings2(connection, new Dictionary<string, object>
            {
                ["campaign"] = campaignId, ["timeline"] = timelineId, ["observer"] = observer, ["target"] = target
            }).FirstOrDefault();
            return ReadInt(row, "threshold", 0);
        }

        private static string RulerRelationshipBand(int value)
        {
            return value <= -70 ? "nemesis" : value <= -50 ? "enemy" : value <= -30 ? "rival"
                : value <= -10 ? "irritant" : value <= 9 ? "neutral" : value <= 29 ? "acquaintance"
                : value <= 49 ? "friend" : value <= 69 ? "close_friend" : value <= 84 ? "devoted" : "bonded";
        }

        internal static List<Dictionary<string, object>> QueryKingdomLeaderActivity(
            ReignDbConnection connection, string campaignId, string timelineId,
            Dictionary<string, string> query, int limit)
        {
            EnsureKingdomLeaderDiplomacySchema(connection);
            string observer = query != null && query.TryGetValue("ruler", out string ruler) ? ruler : "";
            string pair = query != null && query.TryGetValue("pair", out string requestedPair) ? requestedPair : "";
            string polarity = query != null && query.TryGetValue("polarity", out string requestedPolarity) ? requestedPolarity : "";
            string status = query != null && query.TryGetValue("status", out string requestedStatus) ? requestedStatus : "";
            string eventType = query != null && query.TryGetValue("eventType", out string requestedEventType) ? requestedEventType : "";
            int threshold = query != null && query.TryGetValue("threshold", out string requestedThreshold)
                && int.TryParse(requestedThreshold, out int parsedThreshold) ? parsedThreshold : 0;
            double fromDay = query != null && query.TryGetValue("fromDay", out string requestedFrom)
                && double.TryParse(requestedFrom, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedFrom)
                    ? parsedFrom : double.MinValue;
            double toDay = query != null && query.TryGetValue("toDay", out string requestedTo)
                && double.TryParse(requestedTo, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedTo)
                    ? parsedTo : double.MaxValue;
            string[] pairParts = pair.Split(new[] { '|', '>' }, StringSplitOptions.RemoveEmptyEntries);
            string pairA = pairParts.Length > 0 ? pairParts[0].Trim() : "";
            string pairB = pairParts.Length > 1 ? pairParts[pairParts.Length - 1].Trim() : "";
            return DiplomacyStore.SelectRulerDiplomacyActivity4(connection, Math.Max(1, Math.Min(1000, limit)).ToString(CultureInfo.InvariantCulture),                new Dictionary<string, object> { ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["ruler"] = observer, ["pairA"] = pairA, ["pairB"] = pairB,
                    ["polarity"] = polarity, ["status"] = status, ["event"] = eventType,
                    ["threshold"] = threshold, ["fromDay"] = fromDay, ["toDay"] = toDay });
        }

        private static Dictionary<string, object> SelectRulerDiplomacyOpportunity(
            string campaignId, string timelineId, double worldDay,
            List<Dictionary<string, object>> kingdoms, Dictionary<string, object> state)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureKingdomLeaderDiplomacySchema(connection);
                foreach (Dictionary<string, object> opportunity in DiplomacyStore.SelectRulerDiplomacyOpportunities4(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId, ["day"] = worldDay
                }))
                {
                    Dictionary<string, object> actor = kingdoms.FirstOrDefault(row =>
                        ReadString(row, "leaderHeroId", "").Equals(ReadString(opportunity, "observer_id", ""), StringComparison.OrdinalIgnoreCase)
                        && ReadString(row, "kingdomId", "").Equals(ReadString(opportunity, "observer_kingdom_id", ""), StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, object> target = kingdoms.FirstOrDefault(row =>
                        ReadString(row, "leaderHeroId", "").Equals(ReadString(opportunity, "target_id", ""), StringComparison.OrdinalIgnoreCase)
                        && ReadString(row, "kingdomId", "").Equals(ReadString(opportunity, "target_kingdom_id", ""), StringComparison.OrdinalIgnoreCase));
                    if (actor == null || target == null)
                    {
                        CompleteRulerOpportunity(connection, campaignId, timelineId,
                            ReadString(opportunity, "opportunity_id", ""), worldDay,
                            "invalidated", "obsolete_ruler_or_kingdom", "");
                        continue;
                    }
                    Dictionary<string, object> rulerState = GetDirectorRulerState(state, actor, worldDay);
                    Dictionary<string, object> pairCooldowns = ReadDictionary(rulerState, "pairCooldowns") ?? new Dictionary<string, object>();
                    bool cooling = worldDay < ReadDouble(rulerState, "cooldownUntilDay", 0d)
                        || worldDay < ReadDouble(pairCooldowns, ReadString(target, "kingdomId", ""), 0d);
                    if (cooling)
                    {
                        DiplomacyStore.UpdateRulerDiplomacyOpportunities3(connection, new Dictionary<string, object>
                        {
                            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["id"] = ReadString(opportunity, "opportunity_id", "")
                        });
                        continue;
                    }
                    bool atWar = ReadStringList(actor, "enemies").Contains(ReadString(target, "kingdomId", ""), StringComparer.OrdinalIgnoreCase);
                    string polarity = ReadString(opportunity, "polarity", "");
                    string mode = atWar ? "peace" : polarity.Equals("positive", StringComparison.OrdinalIgnoreCase)
                        ? (ReadInt(opportunity, "threshold", 0) >= 50 ? "security" : "prosperity") : "expansion";
                    return new Dictionary<string, object>
                    {
                        ["ruler"] = actor, ["mode"] = mode,
                        ["targetKingdomId"] = ReadString(target, "kingdomId", ""),
                        ["relationshipOpportunityId"] = ReadString(opportunity, "opportunity_id", ""),
                        ["relationshipPolarity"] = polarity,
                        ["relationshipThreshold"] = ReadInt(opportunity, "threshold", 0),
                        ["traitGroupScore"] = 50d, ["personalityBaseline"] = 1d,
                        ["opportunity"] = 1d, ["finalChance"] = 1d, ["roll"] = 0d,
                        ["priority"] = 2d
                    };
                }
            }
            return null;
        }

        internal static string ClassifyRulerDiplomacyPolarity(Dictionary<string, object> candidate)
        {
            string command = ActionPipelineService.CanonicalCommand(ReadString(candidate, "command", ""));
            HashSet<string> positive = new HashSet<string>(new[]
            {
                "make_peace","offer_tribute_peace","demand_reparations_peace",
                "demand_settlement_peace","demand_surrender_peace","record_promise","sign_trade_agreement",
                "sign_non_aggression_pact","sign_alliance","sign_defensive_pact","exchange_prisoners",
                "ransom_package","hostage_guarantee","recognize_conquest","return_occupied_settlement",
                "supply_agreement","loan_or_subsidy","pay_to_stay_neutral","pay_to_join_war",
                "guarantee_independence"
            }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> negative = new HashSet<string>(new[]
            {
                "declare_war","break_treaty","war_indemnity","demilitarized_border",
                "protectorate_or_vassalage"
            }, StringComparer.OrdinalIgnoreCase);
            if (positive.Contains(command)) return "positive";
            if (negative.Contains(command)) return "negative";
            if (!command.Equals("diplomatic_package", StringComparison.OrdinalIgnoreCase)) return "ambiguous";
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            bool hasPositive = ReadBool(terms, "includeAlliance", false)
                || ReadBool(terms, "guaranteeIndependence", false)
                || ReadBool(terms, "militaryCommitment", false)
                || ReadBool(terms, "releasePrisoners", false)
                || ReadInt(terms, "loanGold", 0) > 0
                || DialogueService.ContainsAny(ReadString(terms, "treatyKind", ""), "alliance", "trade", "pact", "guarantee", "aid");
            bool hasNegative = ReadInt(terms, "reparationsGold", 0) > 0
                || ReadInt(terms, "indemnityGold", 0) > 0
                || ReadBool(terms, "submission", false)
                || ReadBool(terms, "demilitarize", false)
                || ReadStringList(terms, "settlementIds").Count > 0
                || DialogueService.ContainsAny(ReadString(terms, "treatyKind", ""), "surrender", "vassal", "indemnity", "punitive");
            return hasPositive == hasNegative ? "ambiguous" : hasPositive ? "positive" : "negative";
        }

        private static void FinishRelationshipOpportunity(string campaignId, string timelineId,
            string opportunityId, double worldDay, string status, string outcome, string actionId)
        {
            if (string.IsNullOrWhiteSpace(opportunityId)) return;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureKingdomLeaderDiplomacySchema(connection);
                DiplomacyStore.Begin(connection);
                try
                {
                    CompleteRulerOpportunity(connection, campaignId, timelineId,
                        opportunityId, worldDay, status, outcome, actionId);
                    DiplomacyStore.Commit(connection);
                }
                catch
                {
                    try { DiplomacyStore.Rollback(connection); } catch { }
                    throw;
                }
            }
        }

        private static void ApplyDiplomaticRelationshipFeedback(string campaignId,
            Dictionary<string, object> diplomaticEvent, string actionId)
        {
            string actor = ReadString(diplomaticEvent, "actorHeroId", "");
            string target = ReadString(diplomaticEvent, "targetHeroId", "");
            if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(target)) return;
            string timelineId = ReadString(diplomaticEvent, "timelineId", "main");
            double worldDay = ReadDouble(diplomaticEvent, "worldDay", 0d);
            string command = ReadString(diplomaticEvent, "command", "");
            string actorKingdom = ReadFirstString(diplomaticEvent, "actorKingdomId", "actorKingdomStringId");
            string targetKingdom = ReadFirstString(diplomaticEvent, "targetKingdomId", "targetKingdomStringId");
            int actorTowardTarget = 0;
            int targetTowardActor = 0;
            if (command.Equals("sign_trade_agreement", StringComparison.OrdinalIgnoreCase))
                actorTowardTarget = targetTowardActor = 10;
            else if (command.Equals("sign_non_aggression_pact", StringComparison.OrdinalIgnoreCase))
                actorTowardTarget = targetTowardActor = 15;
            else if (command.Equals("sign_defensive_pact", StringComparison.OrdinalIgnoreCase))
                actorTowardTarget = targetTowardActor = 20;
            else if (command.Equals("sign_alliance", StringComparison.OrdinalIgnoreCase)
                || IsDirectorAllianceProposal(new Dictionary<string, object>
                { ["command"] = command, ["terms"] = ReadDictionary(diplomaticEvent, "terms") ?? new Dictionary<string, object>() }))
                actorTowardTarget = targetTowardActor = 25;
            else if (command.Equals("guarantee_independence", StringComparison.OrdinalIgnoreCase))
                actorTowardTarget = targetTowardActor = 15;
            else if (DialogueService.ContainsAny(command, "make_peace", "offer_tribute_peace", "exchange_prisoners", "ransom_package"))
                actorTowardTarget = targetTowardActor = 5;
            else if (DialogueService.ContainsAny(command, "loan_or_subsidy", "supply_agreement", "return_occupied_settlement"))
                targetTowardActor = 10;
            else if (command.Equals("diplomatic_package", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> terms = ReadDictionary(diplomaticEvent, "terms") ?? new Dictionary<string, object>();
                bool punitive = ReadInt(terms, "reparationsGold", 0) > 0
                    || ReadInt(terms, "indemnityGold", 0) > 0
                    || ReadStringList(terms, "settlementIds").Count > 0
                    || ReadBool(terms, "submission", false)
                    || DialogueService.ContainsAny(ReadString(terms, "treatyKind", ""), "surrender", "punitive", "vassal");
                if (punitive) targetTowardActor = -30;
                else actorTowardTarget = targetTowardActor = 5;
            }
            else if (DialogueService.ContainsAny(command, "demand_surrender", "demand_settlement", "demand_reparations", "war_indemnity", "protectorate", "vassalage"))
                targetTowardActor = -30;
            // War and treaty breach consequences are ingested from authoritative native records.
            if (actorTowardTarget == 0 && targetTowardActor == 0) return;
            string correlation = "diplomacy-feedback:" + actionId;
            ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay,
                "completed_" + command, actionId + ":actor", actionId,
                actor, target, actorKingdom, targetKingdom, actorTowardTarget,
                "", 0, true, correlation);
            ApplyRulerDiplomaticIncident(campaignId, timelineId, worldDay,
                "completed_" + command, actionId + ":target", actionId,
                target, actor, targetKingdom, actorKingdom, targetTowardActor,
                "", 0, true, correlation);
        }

        private static void ApplyDiplomaticRefusalRelationshipFeedback(string campaignId,
            Dictionary<string, object> diplomaticEvent)
        {
            string command = ReadString(diplomaticEvent, "command", "");
            Dictionary<string, object> refusalCandidate = new Dictionary<string, object>
            { ["command"] = command, ["terms"] = ReadDictionary(diplomaticEvent, "terms") ?? new Dictionary<string, object>() };
            int delta = command.Equals("sign_alliance", StringComparison.OrdinalIgnoreCase)
                || IsDirectorAllianceProposal(refusalCandidate) ? -25
                : DialogueService.ContainsAny(command, "sign_trade", "non_aggression", "defensive_pact",
                    "guarantee_independence", "make_peace", "offer_tribute_peace",
                    "exchange_prisoners", "ransom_package", "loan_or_subsidy", "supply_agreement") ? -20 : 0;
            if (delta == 0) return;
            string eventId = ReadString(diplomaticEvent, "eventId", "");
            ApplyRulerDiplomaticIncident(campaignId,
                ReadString(diplomaticEvent, "timelineId", "main"),
                ReadDouble(diplomaticEvent, "worldDay", 0d), "proposal_refused",
                "refusal:" + eventId, eventId,
                ReadString(diplomaticEvent, "actorHeroId", ""),
                ReadString(diplomaticEvent, "targetHeroId", ""),
                ReadFirstString(diplomaticEvent, "actorKingdomId", "actorKingdomStringId"),
                ReadFirstString(diplomaticEvent, "targetKingdomId", "targetKingdomStringId"),
                delta, "", 0, true, "diplomacy-refusal:" + eventId);
        }

        internal static void ApplyRulerDiplomaticIncident(string campaignId, string timelineId,
            double worldDay, string eventKind, string incidentId, string sourceId,
            string observer, string target, string observerKingdom, string targetKingdom,
            int affinityDelta, string publicSubjectId, int publicDelta,
            bool enableBreakthroughs, string correlationId,
            Dictionary<string, object> payload = null)
        {
            if (string.IsNullOrWhiteSpace(incidentId) || string.IsNullOrWhiteSpace(observer)
                || string.IsNullOrWhiteSpace(target) || observer.Equals(target, StringComparison.OrdinalIgnoreCase)
                || (affinityDelta == 0 && publicDelta == 0)) return;
            string stableIncidentId = campaignId + ":" + timelineId + ":" + incidentId;
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                RelationshipsService.EnsureMbtiRelationshipSchema(connection);
                RelationshipsService.EnsureWorldRelationshipSchema(connection);
                EnsureKingdomLeaderDiplomacySchema(connection);
                lock (RelationshipsService.CampaignRelationshipWriteLock(campaignId))
                {
                    // Keep the idempotency check inside the same relationship writer
                    // critical section as the insert so duplicate action reports cannot
                    // both pass the preflight and apply the affinity delta twice.
                    if (ReadInt(DiplomacyStore.CountRulerDiplomaticIncidents2(connection, new Dictionary<string, object> { ["id"] = stableIncidentId })
                        .FirstOrDefault(), "count", 0) > 0) return;
                    DiplomacyStore.Begin(connection);
                    try
                    {
                        Dictionary<string, object> before = RelationshipsService.ResolveRulerDiplomaticAttitude(connection,
                            campaignId, timelineId, observer, target, eventKind);
                        int beforeEffective = ReadInt(before, "effectiveAttitude", 0);
                        if (affinityDelta != 0)
                            RelationshipsService.ApplyAuthoritativeRelationshipDelta(connection, campaignId, observer,
                                target, affinityDelta, worldDay, correlationId, timelineId);
                        int boundedPublic = Clamp(publicDelta, -6, 3);
                        if (boundedPublic != 0 && !string.IsNullOrWhiteSpace(publicSubjectId))
                        {
                            ReputationService.EnsureRumorSchema(connection);
                            ApplyDiplomaticPublicStandingIncident(connection, campaignId, timelineId,
                                publicSubjectId, incidentId, eventKind, boundedPublic, worldDay);
                            ReputationService.RecomputePublicStandingForSubjects(connection, campaignId, timelineId,
                                new[] { publicSubjectId }, worldDay);
                        }
                        Dictionary<string, object> after = RelationshipsService.ResolveRulerDiplomaticAttitude(connection,
                            campaignId, timelineId, observer, target, eventKind);
                        int afterEffective = ReadInt(after, "effectiveAttitude", beforeEffective + affinityDelta);
                        WriteRulerDiplomaticIncidentReceipt(connection, null, campaignId, timelineId, worldDay,
                            eventKind, stableIncidentId, sourceId, observer, target, observerKingdom, targetKingdom,
                            affinityDelta, publicSubjectId, boundedPublic, beforeEffective, afterEffective,
                            enableBreakthroughs, correlationId, payload);
                        DiplomacyStore.Commit(connection);
                    }
                    catch
                    {
                        try { DiplomacyStore.Rollback(connection); } catch { }
                        throw;
                    }
                }
            }
        }

        // The daily pressure producer owns the campaign/timeline gates, relationship writer lock,
        // captured relationship file inputs, schema setup, PostgreSQL transaction and post-commit publication.
        private static bool ApplyRulerDiplomaticIncidentInTransaction(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaignId, string timelineId, double worldDay,
            string eventKind, string incidentId, string sourceId, string observer, string target,
            string observerKingdom, string targetKingdom, int affinityDelta, bool enableBreakthroughs,
            string correlationId, Dictionary<string, object> payload = null)
        {
            if (string.IsNullOrWhiteSpace(incidentId) || string.IsNullOrWhiteSpace(observer)
                || string.IsNullOrWhiteSpace(target) || observer.Equals(target, StringComparison.OrdinalIgnoreCase)
                || affinityDelta == 0) return false;
            if (transaction == null || !ReferenceEquals(transaction.Connection, connection)
                || !string.Equals(ReignPostgreSqlStorage.CampaignIdForConnection(connection), campaignId, StringComparison.Ordinal)
                || !System.Threading.Monitor.IsEntered(RelationshipsService.CampaignRelationshipWriteLock(campaignId))
                || !RelationshipsService.RelationshipStandingContext.DefersPublicationFor(connection)
                || RelationshipsService.RelationshipStandingContext.FilesFor(connection, campaignId, timelineId) == null)
                throw new InvalidOperationException("Ruler pressure effects require the registered campaign transaction and captured inputs.");

            string stableIncidentId = campaignId + ":" + timelineId + ":" + incidentId;
            var prior = DiplomacyStore.SelectRulerDiplomaticIncidentReceipt(connection, stableIncidentId, transaction);
            if (prior != null)
            {
                var priorPayload = TryParseJsonObject(ReadString(prior, "payload_json", ""));
                if (ReadString(prior, "campaign_id", "") != campaignId || ReadString(prior, "timeline_id", "") != timelineId
                    || ReadDouble(prior, "world_day", double.NaN) != worldDay
                    || ReadString(prior, "event_kind", "") != (eventKind ?? "")
                    || ReadString(prior, "source_id", "") != (sourceId ?? "")
                    || ReadString(prior, "observer_id", "") != observer || ReadString(prior, "target_id", "") != target
                    || ReadString(prior, "observer_kingdom_id", "") != (observerKingdom ?? "")
                    || ReadString(prior, "target_kingdom_id", "") != (targetKingdom ?? "")
                    || ReadInt(prior, "affinity_delta", int.MinValue) != affinityDelta
                    || ReadString(prior, "public_subject_id", "") != "" || ReadInt(prior, "public_delta", int.MinValue) != 0
                    || ReadInt(prior, "breakthroughs_enabled", -1) != (enableBreakthroughs ? 1 : 0)
                    || ReadString(prior, "correlation_id", "") != (correlationId ?? "")
                    || priorPayload == null
                    || CanonicalJson(priorPayload) != CanonicalJson(payload ?? new Dictionary<string, object>()))
                    throw new InvalidOperationException("The existing ruler pressure receipt records different terms.");
                return false;
            }

            int before = ReadInt(RelationshipsService.ResolveRulerDiplomaticAttitude(connection, campaignId,
                timelineId, observer, target, eventKind), "effectiveAttitude", 0);
            RelationshipsService.ApplyAuthoritativeRelationshipDeltaInTransaction(connection, transaction,
                campaignId, timelineId, observer, target, affinityDelta, worldDay, correlationId);
            int after = ReadInt(RelationshipsService.ResolveRulerDiplomaticAttitude(connection, campaignId,
                timelineId, observer, target, eventKind), "effectiveAttitude", before + affinityDelta);
            WriteRulerDiplomaticIncidentReceipt(connection, transaction, campaignId, timelineId, worldDay,
                eventKind, stableIncidentId, sourceId, observer, target, observerKingdom, targetKingdom,
                affinityDelta, "", 0, before, after, enableBreakthroughs, correlationId, payload);
            return true;
        }

        private static void WriteRulerDiplomaticIncidentReceipt(ReignDbConnection connection,
            ReignDbTransaction transaction, string campaignId, string timelineId, double worldDay,
            string eventKind, string stableIncidentId, string sourceId, string observer, string target,
            string observerKingdom, string targetKingdom, int affinityDelta, string publicSubjectId,
            int publicDelta, int beforeEffective, int afterEffective, bool enableBreakthroughs,
            string correlationId, Dictionary<string, object> payload)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            DiplomacyStore.InsertRulerDiplomaticIncidents(connection, new Dictionary<string, object>
            {
                ["id"] = stableIncidentId, ["campaign"] = campaignId, ["timeline"] = timelineId,
                ["day"] = worldDay, ["kind"] = eventKind ?? "", ["source"] = sourceId ?? "",
                ["observer"] = observer, ["target"] = target,
                ["observerKingdom"] = observerKingdom ?? "", ["targetKingdom"] = targetKingdom ?? "",
                ["delta"] = affinityDelta, ["publicSubject"] = publicSubjectId ?? "",
                ["publicDelta"] = publicDelta, ["before"] = beforeEffective, ["after"] = afterEffective,
                ["breakthroughs"] = enableBreakthroughs ? 1 : 0, ["correlation"] = correlationId ?? "",
                ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()), ["ts"] = ts
            }, transaction);
            RecordRulerActivity(connection, campaignId, timelineId, worldDay, observer, target,
                "relationship_incident", affinityDelta >= 0 ? "positive" : "negative", "applied", 0,
                beforeEffective, afterEffective, 0, 0, sourceId, correlationId,
                eventKind, new Dictionary<string, object>
                {
                    ["incidentId"] = stableIncidentId, ["affinityDelta"] = affinityDelta,
                    ["publicSubjectId"] = publicSubjectId ?? "", ["publicDelta"] = publicDelta,
                    ["eventKind"] = eventKind ?? "", ["sourceId"] = sourceId ?? ""
                }, transaction);
            if (enableBreakthroughs)
                DetectRulerBreakthroughs(connection, campaignId, timelineId,
                    (int)Math.Floor(worldDay / 31.5d), worldDay, observer, target,
                    observerKingdom ?? "", targetKingdom ?? "", beforeEffective, afterEffective,
                    correlationId, ts, transaction);
        }

        private static void IngestNativeDiplomacyRelationshipState(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            string campaignId = ReadString(payload, "campaignId", "default");
            string timelineId = ReadString(payload, "timelineId", "main");
            double snapshotDay = ReadDouble(payload, "worldDay", 0d);
            List<Dictionary<string, object>> kingdoms = ReadDictionaryList(payload, "kingdoms");
            List<Dictionary<string, object>> agreementHistory = ReadDictionaryList(payload, "agreementHistory");
            MirrorNativeDiplomacyLedgers(campaignId, timelineId, payload);
            foreach (Dictionary<string, object> origin in ReadDictionaryList(payload, "warOrigins"))
            {
                string warId = ReadString(origin, "warId", "");
                if (string.IsNullOrWhiteSpace(warId)) continue;
                string aggressorKingdom = ReadString(origin, "aggressorKingdomId", "");
                string defenderKingdom = ReadString(origin, "defenderKingdomId", "");
                string aggressor = FirstNonEmpty(ReadString(origin, "aggressorRulerHeroId", ""),
                    KingdomRulerId(kingdoms, aggressorKingdom));
                string defender = FirstNonEmpty(ReadString(origin, "defenderRulerHeroId", ""),
                    KingdomRulerId(kingdoms, defenderKingdom));
                if (string.IsNullOrWhiteSpace(aggressor) || string.IsNullOrWhiteSpace(defender)) continue;
                double day = ReadDouble(origin, "declarationDay", snapshotDay);
                string kind = ReadString(origin, "originKind", "direct");
                string correlation = "war-origin:" + warId;
                if (kind.Equals("preexisting_unknown", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "preexisting_war_seed",
                        "war-seed:" + warId + ":aggressor", warId, aggressor, defender,
                        aggressorKingdom, defenderKingdom, -50, "", 0, false, correlation, origin);
                    ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "preexisting_war_seed",
                        "war-seed:" + warId + ":defender", warId, defender, aggressor,
                        defenderKingdom, aggressorKingdom, -50, "", 0, false, correlation, origin);
                    continue;
                }

                int defenderPenalty = -50;
                int aggressorPenalty = -10;
                bool publicBreach = false;
                foreach (Dictionary<string, object> agreement in agreementHistory.Where(row =>
                    ReadString(row, "triggeringWarId", "").Equals(warId, StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "endReason", "").Equals("war_declaration", StringComparison.OrdinalIgnoreCase)))
                {
                    TreatyBreachPenalties(ReadString(agreement, "kind", ""), out int betrayed, out int breaker);
                    defenderPenalty += betrayed;
                    aggressorPenalty += breaker;
                    publicBreach = publicBreach || betrayed != 0;
                }
                defenderPenalty = Math.Max(-100, defenderPenalty);
                aggressorPenalty = Math.Max(-100, aggressorPenalty);
                ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "war_declaration",
                    "war:" + warId + ":defender", warId, defender, aggressor,
                    defenderKingdom, aggressorKingdom, defenderPenalty, aggressor,
                    publicBreach ? -6 : -3, true, correlation, origin);
                ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "war_declaration",
                    "war:" + warId + ":aggressor", warId, aggressor, defender,
                    aggressorKingdom, defenderKingdom, aggressorPenalty, "", 0,
                    true, correlation, origin);
            }

            foreach (Dictionary<string, object> agreement in agreementHistory.Where(row =>
                ReadString(row, "endReason", "").Equals("explicit_breach", StringComparison.OrdinalIgnoreCase)))
            {
                string agreementId = ReadString(agreement, "agreementId", "");
                string breakerKingdom = ReadString(agreement, "breakerKingdomId", "");
                string leftKingdom = ReadString(agreement, "actorKingdomId", "");
                string rightKingdom = ReadString(agreement, "targetKingdomId", "");
                string betrayedKingdom = leftKingdom.Equals(breakerKingdom, StringComparison.OrdinalIgnoreCase)
                    ? rightKingdom : leftKingdom;
                string breakerRuler = KingdomRulerId(kingdoms, breakerKingdom);
                string betrayedRuler = KingdomRulerId(kingdoms, betrayedKingdom);
                TreatyBreachPenalties(ReadString(agreement, "kind", ""), out int betrayedPenalty, out int breakerPenalty);
                double day = ReadDouble(agreement, "endedDay", snapshotDay);
                string correlation = "treaty-breach:" + agreementId;
                ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "treaty_breach",
                    "breach:" + agreementId + ":betrayed", agreementId, betrayedRuler, breakerRuler,
                    betrayedKingdom, breakerKingdom, betrayedPenalty, breakerRuler, -6, true, correlation, agreement);
                ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "treaty_breach",
                    "breach:" + agreementId + ":breaker", agreementId, breakerRuler, betrayedRuler,
                    breakerKingdom, betrayedKingdom, breakerPenalty, "", 0, true, correlation, agreement);
            }

            foreach (Dictionary<string, object> obligation in ReadDictionaryList(payload, "treatyObligations"))
            {
                string status = ReadString(obligation, "status", "");
                if (!status.StartsWith("honored", StringComparison.OrdinalIgnoreCase)) continue;
                string obligationId = ReadString(obligation, "obligationId", "");
                string allyKingdom = ReadString(obligation, "allyKingdomId", "");
                string defendedKingdom = ReadString(obligation, "defendedKingdomId", "");
                string ally = FirstNonEmpty(ReadString(obligation, "allyRulerHeroId", ""), KingdomRulerId(kingdoms, allyKingdom));
                string defended = FirstNonEmpty(ReadString(obligation, "defendedRulerHeroId", ""), KingdomRulerId(kingdoms, defendedKingdom));
                double day = ReadDouble(obligation, "triggeredDay", snapshotDay);
                string correlation = "defensive-obligation:" + obligationId;
                ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "defensive_pact_honored",
                    "obligation:" + obligationId + ":defended", obligationId, defended, ally,
                    defendedKingdom, allyKingdom, 20, ally, 1, true, correlation, obligation);
                ApplyRulerDiplomaticIncident(campaignId, timelineId, day, "defensive_pact_honored",
                    "obligation:" + obligationId + ":ally", obligationId, ally, defended,
                    allyKingdom, defendedKingdom, 5, "", 0, true, correlation, obligation);
            }
        }

        private static void MirrorNativeDiplomacyLedgers(string campaignId, string timelineId,
            Dictionary<string, object> payload)
        {
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureKingdomLeaderDiplomacySchema(connection);
                foreach (Dictionary<string, object> origin in ReadDictionaryList(payload, "warOrigins"))
                {
                    DiplomacyStore.UpsertRulerWarOrigins(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["war"] = ReadString(origin, "warId", ""),
                            ["aggressorKingdom"] = ReadString(origin, "aggressorKingdomId", ""),
                            ["defenderKingdom"] = ReadString(origin, "defenderKingdomId", ""),
                            ["aggressor"] = ReadString(origin, "aggressorRulerHeroId", ""), ["defender"] = ReadString(origin, "defenderRulerHeroId", ""),
                            ["day"] = ReadDouble(origin, "declarationDay", 0d), ["kind"] = ReadString(origin, "originKind", ""),
                            ["cause"] = ReadString(origin, "cause", ""), ["action"] = ReadString(origin, "sourceActionId", ""),
                            ["parent"] = ReadString(origin, "parentWarId", ""), ["active"] = ReadBool(origin, "isActive", true) ? 1 : 0,
                            ["ended"] = ReadDouble(origin, "endedDay", 0d), ["payload"] = Json.Serialize(origin)
                        });
                }
                foreach (Dictionary<string, object> agreement in ReadDictionaryList(payload, "agreementHistory"))
                {
                    DiplomacyStore.UpsertRulerAgreementHistory(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["id"] = ReadString(agreement, "agreementId", ""),
                            ["kind"] = ReadString(agreement, "kind", ""), ["actor"] = ReadString(agreement, "actorKingdomId", ""),
                            ["target"] = ReadString(agreement, "targetKingdomId", ""), ["created"] = ReadDouble(agreement, "createdDay", 0d),
                            ["expire"] = ReadDouble(agreement, "expireDay", 0d), ["active"] = ReadBool(agreement, "isActive", false) ? 1 : 0,
                            ["ended"] = ReadDouble(agreement, "endedDay", 0d), ["reason"] = ReadString(agreement, "endReason", ""),
                            ["breaker"] = ReadString(agreement, "breakerKingdomId", ""), ["war"] = ReadString(agreement, "triggeringWarId", ""),
                            ["payload"] = Json.Serialize(agreement)
                        });
                }
                foreach (Dictionary<string, object> obligation in ReadDictionaryList(payload, "treatyObligations"))
                {
                    DiplomacyStore.UpsertRulerTreatyObligations(connection, new Dictionary<string, object>
                        {
                            ["campaign"] = campaignId, ["timeline"] = timelineId, ["id"] = ReadString(obligation, "obligationId", ""),
                            ["agreement"] = ReadString(obligation, "agreementId", ""), ["trigger"] = ReadString(obligation, "triggeringWarId", ""),
                            ["result"] = ReadString(obligation, "resultingWarId", ""), ["allyKingdom"] = ReadString(obligation, "allyKingdomId", ""),
                            ["ally"] = ReadString(obligation, "allyRulerHeroId", ""), ["defendedKingdom"] = ReadString(obligation, "defendedKingdomId", ""),
                            ["defended"] = ReadString(obligation, "defendedRulerHeroId", ""), ["aggressor"] = ReadString(obligation, "aggressorKingdomId", ""),
                            ["day"] = ReadDouble(obligation, "triggeredDay", 0d), ["status"] = ReadString(obligation, "status", ""),
                            ["reason"] = ReadString(obligation, "reason", ""), ["payload"] = Json.Serialize(obligation)
                        });
                }
            }
        }

        private static string KingdomRulerId(List<Dictionary<string, object>> kingdoms, string kingdomId)
        {
            Dictionary<string, object> row = (kingdoms ?? new List<Dictionary<string, object>>())
                .FirstOrDefault(x => ReadString(x, "kingdomId", "").Equals(kingdomId ?? "", StringComparison.OrdinalIgnoreCase));
            return ReadFirstString(row, "leaderHeroId", "rulerHeroId");
        }

        private static void TreatyBreachPenalties(string kind, out int betrayedPenalty, out int breakerPenalty)
        {
            string value = (kind ?? "").Trim().ToLowerInvariant();
            if (value == "trade_agreement") { betrayedPenalty = -20; breakerPenalty = -5; return; }
            if (value == "non_aggression_pact") { betrayedPenalty = -40; breakerPenalty = -10; return; }
            if (value == "defensive_pact" || value == "alliance" || value == "guarantee_independence")
            { betrayedPenalty = -50; breakerPenalty = -10; return; }
            betrayedPenalty = 0;
            breakerPenalty = 0;
        }

        private static void ApplyDiplomaticPublicStandingIncident(
            ReignDbConnection connection, string campaignId, string timelineId,
            string subjectId, string actionId, string command, int value,
            double worldDay)
        {
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string tag = "diplomacy_incident:" + actionId;
            string description = "Public consequence of "
                + (command ?? "diplomacy").Replace('_', ' ') + ".";
            Dictionary<string, object> snapshot = new Dictionary<string, object>
            {
                ["label"] = "Diplomatic conduct",
                ["description"] = description,
                ["reputationValue"] = value,
                ["sourceActionId"] = actionId,
                ["command"] = command ?? ""
            };
            DiplomacyStore.InsertOrIgnoreCharacterReputations(connection, new Dictionary<string, object>
                {
                    ["campaign"] = campaignId, ["timeline"] = timelineId,
                    ["subject"] = subjectId, ["tag"] = tag,
                    ["action"] = actionId, ["description"] = description,
                    ["value"] = value, ["day"] = worldDay,
                    ["snapshot"] = Json.Serialize(snapshot), ["ts"] = ts
                });
        }

        internal static void RecordRulerDiplomacyEventActivity(string campaignId,
            Dictionary<string, object> diplomaticEvent, string eventType,
            string status)
        {
            string actor = ReadString(diplomaticEvent, "actorHeroId", "");
            string target = ReadString(diplomaticEvent, "targetHeroId", "");
            if (string.IsNullOrWhiteSpace(actor) || string.IsNullOrWhiteSpace(target)) return;
            string timelineId = ReadString(diplomaticEvent, "timelineId", "main");
            string opportunityId = ReadString(diplomaticEvent,
                "relationshipOpportunityId", "");
            string correlation = ReadString(diplomaticEvent, "correlationId", "");
            using (ReignDbConnection connection = ReignDatabase.OpenCampaignConnection(campaignId))
            {
                EnsureKingdomLeaderDiplomacySchema(connection);
                Dictionary<string, object> opportunity = string.IsNullOrWhiteSpace(opportunityId)
                    ? null : DiplomacyStore.SelectRulerDiplomacyOpportunities5(connection, new Dictionary<string, object>
                    { ["id"] = opportunityId }).FirstOrDefault();
                if (opportunity != null)
                    correlation = ReadString(opportunity, "correlation_id", correlation);
                RecordRulerActivity(connection, campaignId, timelineId,
                    ReadDouble(diplomaticEvent, "worldDay", 0d), actor, target,
                    eventType, FirstNonEmpty(ReadString(diplomaticEvent,
                        "relationshipPolarity", ""),
                        ClassifyRulerDiplomacyPolarity(diplomaticEvent)),
                    status, ReadInt(opportunity, "threshold", 0), 0, 0, 0, 0,
                    ReadString(diplomaticEvent, "actionId", ""), correlation,
                    ReadString(diplomaticEvent, "command", "") + ": "
                        + ReadString(diplomaticEvent, "outcome", status),
                    new Dictionary<string, object>
                    {
                        ["eventId"] = ReadString(diplomaticEvent, "eventId", ""),
                        ["opportunityId"] = opportunityId,
                        ["command"] = ReadString(diplomaticEvent, "command", ""),
                        ["terms"] = ReadDictionary(diplomaticEvent, "terms")
                            ?? new Dictionary<string, object>()
                    });
            }
        }
    }
}
