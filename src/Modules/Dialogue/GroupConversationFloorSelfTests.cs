using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Reign.Core.Contracts.Dialogue;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunGroupConversationFloorSelfTests()
        {
            var tests = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => tests.Add(TestDict("caseId", "group_floor_" + id,
                "suite", "interaction_architecture", "passed", pass, "summary", id, "data", data));
            var roster = new[] {
                new GroupTurnParticipant { Id = "arven", Name = "Arven" },
                new GroupTurnParticipant { Id = "lysa", Name = "Lysa the Elder" },
                new GroupTurnParticipant { Id = "taren", Name = "Taren" }
            };
            GroupTurnPlan Plan(string text, string seed = "one", IEnumerable<GroupTurnLine> lines = null) => GroupConversationTurnOrder.Plan(roster, text, seed, lines);
            foreach (string text in new[] { "Only Arven, tell me.", "Nobody needs to answer.", "Lysa, please stay quiet.",
                "The river is quiet today.", "What do you think?", "*Arven and I enter the keep hall together*", "*I silently think.*", "" })
            {
                var opportunity = Plan(text);
                Check("full_roster_opportunities_" + PromptHash(text).Substring(0, 6), opportunity.ResponseOrder.Count == 3
                    && opportunity.ResponseOrder.Distinct().Count() == 3);
            }
            Check("named_recipient_first_without_exclusion", Plan("Only Taren, tell me.").ResponseOrder.First() == "taren");
            Check("public_mention_not_invitation", !Plan("I spoke with Taren about Arven yesterday.").AddressedIds.Any());
            Check("quoted_address_not_invitation", !Plan("He said \"Arven, answer me.\" What a demand.").AddressedIds.Any());
            Check("private_name_not_invitation", !Plan("*I silently remember Taren.*").AddressedIds.Any());
            Check("group_mention_not_invitation", !Plan("Everyone heard that yesterday.").AddressedIds.Any());
            Check("everyone_address_keeps_full_audience", Plan("Everyone, tell me your view.").AddressedIds.Count == 3);
            Check("named_wait_orders_recipient", Plan("*I wait for Taren to respond to that.*").ResponseOrder.First() == "taren");
            foreach (string privateText in new[] { "*I imagine waiting for Taren to respond.*", "*If I wait for Taren to respond, perhaps she will.*",
                "He said \"*I wait for Taren to respond.*\"", "*I look at Taren in my imagination.*" })
                Check("private_wait_not_invitation_" + PromptHash(privateText).Substring(0, 6), !Plan(privateText).AddressedIds.Any());
            var prior = new[] { new GroupTurnLine { SpeakerId = "arven", Role = "npc", Text = "I would start at the river." } };
            Check("followup_prioritizes_partner", Plan("Why there?", lines: prior).ResponseOrder.First() == "arven");
            Check("retry_roster_order_independent", Plan("What worries you?", "same", prior).ResponseOrder.SequenceEqual(
                GroupConversationTurnOrder.Plan(roster.Reverse(), "What worries you?", "same", prior).ResponseOrder));
            Check("focus_orders_but_does_not_exclude", GroupConversationTurnOrder.Plan(roster,
                "A wide world awaits us.", "focus", replyFocusId: "taren").ResponseOrder.First() == "taren");
            Check("foreign_focus_not_added", GroupConversationTurnOrder.Plan(roster,
                "A wide world awaits us.", "focus", replyFocusId: "foreign").ResponseOrder.Count == 3);

            string campaign = "group_floor_" + Guid.NewGuid().ToString("N");
            try
            {
                var profiles = roster.Select(p => TestDict("heroStringId", p.Id, "name", p.Name)).ToList();
                var payload = TestDict("campaignId", campaign, "conversationSessionId", "floor-session", "eventId", "floor-event",
                    "sceneTurnId", "floor-1", "turnId", "floor-1", "timelineId", "floor-timeline", "worldDay", 1d,
                    "playerHeroStringId", "player", "playerName", "Player", "playerText", "Arven, what is your view?",
                    "activeHeroIds", roster.Select(p => p.Id).ToList(), "attendees", profiles, "mode", "party_chat");
                var first = GroupConversationFloorApi(payload);
                Check("server_order_defers_participation_to_writer", ReadBool(first, "ok", false) && ReadInt(first, "providerCalls", -1) == 0
                    && ReadString(first, "policy", "") == "llm_contextual" && ReadStringList(first, "responseOrder").Count == 3
                    && !first.ContainsKey("speakingChances") && !first.ContainsKey("listeningHeroStringIds"));
                var replay = GroupConversationFloorApi(payload);
                Check("retry_freezes_order", ReadBool(replay, "replayed", false)
                    && ReadStringList(first, "responseOrder").SequenceEqual(ReadStringList(replay, "responseOrder")));
                UpdateSharedGroupConversationState(campaign, payload, "arven", "Arven", "I would begin at the river.", "floor-session", 10, "floor-accepted");
                Check("accepted_contribution_keeps_floor_cache", ReadBool(GroupConversationFloorApi(payload), "replayed", false));
                var changed = CloneDictionary(payload); changed["playerText"] = "A different request with the same turn id.";
                Check("changed_retry_input_rejected", !ReadBool(GroupConversationFloorApi(changed), "ok", true));
                payload = CloneDictionary(payload);
                payload["sceneTurnId"] = "floor-2"; payload["turnId"] = "floor-2"; payload["contributionId"] = "floor-2";
                payload["playerText"] = "Only Arven, tell me your view.";
                payload["conversationRelationshipChangesEnabled"] = false;
                payload["idleCounters"] = TestDict("lysa", 2, "taren", 2);
                payload["participantRequests"] = roster.Select(p => TestDict("speakerHeroStringId", p.Id, "heroStringId", p.Id)).ToList();
                var eventPreflight = SocialEventTurnPlanApi(payload);
                Check("event_preflight_fixes_floor_without_provider", ReadBool(eventPreflight, "ok", false)
                    && ReadInt(eventPreflight, "providerCalls", -1) == 0
                    && ReadStringList(eventPreflight, "responseOrder").Count == 3
                    && ReadStringList(eventPreflight, "responseOrder").First() == "arven");
                int calls = 0; bool fullAudience = false;
                var result = SocialEventTurnWithResponder(payload, request => { calls++;
                    fullAudience = ReadStringList(request, "activeHeroIds").Count == 3;
                    bool speak = ReadString(request, "speakerHeroStringId", "") == "arven";
                    return TestDict("ok", true, "reply", speak ? "I would begin at the river." : "", "participation", speak ? "speak" : "quiet"); });
                Check("event_every_present_writer_can_choose_silence", calls == 3 && ReadBool(result, "completedExchange", false), result);
                Check("quiet_event_listeners_are_not_removed", fullAudience && ReadStringList(result, "wanderedHeroStringIds").Count == 0
                    && ReadInt(ReadDictionary(result, "nextIdleCounters"), "taren", -1) == 0);
                Check("event_retry_no_additional_writer", ReadBool(SocialEventTurnWithResponder(payload, _ => throw new InvalidOperationException("Unexpected retry call")), "idempotent", false));

                var quiet = CloneDictionary(payload);
                quiet["sceneTurnId"] = "floor-quiet"; quiet["turnId"] = "floor-quiet"; quiet["contributionId"] = "floor-quiet";
                quiet["playerText"] = "Nobody needs to answer. The ferry leaves at dawn. *I silently remember SECRET-901.*";
                quiet["speakerHeroStringId"] = "taren";
                Check("party_route_does_not_synthesize_quiet", ApplyGroupConversationFloorToSpeaker(quiet) == null);
                string quietPrompt = BuildSharedGroupConversationPrompt(campaign, quiet, "taren", new List<Dictionary<string, object>>(), ReadString(quiet, "playerText", ""));
                Check("writer_prompt_owns_contextual_participation", quietPrompt.Contains(GroupConversationParticipationGuidance)
                    && quietPrompt.Contains("Queue position, an empty reply or a pending request never establishes an answer."));
                var outsider = CloneDictionary(quiet); outsider["speakerHeroStringId"] = "outsider";
                Check("party_route_rejects_absent_speaker", !ReadBool(ApplyGroupConversationFloorToSpeaker(outsider), "ok", true));
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var heard = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='floor-quiet' AND status='active';");
                    var listener = BuildKnowledgeAccessContext("taren", "player", "", quiet);
                    Check("all_quiet_retains_heard_words_without_fabricated_npc", heard.Count == 1
                        && ReadString(heard[0], "role", "") == "player"
                        && ReadString(heard[0], "text", "") == ReadString(quiet, "playerText", "")
                        && PrecisionTurnVisible(heard[0], "taren", listener));
                    GroupConversationFloorApi(quiet);
                    Check("quiet_retry_has_one_source", QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE exchange_id='floor-quiet' AND status='active';").Count == 1);
                }
                var whisper = CloneDictionary(payload);
                whisper["sceneTurnId"] = "floor-whisper"; whisper["turnId"] = "floor-whisper"; whisper["contributionId"] = "floor-whisper";
                whisper["playerText"] = "*I whisper to Taren* Meet me at the silver willow.";
                var whispered = GroupConversationFloorApi(whisper);
                Check("private_recipient_invited_without_broadcasting", ReadStringList(whispered, "addressedHeroStringIds").Contains("taren")
                    && ReadStringList(whispered, "responseOrder").First() == "taren"
                    && ReadStringList(whispered, "responseOrder").Count == 3
                    && !ProjectPlayerInputForAudience(ReadString(whisper, "playerText", ""), "arven", profiles, out _, out _).Contains("silver willow"));
                var handover = CloneDictionary(payload);
                handover["sceneTurnId"] = "floor-handover"; handover["turnId"] = "floor-handover"; handover["contributionId"] = "floor-handover";
                handover["playerText"] = "*I hand Taren 20 denars.*";
                var handoverPlan = GroupConversationFloorApi(handover);
                Check("actual_handover_recipient_can_respond", ReadStringList(handoverPlan, "addressedHeroStringIds").Contains("taren")
                    && ReadInt(handoverPlan, "providerCalls", -1) == 0 && !handoverPlan.ContainsKey("queuedDialogueActions"));
                var explicitAction = CloneDictionary(payload);
                explicitAction["sceneTurnId"] = "floor-structured"; explicitAction["turnId"] = "floor-structured"; explicitAction["contributionId"] = "floor-structured";
                explicitAction["playerText"] = "Nobody else needs to answer. I would like you to give me 100 denars.";
                explicitAction["structuredActionProtocolVersion"] = 1; explicitAction["structuredActionRecipientId"] = "taren";
                var explicitPlan = GroupConversationFloorApi(explicitAction);
                Check("typed_action_selects_exact_recipient_without_execution", ReadStringList(explicitPlan, "responseOrder").Contains("taren")
                    && ReadStringList(explicitPlan, "addressedHeroStringIds").Contains("taren") && !explicitPlan.ContainsKey("queuedDialogueActions"));
                explicitAction["structuredActionRecipientId"] = "outsider";
                Check("typed_action_cannot_address_outside_audience", !ReadBool(GroupConversationFloorApi(explicitAction), "ok", true));
                var eventQuiet = CloneDictionary(payload);
                eventQuiet["sceneTurnId"] = "floor-event-quiet"; eventQuiet["turnId"] = "floor-event-quiet"; eventQuiet["contributionId"] = "floor-event-quiet";
                eventQuiet["playerText"] = "Nobody needs to answer. The ferry leaves at dawn.";
                int quietCalls = 0; bool noInventedPriorSpeech = true;
                var eventQuietResult = SocialEventTurnWithResponder(eventQuiet, request => {
                    quietCalls++;
                    noInventedPriorSpeech &= ReadDictionaryList(request, "groupTurnResponses").Count == 0
                        && !ReadBool(request, "mustAccountForPriorSpeaker", true)
                        && !ReadString(request, "sceneContext", "").Contains("One or more attributed NPC contributions");
                    return TestDict("ok", true, "reply", "", "participation", "quiet"); });
                Check("all_quiet_is_three_model_decisions", quietCalls == 3 && ReadBool(eventQuietResult, "completedExchange", false)
                    && ReadDictionaryList(eventQuietResult, "participantResults").Count == 3
                    && ReadDictionaryList(eventQuietResult, "participantResults").All(r => ReadString(r, "reply", "") == ""));
                Check("quiet_queue_does_not_imply_prior_speech", noInventedPriorSpeech);
                Check("all_quiet_replay_makes_no_new_calls", ReadBool(SocialEventTurnWithResponder(eventQuiet,
                    _ => throw new InvalidOperationException("Quiet replay called a writer")), "idempotent", false));

                var arrival = CloneDictionary(payload);
                arrival["sceneTurnId"] = "floor-arrival"; arrival["turnId"] = "floor-arrival"; arrival["contributionId"] = "floor-arrival";
                arrival["playerText"] = "*Arven and I enter the keep hall together*";
                arrival["participantRequests"] = roster.Select(p => {
                    var request = CloneDictionary(arrival); request["speakerHeroStringId"] = p.Id; return request; }).ToList();
                int arrivalCalls = 0; bool arrivalPreserved = true;
                var arrivalResult = SocialEventTurnWithResponder(arrival, request => {
                    arrivalCalls++;
                    arrivalPreserved &= ReadString(request, "playerText", "") == "*Arven and I enter the keep hall together*"
                        && ReadString(request, "sceneContext", "").Contains("remembered invitations and commitments");
                    bool host = ReadString(request, "speakerHeroStringId", "") == "arven";
                    return TestDict("ok", true, "participation", host ? "speak" : "quiet", "reply", host ? "Welcome home. Come sit by the fire." : ""); });
                Check("narrated_castle_arrival_reaches_every_writer", arrivalCalls == 3 && arrivalPreserved
                    && ReadBool(arrivalResult, "completedExchange", false)
                    && ReadDictionaryList(arrivalResult, "participantResults").Any(r => ReadString(r, "reply", "").StartsWith("Welcome home.")));

                var wilderness = CloneDictionary(payload);
                wilderness["mode"] = "social_event"; wilderness["templateId"] = "generated_wilderness";
                wilderness["conversationSessionId"] = "floor-wilderness";
                wilderness["wildernessInitiatorHeroStringId"] = "taren";
                wilderness["sceneTurnId"] = "floor-wilderness-1"; wilderness["turnId"] = "floor-wilderness-1"; wilderness["contributionId"] = "floor-wilderness-1";
                wilderness["playerText"] = "*I look over at her and smile* There is much more to see on our travels.";
                wilderness["transcript"] = new[] { "Event: Taren watches a fox in the long grass.", "Player: " + ReadString(wilderness, "playerText", "") };
                var eventPlan = SocialEventTurnPlanApi(wilderness);
                Check("wilderness_first_statement_has_initiator", ReadBool(eventPlan, "ok", false)
                    && ReadStringList(eventPlan, "responseOrder").FirstOrDefault() == "taren"
                    && ReadString(eventPlan, "replyFocusHeroStringId", "") == "taren", eventPlan);
                int writers = 0;
                wilderness["participantRequests"] = ReadStringList(eventPlan, "responseOrder").Select(id => {
                    var request = CloneDictionary(wilderness); request["speakerHeroStringId"] = id;
                    request["speaker"] = profiles.First(p => CharacterIdFrom(p) == id); return request; }).ToList();
                var completed = SocialEventTurnWithResponder(wilderness, request => {
                    writers++; return TestDict("ok", true, "reply", "There is much to discover.", "participation", "speak"); });
                Check("wilderness_selected_writer_delivers_and_retry_does_not_call", writers == ReadStringList(eventPlan, "responseOrder").Count
                    && ReadBool(completed, "completedExchange", false) && ReadDictionaryList(completed, "participantResults")
                        .Any(r => ReadString(r, "heroStringId", "") == "taren" && ReadString(r, "reply", "").Length > 0), completed);
                int beforeRetry = writers;
                var eventReplay = SocialEventTurnWithResponder(wilderness, _ => { writers++; throw new InvalidOperationException("Repeated writer"); });
                Check("wilderness_retry_keeps_exact_reply", writers == beforeRetry && ReadBool(eventReplay, "idempotent", false));
                var wait = CloneDictionary(wilderness); wait["sceneTurnId"] = "floor-wilderness-wait"; wait["turnId"] = "floor-wilderness-wait"; wait["contributionId"] = "floor-wilderness-wait";
                wait["playerText"] = "*I wait for Taren to respond to that.*";
                Check("wilderness_named_wait_survives_observer_projection", ReadStringList(SocialEventTurnPlanApi(wait), "addressedHeroStringIds").Contains("taren"));
                var legacy = CloneDictionary(wilderness); legacy["wildernessInitiatorHeroStringId"] = "";
                legacy["wildernessApproachText"] = "Morning light falls across the road. Taren studies the grass.";
                Check("legacy_visible_approach_recovers_focus", WildernessConversationReplyFocus(legacy, profiles,
                    roster.Select(p => p.Id).ToList(), new List<GroupTurnLine>()) == "taren");
                legacy["wildernessApproachText"] = "Nobody is named in this scene.";
                Check("legacy_unknown_focus_is_not_guessed", WildernessConversationReplyFocus(legacy, profiles,
                    roster.Select(p => p.Id).ToList(), new List<GroupTurnLine>()) == "");
                var boundaries = new List<GroupTurnLine> { new GroupTurnLine { Role = "npc", SpeakerId = "arven", Text = "A journey." },
                    new GroupTurnLine { Role = "player", Text = "A new subject." } };
                Check("wilderness_does_not_search_past_player_boundary", WildernessConversationReplyFocus(wilderness, profiles,
                    roster.Select(p => p.Id).ToList(), boundaries) == "");
                legacy["templateId"] = "feast"; legacy["wildernessInitiatorHeroStringId"] = "taren";
                Check("ordinary_event_has_no_wilderness_fallback", WildernessConversationReplyFocus(legacy, profiles,
                    roster.Select(p => p.Id).ToList(), new List<GroupTurnLine>()) == "");

                var tavernRoster = profiles.Select((profile, index) => {
                    var copy = CloneDictionary(profile); copy["age"] = 30; copy["isAvailable"] = true;
                    copy["isMadam"] = index == 0; return copy; }).ToList();
                var playerProfile = TestDict("heroStringId", "player", "name", "Player", "age", 30, "isPlayer", true);
                var tavern = TestDict("campaignId", campaign, "timelineId", "floor-timeline", "worldDay", 1d,
                    "conversationSessionId", "floor-private-visit", "townId", "town", "isTown", true,
                    "playerHeroStringId", "player", "player", playerProfile, "roster", tavernRoster,
                    "identityRoster", tavernRoster.Concat(new[] { playerProfile }).ToList(),
                    "requestId", "visit-listener", "playerTurnId", "visit-turn", "speakerHeroStringId", "taren",
                    "playerText", "Only Lysa, tell me about the river.");
                var visit = NewTavernHouseSession(tavern); visit["status"] = "visiting";
                visit["receipt"] = TestDict("participantHeroIds", new[] { "lysa", "taren" });
                string visitPath = TavernHouseSessionPath(tavern); SaveTavernHouseSession(visitPath, visit);
                string beforeVisit = File.ReadAllText(visitPath);
                var visitPlan = TavernHouseTurnPlanApi(tavern);
                Check("tavern_preflight_uses_receipt_audience", ReadBool(visitPlan, "ok", false)
                    && ReadStringList(visitPlan, "responseOrder").SequenceEqual(new[] { "lysa", "taren" })
                    && ReadStringList(visitPlan, "audienceHeroStringIds").OrderBy(x => x).SequenceEqual(new[] { "lysa", "taren" })
                    && ReadInt(visitPlan, "providerCalls", -1) == 0, visitPlan);
                Check("tavern_preflight_does_not_publish_dialogue_or_terms", File.ReadAllText(visitPath) == beforeVisit);
                var tavernReplay = TavernHouseTurnPlanApi(tavern);
                Check("tavern_order_retry_keeps_both_opportunities", ReadBool(tavernReplay, "replayed", false)
                    && ReadStringList(tavernReplay, "responseOrder").SequenceEqual(new[] { "lysa", "taren" }));
                var absentVisit = CloneDictionary(tavern); absentVisit["speakerHeroStringId"] = "arven";
                Check("tavern_roster_card_cannot_create_attendance", !ReadBool(TavernHouseTurnPlanApi(absentVisit), "ok", true));
            }
            catch (Exception ex) { Check("exception", false, LimitText(ex.ToString(), 2200)); }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                TryDeleteDirectory(Path.Combine(CampaignsRoot(), SafePathSegment(campaign, "campaign")));
            }
            return tests;
        }
    }
}
