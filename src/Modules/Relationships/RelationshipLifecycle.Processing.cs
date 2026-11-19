using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static Dictionary<string, object> ProcessRelationshipLifecycle(
            ReignDbConnection connection,
            string campaignId,
            AmbientPairContext pair,
            int day,
            Dictionary<string, object> heroA,
            Dictionary<string, object> heroB,
            int affinityAB,
            int affinityBA,
            Dictionary<string, object> preloadedRow = null,
            bool rowPreloaded = false,
            string timelineId = "main")
        {
            Dictionary<string, object> row;
            if (rowPreloaded)
            {
                row = preloadedRow ?? new Dictionary<string, object>();
            }
            else
            {
                EnsureRelationshipLifecycleSchema(connection);
                row = ReignDatabase.QuerySql(connection,
                    "SELECT * FROM relationship_pair_lifecycle WHERE pair_key=$pair LIMIT 1;",
                    new Dictionary<string, object> { ["pair"] = pair.PairKey }).FirstOrDefault()
                    ?? new Dictionary<string, object>();
            }
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            bool related = !MbtiRomanceEligible(heroA, heroB);
            string spouseA = ReadString(heroA, "spouseId", "");
            string spouseB = ReadString(heroB, "spouseId", "");
            bool marriedToEachOther = spouseA.Equals(pair.HeroBId, StringComparison.OrdinalIgnoreCase)
                && spouseB.Equals(pair.HeroAId, StringComparison.OrdinalIgnoreCase);
            bool marriedToOther = (!string.IsNullOrWhiteSpace(spouseA) && !spouseA.Equals(pair.HeroBId, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(spouseB) && !spouseB.Equals(pair.HeroAId, StringComparison.OrdinalIgnoreCase));
            bool sameSex = ReadBool(heroA, "isFemale", false) == ReadBool(heroB, "isFemale", false);
            bool requiresLifecycleState = row.Count > 0 || marriedToEachOther
                || (!related && affinityAB >= RomanceFlirtationThreshold
                    && affinityBA >= RomanceFlirtationThreshold);
            if (!requiresLifecycleState)
            {
                return new Dictionary<string, object>
                {
                    ["loverStarted"] = false, ["loverEnded"] = false,
                    ["affairStarted"] = false, ["affairEnded"] = false,
                    ["marriageQueued"] = false, ["divorceQueued"] = false,
                    ["conceptionQueued"] = false, ["rumorCreated"] = false,
                    ["sharedTag"] = SharedRelationshipTag(campaignId, pair.PairKey,
                        affinityAB, affinityBA, heroA, heroB)
                };
            }
            bool lovers = ReadInt(row, "lover_active", 0) == 1;
            bool affair = ReadInt(row, "affair_active", 0) == 1;
            bool loverStarted = false, loverEnded = false, affairStarted = false, affairEnded = false;
            bool flirtationStarted = false, growingAttractionStarted = false;
            bool seriousAttractionStarted = false, flirtationAttemptedNow = false;
            bool affairAttemptedNow = false;
            bool marriageQueued = false, divorceQueued = false, conceptionQueued = false, rumorCreated = false;
            int exceptionAttempted = ReadInt(row, "lover_exception_attempted", 0);
            int exceptionPassed = ReadInt(row, "lover_exception_passed", 0);
            int exceptionRoll = ReadInt(row, "lover_exception_roll", 0);
            string romanceStage = ReadString(row, "romance_stage", "");
            if (string.IsNullOrWhiteSpace(romanceStage) && lovers)
                romanceStage = "lovers";
            double romanceStageStartedDay = ReadDouble(row,
                "romance_stage_started_day", 0d);
            double romanceLastColocatedDay = ReadDouble(row,
                "romance_last_colocated_day", 0d);
            int romancePositiveBonus = ReadInt(row,
                "romance_positive_bonus", RomancePositiveChanceBonus(row));
            int flirtEpisode = ReadInt(row, "flirt_episode", 0);
            int flirtAttempted = ReadInt(row, "flirt_attempted", 0);
            int flirtPassed = ReadInt(row, "flirt_passed", 0);
            int flirtTraitA = ReadInt(row, "flirt_trait_a", 0);
            int flirtTraitB = ReadInt(row, "flirt_trait_b", 0);
            int flirtRollA = ReadInt(row, "flirt_roll_a", 0);
            int flirtRollB = ReadInt(row, "flirt_roll_b", 0);
            int affairEpisode = ReadInt(row, "affair_episode", 0);
            int affairAttempted = ReadInt(row, "affair_attempted", 0);
            int affairPassed = ReadInt(row, "affair_passed", 0);
            int judgmentTraitA = ReadInt(row, "judgment_trait_a", 0);
            int judgmentTraitB = ReadInt(row, "judgment_trait_b", 0);
            int judgmentRollA = ReadInt(row, "judgment_roll_a", 0);
            int judgmentRollB = ReadInt(row, "judgment_roll_b", 0);
            if (affair && affairAttempted == 0)
            {
                // Preserve valid affairs from lifecycle v3. New affairs always
                // carry both explicit judgment receipts.
                affairEpisode = 1;
                affairAttempted = 1;
                affairPassed = 1;
                exceptionAttempted = 1;
                exceptionPassed = 1;
            }
            double loverStartedDay = ReadDouble(row, "lover_started_day", 0d);
            double affairStartedDay = ReadDouble(row, "affair_started_day", 0d);
            string loverRumorId = ReadString(row, "lover_rumor_id", "");
            string affairRumorId = ReadString(row, "affair_rumor_id", "");
            string estrangedRumorId = ReadString(row, "estranged_rumor_id", "");
            string marriageActionId = ReadString(row, "marriage_action_id", "");
            bool marriageBlocked = ReadInt(row, "marriage_blocked", 0) == 1;
            string marriageBlockReason = ReadString(row,
                "marriage_block_reason", "");
            string divorceActionId = ReadString(row, "divorce_action_id", "");
            string divorceRumorId = ReadString(row, "divorce_rumor_id", "");
            string conceptionActionId = ReadString(row, "conception_action_id", "");
            double lastMarriageEvaluationDay = ReadDouble(row,
                "last_marriage_evaluation_day", -1000d);
            int organicMarriageColocatedDays = ReadInt(row,
                "organic_marriage_colocated_days", 0);
            bool divorced = ReadInt(row, "divorced", 0) == 1;
            double divorceDay = ReadDouble(row, "divorce_day", 0d);

            int mutualAffinity = Math.Min(affinityAB, affinityBA);
            if (!lovers && mutualAffinity < RomanceFlirtationResetThreshold
                && (flirtAttempted == 1 || !string.IsNullOrWhiteSpace(romanceStage)))
            {
                romanceStage = "";
                romanceStageStartedDay = 0d;
                romancePositiveBonus = 0;
                flirtEpisode = Math.Max(1, flirtEpisode) + 1;
                flirtAttempted = 0;
                flirtPassed = 0;
                flirtTraitA = flirtTraitB = flirtRollA = flirtRollB = 0;
                affairEpisode = Math.Max(1, affairEpisode) + 1;
                affairAttempted = affairPassed = 0;
                judgmentTraitA = judgmentTraitB = judgmentRollA = judgmentRollB = 0;
            }
            else if (!lovers
                && romanceStage.Equals("serious_attraction",
                    StringComparison.OrdinalIgnoreCase)
                && mutualAffinity < RomanceSeriousResetThreshold)
            {
                romanceStage = mutualAffinity >= RomanceGrowingThreshold
                    ? "growing_attraction"
                    : mutualAffinity >= RomanceFlirtationResetThreshold
                        ? "flirtation" : "";
                romanceStageStartedDay = day;
                affairEpisode = Math.Max(1, affairEpisode) + 1;
                affairAttempted = affairPassed = 0;
                judgmentTraitA = judgmentTraitB = judgmentRollA = judgmentRollB = 0;
            }
            if (!lovers
                && romanceStage.Equals("growing_attraction",
                    StringComparison.OrdinalIgnoreCase)
                && mutualAffinity < RomanceGrowingResetThreshold)
            {
                romanceStage = mutualAffinity >= RomanceFlirtationResetThreshold
                    ? "flirtation" : "";
                romanceStageStartedDay = day;
            }
            if (!lovers && affairAttempted == 1 && affairPassed == 0
                && mutualAffinity < RomanceGrowingResetThreshold)
            {
                affairEpisode = Math.Max(1, affairEpisode) + 1;
                affairAttempted = affairPassed = 0;
                judgmentTraitA = judgmentTraitB = judgmentRollA = judgmentRollB = 0;
            }

            if (!lovers && !related && string.IsNullOrWhiteSpace(romanceStage)
                && mutualAffinity >= RomanceFlirtationThreshold
                && flirtAttempted == 0)
            {
                flirtEpisode = Math.Max(1, flirtEpisode);
                flirtAttempted = 1;
                flirtationAttemptedNow = true;
                flirtTraitA = RomanceTraitPercentage(campaignId, heroA,
                    pair.HeroAId, "flirtatiousness", false);
                flirtTraitB = RomanceTraitPercentage(campaignId, heroB,
                    pair.HeroBId, "flirtatiousness", false);
                flirtRollA = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + flirtEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|flirt_a", 100);
                flirtRollB = StableDie(campaignId + "|" + pair.PairKey + "|"
                    + flirtEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|flirt_b", 100);
                flirtPassed = flirtRollA <= flirtTraitA
                    && flirtRollB <= flirtTraitB ? 1 : 0;
                RecordRelationshipIncident(connection, pair,
                    flirtPassed == 1 ? "flirtation_started"
                        : "flirtation_declined", day,
                    flirtPassed == 1
                        ? "Both participants independently chose to begin flirting."
                        : "At least one participant declined the flirtation opportunity.",
                    new Dictionary<string, object>
                    {
                        ["episode"] = flirtEpisode,
                        ["traitAToB"] = flirtTraitA,
                        ["traitBToA"] = flirtTraitB,
                        ["rollAToB"] = flirtRollA,
                        ["rollBToA"] = flirtRollB,
                        ["passedAToB"] = flirtRollA <= flirtTraitA,
                        ["passedBToA"] = flirtRollB <= flirtTraitB,
                        ["affinityAToB"] = affinityAB,
                        ["affinityBToA"] = affinityBA
                    });
                if (flirtPassed == 1)
                {
                    romanceStage = "flirtation";
                    romanceStageStartedDay = day;
                    flirtationStarted = true;
                }
            }
            if (!lovers && !related
                && romanceStage.Equals("flirtation",
                    StringComparison.OrdinalIgnoreCase)
                && mutualAffinity >= RomanceGrowingThreshold)
            {
                if (!marriedToOther)
                {
                    lovers = true;
                    loverStarted = true;
                    romanceStage = "lovers";
                    romanceStageStartedDay = day;
                }
                else
                {
                    if (affairAttempted == 0)
                    {
                        affairEpisode = Math.Max(1, affairEpisode);
                        affairAttempted = 1;
                        affairAttemptedNow = true;
                        judgmentTraitA = RomanceTraitPercentage(campaignId,
                            heroA, pair.HeroAId, "judgment", true);
                        judgmentTraitB = RomanceTraitPercentage(campaignId,
                            heroB, pair.HeroBId, "judgment", true);
                        judgmentRollA = StableDie(campaignId + "|"
                            + pair.PairKey + "|"
                            + affairEpisode.ToString(CultureInfo.InvariantCulture)
                            + "|affair_judgment_a", 100);
                        judgmentRollB = StableDie(campaignId + "|"
                            + pair.PairKey + "|"
                            + affairEpisode.ToString(CultureInfo.InvariantCulture)
                            + "|affair_judgment_b", 100);
                        // Judgment is the stop-yourself roll: a participant
                        // proceeds only when temptation rolls above judgment.
                        affairPassed = judgmentRollA > judgmentTraitA
                            && judgmentRollB > judgmentTraitB ? 1 : 0;
                        exceptionAttempted = affairAttempted;
                        exceptionPassed = affairPassed;
                        exceptionRoll = Math.Max(judgmentRollA, judgmentRollB);
                        RecordRelationshipIncident(connection, pair,
                            affairPassed == 1 ? "affair_judgment_passed"
                                : "affair_judgment_refused", day,
                            affairPassed == 1
                                ? "Both participants independently chose to proceed despite an existing marriage."
                                : "At least one participant's judgment stopped the attraction from becoming an affair.",
                            new Dictionary<string, object>
                            {
                                ["episode"] = affairEpisode,
                                ["judgmentA"] = judgmentTraitA,
                                ["judgmentB"] = judgmentTraitB,
                                ["rollA"] = judgmentRollA,
                                ["rollB"] = judgmentRollB,
                                ["proceededA"] = judgmentRollA > judgmentTraitA,
                                ["proceededB"] = judgmentRollB > judgmentTraitB,
                                ["affinityAToB"] = affinityAB,
                                ["affinityBToA"] = affinityBA
                            });
                        if (affairPassed == 1)
                        {
                            lovers = true;
                            loverStarted = true;
                            romanceStage = "lovers";
                            romanceStageStartedDay = day;
                        }
                    }
                }
                if (loverStarted)
                {
                    loverStartedDay = day;
                    RecordRelationshipIncident(connection, pair,
                        "lovers_started", day,
                        marriedToOther
                            ? "The mutual flirtation became an affair at mutual affinity fifty after both individual judgment checks passed."
                            : "The mutual flirtation became a lovers relationship at mutual affinity fifty.",
                        new Dictionary<string, object>
                        {
                            ["sameSex"] = sameSex,
                            ["marriedToOther"] = marriedToOther,
                            ["affinityAToB"] = affinityAB,
                            ["affinityBToA"] = affinityBA
                        });
                }
            }

            if (lovers && marriedToOther && !affair && affairAttempted == 0)
            {
                affairEpisode = Math.Max(1, affairEpisode);
                affairAttempted = 1;
                affairAttemptedNow = true;
                judgmentTraitA = RomanceTraitPercentage(campaignId, heroA,
                    pair.HeroAId, "judgment", true);
                judgmentTraitB = RomanceTraitPercentage(campaignId, heroB,
                    pair.HeroBId, "judgment", true);
                judgmentRollA = StableDie(campaignId + "|" + pair.PairKey
                    + "|" + affairEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|existing_lover_affair_judgment_a", 100);
                judgmentRollB = StableDie(campaignId + "|" + pair.PairKey
                    + "|" + affairEpisode.ToString(CultureInfo.InvariantCulture)
                    + "|existing_lover_affair_judgment_b", 100);
                affairPassed = judgmentRollA > judgmentTraitA
                    && judgmentRollB > judgmentTraitB ? 1 : 0;
                exceptionAttempted = affairAttempted;
                exceptionPassed = affairPassed;
                exceptionRoll = Math.Max(judgmentRollA, judgmentRollB);
                if (affairPassed == 0)
                {
                    lovers = false;
                    loverEnded = true;
                    romanceStage = "serious_attraction";
                    romanceStageStartedDay = day;
                    RecordRelationshipIncident(connection, pair,
                        "lovers_ended_at_marriage_boundary", day,
                        "An existing lovers relationship did not pass both judgment checks after a marriage made it an affair.",
                        new Dictionary<string, object>
                        {
                            ["judgmentA"] = judgmentTraitA,
                            ["judgmentB"] = judgmentTraitB,
                            ["rollA"] = judgmentRollA,
                            ["rollB"] = judgmentRollB
                        });
                }
            }

            if (lovers && (affinityAB < RomanceFlirtationThreshold
                || affinityBA < RomanceFlirtationThreshold))
            {
                lovers = false;
                loverEnded = true;
                romanceStage = mutualAffinity >= RomanceFlirtationResetThreshold
                    ? "flirtation" : "";
                romanceStageStartedDay = day;
                RecordRelationshipIncident(connection, pair, "lovers_ended", day,
                    "The relationship fell below the minimum affinity required to remain lovers.",
                    new Dictionary<string, object> { ["affinityAToB"] = affinityAB, ["affinityBToA"] = affinityBA });
            }
            bool nowAffair = lovers && marriedToOther && affairPassed == 1;
            if (nowAffair && !affair)
            {
                affairStarted = true;
                affairStartedDay = day;
                RecordRelationshipIncident(connection, pair, "affair_started", day,
                    "A lovers relationship became an active affair because at least one participant is married to someone else.",
                    new Dictionary<string, object> { ["spouseAId"] = spouseA, ["spouseBId"] = spouseB });
            }
            else if (!nowAffair && affair)
            {
                affairEnded = true;
                RecordRelationshipIncident(connection, pair, "affair_ended", day,
                    "The active affair ended.", new Dictionary<string, object>());
            }
            affair = nowAffair;
            if (lovers || affair || !string.IsNullOrWhiteSpace(romanceStage))
                romanceLastColocatedDay = day;
            romancePositiveBonus = RomancePositiveChanceBonus(romanceStage,
                lovers);

            bool estrangedAB = marriedToEachOther && affinityAB <= -30;
            bool estrangedBA = marriedToEachOther && affinityBA <= -30;
            bool wasEstranged = ReadInt(row, "estranged_a_to_b", 0) == 1 || ReadInt(row, "estranged_b_to_a", 0) == 1;
            bool isEstranged = estrangedAB || estrangedBA;
            if (isEstranged && !wasEstranged)
            {
                RecordRelationshipIncident(connection, pair, "estranged", day,
                    "At least one spouse's directional affinity fell to negative thirty or below.",
                    new Dictionary<string, object> { ["estrangedAToB"] = estrangedAB, ["estrangedBToA"] = estrangedBA });
            }

            if (marriedToEachOther && (affinityAB <= -50 || affinityBA <= -50) && string.IsNullOrWhiteSpace(divorceActionId))
            {
                divorceActionId = QueueDirectorAction(connection, "divorce", day, pair.HeroAId, pair.HeroBId,
                    new Dictionary<string, object>
                    {
                        ["source"] = "mbti_relationship_lifecycle", ["pairKey"] = pair.PairKey,
						["timelineId"] = timelineId,
                        ["affinityAToB"] = affinityAB, ["affinityBToA"] = affinityBA, ["silent"] = true
                    });
                divorceQueued = true;
            }

            bool bothUnmarried = string.IsNullOrWhiteSpace(spouseA) && string.IsNullOrWhiteSpace(spouseB);
            if (affair && mutualAffinity >= RomanceSeriousThreshold)
                TryBeginAffairMarriageCommitment(connection, campaignId,
                    timelineId, pair, day, heroA, heroB, spouseA, spouseB,
                    mutualAffinity);
            bool organicMarriageEligible = bothUnmarried && !related && lovers
                && affinityAB >= RomanceSeriousThreshold
                && affinityBA >= RomanceSeriousThreshold
                && SnapshotMarriageEligible(heroA, heroB)
                && !marriageBlocked
                && string.IsNullOrWhiteSpace(marriageActionId);
            if (organicMarriageEligible)
            {
                organicMarriageColocatedDays++;
                if (organicMarriageColocatedDays
                    >= OrganicMarriageEvaluationCadenceDays)
                {
                    organicMarriageColocatedDays = 0;
                    lastMarriageEvaluationDay = day;
                    int roll = StableDie(campaignId + "|" + pair.PairKey
                        + "|" + day.ToString(CultureInfo.InvariantCulture)
                        + "|mutual_marriage", 100);
                    Dictionary<string, object> marriageCounters =
                        new Dictionary<string, object>
                    {
                        ["organicEvaluations"] = 1,
                        ["organicAccepted"] = roll
                            <= OrganicMarriageChancePercent ? 1 : 0,
                        ["organicRefused"] = roll
                            <= OrganicMarriageChancePercent ? 0 : 1,
                        ["chancePercent"] = OrganicMarriageChancePercent,
                        ["roll"] = roll
                    };
                    if (roll <= OrganicMarriageChancePercent)
                    {
                        marriageActionId = QueueDirectorAction(connection,
                            "marriage", day, pair.HeroAId, pair.HeroBId,
                            new Dictionary<string, object>
                            {
                                ["source"] = "mbti_relationship_lifecycle",
                                ["pairKey"] = pair.PairKey,
                                ["timelineId"] = timelineId,
                                ["route"] = "mutual_affinity",
                                ["roll"] = roll,
                                ["chancePercent"] =
                                    OrganicMarriageChancePercent,
                                ["affinityAToB"] = affinityAB,
                                ["affinityBToA"] = affinityBA,
                                ["silent"] = true
                            });
                        marriageQueued = !string.IsNullOrWhiteSpace(
                            marriageActionId);
                        marriageCounters["organicQueued"] = marriageQueued
                            ? 1 : 0;
                        marriageCounters["reservationBlocked"] = marriageQueued
                            ? 0 : 1;
                    }
                    WorldSimulationService.RecordWorldTestCounter(connection, campaignId, timelineId,
                        day, "marriages", "organic_" + pair.PairKey + "_"
                            + day.ToString(CultureInfo.InvariantCulture),
                        marriageCounters);
                }
            }

            if (lovers && !sameSex && string.IsNullOrWhiteSpace(conceptionActionId))
            {
                Dictionary<string, object> mother = ReadBool(heroA, "isFemale", false) ? heroA : heroB;
                Dictionary<string, object> father = ReferenceEquals(mother, heroA) ? heroB : heroA;
                double motherAge = ReadDouble(mother, "age", 0d);
                if (!ReadBool(mother, "isPregnant", false) && motherAge >= 18d && motherAge <= 45d)
                {
                    int roll = StableDie(campaignId + "|" + pair.PairKey + "|" + day.ToString(CultureInfo.InvariantCulture) + "|lover_conception", 100);
                    if (roll <= LoverConceptionChancePercent)
                    {
                        string motherId = ReadFirstString(mother, "heroStringId", "heroId", "id");
                        string fatherId = ReadFirstString(father, "heroStringId", "heroId", "id");
                        string legalFatherId = FirstNonEmpty(ReadString(mother, "spouseId", ""), fatherId);
                        string conceptionId = CreateLifecycleConceptionId(campaignId, timelineId, pair.PairKey, day);
                        bool unmarriedCommitment = bothUnmarried;
                        bool illegitimate = affair
                            || (!marriedToEachOther && !unmarriedCommitment)
                            || !legalFatherId.Equals(fatherId,
                                StringComparison.OrdinalIgnoreCase);
                        ReignDatabase.ExecuteSql(connection, @"INSERT INTO conceptions(
conception_id,attempt_id,mother_id,biological_father_id,legal_father_id,conception_day,due_day,status,secrecy,
payload_json,created_ts,updated_ts)
VALUES($id,$attempt,$mother,$father,$legal,$day,$due,'pending_game',$secrecy,$payload,$ts,$ts);",
                            new Dictionary<string, object>
                            {
                                ["id"] = conceptionId, ["attempt"] = "relationship_lifecycle_" + pair.PairKey + "_" + day,
                                ["mother"] = motherId, ["father"] = fatherId, ["legal"] = legalFatherId,
                                ["day"] = day, ["due"] = day + 36d, ["secrecy"] = illegitimate ? 0.75d : 0d,
                                ["payload"] = Json.Serialize(new Dictionary<string, object>
                                {
                                    ["source"] = "mbti_relationship_lifecycle", ["pairKey"] = pair.PairKey,
                                    ["roll"] = roll,
                                    ["chancePercent"] = LoverConceptionChancePercent,
                                    ["isIllegitimate"] = illegitimate,
                                    ["unmarriedCommitmentPending"] = unmarriedCommitment
                                }), ["ts"] = ts
                            });
                        conceptionActionId = QueueDirectorAction(connection, "start_conception", day, motherId, fatherId,
                            new Dictionary<string, object>
                            {
                                ["source"] = "mbti_relationship_lifecycle", ["pairKey"] = pair.PairKey,
                                ["timelineId"] = timelineId,
                                ["conceptionId"] = conceptionId, ["motherId"] = motherId,
                                ["biologicalFatherId"] = fatherId, ["legalFatherId"] = legalFatherId,
                                ["conceptionDay"] = day, ["dueDay"] = day + 36d,
                                ["secrecy"] = illegitimate ? 0.75d : 0d,
                                ["isIllegitimate"] = illegitimate,
                                ["unmarriedCommitmentPending"] = unmarriedCommitment
                            });
                        if (unmarriedCommitment)
                            RegisterPregnancyCommitment(connection, campaignId,
                                pair, conceptionId, day, heroA, heroB,
                                marriageActionId);
                        conceptionQueued = true;
                        if (affair)
                        {
                            Dictionary<string, object> promotedAffair = ReputationService.RegisterSocialOccurrence(connection, campaignId,
                                ReputationService.BuildRelationshipSocialOccurrence(pair, day, "affair", heroA, heroB, spouseA, spouseB, true, true,
                                    "The affair resulted in a conception and could no longer remain socially containable.",
                                    timelineId), worldTestSchemaReady: rowPreloaded,
                                useExistingTransaction: rowPreloaded);
                            string promotedOccurrenceId = ReadString(promotedAffair, "occurrenceId", "");
                            if (!string.IsNullOrWhiteSpace(promotedOccurrenceId)) affairRumorId = promotedOccurrenceId;
                        }
                        RecordRelationshipIncident(connection, pair, illegitimate ? "bastard_conception" : "lover_conception",
                            day, illegitimate ? "The lovers conceived a child outside the mother's marriage."
                                : "The lovers conceived a child.",
                            new Dictionary<string, object> { ["motherId"] = motherId, ["fatherId"] = fatherId, ["conceptionId"] = conceptionId });
                    }
                }
            }

            if (affair)
            {
                Dictionary<string, object> occurrence = ReputationService.RegisterSocialOccurrence(connection, campaignId,
                    ReputationService.BuildRelationshipSocialOccurrence(pair, day, "affair", heroA, heroB, spouseA, spouseB, false, false,
                        "Secondhand reports describe an affair between the two participants.", timelineId),
                    worldTestSchemaReady: rowPreloaded,
                    useExistingTransaction: rowPreloaded);
                if (ReadBool(occurrence, "exposed", false))
                    affairRumorId = FirstNonEmpty(ReadString(occurrence, "occurrenceId", ""), affairRumorId);
                rumorCreated = ReadBool(occurrence, "exposed", false) && !ReadBool(occurrence, "duplicate", false);
            }
            if (isEstranged)
            {
                Dictionary<string, object> occurrence = ReputationService.RegisterSocialOccurrence(connection, campaignId,
                    ReputationService.BuildRelationshipSocialOccurrence(pair, day, "marital_strife", heroA, heroB, spouseA, spouseB, false, false,
                        "Secondhand reports describe serious and continuing strife in the marriage.", timelineId),
                    worldTestSchemaReady: rowPreloaded,
                    useExistingTransaction: rowPreloaded);
                if (ReadBool(occurrence, "exposed", false))
                    estrangedRumorId = FirstNonEmpty(ReadString(occurrence, "occurrenceId", ""), estrangedRumorId);
                rumorCreated = rumorCreated || (ReadBool(occurrence, "exposed", false) && !ReadBool(occurrence, "duplicate", false));
            }

            double persistedConceptionDay = conceptionQueued
                ? day : ReadDouble(row, "last_conception_day", -1000d);
            bool lifecycleStateChanged = row.Count == 0
                || ReadInt(row, "lover_active", 0) != (lovers ? 1 : 0)
                || Math.Abs(ReadDouble(row, "lover_started_day", 0d)
                    - loverStartedDay) > 0.000001d
                || ReadInt(row, "lover_exception_attempted", 0) != exceptionAttempted
                || ReadInt(row, "lover_exception_passed", 0) != exceptionPassed
                || ReadInt(row, "lover_exception_roll", 0) != exceptionRoll
                || !ReadString(row, "lover_rumor_id", "").Equals(
                    loverRumorId, StringComparison.Ordinal)
                || !ReadString(row, "romance_stage", "").Equals(
                    romanceStage, StringComparison.OrdinalIgnoreCase)
                || Math.Abs(ReadDouble(row, "romance_stage_started_day", 0d)
                    - romanceStageStartedDay) > 0.000001d
                || Math.Abs(ReadDouble(row, "romance_last_colocated_day", 0d)
                    - romanceLastColocatedDay) > 0.000001d
                || ReadInt(row, "romance_positive_bonus", 0)
                    != romancePositiveBonus
                || ReadInt(row, "flirt_episode", 0) != flirtEpisode
                || ReadInt(row, "flirt_attempted", 0) != flirtAttempted
                || ReadInt(row, "flirt_passed", 0) != flirtPassed
                || ReadInt(row, "flirt_trait_a", 0) != flirtTraitA
                || ReadInt(row, "flirt_trait_b", 0) != flirtTraitB
                || ReadInt(row, "flirt_roll_a", 0) != flirtRollA
                || ReadInt(row, "flirt_roll_b", 0) != flirtRollB
                || ReadInt(row, "affair_episode", 0) != affairEpisode
                || ReadInt(row, "affair_attempted", 0) != affairAttempted
                || ReadInt(row, "affair_passed", 0) != affairPassed
                || ReadInt(row, "judgment_trait_a", 0) != judgmentTraitA
                || ReadInt(row, "judgment_trait_b", 0) != judgmentTraitB
                || ReadInt(row, "judgment_roll_a", 0) != judgmentRollA
                || ReadInt(row, "judgment_roll_b", 0) != judgmentRollB
                || ReadInt(row, "affair_active", 0) != (affair ? 1 : 0)
                || Math.Abs(ReadDouble(row, "affair_started_day", 0d)
                    - affairStartedDay) > 0.000001d
                || !ReadString(row, "affair_rumor_id", "").Equals(
                    affairRumorId, StringComparison.Ordinal)
                || ReadInt(row, "estranged_a_to_b", 0) != (estrangedAB ? 1 : 0)
                || ReadInt(row, "estranged_b_to_a", 0) != (estrangedBA ? 1 : 0)
                || !ReadString(row, "estranged_rumor_id", "").Equals(
                    estrangedRumorId, StringComparison.Ordinal)
                || ReadInt(row, "married", 0) != (marriedToEachOther ? 1 : 0)
                || !ReadString(row, "marriage_action_id", "").Equals(
                    marriageActionId, StringComparison.Ordinal)
                || ReadInt(row, "marriage_blocked", 0) != (marriageBlocked ? 1 : 0)
                || !ReadString(row, "marriage_block_reason", "").Equals(
                    marriageBlockReason, StringComparison.Ordinal)
                || ReadInt(row, "divorced", 0) != (divorced ? 1 : 0)
                || Math.Abs(ReadDouble(row, "divorce_day", 0d)
                    - divorceDay) > 0.000001d
                || !ReadString(row, "divorce_action_id", "").Equals(
                    divorceActionId, StringComparison.Ordinal)
                || !ReadString(row, "divorce_rumor_id", "").Equals(
                    divorceRumorId, StringComparison.Ordinal)
                || !ReadString(row, "conception_action_id", "").Equals(
                    conceptionActionId, StringComparison.Ordinal)
                || Math.Abs(ReadDouble(row, "last_conception_day", -1000d)
                    - persistedConceptionDay) > 0.000001d
                || Math.Abs(ReadDouble(row,
                    "last_marriage_evaluation_day", -1000d)
                    - lastMarriageEvaluationDay) > 0.000001d
                || ReadInt(row, "organic_marriage_colocated_days", 0)
                    != organicMarriageColocatedDays
                || ReadInt(row, "version", 0) != RelationshipLifecycleVersion;
            if (lifecycleStateChanged)
            {
                ExecuteRelationshipLifecycleWrite(connection, @"INSERT INTO relationship_pair_lifecycle(
pair_key,hero_a_id,hero_b_id,lover_active,lover_started_day,lover_exception_attempted,lover_exception_passed,
lover_exception_roll,lover_rumor_id,romance_stage,romance_stage_started_day,romance_last_colocated_day,
romance_positive_bonus,flirt_episode,flirt_attempted,flirt_passed,flirt_trait_a,flirt_trait_b,flirt_roll_a,flirt_roll_b,
affair_episode,affair_attempted,affair_passed,judgment_trait_a,judgment_trait_b,judgment_roll_a,judgment_roll_b,
affair_active,affair_started_day,affair_rumor_id,
estranged_a_to_b,estranged_b_to_a,estranged_rumor_id,married,marriage_action_id,
marriage_blocked,marriage_block_reason,divorced,divorce_day,
divorce_action_id,divorce_rumor_id,conception_action_id,last_conception_day,last_marriage_evaluation_day,organic_marriage_colocated_days,last_processed_day,version,updated_ts)
VALUES($pair,$a,$b,$lovers,$loverDay,$attempted,$passed,$exceptionRoll,$loverRumor,$romanceStage,$romanceStageDay,$romanceColocatedDay,
$romanceBonus,$flirtEpisode,$flirtAttempted,$flirtPassed,$flirtTraitA,$flirtTraitB,$flirtRollA,$flirtRollB,
$affairEpisode,$affairAttempted,$affairPassed,$judgmentTraitA,$judgmentTraitB,$judgmentRollA,$judgmentRollB,
$affair,$affairDay,$affairRumor,
$estrangedAB,$estrangedBA,$estrangedRumor,$married,$marriageAction,$marriageBlocked,$marriageBlockReason,
$divorced,$divorceDay,$divorceAction,
$divorceRumor,$conceptionAction,$conceptionDay,$marriageEvaluationDay,$marriageColocatedDays,$day,$version,$ts)
ON CONFLICT(pair_key) DO UPDATE SET lover_active=$lovers,lover_started_day=$loverDay,
 lover_exception_attempted=$attempted,lover_exception_passed=$passed,lover_exception_roll=$exceptionRoll,
 lover_rumor_id=$loverRumor,romance_stage=$romanceStage,romance_stage_started_day=$romanceStageDay,
 romance_last_colocated_day=$romanceColocatedDay,romance_positive_bonus=$romanceBonus,
 flirt_episode=$flirtEpisode,flirt_attempted=$flirtAttempted,flirt_passed=$flirtPassed,
 flirt_trait_a=$flirtTraitA,flirt_trait_b=$flirtTraitB,flirt_roll_a=$flirtRollA,flirt_roll_b=$flirtRollB,
 affair_episode=$affairEpisode,affair_attempted=$affairAttempted,affair_passed=$affairPassed,
 judgment_trait_a=$judgmentTraitA,judgment_trait_b=$judgmentTraitB,judgment_roll_a=$judgmentRollA,
 judgment_roll_b=$judgmentRollB,affair_active=$affair,affair_started_day=$affairDay,affair_rumor_id=$affairRumor,
estranged_a_to_b=$estrangedAB,estranged_b_to_a=$estrangedBA,estranged_rumor_id=$estrangedRumor,
married=$married,marriage_action_id=$marriageAction,marriage_blocked=$marriageBlocked,
marriage_block_reason=$marriageBlockReason,divorced=$divorced,divorce_day=$divorceDay,
divorce_action_id=$divorceAction,divorce_rumor_id=$divorceRumor,conception_action_id=$conceptionAction,
last_conception_day=$conceptionDay,last_marriage_evaluation_day=$marriageEvaluationDay,
organic_marriage_colocated_days=$marriageColocatedDays,last_processed_day=$day,version=$version,updated_ts=$ts;",
                new Dictionary<string, object>
                {
                    ["pair"] = pair.PairKey, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                    ["lovers"] = lovers ? 1 : 0, ["loverDay"] = loverStartedDay,
                    ["attempted"] = exceptionAttempted, ["passed"] = exceptionPassed, ["exceptionRoll"] = exceptionRoll,
                    ["loverRumor"] = loverRumorId,
                    ["romanceStage"] = romanceStage,
                    ["romanceStageDay"] = romanceStageStartedDay,
                    ["romanceColocatedDay"] = romanceLastColocatedDay,
                    ["romanceBonus"] = romancePositiveBonus,
                    ["flirtEpisode"] = flirtEpisode,
                    ["flirtAttempted"] = flirtAttempted,
                    ["flirtPassed"] = flirtPassed,
                    ["flirtTraitA"] = flirtTraitA,
                    ["flirtTraitB"] = flirtTraitB,
                    ["flirtRollA"] = flirtRollA,
                    ["flirtRollB"] = flirtRollB,
                    ["affairEpisode"] = affairEpisode,
                    ["affairAttempted"] = affairAttempted,
                    ["affairPassed"] = affairPassed,
                    ["judgmentTraitA"] = judgmentTraitA,
                    ["judgmentTraitB"] = judgmentTraitB,
                    ["judgmentRollA"] = judgmentRollA,
                    ["judgmentRollB"] = judgmentRollB,
                    ["affair"] = affair ? 1 : 0, ["affairDay"] = affairStartedDay,
                    ["affairRumor"] = affairRumorId, ["estrangedAB"] = estrangedAB ? 1 : 0,
                    ["estrangedBA"] = estrangedBA ? 1 : 0, ["estrangedRumor"] = estrangedRumorId,
                    ["married"] = marriedToEachOther ? 1 : 0, ["marriageAction"] = marriageActionId,
                    ["marriageBlocked"] = marriageBlocked ? 1 : 0,
                    ["marriageBlockReason"] = marriageBlockReason,
                    ["divorced"] = divorced ? 1 : 0, ["divorceDay"] = divorceDay, ["divorceAction"] = divorceActionId,
                    ["divorceRumor"] = divorceRumorId, ["conceptionAction"] = conceptionActionId,
                    ["conceptionDay"] = persistedConceptionDay,
                    ["marriageEvaluationDay"] = lastMarriageEvaluationDay,
                    ["marriageColocatedDays"] =
                        organicMarriageColocatedDays,
                    ["day"] = day, ["version"] = RelationshipLifecycleVersion, ["ts"] = ts
                });
            }

            // A four-day relationship batch evaluates each actual co-presence
            // day in order. Keep the preloaded row synchronized with the upsert
            // so the next day observes lifecycle state created earlier in the
            // same transaction without another query.
            row["pair_key"] = pair.PairKey;
            row["hero_a_id"] = pair.HeroAId;
            row["hero_b_id"] = pair.HeroBId;
            row["lover_active"] = lovers ? 1 : 0;
            row["lover_started_day"] = loverStartedDay;
            row["lover_exception_attempted"] = exceptionAttempted;
            row["lover_exception_passed"] = exceptionPassed;
            row["lover_exception_roll"] = exceptionRoll;
            row["lover_rumor_id"] = loverRumorId;
            row["romance_stage"] = romanceStage;
            row["romance_stage_started_day"] = romanceStageStartedDay;
            row["romance_last_colocated_day"] = romanceLastColocatedDay;
            row["romance_positive_bonus"] = romancePositiveBonus;
            row["flirt_episode"] = flirtEpisode;
            row["flirt_attempted"] = flirtAttempted;
            row["flirt_passed"] = flirtPassed;
            row["flirt_trait_a"] = flirtTraitA;
            row["flirt_trait_b"] = flirtTraitB;
            row["flirt_roll_a"] = flirtRollA;
            row["flirt_roll_b"] = flirtRollB;
            row["affair_episode"] = affairEpisode;
            row["affair_attempted"] = affairAttempted;
            row["affair_passed"] = affairPassed;
            row["judgment_trait_a"] = judgmentTraitA;
            row["judgment_trait_b"] = judgmentTraitB;
            row["judgment_roll_a"] = judgmentRollA;
            row["judgment_roll_b"] = judgmentRollB;
            row["affair_active"] = affair ? 1 : 0;
            row["affair_started_day"] = affairStartedDay;
            row["affair_rumor_id"] = affairRumorId;
            row["estranged_a_to_b"] = estrangedAB ? 1 : 0;
            row["estranged_b_to_a"] = estrangedBA ? 1 : 0;
            row["estranged_rumor_id"] = estrangedRumorId;
            row["married"] = marriedToEachOther ? 1 : 0;
            row["marriage_action_id"] = marriageActionId;
            row["marriage_blocked"] = marriageBlocked ? 1 : 0;
            row["marriage_block_reason"] = marriageBlockReason;
            row["divorced"] = divorced ? 1 : 0;
            row["divorce_day"] = divorceDay;
            row["divorce_action_id"] = divorceActionId;
            row["divorce_rumor_id"] = divorceRumorId;
            row["conception_action_id"] = conceptionActionId;
            row["last_conception_day"] = persistedConceptionDay;
            row["last_marriage_evaluation_day"] =
                lastMarriageEvaluationDay;
            row["organic_marriage_colocated_days"] =
                organicMarriageColocatedDays;
            if (lifecycleStateChanged) row["last_processed_day"] = day;
            row["version"] = RelationshipLifecycleVersion;
            if (lifecycleStateChanged) row["updated_ts"] = ts;

            return new Dictionary<string, object>
            {
                ["loverStarted"] = loverStarted, ["loverEnded"] = loverEnded,
                ["affairStarted"] = affairStarted, ["affairEnded"] = affairEnded,
                ["marriageQueued"] = marriageQueued, ["divorceQueued"] = divorceQueued,
                ["conceptionQueued"] = conceptionQueued, ["rumorCreated"] = rumorCreated,
                ["flirtationAttempted"] = flirtationAttemptedNow,
                ["flirtationStarted"] = flirtationStarted,
                ["growingAttractionStarted"] = growingAttractionStarted,
                ["seriousAttractionStarted"] = seriousAttractionStarted,
                ["affairJudgmentAttempted"] = affairAttemptedNow,
                ["romanceStage"] = romanceStage,
                ["romancePositiveChanceBonus"] = romancePositiveBonus,
                ["sharedTag"] = divorced ? "divorced" : marriedToEachOther ? "married"
                    : affair ? "lovers_affair" : lovers ? "lovers"
                    : SharedRelationshipTag(campaignId, pair.PairKey, affinityAB, affinityBA, heroA, heroB)
            };
        }

        private static Dictionary<string, object>
            ApplyPostgreSqlFiveDayRelationshipDecay(
                ReignDbConnection connection,
                string campaignId,
                string timelineId,
                int day)
        {
            int period = Math.Max(0, day / 5);
            int pairsDecayed = 0;
            int directionsDecayed = 0;
            int nativeQueued = 0;
            int loversEnded = 0;
            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            HashSet<string> courtPopularitySubjects =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Seed missing checkpoints in one write. They are existing state,
            // not decay work for the current period.
            ReignDatabase.ExecuteSql(connection, @"
UPDATE relationship_pair_chemistry
SET last_decay_period=$period,updated_ts=$ts
WHERE last_decay_period<0
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);",
                new Dictionary<string, object>
                {
                    ["period"] = period,
                    ["ts"] = ts
                });

            List<Dictionary<string, object>> rows = ReignDatabase.QuerySql(connection, @"
SELECT * FROM relationship_pair_chemistry
WHERE last_decay_period>=0 AND last_decay_period<$period
AND (affinity_a_to_b<>0 OR affinity_b_to_a<>0);",
                new Dictionary<string, object> { ["period"] = period });
            if (rows.Count == 0)
            {
                return new Dictionary<string, object>
                {
                    ["period"] = period,
                    ["pairsDecayed"] = 0,
                    ["directionsDecayed"] = 0,
                    ["nativeChangesQueued"] = 0,
                    ["loversEnded"] = 0,
                    ["courtPopularitySubjectIds"] = new List<object>()
                };
            }

            Dictionary<string, int> standings = ReignDatabase.QuerySql(connection, @"
SELECT subject_id,standing_value FROM character_public_standing
WHERE campaign_id=$campaign AND timeline_id=$timeline;",
                new Dictionary<string, object>
                {
                    ["campaign"] = campaignId,
                    ["timeline"] = timelineId
                })
                .Where(row => !string.IsNullOrWhiteSpace(
                    ReadString(row, "subject_id", "")))
                .ToDictionary(row => ReadString(row, "subject_id", ""),
                    row => ReadInt(row, "standing_value", 0),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> lifecycleByPair =
                ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_pair_lifecycle;")
                    .ToDictionary(row => ReadString(row, "pair_key", ""),
                        row => row, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, Dictionary<string, object>> targetsByPair =
                ReignDatabase.QuerySql(connection, "SELECT * FROM relationship_native_targets;")
                    .ToDictionary(row => ReadString(row, "pair_key", ""),
                        row => row, StringComparer.OrdinalIgnoreCase);

            List<Dictionary<string, object>> pairUpdates =
                new List<Dictionary<string, object>>(rows.Count);
            List<Dictionary<string, object>> targetUpserts =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> targetDeletes =
                new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> endedLovers =
                new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> row in rows)
            {
                int steps = Math.Max(0, period
                    - ReadInt(row, "last_decay_period", -1));
                if (steps == 0) continue;

                string pairKey = ReadString(row, "pair_key", "");
                string heroA = ReadString(row, "hero_a_id", "");
                string heroB = ReadString(row, "hero_b_id", "");
                int oldAB = ReadInt(row, "affinity_a_to_b", 0);
                int oldBA = ReadInt(row, "affinity_b_to_a", 0);
                int affinityAB = MoveTowardZero(oldAB, steps);
                int affinityBA = MoveTowardZero(oldBA, steps);
                directionsDecayed += (oldAB == affinityAB ? 0 : 1)
                    + (oldBA == affinityBA ? 0 : 1);
                if (CourtPopularityQualification(oldAB)
                    != CourtPopularityQualification(affinityAB))
                    courtPopularitySubjects.Add(heroB);
                if (CourtPopularityQualification(oldBA)
                    != CourtPopularityQualification(affinityBA))
                    courtPopularitySubjects.Add(heroA);

                string tagAB = DirectionalRelationshipTag(campaignId,
                    pairKey, "a_to_b", affinityAB, affinityBA);
                string tagBA = DirectionalRelationshipTag(campaignId,
                    pairKey, "b_to_a", affinityBA, affinityAB);
                string sharedTag = ReadString(row, "shared_tag", "");
                if (lifecycleByPair.TryGetValue(pairKey,
                    out Dictionary<string, object> lifecycle)
                    && ReadInt(lifecycle, "lover_active", 0) == 1
                    && (affinityAB < 30 || affinityBA < 30))
                {
                    endedLovers.Add(new Dictionary<string, object>
                    {
                        ["pair"] = pairKey,
                        ["a"] = heroA,
                        ["b"] = heroB,
                        ["ab"] = affinityAB,
                        ["ba"] = affinityBA,
                        ["ts"] = ts
                    });
                    loversEnded++;
                    sharedTag = SharedRelationshipTag(campaignId, pairKey,
                        affinityAB, affinityBA,
                        new Dictionary<string, object>(),
                        new Dictionary<string, object>());
                }

                int standingA = standings.TryGetValue(heroA,
                    out int standingAValue) ? standingAValue : 0;
                int standingB = standings.TryGetValue(heroB,
                    out int standingBValue) ? standingBValue : 0;
                int effectiveAB = Clamp(affinityAB + standingB, -100, 100);
                int effectiveBA = Clamp(affinityBA + standingA, -100, 100);
                int projected = ProjectNativeRelation(connection, heroA, heroB,
                    affinityAB, affinityBA, effectiveAB, effectiveBA);

                targetsByPair.TryGetValue(pairKey,
                    out Dictionary<string, object> target);
                int observed = target != null
                    ? ReadInt(target, "observed_relation", 0)
                    : ReadInt(row, "native_action_pending", 0) == 0
                        ? ReadInt(row, "projected_native_relation", 0)
                        : 0;
                bool pending = projected != observed;
                string action = pending ? "native_target:" + pairKey : "";

                pairUpdates.Add(new Dictionary<string, object>
                {
                    ["pair"] = pairKey,
                    ["ab"] = affinityAB,
                    ["ba"] = affinityBA,
                    ["tagab"] = tagAB,
                    ["tagba"] = tagBA,
                    ["shared"] = sharedTag,
                    ["projected"] = projected,
                    ["pending"] = pending ? 1 : 0,
                    ["action"] = action,
                    ["period"] = period,
                    ["ts"] = ts
                });

                if (pending)
                {
                    bool targetChanged = target == null
                        || ReadInt(target, "target_relation", int.MinValue)
                            != projected
                        || ReadInt(target, "observed_relation", int.MinValue)
                            != observed
                        || !new[] { "pending", "claimed" }.Contains(
                            ReadString(target, "status", ""),
                            StringComparer.OrdinalIgnoreCase);
                    if (targetChanged)
                    {
                        targetUpserts.Add(new Dictionary<string, object>
                        {
                            ["pair"] = pairKey,
                            ["actor"] = heroA,
                            ["targethero"] = heroB,
                            ["targetrelation"] = projected,
                            ["observed"] = observed,
                            ["day"] = (double)day,
                            ["ts"] = ts
                        });
                        nativeQueued++;
                    }
                }
                else if (target != null)
                {
                    targetDeletes.Add(new Dictionary<string, object>
                    {
                        ["pair"] = pairKey
                    });
                    nativeQueued++;
                }
                pairsDecayed++;
            }

            if (endedLovers.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,a text,b text,ab integer,ba integer,ts bigint)
)
UPDATE relationship_pair_lifecycle AS lifecycle
SET lover_active=0,
    affair_active=0,
    romance_stage=CASE WHEN LEAST(x.ab,x.ba) >= 25
        THEN 'flirtation' ELSE '' END,
    romance_stage_started_day=0,
    romance_positive_bonus=CASE WHEN LEAST(x.ab,x.ba) >= 25
        THEN 10 ELSE 0 END,
    affair_episode=CASE WHEN affair_attempted=1
        THEN GREATEST(1,affair_episode)+1 ELSE affair_episode END,
    affair_attempted=0,
    affair_passed=0,
    judgment_trait_a=0,
    judgment_trait_b=0,
    judgment_roll_a=0,
    judgment_roll_b=0,
    updated_ts=x.ts
FROM x WHERE lifecycle.pair_key=x.pair;", 
                    PostgreSqlRelationshipRowsJson(endedLovers));
                foreach (Dictionary<string, object> ended in endedLovers)
                {
                    AmbientPairContext pair = new AmbientPairContext
                    {
                        PairKey = ReadString(ended, "pair", ""),
                        HeroAId = ReadString(ended, "a", ""),
                        HeroBId = ReadString(ended, "b", "")
                    };
                    RecordRelationshipIncident(connection, pair,
                        "lovers_ended", day,
                        "Five-day relationship decay lowered at least one lover below thirty.",
                        new Dictionary<string, object>
                        {
                            ["affinityAToB"] = ReadInt(ended, "ab", 0),
                            ["affinityBToA"] = ReadInt(ended, "ba", 0)
                        });
                }
            }

            if (pairUpdates.Count > 0)
            {
                ExecutePostgreSqlJsonCommand(connection, @"
WITH x AS (
    SELECT * FROM jsonb_to_recordset(@rows) AS r(
        pair text,ab integer,ba integer,tagab text,tagba text,
        shared text,projected integer,pending integer,action text,
        period integer,ts bigint)
)
UPDATE relationship_pair_chemistry AS chemistry
SET affinity_a_to_b=x.ab,
    affinity_b_to_a=x.ba,
    effective_affinity_a_to_b=x.ab,
    effective_affinity_b_to_a=x.ba,
    tag_a_to_b=x.tagab,
    tag_b_to_a=x.tagba,
    shared_tag=x.shared,
    projected_native_relation=x.projected,
    native_action_pending=x.pending,
    native_action_id=x.action,
    native_incident_offset=0,
    last_decay_period=x.period,
    state_revision=chemistry.state_revision+1,
    updated_ts=x.ts
FROM x
WHERE chemistry.pair_key=x.pair;", 
                    PostgreSqlRelationshipRowsJson(pairUpdates));
                InvalidateRelationshipPairStateCache(campaignId);
            }
            ExecutePostgreSqlRelationshipWriteSet(connection, null,
                targetUpserts, targetDeletes);

            return new Dictionary<string, object>
            {
                ["period"] = period,
                ["pairsDecayed"] = pairsDecayed,
                ["directionsDecayed"] = directionsDecayed,
                ["nativeChangesQueued"] = nativeQueued,
                ["loversEnded"] = loversEnded,
                ["courtPopularitySubjectIds"] =
                    courtPopularitySubjects.OrderBy(value => value)
                        .Cast<object>().ToList()
            };
        }

        private static int MoveTowardZero(int value, int steps)
        {
            if (value > 0) return Math.Max(0, value - Math.Max(0, steps));
            if (value < 0) return Math.Min(0, value + Math.Max(0, steps));
            return 0;
        }

        private static void RecordRelationshipIncident(ReignDbConnection connection, AmbientPairContext pair, string kind,
            double day, string summary, Dictionary<string, object> payload, string rumorId = "")
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO relationship_incidents(
incident_id,pair_key,kind,hero_a_id,hero_b_id,world_day,summary,rumor_id,payload_json,created_ts)
VALUES($id,$pair,$kind,$a,$b,$day,$summary,$rumor,$payload,$ts);",
                new Dictionary<string, object>
                {
                    ["id"] = "relationship_incident_" + Guid.NewGuid().ToString("N"),
                    ["pair"] = pair.PairKey, ["kind"] = kind, ["a"] = pair.HeroAId, ["b"] = pair.HeroBId,
                    ["day"] = day, ["summary"] = summary ?? "", ["rumor"] = rumorId ?? "",
                    ["payload"] = Json.Serialize(payload ?? new Dictionary<string, object>()),
                    ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }
    }
}
