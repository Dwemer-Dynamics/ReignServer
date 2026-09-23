using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunDialogueActionReliabilitySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, data) => results.Add(TestDict(
                "caseId", "action_reliability_" + id, "passed", pass, "summary", id, "data", data));
            var payload = ReadDictionary(DefaultActionTestSnapshot(), "payload");
            var hero = ReadDictionary(payload, "hero");
            payload["campaignCalendar"] = TestDict("daysPerSeason", 31.5, "daysPerWeek", 7, "daysPerYear", 126);
            var settings = new Dictionary<string, object>(LoadSettings()) { ["enableMinimeMemoryWorker"] = false };
            Func<string, Dictionary<string, object>> accepted = intent => TestDict("needed", true, "commitment", "accepted", "intent", intent);
            var allowed = new HashSet<string>(new[] { "accept_temporary_party_guest", "renew_temporary_party_guest", "end_temporary_party_guest" });

            foreach (string text in new[] { "Join my party for the season and travel beside us.",
                "Will you be my traveling companion?", "Come along with us.", "Travel with me.",
                "Serve as my scout for the season.", "Join us on the road." })
            {
                var gate = accepted("The speaker accepts temporary travel with the player.");
                var choices = BuildAllowedActionPlannerChoices(settings, payload, hero, gate, text, "I agree. Let us go.", 3);
                var fallback = BuildAcceptedTemporaryPartyGuestFallbackAction(gate, payload, hero, text, "I agree. Let us go.", allowed);
                add("companion_" + results.Count, choices.Any(x => ReadString(x, "command", "") == "accept_temporary_party_guest")
                    && fallback != null && ReadBool(ReadDictionary(fallback, "terms"), "consentConfirmed", false), choices);
            }
            var season = new Dictionary<string, object>();
            add("reign_season", TryCompleteAcceptedGuestSchedule(season, payload, "Join my party for the season")
                && ReadDouble(season, "durationDays", 0) == 31.5 && ReadBool(season, "startNow", false), season);
            var customCalendar = CloneDictionary(payload);
            customCalendar["campaignCalendar"] = TestDict("daysPerSeason", 28, "daysPerWeek", 7, "daysPerYear", 112);
            var custom = new Dictionary<string, object>();
            add("modded_native_season", TryCompleteAcceptedGuestSchedule(custom, customCalendar, "Join my party for one season")
                && ReadInt(custom, "durationDays", 0) == 28, custom);
            var deferred = new Dictionary<string, object>();
            add("ulagara_future_service", TryCompleteAcceptedGuestSchedule(deferred, payload,
                "An accepted service agreement: 500 denars per season to her father. I will return for you after the tournament.")
                && ReadBool(deferred, "deferStart", false) && ReadInt(deferred, "wageGold", 0) == 500
                && ReadDouble(deferred, "wagePeriodDays", 0) == 31.5 && ReadString(deferred, "wageRecipientRelation", "") == "father"
                && !deferred.ContainsKey("settlementIds") && !deferred.ContainsKey("startNow"), deferred);
            var spokenWage = new Dictionary<string, object>();
            add("captured_spoken_seasonal_wage", TryCompleteAcceptedGuestSchedule(spokenWage, payload,
                "Five hundred a season to my father. I will return for you after the tournament.")
                && ReadInt(spokenWage, "wageGold", 0) == 500 && ReadDouble(spokenWage, "wagePeriodDays", 0) == 31.5
                && ReadBool(spokenWage, "deferStart", false), spokenWage);
            var fractionalDays = new Dictionary<string, object>();
            add("fractional_duration_preserved", TryCompleteAcceptedGuestSchedule(fractionalDays, payload,
                "Join my party for 31.5 days") && ReadDouble(fractionalDays, "durationDays", 0) == 31.5, fractionalDays);
            foreach (string text in new[] { "Paid service at wages to be determined", "500 denars per season paid up front",
                "500 denars per season and 600 denars per season", "Join my party until the tournament is over", "Join my party for 127 days" })
                add("unresolved_service_" + results.Count, !TryCompleteAcceptedGuestSchedule(new Dictionary<string, object>(), payload, text), text);
            foreach (string commitment in new[] { "refused", "conditional", "considering", "roleplay_only" })
                add("companion_consent_" + commitment, TemporaryPartyGuestCandidateToPreserve(
                    TestDict("needed", true, "commitment", commitment, "intent", "Join my party"), "Travel with me.") == "", null);

            var together = accepted("travel_to_tournament: We will take a detour together if the others agree.");
            const string shared = "We could all go to Chaikand together if Temur agrees to one more day.";
            add("captured_shared_detour", IsSharedTravelOnly(together, shared), null);
            var mainPartyHero = CloneDictionary(hero);
            mainPartyHero["currentPartyId"] = "player_party";
            foreach (string command in SeparatePartyCommands)
                add("main_party_excluded_" + command, !DialoguePlannerCandidateEligible(command, payload, mainPartyHero, together, shared), null);
            foreach (string text in new[] { "Scouts know these roads.", "We travel together tomorrow.",
                "My brother will escort the caravan.", "*She describes a patrol route.* I like the trees.", "I don't want you to raid the village." })
                add("no_false_order_" + results.Count, !HasDirectCampaignOrderRequest(text), text);
            foreach (string text in new[] { "Patrol around Chaikand.", "Could you scout the route?", "I want you to escort that caravan.", "Please return home.",
                "Morwyn, keep an eye on Zeonica with your own men.", "Get your men together, then head to Zeonica.", "Call the lords and break the siege." })
                add("direct_order_" + results.Count, HasDirectCampaignOrderRequest(text), text);
            add("no_unearned_banishment", !DialoguePlannerCandidateEligible("acknowledge_own_faction_combat_risk",
                payload, hero, accepted("Employment under the player's banner"), "You will ride under my banner."), null);

            var speakerGoldGift = BuildAllowedActionPlannerChoices(settings, payload, hero,
                accepted("Catella gives 25 denars from her purse to the player."),
                "I need 25 denars; will you give them to me?", "Twenty-five. Here.", 3);
            add("speaker_gold_gift_uses_specialized_schema",
                speakerGoldGift.Any(x => ReadString(x, "command", "") == "give_gold_to_player")
                && !speakerGoldGift.Any(x => ReadString(x, "command", "") == "transfer_gold"),
                speakerGoldGift);

            string conditionalProbeReply = "I may do that after you clarify the earlier sum.";
            var conditionalProbeGate = NormalizeActionGate(TestDict(
                "needed", false, "commitment", "conditional",
                "intent", "Payment awaits clarification."),
                "Please pay Derthert 17 denars.", conditionalProbeReply);
            bool cooperationRepaired = EnforceActionAcceptanceCooperationGate(
                true, conditionalProbeGate, "Please pay Derthert 17 denars.",
                ref conditionalProbeReply);
            add("cooperation_override_makes_positive_probe_unconditional",
                cooperationRepaired && ActionGateShouldPlan(conditionalProbeGate)
                && ReadString(conditionalProbeGate, "commitment", "") == "accepted"
                && conditionalProbeReply.Contains("I agree", StringComparison.Ordinal),
                conditionalProbeGate);

            string acceptedProbeReply = "I agree and will pay Derthert now.";
            var acceptedProbeGate = NormalizeActionGate(TestDict(
                "needed", true, "commitment", "accepted",
                "intent", "Pay Derthert now."),
                "Please pay Derthert 17 denars.", acceptedProbeReply);
            add("cooperation_override_preserves_accepted_probe",
                !EnforceActionAcceptanceCooperationGate(true, acceptedProbeGate,
                    "Please pay Derthert 17 denars.", ref acceptedProbeReply)
                && acceptedProbeReply == "I agree and will pay Derthert now.",
                acceptedProbeGate);

            var movementAllowed = new HashSet<string>(new[]
            {
                "go_to_settlement",
                "patrol_around_settlement",
                "wait_near_settlement"
            });
            foreach (var movementCase in new[]
            {
                TestDict("command", "go_to_settlement", "text",
                    "Take your party to Lycaron."),
                TestDict("command", "go_to_settlement", "text",
                    "Head for Lycaron and bring your warband there."),
                TestDict("command", "patrol_around_settlement", "text",
                    "Keep a mounted patrol near Lycaron for me."),
                TestDict("command", "patrol_around_settlement", "text",
                    "Circle Lycaron and watch its approaches."),
                TestDict("command", "wait_near_settlement", "text",
                    "Hold your position near Lycaron until I call for you."),
                TestDict("command", "wait_near_settlement", "text",
                    "Remain in the vicinity of Lycaron and await further word.")
            })
            {
                string expected = ReadString(movementCase, "command", "");
                string text = ReadString(movementCase, "text", "");
                var movementGate = accepted(
                    "The speaker accepts the explicit party movement order now.");
                var fallback = BuildAcceptedPartyMovementFallbackAction(
                    movementGate, payload, hero, text, movementAllowed);
                add("accepted_party_movement_" + expected + "_" + results.Count,
                    AcceptedPartyMovementCandidateToPreserve(
                        movementGate, text) == expected
                    && fallback != null
                    && ReadString(fallback, "actorHeroStringId", "")
                        == ReadFirstString(payload, "speakerHeroStringId",
                            "heroStringId")
                    && ReadString(fallback, "TargetSettlement", "")
                        == "Lycaron",
                    fallback);
            }
            var mismatchedMovementCandidates = new List<Dictionary<string, object>>
            {
                TestDict("command", "patrol_around_settlement")
            };
            add("accepted_travel_removes_wrong_patrol_result",
                RemoveSupersededPartyMovementCandidates(
                    mismatchedMovementCandidates, "go_to_settlement")
                && mismatchedMovementCandidates.Count == 0,
                mismatchedMovementCandidates);
            var patrolCollisionChoices = BuildAllowedActionPlannerChoices(
                settings, payload, hero,
                accepted("Adram will circle Lycaron, then return to report."),
                "Circle Lycaron and watch its approaches.",
                "I will circle Lycaron and return to report.", 10);
            add("accepted_patrol_request_overrides_return_to_report_prose",
                patrolCollisionChoices.Any(choice => string.Equals(
                    CanonicalCommand(ReadString(choice, "command", "")),
                    "patrol_around_settlement", StringComparison.OrdinalIgnoreCase))
                && !patrolCollisionChoices.Any(choice => string.Equals(
                    CanonicalCommand(ReadString(choice, "command", "")),
                    "go_to_settlement", StringComparison.OrdinalIgnoreCase)),
                patrolCollisionChoices);
            var followAllowed = new HashSet<string>(new[]
            {
                "follow", "stop_following"
            });
            foreach (var followCase in new[]
            {
                TestDict("command", "follow", "text",
                    "Adram, follow my party on the campaign map."),
                TestDict("command", "follow", "text",
                    "Adram, escort us while we travel."),
                TestDict("command", "follow", "text",
                    "Adram, stay behind our party on the road and keep to our route."),
                TestDict("command", "follow", "text",
                    "Adram, stay close and follow me through this room."),
                TestDict("command", "follow", "text",
                    "Adram, walk with me and stay by my side wherever I go."),
                TestDict("command", "stop_following", "text",
                    "Adram, break off the escort and go your own way."),
                TestDict("command", "stop_following", "text",
                    "Adram, no need to trail us any longer; hold here.")
            })
            {
                string expected = ReadString(followCase, "command", "");
                string text = ReadString(followCase, "text", "");
                var followGate = accepted(
                    "The speaker accepts the explicit map party order now.");
                var fallback = BuildAcceptedPartyMovementFallbackAction(
                    followGate, payload, hero, text, followAllowed);
                bool targetOk = expected == "stop_following"
                    || ReadString(fallback, "targetHeroStringId", "")
                        == ReadFirstString(payload, "playerHeroStringId",
                            "mainHeroStringId", "playerId");
                add("accepted_map_follow_" + expected + "_" + results.Count,
                    AcceptedPartyMovementCandidateToPreserve(
                        followGate, text) == expected
                    && fallback != null
                    && ReadString(fallback, "actorHeroStringId", "")
                        == ReadFirstString(payload, "speakerHeroStringId",
                            "heroStringId")
                    && targetOk,
                    fallback);
            }
            var followGuestCollisionChoices = BuildAllowedActionPlannerChoices(
                settings, payload, hero,
                accepted("Adram agrees to ride with the player's column."),
                "Adram, follow my party on the campaign map.",
                "I will ride with you and join your column.", 10);
            add("accepted_follow_request_overrides_temporary_guest_prose",
                followGuestCollisionChoices.Any(choice => string.Equals(
                    CanonicalCommand(ReadString(choice, "command", "")),
                    "follow", StringComparison.OrdinalIgnoreCase))
                && !followGuestCollisionChoices.Any(choice => string.Equals(
                    CanonicalCommand(ReadString(choice, "command", "")),
                    "accept_temporary_party_guest",
                    StringComparison.OrdinalIgnoreCase)),
                followGuestCollisionChoices);
            foreach (string text in new[]
            {
                "Do not stop following us yet.",
                "Don't stop escorting us yet.",
                "Never cease trailing my party.",
                "Do not break off the escort."
            })
            {
                var negatedStopChoices = new List<Dictionary<string, object>>
                {
                    TestDict("command", "stop_following"),
                    TestDict("command", "follow")
                };
                bool removedNegatedChoices =
                    RemoveAllPartyMovementCandidates(negatedStopChoices);
                add("negated_stop_following_creates_no_movement_action_"
                        + results.Count,
                    NegatesPartyMovementStateChange(text)
                    && AcceptedPartyMovementCandidateToPreserve(
                        accepted("Adram agrees to keep following."), text) == ""
                    && BuildAcceptedPartyMovementFallbackAction(
                        accepted("Adram agrees to keep following."), payload,
                        hero, text, followAllowed) == null
                    && removedNegatedChoices
                    && negatedStopChoices.Count == 0,
                    negatedStopChoices);
            }
            var narrationMovementGate = TestDict("needed", false,
                "commitment", "roleplay_only",
                "intent", "Recall a past journey.");
            add("travel_narration_does_not_create_movement_fallback",
                AcceptedPartyMovementCandidateToPreserve(
                    narrationMovementGate,
                    "I remember when we both went to Lycaron last spring.") == ""
                && BuildAcceptedPartyMovementFallbackAction(
                    narrationMovementGate, payload, hero,
                    "I remember when we both went to Lycaron last spring.",
                    movementAllowed) == null,
                null);

            foreach (string text in new[]
            {
                "Rhagaea, transfer 21 denars from your purse to Derthert.",
                "Please pay Derthert 17 denars.",
                "Send 13 denars from your own funds to Derthert."
            })
            {
                var thirdPartyGold = BuildAllowedActionPlannerChoices(settings, payload, hero,
                    accepted("Rhagaea transfers denars from her purse to Derthert."),
                    text, "I agree. I will pay Derthert.", 3);
                add("named_third_party_gold_uses_generic_schema_" + results.Count,
                    thirdPartyGold.Any(x => ReadString(x, "command", "") == "transfer_gold")
                    && !thirdPartyGold.Any(x => ReadString(x, "command", "") == "give_gold_to_player"),
                    thirdPartyGold);
            }

            var clanAllowed = new HashSet<string> { "join_clan" };
            foreach (string text in new[]
            {
                "Achaku, leave Baltait and join my clan, fen Domus.",
                "Would you become a member of my clan, Adalindis?",
                "Alcaea, enter fen Domus as one of our own."
            })
            {
                var clanGate = accepted("The speaker accepts membership under the player's banner.");
                var clanFallback = BuildAcceptedClanMembershipFallbackAction(
                    clanGate, payload, hero, text, clanAllowed);
                add("accepted_clan_wording_" + results.Count,
                    AcceptedClanMembershipCandidateToPreserve(clanGate, text) == "join_clan"
                    && clanFallback != null
                    && ReadString(clanFallback, "actorHeroStringId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                    && ReadString(clanFallback, "targetClanStringId", "") == ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                    clanFallback);
            }
            var roleplayClanGate = TestDict("needed", false, "commitment", "roleplay_only",
                "intent", "Continue the conversation naturally.");
            bool promotedClanGate = PromoteAcceptedClanMembershipGate(roleplayClanGate, payload, hero,
                "Hulara, join my clan as one of our own.",
                "I accept. I stand beneath your banner as a member of your clan.");
            add("roleplay_only_clan_acceptance_is_promoted",
                promotedClanGate && ActionGateShouldPlan(roleplayClanGate)
                && AcceptedClanMembershipCandidateToPreserve(roleplayClanGate,
                    "Hulara, join my clan as one of our own.") == "join_clan", roleplayClanGate);
            foreach (var rejectedPromotion in new[]
            {
                new { Player = "Join my party as a temporary guest and scout.", Reply = "I accept. I will travel beneath your banner for now." },
                new { Player = "Wear my clan colors as a disguise.", Reply = "I accept the disguise." },
                new { Player = "Join my clan if your father agrees.", Reply = "Perhaps later, if he approves." },
                new { Player = "Join my clan as one of our own.", Reply = "No. I will not join your clan." }
            })
            {
                var rejectedGate = TestDict("needed", false, "commitment", "roleplay_only", "intent", "Talk only.");
                add("roleplay_clan_promotion_rejects_nonfinal_" + results.Count,
                    !PromoteAcceptedClanMembershipGate(rejectedGate, payload, hero,
                        rejectedPromotion.Player, rejectedPromotion.Reply)
                    && !ActionGateShouldPlan(rejectedGate), rejectedGate);
            }
            var intoxicatedPayload = CloneDictionary(payload);
            intoxicatedPayload["intoxicationReceipt"] = TestDict("suppressedCommitments", true);
            var intoxicatedClanGate = TestDict("needed", false, "commitment", "roleplay_only", "intent", "Talk only.");
            add("intoxication_blocks_clan_gate_promotion",
                !PromoteAcceptedClanMembershipGate(intoxicatedClanGate, intoxicatedPayload, hero,
                    "Join my clan as one of our own.", "I accept membership in your clan.")
                && !ActionGateShouldPlan(intoxicatedClanGate), intoxicatedClanGate);
            var clanLeaveAllowed = new HashSet<string> { "leave_clan" };
            foreach (string text in new[]
            {
                "Achaku, leave Baltait and stand without a clan.",
                "Adalindis, withdraw from your current clan now.",
                "Alcaea, renounce your clan membership and become clanless."
            })
            {
                var clanGate = accepted("The speaker accepts leaving their current clan now.");
                var clanFallback = BuildAcceptedClanMembershipFallbackAction(
                    clanGate, payload, hero, text, clanLeaveAllowed);
                add("accepted_leave_clan_wording_" + results.Count,
                    AcceptedClanMembershipCandidateToPreserve(clanGate, text) == "leave_clan"
                    && clanFallback != null
                    && ReadString(clanFallback, "command", "") == "leave_clan"
                    && ReadString(clanFallback, "actorHeroStringId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                    && string.IsNullOrWhiteSpace(ReadString(clanFallback, "targetClanStringId", "")),
                    clanFallback);
            }
            var competingClanCandidates = new List<Dictionary<string, object>>
            {
                TestDict("command", "leave_clan"),
                TestDict("command", "join_clan")
            };
            add("accepted_join_clan_suppresses_intermediate_leave_clan",
                RemoveSupersededClanMembershipCandidates(competingClanCandidates, "join_clan")
                && competingClanCandidates.Count == 1
                && ReadString(competingClanCandidates[0], "command", "") == "join_clan",
                competingClanCandidates);
            var mismatchedClanCandidates = new List<Dictionary<string, object>>
            {
                TestDict("command", "leave_clan")
            };
            add("accepted_join_clan_removes_leave_only_planner_result",
                RemoveSupersededClanMembershipCandidates(mismatchedClanCandidates, "join_clan")
                && mismatchedClanCandidates.Count == 0,
                mismatchedClanCandidates);
            var competingLeaveCandidates = new List<Dictionary<string, object>>
            {
                TestDict("command", "join_clan"),
                TestDict("command", "leave_clan")
            };
            add("accepted_leave_clan_suppresses_join_clan",
                RemoveSupersededClanMembershipCandidates(competingLeaveCandidates, "leave_clan")
                && competingLeaveCandidates.Count == 1
                && ReadString(competingLeaveCandidates[0], "command", "") == "leave_clan",
                competingLeaveCandidates);
            var joined = TestDict("command", "join_clan");
            CompleteConversationActionTerms(joined, payload, hero, "Join my clan.");
            add("join_clan_direction_is_speaker_to_player_clan",
                ReadString(joined, "actorHeroId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                && ReadString(joined, "actorClanId", "") == ReadFirstString(payload, "speakerClanId", "targetClanId", "targetClanStringId")
                && ReadString(joined, "targetClanId", "") == ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId"),
                joined);
            var kingdomAllowed = new HashSet<string> { "join_kingdom" };
            foreach (string text in new[]
            {
                "Abalytos, bring Prienicos into my kingdom and swear your clan to my realm.",
                "Achios, will Corenios join my kingdom now?",
                "Adram, pledge Banu Sarran and your whole clan to my realm."
            })
            {
                var kingdomGate = accepted("The speaker accepts defection and joining the player's kingdom now.");
                var kingdomFallback = BuildAcceptedKingdomMembershipFallbackAction(
                    kingdomGate, payload, hero, text, kingdomAllowed);
                add("accepted_join_kingdom_wording_" + results.Count,
                    AcceptedKingdomMembershipCandidateToPreserve(kingdomGate, text) == "join_kingdom"
                    && kingdomFallback != null
                    && ReadString(kingdomFallback, "actorHeroStringId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                    && ReadString(kingdomFallback, "actorClanStringId", "") == ReadFirstString(payload, "speakerClanId", "npcClanId")
                    && ReadString(kingdomFallback, "targetKingdomStringId", "") == ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                    kingdomFallback);
            }
            var competingKingdomCandidates = new List<Dictionary<string, object>>
            {
                TestDict("command", "join_clan"),
                TestDict("command", "leave_kingdom"),
                TestDict("command", "join_kingdom")
            };
            add("accepted_join_kingdom_suppresses_personal_and_intermediate_membership",
                RemoveSupersededKingdomMembershipCandidates(competingKingdomCandidates, "join_kingdom")
                && competingKingdomCandidates.Count == 1
                && ReadString(competingKingdomCandidates[0], "command", "") == "join_kingdom",
                competingKingdomCandidates);
            var joinedKingdom = TestDict("command", "join_kingdom");
            CompleteConversationActionTerms(joinedKingdom, payload, hero,
                "Bring your clan into my kingdom.");
            add("join_kingdom_direction_is_speaker_clan_to_player_kingdom",
                ReadString(joinedKingdom, "actorHeroId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                && ReadString(joinedKingdom, "actorClanId", "") == ReadFirstString(payload, "speakerClanId", "npcClanId")
                && ReadString(joinedKingdom, "targetKingdomId", "") == ReadFirstString(payload, "playerKingdomId", "actorKingdomId", "actorKingdomStringId"),
                joinedKingdom);
            var leaveKingdomAllowed = new HashSet<string> { "leave_kingdom" };
            foreach (string text in new[]
            {
                "Abalytos, take Prienicos out of the Southern Empire and make your clan independent.",
                "Achios, will Corenios leave the Western Empire now?",
                "Adram, break Banu Sarran's allegiance to the Aserai and leave that kingdom."
            })
            {
                var leaveKingdomGate = accepted("The speaker accepts breaking allegiance and leaving their kingdom now.");
                var leaveKingdomFallback = BuildAcceptedKingdomMembershipFallbackAction(
                    leaveKingdomGate, payload, hero, text, leaveKingdomAllowed);
                add("accepted_leave_kingdom_wording_" + results.Count,
                    AcceptedKingdomMembershipCandidateToPreserve(leaveKingdomGate, text) == "leave_kingdom"
                    && leaveKingdomFallback != null
                    && ReadString(leaveKingdomFallback, "actorHeroStringId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                    && ReadString(leaveKingdomFallback, "actorClanStringId", "") == ReadFirstString(payload, "speakerClanId", "npcClanId")
                    && string.IsNullOrWhiteSpace(ReadFirstString(leaveKingdomFallback,
                        "targetKingdomId", "targetKingdomStringId", "TargetKingdom")),
                    leaveKingdomFallback);
            }
            var leftKingdom = TestDict("command", "leave_kingdom", "targetKingdomId", "wrong_player_kingdom");
            CompleteConversationActionTerms(leftKingdom, payload, hero,
                "Leave your current kingdom and make your clan independent.");
            add("leave_kingdom_direction_is_speaker_clan_out_of_speaker_kingdom",
                ReadString(leftKingdom, "actorHeroId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                && ReadString(leftKingdom, "actorClanId", "") == ReadFirstString(payload, "speakerClanId", "npcClanId")
                && ReadString(leftKingdom, "targetClanId", "") == ReadFirstString(payload, "speakerClanId", "npcClanId")
                && ReadString(leftKingdom, "actorKingdomId", "") == ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId")
                && string.IsNullOrWhiteSpace(ReadFirstString(leftKingdom,
                    "targetKingdomId", "targetKingdomStringId", "TargetKingdom")),
                leftKingdom);
            var playerServiceAllowed = new HashSet<string>(new[]
            {
                "hire_player_as_mercenary", "dismiss_player_mercenary",
                "offer_player_vassalage", "dismiss_player_vassal"
            });
            foreach (var serviceCase in new[]
            {
                TestDict("command", "hire_player_as_mercenary", "text",
                    "Derthert, hire me and fen Domus as mercenaries for Vlandia."),
                TestDict("command", "dismiss_player_mercenary", "text",
                    "Derthert, release fen Domus from Vlandia's mercenary contract now."),
                TestDict("command", "offer_player_vassalage", "text",
                    "Derthert, accept me and my clan as sworn vassals of Vlandia."),
                TestDict("command", "dismiss_player_vassal", "text",
                    "Derthert, release my clan from our vassal oath to Vlandia.")
            })
            {
                string expected = ReadString(serviceCase, "command", "");
                string text = ReadString(serviceCase, "text", "");
                var serviceGate = accepted("The sovereign accepts the player's requested service change now.");
                var fallback = BuildAcceptedPlayerServiceFallbackAction(
                    serviceGate, payload, hero, text, playerServiceAllowed);
                add("accepted_player_service_" + expected,
                    AcceptedPlayerServiceCandidateToPreserve(serviceGate, text) == expected
                    && fallback != null
                    && ReadString(fallback, "actorHeroStringId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                    && ReadString(fallback, "targetClanStringId", "") == ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId")
                    && ReadString(fallback, "targetKingdomStringId", "") == ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                    fallback);
            }
            var mercenaryContractGate = accepted(
                "Derthert accepts hiring fen Domus as paid mercenaries under Vlandia's banner.");
            const string mercenaryContractText =
                "Derthert, offer us a mercenary contract under Vlandia's banner.";
            string acceptedMercenaryContract = AcceptedPlayerServiceCandidateToPreserve(
                mercenaryContractGate, mercenaryContractText);
            string lowerPriorityClanMembership = string.IsNullOrWhiteSpace(acceptedMercenaryContract)
                ? AcceptedClanMembershipCandidateToPreserve(
                    mercenaryContractGate, mercenaryContractText)
                : "";
            add("accepted_player_service_precedes_clan_banner_wording",
                acceptedMercenaryContract == "hire_player_as_mercenary"
                && string.IsNullOrWhiteSpace(lowerPriorityClanMembership),
                TestDict("playerService", acceptedMercenaryContract,
                    "clanMembership", lowerPriorityClanMembership));
            var dismissedMercenary = TestDict("command", "dismiss_player_mercenary",
                "actorKingdomId", "wrong_player_kingdom", "targetKingdomId", "wrong_player_kingdom");
            CompleteConversationActionTerms(dismissedMercenary, payload, hero,
                "Release my clan from your mercenary contract now.");
            add("player_service_direction_is_speaker_kingdom_to_player_clan",
                ReadString(dismissedMercenary, "actorHeroId", "") == ReadFirstString(payload, "speakerHeroStringId", "heroStringId")
                && ReadString(dismissedMercenary, "targetClanId", "") == ReadFirstString(payload, "playerClanId", "actorClanId", "actorClanStringId")
                && ReadString(dismissedMercenary, "actorKingdomId", "") == ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId")
                && ReadString(dismissedMercenary, "targetKingdomId", "") == ReadFirstString(payload, "speakerKingdomId", "targetKingdomId", "targetKingdomStringId"),
                dismissedMercenary);
            foreach (string commitment in new[] { "refused", "conditional", "roleplay_only" })
            {
                var clanGate = TestDict("needed", true, "commitment", commitment,
                    "intent", "The speaker might join the player's clan.");
                add("clan_fallback_requires_" + commitment,
                    AcceptedClanMembershipCandidateToPreserve(clanGate, "Join my clan.") == ""
                    && BuildAcceptedClanMembershipFallbackAction(
                        clanGate, payload, hero, "Join my clan.", clanAllowed) == null, null);
            }

            var compactLivePayload = CloneDictionary(payload);
            compactLivePayload["actionResolutionIndex"] = TestDict("version", "2", "heroesCount", 4275);
            var compactLiveHero = ReadDictionary(compactLivePayload, "hero");
            compactLiveHero["spouseName"] = "Derthert";
            foreach (string text in new[]
            {
                "Rhagaea, transfer 21 denars from your purse to your husband Derthert.",
                "Please pay Derthert 17 denars."
            })
            {
                var thirdPartyGold = BuildAllowedActionPlannerChoices(settings, compactLivePayload, compactLiveHero,
                    accepted("Rhagaea transfers denars from her purse to her husband Derthert."),
                    text, "I agree. I will pay Derthert.", 3);
                add("compact_live_named_third_party_gold_" + results.Count,
                    thirdPartyGold.Any(x => ReadString(x, "command", "") == "transfer_gold")
                    && !thirdPartyGold.Any(x => ReadString(x, "command", "") == "give_gold_to_player"),
                    thirdPartyGold);
            }

            var catalog = ReadDictionaryList(ActionCatalog(), "commands");
            add("registered_catalog_count", catalog.Count == 92, catalog.Count);
            foreach (var entry in catalog)
            {
                string command = ReadString(entry, "command", "");
                var blank = TestDict("command", command, "terms", new Dictionary<string, object>());
                CompleteConversationActionTerms(blank, payload, hero, "We have an agreement.");
                var blankTerms = ReadDictionary(blank, "terms");
                var invented = new[] { "gold", "amount", "dailyTribute", "reparationsGold", "durationDays", "settlementIds", "workshopId" }
                    .Where(blankTerms.ContainsKey).ToList();
                add("no_test_values_" + command, invented.Count == 0, invented);
                var precise = TestDict("command", command, "TargetSettlement", "Explicit destination", "terms",
                    TestDict("gold", 437, "amount", 2, "durationDays", 17, "itemModifierId", "fine", "delivery", "personal"));
                CompleteConversationActionTerms(precise, payload, hero, "We discussed Akkalat, but the exact agreement is already recorded.");
                var terms = ReadDictionary(precise, "terms");
                add("exact_terms_" + command, ReadInt(terms, "gold", 0) == 437 && ReadInt(terms, "amount", 0) == 2
                    && ReadInt(terms, "durationDays", 0) == 17 && ReadString(terms, "itemModifierId", "") == "fine"
                    && ReadString(precise, "TargetSettlement", "") == "Explicit destination"
                    && MapCommandToActionType(command) == ReadString(entry, "mapsTo", "")
                    && PlannerCanExposeAction(command, false), terms);
            }
            var gift = TestDict("command", "transfer_item", "terms", TestDict("delivery", "equip", "itemId", "aserai_armor_02_b", "amount", 1));
            BindAcceptedItemDelivery(gift, "fixture_temurtai");
            add("equipment_acceptance_reaches_native", ReadString(gift, "acceptedByHeroStringId", "") == "fixture_temurtai"
                && ReadBool(ReadDictionary(gift, "terms"), "recipientConsentConfirmed", false), gift);
            add("queued_is_not_executed", ClassifyDialogueQueueOutcome(1, 1, true, new List<string>()) == "queued_awaiting_native", null);
            add("queued_with_fallback_warning", ClassifyDialogueQueueOutcome(2, 1, true,
                new List<string> { "An earlier fallback had no match." }) == "queued_awaiting_native", null);
            add("empty_actionable_is_failure", ClassifyDialogueQueueOutcome(0, 0, true, new List<string>()) == "no_executable_action", null);
            add("empty_candidate_errors_survive", ClassifyDialogueQueueOutcome(0, 0, true, new List<string> { "A required item is missing" }) == "missing_required_field", null);

            var incomplete = TestDict("command", "transfer_gold", "FromHero", "player", "ToHero", "rhagaea");
            var resolved = CloneDictionary(incomplete);
            resolved["terms"] = TestDict("gold", 437);
            add("incomplete_guess_needs_planner", ExecutableRouterCandidates(
                new List<Dictionary<string, object>> { incomplete }, "test_campaign", payload, "Give her some gold.").Count == 0, null);
            add("resolved_terms_are_executable", ExecutableRouterCandidates(
                new List<Dictionary<string, object>> { resolved }, "test_campaign", payload, "Give her 437 gold.").Count == 1, null);
            var preferred = PreferResolvedRouterCandidates(new List<Dictionary<string, object>> { resolved },
                new List<Dictionary<string, object>> { incomplete });
            add("semantic_terms_win_deduplication", preferred.Count == 1
                && ReadInt(ReadDictionary(preferred[0], "terms"), "gold", 0) == 437, preferred);
            CompleteConversationActionTerms(resolved, payload, hero, "We discussed 9000 gold earlier, but that offer changed.");
            add("old_offer_cannot_overwrite_agreement", ReadInt(ReadDictionary(resolved, "terms"), "gold", 0) == 437
                && !resolved.ContainsKey("GoldAmount"), resolved);

            var inventoryRows = Enumerable.Range(0, 40).Select(i => TestDict("itemId", "fixture_" + i,
                "name", "Fixture " + i, "count", 1)).ToList();
            inventoryRows.Add(TestDict("itemId", "pernach", "name", "Pernach", "itemModifierId", "fine", "count", 1));
            var itemIndex = TestDict("assets", TestDict("player", TestDict("inventory",
                TestDict("items", inventoryRows, "topItems", inventoryRows.Take(32).ToList()))));
            var itemTerms = new Dictionary<string, object>();
            ResolveItemReference(itemIndex, TestDict("Item", "Pernach"), itemTerms, new List<Dictionary<string, object>>());
            add("gift_below_inventory_display_cutoff", ReadString(itemTerms, "itemId", "") == "pernach"
                && ReadString(itemTerms, "itemModifierId", "") == "fine", itemTerms);
            var giftPayload = CloneDictionary(payload);
            giftPayload["actionResolutionIndex"] = CloneDictionary(ReadDictionary(payload, "actionResolutionIndex"));
            ReadDictionary(giftPayload, "actionResolutionIndex")["assets"] = ReadDictionary(itemIndex, "assets");
            const string capturedGift = "Here is the pernach I won, would you like it?";
            var giftGate = accepted("The speaker accepts the pernach as a gift.");
            var recoveredGift = BuildAcceptedNamedGiftFallback(giftGate, giftPayload, hero, capturedGift,
                new HashSet<string> { "transfer_item" });
            add("captured_pernach_recovers_empty_planner", recoveredGift != null
                && ReadString(ReadDictionary(recoveredGift, "terms"), "itemId", "") == "pernach"
                && ReadInt(ReadDictionary(recoveredGift, "terms"), "amount", 0) == 1
                && ReadString(recoveredGift, "FromHero", "") == "player" && ReadString(recoveredGift, "ToHero", "") == "rhagaea", recoveredGift);
            foreach (string commitment in new[] { "refused", "conditional", "roleplay_only" })
                add("gift_fallback_requires_" + commitment, BuildAcceptedNamedGiftFallback(
                    TestDict("needed", true, "commitment", commitment, "intent", "Accepts a gift"), giftPayload, hero,
                    capturedGift, new HashSet<string> { "transfer_item" }) == null, null);
            inventoryRows.Add(TestDict("itemId", "pernach", "name", "Pernach", "itemModifierId", "rusty", "count", 1));
            itemTerms = new Dictionary<string, object>();
            ResolveItemReference(itemIndex, TestDict("Item", "Pernach"), itemTerms, new List<Dictionary<string, object>>());
            add("duplicate_copy_needs_native_resolution", ReadString(itemTerms, "itemId", "") == "pernach"
                && !itemTerms.ContainsKey("itemModifierId"), itemTerms);
            inventoryRows.Add(TestDict("itemId", "other_pernach", "name", "Pernach", "count", 1));
            itemTerms = new Dictionary<string, object>();
            ResolveItemReference(itemIndex, TestDict("Item", "Pernach"), itemTerms, new List<Dictionary<string, object>>());
            add("ambiguous_item_name_is_not_guessed", !itemTerms.ContainsKey("itemId"), itemTerms);
            add("ambiguous_armor_is_not_most_expensive", ResolveSemanticItemReference("armor",
                new List<Dictionary<string, object>> { TestDict("itemId", "a", "isArmorOrClothing", true, "value", 1),
                    TestDict("itemId", "b", "isArmorOrClothing", true, "value", 9000) }) == null, null);
            foreach (double value in new[] { double.NaN, double.PositiveInfinity, 0, -1, 500.5 })
            {
                var errors = new List<string>();
                ValidateGuestServiceTerms(TestDict("wageGold", value, "wagePeriodDays", 21), errors);
                add("invalid_wage_" + results.Count, errors.Count > 0, null);
            }
            return results;
        }
    }
}
