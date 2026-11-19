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
        private static Dictionary<string, object> BuildDirectorCandidate(Dictionary<string, object> decision, Dictionary<string, object> ruler)
        {
            return new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                ["command"] = ActionPipelineService.CanonicalCommand(ReadFirstString(decision, "command", "action", "type")),
                ["actorHeroStringId"] = ReadString(ruler, "leaderHeroId", ""),
                ["actorKingdomStringId"] = ReadString(ruler, "kingdomId", ""),
                ["actorClanStringId"] = ReadString(ruler, "rulingClanId", ""),
                ["targetKingdomId"] = ReadFirstString(decision, "targetKingdomId", "targetKingdomStringId"),
                ["targetKingdomStringId"] = ReadFirstString(decision, "targetKingdomId", "targetKingdomStringId"),
                ["targetSettlementId"] = ReadFirstString(decision, "targetSettlementId", "targetSettlementStringId"),
                ["targetSettlementStringId"] = ReadFirstString(decision, "targetSettlementId", "targetSettlementStringId"),
                ["terms"] = ReadDictionary(decision, "terms") ?? new Dictionary<string, object>(),
                ["publicReason"] = RequirePublicReason(decision, ReadString(ruler, "leaderName", "The ruler") + " acted in the interests of the realm."),
                ["reason"] = RequirePublicReason(decision, "A ruler-driven diplomatic decision."),
                ["decisionFactors"] = decision.ContainsKey("decisionFactors") ? decision["decisionFactors"] : new ArrayList(),
                ["confidence"] = ReadDouble(decision, "confidence", 0.5d),
                ["source"] = "world_diplomacy_director",
                ["correlationId"] = "world-diplomacy-" + Guid.NewGuid().ToString("N")
            };
        }

        private static void ApplyDirectorTargetIdentity(
            Dictionary<string, object> candidate,
            Dictionary<string, object> target)
        {
            if (candidate == null || target == null) return;
            string targetRulerId = ReadString(target, "leaderHeroId", "");
            string targetClanId = ReadString(target, "rulingClanId", "");
            if (!string.IsNullOrWhiteSpace(targetRulerId))
                candidate["targetHeroStringId"] = targetRulerId;
            if (!string.IsNullOrWhiteSpace(targetClanId))
                candidate["targetClanStringId"] = targetClanId;
        }

        internal static Dictionary<string, object> ApplyCounteroffer(Dictionary<string, object> original, Dictionary<string, object> answer)
        {
            Dictionary<string, object> next = new Dictionary<string, object>(
                StringComparer.OrdinalIgnoreCase);
            foreach (string key in new[]
            {
                "actorHeroStringId", "actorKingdomStringId", "actorClanStringId",
                "targetHeroStringId", "targetClanStringId",
                "targetKingdomId", "targetKingdomStringId", "source",
                "correlationId", "confidence", "timelineId", "requiredPolarity",
                "relationshipOpportunityId", "relationshipPolarity",
                "relationshipThreshold", "politicalPressureId",
                "politicalPressurePolarity", "politicalPressureValue",
                "politicalPressureChannel", "initiativeDiagnostics"
            })
            {
                if (original.TryGetValue(key, out object value)) next[key] = value;
            }

            next["command"] = ActionPipelineService.CanonicalCommand(ReadFirstString(answer,
                "command", "action", "type"));
            string settlementId = ReadFirstString(answer, "targetSettlementId",
                "targetSettlementStringId");
            next["targetSettlementId"] = settlementId;
            next["targetSettlementStringId"] = settlementId;
            next["terms"] = ReadDictionary(answer, "terms")
                ?? new Dictionary<string, object>();
            string reason = RequirePublicReason(answer, "The responding ruler proposed revised terms.");
            next["counterPublicReason"] = reason;
            next["publicReason"] = reason;
            next["reason"] = reason;
            next["decisionFactors"] = answer.ContainsKey("decisionFactors")
                ? answer["decisionFactors"] : new ArrayList();
            return next;
        }

        private static string InvalidCounterofferPublicReason(
            Dictionary<string, object> candidate, List<string> errors)
        {
            List<string> failures = errors ?? new List<string>();
            if (failures.Any(x => x.IndexOf("requires positive polarity",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("requires negative polarity",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The revised terms reversed the purpose of this diplomatic approach, so no agreement was reached.";
            if (failures.Any(x => x.StartsWith("unsupported command",
                    StringComparison.OrdinalIgnoreCase)))
                return "The counteroffer named no valid diplomatic action, so the negotiation ended without an agreement.";
            if (failures.Any(x => x.IndexOf("settlement",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("surrender",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The territorial counteroffer did not identify lawful holdings that could be transferred, so it was refused.";
            if (failures.Any(x => x.IndexOf("reparations",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("tribute",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("ransom",
                    StringComparison.OrdinalIgnoreCase) >= 0
                || x.IndexOf("afford",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The counteroffer's payment terms were incomplete or impossible to honor, so no agreement was reached.";
            if (failures.Any(x => x.IndexOf("durationDays",
                    StringComparison.OrdinalIgnoreCase) >= 0))
                return "The proposed treaty omitted a valid duration, so it could not be concluded.";
            string command = ActionPipelineService.CanonicalCommand(ReadString(candidate, "command", ""))
                .Replace('_', ' ');
            return "The revised " + (string.IsNullOrWhiteSpace(command)
                ? "diplomatic" : command) + " terms were not executable, so no agreement was reached.";
        }

        internal static bool IsDirectorAllianceProposal(Dictionary<string, object> candidate)
        {
            string command = ReadString(candidate, "command", "");
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            return command.Equals("sign_alliance", StringComparison.OrdinalIgnoreCase)
                || (command.Equals("diplomatic_package", StringComparison.OrdinalIgnoreCase)
                    && (ReadString(terms, "treatyKind", "").Equals("alliance_package", StringComparison.OrdinalIgnoreCase) || ReadBool(terms, "includeAlliance", false)));
        }

        private static Dictionary<string, object> BuildAllianceLeverage(Dictionary<string, object> world, Dictionary<string, object> proposer, Dictionary<string, object> responder, Dictionary<string, object> candidate)
        {
            double proposerPower = DirectorPowerScore(world, ReadString(proposer, "kingdomId", ""), ReadDouble(proposer, "strength", 1d));
            double responderPower = DirectorPowerScore(world, ReadString(responder, "kingdomId", ""), ReadDouble(responder, "strength", 1d));
            double ratio = proposerPower / Math.Max(0.01d, responderPower);
            int modifier;
            int required;
            string band;
            if (ratio >= 1.50d) { modifier = 15; required = 0; band = "dominant"; }
            else if (ratio >= 1.15d) { modifier = 8; required = 0; band = "stronger"; }
            else if (ratio >= 0.85d) { modifier = 0; required = 0; band = "peer"; }
            else if (ratio >= 0.67d) { modifier = -8; required = 10; band = "weaker"; }
            else { modifier = -15; required = 20; band = "much_weaker"; }
            double consideration = DirectorAllianceConsideration(candidate, world, proposer, responder);
            return new Dictionary<string, object>
            {
                ["proposerPower"] = proposerPower, ["responderPower"] = responderPower, ["powerRatio"] = ratio,
                ["powerBand"] = band, ["powerModifier"] = modifier, ["requiredConsideration"] = required,
                ["considerationValue"] = consideration, ["considerationRequirementMet"] = consideration + 0.0001d >= required
            };
        }

        private static double DirectorPowerScore(Dictionary<string, object> world, string kingdomId, double fallback)
        {
            Dictionary<string, object> assessment = ReadDictionaryList(world, "nationalPowerAssessments")
                .FirstOrDefault(x => string.Equals(ReadString(x, "kingdomId", ""), kingdomId, StringComparison.OrdinalIgnoreCase));
            return Math.Max(0.01d, assessment == null ? Math.Max(1d, fallback) : ReadDouble(assessment, "score", 1d));
        }

        private static double DirectorAllianceConsideration(Dictionary<string, object> candidate, Dictionary<string, object> world, Dictionary<string, object> proposer, Dictionary<string, object> responder)
        {
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            string proposerId = ReadString(proposer, "kingdomId", "");
            string responderId = ReadString(responder, "kingdomId", "");
            string candidateActorId = ReadFirstString(candidate, "actorKingdomStringId", "actorKingdomId");
            string fromHeroId = ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId");
            string toHeroId = ReadFirstString(terms, "goldToHeroStringId", "toHeroStringId");
            bool defaultDirection = string.IsNullOrWhiteSpace(fromHeroId) && string.IsNullOrWhiteSpace(toHeroId) && string.Equals(candidateActorId, proposerId, StringComparison.OrdinalIgnoreCase);
            bool explicitDirection = string.Equals(fromHeroId, ReadString(proposer, "leaderHeroId", ""), StringComparison.OrdinalIgnoreCase)
                && string.Equals(toHeroId, ReadString(responder, "leaderHeroId", ""), StringComparison.OrdinalIgnoreCase);
            double value = 0d;
            if (defaultDirection || explicitDirection)
            {
                int gold = ReadInt(terms, "gold", 0);
                double economicScale = Math.Max(10000d, ReadDouble(responder, "treasury", 0d) * 0.25d);
                value += Math.Min(15d, 15d * gold / economicScale);
                if (terms.ContainsKey("items") || !string.IsNullOrWhiteSpace(ReadString(terms, "itemId", ""))) value += 5d;
            }

            string marriageA = ReadFirstString(terms, "marriageHero1StringId", "groomHeroStringId");
            string marriageB = ReadFirstString(terms, "marriageHero2StringId", "brideHeroStringId", "spouseHeroStringId");
            if (DirectorMarriageOptionExists(world, proposerId, responderId, marriageA, marriageB)) value += 20d;

            string settlementFrom = ReadString(terms, "settlementFromKingdomId", candidateActorId);
            string settlementTo = ReadString(terms, "settlementToKingdomId", string.Equals(candidateActorId, proposerId, StringComparison.OrdinalIgnoreCase) ? responderId : "");
            if (string.Equals(settlementFrom, proposerId, StringComparison.OrdinalIgnoreCase) && string.Equals(settlementTo, responderId, StringComparison.OrdinalIgnoreCase))
            {
                double responderProsperity = Math.Max(1000d, ReadDouble(responder, "settlementProsperity", 0d));
                double offeredProsperity = ReadStringList(terms, "settlementIds").Select(id => ReadDictionaryList(world, "settlements").FirstOrDefault(x => string.Equals(ReadString(x, "settlementId", ""), id, StringComparison.OrdinalIgnoreCase))).Where(x => x != null).Sum(x => ReadDouble(x, "prosperity", 0d));
                if (offeredProsperity > 0d) value += Math.Min(25d, 10d + 15d * offeredProsperity / responderProsperity);
            }

            if (!string.IsNullOrWhiteSpace(ReadFirstString(terms, "hostageHeroStringId", "prisonerHeroStringId"))) value += 8d;
            if (ReadBool(terms, "guaranteeIndependence", false)) value += 10d;
            if (ReadBool(terms, "militaryCommitment", false) || ReadBool(terms, "warSupport", false)) value += 10d;
            return Math.Min(30d, value);
        }

        private static bool DirectorMarriageOptionExists(Dictionary<string, object> world, string firstKingdomId, string secondKingdomId, string firstHeroId, string secondHeroId)
        {
            if (string.IsNullOrWhiteSpace(firstHeroId) || string.IsNullOrWhiteSpace(secondHeroId)) return false;
            bool legacyMatch = ReadDictionaryList(world, "allianceMarriageOptions").Any(x =>
            {
                bool kingdomsMatch = (string.Equals(ReadString(x, "kingdomAId", ""), firstKingdomId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "kingdomBId", ""), secondKingdomId, StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(ReadString(x, "kingdomAId", ""), secondKingdomId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "kingdomBId", ""), firstKingdomId, StringComparison.OrdinalIgnoreCase));
                bool heroesMatch = (string.Equals(ReadString(x, "heroAId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "heroBId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase))
                    || (string.Equals(ReadString(x, "heroAId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase) && string.Equals(ReadString(x, "heroBId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase));
                return kingdomsMatch && heroesMatch;
            });
            if (legacyMatch) return true;
            Dictionary<string, object> ideal = SelectIdealDirectorMarriageOption(world, firstKingdomId, secondKingdomId);
            if (ideal == null) return false;
            return (string.Equals(ReadString(ideal, "heroAId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(ideal, "heroBId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase))
                || (string.Equals(ReadString(ideal, "heroAId", ""), secondHeroId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(ReadString(ideal, "heroBId", ""), firstHeroId, StringComparison.OrdinalIgnoreCase));
        }

        internal static bool ResolveAllianceAcceptance(string campaignId, double worldDay, Dictionary<string, object> world, Dictionary<string, object> proposer, Dictionary<string, object> responder, Dictionary<string, object> candidate, Dictionary<string, object> answer, bool counter, out Dictionary<string, object> audit)
        {
            audit = new Dictionary<string, object>();
            if (!IsDirectorAllianceProposal(candidate))
            {
                double negotiatedBaseWillingness = Clamp(0d, 100d,
                    ReadDouble(answer, "baseWillingness", 50d));
                Dictionary<string, object> attitude =
                    FindDirectorRulerAttitude(responder, proposer);
                double effectiveAttitude = ReadDouble(attitude,
                    "effectiveAttitude", 0d);
                string command = ReadString(candidate, "command", "");
                double factor = IsDirectorPeaceCommand(command) ? 0.15d
                    : DialogueService.ContainsAny(command, "exchange_prisoners", "ransom_package",
                        "loan_or_subsidy", "supply_agreement") ? 0.10d
                    : DialogueService.ContainsAny(command, "demand_", "war_indemnity",
                        "protectorate", "vassalage", "demilitarized") ? 0.08d
                    : 0.20d;
                double cap = IsDirectorPeaceCommand(command) ? 15d
                    : factor == 0.10d ? 10d : factor == 0.08d ? 8d : 20d;
                double negotiatedRelationshipModifier = Clamp(-cap, cap,
                    effectiveAttitude * factor);
                double negotiatedFinalChance = Clamp(5d, 95d,
                    negotiatedBaseWillingness + negotiatedRelationshipModifier);
                double negotiatedRoll = StableDirectorRoll(campaignId,
                    (int)Math.Floor(worldDay), "negotiated_accept|" + command + "|"
                        + ReadString(proposer, "kingdomId", "") + "|"
                        + ReadString(responder, "kingdomId", "") + "|"
                        + (counter ? "counter|" : "initial|")
                        + Json.Serialize(ReadDictionary(candidate, "terms")
                            ?? new Dictionary<string, object>()));
                audit = new Dictionary<string, object>
                {
                    ["baseWillingness"] = negotiatedBaseWillingness,
                    ["relationshipModifier"] = negotiatedRelationshipModifier,
                    ["finalChance"] = negotiatedFinalChance, ["roll"] = negotiatedRoll,
                    ["responderEffectiveAttitude"] = effectiveAttitude,
                    ["passed"] = negotiatedRoll < negotiatedFinalChance / 100d
                };
                return ReadBool(audit, "passed", false);
            }
            Dictionary<string, object> leverage = BuildAllianceLeverage(world, proposer, responder, candidate);
            double baseWillingness = Clamp(0d, 100d, ReadDouble(answer, "baseWillingness", 50d));
            Dictionary<string, object> responderAttitude =
                FindDirectorRulerAttitude(responder, proposer);
            double relationshipModifier = Clamp(-25d, 25d,
                ReadDouble(responderAttitude, "effectiveAttitude", 0d) / 4d);
            double finalChance = Clamp(5d, 95d, baseWillingness
                + ReadDouble(leverage, "powerModifier", 0d)
                + ReadDouble(leverage, "considerationValue", 0d)
                + relationshipModifier);
            double roll = StableDirectorRoll(campaignId, (int)Math.Floor(worldDay), "alliance_accept|" + ReadString(proposer, "kingdomId", "") + "|" + ReadString(responder, "kingdomId", "") + "|" + (counter ? "counter|" : "initial|") + Json.Serialize(ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>()));
            audit = new Dictionary<string, object>(leverage, StringComparer.OrdinalIgnoreCase)
            {
                ["baseWillingness"] = baseWillingness, ["finalChance"] = finalChance, ["roll"] = roll,
                ["relationshipModifier"] = relationshipModifier,
                ["responderPersonalAffinity"] = ReadInt(responderAttitude,
                    "personalAffinity", 0),
                ["proposerPublicStanding"] = ReadInt(responderAttitude,
                    "targetPublicStanding", 0),
                ["responderEffectiveAttitude"] = ReadInt(responderAttitude,
                    "effectiveAttitude", 0),
                ["publicStandingRevision"] = ReadInt(responderAttitude,
                    "publicStandingRevision", 0),
                ["passed"] = ReadBool(leverage, "considerationRequirementMet", false) && roll < finalChance / 100d
            };
            return ReadBool(audit, "passed", false);
        }

        private static void AddAllianceAcceptanceAudit(Dictionary<string, object> candidate, Dictionary<string, object> audit)
        {
            if (candidate == null || audit == null || audit.Count == 0) return;
            Dictionary<string, object> diagnostics = ReadDictionary(candidate, "initiativeDiagnostics") ?? new Dictionary<string, object>();
            candidate["initiativeDiagnostics"] = diagnostics;
            ArrayList attempts = diagnostics.TryGetValue("allianceAcceptance", out object value) && value is ArrayList list ? list : new ArrayList();
            diagnostics["allianceAcceptance"] = attempts;
            attempts.Add(audit);
        }

        private static List<string> ValidateDirectorCandidate(Dictionary<string, object> candidate, Dictionary<string, object> world, string playerKingdomId, string mode, string fixedTargetId)
        {
            List<string> errors = new List<string>();
            string command = ActionPipelineService.CanonicalCommand(ReadString(candidate, "command", ""));
            string actorId = ReadFirstString(candidate, "actorKingdomStringId", "actorKingdomId");
            string targetId = ReadFirstString(candidate, "targetKingdomStringId", "targetKingdomId");
            Dictionary<string, object> actor = FindDirectorKingdom(world, actorId);
            Dictionary<string, object> target = FindDirectorKingdom(world, targetId);
            if (!DirectorSupportedCommands.Contains(command)) errors.Add("unsupported command " + command);
            if (actor == null) errors.Add("actor kingdom does not exist");
            if (target == null) errors.Add("target kingdom does not exist");
            if (string.Equals(actorId, targetId, StringComparison.OrdinalIgnoreCase)) errors.Add("actor and target are the same kingdom");
            if (!string.IsNullOrWhiteSpace(playerKingdomId) && string.Equals(actorId, playerKingdomId, StringComparison.OrdinalIgnoreCase)) errors.Add("the diplomacy director cannot decide for the player kingdom");
            if (ReadBool(actor, "isPlayerKingdom", false)) errors.Add("the diplomacy director cannot decide for the player kingdom");
            if (ReadDouble(candidate, "confidence", 0d) < 0.35d) errors.Add("confidence is below 0.35");
            if (string.IsNullOrWhiteSpace(ReadString(candidate, "publicReason", ""))) errors.Add("public reason is required");
            bool atWar = ReadStringList(actor, "enemies").Any(x => string.Equals(x, targetId, StringComparison.OrdinalIgnoreCase));
            bool civilWarPair = string.Equals(ReadString(actor, "civilWarOpponentKingdomId", ""), targetId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ReadString(target, "civilWarOpponentKingdomId", ""), actorId, StringComparison.OrdinalIgnoreCase);
            if (civilWarPair && IsDirectorPeaceCommand(command)) errors.Add("civil-war peace requires an explicit negotiated political result");
            if (command == "declare_war" && atWar) errors.Add("kingdoms are already at war");
            if (command == "declare_war" && !atWar && actor != null && target != null && !DirectorCanDeclareAdditionalWar(world, actor, target)) errors.Add("a kingdom already at war must be stronger than all existing enemies and the proposed target combined");
            if (IsDirectorPeaceCommand(command) && !atWar) errors.Add("peace terms require an active war");
            if (IsDirectorPeacefulAgreement(command) && atWar) errors.Add("peaceful agreement cannot be signed during war");
            if (!DirectorCommandAllowedForIntent(mode, command)) errors.Add("command does not belong to the evaluated intent family");
            string requiredPolarity = ReadString(candidate, "requiredPolarity", "");
            if (!string.IsNullOrWhiteSpace(requiredPolarity))
            {
                string actualPolarity = ClassifyRulerDiplomacyPolarity(candidate);
                if (!actualPolarity.Equals(requiredPolarity,
                    StringComparison.OrdinalIgnoreCase))
                    errors.Add("relationship bonus requires " + requiredPolarity
                        + " polarity but the proposed action is " + actualPolarity);
            }
            if (!string.IsNullOrWhiteSpace(fixedTargetId) && !string.Equals(targetId, fixedTargetId, StringComparison.OrdinalIgnoreCase)) errors.Add("peace target differs from the evaluated war");
            if (ReadBool(actor, "rulerIsPrisoner", false) && !IsDirectorCaptiveCommand(command)) errors.Add("captive ruler may only negotiate peace, ransom, or prisoner exchange");
            ValidateDirectorTerms(candidate, world, actor, target, errors);
            return errors;
        }

        private static bool ShouldFallbackPoliticalPressurePeace(
            Dictionary<string, object> candidate, string mode,
            List<string> errors)
        {
            return mode.Equals("peace", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(candidate,
                    "politicalPressureId", ""))
                && ReadString(candidate, "requiredPolarity", "")
                    .Equals("positive", StringComparison.OrdinalIgnoreCase)
                && errors.Any(x => x.IndexOf("requires positive polarity",
                    StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Dictionary<string, object>
            BuildPoliticalPressurePeaceFallback(
                Dictionary<string, object> candidate,
                Dictionary<string, object> ruler,
                Dictionary<string, object> target)
        {
            Dictionary<string, object> fallback =
                new Dictionary<string, object>(candidate,
                    StringComparer.OrdinalIgnoreCase);
            string actorName = ReadString(ruler, "name", "The proposing realm");
            string targetName = ReadString(target, "name", "the opposing realm");
            fallback["command"] = "make_peace";
            fallback["targetSettlementId"] = "";
            fallback["targetSettlementStringId"] = "";
            fallback["terms"] = new Dictionary<string, object>();
            fallback["publicReason"] = actorName
                + " proposes a clean peace with " + targetName
                + " so both realms may end the war without further conditions.";
            fallback["reason"] = ReadString(fallback, "publicReason", "");
            fallback["decisionFactors"] = new ArrayList
            {
                "political_pressure:the court requires a legal conciliatory action toward the fixed wartime target",
                "fallback:the ruler's first proposal violated the binding diplomatic polarity, so one clean-peace proposal was attempted"
            };
            fallback["confidence"] = Math.Max(0.35d,
                ReadDouble(fallback, "confidence", 0.35d));
            fallback["politicalPressureFallback"] = "clean_peace";
            return fallback;
        }

        internal static void ValidateDirectorTerms(Dictionary<string, object> candidate, Dictionary<string, object> world, Dictionary<string, object> actor, Dictionary<string, object> target, List<string> errors)
        {
            Dictionary<string, object> terms = ReadDictionary(candidate, "terms") ?? new Dictionary<string, object>();
            string command = ActionPipelineService.CanonicalCommand(ReadString(candidate, "command", ""));
            string settlementId = FirstNonEmpty(ReadFirstString(candidate, "targetSettlementId", "targetSettlementStringId"), ReadFirstString(terms, "targetSettlementId", "settlementId"));
            List<string> settlementIds = ReadStringList(terms, "settlementIds")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (!string.IsNullOrWhiteSpace(settlementId)
                && !settlementIds.Contains(settlementId,
                    StringComparer.OrdinalIgnoreCase))
                settlementIds.Add(settlementId);
            if (!string.IsNullOrWhiteSpace(settlementId))
            {
                Dictionary<string, object> settlement = ReadDictionaryList(world, "settlements").FirstOrDefault(x => string.Equals(ReadString(x, "settlementId", ""), settlementId, StringComparison.OrdinalIgnoreCase));
                if (settlement == null) errors.Add("target settlement does not exist");
                else ValidateDirectorSettlementOwner(ReadString(candidate, "command", ""), settlement, actor, target, terms, errors);
            }

            foreach (string id in ReadStringList(terms, "settlementIds"))
            {
                Dictionary<string, object> settlement = ReadDictionaryList(world, "settlements").FirstOrDefault(x => string.Equals(ReadString(x, "settlementId", ""), id, StringComparison.OrdinalIgnoreCase));
                if (settlement == null) errors.Add("settlementIds contains unknown id " + id);
                else ValidateDirectorSettlementOwner(ReadString(candidate, "command", ""), settlement, actor, target, terms, errors);
            }

            string enemyId = ReadFirstString(terms, "enemyKingdomId", "thirdKingdomId");
            if (!string.IsNullOrWhiteSpace(enemyId) && FindDirectorKingdom(world, enemyId) == null) errors.Add("enemy kingdom does not exist");
            foreach (string key in new[] { "gold", "loanGold", "reparationsGold", "indemnityGold", "dailyTribute" })
            {
                int amount = ReadInt(terms, key, 0);
                if (amount < 0) errors.Add(key + " cannot be negative");
                if (amount > 5000000) errors.Add(key + " exceeds the diplomacy safety ceiling");
            }

            if (command == "demand_settlement_peace"
                && settlementIds.Count == 0)
                errors.Add("settlement peace requires at least one explicit settlement id");
            if (command == "demand_surrender_peace"
                && settlementIds.Count == 0
                && !ReadBool(terms, "allTargetFortifications", false))
                errors.Add("surrender peace requires explicit settlements or allTargetFortifications");
            if (command == "offer_tribute_peace"
                && (ReadInt(terms, "dailyTribute", 0) <= 0
                    || ReadInt(terms, "durationDays", 0) <= 0))
                errors.Add("tribute peace requires positive dailyTribute and durationDays");
            if (command == "demand_reparations_peace")
            {
                int reparations = ReadInt(terms, "reparationsGold", 0);
                int tribute = ReadInt(terms, "dailyTribute", 0);
                if (reparations <= 0 && tribute <= 0)
                    errors.Add("reparations peace requires positive reparationsGold or dailyTribute");
                if (tribute > 0 && ReadInt(terms, "durationDays", 0) <= 0)
                    errors.Add("reparations tribute requires positive durationDays");
            }
            if (command == "ransom_package" && ReadInt(terms, "gold", 0) <= 0)
                errors.Add("ransom package requires positive gold");
            if (DialogueService.ContainsAny(command, "sign_trade_agreement",
                    "sign_non_aggression_pact", "sign_alliance",
                    "sign_defensive_pact", "demilitarized_border")
                && ReadInt(terms, "durationDays", 0) <= 0)
                errors.Add(command + " requires positive durationDays");

            int outgoingGold = Math.Max(Math.Max(ReadInt(terms, "gold", 0), ReadInt(terms, "loanGold", 0)), ReadInt(terms, "reparationsGold", 0));
            bool targetPays = command == "war_indemnity" || command == "ransom_package" || command == "demand_reparations_peace" || command == "demand_surrender_peace";
            if (command == "diplomatic_package" && string.Equals(ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId"), ReadString(target, "leaderHeroId", ""), StringComparison.OrdinalIgnoreCase)) targetPays = true;
            Dictionary<string, object> payer = targetPays ? target : actor;
            // Packages debit the named ruler, not the sum of every clan's wealth.
            // Old snapshots without a payer balance must not authorize a payment.
            int availableGold = ReadInt(payer, "leaderGold", -1);
            if (outgoingGold > 0 && availableGold < 0) errors.Add("payer gold balance is unavailable");
            else if (outgoingGold > 0 && outgoingGold > availableGold) errors.Add((targetPays ? "target" : "actor") + " cannot afford proposed gold transfer");

            if (IsDirectorAllianceProposal(candidate))
            {
                if (command == "diplomatic_package" && !ReadString(terms, "treatyKind", "").Equals("alliance_package", StringComparison.OrdinalIgnoreCase)) errors.Add("alliance package must declare treatyKind alliance_package");
                if (ReadInt(terms, "gold", 0) > 0)
                {
                    string goldFrom = ReadFirstString(terms, "goldFromHeroStringId", "fromHeroStringId");
                    string goldTo = ReadFirstString(terms, "goldToHeroStringId", "toHeroStringId");
                    HashSet<string> participantLeaders = new HashSet<string>(new[] { ReadString(actor, "leaderHeroId", ""), ReadString(target, "leaderHeroId", "") }, StringComparer.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(goldFrom) || string.IsNullOrWhiteSpace(goldTo) || string.Equals(goldFrom, goldTo, StringComparison.OrdinalIgnoreCase) || !participantLeaders.Contains(goldFrom) || !participantLeaders.Contains(goldTo)) errors.Add("alliance package gold requires explicit opposing participant ruler IDs");
                }
                string marriageA = ReadFirstString(terms, "marriageHero1StringId", "groomHeroStringId");
                string marriageB = ReadFirstString(terms, "marriageHero2StringId", "brideHeroStringId", "spouseHeroStringId");
                if(marriageA!=""&&marriageB!="")
                {
                    var candidates=ReadDictionaryList(world,"allianceMarriageCandidates");
                    var first=candidates.FirstOrDefault(x=>ReadString(x,"heroId","")==marriageA);
                    var second=candidates.FirstOrDefault(x=>ReadString(x,"heroId","")==marriageB);
                    if(first==null||second==null)errors.Add("Marriage requires current native standing and a monetary quote.");
                    else
                    {
                        Dictionary<string,object> HeroRow(Dictionary<string,object> x)=>new Dictionary<string,object>{{"heroStringId",ReadString(x,"heroId","")},{"isRulingClanMember",true},{"clanFiefCount",ReadInt(x,"fiefCount",-1)}};
                        Dictionary<string,object> ClanRow(Dictionary<string,object> x)=>new Dictionary<string,object>{{"clanId",ReadString(x,"clanId","")},{"leaderId",ReadString(x,"leaderId","")},{"tier",ReadInt(x,"clanTier",0)},{"fiefCount",ReadInt(x,"fiefCount",-1)}};
                        var quote=RelationshipsService.MarriageQuote(ReadString(world,"campaignId",""),HeroRow(first),HeroRow(second),ClanRow(first),ClanRow(second),new List<Dictionary<string,object>>(),ReadInt(first,"nativeYear",-1));
                        if(quote==null)errors.Add("Royal marriage opportunity is unavailable this year.");
                        else { terms["marriageTerms"]=quote; candidate["terms"]=terms;
                            string marriagePayer=ReadString(quote,"payerId","");var pay=ReadString(first,"leaderId","")==marriagePayer?first:second;
                            if(ReadDouble(pay,"leaderGold",0)<ReadInt(quote,"fee",0))errors.Add("The lower-tier marriage clan cannot fund the required fee."); }
                    }
                }

                if ((!string.IsNullOrWhiteSpace(marriageA) || !string.IsNullOrWhiteSpace(marriageB))
                    && !DirectorMarriageOptionExists(world, ReadString(actor, "kingdomId", ""), ReadString(target, "kingdomId", ""), marriageA, marriageB)) errors.Add("marriage pair is unavailable or includes a protected heir");
            }
        }

        private static void ValidateDirectorSettlementOwner(string command, Dictionary<string, object> settlement, Dictionary<string, object> actor, Dictionary<string, object> target, Dictionary<string, object> terms, List<string> errors)
        {
            string ownerId = ReadString(settlement, "ownerKingdomId", "");
            string expectedOwnerId = command == "return_occupied_settlement" ? ReadString(actor, "kingdomId", "")
                : command == "diplomatic_package" ? ReadString(terms, "settlementFromKingdomId", ReadString(target, "kingdomId", ""))
                : ReadString(target, "kingdomId", "");
            if ((command == "demand_settlement_peace" || command == "demand_surrender_peace" || command == "diplomatic_package" || command == "return_occupied_settlement")
                && !string.Equals(ownerId, expectedOwnerId, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("settlement " + ReadString(settlement, "settlementId", "") + " is not owned by the required kingdom");
            }
        }

        internal static Dictionary<string, object> BuildDirectorNormalizationPayload(Dictionary<string, object> world, Dictionary<string, object> ruler)
        {
            List<object> heroes = ReadDictionaryList(world, "kingdoms").Select(x => (object)new Dictionary<string, object>
            {
                ["heroId"] = ReadString(x, "leaderHeroId", ""),
                ["heroStringId"] = ReadString(x, "leaderHeroId", ""),
                ["name"] = ReadString(x, "leaderName", ""),
                ["kingdomId"] = ReadString(x, "kingdomId", "")
            }).ToList();
            heroes.AddRange(ReadDictionaryList(world, "prisoners").Select(x => (object)new Dictionary<string, object>
            {
                ["heroId"] = ReadString(x, "heroId", ""),
                ["heroStringId"] = ReadString(x, "heroId", ""),
                ["name"] = ReadString(x, "name", ""),
                ["kingdomId"] = ReadString(x, "kingdomId", "")
            }));
            heroes.AddRange(ReadDictionaryList(world, "allianceMarriageOptions").SelectMany(x => new[]
            {
                (object)new Dictionary<string, object> { ["heroId"] = ReadString(x, "heroAId", ""), ["heroStringId"] = ReadString(x, "heroAId", ""), ["name"] = ReadString(x, "heroAName", ""), ["kingdomId"] = ReadString(x, "kingdomAId", "") },
                (object)new Dictionary<string, object> { ["heroId"] = ReadString(x, "heroBId", ""), ["heroStringId"] = ReadString(x, "heroBId", ""), ["name"] = ReadString(x, "heroBName", ""), ["kingdomId"] = ReadString(x, "kingdomBId", "") }
            }).GroupBy(x => ReadString((Dictionary<string, object>)x, "heroId", ""), StringComparer.OrdinalIgnoreCase).Select(x => x.First()));
            heroes.AddRange(ReadDictionaryList(world, "allianceMarriageCandidates").Select(x => (object)new Dictionary<string, object>
            {
                ["heroId"] = ReadString(x, "heroId", ""),
                ["heroStringId"] = ReadString(x, "heroId", ""),
                ["name"] = ReadString(x, "heroName", ""),
                ["kingdomId"] = ReadString(x, "kingdomId", "")
            }).GroupBy(x => ReadString((Dictionary<string, object>)x, "heroId", ""), StringComparer.OrdinalIgnoreCase).Select(x => x.First()));
            return new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(world, "campaignId", "default"),
                ["hero"] = ruler,
                ["actionResolutionIndex"] = new Dictionary<string, object>
                {
                    ["kingdoms"] = world.ContainsKey("kingdoms") ? world["kingdoms"] : new ArrayList(),
                    ["clans"] = world.ContainsKey("clans") ? world["clans"] : new ArrayList(),
                    ["settlements"] = world.ContainsKey("settlements") ? world["settlements"] : new ArrayList(),
                    ["heroes"] = heroes
                }
            };
        }

#if !REIGN_EXCLUDE_COURT
        private static string QueuePlayerDiplomacyCourtMatter(string campaignId, Dictionary<string, object> world,
            Dictionary<string, object> actor, Dictionary<string, object> target, Dictionary<string, object> candidate,
            Dictionary<string, object> normalizedAction)
        {
            string timelineId = ReadString(world, "timelineId", "main");
            double day = ReadDouble(world, "worldDay", 0d);
            string command = ReadString(candidate, "command", "diplomatic_package");
            string actorId = ReadString(actor, "kingdomId", "");
            string matterId = "court_diplomacy_" + CourtService.CourtHash(timelineId + "|" + actorId + "|" + command + "|" + Math.Floor(day)).Substring(0, 24);
            int severity = Reign.Core.Contracts.Court.ReignInternationalDocketRules.SeverityForRoll(
                CourtService.StableAmbassadorInt(matterId + "|severity", 100)) is Reign.Core.Contracts.Court.ReignNobleMatterSeverity rank ? (int)rank : 0;
            Dictionary<string, object> data = new Dictionary<string, object>
            {
                ["matterId"] = matterId, ["kind"] = "proposal", ["severityRank"] = severity,
                ["foreignKingdomId"] = actorId, ["foreignRulerId"] = ReadString(actor, "leaderHeroId", ""),
                ["playerKingdomId"] = ReadString(target, "kingdomId", ""), ["createdDay"] = day,
                ["title"] = ReadString(actor, "name", "A foreign realm") + " sends a " + command.Replace('_', ' ') + " proposal",
                ["summary"] = ReadString(candidate, "publicReason", "A foreign ruler has sent exact terms for consideration."),
                ["candidate"] = candidate, ["normalizedAction"] = normalizedAction,
                ["foreign"] = actor, ["player"] = target, ["channel"] = "trade",
                ["termsSummary"] = BuildDirectorTermsText(ReadDictionary(candidate, "terms"))
            };
            CourtService.StoreInternationalQueueItem(campaignId, timelineId, matterId, "proposal", severity, day, data);
            return matterId;
        }
#endif
    }
}
