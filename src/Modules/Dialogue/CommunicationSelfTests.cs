using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCommunicationSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool passed, object detail = null) => results.Add(TestDict("caseId", "communication_" + name,
                "passed", passed, "name", name, "summary", passed ? name : name + ": " + LimitText(Json.Serialize(detail), 6500), "detail", detail));
            Check("explicit_will_and_contraction", CommunicationPromise("I will tell target about the school.") && CommunicationPromise("I'll write to target tomorrow."));
            foreach (string text in new[] { "I might tell target.", "I would ask target.", "I will not tell target.", "If you pay, I will tell target.",
                "She said I will tell target.", "\"I will tell target\" was her promise.", "*I will tell target.*", "I will ask target only if you pay." })
                Check("noncommitment_" + PromptHash(text).Substring(0, 8), !CommunicationPromise(text));
            Check("negative_message_content_is_still_a_promise", CommunicationPromise("I will tell target you are not coming."));
            Check("ordinary_communication_phrases", CommunicationPromise("I'll let her know about the school.") && CommunicationPromise("I will have a messenger tell target."));
            foreach (string text in new[] { "The letter goes to Ira when you have gone your way — not before, and not with one word added or withheld.",
                "My message will reach target.", "The invitation will be sent to target.", "I will personally write to target.",
                "I will make sure target is told.", "I'll see to it that a message reaches target.", "I am going to send for target.",
                "I will tell target \"teachers first\".", "Ira will receive exactly this: the proposed room opens at the second bell.",
                "the Olive 17 question goes on its own sheet beside the summons", "Both letters leave together.", "I will carry it faithfully." })
                Check("direct_dispatch_commitment_" + PromptHash(text).Substring(0, 8), CommunicationPromise(text));
            foreach (string text in new[] { "Her letter goes to Ira.", "She says the letter goes to Ira.",
                "The letter would go to Ira.", "The letter goes to Ira if you pay me.", "The letter will not go to Ira.",
                "She said Ira will receive the note.", "Ira will receive the note if you pay me.", "Otherwise both letters leave together.", "I will carry your horse." })
                Check("not_my_committed_dispatch_" + PromptHash(text).Substring(0, 8), !CommunicationPromise(text));
            Check("captured_ordinary_withdrawal_is_clear", CommunicationWithdrawal("Withdrawn, then, Your Majesty. Nothing of the reading room travels to Ira at all.")
                && CommunicationWithdrawal("The note is cancelled."));
            Check("captured_only_revised_terms_are_a_dispatch", CommunicationPromise("Only the revised terms travel to my daughter: the proposed reading room opens at the second bell.")
                && !CommunicationPromise("Only the revised terms travel to my daughter if you pay.")
                && !CommunicationPromise("She said only the revised terms travel to my daughter."));
            const string referredInvitation = "The invitation, exactly: Pharon is summoned to Onira. He comes alone and remains one full day after arrival. It goes out tomorrow, and I will be at Onira, waiting.";
            Check("captured_pronoun_dispatch_has_own_named_antecedent", CommunicationPromise("It goes out tomorrow, and I will be at Onira, waiting.", referredInvitation));
            Check("omitted_pronoun_plan_can_route_bounded_review", CommunicationReplyNeedsPlanReview(referredInvitation));
            foreach (string unrelated in new[] { "The lantern burns. It goes out tomorrow.", "Her letter is ready. It goes out tomorrow.",
                "She said the invitation is ready. It goes out tomorrow.", "She said:\nThe invitation is ready. It goes out tomorrow.",
                "The invitation goes only if you pay. It goes out tomorrow.", "“The invitation is ready. It goes out tomorrow.”" })
                Check("pronoun_does_not_invent_owned_dispatch_" + PromptHash(unrelated).Substring(0, 8), !CommunicationPromise("It goes out tomorrow.", unrelated));
            Check("unresolved_pronoun_and_conditional_dispatch_remain_blocked", !CommunicationPromise("It goes out tomorrow.")
                && !CommunicationPromise("It goes out if you pay.", "The invitation is ready. It goes out if you pay."));
            foreach (string text in new[] { "tomorrow I send Pharon a fresh summons to Zeonica.", "Tomorrow, I will write to Ira.",
                "Tonight I send the letter.", "At dawn, I tell target.", "In two days I write to target." })
                Check("explicit_future_time_is_a_commitment_" + PromptHash(text).Substring(0, 8), CommunicationPromise(text));
            foreach (string text in new[] { "Tomorrow I might tell target.", "Tomorrow, if you pay, I will write to target.", "Yesterday I send the letter.", "Every day I write to target." })
                Check("future_time_does_not_create_hypothetical_or_habitual_commitment_" + PromptHash(text).Substring(0, 8), !CommunicationPromise(text));
            Check("quoted_or_conditional_withdrawal_is_not_my_cancellation", !CommunicationWithdrawal("She said I withdraw the letter.")
                && !CommunicationWithdrawal("If you pay, I will not send the letter.") && !CommunicationWithdrawal("Withdrawn if you pay."));
            foreach (string text in new[] { "When we have finished here, I will write to Ira.",
                "After we end our conversation, I will tell target.", "Once our conversation is over, I'll ask target.",
                "Yes, Your Majesty, I shall write to Ira.", "After our discussion ends, I'll send for target." })
                Check("closure_timing_promise_" + PromptHash(text).Substring(0, 8), CommunicationPromise(text));
            foreach (string text in new[] { "Once you pay, I will write to Ira.", "When she agrees, I will tell target.",
                "After we have finished here, I might write to Ira.", "Once our conversation is over, she said I will write to Ira.",
                "Once our conversation is over, I will not write to Ira." })
                Check("closure_is_not_contingent_or_reported_" + PromptHash(text).Substring(0, 8), !CommunicationPromise(text));
            var host = TestDict("settlementId", "host_town");
            Check("summons_terms_are_stated", CommunicationMeetingTerms(host, "host_town", 3, "I will ask target to stay here for three days.")
                && CommunicationMeetingTerms(host, "host_town", .25, "I will ask target to remain for 6 hours."));
            Check("summons_cannot_invent_duration_or_location", !CommunicationMeetingTerms(host, "host_town", 3, "I will send for target.")
                && !CommunicationMeetingTerms(host, "elsewhere", 3, "I will ask target to stay for three days.")
                && !CommunicationMeetingTerms(host, "host_town", 4, "I will ask target to stay for three days."));
            bool playerProviderCalled = false;
            var playerReply = ComposeCommunicationReply("unused", TestDict("recipient_id", "player", "player_id", "player"), TestDict(), TestDict(),
                _ => { playerProviderCalled = true; return TestDict("ok", true); });
            Check("background_never_authors_a_player_reply", !playerProviderCalled && !ReadBool(playerReply, "ok", true));
            string namedInvitation = CommunicationBody(TestDict("speaker_id", "queen"), TestDict("speakerName", "Rhagaea", "destinationId", "town_ES5", "destinationName", "Onira", "stayDays", 1d), true);
            string campInvitation = CommunicationBody(TestDict("speaker_id", "queen"), TestDict("speakerName", "Rhagaea", "destinationId", "field:100:200:land", "stayDays", 1d), true);
            Check("summons_text_names_the_place_without_exposing_routing_ids", namedInvitation.Contains("Onira", StringComparison.Ordinal)
                && namedInvitation.Contains("one full day", StringComparison.Ordinal) && !namedInvitation.Contains("town_ES5", StringComparison.Ordinal)
                && campInvitation.Contains("the agreed camp", StringComparison.Ordinal) && !campInvitation.Contains("field:", StringComparison.Ordinal));
            Check("recipient_sees_solo_camp_and_return_terms_before_deciding", namedInvitation.Contains("Come alone.", StringComparison.Ordinal)
                && namedInvitation.Contains("original camp throughout your travel and stay", StringComparison.Ordinal)
                && namedInvitation.Contains("return to them afterward", StringComparison.Ordinal));
            string plainSummons = CommunicationBody(TestDict("speaker_id", "queen", "kind", "summon"),
                TestDict("speakerName", "Rhagaea", "destinationId", "town_ES5", "destinationName", "Onira", "stayDays", 1d,
                    "contentEvidence", new[] { TestDict("speakerId", "queen", "quote", "We will discuss reading-room hours.") }));
            Check("plain_summons_contains_actual_meeting_terms_and_selected_purpose", plainSummons.Contains("Onira", StringComparison.Ordinal)
                && plainSummons.Contains("one full day", StringComparison.Ordinal) && plainSummons.Contains("Come alone.", StringComparison.Ordinal)
                && plainSummons.Contains("We will discuss reading-room hours.", StringComparison.Ordinal));
            string omittedSummon = "*She sets down the cup.*\n\nA sealed summons. Yes — I will send for Pharon once we finish here. He comes alone and remains a full day after arrival.";
            Check("captured_summons_without_plan_routes_metadata_review", CommunicationReplyNeedsPlanReview(omittedSummon));
            Check("review_does_not_promote_reported_conditional_or_narrated_promises", !CommunicationReplyNeedsPlanReview("She said I will write to Ira.")
                && !CommunicationReplyNeedsPlanReview("If you pay, I will send for Pharon.")
                && !CommunicationReplyNeedsPlanReview("*I will send for Pharon.*") && !CommunicationReplyNeedsPlanReview("I will not write to Ira."));
            var ordinarySchema = TryParseJsonObject(ConversationMemoryOutputSchema("{\"reply\":\"\",\"customField\":true}"));
            Check("ordinary_json_example_includes_communication_plan", ReadDictionaryList(ordinarySchema, "communicationPlans").Count == 1
                && ReadBool(ordinarySchema, "customField", false));
            Check("custom_prose_schema_preserves_instructions_and_adds_communication_field", ConversationMemoryOutputSchema("CUSTOM OWNER INSTRUCTIONS")
                .StartsWith("CUSTOM OWNER INSTRUCTIONS", StringComparison.Ordinal)
                && ConversationMemoryOutputSchema("CUSTOM OWNER INSTRUCTIONS").Contains("communicationPlans", StringComparison.Ordinal));
            Check("compact_metadata_preserves_communication_plans", ReadStringList(CodexConversationContracts.CompactMetadataPolicy("dialogue"), "preserveFields").Contains("communicationPlans"));
            var untouched = TestDict("reply", omittedSummon, "structuredActionDecision", TestDict("kind", "none"), "memoryWrites", new[] { TestDict("text", "kept") });
            var repairPlan = TestDict("key", "pharon_meeting", "kind", "summon_then_inform", "operation", "send", "recipientId", "lord_1_15",
                "evidenceQuote", "I will send for Pharon once we finish here.", "destinationId", "host_town", "stayDays", 1d);
            var metadataOnly = ApplyCommunicationPlanPatch(untouched, TestDict("communicationPlans", new[] { repairPlan }, "structuredActionDecision", TestDict("kind", "accept")));
            Check("metadata_patch_keeps_speech_actions_and_memories", ReadString(metadataOnly, "reply", "") == omittedSummon
                && ReadString(ReadDictionary(metadataOnly, "structuredActionDecision"), "kind", "") == "none"
                && ReadDictionaryList(metadataOnly, "memoryWrites").Count == 1 && !untouched.ContainsKey("communicationPlans"));
            Check("metadata_patch_rejects_rewritten_or_unsupported_promise", ApplyCommunicationPlanPatch(untouched, TestDict("reply", "I agree to pay.", "communicationPlans", new[] { repairPlan })) == null
                && ApplyCommunicationPlanPatch(untouched, TestDict("communicationPlans", new[] { TestDict("evidenceQuote", "I will pay for it.") })) == null
                && ApplyCommunicationPlanPatch(untouched, TestDict("communicationPlans", new object[0])) == null);
            var formatOriginal = CloneDictionary(untouched); formatOriginal["communicationPlans"] = new[] { repairPlan };
            var formatRepaired = TestDict("reply", omittedSummon); PreserveFormatRepairMetadata(formatOriginal, formatRepaired);
            Check("format_repair_retains_existing_communication_metadata", ReadDictionaryList(formatRepaired, "communicationPlans").Count == 1);
            string campaign = "communication_test_" + Guid.NewGuid().ToString("N");
            try
            {
                using var c = OpenCampaignConnection(campaign);
                var repairPayload = TestDict("playerText", "Please send for Pharon and ask him to remain for one full day.", "playerHeroStringId", "main_hero", "timelineId", "main", "worldDay", 10d,
                    "gossipPresence", TestDict("people", new[] { TestDict("heroStringId", "lord_1_14", "name", "Rhagaea", "settlementId", "host_town"),
                        TestDict("heroStringId", "lord_1_15", "name", "Pharon", "isAlive", true), TestDict("heroStringId", "main_hero", "name", "Player") }));
                int metadataCalls = 0;
                var repairedEnvelope = RepairMissingCommunicationPlans(TestDict("ok", true, "content", Json.Serialize(untouched)), repairPayload, campaign, "test_metadata", "dialogue", "lord_1_14", request =>
                {
                    metadataCalls++;
                    Check("repair_sees_current_native_destination_and_exact_turns", Json.Serialize(request).Contains("host_town", StringComparison.Ordinal)
                        && Json.Serialize(request).Contains("current_player", StringComparison.Ordinal) && Json.Serialize(request).Contains("lord_1_15", StringComparison.Ordinal));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { repairPlan })));
                });
                Check("missing_plan_uses_one_bounded_metadata_call", metadataCalls == 1 && ReadBool(repairedEnvelope, "ok", false)
                    && ReadFirstString(TryParseJsonObject(ReadString(repairedEnvelope, "content", "")), "reply") == omittedSummon);
                var invalidEnvelope = RepairMissingCommunicationPlans(TestDict("ok", true, "content", Json.Serialize(untouched)), repairPayload, campaign, "test_missing_metadata", "dialogue", "lord_1_14",
                    _ => { metadataCalls++; return TestDict("ok", true, "content", "{\"communicationPlans\":[]}"); });
                Check("failed_metadata_review_cannot_publish_false_promise", metadataCalls == 2 && !ReadBool(EnsureConversationDelivery(invalidEnvelope), "ok", true)
                    && ReadString(invalidEnvelope, "errorCode", "") == "communication_plan_missing");
                var completeEnvelope = TestDict("ok", true, "content", Json.Serialize(formatOriginal));
                RepairMissingCommunicationPlans(completeEnvelope, repairPayload, campaign, "test_no_extra_review", "dialogue", "lord_1_14", _ => { metadataCalls++; return TestDict(); });
                Check("existing_plan_never_requests_extra_provider_review", metadataCalls == 2);
                RepairMissingCommunicationPlans(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I will tell you more about the school."))), repairPayload,
                    campaign, "test_player_direct_speech", "dialogue", "lord_1_14", _ => { metadataCalls++; return TestDict(); });
                Check("ordinary_speech_to_player_does_not_schedule_or_request_review", metadataCalls == 2);
                const string headerReply = "The invitation, exactly as agreed: tomorrow I send Pharon a fresh summons. It goes out tomorrow.";
                var headerPlan = CloneDictionary(repairPlan); headerPlan["evidenceQuote"] = headerReply;
                var headerEnvelope = TestDict("ok", true, "content", Json.Serialize(TestDict("reply", headerReply, "communicationPlans", new[] { headerPlan })));
                int quoteRepairCalls = 0;
                var quoteRepaired = RepairMissingCommunicationPlans(headerEnvelope, repairPayload, campaign, "test_unusable_quote", "dialogue", "lord_1_14", request => {
                    quoteRepairCalls++;
                    Check("quote_review_sees_only_exact_executable_candidates", Json.Serialize(request).Contains("It goes out tomorrow.", StringComparison.Ordinal));
                    var selected = CloneDictionary(headerPlan); selected["evidenceQuote"] = "It goes out tomorrow.";
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { selected })));
                });
                Check("existing_unusable_quote_gets_one_metadata_only_repair", quoteRepairCalls == 1 && ReadBool(quoteRepaired, "ok", false)
                    && ReadFirstString(TryParseJsonObject(ReadString(quoteRepaired, "content", "")), "reply") == headerReply
                    && ReadString(ReadDictionaryList(TryParseJsonObject(ReadString(quoteRepaired, "content", "")), "communicationPlans").Single(), "evidenceQuote", "") == "It goes out tomorrow.");
                var uncorrected = RepairMissingCommunicationPlans(headerEnvelope, repairPayload, campaign, "test_still_unusable_quote", "dialogue", "lord_1_14",
                    _ => TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { headerPlan }))));
                Check("failed_quote_repair_cannot_publish_unschedulable_promise", !ReadBool(EnsureConversationDelivery(uncorrected), "ok", true));
                const string compoundReply = "Pharon is summoned to meet me here. The summons goes out tomorrow, and it asks for his own word.";
                var compoundPlan = CloneDictionary(repairPlan); compoundPlan["evidenceQuote"] = "Pharon is summoned to meet me here. The summons goes out tomorrow";
                int knownPlanReviewCalls = 0;
                var compoundFixed = RepairMissingCommunicationPlans(TestDict("ok", true, "content", Json.Serialize(TestDict("reply", compoundReply,
                    "communicationPlans", new[] { compoundPlan }))), repairPayload, campaign, "test_known_recipient_compound_quote", "dialogue", "lord_1_14", _ => {
                        knownPlanReviewCalls++;
                        var selected = CloneDictionary(compoundPlan); selected["evidenceQuote"] = "The summons goes out tomorrow";
                        return TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { selected })));
                    });
                Check("known_recipient_quote_repair_does_not_require_recipient_in_same_clause", knownPlanReviewCalls == 1 && ReadBool(compoundFixed, "ok", false)
                    && ReadString(ReadDictionaryList(TryParseJsonObject(ReadString(compoundFixed, "content", "")), "communicationPlans").Single(), "evidenceQuote", "") == "The summons goes out tomorrow"
                    && ReadFirstString(TryParseJsonObject(ReadString(compoundFixed, "content", "")), "reply") == compoundReply);
                Check("native_full_day_terms_are_explicit", CommunicationMeetingTerms(host, "host_town", 1, "He must remain for one full day after arrival.")
                    && CommunicationMeetingTerms(host, "host_town", 1, "I will have him stay a full day.")
                    && CommunicationMeetingTerms(host, "host_town", 1, "He remains with me a full day after he arrives.")
                    && !CommunicationMeetingTerms(host, "host_town", 2, "I will have him stay a full day."));
                EnsureMemoryPrecisionSchema(c); EnsureCommunicationSchema(c); EnsurePersonalGossipSchema(c);
                var sourceHero = GossipTestHero("source"); var target = GossipTestHero("target", "distant_town"); var player = GossipTestHero("player");
                target["positionX"] = 160d;
                var people = new List<Dictionary<string, object>> { sourceHero, target, player };
                string request = "Tell target that I offer 2000 denars for the school, provided the teachers are paid first.";
                string reply = "I will tell target about your offer.";
                var payload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 10d,
                    "heroStringId", "source", "playerHeroStringId", "player", "playerText", request, "ts", 100L,
                    "gossipPresence", TestDict("people", people), "conversationSessionId", "comm_session", "channel", "in_person");
                var started = ConversationStartApi(payload);
                Check("production_session_established", ReadBool(started, "ok", false), started);
                string playerPromise = "I will write to player about the school.";
                var playerExchange = StoreConversationExchange(campaign, payload, "source", "player", "Player", "Source", request, playerPromise, "comm_player_guard", 99);
                var playerUnit = new AcceptedConversationUnit { Campaign = campaign, Hero = "source", Player = "player", Reply = playerPromise,
                    PlayerText = request, Payload = payload, Parsed = TestDict("communicationPlans", new[] { TestDict("key", "player_guard", "recipientId", "player", "evidenceQuote", playerPromise) }) };
                var playerAcceptance = AcceptCommunicationCommitments(c, playerUnit, playerExchange, ReadStringList(playerExchange, "turnIds")
                    .SelectMany(id => QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id))).ToList());
                Check("player_is_not_a_background_npc_recipient", ReadStringList(playerAcceptance, "accepted").Count == 0
                    && ReadStringList(playerAcceptance, "rejected").Any(r => r.Contains("unresolved_recipient", StringComparison.Ordinal)));
                var exchange = StoreConversationExchange(campaign, payload, "source", "player", "Player", "Source", request, reply, "comm_exchange", 100);
                string sessionId = ReadString(exchange, "sessionId", "");
                var current = ReadStringList(exchange, "turnIds").SelectMany(id => QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id))).ToList();
                var plan = TestDict("key", "school", "operation", "send", "kind", "inform", "recipientId", "target", "evidenceQuote", reply,
                    "contentEvidence", new[] { TestDict("turnId", "current_player", "quote", request) }, "delayDays", 0d);
                var unit = new AcceptedConversationUnit { Campaign = campaign, Hero = "source", Player = "player", PlayerName = "Player", HeroName = "Source",
                    EventId = "comm_exchange", Reply = reply, PlayerText = request, Payload = payload, Parsed = TestDict("communicationPlans", new[] { plan }), Generation = NpcLivesGeneration(c) };
                var receipt = AcceptCommunicationCommitments(c, unit, exchange, current);
                var row = QuerySql(c, "SELECT * FROM communication_messages;").SingleOrDefault();
                Check("accepted_promise_creates_one_draft", ReadStringList(receipt, "accepted").Count == 1 && ReadString(row, "status", "") == "draft", receipt);
                AcceptCommunicationCommitments(c, unit, exchange, current);
                Check("accepted_retry_does_not_duplicate", QuerySql(c, "SELECT * FROM communication_messages;").Count == 1);
                string originalId = ReadString(row, "message_id", "");
                var reviewPayload = CloneDictionary(payload); reviewPayload["playerText"] = "Change 2000 to 2400 before sending; keep the other conditions.";
                const string completeRevision = "I will tell target these revised terms: the offer is 2400 denars for the school, provided the teachers are paid first.";
                var deltaPlan = CloneDictionary(plan); deltaPlan["evidenceQuote"] = "I will tell target these revised terms";
                deltaPlan["contentEvidence"] = new[] { TestDict("turnId", "current_player", "quote", ReadString(reviewPayload, "playerText", "")) };
                var reviewEnvelope = TestDict("ok", true, "content", Json.Serialize(TestDict("reply", completeRevision, "communicationPlans", new[] { deltaPlan })));
                int revisionReviews = 0;
                var reconciled = RepairMissingCommunicationPlans(reviewEnvelope, reviewPayload, campaign, "review_revision", "dialogue", "source", candidate =>
                {
                    revisionReviews++;
                    Check("revision_review_contains_old_conditions_and_current_complete_terms", Json.Serialize(candidate).Contains("teachers are paid first", StringComparison.Ordinal)
                        && Json.Serialize(candidate).Contains("pendingDraftTerms", StringComparison.Ordinal));
                    var completePlan = CloneDictionary(deltaPlan); completePlan["contentEvidence"] = new[] { TestDict("turnId", "current_npc", "quote", completeRevision) };
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { completePlan })));
                });
                Check("draft_revision_is_reconciled_once_without_changing_its_identity", revisionReviews == 1 && ReadBool(reconciled, "ok", false)
                    && ReadString(ReadDictionaryList(TryParseJsonObject(ReadString(reconciled, "content", "")), "communicationPlans").Single(), "key", "") == "school");
                var deltaOnly = RepairMissingCommunicationPlans(reviewEnvelope, reviewPayload, campaign, "review_delta_only", "dialogue", "source",
                    _ => TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { deltaPlan }))));
                Check("delta_only_review_holds_the_prior_draft", !ReadBool(deltaOnly, "ok", true)
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", originalId)).Single(), "status", "") == "needs_clarification");
                ReleaseCommunicationSession(c, QuerySql(c, "SELECT * FROM conversation_sessions WHERE session_id=$id;", TestDict("id", sessionId)).Single(), "closed_by_player", 10);
                AdvanceCommunications(c, campaign, "main", 10, people);
                Check("unresolved_revision_never_dispatches_the_stale_version", QuerySql(c, "SELECT * FROM communication_history;").Count == 0
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", originalId)).Single(), "status", "") == "needs_clarification");
                request = "Tell target that I now offer 2400 denars for the school, provided the teachers are paid first.";
                reply = "I will tell target about your revised offer.";
                payload = CloneDictionary(payload); payload["playerText"] = request;
                var revisedExchange = StoreConversationExchange(campaign, payload, "source", "player", "Player", "Source", request, reply, "comm_revision", 101);
                current = ReadStringList(revisedExchange, "turnIds").SelectMany(id => QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id))).ToList();
                plan["evidenceQuote"] = reply; plan["contentEvidence"] = new[] { TestDict("turnId", "current_player", "quote", request) };
                unit.Payload = payload; unit.PlayerText = request; unit.Reply = reply; unit.EventId = "comm_revision"; unit.Timestamp = 101;
                AcceptCommunicationCommitments(c, unit, revisedExchange, current);
                Check("changed_terms_update_the_existing_draft", QuerySql(c, "SELECT * FROM communication_messages;").Count == 1
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages;").Single(), "message_id", "") == originalId);
                var session = QuerySql(c, "SELECT * FROM conversation_sessions WHERE session_id=$id;", TestDict("id", sessionId)).Single();
                ReleaseCommunicationSession(c, session, "scene_transition", 10);
                Check("scene_handoff_does_not_dispatch", ReadString(QuerySql(c, "SELECT * FROM communication_messages;").Single(), "status", "") == "draft");
                AdvanceCommunications(c, campaign, "main", 10, people);
                Check("open_conversation_has_no_history", QuerySql(c, "SELECT * FROM communication_history;").Count == 0);
                ReleaseCommunicationSession(c, session, "closed_by_player", 10);
                AdvanceCommunications(c, campaign, "main", 10, people);
                row = QuerySql(c, "SELECT * FROM communication_messages;").Single();
                double due = ReadDouble(row, "due_day", 0);
                Check("dispatch_has_a_real_future_deadline", due > 10 && ReadString(row, "status", "") == "in_transit");
                Check("sender_knows_dispatch_without_early_recipient_history", QuerySql(c, "SELECT * FROM communication_history;").Count == 1
                    && ReadString(QuerySql(c, "SELECT * FROM communication_history;").Single(), "owner_id", "") == "source");
                AdvanceCommunications(c, campaign, "main", due - .00001, people);
                Check("no_delivery_before_boundary", QuerySql(c, "SELECT * FROM communication_history WHERE owner_id='target';").Count == 0);
                target["available"] = false;
                AdvanceCommunications(c, campaign, "main", due, people);
                Check("unavailable_recipient_waits_without_early_history", QuerySql(c, "SELECT * FROM communication_history WHERE owner_id='target';").Count == 0
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages;").Single(), "status", "") == "awaiting_recipient"
                    && ReadLong(CommunicationReadiness(c, "main", due), "due", -1) == 0);
                target["available"] = true;
                AdvanceCommunications(c, campaign, "main", due, people);
                AdvanceCommunications(c, campaign, "main", due + .00001, people);
                var history = QuerySql(c, "SELECT * FROM communication_history WHERE owner_id='target';");
                Check("delivery_is_once_and_preserves_exact_terms", history.Count == 1 && ReadString(GossipJson(history.Single()), "body", "").Contains(request, StringComparison.Ordinal));
                var sourceIds = new List<string>();
                payload["worldDay"] = due + .1;
                string targetPrompt = BuildCommunicationPrompt(c, "target", payload, sourceIds);
                string strangerPrompt = BuildCommunicationPrompt(c, "stranger", payload, new List<string>());
                Check("delivered_knowledge_is_private_to_recipient", targetPrompt.Contains(request, StringComparison.Ordinal) && !strangerPrompt.Contains(request, StringComparison.Ordinal));
                payload["worldDay"] = 9d;
                Check("asof_day_hides_future_receipt", !BuildCommunicationPrompt(c, "target", payload, new List<string>()).Contains(request, StringComparison.Ordinal));
                payload["worldDay"] = due + .1; payload["memoryAsOfAcceptedSequence"] = 0L;
                Check("asof_sequence_hides_future_receipt", !BuildCommunicationPrompt(c, "target", payload, new List<string>()).Contains(request, StringComparison.Ordinal));
                payload.Remove("memoryAsOfAcceptedSequence");
                var readiness = CommunicationReadiness(c, "main", due);
                Check("checkpoint_blocks_pending_reply", ReadLong(readiness, "pending", 0) == 1);
                var meetingKnowledge = new NpcLivesState { CampaignId = campaign, TimelineId = "main" };
                meetingKnowledge.Meetings.Add(new NpcSummonedMeeting { Id = "meeting_" + originalId, ProposalId = originalId,
                    IssuerId = "source", TargetId = "target", Purpose = "School meeting", DestinationId = "meeting_town", StayDays = 3, Status = "travelling" });
                var knowledgePayload = TestDict("campaignId", campaign, "timelineId", "main", "playerHeroStringId", "player", "worldDay", due);
                var meetingOptions = new NpcLivesOptions(_ => true, _ => null);
                Check("issuer_cannot_see_distant_acceptance_before_return_receipt", !NpcLivesPromptBody(meetingKnowledge, "source", "player", knowledgePayload,
                    meetingOptions, c).Contains("meeting_" + originalId, StringComparison.Ordinal)
                    && NpcLivesPromptBody(meetingKnowledge, "target", "player", knowledgePayload, meetingOptions, c).Contains("meeting_" + originalId, StringComparison.Ordinal));
                int controlledReplyCalls = 0;
                CommunicationPlayers[campaign] = (NpcLivesGeneration(c), "target");
                ProcessCommunicationReplies(campaign, _ => { controlledReplyCalls++; return TestDict("ok", true); });
                Check("new_player_hero_never_gets_an_automatic_reply", controlledReplyCalls == 0
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages;").Single(), "status", "") == "awaiting_player");
                CommunicationPlayers.TryRemove(campaign, out _);
                ExecuteSql(c, "UPDATE communication_messages SET status='reply_pending';");
                ProcessCommunicationReplies(campaign, _ => {
                    ExecuteSql(c, "UPDATE memory_precision_state SET restore_generation=$generation WHERE state_id='current';", TestDict("generation", Guid.NewGuid().ToString("N")));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("body", "This stale reply must be discarded.", "answerKind", "opinion", "sourceQuote", "", "privacy", "public")));
                });
                Check("late_reply_cannot_write_across_restore_generation", QuerySql(c, "SELECT * FROM communication_messages WHERE parent_id<>'';").Count == 0);
                int replyCalls = 0;
                ProcessCommunicationReplies(campaign, _ => { replyCalls++; return TestDict("ok", true, "content", Json.Serialize(TestDict(
                    "body", "I think paying the teachers first is wise.", "answerKind", "opinion", "sourceQuote", "", "privacy", "public"))); });
                var returnMessage = QuerySql(c, "SELECT * FROM communication_messages WHERE parent_id<>'';").Single();
                Check("one_reply_is_queued_after_acquisition", replyCalls == 1 && ReadString(returnMessage, "status", "") == "queued");
                AdvanceCommunications(c, campaign, "main", due, people);
                returnMessage = QuerySql(c, "SELECT * FROM communication_messages WHERE parent_id<>'';").Single();
                double returnDue = ReadDouble(returnMessage, "due_day", 0);
                Check("return_journey_has_its_own_deadline", returnDue > due);
                Check("sender_has_no_early_reply_knowledge", QuerySql(c, "SELECT * FROM communication_history WHERE owner_id='source';").Count == 1);
                AdvanceCommunications(c, campaign, "main", returnDue, people);
                ProcessCommunicationReplies(campaign, _ => { replyCalls++; throw new InvalidOperationException("No recursive reply."); });
                Check("reply_arrives_once_without_a_reply_loop", replyCalls == 1
                    && QuerySql(c, "SELECT * FROM communication_history WHERE owner_id='source';").Count == 2
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE parent_id='';").Single(), "status", "") == "replied");
                knowledgePayload = TestDict("campaignId", campaign, "timelineId", "main", "playerHeroStringId", "player", "worldDay", returnDue);
                Check("return_receipt_releases_issuer_meeting_knowledge", NpcLivesPromptBody(meetingKnowledge, "source", "player", knowledgePayload,
                    meetingOptions, c).Contains("meeting_" + originalId, StringComparison.Ordinal));
                knowledgePayload = TestDict("campaignId", campaign, "timelineId", "main", "playerHeroStringId", "player", "worldDay", returnDue, "memoryAsOfAcceptedSequence", 0L);
                Check("old_observation_cannot_use_future_return_receipt", !NpcLivesPromptBody(meetingKnowledge, "source", "player", knowledgePayload,
                    meetingOptions, c).Contains("meeting_" + originalId, StringComparison.Ordinal));
                var data = GossipJson(row);
                var bad = ComposeCommunicationReply(campaign, row, data, TestDict("recipientMemory", "No supporting fact."), _ =>
                    TestDict("ok", true, "content", Json.Serialize(TestDict("body", "The king paid 9000 denars.", "answerKind", "answered", "sourceQuote", "invented", "privacy", "public"))));
                Check("unsupported_factual_reply_rejected", !ReadBool(bad, "ok", true));
                var opinion = ComposeCommunicationReply(campaign, row, data, TestDict("recipientMemory", ""), _ =>
                    TestDict("ok", true, "content", Json.Serialize(TestDict("body", "I think the teachers should be paid first.", "answerKind", "opinion", "sourceQuote", "", "privacy", "public"))));
                Check("own_opinion_needs_no_invented_history", ReadBool(opinion, "ok", false));
                var secret = ComposeCommunicationReply(campaign, row, data, TestDict("recipientMemory", request), _ =>
                    TestDict("ok", true, "content", Json.Serialize(TestDict("body", request, "answerKind", "answered", "sourceQuote", request, "privacy", "secret"))));
                Check("secret_reply_is_not_sent", !ReadBool(secret, "ok", true));
                var staged = new Dictionary<string, object>(row) { ["kind"] = "summon_then_inform" };
                data["destinationId"] = "meeting_town"; data["stayDays"] = 3d;
                Check("invitation_does_not_reveal_later_briefing", !CommunicationBody(staged, data, true).Contains(request, StringComparison.Ordinal)
                    && CommunicationBody(staged, data).Contains(request, StringComparison.Ordinal));
                string libraryPromise = "I will tell target that the library opens tomorrow.";
                var libraryPayload = CloneDictionary(payload); libraryPayload["playerText"] = "Tell target when the library opens.";
                var libraryExchange = StoreConversationExchange(campaign, libraryPayload, "source", "player", "Player", "Source",
                    ReadString(libraryPayload, "playerText", ""), libraryPromise, "comm_library", 102);
                var librarySources = ReadStringList(libraryExchange, "turnIds").SelectMany(id => QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id))).ToList();
                var libraryPlan = TestDict("key", "library", "operation", "send", "kind", "inform", "recipientId", "target", "evidenceQuote", libraryPromise,
                    "contentEvidence", new[] { TestDict("turnId", "current_npc", "quote", libraryPromise) });
                unit.Payload = libraryPayload; unit.Parsed = TestDict("communicationPlans", new[] { libraryPlan }); unit.PlayerText = ReadString(libraryPayload, "playerText", "");
                unit.Reply = libraryPromise; unit.EventId = "comm_library";
                AcceptCommunicationCommitments(c, unit, libraryExchange, librarySources);
                string withdrawal = "I will not tell target the library hours.";
                var withdrawalPayload = CloneDictionary(libraryPayload); withdrawalPayload["playerText"] = "Do not send the library details.";
                var withdrawalExchange = StoreConversationExchange(campaign, withdrawalPayload, "source", "player", "Player", "Source",
                    ReadString(withdrawalPayload, "playerText", ""), withdrawal, "comm_cancel", 103);
                var withdrawalSources = ReadStringList(withdrawalExchange, "turnIds").SelectMany(id => QuerySql(c, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id))).ToList();
                libraryPlan["operation"] = "cancel"; libraryPlan["evidenceQuote"] = withdrawal;
                unit.Payload = withdrawalPayload; unit.PlayerText = ReadString(withdrawalPayload, "playerText", ""); unit.Reply = withdrawal; unit.EventId = "comm_cancel";
                AcceptCommunicationCommitments(c, unit, withdrawalExchange, withdrawalSources);
                ReleaseCommunicationSession(c, session, "closed_by_player", returnDue);
                Check("withdrawn_draft_is_not_dispatched", ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE topic_key='library';").Single(), "status", "") == "cancelled"
                    && QuerySql(c, "SELECT * FROM communication_history;").Count == 4);
                ExecuteSql(c, "UPDATE conversation_turns SET text='changed' WHERE turn_id=$id;", TestDict("id", ReadString(ReadDictionaryList(data, "contentEvidence").First(), "turnId", "")));
                Check("tampered_source_invalidates_unsent_content", !CommunicationSourcesActive(c, data));
                // Captured first native failure: three people named Ira, with only
                // one actually the speaker's daughter. Replay through production
                // accepted-unit publication, not just a helper classifier.
                var queen = GossipTestHero("lord_1_14", "town_EW2"); queen["name"] = "Rhagaea";
                var daughter = GossipTestHero("lord_1_37", "town_ES5"); daughter["name"] = "Ira"; daughter["motherId"] = "lord_1_14";
                var namesake = GossipTestHero("other_ira", ""); namesake["name"] = "Ira";
                var secondNamesake = GossipTestHero("second_ira", ""); secondNamesake["name"] = "Ira"; secondNamesake["motherId"] = "another_mother";
                var nativePlayer = GossipTestHero("main_hero", "town_EW2");
                string nativeText = "Please write to your daughter Ira after we finish our conversation. Tell her that I offer 2,400 denars toward a school in Zeonica, provided the teachers are paid before any decoration. This is an offer for her to consider, not permission to pay anyone yet. Will you send her those exact terms?";
                string nativePromise = "When we have finished here, I will write to Ira, and she shall have your terms word for word: two thousand four hundred denars toward a school in Zeonica, the teachers paid before any stone is carved or banner hung, an offer for her consideration — not a purse opened, not a permission granted.";
                var nativePayload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 91077.37496207407d,
                    "heroStringId", "lord_1_14", "playerHeroStringId", "main_hero", "playerText", nativeText,
                    "conversationSessionId", "native_identity_session", "sceneTurnId", "native_identity_turn", "channel", "in_person",
                    "gossipPresence", TestDict("people", new[] { queen, daughter, namesake, secondNamesake, nativePlayer }));
                ConversationStartApi(nativePayload);
                var nativePlan = TestDict("key", "ira_school_offer", "operation", "send", "kind", "inform", "recipientId", "lord_1_37",
                    "evidenceQuote", nativePromise, "contentEvidence", new[] { TestDict("turnId", "current_player", "quote",
                        "I offer 2,400 denars toward a school in Zeonica, provided the teachers are paid before any decoration. This is an offer for her to consider, not permission to pay anyone yet.") });
                var nativeUnit = new AcceptedConversationUnit { Campaign = campaign, Hero = "lord_1_14", Player = "main_hero", Location = "town_EW2",
                    HeroName = "Rhagaea", PlayerName = "Player", PlayerText = nativeText, Reply = nativePromise,
                    EventId = AcceptedConversationEventId(campaign, "lord_1_14", nativePayload), EventType = "dialogue_turn", Timestamp = 200,
                    Generation = NpcLivesGeneration(c), Payload = nativePayload, Profile = TestDict("name", "Rhagaea"), Motive = TestDict(),
                    Parsed = TestDict("reply", nativePromise, "communicationPlans", new[] { nativePlan }), CommitEffects = false,
                    Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                var nativeExchange = PublishAcceptedConversationUnit(nativeUnit, accept => StoreConversationExchange(campaign, nativePayload,
                    nativeUnit.Hero, nativeUnit.Player, nativeUnit.PlayerName, nativeUnit.HeroName, nativeText, nativePromise, nativeUnit.EventId, 200, acceptance: accept));
                var nativeReceipt = ReadDictionary(ReadDictionary(nativeExchange, "acceptedUnit"), "communications");
                Check("captured_native_named_daughter_publishes_one_draft", ReadStringList(nativeReceipt, "accepted").Count == 1
                    && QuerySql(c, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37' AND status='draft';").Count == 1, nativeReceipt);
                var nativeSnapshot = ReadCommunicationSnapshot(c, "main", "lord_1_14", 0, 50);
                Check("admission_diagnostics_retain_source_bound_receipt", ReadDictionaryList(nativeSnapshot, "admissions").Any(a => ReadString(a, "unit_id", "") == nativeUnit.EventId));
                var whileOpen = PersonalGossipConversationScheduling(c, "main", 91077.37496207407d, new List<string> { "lord_1_14" });
                var whileOpenSnapshot = ReadDictionary(ReadDictionaryList(whileOpen, "communications").Single(), "snapshot");
                Check("native_open_chat_observation_proves_unsent_draft", !ReadBool(whileOpen, "npcKnowledgeAuthority", true)
                    && ReadDictionaryList(whileOpenSnapshot, "messages").All(m => ReadString(m, "status", "") == "draft")
                    && ReadLong(whileOpenSnapshot, "messageCount", 0) == 1 && ReadLong(whileOpenSnapshot, "historyCount", -1) == 0);
                string originalNativeTurn = ReadStringList(nativeExchange, "turnIds").First(id => id.EndsWith("_player", StringComparison.Ordinal));
                var earlierTerms = new[] { TestDict("turnId", originalNativeTurn, "quote",
                    "Tell her that I offer 2,400 denars toward a school in Zeonica, provided the teachers are paid before any decoration. This is an offer for her to consider, not permission to pay anyone yet.") };
                var reopenPayload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 91077.37496207407d,
                    "heroStringId", "lord_1_14", "playerHeroStringId", "main_hero", "ts", 201L,
                    "conversationSessionId", "native_reaffirm_session", "sceneTurnId", "native_reaffirm_turn", "channel", "in_person",
                    "gossipPresence", ReadDictionary(nativePayload, "gossipPresence"), "playerText",
                    "Please keep that promised letter to your daughter Ira, with the same exact school offer.");
                ConversationFinishApi(TestDict("campaignId", campaign, "sessionId", ReadString(nativeExchange, "sessionId", ""),
                    "reason", "live_interaction_scene_boundary", "worldDay", 91077.37496207407d, "ts", 200L));
                var reaffirmStart = ConversationStartApi(reopenPayload);
                Check("reaffirmation_has_its_own_production_session", ReadBool(reaffirmStart, "ok", false), reaffirmStart);
                Check("earlier_own_terms_can_be_forwarded_by_exact_id", CommunicationContent(c, "lord_1_14", reopenPayload,
                    "native_reaffirm_session", Array.Empty<Dictionary<string, object>>(), earlierTerms).Count == 1);
                Check("earlier_other_owners_terms_remain_private", CommunicationContent(c, "unrelated_observer", reopenPayload,
                    "native_reaffirm_session", Array.Empty<Dictionary<string, object>>(), earlierTerms).Count == 0);
                var pastCutoff = CloneDictionary(reopenPayload); pastCutoff["memoryAsOfTimestamp"] = 199L;
                Check("earlier_terms_respect_time_cutoff", CommunicationContent(c, "lord_1_14", pastCutoff,
                    "native_reaffirm_session", Array.Empty<Dictionary<string, object>>(), earlierTerms).Count == 0);
                var sequenceCutoff = CloneDictionary(reopenPayload);
                sequenceCutoff["memoryAsOfAcceptedSequence"] = ReadLong(QuerySql(c, "SELECT accepted_sequence FROM conversation_turns WHERE turn_id=$id;",
                    TestDict("id", originalNativeTurn)).Single(), "accepted_sequence", 0) - 1;
                Check("earlier_terms_respect_sequence_cutoff", CommunicationContent(c, "lord_1_14", sequenceCutoff,
                    "native_reaffirm_session", Array.Empty<Dictionary<string, object>>(), earlierTerms).Count == 0);
                var wrongTimeline = CloneDictionary(reopenPayload); wrongTimeline["timelineId"] = "unrelated_branch";
                Check("earlier_terms_respect_timeline", CommunicationContent(c, "lord_1_14", wrongTimeline,
                    "native_reaffirm_session", Array.Empty<Dictionary<string, object>>(), earlierTerms).Count == 0);
                string reaffirmation = "The letter goes to Ira when you have gone your way — not before, and not with one word added or withheld.";
                nativePlan["evidenceQuote"] = reaffirmation; nativePlan["contentEvidence"] = earlierTerms;
                var reaffirmedUnit = new AcceptedConversationUnit { Campaign = campaign, Hero = "lord_1_14", Player = "main_hero", Location = "town_EW2",
                    HeroName = "Rhagaea", PlayerName = "Player", PlayerText = ReadString(reopenPayload, "playerText", ""), Reply = reaffirmation,
                    EventId = AcceptedConversationEventId(campaign, "lord_1_14", reopenPayload), EventType = "dialogue_turn", Timestamp = 201,
                    Generation = NpcLivesGeneration(c), Payload = reopenPayload, Profile = TestDict("name", "Rhagaea"), Motive = TestDict(),
                    Parsed = TestDict("reply", reaffirmation, "communicationPlans", new[] { nativePlan }), CommitEffects = false,
                    Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                var reaffirmedExchange = PublishAcceptedConversationUnit(reaffirmedUnit, accept => StoreConversationExchange(campaign, reopenPayload,
                    reaffirmedUnit.Hero, reaffirmedUnit.Player, reaffirmedUnit.PlayerName, reaffirmedUnit.HeroName, reaffirmedUnit.PlayerText, reaffirmation,
                    reaffirmedUnit.EventId, 201, acceptance: accept));
                Check("captured_impersonal_reaffirmation_rebinds_one_unsent_draft", ReadStringList(ReadDictionary(ReadDictionary(reaffirmedExchange, "acceptedUnit"), "communications"), "accepted").Count == 1
                    && QuerySql(c, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37';").Count == 1
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14';").Single(), "status", "") == "draft", reaffirmedExchange);
                var actualClose = TestDict("campaignId", campaign, "sessionId", ReadString(reaffirmedExchange, "sessionId", ""),
                    "reason", "closed_by_player", "worldDay", 91077.37496207407d, "ts", 202L);
                var closeResult = ConversationFinishApi(actualClose);
                Check("production_player_close_is_acknowledged", ReadBool(closeResult, "ok", false), closeResult);
                Check("captured_native_promise_releases_only_after_close", QuerySql(c,
                    "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37' AND status='queued';").Count == 1);
                ConversationFinishApi(actualClose);
                Check("production_close_retry_does_not_duplicate_dispatch", QuerySql(c,
                    "SELECT * FROM communication_messages WHERE speaker_id='lord_1_14' AND recipient_id='lord_1_37' AND status='queued';").Count == 1);
                const string rememberedTerms = "The offer is 2,400 denars for the school; teachers must be paid before decoration.";
                void LetterMemory(string id, string owner, string peer, string timeline, double day, long ts, long sequence, string body)
                {
                    ExecuteSql(c, @"INSERT INTO communication_history(receipt_id,message_id,timeline_id,owner_id,peer_id,acquired_day,acquired_ts,accepted_sequence,payload_json)
VALUES($id,$id,$timeline,$owner,$peer,$day,$ts,$sequence,$json);", TestDict("id", id, "timeline", timeline, "owner", owner, "peer", peer,
                        "day", day, "ts", ts, "sequence", sequence, "json", Json.Serialize(TestDict("body", body, "speakerId", peer))));
                }
                LetterMemory("reply_memory_valid", "memory_receiver", "memory_sender", "reply_memory", 5, 100, 10, rememberedTerms);
                LetterMemory("reply_memory_future_day", "memory_receiver", "memory_sender", "reply_memory", 11, 100, 11, "FUTURE_DAY_CONFIDENCE");
                LetterMemory("reply_memory_future_ts", "memory_receiver", "memory_sender", "reply_memory", 5, 151, 12, "FUTURE_TIMESTAMP_CONFIDENCE");
                LetterMemory("reply_memory_future_sequence", "memory_receiver", "memory_sender", "reply_memory", 5, 100, 21, "FUTURE_SEQUENCE_CONFIDENCE");
                LetterMemory("reply_memory_other_owner", "someone_else", "memory_sender", "reply_memory", 5, 100, 13, "OTHER_OWNER_CONFIDENCE");
                LetterMemory("reply_memory_other_peer", "memory_receiver", "third_party", "reply_memory", 5, 100, 14, "THIRD_PARTY_CONFIDENCE");
                LetterMemory("reply_memory_other_timeline", "memory_receiver", "memory_sender", "other_timeline", 5, 100, 15, "OTHER_TIMELINE_CONFIDENCE");
                var acquiredContext = TestDict("speakerId", "memory_receiver", "playerId", "main_hero", "timelineId", "reply_memory", "currentDay", 10d,
                    "memoryAsOfTimestamp", 150L, "memoryAsOfAcceptedSequence", 20L);
                var acquiredQuestion = TestDict("speaker_id", "memory_sender", "recipient_id", "memory_receiver", "kind", "ask");
                PrepareCommunicationReply(c, campaign, acquiredQuestion, acquiredContext);
                string acquiredMemory = ReadString(acquiredContext, "recipientMemory", "");
                Check("received_letter_supports_reply_without_prior_conversation", acquiredMemory.Contains(rememberedTerms, StringComparison.Ordinal)
                    && ReadDictionaryList(acquiredContext, "replyEvidence").Count == 1);
                Check("reply_letter_memory_keeps_owner_peer_timeline_and_asof_cutoffs", !acquiredMemory.Contains("CONFIDENCE", StringComparison.Ordinal));
                var acquiredAnswer = ComposeCommunicationReply(campaign, acquiredQuestion, TestDict("body", "What amount was offered, and what must be paid first?"), acquiredContext,
                    _ => TestDict("ok", true, "content", Json.Serialize(TestDict("body", "2,400 denars; teachers must be paid first.",
                        "answerKind", "answered", "sourceQuote", rememberedTerms, "privacy", "personal"))));
                Check("factual_answer_can_ground_on_acquired_letter", ReadBool(acquiredAnswer, "ok", false)
                    && ReadString(acquiredAnswer, "sourceQuote", "") == rememberedTerms);
                Check("unacquired_letters_cannot_support_factual_answer", !ReadBool(ComposeCommunicationReply(campaign, acquiredQuestion,
                    TestDict("body", "What was offered?"), acquiredContext, _ => TestDict("ok", true, "content", Json.Serialize(TestDict("body", "Invented terms.",
                        "answerKind", "answered", "sourceQuote", "FUTURE_DAY_CONFIDENCE", "privacy", "personal")))), "ok", true));
                var regressionPresence = CloneDictionary(ReadDictionary(reopenPayload, "gossipPresence"));
                var regressionPeople = ReadDictionaryList(regressionPresence, "people");
                regressionPeople.Single(p => GossipHeroId(p) == "lord_1_14")["settlementId"] = "town_ES5";
                var pharon = GossipTestHero("lord_1_15", "pharon_origin"); pharon["name"] = "Pharon"; regressionPeople.Add(pharon);
                regressionPresence["people"] = regressionPeople;
                var regressionPayload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 91077.37496207407d,
                    "playerHeroStringId", "main_hero", "heroStringId", "lord_1_14", "conversationSessionId", "reading_room_regression",
                    "channel", "in_person", "gossipPresence", regressionPresence);
                Dictionary<string, object> PublishRegression(string eventName, string playerText, string npcText, Dictionary<string, object> proposed, long timestamp)
                {
                    var p = CloneDictionary(regressionPayload); p["sceneTurnId"] = eventName; p["playerText"] = playerText;
                    ConversationStartApi(p);
                    var u = new AcceptedConversationUnit { Campaign = campaign, Hero = "lord_1_14", Player = "main_hero", Location = "town_ES5",
                        HeroName = "Rhagaea", PlayerName = "Player", PlayerText = playerText, Reply = npcText,
                        EventId = AcceptedConversationEventId(campaign, "lord_1_14", p), EventType = "dialogue_turn", Timestamp = timestamp,
                        Generation = NpcLivesGeneration(c), Payload = p, Profile = TestDict("name", "Rhagaea"), Motive = TestDict(),
                        Parsed = TestDict("reply", npcText, "communicationPlans", new[] { proposed }), CommitEffects = false,
                        Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                    return PublishAcceptedConversationUnit(u, accept => StoreConversationExchange(campaign, p, u.Hero, u.Player, u.PlayerName, u.HeroName,
                        playerText, npcText, u.EventId, timestamp, acceptance: accept));
                }
                const string originalRoom = "The proposed reading room in Zeonica would open at the third bell on market days. It is only a proposal, with no spending authorized.";
                var roomPlan = TestDict("key", "reading_room_regression", "operation", "send", "kind", "inform", "recipientId", "lord_1_37",
                    "evidenceQuote", "The message goes to Ira exactly as you gave it.", "contentEvidence", new[] { TestDict("turnId", "current_player", "quote", originalRoom) });
                var roomFirst = PublishRegression("room_initial", "Please write to your daughter Ira. " + originalRoom,
                    "The message goes to Ira exactly as you gave it.", roomPlan, 300);
                Check("reading_room_regression_initial_admission", ReadStringList(ReadDictionary(ReadDictionary(roomFirst, "acceptedUnit"), "communications"), "accepted").Count == 1, roomFirst);
                string roomId = ReadStringList(ReadDictionary(ReadDictionary(roomFirst, "acceptedUnit"), "communications"), "accepted").Single();
                roomPlan["evidenceQuote"] = "If you pay, I will write to Ira.";
                roomPlan["contentEvidence"] = new[] { TestDict("turnId", "current_player", "quote", "Change the third bell to the second bell.") };
                PublishRegression("room_invalid_revision", "Change the third bell to the second bell.", "If you pay, I will write to Ira.", roomPlan, 301);
                Check("rejected_revision_quarantines_original_production_draft", ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;",
                    TestDict("id", roomId)).Single(), "status", "") == "needs_clarification");
                ConversationFinishApi(TestDict("campaignId", campaign, "sessionId", ReadString(roomFirst, "sessionId", ""), "reason", "closed_by_player", "worldDay", 91077.37496207407d, "ts", 302L));
                Check("production_close_cannot_release_stale_corrected_draft", QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id AND status='queued';", TestDict("id", roomId)).Count == 0);
                regressionPayload["conversationSessionId"] = "reading_room_reconfirmed";
                const string correctedRoom = "Only the revised terms travel to my daughter: the proposed reading room in Zeonica would open at the second bell on market days, and entry would be free. A proposal only, no spending authorized by anyone.";
                roomPlan["evidenceQuote"] = correctedRoom; roomPlan["contentEvidence"] = new[] { TestDict("turnId", "current_npc", "quote", correctedRoom) };
                var roomRevised = PublishRegression("room_reconfirmed", "Please write to Ira with the second bell, free entry and the same no-spending condition.", correctedRoom, roomPlan, 303);
                var finalRoom = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", roomId)).Single();
                Check("captured_recipient_receipt_promise_resumes_same_draft_with_complete_final_terms", ReadString(finalRoom, "status", "") == "draft"
                    && ReadInt(finalRoom, "revision", 0) == 2 && ReadStringList(ReadDictionary(ReadDictionary(roomRevised, "acceptedUnit"), "communications"), "accepted").Single() == roomId
                    && Json.Serialize(GossipJson(finalRoom)).Contains("no spending authorized", StringComparison.Ordinal));
                const string roomWithdrawal = "Withdrawn, then, Your Majesty. The second bell dies with the third, the free entry with both, and nothing of the reading room travels to Ira at all.";
                roomPlan["operation"] = "cancel"; roomPlan["evidenceQuote"] = roomWithdrawal;
                roomPlan["contentEvidence"] = new[] { TestDict("turnId", "current_player", "quote", "Cancel the whole reading-room note to Ira.") };
                var roomCancelled = PublishRegression("room_cancelled", "Cancel the whole reading-room note to Ira.", roomWithdrawal, roomPlan, 304);
                ConversationFinishApi(TestDict("campaignId", campaign, "sessionId", ReadString(roomCancelled, "sessionId", ""), "reason", "closed_by_player", "worldDay", 91077.37496207407d, "ts", 305L));
                Check("captured_withdrawal_cancels_before_production_close", ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", roomId)).Single(), "status", "") == "cancelled"
                    && QuerySql(c, "SELECT * FROM communication_history WHERE message_id=$id;", TestDict("id", roomId)).Count == 0);
                regressionPayload["conversationSessionId"] = "pharon_summon_regression";
                const string summonsReply = "The summons goes to Pharon when this conversation ends. He is to join me at Onira, where I am actually staying; he comes alone, his troops safe in their camp; and he remains with me one full day after he arrives. When he stands in my hall, I will tell him myself: daughters of working families may enter on the same terms as sons.";
                var summonPlan = TestDict("key", "pharon_summon_regression", "operation", "send", "kind", "summon_then_inform", "recipientId", "lord_1_15",
                    "evidenceQuote", "The summons goes to Pharon when this conversation ends.", "destinationId", "town_ES5", "stayDays", 1d,
                    "contentEvidence", new[] { TestDict("turnId", "current_player", "quote", "Tell him at arrival that daughters of working families may enter on the same terms as sons.") });
                var summonAccepted = PublishRegression("pharon_summon_terms", "Send for Pharon to come alone for one full day. Tell him at arrival that daughters of working families may enter on the same terms as sons.", summonsReply, summonPlan, 306);
                Check("summons_stay_can_be_stated_elsewhere_in_same_accepted_reply", ReadStringList(ReadDictionary(ReadDictionary(summonAccepted, "acceptedUnit"), "communications"), "accepted").Count == 1, summonAccepted);
                string summonId = ReadStringList(ReadDictionary(ReadDictionary(summonAccepted, "acceptedUnit"), "communications"), "accepted").Single();
                var summonRow = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", summonId)).Single();
                var summonSession = QuerySql(c, "SELECT * FROM conversation_sessions WHERE session_id=$id;", TestDict("id", ReadString(summonRow, "session_id", ""))).Single();
                const double hostDay = 91077.37496207407d;
                ReleaseCommunicationSession(c, summonSession, "closed_by_player", hostDay);
                summonRow = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", summonId)).Single();
                var holdData = GossipJson(summonRow); var hostWork = CommunicationHostWork(c, "main").Single(w => ReadString(w, "id", "") == summonId);
                Check("closed_summons_waits_for_native_host_before_dispatch", ReadString(summonRow, "status", "") == "awaiting_host"
                    && ReadString(hostWork, "reservationHash", "").Length > 0 && ReadDouble(hostWork, "expiresDay", 0) == hostDay + 14);
                AdvanceCommunications(c, campaign, "main", hostDay, regressionPeople);
                Check("unreserved_summons_has_no_dispatch_or_receipt", ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", summonId)).Single(), "status", "") == "awaiting_host"
                    && QuerySql(c, "SELECT * FROM communication_history WHERE message_id=$id;", TestDict("id", summonId)).Count == 0);
                var hostReceipt = TestDict("communicationId", summonId, "kind", "host_reserved", "hostId", "lord_1_14", "destinationId", "town_ES5",
                    "reservationHash", ReadString(holdData, "hostReservationHash", ""));
                bool mismatchRejected = false;
                var forgedHost = CloneDictionary(hostReceipt); forgedHost["hostId"] = "main_hero";
                try { AcceptCommunicationNativeReceipt(c, new NpcLivesState { TimelineId = "main" }, forgedHost, hostDay); }
                catch (InvalidOperationException) { mismatchRejected = true; }
                Check("host_receipt_cannot_reserve_a_different_actor", mismatchRejected);
                AcceptCommunicationNativeReceipt(c, new NpcLivesState { TimelineId = "main" }, hostReceipt, hostDay);
                AcceptCommunicationNativeReceipt(c, new NpcLivesState { TimelineId = "main" }, hostReceipt, hostDay);
                summonRow = QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", summonId)).Single();
                Check("matching_native_host_receipt_releases_once", ReadString(summonRow, "status", "") == "queued"
                    && ReadDouble(GossipJson(summonRow), "hostReservedDay", 0) == hostDay);
                SetCommunicationStatus(c, summonRow, "awaiting_reply", GossipJson(summonRow));
                Check("host_stays_while_refusal_return_is_still_unknown", CommunicationHostWork(c, "main").Any(w => ReadString(w, "id", "") == summonId));
                SetCommunicationStatus(c, summonRow, "replied", GossipJson(summonRow));
                Check("acquired_refusal_releases_invitation_host", !CommunicationHostWork(c, "main").Any(w => ReadString(w, "id", "") == summonId));
                SetCommunicationStatus(c, summonRow, "queued", GossipJson(summonRow));
                ExpireCommunicationHostWaits(c, "main", hostDay + 14);
                Check("host_wait_has_bounded_expiry_and_cannot_be_reanimated", ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", summonId)).Single(), "status", "") == "expired"
                    && !CommunicationHostWork(c, "main").Any(w => ReadString(w, "id", "") == summonId));
                AcceptCommunicationNativeReceipt(c, new NpcLivesState { TimelineId = "main" }, hostReceipt, hostDay + 14);
                Check("late_host_ack_does_not_reopen_expired_invitation", ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", summonId)).Single(), "status", "") == "expired");
                var directAskRow = TestDict("speaker_id", "queen", "recipient_id", "pharon", "player_id", "player", "kind", "ask");
                var directAskData = TestDict("speakerName", "Rhagaea", "contentEvidence", new[] { TestDict("speakerId", "player", "quote", "Ask Pharon to repeat Olive 17 and whether this note authorizes payment; it authorizes no spending.") });
                directAskData["body"] = CommunicationBody(directAskRow, directAskData);
                bool directReplyFraming = false;
                var directAnswer = ComposeCommunicationReply(campaign, directAskRow, directAskData, TestDict("recipientMemory", ReadString(directAskData, "body", "")), request => {
                    string system = ReadString(ReadDictionaryList(request, "messages").First(), "content", "");
                    directReplyFraming = system.Contains("IS your actual return letter", StringComparison.Ordinal)
                        && ReadString(directAskData, "body", "").Contains("named recipient mean you", StringComparison.Ordinal);
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("body", "The reference is Olive 17. No payment is authorized.", "answerKind", "answered",
                        "sourceQuote", "Ask Pharon to repeat Olive 17 and whether this note authorizes payment; it authorizes no spending.", "privacy", "personal")));
                });
                Check("first_received_question_is_addressed_to_recipient_for_actual_return_letter", directReplyFraming && ReadBool(directAnswer, "ok", false));
                regressionPayload["conversationSessionId"] = "pharon_pronoun_regression";
                var pronounPlan = CloneDictionary(summonPlan); pronounPlan["key"] = "pharon_pronoun_regression";
                pronounPlan["evidenceQuote"] = "It goes out tomorrow, and I will be at Onira, waiting.";
                pronounPlan["delayDays"] = 1d;
                var pronounAccepted = PublishRegression("pharon_pronoun", "Send for Pharon to come alone for one full day. Tell him at arrival that daughters of working families may enter on the same terms as sons.",
                    referredInvitation, pronounPlan, 307);
                Check("context_bound_pronoun_is_accepted_by_production_publication", ReadStringList(ReadDictionary(ReadDictionary(pronounAccepted, "acceptedUnit"), "communications"), "accepted").Count == 1, pronounAccepted);
                regressionPayload["conversationSessionId"] = "birch_key_alias_regression";
                const string birchContent = "the planning reference is Birch 12, a proposal only with no spending authorized";
                var birchPlan = TestDict("key", "ira_birch12_note", "operation", "send", "kind", "inform", "recipientId", "lord_1_37",
                    "evidenceQuote", "I will write to Ira.", "contentEvidence", new[] { TestDict("turnId", "current_player", "quote", birchContent) });
                var birchFirst = PublishRegression("birch_initial", "Please write Ira a separate note after we finish: " + birchContent + ".",
                    "I will write to Ira. My daughter will receive exactly the Birch 12 proposal, with no spending authorized.", birchPlan, 308);
                var birchIds = ReadStringList(ReadDictionary(ReadDictionary(birchFirst, "acceptedUnit"), "communications"), "accepted");
                Check("recipient_family_binding_uses_complete_same_speaker_reply", birchIds.Count == 1, birchFirst);
                string birchId = birchIds.Single();
                var birchCancel = TestDict("key", "birch12_note", "operation", "cancel", "kind", "inform", "recipientId", "lord_1_37",
                    "evidenceQuote", "Cancelled, and cleanly — nothing has left my hand.");
                var birchWithdrawn = PublishRegression("birch_withdrawn", "Cancel that whole Birch 12 note to your daughter Ira.",
                    "Cancelled, and cleanly — nothing has left my hand.", birchCancel, 309);
                ConversationFinishApi(TestDict("campaignId", campaign, "sessionId", ReadString(birchWithdrawn, "sessionId", ""), "reason", "closed_by_player", "worldDay", hostDay, "ts", 310L));
                Check("withdrawal_key_variation_binds_same_single_unsent_draft", ReadStringList(ReadDictionary(ReadDictionary(birchWithdrawn, "acceptedUnit"), "communications"), "accepted").Single() == birchId
                    && ReadString(QuerySql(c, "SELECT * FROM communication_messages WHERE message_id=$id;", TestDict("id", birchId)).Single(), "status", "") == "cancelled"
                    && QuerySql(c, "SELECT * FROM communication_history WHERE message_id=$id;", TestDict("id", birchId)).Count == 0);
                regressionPayload["conversationSessionId"] = "ambiguous_withdrawal_regression";
                var northPlan = CloneDictionary(birchPlan); northPlan["key"] = "north_note";
                var southPlan = CloneDictionary(birchPlan); southPlan["key"] = "south_note";
                PublishRegression("north_note", "Please write to your daughter Ira: " + birchContent + ".", "I will write to Ira.", northPlan, 311);
                PublishRegression("south_note", "Please write a separate note to your daughter Ira: " + birchContent + ".", "I will write to Ira.", southPlan, 312);
                var unclearCancel = CloneDictionary(birchCancel); unclearCancel["key"] = "unknown_note";
                var unclear = PublishRegression("unclear_withdrawal", "Cancel that note to your daughter Ira.", "Cancelled, and cleanly — nothing has left my hand.", unclearCancel, 313);
                ConversationFinishApi(TestDict("campaignId", campaign, "sessionId", ReadString(unclear, "sessionId", ""), "reason", "closed_by_player", "worldDay", hostDay, "ts", 314L));
                var ambiguousDrafts = QuerySql(c, "SELECT * FROM communication_messages WHERE topic_key IN ('north_note','south_note');");
                Check("ambiguous_withdrawal_holds_candidates_instead_of_sending_or_picking_one", ambiguousDrafts.Count == 2
                    && ambiguousDrafts.All(row => ReadString(row, "status", "") == "needs_clarification")
                    && ambiguousDrafts.All(row => QuerySql(c, "SELECT * FROM communication_history WHERE message_id=$id;", TestDict("id", ReadString(row, "message_id", ""))).Count == 0));

                const string semanticQuote = "Tomorrow a sealed summons rides to Pharon, asking his own fresh word — acceptance or refusal, owing nothing to the Onira invitation.";
                const string semanticReply = semanticQuote + " He is to meet me here and remain for one full day after arrival.";
                const string semanticRequest = "Please send for Pharon. Tell him after arrival that the proposed register is Lantern Nine; no spending is authorized.";
                regressionPayload["conversationSessionId"] = "semantic_commitment_regression";
                var semanticPayload = CloneDictionary(regressionPayload); semanticPayload["sceneTurnId"] = "semantic_commitment";
                semanticPayload["playerText"] = semanticRequest;
                var semanticPlan = CloneDictionary(summonPlan); semanticPlan["key"] = "semantic_commitment";
                semanticPlan["evidenceQuote"] = semanticQuote; semanticPlan["delayDays"] = 1d;
                semanticPlan["contentEvidence"] = new[] { TestDict("turnId", "current_player", "quote", semanticRequest) };
                semanticPlan["unconditionalSpeakerCommitment"] = true;
                var semanticOriginal = TestDict("reply", semanticReply, "communicationPlans", new[] { semanticPlan });
                Check("primary_model_boolean_cannot_authorize_semantic_fallback", ApplyCommunicationPlanPatch(semanticOriginal,
                    TestDict("communicationPlans", new[] { semanticPlan })) == null);
                int semanticCalls = 0;
                var semanticEnvelope = RepairMissingCommunicationPlans(TestDict("ok", true, "content", Json.Serialize(semanticOriginal)),
                    semanticPayload, campaign, "test_semantic_commitment", "dialogue", "lord_1_14", _ => {
                        semanticCalls++; return TestDict("ok", true, "content", Json.Serialize(TestDict("communicationPlans", new[] { semanticPlan })));
                    });
                var semanticReviewed = ReadDictionaryList(TryParseJsonObject(ReadString(semanticEnvelope, "content", "")), "communicationPlans").Single();
                var semanticUnit = new AcceptedConversationUnit { Campaign = campaign, Hero = "lord_1_14", Reply = semanticReply,
                    EventId = AcceptedConversationEventId(campaign, "lord_1_14", semanticPayload), Generation = NpcLivesGeneration(c) };
                Check("noncanonical_own_commitment_uses_one_sealed_semantic_review", semanticCalls == 1 && ReadBool(semanticEnvelope, "ok", false)
                    && CommunicationSemanticAccepted(semanticReviewed, semanticUnit)
                    && ReadFirstString(TryParseJsonObject(ReadString(semanticEnvelope, "content", "")), "reply") == semanticReply);
                foreach (string field in new[] { "key", "recipientId", "kind", "operation", "destinationId", "evidenceQuote", "stayDays", "delayDays", "contentEvidence" })
                {
                    var changed = CloneDictionary(semanticReviewed);
                    changed[field] = field == "stayDays" || field == "delayDays" ? 2d : field == "contentEvidence"
                        ? new[] { TestDict("turnId", "current_player", "quote", "Altered terms") } : "altered";
                    Check("semantic_seal_rejects_changed_" + field, !CommunicationSemanticAccepted(changed, semanticUnit));
                }
                string semanticEvent = semanticUnit.EventId, semanticGeneration = semanticUnit.Generation;
                semanticUnit.EventId += "_other"; Check("semantic_seal_rejects_other_turn", !CommunicationSemanticAccepted(semanticReviewed, semanticUnit));
                semanticUnit.EventId = semanticEvent; semanticUnit.Generation += "_restored";
                Check("semantic_seal_rejects_restored_generation", !CommunicationSemanticAccepted(semanticReviewed, semanticUnit));
                semanticUnit.Generation = semanticGeneration; semanticUnit.Hero = "lord_1_15";
                Check("semantic_seal_rejects_other_speaker", !CommunicationSemanticAccepted(semanticReviewed, semanticUnit));
                semanticUnit.Hero = "lord_1_14"; semanticUnit.Reply += " Altered speech.";
                Check("semantic_seal_rejects_changed_reply", !CommunicationSemanticAccepted(semanticReviewed, semanticUnit));
                semanticUnit.Reply = semanticReply;
                var forgedSemantic = CloneDictionary(semanticReviewed); forgedSemantic["semanticCommitmentReceipt"] = new string('0', 64);
                Check("semantic_seal_rejects_forged_receipt", !CommunicationSemanticAccepted(forgedSemantic, semanticUnit));
                foreach (string rejected in new[] { "I will not send for Pharon.", "If you pay, a summons rides to Pharon.",
                    "She said a summons rides to Pharon.", "Perhaps a summons rides to Pharon.", "A summons might ride to Pharon." })
                {
                    var semanticNegative = CloneDictionary(semanticPlan); semanticNegative["evidenceQuote"] = rejected;
                    Check("semantic_review_rejects_clear_noncommitment_" + PromptHash(rejected).Substring(0, 8),
                        ApplyCommunicationPlanPatch(TestDict("reply", rejected), TestDict("communicationPlans", new[] { semanticNegative }), allowSemanticReview: true) == null);
                }
                Check("semantic_review_rejects_quoted_and_narrated_sources", !CommunicationSemanticCandidate(semanticQuote, "She said: “" + semanticQuote + "”")
                    && !CommunicationSemanticCandidate(semanticQuote, "*" + semanticQuote + "*")
                    && !CommunicationSemanticCandidate(semanticQuote, "She said: " + semanticQuote));
                var semanticAccepted = PublishRegression("semantic_commitment", semanticRequest, semanticReply, semanticReviewed, 315);
                Check("sealed_semantic_plan_passes_production_publication", ReadStringList(ReadDictionary(ReadDictionary(semanticAccepted,
                    "acceptedUnit"), "communications"), "accepted").Count == 1, semanticAccepted);
            }
            catch (Exception ex) { Check("fixture_execution", false, ex.ToString()); }
            finally { CommunicationPlayers.TryRemove(campaign, out _); ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
