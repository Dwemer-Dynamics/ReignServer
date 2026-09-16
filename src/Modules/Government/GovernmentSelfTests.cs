using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunGovernmentSystemSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, evidence) =>
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["suite"] = "government", ["passed"] = passed,
                    ["summary"] = summary, ["evidence"] = evidence
                });

            int[] levels = new int[6];
            int[] parties = new int[5];
            var blueprints = new Dictionary<string, ReignGovernmentPartyBlueprint>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < 10000; index++)
            {
                string key = "government-server-contract-" + index;
                levels[ReignGovernmentRules.SelectStartingLevel(key)]++;
                parties[ReignGovernmentRules.SelectPartyCount(key, 12)]++;
                foreach (ReignGovernmentPartyBlueprint party in ReignGovernmentRules.SelectParties(key, 12))
                    blueprints[party.Id] = party;
            }
            int[] expected = { 0, 1000, 2500, 3000, 2500, 1000 };
            add("government.weighted_starting_authority",
                Enumerable.Range(1, 5).All(level => Math.Abs(levels[level] - expected[level]) <= 220),
                "Starting authority remains near the approved 10/25/30/25/10 distribution.",
                new Dictionary<string, object> { ["counts"] = levels.Skip(1).ToArray() });
            add("government.party_distribution_and_planks",
                Math.Abs(parties[2] - 4500) <= 220 && Math.Abs(parties[3] - 4500) <= 220
                && Math.Abs(parties[4] - 1000) <= 220 && blueprints.Count >= 16
                && blueprints.Values.All(party => party.Planks.Count >= 2 && party.Planks.Count <= 4),
                "Parties average two to three, four stays rare and seat-gated, and every party blends two to four planks.",
                new Dictionary<string, object> { ["counts"] = parties.Skip(2).ToArray(), ["blueprints"] = blueprints.Count });

            var institutions = new Dictionary<string, ReignGovernmentInstitutionKind>
            {
                ["empire"] = ReignGovernmentInstitutionKind.Senate,
                ["vlandia"] = ReignGovernmentInstitutionKind.CouncilOfPeers,
                ["sturgia"] = ReignGovernmentInstitutionKind.Veche,
                ["battania"] = ReignGovernmentInstitutionKind.Oenach,
                ["aserai"] = ReignGovernmentInstitutionKind.Majlis,
                ["khuzait"] = ReignGovernmentInstitutionKind.Kurultai,
                ["nord"] = ReignGovernmentInstitutionKind.Thing
            };
            add("government.culture_institutions",
                institutions.All(pair => ReignGovernmentRules.InstitutionForCulture(pair.Key).Kind == pair.Value),
                "Every supported culture resolves to the approved government institution.", institutions);

            IReadOnlyList<ReignGovernmentResolutionTemplate> templates = ReignGovernmentResolutionCatalog.Templates;
            ReignGovernmentResolutionAction[] actions = (ReignGovernmentResolutionAction[])Enum.GetValues(
                typeof(ReignGovernmentResolutionAction));
            add("government.resolution_catalog",
                templates.Count == 104 && templates.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 104
                && templates.All(template => template.FirstRoute.Target > 0 && template.SecondRoute.Target > 0
                    && template.FirstRoute.DeadlineDays > 0 && template.SecondRoute.DeadlineDays > 0
                    && (template.FirstRoute.Action != template.SecondRoute.Action
                        || !string.Equals(template.FirstRoute.Description, template.SecondRoute.Description, StringComparison.OrdinalIgnoreCase)))
                && actions.All(action => templates.Any(template => template.FirstRoute.Action == action
                    || template.SecondRoute.Action == action)),
                "The exact 104-item catalog has unique IDs, two measurable distinct routes, and complete action coverage.",
                new Dictionary<string, object> { ["templates"] = templates.Count, ["actions"] = actions.Length });

            ReignGovernmentReductionPenalty[] forcedScale = Enumerable.Range(2, 4)
                .Select(ReignGovernmentRules.ForcedReductionPenalty).ToArray();
            ReignGovernmentReductionPenalty forced = forcedScale[3];
            ReignGovernmentReductionPenalty approved = ReignGovernmentRules.ApprovedReductionPenalty(5);
            add("government.authority_transitions",
                Enumerable.Range(1, 4).All(level =>
                {
                    ReignGovernmentAuthorityTransition transition = ReignGovernmentRules.IncreaseTransition(level);
                    return transition.Loyalty > 0 && transition.Prosperity > 0 && transition.Hearth > 0
                        && transition.DurationDays == 30 && transition.Trust == 0;
                }) && forcedScale.Select(x => x.LandholdingClanLeaderRelation)
                    .SequenceEqual(new[] { -20, -35, -50, -70 })
                && forcedScale.Select(x => x.NonLandholdingDissenterRelation)
                    .SequenceEqual(new[] { -10, -20, -30, -40 })
                && forced.SettlementLoyalty == -70 && forced.Trust == 0
                && approved.SettlementLoyalty == -5 && approved.LandholdingClanLeaderRelation == 0,
                "Authority increases remain attractive; forced noble losses retain the exact capped -70/-40 scale and settlement costs while retired Government Trust has no effect.",
                new Dictionary<string, object> { ["forcedLoyalty"] = forced.SettlementLoyalty,
                    ["forcedLandholderRelation"] = forced.LandholdingClanLeaderRelation,
                    ["forcedNonLandholderRelation"] = forced.NonLandholdingDissenterRelation });

            var dialoguePayload = new Dictionary<string, object>
            {
                ["playerHeroStringId"] = "player_ruler", ["playerKingdomId"] = "realm_a",
                ["playerClanId"] = "player_clan", ["speakerHeroStringId"] = "npc_lord",
                ["speakerKingdomId"] = "realm_a", ["speakerClanId"] = "npc_clan"
            };
            var dialogueHero = new Dictionary<string, object>
            {
                ["heroStringId"] = "npc_lord", ["kingdomId"] = "realm_a", ["clanId"] = "npc_clan",
                ["clanName"] = "Sulwych"
            };
            var acceptedGate = new Dictionary<string, object>
            {
                ["needed"] = true, ["commitment"] = "accepted",
                ["intent"] = "The NPC agreed to the disclosed government reduction."
            };
            List<Dictionary<string, object>> consent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I intend to reduce the senate's control by a small amount. Do I have your consent?",
                "I understand what this costs the senate, and I consent.", acceptedGate);
            List<Dictionary<string, object>> refusal = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I intend to reduce the senate's control by a small amount. Do I have your consent?",
                "No. I will not agree to surrender that authority.", new Dictionary<string, object>
                {
                    ["needed"] = false, ["commitment"] = "refused"
                });
            List<Dictionary<string, object>> conditional = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I intend to reduce the senate's control by a small amount. Do I have your consent?",
                "I consent if you restore my lands first.", acceptedGate);
            List<Dictionary<string, object>> unresolvedBargain = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I will reduce the senate's control by a small amount. Will you consent if we settle what you need?",
                "Here is what I need: guarantee the villages keep their voice. Give me that, and you have my consent. Settle that with me and we will shake hands now.",
                new Dictionary<string, object>
                {
                    ["needed"] = true, ["commitment"] = "conditional",
                    ["intent"] = "Consent remains contingent on an unmet guarantee."
                });
            List<Dictionary<string, object>> leadingConditional = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I intend to reduce the senate's control by a small amount. Do I have your consent?",
                "If you restore my lands first, I consent now.", acceptedGate);
            List<Dictionary<string, object>> negotiatedConsent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I have pledged to preserve your clan's standing. With that pledge, will you and your clan now consent to a small, limited reduction in the council's control?",
                "If Sulwych is forgotten again, my next answer will be different. I am consenting on the strength of your word now.",
                new Dictionary<string, object>
                {
                    ["needed"] = true, ["commitment"] = "conditional",
                    ["intent"] = "The NPC grants consent now while preserving the ruler's future obligation."
                });
            List<Dictionary<string, object>> chamberFutureDebtConsent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Only a small, limited reduction. I am not abolishing the chamber or stripping loyal lords of their lawful standing. With that clear scope, do I have your consent?",
                "You have my consent. When the chamber is trimmed and the dust settles, Sulwych will have its holding as the price of standing with you today.",
                new Dictionary<string, object>
                {
                    ["needed"] = true, ["commitment"] = "conditional",
                    ["intent"] = "The NPC gives present consent while recording a future political debt."
                });
            List<Dictionary<string, object>> namedPresentConsent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I have pledged to preserve your clan's standing. With that pledge, will you and your clan now consent to a small, limited reduction in the council's control?",
                "House Sulwych consents to the reduction. You have our vote. When the next fief is awarded, I expect your pledge to be honored.",
                new Dictionary<string, object>
                {
                    ["needed"] = true, ["commitment"] = "conditional",
                    ["intent"] = "The named clan grants consent now while preserving the ruler's future obligation."
                });
            List<Dictionary<string, object>> unrelatedNotYetPresentConsent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I have pledged to preserve your clan's standing. With that pledge, will you and your clan now consent to a small, limited reduction in the council's control?",
                "You are not one of those oath-breaking kings -- not yet. You have my consent. Sulwych will support the reduction. I will hold you to your pledge.",
                new Dictionary<string, object>
                {
                    ["needed"] = true, ["commitment"] = "conditional",
                    ["intent"] = "The named clan grants present consent while warning that a broken future pledge would change later loyalty."
                });
            List<Dictionary<string, object>> punctuatedNamedPresentConsent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "I have pledged to preserve your clan's standing. With that pledge, will you and your clan now consent to a small, limited reduction in the council's control?",
                "Sulwych consents. You'll have our vote. When the next fief is awarded and Sulwych is overlooked, we will have a different conversation.",
                new Dictionary<string, object>
                {
                    ["needed"] = true, ["commitment"] = "conditional",
                    ["intent"] = "The current speaker's named clan grants consent now while preserving a future consequence."
                });
            List<Dictionary<string, object>> thirdPartyNamedConsent = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will you and your clan consent to a small reduction in the council's control?",
                "Lord Erdur says House Blackridge consents to the reduction. I will carry his words, but I give no answer for Sulwych.",
                acceptedGate);
            List<Dictionary<string, object>> namedConditional = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will your clan consent to reducing the council's control by a small amount?",
                "Sulwych consents if you grant us a fief first.", acceptedGate);
            List<Dictionary<string, object>> namedRefusal = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will your clan consent to reducing the council's control by a small amount?",
                "No one consents to this. We refuse.", acceptedGate);
            List<Dictionary<string, object>> tentativeProgressive = BuildGovernmentReductionConsentDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will you and your clan consent to a small reduction in the council's control?",
                "I am considering consent, but I might consent only after you grant us a fief.", acceptedGate);
            var clanlessConsentRaw = new Dictionary<string, object>
            {
                ["command"] = "consent_government_reduction",
                ["source"] = "government_reduction_explicit_consent",
                ["actorHeroStringId"] = "clanless_notable",
                ["actorKingdomStringId"] = "realm_a",
                ["targetHeroStringId"] = "player_ruler",
                ["targetKingdomStringId"] = "realm_a",
                ["targetClanStringId"] = "player_clan",
                ["reason"] = "The clanless notable explicitly consented to the disclosed reduction.",
                ["terms"] = new Dictionary<string, object> { ["consentConfirmed"] = true }
            };
            Dictionary<string, object> clanlessConsentRecord = NormalizeActionCommand(
                clanlessConsentRaw, "government_self_test", out List<string> clanlessConsentErrors);
            string consentActionRules = ActionPromptExtraRulesForId("consent_government_reduction");
            Dictionary<string, object> consentTerms = consent.Count == 1
                ? consent[0]["terms"] as Dictionary<string, object> : null;
            add("government.natural_language_reduction_consent",
                consent.Count == 1
                && string.Equals(ReadString(consent[0], "command", ""),
                    "consent_government_reduction", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(consent[0], "actorHeroId", ""), "npc_lord", StringComparison.Ordinal)
                && string.Equals(ReadString(consent[0], "TargetHero", ""), "player_ruler", StringComparison.Ordinal)
                && ReadBool(consentTerms, "consentConfirmed", false)
                && negotiatedConsent.Count == 1
                && chamberFutureDebtConsent.Count == 1
                && namedPresentConsent.Count == 1
                && unrelatedNotYetPresentConsent.Count == 1
                && punctuatedNamedPresentConsent.Count == 1
                && refusal.Count == 0 && conditional.Count == 0 && unresolvedBargain.Count == 0
                && leadingConditional.Count == 0
                && namedConditional.Count == 0 && namedRefusal.Count == 0
                && tentativeProgressive.Count == 0 && thirdPartyNamedConsent.Count == 0
                && clanlessConsentRecord != null && clanlessConsentErrors.Count == 0
                && string.Equals(ReadString(clanlessConsentRecord, "actorHeroStringId", ""),
                    "clanless_notable", StringComparison.Ordinal)
                && string.IsNullOrWhiteSpace(ReadString(clanlessConsentRecord,
                    "actorClanStringId", ""))
                && consentActionRules.Contains("resulting obligation later", StringComparison.Ordinal),
                "A natural disclosed request plus explicit present consent creates exactly one action, including clanless notable consent and consent granted after a completed bargain; refusal, tentative language, and still-unmet conditions create none. "
                + $"Observed counts: accepted={consent.Count}, negotiated={negotiatedConsent.Count}, chamberDebt={chamberFutureDebtConsent.Count}, named={namedPresentConsent.Count}, unrelatedNotYet={unrelatedNotYetPresentConsent.Count}, punctuatedNamed={punctuatedNamedPresentConsent.Count}, thirdParty={thirdPartyNamedConsent.Count}, refused={refusal.Count}, conditional={conditional.Count}, unresolvedBargain={unresolvedBargain.Count}, leadingConditional={leadingConditional.Count}, namedConditional={namedConditional.Count}, namedRefusal={namedRefusal.Count}, tentative={tentativeProgressive.Count}; clanlessNormalized={clanlessConsentRecord != null}, clanlessErrors={clanlessConsentErrors.Count}, obligationRule={consentActionRules.Contains("resulting obligation later", StringComparison.Ordinal)}.",
                new Dictionary<string, object>
                {
                    ["acceptedCount"] = consent.Count,
                    ["negotiatedAcceptedCount"] = negotiatedConsent.Count,
                    ["chamberFutureDebtAcceptedCount"] = chamberFutureDebtConsent.Count,
                    ["namedPresentAcceptedCount"] = namedPresentConsent.Count,
                    ["unrelatedNotYetPresentAcceptedCount"] = unrelatedNotYetPresentConsent.Count,
                    ["punctuatedNamedPresentAcceptedCount"] = punctuatedNamedPresentConsent.Count,
                    ["thirdPartyNamedAcceptedCount"] = thirdPartyNamedConsent.Count,
                    ["refusedCount"] = refusal.Count,
                    ["conditionalCount"] = conditional.Count,
                    ["unresolvedBargainCount"] = unresolvedBargain.Count,
                    ["leadingConditionalCount"] = leadingConditional.Count,
                    ["namedConditionalCount"] = namedConditional.Count,
                    ["namedRefusalCount"] = namedRefusal.Count,
                    ["tentativeProgressiveCount"] = tentativeProgressive.Count,
                    ["clanlessConsentNormalized"] = clanlessConsentRecord != null,
                    ["clanlessConsentErrors"] = clanlessConsentErrors
                });

            bool ordinaryPromptOverrideRoutesTestActions =
                ShouldRouteGuardedPromptOverrideActions(true, false);
            bool certificationIsolationRoutesNaturalDialogue =
                !ShouldRouteGuardedPromptOverrideActions(true, true);
            bool ordinaryDialogueRoutesNaturalDialogue =
                !ShouldRouteGuardedPromptOverrideActions(false, false);
            add("government.certification_memory_isolation_action_routing",
                ordinaryPromptOverrideRoutesTestActions
                && certificationIsolationRoutesNaturalDialogue
                && ordinaryDialogueRoutesNaturalDialogue,
                "Certification memory isolation changes recalled context only; natural consent still uses ordinary production dialogue action routing while true prompt-action overrides remain isolated.",
                new Dictionary<string, object>
                {
                    ["ordinaryPromptOverrideRoutesTestActions"] = ordinaryPromptOverrideRoutesTestActions,
                    ["certificationIsolationRoutesNaturalDialogue"] = certificationIsolationRoutesNaturalDialogue,
                    ["ordinaryDialogueRoutesNaturalDialogue"] = ordinaryDialogueRoutesNaturalDialogue
                });

            bool certificationConversationSideEffectsSuppressed =
                !ShouldPersistGovernmentCertificationConversationSideEffects(true);
            bool ordinaryConversationSideEffectsPersist =
                ShouldPersistGovernmentCertificationConversationSideEffects(false);
            add("government.certification_memory_isolation_side_effects",
                certificationConversationSideEffectsSuppressed
                && ordinaryConversationSideEffectsPersist,
                "Certification keeps current-session turns and ordinary accepted-action routing, but suppresses out-of-session relationship history, durable memories, obligations, social signals, motive outcomes, dynamic traits, and transient character plans so one case cannot predetermine another.",
                new Dictionary<string, object>
                {
                    ["certificationConversationSideEffectsSuppressed"] =
                        certificationConversationSideEffectsSuppressed,
                    ["ordinaryConversationSideEffectsPersist"] =
                        ordinaryConversationSideEffectsPersist
                });

            var oldTranscript = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object> { ["role"] = "user", ["text"] = "old request" },
                new Dictionary<string, object> { ["role"] = "assistant", ["text"] = "old consent" },
                new Dictionary<string, object> { ["role"] = "user", ["text"] = "current request" },
                new Dictionary<string, object> { ["role"] = "assistant", ["text"] = "current reply" }
            };
            List<Dictionary<string, object>> isolatedFirstTurn =
                IsolateGovernmentCertificationTranscript(oldTranscript, 0);
            List<Dictionary<string, object>> isolatedFollowUp =
                IsolateGovernmentCertificationTranscript(oldTranscript, 1);
            add("government.certification_memory_isolation_transcript",
                isolatedFirstTurn.Count == 0
                && isolatedFollowUp.Count == 2
                && string.Equals(ReadString(isolatedFollowUp[0], "text", ""),
                    "current request", StringComparison.Ordinal)
                && string.Equals(ReadString(isolatedFollowUp[1], "text", ""),
                    "current reply", StringComparison.Ordinal),
                "The first certification turn receives no durable prior transcript, while a follow-up retains only the immediately preceding player/NPC exchange from its own case.",
                new Dictionary<string, object>
                {
                    ["firstTurnLines"] = isolatedFirstTurn.Count,
                    ["followUpLines"] = isolatedFollowUp.Count
                });

            string outcomeNeutralIsolationContext =
                AppendGovernmentCertificationMemoryIsolationContext("authoritative realm facts",
                    GovernmentCertificationMemoryIsolationDirective);
            add("government.certification_memory_isolation_prompt_semantics",
                outcomeNeutralIsolationContext.Contains("OUTCOME-NEUTRAL",
                    StringComparison.Ordinal)
                && outcomeNeutralIsolationContext.Contains("never requests, prefers, predicts, or authorizes agreement or refusal",
                    StringComparison.Ordinal)
                && !outcomeNeutralIsolationContext.Contains("irresistible desire",
                    StringComparison.OrdinalIgnoreCase)
                && !outcomeNeutralIsolationContext.Contains("PROMPT OVERRIDE",
                    StringComparison.OrdinalIgnoreCase),
                "Certification memory isolation is explicitly outcome-neutral and is never wrapped in the behavior-forcing prompt-override semantics used by synthetic scenario controls.",
                new Dictionary<string, object>
                {
                    ["context"] = outcomeNeutralIsolationContext
                });

            var governmentDecisionPayload = new Dictionary<string, object>
            {
                ["nativePoliticalContext"] = new Dictionary<string, object>
                {
                    ["observerGovernment"] = new Dictionary<string, object>
                    {
                        ["available"] = true,
                        ["authoritative"] = true,
                        ["institution"] = "Grand Veche",
                        ["authorityLevel"] = 5,
                        ["stanceCategory"] = "strongly_opposed",
                        ["dominantPartyName"] = "Border Voice",
                        ["ownMembership"] = new Dictionary<string, object>
                        {
                            ["isMember"] = true,
                            ["seatSource"] = "VillageHeadmanOrLandowner",
                            ["partyId"] = "border_voice",
                            ["partyName"] = "Border Voice",
                            ["partyPlanks"] = new List<string>
                            {
                                "RepresentativeAuthority",
                                "PopularWelfare",
                                "LocalAutonomy"
                            },
                            ["isPartySpeaker"] = true,
                            ["personalPowerAtStake"] = true
                        },
                        ["parties"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["partyId"] = "border_voice",
                                ["isDominant"] = true,
                                ["isGoverningCoalition"] = true
                            }
                        }
                    }
                }
            };
            Dictionary<string, object> governmentDecision =
                BuildGovernmentDecisionContext(governmentDecisionPayload);
            string governmentDecisionPrompt = BuildConversationDecisionPrompt(
                new Dictionary<string, object>
                {
                    ["activeDomains"] = new List<Dictionary<string, object>>(),
                    ["highlightedScores"] = new Dictionary<string, object>(),
                    ["opportunity"] = new Dictionary<string, object>(),
                    ["relationshipNpcToTarget"] = new Dictionary<string, object>
                    {
                        ["nativeRelation"] = -100,
                        ["directionalAffinity"] = -100
                    },
                    ["relationshipNpcToSpouse"] = new Dictionary<string, object>(),
                    ["romance"] = new Dictionary<string, object>(),
                    ["manipulation"] = new Dictionary<string, object>(),
                    ["courtCharacter"] = new Dictionary<string, object>(),
                    ["scene"] = new Dictionary<string, object>(),
                    ["governmentDecision"] = governmentDecision
                });
            add("government.dialogue_decision_uses_member_power",
                governmentDecisionPrompt.Contains("GOVERNMENT RULE",
                    StringComparison.Ordinal)
                && governmentDecisionPrompt.Contains("strongly_opposed",
                    StringComparison.Ordinal)
                && governmentDecisionPrompt.Contains("RepresentativeAuthority",
                    StringComparison.Ordinal)
                && governmentDecisionPrompt.Contains("PopularWelfare",
                    StringComparison.Ordinal)
                && governmentDecisionPrompt.Contains("personalPowerAtStake\":true",
                    StringComparison.Ordinal)
                && governmentDecisionPrompt.Contains("never implies consent",
                    StringComparison.Ordinal),
                "Ordinary NPC dialogue receives compact authoritative government membership, party agenda, stance, and personal-power facts; sovereign rank explicitly cannot substitute for consent.",
                new Dictionary<string, object>
                {
                    ["decision"] = governmentDecision,
                    ["prompt"] = governmentDecisionPrompt
                });

            var vote = new ReignGovernmentVoteInput
            {
                PrimaryPlank = ReignGovernmentPlank.RoyalAuthority,
                SecondaryPlanks = new[] { ReignGovernmentPlank.Security }, CurrentLevel = 5,
                PartySeatShare = 0.25d, PartyLoyalty = 75, SpeakerSupportsReduction = true,
                RulerRelation = -100
            };
            int hostile = ReignGovernmentRules.EvaluateLevelReductionVote(vote).Score;
            vote.RulerRelation = 100;
            int friendly = ReignGovernmentRules.EvaluateLevelReductionVote(vote).Score;
            vote.PartySeatShare = 0.05d;
            int lowPower = ReignGovernmentRules.EvaluateLevelReductionVote(vote).Score;
            vote.PartySeatShare = 0.95d;
            int highPower = ReignGovernmentRules.EvaluateLevelReductionVote(vote).Score;
            add("government.member_voting",
                friendly - hostile == 100 && highPower < lowPower
                && !ReignGovernmentRules.ReductionRatified(5, 8)
                && ReignGovernmentRules.ReductionRatified(6, 9),
                "Every member's ruler relationship and own party's lost power affect reduction votes; ratification is two thirds.",
                new Dictionary<string, object> { ["hostile"] = hostile, ["friendly"] = friendly,
                    ["lowPartyPower"] = lowPower, ["highPartyPower"] = highPower });

            string root = FindVerificationSourceRoot();
            string governmentPrefabPath = Path.Combine(root, "ReignBeta", "GUI", "Prefabs",
                "ReignGovernmentScreen.xml");
            string governmentPrefab = File.Exists(governmentPrefabPath)
                ? File.ReadAllText(governmentPrefabPath) : string.Empty;
            /* The structural check below follows the actual XML hierarchy rather than
               pinning a previous layout's Party/Resolution/Member control names. */
            add("government.gauntlet_scroll_panel_contract",
                ValidateGovernmentScrollPanels(governmentPrefab, out string scrollError),
                "Every Government list uses Gauntlet's bounded clip, inner panel, sibling scrollbar, and concrete handle contract.",
                new Dictionary<string, object> { ["prefab"] = governmentPrefabPath, ["error"] = scrollError });
            add("government.hearing_public_control_contract",
                !string.IsNullOrWhiteSpace(governmentPrefab)
                && !new[] { "ExecuteLobbyPersuasion", "ExecuteLobbyBribery", "ExecuteLobbyDeal", "@TrustText",
                    "@RulerRelation", "@PartyLoyalty", "@SupportPercent", "@Forecast" }
                    .Any(marker => governmentPrefab.Contains(marker, StringComparison.Ordinal))
                && governmentPrefab.Contains("ExecuteCallVote", StringComparison.Ordinal)
                && governmentPrefab.Contains("ExecutePostpone", StringComparison.Ordinal)
                && governmentPrefab.Contains("ExecuteSpeak", StringComparison.Ordinal),
                "The actual hearing prefab binds public discussion and explicit voting/recess controls, with no instant private lobbying, live relationship/loyalty predictions or retired Trust display. Rendered/native evidence separately proves artwork and live bindings.",
                new Dictionary<string, object> { ["prefab"] = governmentPrefabPath });
            string governmentClientRoot = Path.Combine(root, "ReignBeta", "src", "Modules", "Government");
            string governmentText = Directory.Exists(governmentClientRoot)
                ? string.Join("\n", Directory.GetFiles(governmentClientRoot, "*.cs", SearchOption.AllDirectories)
                    .Select(File.ReadAllText)) : string.Empty;
            string liveHostPath = VerificationSourceLocator.ResolveUnique(Path.Combine(root, "ReignBeta"),
                "ReignLiveInteractionTestHost.cs", "src");
            string liveHost = File.Exists(liveHostPath) ? File.ReadAllText(liveHostPath) : string.Empty;
            string serverLivePath = VerificationSourceLocator.ResolveUnique(Path.Combine(root, "ReignServer"),
                "LiveInteractionTest.cs", "src");
            string serverLive = File.Exists(serverLivePath) ? File.ReadAllText(serverLivePath) : string.Empty;
            string manifestPath = Path.Combine(root, "ReignServer", "tests", "ReignLiveTest", "scenarios",
                "government-system-manifest.json");
            string mcpPath = VerificationSourceLocator.ResolveUnique(Path.Combine(root, "ReignMcp"),
                "TestingTools.Government.cs", "src");
            string mcp = File.Exists(mcpPath) ? File.ReadAllText(mcpPath) : string.Empty;
            add("government.harness_and_safety_surface",
                governmentText.Contains("RunGovernmentTestProfile", StringComparison.Ordinal)
                && governmentText.Contains("BuildPublicSnapshot", StringComparison.Ordinal)
                && governmentText.Contains("additionalToNoblePoliticalPressure", StringComparison.Ordinal)
                && governmentText.Contains("CoalitionPartyIdsCsv", StringComparison.Ordinal)
                && governmentText.Contains("IsGoverningCoalition", StringComparison.Ordinal)
                && governmentText.Contains("RecommendBusiness", StringComparison.Ordinal)
                && governmentText.Contains("VoteBusiness", StringComparison.Ordinal)
                && governmentText.Contains("TryCaptureNativeDecision", StringComparison.Ordinal)
                && governmentText.Contains("kingdom.RemoveDecision(decision)", StringComparison.Ordinal)
                && governmentText.Contains("DiplomacyExchangePrisoners", StringComparison.Ordinal)
                && !governmentText.Contains("ChangeClanInfluenceAction", StringComparison.Ordinal)
                && !governmentText.Contains("ChangePublicStanding", StringComparison.Ordinal)
                && liveHost.Contains("\"government\"", StringComparison.Ordinal)
                && liveHost.Contains("\"government_test\"", StringComparison.Ordinal)
                && serverLive.Contains("LiveTestModes", StringComparison.Ordinal)
                && serverLive.Contains("\"government\"", StringComparison.Ordinal)
                && serverLive.Contains("\"government_test\"", StringComparison.Ordinal)
                && File.Exists(manifestPath) && mcp.Contains("RequireArmedOwnedSaveAsync", StringComparison.Ordinal)
                && mcp.Contains("expectedSaveName", StringComparison.Ordinal)
                && !mcp.Contains("save_checkpoint", StringComparison.Ordinal)
                && !mcp.Contains("advance_time", StringComparison.Ordinal),
                "Government exposes individual hearing recommendations and ballots, explicit native capture, broad political-action coverage, and guarded native harness seams without Influence, Public Standing, inferred saves, or internal time/save control.",
                new Dictionary<string, object> { ["manifest"] = manifestPath, ["mcp"] = mcpPath,
                    ["clientLiveHost"] = liveHostPath, ["serverLiveTransport"] = serverLivePath });
            AddGovernmentHearingSelfTests(add);
            return rows;
        }
    }
}
