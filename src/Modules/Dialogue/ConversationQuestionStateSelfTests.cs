using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationQuestionStateAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "question_state_" + id,
                "passed", pass, "summary", id, "data", data));
            string campaign = "question_test_" + Guid.NewGuid().ToString("N");
            var payload = TestDict("campaignId", campaign, "timelineId", "question_timeline", "worldDay", 5d,
                "playerHeroStringId", "player", "conversationSessionId", "question_session", "sceneTurnId", "beat_1",
                "playerText", "I have a plan.");
            const string question = "How will we build an independent nation?";
            const string answer = "Tournaments fund neutral caravans, intelligence and elite units, then we buy land from indebted lords.";
            const string ack = "I understand the plan you explained, though I am not convinced it will work.";
            try
            {
                ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "question_timeline", "sessionId", "question_session",
                    "npcId", "npc", "playerId", "player", "worldDay", 5d, "ts", 1L));
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureConversationContinuitySchema(connection);
                    void Turn(string id, string eventId, string who, string text, int order, long timestamp) =>
                        InsertConversationTurn(connection, id, "question_session", eventId, order, eventId, who == "player" ? "player" : "npc",
                            who, who, text, "in_person", 5d, timestamp, "town", new List<string> { "npc", "player" }, payload);
                    Turn("old_answer", "before_question", "player", "We could first secure the old bridge.", 0, 1L);
                    Turn("plan_question", "question_event", "npc", question, 1, 2L);
                    var proposed = TestDict("id", "question_plan", "kind", "question", "peerId", "player", "subject", "Independent-nation plan",
                        "status", "active", "evidenceQuote", question, "expectedRevision", 0,
                        "questionState", TestDict("question", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", question),
                            "addresseeIds", new[] { "player" }, "answerState", "unanswered", "answers", new List<Dictionary<string, object>>(),
                            "evaluation", "unassessed", "followUpTo", ""));
                    var initial = StoreAcceptedConversationContinuity(connection, payload, "npc", "player", "question_event", question,
                        new List<Dictionary<string, object>> { proposed }, 2L);
                    Check("question_binds_original_asker", ReadStringList(initial, "applied").Contains("question_plan"), initial);

                    payload["playerText"] = answer; payload["sceneTurnId"] = "beat_2";
                    CaptureContinuityReadSnapshot(connection, payload, "npc");
                    Turn("plan_answer", "answer_event", "player", answer, 2, 3L);
                    Turn("plan_ack", "answer_event", "npc", ack, 3, 4L);
                    var resolved = CloneDictionary(proposed);
                    resolved["evidenceQuote"] = ack; resolved["expectedRevision"] = 99;
                    resolved["questionState"] = TestDict("question", TestDict("turnId", "plan_question", "speakerId", "npc", "quote", question),
                        "addresseeIds", new[] { "player" }, "answerState", "answered",
                        "answers", new[] { TestDict("turnId", "current_player", "speakerId", "player", "quote", answer) },
                        "evaluation", "unconvincing", "followUpTo", "");
                    var accepted = StoreAcceptedConversationContinuity(connection, payload, "npc", "player", "answer_event", ack,
                        new List<Dictionary<string, object>> { resolved }, 4L);
                    Check("answered_and_unconvincing_can_both_persist", ReadStringList(accepted, "applied").Contains("question_plan")
                        && ReadString(QuerySql(connection, "SELECT status FROM conversation_continuity WHERE record_id='question_plan';").Single(), "status", "") == "resolved", accepted);
                    var next = new Dictionary<string, object>(payload);
                    var required = new List<string>();
                    string prompt = BuildQuestionContinuityPrompt(connection, next, "npc", "player", required);
                    Check("next_request_keeps_question_answer_and_skepticism", prompt.Contains(question) && prompt.Contains(answer)
                        && prompt.Contains("answer received=answered") && prompt.Contains("unconvincing") && required.Contains("question_plan"), prompt);
                    Check("missing_original_is_not_claimed_accurate", ReadString(ReadDictionary(next, "questionStateEvidence"), "coverage", "") == "selected_originals_verified");

                    var wrongActor = CloneDictionary(resolved); wrongActor["id"] = "";
                    ReadDictionaryList(ReadDictionary(wrongActor, "questionState"), "answers")[0]["speakerId"] = "npc";
                    Check("wrong_answer_actor_rejected", PrepareQuestionContinuityWrite(connection, wrongActor, payload, "npc", "player", "answer_event", ack) == "question_answer_source_unverified");
                    var preceding = CloneDictionary(resolved); preceding["id"] = "";
                    ReadDictionary(preceding, "questionState")["answers"] = new[] { TestDict("turnId", "old_answer", "speakerId", "player", "quote", "We could first secure the old bridge.") };
                    Check("old_answer_cannot_answer_later_question", PrepareQuestionContinuityWrite(connection, preceding, payload, "npc", "player", "answer_event", ack) == "question_answer_precedes_question");
                    var noAnswer = CloneDictionary(resolved); noAnswer["id"] = "";
                    ReadDictionary(noAnswer, "questionState")["answers"] = new List<Dictionary<string, object>>();
                    Check("asserting_answer_without_source_rejected", PrepareQuestionContinuityWrite(connection, noAnswer, payload, "npc", "player", "answer_event", ack) == "question_answer_required");

                    const string detail = "Which town would we approach first?";
                    Turn("town_question", "detail_event", "npc", detail, 4, 5L);
                    var moving = CloneDictionary(proposed);
                    moving["evidenceQuote"] = detail;
                    moving["questionState"] = TestDict("question", TestDict("turnId", "town_question", "speakerId", "npc", "quote", detail),
                        "addresseeIds", new[] { "player" }, "answerState", "unanswered", "answers", new List<Dictionary<string, object>>(),
                        "evaluation", "unassessed", "followUpTo", "");
                    Check("new_detail_cannot_replace_old_question", PrepareQuestionContinuityWrite(connection, moving, payload, "npc", "player", "detail_event", detail) == "question_identity_changed");
                    moving["id"] = ""; moving["subject"] = "First town to approach";
                    ReadDictionary(moving, "questionState")["followUpTo"] = "question_plan";
                    Check("narrower_followup_has_distinct_identity", PrepareQuestionContinuityWrite(connection, moving, payload, "npc", "player", "detail_event", detail) == ""
                        && ReadString(moving, "id", "") != "question_plan");
                    var refusedReopen = CloneDictionary(resolved);
                    ReadDictionary(refusedReopen, "questionState")["question"] = TestDict("turnId", "plan_question", "speakerId", "npc", "quote", question);
                    ReadDictionary(refusedReopen, "questionState")["answers"] = new List<Dictionary<string, object>>();
                    ReadDictionary(refusedReopen, "questionState")["answerState"] = "unanswered";
                    ReadDictionary(refusedReopen, "questionState")["evaluation"] = "unassessed";
                    Check("repetition_does_not_reopen_answered_question", PrepareQuestionContinuityWrite(connection, refusedReopen, payload, "npc", "player", "detail_event", ack) == "answered_question_requires_explicit_reopening");

                    var unseen = CloneDictionary(proposed); unseen["id"] = "";
                    ReadDictionary(unseen, "questionState")["question"] = TestDict("turnId", "plan_question", "speakerId", "npc", "quote", question);
                    ReadDictionary(unseen, "questionState")["addresseeIds"] = new[] { "absent_npc" };
                    Check("absent_actor_is_not_obliged_to_answer", PrepareQuestionContinuityWrite(connection, unseen, payload, "npc", "player", "question_event", question) == "question_addressee_unverified");
                    var earlier = new Dictionary<string, object>(payload) { ["worldDay"] = 4d };
                    Check("future_answer_excluded_on_rewind", !BuildQuestionContinuityPrompt(connection, earlier, "npc", "player", new List<string>()).Contains(answer));
                    var branch = new Dictionary<string, object>(payload) { ["timelineId"] = "different_branch" };
                    Check("other_branch_cannot_inherit_answer", !BuildQuestionContinuityPrompt(connection, branch, "npc", "player", new List<string>()).Contains(answer));
                    Check("other_observer_does_not_inherit_private_evaluation", !BuildQuestionContinuityPrompt(connection, payload, "other", "player", new List<string>()).Contains(answer));

                    string content = "question_plan: NPC asked how; Player answered with caravans. NPC acknowledged the answer.";
                    var contentEvidence = TestDict("requiredContent", new[] { TestDict("id", "qa", "text", content, "hash", PromptHash(content)) });
                    Check("source_id_without_answer_content_fails_coverage", MissingContinuityContent("question_plan", contentEvidence).Contains("qa"));
                    Check("complete_content_survives_provider_serialization", MissingContinuityContent(Json.Serialize(new[] { TestDict("role", "system", "content", content) }), contentEvidence, true).Count == 0);
                    var alteredEvidence = CloneDictionary(contentEvidence);
                    ReadDictionaryList(alteredEvidence, "requiredContent")[0]["text"] = "question_plan only";
                    Check("tampered_content_hash_fails_coverage", MissingContinuityContent("question_plan only", alteredEvidence).Contains("qa"));
                    string rawIdentityContent = "question_id: Michael asked how.  \r\nPlayer answered with caravans.\r\nNPC acknowledged the answer.";
                    var projectedEvidence = TestDict("continuityEvidence", TestDict("requiredContent", new[] {
                        TestDict("id", "qa_identity", "text", rawIdentityContent, "hash", PromptHash(rawIdentityContent)) }),
                        "memoryPrecisionEvidence", TestDict("mode", "precision", "records", new[] {
                            TestDict("alias", "m_identity", "required", true, "rendered", rawIdentityContent, "textHash", PromptHash(rawIdentityContent)) }));
                    var stranger = TestDict("canonicalNameAllowed", false, "usableName", "the stranger");
                    ProjectConversationEvidenceForIdentity(projectedEvidence, "", "Michael", stranger);
                    string projectedText = "question_id: the stranger asked how.\nPlayer answered with caravans.\nNPC acknowledged the answer.";
                    Check("observer_identity_projection_preserves_answer_content",
                        MissingContinuityContent(projectedText, ReadDictionary(projectedEvidence, "continuityEvidence")).Count == 0
                        && MissingPrecisionEvidence(projectedText, ReadDictionary(projectedEvidence, "memoryPrecisionEvidence")).Count == 0);
                    Check("identity_projection_cannot_bless_dropped_answer",
                        MissingContinuityContent("question_id: the stranger asked how.", ReadDictionary(projectedEvidence, "continuityEvidence")).Count > 0);
                    var whitespaceEvidence = TestDict("requiredContent", new[] { TestDict("id", "qa_whitespace", "text", rawIdentityContent, "hash", PromptHash(rawIdentityContent)) });
                    Check("canonical_prompt_whitespace_is_not_lost_evidence",
                        MissingContinuityContent(NormalizePromptSegment(rawIdentityContent), whitespaceEvidence).Count == 0);
                    var invalidProjection = TestDict("continuityEvidence", alteredEvidence);
                    ProjectConversationEvidenceForIdentity(invalidProjection, "", "Michael", stranger);
                    Check("identity_projection_does_not_rehash_tampered_content", MissingContinuityContent("question_plan only", alteredEvidence).Count > 0);

                    var group = TestDict("mode", "party_chat", "conversationSessionId", "group_questions", "sceneTurnId", "group_beat_1",
                        "timelineId", "question_timeline", "worldDay", 5d, "playerHeroStringId", "player", "playerText", "I have described our plan.",
                        "participantHeroIds", new[] { "npc", "npc_b", "player" });
                    UpdateSharedGroupConversationState(campaign, group, "npc", "Speaker A", question, "group_questions", 20L, "group_event_1");
                    string sameBeat = BuildSharedGroupConversationPrompt(campaign, group, "npc_b", new List<Dictionary<string, object>>(), "I have described our plan.");
                    Check("new_question_does_not_grant_player_a_turn", sameBeat.Contains("none yet") && !sameBeat.Contains("Unresolved questions:"), sameBeat);
                    UpdateSharedGroupConversationState(campaign, group, "npc_b", "Speaker B", "I prefer to listen to that answer first.", "group_questions", 21L, "group_event_2");
                    Check("last_npc_does_not_erase_prior_question", BuildSharedGroupConversationPrompt(campaign, group, "npc", new List<Dictionary<string, object>>(), "I have described our plan.").Contains(question));
                    long revision = ReadLong(QuerySql(connection, "SELECT revision FROM group_conversation_state WHERE session_id='group_questions';").Single(), "revision", 0);
                    UpdateSharedGroupConversationState(campaign, group, "npc", "Speaker A", "A retried response must not replace delivered speech.", "group_questions", 22L, "group_retry");
                    Check("retry_does_not_add_delivery_or_state_revision", ReadLong(QuerySql(connection, "SELECT revision FROM group_conversation_state WHERE session_id='group_questions';").Single(), "revision", 0) == revision
                        && QuerySql(connection, "SELECT * FROM group_conversation_contributions WHERE session_id='group_questions' AND role='npc';").Count == 2);
                    group["sceneTurnId"] = "group_beat_2";
                    group["repairStatus"] = TestDict("allChecksCleared", false);
                    UpdateSharedGroupConversationState(campaign, group, "npc", "Speaker A", "Plain failed repair without punctuation", "group_questions", 23L, "group_failed");
                    Check("repeated_player_words_in_new_beat_are_not_deduplicated", QuerySql(connection,
                        "SELECT * FROM group_conversation_contributions WHERE session_id='group_questions' AND role='player';").Count == 2);
                    Check("structured_repair_failure_is_authority", ReadString(QuerySql(connection,
                        "SELECT validation_status FROM group_conversation_contributions WHERE source_event_id='group_failed';").Single(), "validation_status", "") == "visible_failed_repair");
                    group["sceneTurnId"] = "group_beat_3"; group["repairStatus"] = TestDict("allChecksCleared", true);
                    UpdateSharedGroupConversationState(campaign, group, "npc", "Speaker A", "A successful structured result.,", "group_questions", 24L, "group_success");
                    Check("punctuation_cannot_override_structured_success", ReadString(QuerySql(connection,
                        "SELECT validation_status FROM group_conversation_contributions WHERE source_event_id='group_success';").Single(), "validation_status", "") == "accepted_speech");
                    string laterBeat = BuildSharedGroupConversationPrompt(campaign, group, "npc_b", new List<Dictionary<string, object>>(), "I have described our plan.");
                    Check("later_player_opportunity_is_not_evasion_verdict", laterBeat.Contains("later player contribution exists") && laterBeat.Contains("does not classify it as unanswered"), laterBeat);
                    var otherTimeline = new Dictionary<string, object>(group) { ["timelineId"] = "other_branch" };
                    Check("group_question_branch_isolation", !BuildSharedGroupConversationPrompt(campaign, otherTimeline, "npc_b", new List<Dictionary<string, object>>(), "Continue").Contains(question));
                    var futureState = new Dictionary<string, object>(group) { ["worldDay"] = 4d };
                    Check("group_question_rewind_isolation", !BuildSharedGroupConversationPrompt(campaign, futureState, "npc_b", new List<Dictionary<string, object>>(), "Continue").Contains(question));
                    Check("private_narrated_question_is_not_a_spoken_demand", GroupSpokenQuestionFrames("*Would he discover my secret?* Is the road clear?", "source", "npc", "beat", new List<string> { "npc", "player" })
                        .Select(q => ReadString(q, "quote", "")).SequenceEqual(new[] { "Is the road clear?" }));

                    const string request = "Tell me how you would fund the purchase.";
                    const string conditionalRequest = "If you can explain how you would cover the operating costs, I can judge the proposal.";
                    const string requestAnswer = "I would reserve half of the profits before paying the operating costs.";
                    const string requestAck = "I heard your answer about setting half the profits aside, though I remain skeptical.";
                    Turn("information_request", "request_event", "npc", "*He sits back.*\n\nSo. " + request, 100, 100L);
                    Turn("information_answer", "request_answer_event", "player", requestAnswer, 101, 101L);
                    Turn("information_ack", "request_answer_event", "npc", requestAck, 102, 102L);
                    var requestPayload = new Dictionary<string, object>(payload) { ["playerText"] = requestAnswer };
                    var requestWrite = TestDict("id", "information_request_record", "kind", "question", "peerId", "player",
                        "subject", "Funding the purchase", "evidenceQuote", requestAck, "expectedRevision", 0,
                        "questionState", TestDict("question", TestDict("turnId", "information_request", "speakerId", "npc", "quote", request),
                            "addresseeIds", new[] { "player" }, "answerState", "answered",
                            "answers", new[] { TestDict("turnId", "information_answer", "speakerId", "player", "quote", requestAnswer) },
                            "evaluation", "unconvincing", "followUpTo", ""));
                    var requestStored = StoreAcceptedConversationContinuity(connection, requestPayload, "npc", "player", "request_answer_event",
                        requestAck, new[] { requestWrite }.ToList(), 102L);
                    Check("imperative_answer_persists_with_skepticism", ReadStringList(requestStored, "applied").Contains("information_request_record"), requestStored);
                    string requestPrompt = BuildQuestionContinuityPrompt(connection, requestPayload, "npc", "player", new List<string>());
                    Check("next_read_retains_request_and_answer_without_question_mark", requestPrompt.Contains(request)
                        && requestPrompt.Contains(requestAnswer) && requestPrompt.Contains("direct_information_request") && requestPrompt.Contains("unconvincing"), requestPrompt);
                    var requestLink = TestDict("relatedTurnId", "information_request", "relationKind", "answer", "relatedQuote", request);
                    Check("answer_link_supports_information_requests", ConversationContinuityLinkError(connection, requestLink, "npc", "question_timeline", 5d) == "");
                    var requestRow = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='information_request';").Single();
                    Check("retrieval_keeps_information_request_with_next_answer", ExpandPrecisionQuestionContext(connection,
                        new[] { requestRow }.ToList(), BuildKnowledgeAccessContext("npc", "player", "", requestPayload))
                        .Any(row => ReadString(row, "turn_id", "") == "information_answer"));
                    var requestFrames = GroupSpokenQuestionFrames("*He sits back.*\n\n" + conditionalRequest, "request_source", "npc", "same_beat", new List<string> { "npc", "player" });
                    Check("group_conditional_request_retains_original_words", requestFrames.Count == 1 && ReadString(requestFrames[0], "quote", "") == conditionalRequest
                        && ReadString(requestFrames[0], "requestForm", "") == "conditional_information_request");
                    Check("group_request_does_not_invent_an_answer_opportunity", requestFrames.Count == 1 && GroupPlayerAnswerOpportunity(requestFrames[0],
                        TestDict("sceneTurnId", "same_beat"), "I explained the overall plan.").StartsWith("none yet", StringComparison.Ordinal));
                    Check("declarative_reflection_is_not_a_question", ConversationInformationRequestForm("This makes me reconsider my choices.", "This makes me reconsider my choices.").Length == 0);
                    Check("quoted_fragment_cannot_manufacture_new_request", ConversationInformationRequestForm("She said, tell me how the engine works.", "tell me how the engine works.").Length == 0);
                    Check("private_and_reported_requests_do_not_become_group_demands", GroupSpokenQuestionFrames(
                        "*I wonder whether he will tell me how.* She said, tell me how the engine works. I could explain the mechanism.",
                        "not_requests", "npc", "beat", new List<string> { "npc", "player" }).Count == 0);

                    foreach (string nominal in new[] { "Tell me something you know that I don't.", "Tell us your reason.",
                        "Please tell me a story.", "Tell me the name of that plant.", "Tell us one small fact.", "Tell me more." })
                        Check("nominal_request_form_" + PromptHash(nominal).Substring(0, 8),
                            ConversationInformationRequestForm(nominal, nominal) == "direct_information_request", nominal);
                    foreach (string notRequest in new[] { "Do not tell me anything.", "You can tell me something later.",
                        "She told me something interesting.", "Show me your sword.", "Give me the horse.", "I will tell you a story." })
                        Check("nominal_request_negative_" + PromptHash(notRequest).Substring(0, 8),
                            ConversationInformationRequestForm(notRequest, notRequest).Length == 0, notRequest);
                    Check("nominal_reported_fragment_keeps_original_boundary", ConversationInformationRequestForm(
                        "She said, tell me something you know.", "tell me something you know.").Length == 0);
                    Check("nominal_private_request_is_not_group_demand", GroupSpokenQuestionFrames(
                        "*Tell me something you know, she wishes she could say.* She said, tell me your reason.",
                        "private_nominal", "npc", "beat", new List<string> { "npc", "player" }).Count == 0);

                    const string nominalQuestion = "Tell me something you know that I don't. Something small.";
                    const string nominalAnswer = "The name of that flower is moonwort.";
                    const string nominalAck = "Moonwort. I heard your answer; I had never learned that name.";
                    string nominalReply = "*She settles back.*\n\n" + nominalQuestion;
                    Turn("nominal_question", "nominal_question_event", "npc", nominalReply, 110, 110L);
                    var nominalPayload = new Dictionary<string, object>(payload) { ["sceneTurnId"] = "nominal_beat" };
                    var nominalWrite = TestDict("id", "nominal_question_record", "kind", "question", "peerId", "player",
                        "subject", "A small fact", "evidenceQuote", nominalQuestion, "expectedRevision", 0,
                        "questionState", TestDict("question", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", nominalQuestion),
                            "addresseeIds", new[] { "player" }, "answerState", "unanswered", "answers", new List<Dictionary<string, object>>(),
                            "evaluation", "unassessed", "followUpTo", ""));
                    var nominalStored = StoreAcceptedConversationContinuity(connection, nominalPayload, "npc", "player", "nominal_question_event",
                        nominalReply, new[] { nominalWrite }.ToList(), 110L);
                    Check("nominal_current_request_persists", ReadStringList(nominalStored, "applied").Contains("nominal_question_record"), nominalStored);
                    var nominalFrames = GroupSpokenQuestionFrames(nominalReply, "nominal_question", "npc", "nominal_beat", new List<string> { "npc", "player" });
                    Check("nominal_group_request_preserves_owner_and_player_opportunity", nominalFrames.Count == 1
                        && ReadString(nominalFrames[0], "speakerId", "") == "npc"
                        && GroupPlayerAnswerOpportunity(nominalFrames[0], nominalPayload, "We are still talking.").StartsWith("none yet", StringComparison.Ordinal), nominalFrames);
                    Turn("nominal_answer", "nominal_answer_event", "player", nominalAnswer, 111, 111L);
                    Turn("nominal_ack", "nominal_answer_event", "npc", nominalAck, 112, 112L);
                    nominalPayload["playerText"] = nominalAnswer; nominalPayload["sceneTurnId"] = "nominal_answer_beat";
                    CaptureContinuityReadSnapshot(connection, nominalPayload, "npc");
                    nominalWrite["evidenceQuote"] = nominalAck;
                    nominalWrite["questionState"] = TestDict("question", TestDict("turnId", "nominal_question", "speakerId", "npc", "quote", nominalQuestion),
                        "addresseeIds", new[] { "player" }, "answerState", "answered",
                        "answers", new[] { TestDict("turnId", "current_player", "speakerId", "player", "quote", nominalAnswer) },
                        "evaluation", "unassessed", "followUpTo", "");
                    var nominalAnswered = StoreAcceptedConversationContinuity(connection, nominalPayload, "npc", "player", "nominal_answer_event",
                        nominalAck, new[] { nominalWrite }.ToList(), 112L);
                    string nominalPrompt = BuildQuestionContinuityPrompt(connection, nominalPayload, "npc", "player", new List<string>());
                    Check("nominal_answer_retained_in_next_read", ReadStringList(nominalAnswered, "applied").Contains("nominal_question_record")
                        && nominalPrompt.Contains(nominalQuestion) && nominalPrompt.Contains(nominalAnswer)
                        && nominalPrompt.Contains("answer received=answered"), nominalAnswered);
                    var nominalRow = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='nominal_question';").Single();
                    Check("nominal_retrieval_keeps_adjacent_answer", ExpandPrecisionQuestionContext(connection,
                        new[] { nominalRow }.ToList(), BuildKnowledgeAccessContext("npc", "player", "", nominalPayload))
                        .Any(row => ReadString(row, "turn_id", "") == "nominal_answer"));

                    ExecuteSql(connection, "UPDATE conversation_turns SET text='The source was changed.' WHERE turn_id='plan_answer';");
                    string changed = BuildQuestionContinuityPrompt(connection, next, "npc", "player", new List<string>());
                    Check("changed_source_cannot_certify_old_summary", !changed.Contains(answer)
                        && ReadString(ReadDictionary(next, "questionStateEvidence"), "coverage", "") == "incomplete"
                        && QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='question_plan';").Count == 1, changed);
                }
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
