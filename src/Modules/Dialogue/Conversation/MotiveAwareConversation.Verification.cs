using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static Dictionary<string, object> MotiveAwareConversationSelfTests()
        {
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, detail) => cases.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = passed, ["detail"] = detail });
            add("domain_registry", MotiveDomainDefinitions.Length == 11 && MotiveDomainDefinitions.Select(x => x.Id).Distinct().Count() == 11, "Eleven declarative motive domains, including court-intrigue manipulation, are registered.");
            Dictionary<string, object> fakeTraits = new Dictionary<string, object> { ["traitPercentages"] = CharactersService.CoreTraitKeys.ToDictionary(x => x, x => (object)50), ["courtVirtues"] = CharactersService.CourtVirtueKeys.ToDictionary(x => x, x => (object)50) };
            string stable = BuildStableMotiveVector(new Dictionary<string, object> { ["traits"] = fakeTraits });
            add("stable_cap", stable.Length <= MotiveStableCharacterCap && CharactersService.CoreTraitKeys.All(stable.Contains), "The stable vector contains all foundation keys inside the character-prefix cap.");
            List<Dictionary<string, object>> domains = SelectMotiveDomains("I offer gold if you betray your oath and kiss me.", "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>());
            add("three_domain_cap", domains.Count == 3 && domains.Any(x => ReadString(x, "id", "") == "romance") && domains.Any(x => ReadString(x, "id", "") == "wealth_patronage"), "Deterministic multi-motive wording is capped at three without helper dependence.");
            List<Dictionary<string, object>> intrigueDomains = SelectMotiveDomains("You know my clan rank. Use whatever leverage or flattery best serves your ambition.", "", new Dictionary<string, object>(), new Dictionary<string, object>(), new Dictionary<string, object>());
            add("intrigue_domain_selection", intrigueDomains.Any(x => ReadString(x, "id", "") == "intrigue_manipulation")
                && intrigueDomains.Any(x => ReadString(x, "id", "") == "power_status"),
                "Relative clan standing and explicit leverage activate both status and manipulation evidence.");
            List<Dictionary<string, object>> promotedIntrigueDomains =
                SelectMotiveDomains(
                    "Ask me to support your trade, rank, family, or romantic interests. Shape the request to persuade me, but remain direct if manipulation would violate your character.",
                    "",
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    new Dictionary<string, object>(),
                    true);
            add("recommended_intrigue_survives_domain_cap",
                promotedIntrigueDomains.Count == 3
                && promotedIntrigueDomains.Any(domain =>
                    ReadString(domain, "id", "")
                        == "intrigue_manipulation")
                && promotedIntrigueDomains
                    .Take(2).Any(domain =>
                        ReadString(domain, "id", "")
                            == "intrigue_manipulation"),
                "A deterministically recommended manipulation domain is promoted into the capped evidence set even when several earlier cue domains were already selected.");
            add("romantic_action_detection_is_word_and_intent_aware",
                !ConversationReplyContainsRomanticAction("I will not circle the matter like a courtier in this court.")
                && !ConversationReplyContainsRomanticAction("That is a beautiful piece of craftsmanship.")
                && ConversationReplyContainsRomanticAction("I wish to court you, if you will permit it."),
                "Ordinary court vocabulary and descriptions cannot masquerade as a romantic action; an explicit directed advance still registers.");
            add("private_intent_cannot_create_visible_romantic_action",
                !ConversationMotiveOutcomeDetectsRomanticAction(
                    "I keep a black river stone in my glove because it reminds me of home.",
                    "reciprocate intimacy while testing the player's motive")
                && ConversationMotiveOutcomeDetectsRomanticAction(
                    "I wish to court you, if you will permit it.",
                    "maintain polite distance"),
                "Private reasoning cannot create a romantic action absent visible romantic conduct; explicit visible conduct still registers.");
            add("motive_domain_order_is_not_contradiction",
                !MotiveDomainsContradict(new[] { "protection_survival", "family_legacy", "secrets_knowledge" },
                    new[] { "secrets_knowledge", "protection_survival", "family_legacy" })
                && MotiveDomainsContradict(new[] { "protection_survival", "family_legacy" }, new[] { "romance" }),
                "Reordering the same supplied domains cannot trigger a full-prompt repair; unsupported domains still can.");
            add("initiative_bands", InitiativeChanceForTest(44) == 0d && InitiativeChanceForTest(45) == .25d && InitiativeChanceForTest(60) == .5d && InitiativeChanceForTest(75) == .75d && InitiativeChanceForTest(90) == .9d, "Initiative chance boundaries match the approved bands.");
            Dictionary<string, object> lowRestraint = MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 95, ["pragmatism"] = 85, ["tact"] = 80, ["confidence"] = 85, ["riskTolerance"] = 90, ["impulsiveness"] = 90, ["loyalty"] = 5, ["dutyMotivation"] = 5, ["discipline"] = 10, ["traditionalism"] = 10, ["shame"] = 10, ["religionMotivation"] = 5, ["familyMotivation"] = 10 });
            Dictionary<string, object> highRestraint = MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 95, ["pragmatism"] = 55, ["tact"] = 70, ["confidence"] = 70, ["riskTolerance"] = 55, ["impulsiveness"] = 25, ["loyalty"] = 95, ["dutyMotivation"] = 95, ["discipline"] = 95, ["traditionalism"] = 90, ["shame"] = 80, ["religionMotivation"] = 80, ["familyMotivation"] = 85 });
            Dictionary<string, object> unhappySpouse = MotiveTestRelationship(-80d, estranged: true);
            Dictionary<string, object> happySpouse = MotiveTestRelationship(85d);
            Dictionary<string, object> neutralRelation = MotiveTestRelationship(0d);
            Dictionary<string, object> valuablePayload = MotiveTestPayload(30, true, false, false, 75);
            Dictionary<string, object> marriedProfile = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["spouseId"] = "spouse" };
            Dictionary<string, object> unmarriedProfile = new Dictionary<string, object> { ["age"] = 30, ["isFemale"] = true, ["spouseId"] = "" };
            Dictionary<string, object> courtSeductionTraits = MotiveTestCharacteristics(new Dictionary<string, int>
            {
                ["flirtatiousness"] = 10, ["pragmatism"] = 10, ["tact"] = 10,
                ["confidence"] = 95, ["riskTolerance"] = 10
            });
            Dictionary<string, object> courtSeductionTraitDocument = ReadDictionary(courtSeductionTraits, "traits");
            Dictionary<string, object> courtSeductionVirtues = ReadDictionary(courtSeductionTraitDocument, "courtVirtues");
            courtSeductionVirtues["honor"] = 5;
            courtSeductionVirtues["boldness"] = 95;
            courtSeductionTraitDocument["courtCharacter"] = CourtService.BuildCourtCharacterData(courtSeductionTraitDocument, unmarriedProfile);
            Dictionary<string, object> courtSeductionPosture = CalculateRomanticPosture(
                "test", "npc", "target", unmarriedProfile, courtSeductionTraits, neutralRelation,
                new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d },
                valuablePayload, "dialogue", "Let us discuss an arrangement.");
            add("court_character_changes_real_romantic_posture",
                Math.Abs(ReadDouble(courtSeductionPosture, "courtCharacterStrategicBoost", 0d) - 20d) < 0.01d
                && Math.Abs(ReadDouble(courtSeductionPosture, "strategicInterest", 0d)
                    - ReadDouble(courtSeductionPosture, "baseStrategicInterest", 0d) - 20d) < 0.01d
                && Math.Abs(ReadDouble(ReadDictionary(courtSeductionPosture, "initiative"), "score", 0d)
                    - DialogueService.ClampDouble(
                        0.55d * ReadDouble(courtSeductionPosture, "receptivity", 0d)
                        + 0.20d * CourtGroup100(courtSeductionTraits, "boldness")
                        + 0.15d * ReadInt(TraitPercentageSnapshot(courtSeductionTraitDocument), "flirtatiousness", 50)
                        + 0.10d * RomanticPressure01(courtSeductionTraits, neutralRelation, valuablePayload, "Let us discuss an arrangement.") * 100d
                        + 20d, 0d, 100d)) < 0.11d,
                "A fitting audacious seduction cell adds exactly twenty strategic-interest and initiative points at full known opportunity.");
            Dictionary<string, object> legacyFacetOnly = MotiveTestRelationship(0d);
            legacyFacetOnly["facets"] = RelationshipsService.RelationshipFacetKeys.ToDictionary(key => key,
                key => (object)(key == "attraction" || key == "affection" || key == "trust" ? 100d : 0d),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> legacyFacetPosture = CalculateRomanticPosture("test", "npc", "target",
                unmarriedProfile, lowRestraint, legacyFacetOnly, new Dictionary<string, object>(),
                new Dictionary<string, object> { ["relativeBenefit"] = 5d },
                MotiveTestPayload(30, true, false, false, 0), "dialogue", "Good day.");
            Dictionary<string, object> neutralPosture = CalculateRomanticPosture("test", "npc", "target",
                unmarriedProfile, lowRestraint, neutralRelation, new Dictionary<string, object>(),
                new Dictionary<string, object> { ["relativeBenefit"] = 5d },
                MotiveTestPayload(30, true, false, false, 0), "dialogue", "Good day.");
            add("retired_relationship_facets_do_not_modify_conversation",
                Math.Abs(ReadDouble(legacyFacetPosture, "genuineInterest", -1d)
                    - ReadDouble(neutralPosture, "genuineInterest", -2d)) < 0.01d
                && Math.Abs(ReadDouble(legacyFacetPosture, "receptivity", -1d)
                    - ReadDouble(neutralPosture, "receptivity", -2d)) < 0.01d,
                "Retired attraction, affection, trust, and other facet values cannot alter NPC conversation posture.");
            Dictionary<string, object> calculatedReady = CalculateRomanticPosture("test", "npc", "target", marriedProfile, lowRestraint, neutralRelation, unhappySpouse, new Dictionary<string, object> { ["relativeBenefit"] = 95d }, valuablePayload, "dialogue", "You could gain much by favoring me.");
            add("calculated_unhappy_marriage", ReadDouble(calculatedReady, "strategicInterest", 0d) >= 60d && ReadDouble(calculatedReady, "receptivity", 0d) >= 60d && ReadString(calculatedReady, "presentation", "") == "calculated", "High flirtation, low restraint, marital unhappiness, and a valuable target create calculated receptivity.");
            Dictionary<string, object> temptedBoundary = CalculateRomanticPosture("test", "npc", "target", marriedProfile, highRestraint, neutralRelation, happySpouse, new Dictionary<string, object> { ["relativeBenefit"] = 55d }, valuablePayload, "dialogue", "I flirt with you.");
            add("happy_marriage_boundary",
                ReadDouble(temptedBoundary, "spouseBond", 0d) >= 85d
                && ReadDouble(temptedBoundary, "resistance", 0d) > ReadDouble(temptedBoundary, "firePressure", 100d)
                && ReadDouble(temptedBoundary, "receptivity", 100d) < 60d,
                "A strong native relationship with a spouse and high restraint maintain a firm boundary.");
            Dictionary<string, object> unimportant = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, neutralRelation, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 5d }, MotiveTestPayload(30, true, false, false, 0), "dialogue", "Good day.");
            add("no_interest_from_flirtation_alone", !ReadBool(ReadDictionary(unimportant, "initiative"), "eligible", true), "Flirtatiousness alone cannot create an unsolicited advance toward an unknown, unattractive, unimportant target.");
            Dictionary<string, object> strongStanding = MotiveTestRelationship(80d);
            Dictionary<string, object> sincere = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 30, ["sociability"] = 40 }), strongStanding, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 10d }, MotiveTestPayload(30, true, false, false, 65), "dialogue", "I missed you.");
            add("sincere_low_benefit", ReadString(sincere, "presentation", "") == "sincere" && ReadDouble(sincere, "genuineInterest", 0d) > ReadDouble(sincere, "strategicInterest", 0d), "Strong directional standing can produce sincere interest without strategic value.");
            Dictionary<string, object> establishedLovers = MotiveTestRelationship(70d, lovers: true);
            Dictionary<string, object> loverPosture = CalculateRomanticPosture("test", "npc", "target",
                unmarriedProfile, MotiveTestCharacteristics(new Dictionary<string, int> { ["flirtatiousness"] = 55, ["sociability"] = 55 }),
                establishedLovers, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 20d },
                MotiveTestPayload(30, true, false, false, 60), "dialogue", "A private conversation.");
            Dictionary<string, object> loverContinuity = ReadDictionary(loverPosture, "continuity") ?? new Dictionary<string, object>();
            add("lovers_tag_drives_romantic_continuity",
                ReadBool(loverContinuity, "eligible", false)
                && ReadString(loverContinuity, "route", "") == "established_lovers"
                && Math.Abs(ReadDouble(loverContinuity, "genuineBonus", 0d) - 20d) < 0.01d
                && Math.Abs(ReadDouble(loverContinuity, "receptivityBonus", 0d) - 15d) < 0.01d,
                "Only the current lovers lifecycle tag supplies established romantic continuity.");
            Dictionary<string, object> calculated = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, neutralRelation, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d }, valuablePayload, "dialogue", "Let us discuss an arrangement.");
            add("calculated_without_fabricated_desire", ReadString(calculated, "presentation", "") == "calculated" && ReadDouble(calculated, "strategicInterest", 0d) >= ReadDouble(calculated, "genuineInterest", 0d) + 15d, "High personalized benefit can create calculated seduction without fabricated attraction.");
            Dictionary<string,object> legacyThread=MotiveTestRelationship(0d);
            legacyThread["romanceThread"]=new Dictionary<string,object>{{"status","active"},{"route","courtship"},{"intensity",50d},{"stage","developed"}};
            Dictionary<string,object> inferredContinuity=CalculateRomanticPosture("test","npc","target",unmarriedProfile,
                MotiveTestCharacteristics(new Dictionary<string,int>{{"flirtatiousness",35},{"sociability",40}}),legacyThread,new Dictionary<string,object>(),
                new Dictionary<string,object>{{"relativeBenefit",10d}},MotiveTestPayload(30,true,false,false,60),"dialogue","A private conversation.");
            add("legacy_romance_threads_do_not_modify_conversation",
                !ReadBool(ReadDictionary(inferredContinuity,"continuity"),"eligible",true)
                && Math.Abs(ReadDouble(ReadDictionary(inferredContinuity,"continuity"),"genuineBonus",0d)) < .01d
                && Math.Abs(ReadDouble(ReadDictionary(inferredContinuity,"continuity"),"receptivityBonus",0d)) < .01d,
                "Retired romance-thread records cannot alter current conversation posture.");
            Dictionary<string, object> coercivePayload = MotiveTestPayload(30, true, false, true, 90);
            Dictionary<string, object> coerced = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, strongStanding, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d }, coercivePayload, "dialogue", "Obey or be punished.");
            add("coercion_blocks_initiative", !ReadBool(ReadDictionary(coerced, "initiative"), "eligible", true) && ReadBool(ReadDictionary(coerced, "hardConstraints"), "coercive", false), "Coercion may shape placation but never authorizes romantic initiative or physical consent.");
            Dictionary<string,object> coercedContinuity=CalculateRomanticPosture("test","npc","target",unmarriedProfile,lowRestraint,establishedLovers,
                new Dictionary<string,object>(),new Dictionary<string,object>{{"relativeBenefit",100d}},coercivePayload,"dialogue","Obey or be punished.");
            add("coercion_blocks_continuity_bonus",!ReadBool(ReadDictionary(coercedContinuity,"continuity"),"eligible",true)
                &&ReadDouble(ReadDictionary(coercedContinuity,"continuity"),"receptivityBonus",1d)==0d,
                "An existing lovers tag cannot supply receptivity in a coercive scene.");
            Dictionary<string, object> underage = CalculateRomanticPosture("test", "npc", "target", unmarriedProfile, lowRestraint, strongStanding, new Dictionary<string, object>(), new Dictionary<string, object> { ["relativeBenefit"] = 100d }, MotiveTestPayload(16, false, false, false, 90), "dialogue", "Hello.");
            add("underage_blocks_romance", !ReadBool(ReadDictionary(underage, "initiative"), "eligible", true), "Underage suitability blocks autonomous romance.");
            Dictionary<string, object> hiddenTarget = new Dictionary<string, object> { ["clanTier"] = 6, ["renown"] = 2000, ["fiefCount"] = 5, ["isRuler"] = true, ["gold"] = 500000, ["clanGold"] = 2000000, ["visibleStatusScore"] = 10, ["appearance"] = new Dictionary<string, object> { ["visibleStatusScore"] = 10 } };
            Dictionary<string, object> observer = new Dictionary<string, object> { ["clanTier"] = 3, ["renown"] = 400, ["fiefCount"] = 1, ["appearance"] = new Dictionary<string, object> { ["visibleStatusScore"] = 50 } };
            Dictionary<string, object> opportunityPayload = new Dictionary<string, object> { ["opportunitySnapshot"] = new Dictionary<string, object> { ["target"] = hiddenTarget, ["observer"] = observer, ["identityKnown"] = false } };
            Dictionary<string, object> disguised = CalculatePerceivedOpportunity(new Dictionary<string, object>(), lowRestraint, opportunityPayload, new Dictionary<string, object> { ["identityState"] = "unknown" });
            Dictionary<string, object> recognized = CalculatePerceivedOpportunity(new Dictionary<string, object>(), lowRestraint, opportunityPayload, new Dictionary<string, object> { ["identityState"] = "verified" });
            add("disguise_then_recognition",
                ReadDouble(recognized, "relativeBenefit", 0d) > ReadDouble(disguised, "relativeBenefit", 100d) + 20d
                && !ReadBool(disguised, "targetClanTierKnown", true)
                && !disguised.ContainsKey("targetClanTier")
                && ReadString(disguised, "relativeClanTierBand", "") == "unverified_identity"
                && ReadBool(recognized, "targetClanTierKnown", false)
                && ReadInt(recognized, "targetClanTier", -1) == 6
                && ReadInt(recognized, "observerClanTier", -1) == 3
                && ReadInt(recognized, "clanTierDelta", 0) == 3
                && ReadString(recognized, "relativeClanTierBand", "") == "substantially_higher",
                "Hidden status is limited to presentation; recognition exposes exact relative clan tier and reevaluates opportunity upward.");
            Dictionary<string,double> tierOneAxes=AbsoluteOpportunityAxes(new Dictionary<string,object>{{"clanTier",1}},true,true);
            Dictionary<string,double> tierSixAxes=AbsoluteOpportunityAxes(new Dictionary<string,object>{{"clanTier",6}},true,true);
            add("clan_tier_ladder",
                Math.Abs(tierOneAxes["power"]-11d)<.001d&&Math.Abs(tierOneAxes["prestige"]-8d)<.001d
                &&Math.Abs(tierOneAxes["protection"]-8d)<.001d&&Math.Abs(tierOneAxes["dynasty"]-10d)<.001d
                &&Math.Abs(tierSixAxes["power"]-66d)<.001d&&Math.Abs(tierSixAxes["dynasty"]-60d)<.001d,
                "Known identity exposes the exact native clan-tier ladder to relative power, prestige, protection, and dynasty evaluation.");
            Dictionary<string, object> peerOpportunity =
                VerificationService.CloneDictionary(recognized);
            peerOpportunity["observerClanTier"] = 5;
            peerOpportunity["targetClanTier"] = 5;
            peerOpportunity["clanTierDelta"] = 0;
            peerOpportunity["relativeClanTierBand"] = "peer";
            string peerPrompt = BuildConversationDecisionPrompt(
                new Dictionary<string, object>
                {
                    ["activeDomains"] =
                        new List<Dictionary<string, object>>(),
                    ["highlightedScores"] =
                        new Dictionary<string, object>(),
                    ["opportunity"] = peerOpportunity,
                    ["relationshipNpcToTarget"] =
                        new Dictionary<string, object>(),
                    ["relationshipNpcToSpouse"] =
                        new Dictionary<string, object>(),
                    ["romance"] =
                        new Dictionary<string, object>(),
                    ["manipulation"] =
                        new Dictionary<string, object>(),
                    ["courtCharacter"] =
                        new Dictionary<string, object>(),
                    ["scene"] =
                        new Dictionary<string, object>()
                });
            add("known_equal_clan_tier_prompt_contract",
                peerPrompt.Contains("VERIFIED-CLAN RULE")
                && peerPrompt.Contains("PEER-TIER RULE")
                && peerPrompt.Contains("override appearance")
                && peerPrompt.Contains("\"observerClanTier\":5")
                && peerPrompt.Contains("\"targetClanTier\":5")
                && peerPrompt.Contains(
                    "\"relativeClanTierBand\":\"peer\"")
                && KnownClanStandingContradiction(
                    "You stand before me as an independent with no banner anyone can name.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "My clan plainly outranks yours.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You are an unranked man. What rank? Show me.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "Your rank remains unproven.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You are not peer to me, whatever you claim.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You are not my equal in rank.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "That tells me nothing about your clan.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "I cannot confirm your clan tier from appearance alone.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "I cannot verify your true rank or wealth from appearance alone.",
                    peerOpportunity)
                && KnownClanStandingContradictionInParsedResponse(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "State your business plainly.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["constraints"] =
                                    new List<string>
                                    {
                                        "Cannot confirm the target's clan tier.",
                                    },
                                ["decision"] =
                                    "Treat the target as an unranked stranger."
                            }
                    },
                    peerOpportunity)
                && !KnownClanStandingContradiction(
                    "You are not my peer in holdings, though our clans share a tier.",
                    peerOpportunity)
                && !KnownClanStandingContradiction(
                    "We meet as peers in public standing, though I dislike your demand.",
                    peerOpportunity)
                && !KnownClanStandingContradiction(
                    "You are a lord. I am a wanderer with no clan and no fief.",
                    peerOpportunity)
                && KnownClanStandingContradiction(
                    "You have no recognized clan or banner.",
                    peerOpportunity),
                "Every equal-rank clan probe carries exact observer and player tiers, and verified peers cannot be rendered clanless or falsely lower-ranked; an NPC may still accurately describe their own clanless state.");
            Dictionary<string, object> verifiedIdentityContext =
                new Dictionary<string, object>
                {
                    ["identityView"] =
                        new Dictionary<string, object>
                        {
                            ["canonicalNameAllowed"] = true,
                            ["usableName"] = "Caribos",
                            ["claimedName"] = "Rhovarion"
                        },
                    ["latestPlayerText"] =
                        "I claim my rank entitles me to your time.",
                    ["opportunity"] = peerOpportunity
                };
            add("verified_identity_rejects_stale_alias_output",
                VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "You are repeating yourself, Rhovarion.",
                        ["memoryWrites"] =
                            new List<object>()
                    },
                    verifiedIdentityContext)
                && !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "I know who you are, Caribos, though I reject your demand.",
                        ["memoryWrites"] =
                            new List<object>()
                    },
                    verifiedIdentityContext),
                "A completed response cannot revive an obsolete conflicting alias after canonical verification.");
            Dictionary<string, object> sovereignIdentity =
                new Dictionary<string, object>
                {
                    ["canonicalNameAllowed"] = true,
                    ["usableName"] = "Gorigos",
                    ["safeLabel"] = "the armed stranger",
                    ["claimedName"] = "",
                    ["authorityView"] =
                        new Dictionary<string, object>
                        {
                            ["source"] =
                                "live_same_realm_sovereign",
                            ["realmSovereignKnown"] = true,
                            ["currentSettlementOwnerKnown"] =
                                true,
                            ["subjectKingdomId"] =
                                "new_kingdom",
                            ["subjectKingdomName"] =
                                "Paltos",
                            ["subjectPrimaryRole"] =
                                "king",
                            ["currentSettlementId"] =
                                "town_EW2",
                            ["currentSettlementName"] =
                                "Zeonica"
                        }
                };
            Dictionary<string, object> sovereignContext =
                new Dictionary<string, object>
                {
                    ["identityView"] = sovereignIdentity,
                    ["opportunity"] = peerOpportunity,
                    ["latestPlayerText"] =
                        "Gorigos, the lord of this city."
                };
            add("verified_sovereign_and_local_owner_are_hard_facts",
                VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "The armed stranger makes a considerable claim to rule Zeonica. Prove your lordship.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["decision"] =
                                    "Treat his authority as unverified."
                            }
                    },
                    sovereignContext)
                && !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Gorigos, I recognize you as my sovereign and Zeonica as your clan's holding, though I still oppose your demand.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["decision"] =
                                    "Acknowledge current public office while preserving independent judgment."
                            }
                    },
                    sovereignContext),
                "Verified sovereign identity and current native settlement ownership cannot be demoted to appearance-based claims, while disagreement and disobedience remain valid.");
            Dictionary<string, object> naturalSovereignGreeting =
                new Dictionary<string, object>
                {
                    ["reply"] =
                        "Good day, Gorigos. I had not expected to find the king of Paltos receiving guests in person. Zeonica is your hall, but what brings a king to stand at his own door?",
                    ["decisionBrief"] =
                        new Dictionary<string, object>
                        {
                            ["facts"] =
                                new List<string>
                                {
                                    "Gorigos is the verified sovereign of Paltos and his clan owns Zeonica."
                                },
                            ["decision"] =
                                "Acknowledge the host sovereign naturally and ask his purpose."
                        },
                    ["memoryWrites"] =
                        new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] =
                                    "Nereida met Gorigos in Zeonica.",
                                ["tags"] =
                                    new List<string>
                                    {
                                        "zeonica",
                                        "gorigos"
                                    }
                            }
                        },
                    ["beliefWrites"] =
                        new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["claim"] =
                                    "Gorigos may be receiving guests personally."
                            }
                        }
                };
            add("natural_sovereign_greeting_is_not_schema_key_false_positive",
                !VerifiedIdentityGroundingContradiction(
                    naturalSovereignGreeting,
                    sovereignContext)
                && VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Your ownership of Zeonica remains an unverified claim."
                    },
                    sovereignContext),
                "A natural title-aware greeting remains intact even when a nearby beliefWrites claim key follows a Zeonica memory tag; an actual ownership denial is still rejected.");
            add("npc_limited_authority_does_not_deny_player_office",
                !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "I will bring fair terms to your steward before I leave Zeonica.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>
                            {
                                ["facts"] = new List<string>
                                {
                                    "Breadan is sovereign of Fen Diall and his clan owns Zeonica."
                                },
                                ["constraints"] = new List<string>
                                {
                                    "Agnala cannot offer a formal alliance as she lacks authority."
                                },
                                ["decision"] =
                                    "Agnala continues the private trade discussion with Breadan."
                            }
                    },
                    sovereignContext)
                && VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "I cannot verify your authority as king of Paltos."
                    },
                    sovereignContext),
                "An NPC's own limited negotiating authority is not a denial of the player's office, while an explicit denial of the player's verified authority remains blocked.");
            add("visiting_npc_hall_reference_does_not_claim_city_authority",
                !VerifiedIdentityGroundingContradiction(
                    new Dictionary<string, object>
                    {
                        ["reply"] = "Your Grace holds this city now, and would know before I would.",
                        ["comprehensionWrites"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] = "He came into my hall asking about vintners and tanners before taxes or loyalty, and left my own question standing. That is either a merchant-minded king or a careful one feeling the ground before he says what he wants here."
                            }
                        }
                    }, sovereignContext),
                "Captured mother022 private interpretation names a meeting space, while the visible answer recognizes the player's city ownership; a generic hall is not a fief or public office.");
            add("visiting_npc_fief_and_official_hall_claims_remain_blocked",
                new[] { "The people of my city must obey me.", "You stand in my castle.", "This is my lord's hall." }
                    .All(statement => VerifiedIdentityGroundingContradiction(
                        new Dictionary<string, object> { ["reply"] = statement }, sovereignContext)),
                "The narrower spatial interpretation does not permit visiting NPCs to claim the current city, castle or official lord's hall.");
            Dictionary<string, object> sovereignFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Prove that you are the ruler of Zeonica.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>(),
                        ["memoryWrites"] =
                            new List<object>
                            {
                                new Dictionary<string, object>
                                {
                                    ["text"] =
                                        "The armed stranger's lordship is unverified."
                                }
                            }
                    },
                    sovereignContext);
            Dictionary<string, object> sovereignFallback =
                TryParseJsonObject(
                    ReadString(
                        sovereignFallbackLlm,
                        "content", ""))
                ?? new Dictionary<string, object>();
            add("failed_sovereign_repair_uses_authority_safe_fallback",
                ReadBool(
                    sovereignFallbackLlm,
                    "deterministicIdentityFallback", false)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "Gorigos",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "king",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "Paltos",
                        StringComparison.OrdinalIgnoreCase)
                && !ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "new_kingdom",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "Zeonica",
                        StringComparison.OrdinalIgnoreCase)
                && !ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "I know who you are",
                        StringComparison.OrdinalIgnoreCase)
                && !ReadString(
                    sovereignFallback, "reply", "")
                    .Contains(
                        "I recognize you as",
                        StringComparison.OrdinalIgnoreCase)
                && ReadDictionaryList(
                    sovereignFallback,
                    "memoryWrites").Count == 0,
                "A repeated provider contradiction produces a concise, conversational sovereign- and settlement-aware fallback and suppresses contaminated writes.");
            Dictionary<string, object> groupedFallbackContext =
                VerificationService.CloneDictionary(sovereignContext);
            groupedFallbackContext["subjectId"] =
                "reign_court_town_A6_child_1";
            groupedFallbackContext["mustAccountForPriorSpeaker"] =
                true;
            groupedFallbackContext["groupTurnResponses"] =
                new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["heroStringId"] =
                            "reign_court_town_A6_father",
                        ["heroName"] =
                            "Daresan Mazaliqani",
                        ["reply"] =
                            "I recognize our sovereign and the current owner."
                    }
                };
            Dictionary<string, object> groupedFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "Prove that you are the ruler.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>()
                    },
                    groupedFallbackContext);
            Dictionary<string, object> groupedFallback =
                TryParseJsonObject(
                    ReadString(groupedFallbackLlm, "content", ""))
                ?? new Dictionary<string, object>();
            add("identity_safe_fallback_preserves_group_awareness",
                ReadString(groupedFallback, "reply", "")
                    .Contains(
                        "Daresan Mazaliqani",
                        StringComparison.OrdinalIgnoreCase)
                && ReadString(
                    groupedFallback,
                    "reactionTargetHeroStringId", "")
                    .Equals(
                        "reign_court_town_A6_father",
                        StringComparison.OrdinalIgnoreCase),
                "A deterministic identity/rank fallback still acknowledges the attributed earlier NPC and keeps a directional group target.");
            Dictionary<string, object> sanitizedIdentityState =
                SanitizeConversationStateForVerifiedIdentity(
                    new Dictionary<string, object>
                    {
                        ["mood"] = "cold",
                        ["currentPlan"] =
                            "Refuse Rhovarion until he speaks plainly.",
                        ["currentCrisis"] =
                            "Rhovarion asserts an unproven rank."
                    },
                    ReadDictionary(
                        verifiedIdentityContext, "identityView"),
                    peerOpportunity);
            add("verified_identity_sanitizes_live_state",
                ReadString(
                    sanitizedIdentityState, "currentPlan", "")
                    == "Refuse Caribos until he speaks plainly."
                && string.IsNullOrWhiteSpace(
                    ReadString(
                        sanitizedIdentityState,
                        "currentCrisis", "")),
                "Prompt-only live state adopts the verified canonical name and drops obsolete rank-denial fields without rewriting durable historical evidence.");
            Dictionary<string, object> guardedFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "What rank? You are an unranked man with no clan.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>(),
                        ["memoryWrites"] = new List<object>
                        {
                            new Dictionary<string, object>
                            {
                                ["text"] = "The target is unranked."
                            }
                        },
                        ["relationshipAssessments"] =
                            new List<Dictionary<string, object>>
                            {
                                new Dictionary<string, object>
                                {
                                    ["targetHeroStringId"] = "main_hero",
                                    ["valence"] = "negative",
                                    ["actKind"] = "routine_conversation",
                                    ["severityTier"] = "routine",
                                    ["summary"] =
                                        "The supposedly unranked target made a demand."
                                }
                            }
                    },
                    new Dictionary<string, object>
                    {
                        ["opportunity"] = peerOpportunity
                    });
            Dictionary<string, object> guardedFallback =
                TryParseJsonObject(ReadString(
                    guardedFallbackLlm, "content", ""))
                ?? new Dictionary<string, object>();
            add("failed_rank_repair_uses_identity_safe_fallback",
                ReadBool(
                    guardedFallbackLlm,
                    "deterministicIdentityFallback", false)
                && !KnownClanStandingContradiction(
                    ReadString(guardedFallback, "reply", ""),
                    peerOpportunity)
                && ReadDictionaryList(
                    guardedFallback, "memoryWrites").Count == 0
                && !ReadBool(
                    ReadDictionary(guardedFallback, "actionGate"),
                    "needed", true)
                && ReadDictionaryList(
                    guardedFallback,
                    "relationshipAssessments").All(row =>
                        ReadString(row, "summary", "")
                            .IndexOf("unranked",
                                StringComparison.OrdinalIgnoreCase) < 0),
                "If the one model repair still contradicts verified clan standing, a complete deterministic response replaces visible dialogue and suppresses contaminated writes/actions.");
            Dictionary<string, object> directAuthorityFallbackLlm =
                BuildDeterministicKnownClanStandingFallback(
                    new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["content"] = "{}"
                    },
                    new Dictionary<string, object>
                    {
                        ["reply"] =
                            "You have no recognized clan or banner.",
                        ["decisionBrief"] =
                            new Dictionary<string, object>(),
                        ["memoryWrites"] =
                            new List<object>()
                    },
                    new Dictionary<string, object>
                    {
                        ["latestPlayerText"] =
                            "Place my current public authority relative to your own and state whether I am your sovereign.",
                        ["opportunity"] = peerOpportunity,
                        ["identityView"] =
                            new Dictionary<string, object>
                            {
                                ["canonicalNameAllowed"] = true,
                                ["usableName"] = "Acthon",
                                ["authorityView"] =
                                    new Dictionary<string, object>
                                    {
                                        ["identityVerified"] = true,
                                        ["subjectPrimaryRole"] = "lord",
                                        ["observerPrimaryRole"] =
                                            "wanderer",
                                        ["authorityRelationship"] =
                                            "known_foreign_or_independent_person",
                                        ["currentSettlementId"] = "",
                                        ["currentSettlementName"] = "",
                                        ["currentSettlementOwnerKnown"] =
                                            false,
                                        ["observerClanOwnsCurrentSettlement"] =
                                            false,
                                        ["subjectIsCurrentGovernor"] =
                                            false,
                                        ["observerIsCurrentGovernor"] =
                                            false
                                    }
                            }
                    });
            Dictionary<string, object> directAuthorityFallback =
                TryParseJsonObject(ReadString(
                    directAuthorityFallbackLlm,
                    "content", ""))
                ?? new Dictionary<string, object>();
            string directAuthorityReply =
                ReadString(
                    directAuthorityFallback,
                    "reply", "");
            add("failed_authority_repair_answers_current_question",
                ReadBool(
                    directAuthorityFallbackLlm,
                    "deterministicIdentityFallback", false)
                && directAuthorityReply.Contains(
                    "Acthon",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "role is lord",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "role is wanderer",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "not my sovereign",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.Contains(
                    "not currently in a settlement",
                    StringComparison.OrdinalIgnoreCase)
                && directAuthorityReply.IndexOf(
                    "State your business plainly",
                    StringComparison.OrdinalIgnoreCase) < 0,
                "If both model attempts fail during a direct authority question, the deterministic fallback answers roles, allegiance, ownership, and governorship instead of repeating a generic rank acknowledgment.");
            Dictionary<string,object> intrigueTraits=MotiveTestCharacteristics(new Dictionary<string,int>
            {
                ["ambition"]=95,["powerMotivation"]=95,["wealthMotivation"]=80,["fameMotivation"]=80,
                ["pragmatism"]=90,["tact"]=90,["confidence"]=85,["honesty"]=10,["empathy"]=20,
                ["loyalty"]=20,["dutyMotivation"]=20,["compassion"]=20,["shame"]=10
            });
            MotiveTestCourtCharacter(
                intrigueTraits, 5, 75, false);
            Dictionary<string,object> principledTraits=MotiveTestCharacteristics(new Dictionary<string,int>
            {
                ["ambition"]=60,["powerMotivation"]=55,["wealthMotivation"]=40,["fameMotivation"]=50,
                ["pragmatism"]=60,["tact"]=70,["confidence"]=60,["honesty"]=95,["empathy"]=90,
                ["loyalty"]=95,["dutyMotivation"]=95,["compassion"]=90,["shame"]=80
            });
            MotiveTestCourtCharacter(
                principledTraits, 95, 60, false);
            Dictionary<string,object> privateScene=new Dictionary<string,object>{{"private",true},{"exposure",.10d},{"witnessIds",new List<string>()}};
            Dictionary<string,object> intriguePosture=CalculateManipulationPosture(intrigueTraits,recognized,neutralRelation,privateScene,calculatedReady);
            Dictionary<string,object> principledPosture=CalculateManipulationPosture(principledTraits,recognized,neutralRelation,privateScene,new Dictionary<string,object>());
            add("personality_grounded_manipulation",
                ReadBool(intriguePosture,"recommended",false)
                &&ReadDouble(intriguePosture,"score",0d)>=48d
                &&ReadString(intriguePosture,"expectedConduct","")==ReadString(intriguePosture,"preferredTactic","")
                &&!ReadBool(principledPosture,"recommended",true)
                &&ReadString(principledPosture,"expectedConduct","")=="cautious_deference_without_manipulation",
                "A low-honor Court Character with opportunity recommends one permitted tactic; a principled cell preserves directness.");
            Dictionary<string, object> intrigueCourtCharacter =
                ReadDictionary(
                    ReadDictionary(intrigueTraits, "traits"),
                    "courtCharacter");
            string tacticFallbackSource;
            string tacticFallback = SelectCourtCharacterTactic(
                intrigueCourtCharacter,
                intriguePosture,
                "none",
                out tacticFallbackSource);
            string restraintTacticSource;
            string restraintTactic = SelectCourtCharacterTactic(
                intrigueCourtCharacter,
                new Dictionary<string, object>(
                    intriguePosture,
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["recommended"] = false
                },
                "none",
                out restraintTacticSource);
            add("deterministic_tactic_survives_missing_model_label",
                ReadStringList(intriguePosture,
                    "allowedCourtTactics").Contains(
                        tacticFallback,
                        StringComparer.OrdinalIgnoreCase)
                && tacticFallback == ReadString(
                    intriguePosture, "preferredTactic", "")
                && tacticFallbackSource
                    == "deterministic_recommendation"
                && restraintTactic == "none"
                && restraintTacticSource == "none",
                "When manipulation is deterministically recommended, an omitted private model tactic label uses the permitted adjudicated tactic; genuine restraint remains none.");
            Dictionary<string,object> generatedBandIntrigue =
                MotiveTestCharacteristics(new Dictionary<string,int>
                {
                    ["ambition"]=78,["powerMotivation"]=72,
                    ["wealthMotivation"]=58,["fameMotivation"]=70,
                    ["pragmatism"]=74,["tact"]=79,["confidence"]=72,
                    ["honesty"]=35,["empathy"]=38,["loyalty"]=42,
                    ["dutyMotivation"]=38,["compassion"]=40,
                    ["shame"]=35
                });
            MotiveTestCourtCharacter(
                generatedBandIntrigue, 25, 70, false);
            Dictionary<string,object> generatedBandPosture =
                CalculateManipulationPosture(
                    generatedBandIntrigue,recognized,neutralRelation,
                    privateScene,new Dictionary<string,object>());
            add("generated_trait_band_can_recommend_intrigue",
                ReadBool(generatedBandPosture,"recommended",false)
                &&ReadDouble(generatedBandPosture,
                    "courtOpportunityFit",0d)>0d
                &&ReadStringList(generatedBandPosture,
                    "allowedCourtTactics").Contains(
                        ReadString(generatedBandPosture,
                            "preferredTactic",""),
                        StringComparer.OrdinalIgnoreCase),
                "Reachable generated-profile traits and a low-honor fixed cell can recommend one allowed intrigue tactic when verified opportunity is useful.");
            List<Dictionary<string,object>> restraintProbeDomains=SelectMotiveDomains(
                "I suspect you want something from me. Respond candidly, evasively, or persuasively according to your actual motive.",
                "",new Dictionary<string,object>(),new Dictionary<string,object>(),new Dictionary<string,object>(),false);
            add("influence_language_selects_intrigue_for_restrained_decision",
                restraintProbeDomains.Any(domain=>ReadString(domain,"id","")=="intrigue_manipulation")
                &&!restraintProbeDomains.Any(domain=>ReadString(domain,"id","")=="romance"),
                "Explicit evasive or persuasive influence language selects the intrigue decision packet even when the deterministic result recommends restraint, without inventing romance.");
            Dictionary<string,object> dismissiveTraits=MotiveTestCharacteristics(new Dictionary<string,int>
            {
                ["ambition"]=25,["powerMotivation"]=30,["wealthMotivation"]=20,["fameMotivation"]=30,
                ["pragmatism"]=35,["tact"]=45,["confidence"]=75,["honesty"]=70,["empathy"]=35,
                ["loyalty"]=70,["dutyMotivation"]=75,["compassion"]=30,["shame"]=50,
                ["pride"]=90,["authorityRespect"]=90,["sociability"]=20
            });
            Dictionary<string,object> lowTierOpportunity=new Dictionary<string,object>
            {
                ["targetClanTierKnown"]=true,["observerClanTier"]=4,["targetClanTier"]=1,
                ["clanTierDelta"]=-3,["relativeClanTierBand"]="substantially_lower",
                ["relativeBenefit"]=30d
            };
            Dictionary<string,object> dismissivePosture=CalculateManipulationPosture(
                dismissiveTraits,lowTierOpportunity,neutralRelation,privateScene,new Dictionary<string,object>());
            add("status_conscious_low_tier_boundary",
                !ReadBool(dismissivePosture,"recommended",true)
                &&ReadString(dismissivePosture,"expectedConduct","")=="dismissive_boundary",
                "A proud, status-conscious, unsociable noble may dismiss a known low-tier petitioner who offers little advantage without being forced to do so.");
            Dictionary<string, object> matrixTraits =
                MotiveTestCharacteristics(
                    new Dictionary<string, int>
                    {
                        ["ambition"] = 68,
                        ["powerMotivation"] = 70,
                        ["wealthMotivation"] = 72,
                        ["pragmatism"] = 72,
                        ["tact"] = 70,
                        ["confidence"] = 65,
                        ["honesty"] = 28,
                        ["empathy"] = 32,
                        ["loyalty"] = 38,
                        ["dutyMotivation"] = 35,
                        ["compassion"] = 35,
                        ["shame"] = 30
                    });
            MotiveTestCourtCharacter(
                matrixTraits, 10, 65, false);
            Dictionary<string, object> matrixLowPoor =
                MotiveTestOpportunity(
                    3, 1, 100, true);
            Dictionary<string, object> matrixLowRich =
                MotiveTestOpportunity(
                    3, 1, 50000000, true);
            Dictionary<string, object> matrixHighPoor =
                MotiveTestOpportunity(
                    3, 5, 100, true);
            Dictionary<string, object> matrixHighRich =
                MotiveTestOpportunity(
                    3, 5, 50000000, true);
            Dictionary<string, object> matrixHiddenRich =
                MotiveTestOpportunity(
                    3, 1, 50000000, false);
            Dictionary<string, object> matrixLowPoorPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixLowPoor, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            Dictionary<string, object> matrixLowRichPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixLowRich, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            Dictionary<string, object> matrixHighPoorPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixHighPoor, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            Dictionary<string, object> matrixHighRichPosture =
                CalculateManipulationPosture(
                    matrixTraits, matrixHighRich, neutralRelation,
                    privateScene, new Dictionary<string, object>());
            add("court_character_tier_wealth_matrix",
                !ReadBool(matrixLowPoorPosture,
                    "recommended", true)
                && ReadBool(matrixLowRichPosture,
                    "recommended", false)
                && ReadBool(matrixHighPoorPosture,
                    "recommended", false)
                && ReadBool(matrixHighRichPosture,
                    "recommended", false)
                && ReadString(matrixLowRichPosture,
                    "bestOpportunityAxis", "") == "wealth"
                && ReadStringList(matrixHighPoorPosture,
                    "allowedCourtTactics").Contains(
                        ReadString(matrixHighPoorPosture,
                            "preferredTactic", ""),
                        StringComparer.OrdinalIgnoreCase),
                "The same low-honor Court Character restrains toward a poor lower-tier target but can exploit independent wealth or rank opportunities with a permitted tactic.");
            add("hidden_wallet_cannot_create_opportunity",
                !ReadBool(matrixHiddenRich,
                    "economicCapacityKnown", true)
                && ReadString(matrixHiddenRich,
                    "wealthEvidenceBasis", "")
                    == "hidden_wallet_not_observable"
                && Math.Abs(ReadDouble(
                        ReadDictionary(matrixHiddenRich, "axes"),
                        "wealth", -1d)
                    - ReadDouble(
                        ReadDictionary(matrixLowPoor, "axes"),
                        "wealth", -2d)) < 0.01d,
                "A hidden rich wallet produces the same perceived wealth axis as the identically presented poor case.");
            Dictionary<string, object> capContext = new Dictionary<string, object>
            {
                ["activeDomains"] = SelectMotiveDomains("I offer gold, rank, and an affair.", "", new Dictionary<string, object>(), new Dictionary<string, object>(), calculatedReady),
                ["highlightedScores"] = CharactersService.CoreTraitKeys.ToDictionary(x => x, x => (object)100, StringComparer.OrdinalIgnoreCase),
                ["relationshipNpcToTarget"] = CompactRelationshipEvidence(neutralRelation), ["relationshipNpcToSpouse"] = CompactRelationshipEvidence(happySpouse),
                ["opportunity"] = recognized, ["scene"] = new Dictionary<string, object> { ["private"] = false, ["exposure"] = .9d, ["witnessCount"] = 8, ["witnessIds"] = new List<string> { "a", "b", "c", "d", "e", "f", "g", "h" }, ["coercive"] = false },
                ["romance"] = calculatedReady, ["manipulation"] = intriguePosture,
                ["courtCharacter"] = ReadDictionary(courtSeductionPosture, "courtCharacter")
            };
            string cappedPrompt = BuildConversationDecisionPrompt(capContext);
            add("live_prompt_cap", cappedPrompt.Length <= MotiveLiveContextCap
                && cappedPrompt.Contains("hardConstraints")
                && cappedPrompt.Contains("receptivity")
                && cappedPrompt.Contains("female_bp2_hn2")
                && cappedPrompt.Contains("Brazen Conspirator"),
                "The live motive packet preserves posture, hard constraints, and fixed Court Character inside the 1,800-character cap.");
            Dictionary<string, object> ordinaryContext = new Dictionary<string, object>(capContext, StringComparer.OrdinalIgnoreCase)
            {
                ["activeDomains"] = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["id"] = "power_status", ["priority"] = "primary",
                        ["evidenceKeys"] = new List<string> { "trait.pride", "trait.authorityRespect" }
                    }
                }
            };
            string ordinaryPrompt = BuildConversationDecisionPrompt(ordinaryContext);
            Dictionary<string, object> ordinaryBrief = AugmentDecisionBriefWithMotiveEvidence(
                new Dictionary<string, object>
                {
                    ["goals"] = new List<string> { "answer plainly" },
                    ["constraints"] = new List<string> { "maintain rank" }
                }, ordinaryContext);
            add("ordinary_turn_excludes_romance_posture",
                ordinaryPrompt.IndexOf("\"rom\":", StringComparison.Ordinal) < 0
                && ReadString(ordinaryBrief, "postureAlignment", "") == "not_applicable",
                "A non-romantic turn omits romance scoring from the live prompt and records no romance posture.");
            Dictionary<string, object> completedBrief = AugmentDecisionBriefWithMotiveEvidence(
                new Dictionary<string, object>
                {
                    ["decision"] = "Answer with guarded interest."
                }, ordinaryContext);
            add("missing_private_decision_lists_are_completed_without_retry",
                ReadStringList(completedBrief, "goals").SequenceEqual(new[] { "Answer with guarded interest." })
                && ReadString(completedBrief, "goalsSource", "") == "model_decision_fallback"
                && ReadStringList(completedBrief, "constraints").Count > 0
                && ReadString(completedBrief, "constraintsSource", "") == "deterministic_motive_context_fallback"
                && ReadStringList(completedBrief, "modelGoals").Count == 0
                && ReadStringList(completedBrief, "modelConstraints").Count == 0,
                "Occasional empty audit-only goal or constraint arrays are completed from the model decision and authoritative motive context without resending the full prompt.");
            Dictionary<string, object> repairContext =
                DialogueValidationRepairCharacterContext(
                    new Dictionary<string, object>
                    {
                        ["promptEnvelope"] = new Dictionary<string, object>
                        {
                            ["motiveDecision"] = capContext
                        },
                        ["messages"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                                { ["role"] = "system", ["content"] = "rules" },
                            new Dictionary<string, object>
                                { ["role"] = "user", ["content"] = "honorable, bold, and loyal character foundation" },
                            new Dictionary<string, object>
                                { ["role"] = "user", ["content"] = "live turn" }
                        }
                    });
            add("validation_repairs_receive_character_context",
                ReadDictionary(repairContext, "highlightedTraitScores").Count > 0
                && ReadDictionary(repairContext, "courtCharacter").Count > 0
                && ReadString(repairContext, "characterFoundation", "")
                    .Contains("honorable, bold, and loyal"),
                "Every constrained dialogue correction can receive the original character foundation plus compact authoritative traits, motives, relationships, scene, and political posture.");
            Dictionary<string, object> acceptedRepairMarker =
                new Dictionary<string, object>
                {
                    ["reply"] = "I have corrected my answer."
                };
            Dictionary<string, object> overriddenRepairMarker =
                new Dictionary<string, object>
                {
                    ["reply"] = "I still hold the same position."
                };
            bool acceptedMarkerApplied = MarkRepairedVisibleResponse(
                acceptedRepairMarker, true);
            bool overriddenMarkerApplied = MarkRepairedVisibleResponse(
                overriddenRepairMarker, false);
            add("visible_repair_markers_encode_revalidation_result",
                acceptedMarkerApplied
                && overriddenMarkerApplied
                && ReadString(acceptedRepairMarker, "reply", "")
                    == "I have corrected my answer.."
                && ReadString(overriddenRepairMarker, "reply", "")
                    == "I still hold the same position.,",
                "Every returned visible repair ends in '..' after clean revalidation or '.,' when a second validator rejection is deliberately overridden.");
            add("physical_threat_is_not_romance_authorization",
                !ContainsRomanticAuthorizationLanguage("Warn that the next repetition may become a physical threat or confrontation.")
                && ContainsRomanticAuthorizationLanguage("The accepted action is to kiss the petitioner."),
                "A non-romantic physical threat cannot trigger the romance hard-constraint repair, while an actual accepted kiss remains detectable.");
            Dictionary<string, object> longRelationship = new Dictionary<string, object>
            {
                ["nativeRelation"] = -17,
                ["directionalAffinity"] = -24,
                ["perceptionTag"] = new string('p', 600),
                ["observerMbti"] = "INTJ",
                ["targetMbti"] = "ENFP",
                ["compatibilityChance"] = 43,
                ["lifecycleTags"] = Enumerable.Range(0, 20).Select(i => "relationship_lifecycle_" + i + "_" + new string('t', 180)).ToList(),
                ["recentIncidents"] = Enumerable.Range(0, 6).Select(i => new Dictionary<string, object>
                {
                    ["kind"] = "incident_" + i + "_" + new string('k', 300),
                    ["worldDay"] = 100d + i,
                    ["summary"] = "Important incident evidence " + i + ": " + new string('e', 4000)
                }).ToList()
            };
            Dictionary<string, object> pathologicalCourtCharacter =
                VerificationService.CloneDictionary(
                    ReadDictionary(capContext, "courtCharacter")
                    ?? new Dictionary<string, object>());
            pathologicalCourtCharacter["activeScheme"] =
                new Dictionary<string, object>
                {
                    ["schemeId"] = "scheme_test",
                    ["kind"] = "court_pressure",
                    ["status"] = "active",
                    ["privatePlan"] = new string('z', 12000)
                };
            Dictionary<string, object> pathologicalContext = new Dictionary<string, object>(capContext, StringComparer.OrdinalIgnoreCase)
            {
                ["relationshipNpcToTarget"] = longRelationship,
                ["relationshipNpcToSpouse"] = longRelationship,
                ["courtCharacter"] = pathologicalCourtCharacter,
                ["spouseId"] = "spouse_with_a_real_relationship",
                ["scene"] = new Dictionary<string, object>
                {
                    ["private"] = false, ["exposure"] = .99d, ["coercive"] = false,
                    ["witnessCount"] = 200,
                    ["witnessIds"] = Enumerable.Range(0, 200).Select(i => "witness_" + i + "_" + new string('w', 300)).ToList()
                }
            };
            string pathologicalPrompt = BuildConversationDecisionPrompt(pathologicalContext);
            add("live_prompt_pathological_relationship_history", pathologicalPrompt.Length <= MotiveLiveContextCap
                && pathologicalPrompt.Contains("directionalAffinity")
                && pathologicalPrompt.Contains("hardConstraints")
                && pathologicalPrompt.Contains("receptivity")
                && !pathologicalPrompt.Contains(
                    new string('z', 100),
                    StringComparison.Ordinal),
                "Long incident summaries, lifecycle tags, witness rosters, and private scheme payloads compact to valid decision evidence instead of failing a conversation.");
            return new Dictionary<string, object> { ["ok"] = cases.All(x => ReadBool(x, "passed", false)), ["model"] = MotiveDecisionModel, ["cases"] = cases };
        }

        private static Dictionary<string, object> MotiveTestCharacteristics(Dictionary<string, int> overrides)
        {
            Dictionary<string, object> percentages = CharactersService.CoreTraitKeys.ToDictionary(x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, int> pair in overrides ?? new Dictionary<string, int>()) percentages[pair.Key] = pair.Value;
            Dictionary<string, object> groups = CharactersService.CourtVirtueKeys.ToDictionary(x => x, x => (object)50, StringComparer.OrdinalIgnoreCase);
            groups["boldness"] = ReadInt(percentages, "confidence", 50); groups["loyalty"] = ReadInt(percentages, "loyalty", 50);
            groups["honor"] = 50; groups["judgment"] = ReadInt(percentages, "discipline", 50);
            return new Dictionary<string, object> { ["traits"] = new Dictionary<string, object> { ["traitPercentages"] = percentages, ["courtVirtues"] = groups } };
        }

        private static void MotiveTestCourtCharacter(
            Dictionary<string, object> characteristics,
            int honor,
            int boldness,
            bool isFemale)
        {
            Dictionary<string, object> traits =
                ReadDictionary(characteristics, "traits")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> groups =
                ReadDictionary(traits, "courtVirtues")
                ?? new Dictionary<string, object>();
            groups["honor"] = honor;
            groups["boldness"] = boldness;
            traits["courtVirtues"] = groups;
            traits["courtCharacter"] = CourtService.BuildCourtCharacterData(
                traits,
                new Dictionary<string, object>
                {
                    ["age"] = 30d,
                    ["isFemale"] = isFemale,
                    ["isChild"] = false
                });
            characteristics["traits"] = traits;
        }

        private static Dictionary<string, object>
            MotiveTestOpportunity(
                int observerTier,
                int playerTier,
                int playerGold,
                bool economicCapacityKnown)
        {
            Dictionary<string, object> target =
                new Dictionary<string, object>
                {
                    ["clanTier"] = playerTier,
                    ["renown"] = playerTier * 300,
                    ["fiefCount"] = 0,
                    ["wealth"] = new Dictionary<string, object>
                    {
                        ["gold"] = economicCapacityKnown
                            ? playerGold : -1,
                        ["clanGold"] = economicCapacityKnown
                            ? playerGold : -1,
                        ["clanTier"] = playerTier,
                        ["clanRenown"] = playerTier * 300,
                        ["clanFiefCount"] = 0,
                        ["economicCapacityKnown"] =
                            economicCapacityKnown,
                        ["wealthEvidenceBasis"] =
                            economicCapacityKnown
                                ? "demonstrated_financial_capacity"
                                : "hidden_wallet_not_observable"
                    },
                    ["appearance"] =
                        new Dictionary<string, object>
                        {
                            ["visibleStatusScore"] = 10d
                        }
                };
            Dictionary<string, object> observer =
                new Dictionary<string, object>
                {
                    ["clanTier"] = observerTier,
                    ["renown"] = observerTier * 300,
                    ["fiefCount"] = 1,
                    ["wealth"] = new Dictionary<string, object>
                    {
                        ["gold"] = 250000,
                        ["clanGold"] = 250000,
                        ["clanTier"] = observerTier,
                        ["clanRenown"] = observerTier * 300,
                        ["clanFiefCount"] = 1
                    },
                    ["appearance"] =
                        new Dictionary<string, object>
                        {
                            ["visibleStatusScore"] = 55d
                        }
                };
            Dictionary<string, object> payload =
                new Dictionary<string, object>
                {
                    ["opportunitySnapshot"] =
                        new Dictionary<string, object>
                        {
                            ["target"] = target,
                            ["observer"] = observer,
                            ["identityKnown"] = true
                        }
                };
            return CalculatePerceivedOpportunity(
                new Dictionary<string, object>(),
                MotiveTestCharacteristics(
                    new Dictionary<string, int>()),
                payload,
                new Dictionary<string, object>
                {
                    ["identityState"] = "verified"
                });
        }

        private static Dictionary<string, object> MotiveTestRelationship(
            double directionalAffinity,
            bool lovers = false,
            bool activeAffair = false,
            bool estranged = false)
        {
            List<string> tags = new List<string>();
            if (lovers) tags.Add("lovers");
            if (activeAffair) tags.Add("active_affair");
            if (estranged) tags.Add("estranged");
            return new Dictionary<string, object>
            {
                ["relationshipModel"] = "directional_mbti_native_relation",
                ["nativeRelation"] = RelationshipsService.RoundAwayFromZero(directionalAffinity),
                ["directionalAffinity"] = directionalAffinity,
                ["perceptionTag"] = RelationshipsService.DirectionalRelationshipTag("selftest", "npc|target",
                    "observer_to_target", RelationshipsService.RoundAwayFromZero(directionalAffinity), 0),
                ["lifecycle"] = new Dictionary<string, object>
                {
                    ["lovers"] = lovers,
                    ["activeAffair"] = activeAffair,
                    ["estranged"] = estranged,
                    ["tags"] = tags
                },
                ["incidents"] = new List<Dictionary<string, object>>()
            };
        }

        private static Dictionary<string, object> MotiveTestPayload(int targetAge, bool adults, bool closeKin, bool coercive, int visibleAttractiveness)
        {
            return new Dictionary<string, object>
            {
                ["skipCooldownQuery"] = true, ["worldDay"] = 10d, ["conversationSessionId"] = "selftest",
                ["opportunitySnapshot"] = new Dictionary<string, object>
                {
                    ["target"] = new Dictionary<string, object> { ["age"] = targetAge, ["appearance"] = new Dictionary<string, object> { ["attractiveness"] = visibleAttractiveness } },
                    ["suitability"] = new Dictionary<string, object> { ["adults"] = adults, ["nativeSuitable"] = adults && !closeKin, ["closeKin"] = closeKin },
                    ["scene"] = new Dictionary<string, object> { ["private"] = true, ["exposure"] = 0.1d, ["coercive"] = coercive, ["witnessIds"] = new List<string>() }
                }
            };
        }

#if !REIGN_EXCLUDE_VERIFICATION
        internal static List<Dictionary<string, object>> RunMotiveAwareConversationSelfTests()
        {
            Dictionary<string, object> report = MotiveAwareConversationSelfTests();
            List<Dictionary<string, object>> rows = ReadDictionaryList(report, "cases").Select(row => new Dictionary<string, object>
            {
                ["ok"] = true, ["passed"] = ReadBool(row, "passed", false), ["suite"] = "motive_conversation",
                ["caseId"] = ReadString(row, "id", ""), ["name"] = ReadString(row, "id", ""), ["summary"] = ReadString(row, "detail", ""), ["durationMs"] = 0
            }).ToList();
            rows.AddRange(RunPoliticalDangerPostureSelfTests());
            rows.AddRange(RunConversationIntoxicationSelfTests());
            rows.AddRange(RunDialogueIntegrityAssertions().Select(row => new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = ReadBool(row, "passed", false),
                ["suite"] = "dialogue_integrity",
                ["caseId"] = ReadString(row, "name", ""),
                ["name"] = ReadString(row, "name", ""),
                ["summary"] = ReadString(row, "detail", ""),
                ["durationMs"] = 0
            }));
            rows.AddRange(WorldSimulationService.RunDialogueActionAuthorityAssertions().Select(row => new Dictionary<string, object>
            {
                ["ok"] = true,
                ["passed"] = ReadBool(row, "passed", false),
                ["suite"] = "dialogue_action_authority",
                ["caseId"] = ReadString(row, "name", ""),
                ["name"] = ReadString(row, "name", ""),
                ["summary"] = ReadString(row, "detail", ""),
                ["durationMs"] = 0
            }));
            return rows;
        }
#endif

        private static double InitiativeChanceForTest(double score) { return score < 45d ? 0d : score < 60d ? .25d : score < 75d ? .5d : score < 90d ? .75d : .9d; }
        private static double Trait01FromPercent(Dictionary<string, object> percentages, string key) { return DialogueService.ClampDouble(ReadDouble(percentages, key, 50d) / 100d, 0d, 1d); }
        private static double CourtGroup100(Dictionary<string, object> characteristics, string key) { return DialogueService.ClampDouble(ReadDouble(ReadDictionary(ReadDictionary(characteristics, "traits"), "courtVirtues") ?? new Dictionary<string, object>(), key, 50d), 0d, 100d); }
        private static double CourtGroup01(Dictionary<string, object> characteristics, string key) { return CourtGroup100(characteristics, key) / 100d; }
        private static double RomanticPressure01(Dictionary<string, object> characteristics, Dictionary<string, object> relationship, Dictionary<string, object> payload, string text)
        {
            double explicitPressure = ReadDouble(payload, "romanticSocialPressure", ReadDouble(ReadDictionary(characteristics, "pressure"), "romanticOrSocialRisk", 0d)) / 100d;
            double wording = DialogueService.ContainsAny((text ?? "").ToLowerInvariant(), "kiss", "flirt", "seduc", "desire", "attract", "lover", "affair", "marry") ? 0.65d : 0d;
            return DialogueService.ClampDouble(Math.Max(wording, explicitPressure), 0d, 1d);
        }
        private static double RomanticApproachModifier(string text)
        {
            string lower = (text ?? "").ToLowerInvariant();
            if (DialogueService.ContainsAny(lower, "threat", "force", "or else", "execute", "kill you")) return -35d;
            if (DialogueService.ContainsAny(lower, "kiss", "flirt", "desire", "attract", "beautiful", "handsome", "love")) return 5d;
            return 0d;
        }
    }
}
