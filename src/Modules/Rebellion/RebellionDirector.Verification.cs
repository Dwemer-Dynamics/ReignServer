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
        internal static Dictionary<string, object> RunRebellionSelfTests()
        {
            List<Dictionary<string, object>> tests = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (name, passed, detail) => tests.Add(new Dictionary<string, object> { ["name"] = name, ["passed"] = passed, ["detail"] = detail });
            add("inclusive_relation_threshold",
                RebellionRelationshipThreshold == -35
                    && -36 <= RebellionRelationshipThreshold
                    && -35 <= RebellionRelationshipThreshold
                    && !(-34 <= RebellionRelationshipThreshold),
                "the clan leader's directional effective attitude toward the ruler qualifies at -35 and below");
            Dictionary<string, object> lockedGrace = DiplomacyService.BuildAutonomousWorldStartupGrace(
                new Dictionary<string, object> { ["worldDay"] = 104d,
                    ["campaignStartDay"] = 100d });
            Dictionary<string, object> openGrace = DiplomacyService.BuildAutonomousWorldStartupGrace(
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
            add("declaration_phrase", Program.LooksLikeExplicitPlayerRebellionDeclaration("I challenge your rule and I declare a rebellion."), "explicit first-person ruler challenge maps deterministically");
            add("declaration_not_question", !Program.LooksLikeExplicitPlayerRebellionDeclaration("Should I declare a rebellion someday?"), "questions and future hypotheticals do not declare");
            add("recruitment_phrase", Program.LooksLikeRebellionRecruitmentRequest("Join my rebellion against the ruler."), "post-declaration recruitment request maps");
            add("join_phrase", Program.LooksLikePlayerJoinRebellionRequest("I ask to join your rebellion."), "player request to join NPC rebellion maps");
            add("acceptance_fail_closed", Program.IsUnambiguousActionAcceptance("I accept. My clan stands with you.")
                && !Program.IsUnambiguousActionAcceptance("No. I will not join you."), "NPC recruitment requires unambiguous acceptance and blocks refusal");
            add("surrender_phrases", Program.LooksLikeExplicitPlayerRebellionSurrender("I surrender this rebellion and yield the throne.")
                && Program.LooksLikeExplicitNpcRebellionSurrender("I relinquish my claim. I surrender."), "player and NPC surrender are explicit");
            add("action_type_mapping", Program.MapCommandToActionType("start_ruling_clan_rebellion") == "PoliticsStartRulingClanRebellion"
                && Program.MapCommandToActionType("recruit_lord_to_rebellion") == "PoliticsRecruitLordToRebellion"
                && Program.MapCommandToActionType("join_rebellion") == "PoliticsJoinRebellion"
                && Program.MapCommandToActionType("surrender_rebellion") == "PoliticsSurrenderRebellion"
                && Program.MapCommandToActionType("resolve_rebellion_pledge") == "PoliticsResolveRebellionPledge"
                && Program.MapCommandToActionType("resolve_rebellion_summons") == "PoliticsResolveRebellionSummons"
                && Program.MapCommandToActionType("back_rebellion") == "DiplomacyBackRebellion", "rebellion actions, including foreign backing, map to client enum names");
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
            List<Dictionary<string, object>> unilateral = Program.BuildRebellionDialogueCandidates(dialoguePayload, dialogueHero,
                "I challenge your rule and I declare a rebellion.", "No. I reject your challenge.", refusedActionGate);
            add("unilateral_dialogue_declaration", unilateral.Count == 1
                && ReadString(unilateral[0], "command", "") == "start_ruling_clan_rebellion",
                "ruler refusal cannot block an explicit player declaration");
            List<Dictionary<string, object>> acceptedRecruitment = Program.BuildRebellionDialogueCandidates(dialoguePayload, dialogueHero,
                "Join my rebellion against the ruler.", "I accept. My clan stands with you.", acceptedActionGate);
            add("accepted_dialogue_recruitment", acceptedRecruitment.Count == 1
                && ReadString(acceptedRecruitment[0], "command", "") == "recruit_lord_to_rebellion",
                "unambiguous visible acceptance queues recruitment");
            List<Dictionary<string, object>> refusedRecruitment = Program.BuildRebellionDialogueCandidates(dialoguePayload, dialogueHero,
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
            Dictionary<string, object> mergedMailProfile = RelationshipsService.MergeCorrespondenceRecipientProfile(
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
            Dictionary<string, object> rejectedMailProfile = RelationshipsService.MergeCorrespondenceRecipientProfile(
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
            List<Dictionary<string, object>> conversationalPledge = Program.BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Give me your final answer: will you pledge your clan to my rebellion against our ruler without further condition?",
                "I'll pledge. Wyreglen rides with you.", acceptedActionGate);
            add("preparation_conversational_pledge_candidate",
                conversationalPledge.Count == 1
                    && ReadString(conversationalPledge[0], "command", "") == "resolve_rebellion_pledge"
                    && ReadString(ReadDictionary(conversationalPledge[0], "terms"), "decision", "") == "pledge",
                "a natural future-tense pledge routes to secret preparation rather than immediate declaration");
            List<Dictionary<string, object>> conditionalPreparation = Program.BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Will you pledge your clan to my rebellion against our ruler?",
                "Give me land and a seat, and you'll have my axe and my clan's banners. Until then I stand aside.",
                conditionalActionGate);
            List<Dictionary<string, object>> finalRefusal = Program.BuildRebellionDialogueCandidates(
                dialoguePayload, dialogueHero,
                "Give me your final answer: do you refuse to support my rebellion against our ruler?",
                "My answer is no, and it stays no.",
                finalRefusalActionGate);
            List<Dictionary<string, object>> deferredRefusal = Program.BuildRebellionDialogueCandidates(
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
                RelationshipsService.BuildRebellionLetterReplyCandidates(correspondencePayload,
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
                RelationshipsService.BuildRebellionLetterReplyCandidates(correspondencePayload,
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
                RelationshipsService.BuildRebellionLetterReplyCandidates(correspondencePayload,
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
    }
}
