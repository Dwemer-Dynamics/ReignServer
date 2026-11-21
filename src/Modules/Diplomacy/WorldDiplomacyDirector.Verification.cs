using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Diplomacy;

namespace ReignBetaServer
{
    internal static partial class DiplomacyService
    {
#if !REIGN_EXCLUDE_VERIFICATION
        internal static Dictionary<string, object> RunWorldDiplomacySelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => tests.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed, ["detail"] = detail });
            var stressActor = new Dictionary<string, object> { ["economicStress"] = new Dictionary<string, object>
                { ["schemaVersion"] = 2, ["observedDay"] = 100d, ["maximum"] = .8d } };
            add("war_economy_fresh_stress", CurrentEconomicStress(stressActor, 100d) == .8d, "Current versioned stress is used.");
            add("war_economy_stale_stress", CurrentEconomicStress(stressActor, 102d) == 0d, "Stale evidence does not influence diplomacy.");
            add("war_economy_invalid_day", CurrentEconomicStress(stressActor, double.NaN) == 0d, "Non-finite evidence is rejected.");
            add("war_economy_pressure_is_maximum", Math.Abs(EconomyPeacePressure(50, .8, false) - .64) < .000001, "Economic pressure and fatigue are never added twice.");
            add("war_economy_civil_war_preserved", EconomyPeacePressure(50, 1, true) == .25, "Civil-war resolution retains its own authority.");
            add("war_economy_fatigue_preserved", EconomyPeacePressure(90, .3, false) == .81, "Existing greater fatigue pressure remains effective.");
            var payerActor = TestDirectorKingdom("payer_a", false);
            var payerTarget = TestDirectorKingdom("payer_b", false);
            payerActor["treasury"] = 2000000;
            payerActor["leaderGold"] = 100000;
            var paymentTerms = new Dictionary<string, object>
            {
                ["treatyKind"] = "alliance_package", ["durationDays"] = 84,
                ["gold"] = 650000, ["goldFromHeroStringId"] = "payer_a_ruler",
                ["goldToHeroStringId"] = "payer_b_ruler"
            };
            var payment = new Dictionary<string, object> { ["command"] = "diplomatic_package", ["terms"] = paymentTerms };
            Func<List<string>> paymentErrors = () =>
            {
                var errors = new List<string>();
                ValidateDirectorTerms(payment, new Dictionary<string, object>(), payerActor, payerTarget, errors);
                return errors;
            };
            add("package_rejects_rich_realm_poor_ruler", paymentErrors().Any(e => e.Contains("cannot afford")), "650000 cannot be paid from 100000 personal gold despite 2000000 realm wealth");
            paymentTerms["gold"] = 100000;
            add("package_accepts_exact_payer_balance", paymentErrors().Count == 0, "exact balance is executable");
            paymentTerms["gold"] = 100001;
            add("package_rechecks_raised_counteroffer", paymentErrors().Any(e => e.Contains("cannot afford")), "revised payment is checked again");
            payerActor.Remove("leaderGold");
            add("package_missing_payer_balance_fails_closed", paymentErrors().Any(e => e.Contains("unavailable")), "old snapshots cannot authorize money");
            paymentTerms["gold"] = 0;
            add("package_no_payment_needs_no_balance", paymentErrors().Count == 0, "unpaid alliance stays available");
            paymentTerms["gold"] = 100001;
            paymentTerms["goldFromHeroStringId"] = "payer_b_ruler";
            paymentTerms["goldToHeroStringId"] = "payer_a_ruler";
            add("package_reverse_payment_checks_target", paymentErrors().Any(e => e.StartsWith("target cannot afford")), "explicit reverse payment checks the actual payer");
            Dictionary<string, object> aggressive = TestDirectorVirtues(0d, 100d, 50d, 50d, 50d, 100d, 50d);
            Dictionary<string, object> peaceful = TestDirectorVirtues(100d, 0d, 100d, 100d, 100d, 0d, 100d);
            double aggressiveExpansion = DirectorPersonalityBaseline(DirectorIntentScore("expansion", aggressive));
            double peacefulExpansion = DirectorPersonalityBaseline(DirectorIntentScore("expansion", peaceful));
            double peacefulProsperity = DirectorPersonalityBaseline(DirectorIntentScore("prosperity", peaceful));
            add("aligned_family_baseline", Math.Abs(aggressiveExpansion - DirectorAlignedBaseline) < 0.00001d, aggressiveExpansion.ToString("0.0000"));
            add("opposed_family_baseline", Math.Abs(peacefulExpansion - DirectorOpposedBaseline) < 0.00001d, peacefulExpansion.ToString("0.0000"));
            add("auth_error_is_safe_for_game_ui",
                DiplomacyErrorForDisplay("{\"error\":{\"message\":\"Invalid Authentication\",\"code\":\"missing_api_key\",\"status\":401}}")
                    == "The main LLM API key is missing or rejected. Open the Reign Control Center, save the key under Server Diagnostics, then run Test LLM Key.",
                "provider JSON is replaced with an actionable local diagnostic");
            add("provider_gateway_error_is_retryable",
                LlmProviderService.IsTransientLlmFailure("{\"error\":{\"message\":\"Invalid request parameters. Please check your input and try again.\",\"type\":\"invalid_request_error\",\"code\":\"provider_error\"}}"),
                "The provider's generic gateway rejection is retried before diplomacy is paused.");
            Dictionary<string, object> validationRejection = DirectorValidationRejected("accepted_action_normalization_failed", new List<string> { "missing clan" });
            add("validation_failure_does_not_pause_ai",
                ReadBool(validationRejection, "ok", false)
                    && ReadString(validationRejection, "status", "") == "accepted_action_normalization_failed"
                    && ReadString(validationRejection, "failureClass", "") == "validation_failed"
                    && !ReadBool(validationRejection, "retryable", true),
                "world-data validation failures are recorded without masquerading as an AI-service outage");
            AddDiplomacyModelOutputSelfTests(add);
            Dictionary<string, object> dualOutcomeReasons = new Dictionary<string, object>
            {
                ["publicReason"] = "I accept these terms.",
                ["acceptReason"] = "These terms serve us well.",
                ["refuseReason"] = "These terms do not serve us."
            };
            add("authoritative_refusal_uses_refusal_narration",
                SelectDiplomaticOutcomeReason(dualOutcomeReasons, "accept", false,
                    "accepted fallback", "refused fallback") == "These terms do not serve us.",
                "a failed deterministic roll cannot publish acceptance narration");
            add("authoritative_acceptance_uses_acceptance_narration",
                SelectDiplomaticOutcomeReason(dualOutcomeReasons, "refuse", true,
                    "accepted fallback", "refused fallback") == "These terms serve us well.",
                "a passed deterministic roll cannot publish refusal narration");
            add("legacy_mismatched_narration_fails_closed",
                SelectDiplomaticOutcomeReason(new Dictionary<string, object>
                    { ["publicReason"] = "I accept these terms." }, "accept", false,
                    "accepted fallback", "refused fallback") == "refused fallback",
                "legacy publicReason is ignored when its reported outcome contradicts the authoritative roll");
            Dictionary<string, object> acknowledgedAnnouncement =
                new Dictionary<string, object>
                {
                    ["delivered"] = false, ["acknowledged"] = false
                };
            MarkDiplomaticEventAcknowledged(acknowledgedAnnouncement, 1234L);
            add("acknowledged_announcement_is_delivered",
                ReadBool(acknowledgedAnnouncement, "delivered", false)
                    && ReadBool(acknowledgedAnnouncement, "acknowledged", false)
                    && ReadLong(acknowledgedAnnouncement,
                        "deliveredTs", 0L) == 1234L,
                "client acknowledgement closes both delivery states instead of leaving a permanent false undelivered warning");
            ArrayList excessiveMarriageOptions = new ArrayList();
            for (int targetIndex = 0; targetIndex < 3; targetIndex++)
            {
                for (int optionIndex = 0; optionIndex < 20; optionIndex++)
                {
                    excessiveMarriageOptions.Add(new Dictionary<string, object>
                    {
                        ["kingdomAId"] = "perspective",
                        ["kingdomBId"] = "target_" + targetIndex.ToString(CultureInfo.InvariantCulture),
                        ["heroAId"] = "a_" + targetIndex.ToString(CultureInfo.InvariantCulture) + "_" + optionIndex.ToString(CultureInfo.InvariantCulture),
                        ["heroBId"] = "b_" + targetIndex.ToString(CultureInfo.InvariantCulture) + "_" + optionIndex.ToString(CultureInfo.InvariantCulture)
                    });
                }
            }
            excessiveMarriageOptions.Add(new Dictionary<string, object>
            {
                ["kingdomAId"] = "unrelated_a", ["kingdomBId"] = "unrelated_b",
                ["heroAId"] = "unrelated_hero_a", ["heroBId"] = "unrelated_hero_b"
            });
            Dictionary<string, object> compactMarriageWorld = CompactDirectorWorld(
                new Dictionary<string, object> { ["allianceMarriageOptions"] = excessiveMarriageOptions },
                "perspective");
            List<Dictionary<string, object>> compactMarriageRows = ReadDictionaryList(compactMarriageWorld, "allianceMarriageOptions");
            add("marriage_prompt_uses_one_server_selected_pair_per_counterparty",
                compactMarriageRows.Count == 3
                    && compactMarriageRows.All(x => DirectorMarriageOptionInvolvesKingdom(x, "perspective")),
                compactMarriageRows.Count.ToString(CultureInfo.InvariantCulture) + " ideal pairs selected from " + excessiveMarriageOptions.Count.ToString(CultureInfo.InvariantCulture));
            Dictionary<string, object> counterpartMarriageWorld = CompactDirectorWorld(
                new Dictionary<string, object> { ["allianceMarriageOptions"] = excessiveMarriageOptions },
                "perspective",
                "target_1");
            add("responder_marriage_options_are_pair_scoped",
                ReadDictionaryList(counterpartMarriageWorld, "allianceMarriageOptions").Count == 1
                    && ReadDictionaryList(counterpartMarriageWorld, "allianceMarriageOptions")
                        .All(x => DirectorMarriageOptionInvolvesKingdom(x, "target_1")),
                "Responder prompts include only the two negotiating kingdoms.");
            Dictionary<string, object> idealCandidateWorld = new Dictionary<string, object>
            {
                ["allianceMarriageCandidates"] = new ArrayList
                {
                    new Dictionary<string, object> { ["heroId"] = "ordinary_a", ["heroName"] = "Ordinary A", ["kingdomId"] = "a", ["clanId"] = "clan_a", ["age"] = 30d, ["isFemale"] = false, ["clanTier"] = 4 },
                    new Dictionary<string, object> { ["heroId"] = "dynastic_a", ["heroName"] = "Dynastic A", ["kingdomId"] = "a", ["clanId"] = "clan_a", ["age"] = 25d, ["isFemale"] = false, ["clanTier"] = 4, ["isRulerChild"] = true, ["successionRank"] = 2 },
                    new Dictionary<string, object> { ["heroId"] = "ordinary_b", ["heroName"] = "Ordinary B", ["kingdomId"] = "b", ["clanId"] = "clan_b", ["age"] = 29d, ["isFemale"] = true, ["clanTier"] = 4 },
                    new Dictionary<string, object> { ["heroId"] = "dynastic_b", ["heroName"] = "Dynastic B", ["kingdomId"] = "b", ["clanId"] = "clan_b", ["age"] = 24d, ["isFemale"] = true, ["clanTier"] = 4, ["isRulerChild"] = true, ["successionRank"] = 2 }
                }
            };
            Dictionary<string, object> idealCandidatePair = SelectIdealDirectorMarriageOption(idealCandidateWorld, "a", "b");
            add("server_selects_highest_value_dynastic_couple",
                ReadString(idealCandidatePair, "heroAId", "") == "dynastic_a"
                    && ReadString(idealCandidatePair, "heroBId", "") == "dynastic_b"
                    && DirectorMarriageOptionExists(idealCandidateWorld, "a", "b", "dynastic_a", "dynastic_b"),
                Json.Serialize(idealCandidatePair));
            add("peaceful_ruler_favors_prosperity", peacefulProsperity > peacefulExpansion, peacefulProsperity.ToString("0.0000") + " > " + peacefulExpansion.ToString("0.0000"));
            add("neutral_family_baseline", Math.Abs(DirectorPersonalityBaseline(50d) - DirectorNeutralBaseline) < 0.00001d, DirectorPersonalityBaseline(50d).ToString("0.0000"));
            add("zero_score_baseline", Math.Abs(DirectorPersonalityBaseline(0d) - DirectorOpposedBaseline) < 0.00001d, DirectorPersonalityBaseline(0d).ToString("0.0000"));
            add("hundred_score_baseline", Math.Abs(DirectorPersonalityBaseline(100d) - DirectorAlignedBaseline) < 0.00001d, DirectorPersonalityBaseline(100d).ToString("0.0000"));
            Dictionary<string, object> weighted = TestDirectorVirtues(20d, 80d, 10d, 30d, 40d, 40d, 70d);
            add("expansion_exact_weights", Math.Abs(DirectorIntentScore("expansion", weighted) - 72d) < 0.00001d, DirectorIntentScore("expansion", weighted).ToString("0.00"));
            add("prosperity_exact_weights", Math.Abs(DirectorIntentScore("prosperity", weighted) - 54.5d) < 0.00001d, DirectorIntentScore("prosperity", weighted).ToString("0.00"));
            add("security_exact_weights", Math.Abs(DirectorIntentScore("security", weighted) - 42.5d) < 0.00001d, DirectorIntentScore("security", weighted).ToString("0.00"));
            add("peace_exact_weights", Math.Abs(DirectorIntentScore("peace", weighted) - 37d) < 0.00001d, DirectorIntentScore("peace", weighted).ToString("0.00"));
            Dictionary<string, object> onlyCompassionChanged = TestDirectorVirtues(80d, 80d, 10d, 30d, 40d, 40d, 70d);
            add("expansion_inverts_compassion", Math.Abs(DirectorIntentScore("expansion", weighted) - DirectorIntentScore("expansion", onlyCompassionChanged) - 36d) < 0.00001d, "raising Compassion by 60 lowers Expansion by 36");
            Dictionary<string, object> assessment = DirectorIntentAssessment("expansion", weighted);
            Dictionary<string, object> contributors = ReadDictionary(assessment, "contributors") ?? new Dictionary<string, object>();
            add("initiative_uses_only_main_groups", contributors.Count == 3 && contributors.Keys.All(DirectorCourtVirtueKeys.Contains), string.Join(",", contributors.Keys));
            Dictionary<string, object> missingProfile = LoadDirectorTraits("__director_self_test_missing_campaign__", new Dictionary<string, object> { ["leaderHeroId"] = "__director_self_test_missing_hero__" });
            add("missing_profile_is_neutral", missingProfile.Count == DirectorCourtVirtueKeys.Length && DirectorCourtVirtueKeys.All(x => Math.Abs(ReadDouble(missingProfile, x, -1d) - 50d) < 0.00001d), Json.Serialize(missingProfile));
            add("defensive_war_not_expansion", DirectorIntentCommands["expansion"].Contains("declare_war") && !DirectorIntentCommands["security"].Contains("declare_war"), "Security cannot emit a conquest declaration");
            add("opportunity_blends_to_six_percent", Math.Abs(BlendDirectorChance(DirectorOpposedBaseline, DirectorOpportunityCeiling, 1d) - DirectorOpportunityCeiling) < 0.00001d, DirectorOpportunityCeiling.ToString("0.0000"));
            add("peace_curve_is_nonlinear", Math.Abs(BlendDirectorChance(DirectorNeutralBaseline, DirectorPeaceCeiling, Math.Pow(0.5d, 2d)) - 0.04125d) < 0.00001d, "50 fatigue yields 4.125 percent");
            add("war_fatigue_remains_strategic", Math.Abs(EffectiveWarFatigue(50d) - 50d) < 0.00001d, "personality is applied through the Peace score, not folded into objective fatigue");
            add("retired_actions_excluded", !DirectorSupportedCommands.Contains("sign_temporary_truce") && !DirectorSupportedCommands.Contains("trade_embargo") && DirectorSupportedCommands.Contains("caravan_protection_agreement"), "Only temporary truce and trade embargo remain retired; caravan protection is supported.");
            add("stable_roll", Math.Abs(StableDirectorRoll("campaign", 10, "ruler") - StableDirectorRoll("campaign", 10, "ruler")) < double.Epsilon, "seeded result repeats");
            add("supported_commands_map_to_actions", DirectorSupportedCommands.All(x => !string.IsNullOrWhiteSpace(ActionPipelineService.MapCommandToActionType(x))), "all " + DirectorSupportedCommands.Count + " supported commands map to action types");
            add("harsh_peace_terms_remain_positive",
                ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "demand_reparations_peace" }) == "positive"
                && ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "demand_settlement_peace" }) == "positive"
                && ClassifyRulerDiplomacyPolarity(new Dictionary<string, object>
                    { ["command"] = "demand_surrender_peace" }) == "positive",
                "Ending an active war is conciliatory for opportunity routing while harsh terms remain part of willingness and counteroffer logic.");
            AddRandomDiplomacyDebugSelfTests(add);

            Dictionary<string, object> weakPowerKingdom = TestDirectorKingdom("weak", false);
            Dictionary<string, object> strongPowerKingdom = TestDirectorKingdom("strong", false);
            Dictionary<string, object> nordPowerKingdom = TestDirectorKingdom("nords", false);
            weakPowerKingdom["strength"] = 500d; weakPowerKingdom["fiefCount"] = 1; weakPowerKingdom["settlementProsperity"] = 2000d; weakPowerKingdom["garrisonStrength"] = 200d; weakPowerKingdom["clanCount"] = 2; weakPowerKingdom["dailyGoldChange"] = -500d;
            strongPowerKingdom["strength"] = 2000d; strongPowerKingdom["fiefCount"] = 6; strongPowerKingdom["settlementProsperity"] = 18000d; strongPowerKingdom["garrisonStrength"] = 1800d; strongPowerKingdom["clanCount"] = 8; strongPowerKingdom["dailyGoldChange"] = 1000d;
            nordPowerKingdom["strength"] = 1200d; nordPowerKingdom["fiefCount"] = 3; nordPowerKingdom["settlementProsperity"] = 7000d; nordPowerKingdom["garrisonStrength"] = 700d; nordPowerKingdom["clanCount"] = 4; nordPowerKingdom["dailyGoldChange"] = 200d;
            Dictionary<string, object> powerWorld = new Dictionary<string, object> { ["kingdoms"] = new ArrayList { weakPowerKingdom, strongPowerKingdom, nordPowerKingdom }, ["settlements"] = new ArrayList(), ["allianceMarriageOptions"] = new ArrayList() };
            powerWorld["nationalPowerAssessments"] = BuildNationalPowerAssessments(powerWorld, new Dictionary<string, object>());
            add("custom_nords_receive_power_assessment", ReadDictionaryList(powerWorld, "nationalPowerAssessments").Any(x => ReadString(x, "kingdomId", "") == "nords"), "dynamic nords kingdom included");
            add("composite_power_rewards_full_strength", DirectorPowerScore(powerWorld, "strong", 0d) > DirectorPowerScore(powerWorld, "weak", 0d), "military, territory, economy, and clans contribute");
            double nordReadyPower = DirectorPowerScore(powerWorld, "nords", 0d);
            nordPowerKingdom["enemies"] = new ArrayList { "strong" };
            nordPowerKingdom["rulerIsPrisoner"] = true;
            nordPowerKingdom["dailyGoldChange"] = -5000d;
            Dictionary<string, object> burdenedState = new Dictionary<string, object> { ["wars"] = new Dictionary<string, object> { ["nords|strong"] = new Dictionary<string, object> { ["active"] = true, ["kingdomAId"] = "nords", ["kingdomBId"] = "strong", ["fatigueA"] = 80d, ["fatigueB"] = 10d } } };
            powerWorld["nationalPowerAssessments"] = BuildNationalPowerAssessments(powerWorld, burdenedState);
            add("war_readiness_reduces_power", DirectorPowerScore(powerWorld, "nords", 0d) < nordReadyPower, "war, fatigue, captivity, and negative income reduce usable power");
            nordPowerKingdom["enemies"] = new ArrayList(); nordPowerKingdom["rulerIsPrisoner"] = false; nordPowerKingdom["dailyGoldChange"] = 200d;

            powerWorld["nationalPowerAssessments"] = new ArrayList
            {
                new Dictionary<string, object> { ["kingdomId"] = "weak", ["score"] = 0.60d },
                new Dictionary<string, object> { ["kingdomId"] = "strong", ["score"] = 1.00d },
                new Dictionary<string, object> { ["kingdomId"] = "nords", ["score"] = 1.60d }
            };
            Dictionary<string, object> bareAlliance = new Dictionary<string, object> { ["command"] = "sign_alliance", ["actorKingdomStringId"] = "weak", ["terms"] = new Dictionary<string, object>() };
            Dictionary<string, object> weakLeverage = BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, bareAlliance);
            Dictionary<string, object> strongLeverage = BuildAllianceLeverage(powerWorld, nordPowerKingdom, strongPowerKingdom, bareAlliance);
            add("much_weaker_alliance_modifier", ReadInt(weakLeverage, "powerModifier", 0) == -15 && ReadInt(weakLeverage, "requiredConsideration", 0) == 20, "0.60 ratio requires major consideration");
            add("bare_weak_alliance_fails_consideration", !ReadBool(weakLeverage, "considerationRequirementMet", true), "bare weak offer cannot pass");
            add("dominant_alliance_modifier", ReadInt(strongLeverage, "powerModifier", 0) == 15 && ReadInt(strongLeverage, "requiredConsideration", -1) == 0, "1.60 ratio gains 15 points");
            Dictionary<string, object> weakAssessment = ReadDictionaryList(powerWorld, "nationalPowerAssessments").First(x => ReadString(x, "kingdomId", "") == "weak");
            bool bandsExact = true;
            foreach (Tuple<double, int, int> bandCase in new[] { Tuple.Create(0.66d, -15, 20), Tuple.Create(0.67d, -8, 10), Tuple.Create(0.85d, 0, 0), Tuple.Create(1.15d, 8, 0), Tuple.Create(1.50d, 15, 0) })
            {
                weakAssessment["score"] = bandCase.Item1;
                Dictionary<string, object> band = BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, bareAlliance);
                bandsExact &= ReadInt(band, "powerModifier", 999) == bandCase.Item2 && ReadInt(band, "requiredConsideration", 999) == bandCase.Item3;
            }
            add("alliance_ratio_boundaries", bandsExact, "0.67, 0.85, 1.15, and 1.50 boundaries are exact");
            weakAssessment["score"] = 0.60d;
            Dictionary<string, object> marriageTerms = new Dictionary<string, object> { ["treatyKind"] = "alliance_package", ["marriageHero1StringId"] = "weak_spare", ["marriageHero2StringId"] = "strong_spare" };
            powerWorld["allianceMarriageOptions"] = new ArrayList { new Dictionary<string, object> { ["kingdomAId"] = "weak", ["kingdomBId"] = "strong", ["heroAId"] = "weak_spare", ["heroBId"] = "strong_spare" } };
            Dictionary<string, object> marriageAlliance = new Dictionary<string, object> { ["command"] = "diplomatic_package", ["actorKingdomStringId"] = "weak", ["terms"] = marriageTerms };
            Dictionary<string, object> marriageLeverage = BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, marriageAlliance);
            add("marriage_satisfies_major_consideration", Math.Abs(ReadDouble(marriageLeverage, "considerationValue", 0d) - 20d) < 0.001d && ReadBool(marriageLeverage, "considerationRequirementMet", false), "valid marriage contributes 20 points");
            marriageTerms["guaranteeIndependence"] = true; marriageTerms["militaryCommitment"] = true;
            add("consideration_value_caps_at_thirty", Math.Abs(ReadDouble(BuildAllianceLeverage(powerWorld, weakPowerKingdom, strongPowerKingdom, marriageAlliance), "considerationValue", 0d) - 30d) < 0.001d, "compound consideration capped at 30");
            marriageTerms.Remove("guaranteeIndependence"); marriageTerms.Remove("militaryCommitment");
            ResolveAllianceAcceptance("test", 5d, powerWorld, weakPowerKingdom, strongPowerKingdom, marriageAlliance, new Dictionary<string, object> { ["baseWillingness"] = 50d }, false, out Dictionary<string, object> allianceAudit);
            add("alliance_final_chance_formula", Math.Abs(ReadDouble(allianceAudit, "finalChance", 0d) - 55d) < 0.001d, "50 base - 15 power + 20 marriage = 55");

            Dictionary<string, object> kingdomA = TestDirectorKingdom("a", false, "b");
            Dictionary<string, object> kingdomB = TestDirectorKingdom("b", false, "a");
            kingdomA["civilWarOpponentKingdomId"] = "b";
            kingdomB["civilWarOpponentKingdomId"] = "a";
            Dictionary<string, object> playerKingdom = TestDirectorKingdom("player", true);
            Dictionary<string, object> testWorld = new Dictionary<string, object>
            {
                ["playerKingdomId"] = "player",
                ["kingdoms"] = new ArrayList { kingdomA, kingdomB, playerKingdom },
                ["clans"] = new ArrayList
                {
                    new Dictionary<string, object> { ["clanId"] = "clan_a", ["name"] = "Clan A", ["leaderHeroId"] = ReadString(kingdomA, "leaderHeroId", ""), ["kingdomId"] = "a" },
                    new Dictionary<string, object> { ["clanId"] = "clan_b", ["name"] = "Clan B", ["leaderHeroId"] = ReadString(kingdomB, "leaderHeroId", ""), ["kingdomId"] = "b" }
                },
                ["settlements"] = new ArrayList(),
                ["agreements"] = new ArrayList()
            };
            Dictionary<string, object> normalizationPayload = BuildDirectorNormalizationPayload(testWorld, kingdomA);
            Dictionary<string, object> normalizationIndex = ReadDictionary(normalizationPayload, "actionResolutionIndex") ?? new Dictionary<string, object>();
            add("normalization_carries_clan_index",
                ReadDictionaryList(normalizationIndex, "clans").Count == 2,
                "diplomacy validation receives the authoritative native clan records");
            Dictionary<string, object> ransomCandidate = BuildDirectorCandidate(
                new Dictionary<string, object>
                {
                    ["command"] = "ransom_package",
                    ["targetKingdomId"] = "b",
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["gold"] = 20000
                    },
                    ["publicReason"] = "a ruler offered b ruler a broad ransom for captive nobles."
                }, kingdomA);
            ApplyDirectorTargetIdentity(ransomCandidate, kingdomB);
            Dictionary<string, object> ransomNormalizationWorld =
                new Dictionary<string, object>(testWorld,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["settlements"] = new ArrayList
                    {
                        new Dictionary<string, object>
                        {
                            ["settlementId"] = "town_a",
                            ["name"] = "Town A",
                            ["ownerKingdomId"] = "a"
                        }
                    }
                };
            Dictionary<string, object> normalizedRansom = ActionPipelineService.NormalizeActionCommand(
                ransomCandidate, "director_ransom_selftest",
                out List<string> ransomErrors,
                BuildDirectorNormalizationPayload(ransomNormalizationWorld,
                    kingdomA),
                ReadString(ransomCandidate, "publicReason", ""));
            Dictionary<string, object> normalizedRansomTerms = normalizedRansom == null
                ? new Dictionary<string, object>()
                : Json.Deserialize<Dictionary<string, object>>(
                    ReadString(normalizedRansom, "termsJson", "{}"))
                    ?? new Dictionary<string, object>();
            add("autonomous_ransom_keeps_ruler_and_prisoner_roles_separate",
                normalizedRansom != null && ransomErrors.Count == 0
                && ReadString(normalizedRansom, "actorHeroStringId", "")
                    == "a_ruler"
                && ReadString(normalizedRansom, "targetHeroStringId", "")
                    == "b_ruler"
                && !normalizedRansomTerms.ContainsKey("prisonerHeroStringId"),
                "A broad ruler-to-ruler ransom cannot infer a named prisoner from its public explanation or overwrite the responding ruler identity.");
            Dictionary<string, object> playerCandidate = new Dictionary<string, object>
            {
                ["command"] = "sign_trade_agreement", ["actorKingdomStringId"] = "a", ["targetKingdomStringId"] = "player",
                ["publicReason"] = "test", ["confidence"] = 1d, ["terms"] = new Dictionary<string, object>()
            };
            add("player_proposal_target_allowed", !ValidateDirectorCandidate(playerCandidate, testWorld, "player", "prosperity", "").Any(x => x.Contains("player kingdom") || x.Contains("cannot decide for the player")), "NPC proposal may target the player court");
            Dictionary<string, object> playerWarCandidate = new Dictionary<string, object>(playerCandidate, StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = "declare_war"
            };
            add("player_unilateral_target_allowed",
                !ValidateDirectorCandidate(playerWarCandidate, testWorld, "player", "expansion", "").Any(x =>
                    x.Contains("cannot target the player kingdom")),
                "NPC unilateral diplomacy may target the player while the player remains excluded as an autonomous actor.");
            Dictionary<string, object> playerActorCandidate = new Dictionary<string, object>(playerCandidate, StringComparer.OrdinalIgnoreCase)
            {
                ["actorKingdomStringId"] = "player", ["targetKingdomStringId"] = "a"
            };
            add("player_actor_never_autonomous", ValidateDirectorCandidate(playerActorCandidate, testWorld, "player", "prosperity", "").Any(x => x.Contains("cannot decide for the player")), "director never answers or acts for player ruler");
            Dictionary<string, object> civilWarPeaceCandidate =
                new Dictionary<string, object>
                {
                    ["command"] = "demand_surrender_peace",
                    ["actorKingdomStringId"] = "a",
                    ["targetKingdomStringId"] = "b",
                    ["publicReason"] = "test", ["confidence"] = 1d,
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["allTargetFortifications"] = true
                    }
                };
            add("generic_civil_war_peace_is_rejected",
                IsDirectorCivilWarPair(testWorld, "a", "b")
                && ValidateDirectorCandidate(civilWarPeaceCandidate, testWorld,
                    "player", "peace", "b").Any(x =>
                        x.Contains("explicit negotiated political result")),
                "A normal diplomacy action cannot end a civil war; only the dedicated recognition settlement can do so.");

            ((ArrayList)testWorld["agreements"]).Add(new Dictionary<string, object> { ["kind"] = "non_aggression_pact", ["actorKingdomId"] = "a", ["targetKingdomId"] = "b" });
            Dictionary<string, object> pactCandidate = new Dictionary<string, object>
            {
                ["command"] = "declare_war", ["actorKingdomStringId"] = "a", ["targetKingdomStringId"] = "b",
                ["publicReason"] = "test", ["confidence"] = 1d, ["terms"] = new Dictionary<string, object>()
            };
            kingdomA["enemies"] = new ArrayList();
            kingdomB["enemies"] = new ArrayList();
            add("war_atomically_breaks_incompatible_pacts", !ValidateDirectorCandidate(pactCandidate, testWorld, "player", "expansion", "").Any(x => x.Contains("pact")), "native declaration records and penalizes every incompatible agreement breach");
            TreatyBreachPenalties("trade_agreement", out int tradeBetrayed, out int tradeBreaker);
            TreatyBreachPenalties("non_aggression_pact", out int napBetrayed, out int napBreaker);
            TreatyBreachPenalties("defensive_pact", out int defenseBetrayed, out int defenseBreaker);
            add("treaty_breach_relationship_values", tradeBetrayed == -20 && tradeBreaker == -5
                && napBetrayed == -40 && napBreaker == -10 && defenseBetrayed == -50 && defenseBreaker == -10,
                "trade, non-aggression, and defensive breach directions use approved penalties");
            add("war_breach_transaction_cap", Math.Max(-100, -50 + napBetrayed + defenseBetrayed) == -100
                && Math.Max(-100, -10 + napBreaker + defenseBreaker) == -30,
                "declaration consequences stack per agreement and clamp at -100 per direction");
            Dictionary<string, object> tradeCandidate = new Dictionary<string, object>(pactCandidate, StringComparer.OrdinalIgnoreCase) { ["command"] = "sign_trade_agreement" };
            add("intent_rejects_wrong_action_family", ValidateDirectorCandidate(tradeCandidate, testWorld, "player", "expansion", "").Any(x => x.Contains("intent family")), "trade rejected from expansion evaluation");

            Dictionary<string, object> multiWarActor = TestDirectorKingdom("multi", false, "enemy");
            Dictionary<string, object> existingEnemy = TestDirectorKingdom("enemy", false, "multi");
            Dictionary<string, object> proposedEnemy = TestDirectorKingdom("proposed", false);
            existingEnemy["strength"] = 1000d;
            proposedEnemy["strength"] = 600d;
            Dictionary<string, object> multiWarWorld = new Dictionary<string, object>
            {
                ["kingdoms"] = new ArrayList { multiWarActor, existingEnemy, proposedEnemy },
                ["settlements"] = new ArrayList(), ["agreements"] = new ArrayList()
            };
            Dictionary<string, object> secondWarCandidate = new Dictionary<string, object>
            {
                ["command"] = "declare_war", ["actorKingdomStringId"] = "multi", ["targetKingdomStringId"] = "proposed",
                ["publicReason"] = "test", ["confidence"] = 1d, ["terms"] = new Dictionary<string, object>()
            };
            multiWarActor["strength"] = 1600d;
            add("equal_combined_power_blocks_second_war", ValidateDirectorCandidate(secondWarCandidate, multiWarWorld, "", "expansion", "").Any(x => x.Contains("stronger than all existing enemies")), "1600 cannot attack combined strength 1600");
            multiWarActor["strength"] = 1601d;
            add("greater_combined_power_allows_second_war", !ValidateDirectorCandidate(secondWarCandidate, multiWarWorld, "", "expansion", "").Any(x => x.Contains("stronger than all existing enemies")), "1601 may attack combined strength 1600");
            Dictionary<string, object> wrongPolarityPressureCandidate =
                new Dictionary<string, object>(secondWarCandidate,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["targetKingdomStringId"] = "enemy",
                    ["targetKingdomId"] = "enemy",
                    ["politicalPressureId"] = "multi->enemy",
                    ["requiredPolarity"] = "positive"
                };
            List<string> wrongPolarityErrors = ValidateDirectorCandidate(
                wrongPolarityPressureCandidate, multiWarWorld, "", "peace",
                "enemy");
            Dictionary<string, object> pressureFallback =
                BuildPoliticalPressurePeaceFallback(
                    wrongPolarityPressureCandidate, multiWarActor,
                    existingEnemy);
            add("political_pressure_gets_one_clean_peace_fallback",
                ShouldFallbackPoliticalPressurePeace(
                    wrongPolarityPressureCandidate, "peace",
                    wrongPolarityErrors)
                && ReadString(pressureFallback, "command", "")
                    == "make_peace"
                && ValidateDirectorCandidate(pressureFallback,
                    multiWarWorld, "", "peace", "enemy").Count == 0,
                "A polarity-invalid pressure selection retries once as an executable clean-peace proposal.");

            Dictionary<string, object> baseline = new Dictionary<string, object>
            {
                ["startedDay"] = 0d, ["baselineStrengthA"] = 1000d, ["baselineStrengthB"] = 1000d,
                ["baselineTreasuryA"] = 100000d, ["baselineTreasuryB"] = 100000d,
                ["baselineFiefsA"] = 4, ["baselineFiefsB"] = 4
            };
            Dictionary<string, object> quietWar = new Dictionary<string, object> { ["casualtiesA"] = 0, ["casualtiesB"] = 0, ["raidsA"] = 0, ["raidsB"] = 0 };
            Dictionary<string, object> costlyWar = new Dictionary<string, object> { ["casualtiesA"] = 800, ["casualtiesB"] = 0, ["raidsA"] = 0, ["raidsB"] = 5 };
            Dictionary<string, object> stableSide = TestDirectorKingdom("a", false, "b");
            stableSide["strength"] = 1000d; stableSide["treasury"] = 100000; stableSide["fiefCount"] = 4;
            Dictionary<string, object> stressedSide = new Dictionary<string, object>(stableSide, StringComparer.OrdinalIgnoreCase) { ["strength"] = 300d, ["treasury"] = 10000, ["fiefCount"] = 2, ["rulerIsPrisoner"] = true, ["capturedNobleCount"] = 2 };
            double quietFatigue = CalculateObjectiveFatigue(baseline, quietWar, stableSide, kingdomB, true, 30d);
            double costlyFatigue = CalculateObjectiveFatigue(baseline, costlyWar, stressedSide, kingdomB, true, 30d);
            add("war_losses_raise_fatigue", costlyFatigue > quietFatigue, costlyFatigue.ToString("0.0") + " > " + quietFatigue.ToString("0.0"));

            Dictionary<string, object> original = new Dictionary<string, object>
            {
                ["command"] = "demand_settlement_peace",
                ["actorKingdomStringId"] = "multi",
                ["targetKingdomId"] = "enemy",
                ["targetKingdomStringId"] = "enemy",
                ["targetSettlementId"] = "town_old",
                ["targetSettlementStringId"] = "town_old",
                ["terms"] = new Dictionary<string, object>
                {
                    ["settlementIds"] = new ArrayList { "town_old" },
                    ["reparationsGold"] = 1000
                }
            };
            Dictionary<string, object> counter = new Dictionary<string, object>
            {
                ["command"] = "make_peace",
                ["targetSettlementId"] = "",
                ["terms"] = new Dictionary<string, object>(),
                ["publicReason"] = "No land changes hands."
            };
            Dictionary<string, object> replacement = ApplyCounteroffer(original,
                counter);
            add("counteroffer_is_complete_replacement",
                ReadString(replacement, "command", "") == "make_peace"
                    && string.IsNullOrWhiteSpace(ReadString(replacement,
                        "targetSettlementId", ""))
                    && (ReadDictionary(replacement, "terms")
                        ?? new Dictionary<string, object>()).Count == 0,
                "ordinary peace cannot inherit the original settlement or payment");
            Dictionary<string, object> missingCommandCounter =
                ApplyCounteroffer(original, new Dictionary<string, object>
                {
                    ["terms"] = new Dictionary<string, object>(),
                    ["publicReason"] = "A vague revision."
                });
            add("counteroffer_without_command_fails_closed",
                ValidateDirectorCandidate(missingCommandCounter, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.StartsWith("unsupported command",
                            StringComparison.OrdinalIgnoreCase)),
                "a counter must identify its executable replacement command");
            add("invalid_counteroffer_narrates_polarity_failure",
                InvalidCounterofferPublicReason(missingCommandCounter,
                    new List<string>
                    {
                        "relationship bonus requires positive polarity but the proposed action is negative"
                    }).IndexOf("reversed the purpose",
                        StringComparison.OrdinalIgnoreCase) >= 0,
                "a safely rejected polarity reversal receives a specific public explanation");

            Dictionary<string, object> emptySettlementPeace =
                new Dictionary<string, object>(replacement,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = "demand_settlement_peace"
                };
            add("settlement_peace_requires_asset",
                ValidateDirectorCandidate(emptySettlementPeace, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.Contains("at least one explicit settlement")),
                "an empty territorial demand is rejected");
            Dictionary<string, object> emptyReparations =
                new Dictionary<string, object>(replacement,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = "demand_reparations_peace"
                };
            add("reparations_require_value",
                ValidateDirectorCandidate(emptyReparations, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.Contains("positive reparationsGold")),
                "an empty financial demand is rejected");
            Dictionary<string, object> emptyRansom =
                new Dictionary<string, object>(replacement,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["command"] = "ransom_package"
                };
            add("ransom_requires_payment",
                ValidateDirectorCandidate(emptyRansom, multiWarWorld,
                    "", "peace", "enemy").Any(x =>
                        x.Contains("requires positive gold")),
                "an empty ransom package is rejected");

            Dictionary<string, object> compactScopeWorld =
                new Dictionary<string, object>
                {
                    ["kingdoms"] = new ArrayList
                    {
                        TestDirectorKingdom("scope_a", false),
                        TestDirectorKingdom("scope_b", false),
                        TestDirectorKingdom("scope_c", false)
                    },
                    ["settlements"] = new ArrayList(),
                    ["wars"] = new ArrayList(), ["agreements"] = new ArrayList(),
                    ["relations"] = new ArrayList(), ["prisoners"] = new ArrayList(),
                    ["recentDiplomaticEvents"] = new ArrayList(),
                    ["nationalPowerAssessments"] = new ArrayList(),
                    ["allianceMarriageOptions"] = new ArrayList()
                };
            ArrayList scopeSettlements = (ArrayList)compactScopeWorld["settlements"];
            foreach (string owner in new[] { "scope_a", "scope_b", "scope_c" })
            {
                for (int index = 0; index < 12; index++)
                    scopeSettlements.Add(new Dictionary<string, object>
                    {
                        ["settlementId"] = owner + "_" + index,
                        ["ownerKingdomId"] = owner,
                        ["isFortification"] = index < 6,
                        ["garrison"] = 100 + index,
                        ["loyalty"] = 50d, ["prosperity"] = 1000d - index
                    });
            }
            Dictionary<string, object> pairScope = CompactDirectorWorld(
                compactScopeWorld, "scope_a", "scope_b", "peace");
            add("response_prompt_is_bilateral",
                ReadDictionaryList(pairScope, "kingdoms").Count == 2
                    && ReadDictionaryList(pairScope, "settlements").All(x =>
                        !ReadString(x, "ownerKingdomId", "").Equals("scope_c",
                            StringComparison.OrdinalIgnoreCase))
                    && ReadDictionaryList(pairScope,
                        "allianceMarriageOptions").Count == 0,
                "responses carry only the two kingdoms and intent-relevant data");
            Dictionary<string, object> broadScope = CompactDirectorWorld(
                compactScopeWorld, "scope_a", "", "expansion");
            add("initial_prompt_bounds_settlement_summaries",
                ReadDictionaryList(broadScope, "settlements").Count <= 14
                    && ReadDictionaryList(broadScope,
                        "allianceMarriageOptions").Count == 0,
                "perspective assets are capped at eight and each possible target at three");

            // A grown government must not turn a compact diplomacy world back into
            // an oversized request through the separately serialized ruler rows.
            var governmentPromptRuler = TestDirectorKingdom("prompt_realm", false);
            governmentPromptRuler["leaderGold"] = 73000;
            var detailedPeople = new ArrayList(Enumerable.Range(0, 64).Select(index =>
                (object)new Dictionary<string, object>
                {
                    ["memberHeroId"] = "member_" + index,
                    ["name"] = "Public member " + index + " " + new string('x', 120),
                    ["optionId"] = "approve", ["weight"] = 1.25d,
                    ["eligible"] = true
                }).ToList());
            var detailedMatters = new ArrayList(Enumerable.Range(0, 24).Select(index =>
                (object)new Dictionary<string, object>
                {
                    ["businessId"] = "matter_" + index, ["status"] = "presented",
                    ["title"] = "A complete diplomatic motion " + index,
                    ["summary"] = "The exact tariff cost, fixed expiry and beneficiary remain part of this proposal.",
                    ["targetKingdomId"] = "other_realm", ["petitionerHeroId"] = "petitioner",
                    ["sponsorHeroId"] = "sponsor", ["options"] = new ArrayList
                    {
                        new Dictionary<string, object> { ["id"] = "approve", ["description"] = "A 42-day trial with a 15% tariff loss." }
                    },
                    ["participants"] = detailedPeople, ["ballots"] = detailedPeople,
                    ["publicPositions"] = detailedPeople,
                    ["forecast"] = new Dictionary<string, object>
                    {
                        ["isFinal"] = false, ["crownWeight"] = 25,
                        ["undecidedWeight"] = 35d, ["decisionDay"] = 91154d,
                        ["options"] = new ArrayList
                        {
                            new Dictionary<string, object>
                            {
                                ["optionId"] = "approve", ["weight"] = 65d,
                                ["names"] = new ArrayList(Enumerable.Range(0, 64)
                                    .Select(index => (object)("Public voter " + index)).ToList())
                            }
                        }
                    }
                }).ToList());
            governmentPromptRuler["government"] = new Dictionary<string, object>
            {
                ["available"] = true, ["authorityLevel"] = 4, ["crownVotingWeight"] = 25,
                ["decisionRule"] = "Largest frozen share wins without a later veto.",
                ["parties"] = new ArrayList { new Dictionary<string, object> { ["partyId"] = "trade", ["planks"] = new ArrayList { "Trade", "Revenue" } } },
                ["activeResolutions"] = new ArrayList { new Dictionary<string, object> { ["resolutionId"] = "obligation", ["requiredAmount"] = 400, ["currentValue"] = 120 } },
                ["governmentBusiness"] = detailedMatters
            };
            string unprojectedRulerJson = Json.Serialize(governmentPromptRuler);
            string projectedRulerJson = DirectorRulerPromptJson(governmentPromptRuler);
            var projectedGovernment = ReadDictionary(TryParseJsonObject(projectedRulerJson), "government");
            var projectedMatters = ReadDictionaryList(projectedGovernment, "governmentBusiness");
            var projectedForecast = ReadDictionary(projectedMatters[0], "forecast");
            var projectedShare = ReadDictionaryList(projectedForecast, "options")[0];
            add("ruler_government_prompt_has_growth_headroom",
                unprojectedRulerJson.Length > 240000 && projectedRulerJson.Length < 40000
                    && Json.Serialize(new Dictionary<string, object> { ["content"] = projectedRulerJson }).Length < 80000,
                "Full government input " + unprojectedRulerJson.Length + " characters; projected ruler " + projectedRulerJson.Length + "; unchanged request ceiling 240000.");
            add("ruler_government_prompt_preserves_all_matters_and_terms",
                projectedMatters.Count == 24 && projectedMatters.Select((matter, index) =>
                    ReadString(matter, "businessId", "") == "matter_" + index
                    && ReadString(matter, "summary", "").Contains("exact tariff cost")
                    && ReadString(matter, "targetKingdomId", "") == "other_realm"
                    && ReadString(ReadDictionaryList(matter, "options")[0], "description", "") == "A 42-day trial with a 15% tariff loss.").All(value => value),
                "No matter, complete term, beneficiary summary or diplomatic target is clipped.");
            add("ruler_government_prompt_preserves_authority_and_vote_totals",
                ReadInt(projectedGovernment, "authorityLevel", 0) == 4
                    && ReadInt(projectedGovernment, "crownVotingWeight", 0) == 25
                    && ReadInt(projectedMatters[0], "ballotsCount", 0) == 64
                    && ReadInt(projectedMatters[0], "participantsCount", 0) == 64
                    && ReadInt(projectedMatters[0], "publicPositionsCount", 0) == 64
                    && ReadDouble(projectedShare, "weight", 0d) == 65d
                    && ReadInt(projectedShare, "namedVoterCount", 0) == 64
                    && ReadDouble(projectedForecast, "undecidedWeight", 0d) == 35d
                    && ReadDouble(projectedForecast, "decisionDay", 0d) == 91154d
                    && !ReadBool(projectedForecast, "isFinal", true)
                    && ReadInt(TryParseJsonObject(projectedRulerJson), "leaderGold", 0) == 73000
                    && ReadDictionaryList(projectedGovernment, "activeResolutions").Count == 1
                    && ReadDictionaryList(projectedGovernment, "parties").Count == 1,
                "Political structure, pending delivery, actual ruler funds, dates and numerical shares retain their meaning.");
            add("ruler_government_prompt_does_not_mutate_authoritative_rows",
                Json.Serialize(governmentPromptRuler) == unprojectedRulerJson
                    && DirectorRulerPromptJson(governmentPromptRuler) == projectedRulerJson
                    && !projectedMatters[0].ContainsKey("ballots")
                    && !projectedMatters[0].ContainsKey("publicPositions")
                    && !projectedShare.ContainsKey("names"),
                "Projection is repeatable; the full saved individual evidence remains unchanged outside the prompt.");

            int totalSimulatedEvents = 0;
            const int simulatedCampaigns = 200;
            for (int campaign = 0; campaign < simulatedCampaigns; campaign++)
            {
                Dictionary<string, int> cooldownUntil = Enumerable.Range(0, 8).ToDictionary(x => "ruler" + x, x => 0);
                for (int simulatedDay = 1; simulatedDay <= DirectorCampaignDaysPerYear; simulatedDay += DirectorEvaluationIntervalDays)
                {
                    bool resolved = false;
                    foreach (string rulerId in cooldownUntil.Keys.ToList())
                    {
                        if (simulatedDay < cooldownUntil[rulerId]) continue;
                        foreach (string family in new[] { "expansion", "prosperity", "security" })
                        {
                            if (StableDirectorRoll("simulated_campaign_" + campaign, simulatedDay, rulerId + "|" + family) >= DirectorNeutralBaseline) continue;
                            totalSimulatedEvents++;
                            cooldownUntil[rulerId] = simulatedDay + 15;
                            resolved = true;
                            break;
                        }
                        if (resolved) break;
                    }
                }
            }
            double meanSimulatedEvents = totalSimulatedEvents / (double)simulatedCampaigns;
            double meanEventInterval = DirectorCampaignDaysPerYear / meanSimulatedEvents;
            add("reign_calendar_year_length", DirectorCampaignDaysPerYear == 126, DirectorCampaignDaysPerYear + " campaign days per Reign year");
            add("three_day_evaluation_cadence", DirectorEvaluationIntervalDays == 3, "evaluates every " + DirectorEvaluationIntervalDays + " days");
            add("seeded_year_pacing", meanEventInterval >= 9d && meanEventInterval <= 11d, meanSimulatedEvents.ToString("0.00") + " events; one every " + meanEventInterval.ToString("0.00") + " days");
            tests.AddRange(RunClanAccordSelfTests());
            tests.AddRange(RunClanAccordServerSelfTests());
            bool passedAll = tests.All(x => ReadBool(x, "passed", false));
            return new Dictionary<string, object> { ["passed"] = passedAll, ["count"] = tests.Count, ["tests"] = tests };
        }

        private static void AddDiplomacyModelOutputSelfTests(Action<string, bool, string> add)
        {
            Dictionary<string, object> Output(string content, bool ok = true) =>
                new Dictionary<string, object> { ["ok"] = ok, ["content"] = content };

            foreach (var (stage, field, finalCounterDecision) in new[]
            {
                ("initiative", "decision", false),
                ("proposal", "response", false),
                ("counteroffer", "response", true)
            })
            {
                foreach (var (kind, content) in new[]
                {
                    ("malformed", "{\"" + field + "\":"),
                    ("null", "null"),
                    ("missing_choice", "{}"),
                    ("null_choice", "{\"" + field + "\":null}")
                })
                {
                    Dictionary<string, object> result = ParseDiplomacyModelOutput(Output(content), field, finalCounterDecision);
                    Dictionary<string, object> evaluation = DirectorModelFailure(result, "", false);
                    add(stage + "_" + kind + "_has_no_ruler_choice",
                        !ReadBool(result, "ok", true) && !result.ContainsKey(field)
                            && ReadBool(evaluation, "ok", false)
                            && ReadString(evaluation, "status", "") == "model_output_unusable"
                            && !evaluation.ContainsKey("actionId") && !evaluation.ContainsKey("eventId"),
                        "Unusable output cannot enter the political outcome path or trigger the client outage retry.");
                }
            }

            Dictionary<string, object> noAction = ParseDiplomacyModelOutput(Output("{\"decision\":\"none\"}"), "decision");
            add("initiative_explicit_no_action_remains_a_choice",
                ReadBool(noAction, "ok", false) && ReadString(ReadDictionary(noAction, "decision"), "decision", "") == "none",
                "A usable, explicit no-action decision can still consume an opportunity.");

            Dictionary<string, object> secondCounter = ParseDiplomacyModelOutput(Output("{\"response\":\"counter\"}"), "response", true);
            Dictionary<string, object> firstCounter = ParseDiplomacyModelOutput(Output("{\"response\":\"counter\"}"), "response");
            add("negotiation_allows_only_one_counteroffer",
                ReadBool(firstCounter, "ok", false) && !ReadBool(secondCounter, "ok", true)
                    && !secondCounter.ContainsKey("response"),
                "The final reply requires an explicit acceptance or refusal and cannot extend the negotiation.");

            Dictionary<string, object> exhausted = ParseDiplomacyModelOutput(Output("{\"response\":\"refuse\"}", false), "response");
            add("failed_recovery_cannot_supply_a_ruler_choice",
                !ReadBool(exhausted, "ok", true) && !exhausted.ContainsKey("response")
                    && !ReadBool(DirectorModelFailure(exhausted, "", true), "ok", true),
                "Content retained by a failed recovery is ignored and the debug initiative reports the technical error.");

            Dictionary<string, object> proposer = TestDirectorKingdom("proposer", false);
            Dictionary<string, object> responder = TestDirectorKingdom("responder", false);
            Dictionary<string, object> candidate = new Dictionary<string, object> { ["command"] = "make_peace" };
            foreach (bool counter in new[] { false, true })
            {
                foreach (var (choice, willingness, expected) in new[] { ("refuse", 0, false), ("accept", 100, true) })
                {
                    string content = "{\"response\":\"" + choice + "\",\"baseWillingness\":"
                        + willingness.ToString(CultureInfo.InvariantCulture) + "}";
                    Dictionary<string, object> output = ParseDiplomacyModelOutput(Output(content), "response", counter);
                    Dictionary<string, object> answer = ReadDictionary(output, "response");
                    string stage = counter ? "counteroffer" : "proposal";
                    bool canResolve = ReadBool(output, "ok", false) && ReadString(answer, "response", "") == choice;
                    bool accepted = canResolve && ResolveAllianceAcceptance("diplomacy_contract", 70d,
                        new Dictionary<string, object>(), proposer, responder, candidate, answer, counter, out _);
                    add(stage + "_explicit_" + choice + "_retains_willingness_roll", canResolve && accepted == expected,
                        "Usable responses still yield the authoritative willingness outcome.");
                }
            }
        }
#endif

        private static Dictionary<string, object> TestDirectorKingdom(string id, bool player, params string[] enemies)
        {
            return new Dictionary<string, object>
            {
                ["kingdomId"] = id,
                ["name"] = id,
                ["leaderHeroId"] = id + "_ruler",
                ["leaderName"] = id + " ruler",
                ["rulingClanId"] = id + "_clan",
                ["isPlayerKingdom"] = player,
                ["strength"] = 1000d,
                ["treasury"] = 100000,
                ["leaderGold"] = 100000,
                ["fiefCount"] = 4,
                ["enemies"] = new ArrayList(enemies ?? new string[0])
            };
        }

        private static Dictionary<string, object> TestDirectorVirtues(double compassion, double boldness, double honor, double loyalty, double responsibility, double courage, double judgment)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["compassion"] = compassion,
                ["boldness"] = boldness,
                ["honor"] = honor,
                ["loyalty"] = loyalty,
                ["responsibility"] = responsibility,
                ["courage"] = courage,
                ["judgment"] = judgment
            };
        }
    }
}
