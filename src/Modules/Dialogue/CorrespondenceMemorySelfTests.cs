using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunCorrespondenceMemoryAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "letter_memory_" + id,
                "passed", pass, "summary", id, "data", data));
            string campaign = "letter_memory_test_" + Guid.NewGuid().ToString("N");
            const string body = "I carved a small wooden horse for you. Will you come through the western gate?";
            string letterId = "", futureId = "";
            var context = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 20d,
                "playerHeroStringId", "player", "playerText", "You wrote to me about the carved wooden horse.");
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    letterId = ReadString(InsertLetter(connection, "npc", "Author", "player", "Player", body,
                        "npc_initiated", "gift", "", "a", "b", 10d, 13d, TestDict()), "letterId", "");
                    List<MemoryEvidence> Recall(string who, Dictionary<string, object> ctx = null) => LoadCorrespondenceMemory(connection,
                        who, who == "npc" ? "player" : "npc", "carved wooden horse", ctx ?? context,
                        BuildKnowledgeAccessContext(who, who == "npc" ? "player" : "npc", "", ctx ?? context));
                    var sent = Recall("npc");
                    Check("author_knows_exact_accepted_letter_at_dispatch", sent.Count == 1 && sent[0].Text.Contains(body)
                        && sent[0].Text.Contains("You authored and sent") && sent[0].Required);
                    Check("recipient_cannot_read_in_transit", Recall("player").Count == 0);
                    Check("other_group_member_cannot_read_private_letter", Recall("bystander").Count == 0);
                    Check("official_firewall", Recall("npc", new Dictionary<string, object>(context) { ["officialMemoryFirewall"] = true }).Count == 0);
                    Check("disguise_does_not_reveal_matching_private_history", Recall("npc", new Dictionary<string, object>(context) {
                        ["identityView"] = TestDict("knowsIdentity", false) }).Count == 0);
                    Check("future_dispatch_excluded", Recall("npc", new Dictionary<string, object>(context) { ["worldDay"] = 9d }).Count == 0);
                    var row = QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", letterId)).Single();
                    Check("same_day_future_sequence_excluded", Recall("npc", new Dictionary<string, object>(context) {
                        ["memoryAsOfAcceptedSequence"] = ReadLong(row, "dispatch_sequence", 0) - 1 }).Count == 0);
                    Check("later_recording_excluded", Recall("npc", new Dictionary<string, object>(context) {
                        ["memoryAsOfTimestamp"] = ReadLong(row, "created_ts", 0) - 1 }).Count == 0);
                    ExecuteSql(connection, "UPDATE letters SET status='delivered',delivered_day=11 WHERE letter_id=$id;", TestDict("id", letterId));
                    Check("unread_receipt_is_not_body_knowledge", Recall("player").Count == 0);
                    ExecuteSql(connection, "UPDATE letters SET status='read',read_day=12,delivery_timeline_id='main' WHERE letter_id=$id;", TestDict("id", letterId));
                    Check("recipient_knows_exact_read_body", Recall("player").Single().Text.Contains(body)
                        && Recall("player").Single().Text.Contains("received and read these words on day 12"));
                    Check("author_does_not_gain_read_receipt", Recall("npc").Single().Text == sent.Single().Text);
                    Check("future_receipt_excluded", Recall("player", new Dictionary<string, object>(context) { ["worldDay"] = 11.5d }).Count == 0);

                    var source = ResolveLetterQuestionSource(connection, letterId, "Will you come through the western gate?", "npc", "npc", "player", context);
                    Check("letter_question_has_original_speaker_and_body_hash", source != null
                        && ReadString(source, "turnId", "") == letterId && ReadString(source, "sourceHash", "") == PromptHash(body)
                        && QuestionOriginalStillVerified(connection, source, "npc", BuildKnowledgeAccessContext("npc", "player", "", context)));
                    Check("wrong_letter_speaker_rejected", ResolveLetterQuestionSource(connection, letterId, "Will you come through the western gate?", "player", "npc", "player", context) == null);
                    var write = TestDict("id", "letter_question", "kind", "question", "subject", "Western gate visit", "status", "active",
                        "peerId", "player", "expectedRevision", 0, "evidenceQuote", "Will you come through the western gate?",
                        "questionState", TestDict("question", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", "Will you come through the western gate?"),
                            "addresseeIds", new[] { "player" }, "answerState", "unanswered", "answers", new List<Dictionary<string, object>>(),
                            "evaluation", "unassessed", "followUpTo", ""));
                    var stored = StoreAcceptedConversationContinuity(connection, context, "npc", "player", letterId, body, new[] { write }.ToList(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    string questionPrompt = BuildQuestionContinuityPrompt(connection, context, "npc", "player", new List<string>());
                    Check("authored_question_survives_without_fabricated_chat_turn", ReadStringList(stored, "applied").Contains("letter_question")
                        && questionPrompt.Contains("Will you come through the western gate?"), stored);
                    const string informationRequest = "Please explain how you would fund the purchase.";
                    string requestLetterId = ReadString(InsertLetter(connection, "npc", "Author", "player", "Player", informationRequest,
                        "npc_initiated", "purchase", "", "a", "b", 13d, 16d, TestDict()), "letterId", "");
                    var requestSource = ResolveLetterQuestionSource(connection, requestLetterId, informationRequest, "npc", "npc", "player", context);
                    Check("letter_information_request_retains_body_and_form", requestSource != null
                        && ReadString(requestSource, "requestForm", "") == "direct_information_request"
                        && ReadString(requestSource, "sourceHash", "") == PromptHash(informationRequest));
                    // The earlier accepted write now contains canonical source IDs
                    // and evidence spans. A new proposal must start with fresh input.
                    var requestWrite = TestDict("id", "letter_information_request", "kind", "question", "subject", "Purchase funding",
                        "status", "active", "peerId", "player", "expectedRevision", 0, "evidenceQuote", informationRequest,
                        "questionState", TestDict("question", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", informationRequest),
                            "addresseeIds", new[] { "player" }, "answerState", "unanswered", "answers", new List<Dictionary<string, object>>(),
                            "evaluation", "unassessed", "followUpTo", ""));
                    var requestStored = StoreAcceptedConversationContinuity(connection, context, "npc", "player", requestLetterId, informationRequest,
                        new[] { requestWrite }.ToList(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    Check("letter_request_without_question_mark_survives_next_read", ReadStringList(requestStored, "applied").Contains("letter_information_request")
                        && BuildQuestionContinuityPrompt(connection, context, "npc", "player", new List<string>()).Contains(informationRequest), requestStored);
                    Check("information_request_does_not_disclose_unread_letter", ResolveLetterQuestionSource(connection, requestLetterId,
                        informationRequest, "npc", "player", "npc", context) == null);
                    for (int i = 0; i < 80; i++) InsertLetter(connection, "npc", "Author", "player", "Player", "A routine note about tomorrow's weather.",
                        "npc_initiated", "weather", "", "a", "b", 13d + i / 100d, 16d + i / 100d, TestDict());
                    Check("lifetime_topic_ranks_before_recent_limit", Recall("npc").Any(e => e.SourceId == letterId && e.Text.Contains(body)));
                }

                foreach (string mode in new[] { "legacy", "precision" })
                {
                    var settings = TestDict("memoryPrecisionMode", mode, "enableSemanticMemory", false, "minimeEnabled", false);
                    var packet = BuildNpcMemoryPacket(campaign, "npc", "player", "", "carved wooden horse", 2000, context, settings);
                    Check("shared_memory_packet_" + mode, ReadString(packet, "memoryPacket", "").Contains(body));
                }
                var profile = PromptParityProfile(); profile["heroStringId"] = "npc";
                var empty = TestDict(); var noLines = new List<Dictionary<string, object>>();
                foreach (string memoryMode in new[] { "legacy", "precision" })
                foreach (string mode in new[] { "in_person", "party_chat", "social_event", "correspondence" })
                {
                    var payload = PromptParityPayload(mode, false);
                    payload["officialMemoryFirewall"] = false; payload["timelineId"] = "main";
                    payload["playerHeroStringId"] = "player"; payload["worldDay"] = 20d; payload["mode"] = mode;
                    if (memoryMode == "precision") payload["memoryPrecisionEvidence"] = TestDict("mode", "precision");
                    const string input = "You wrote to me about the carved wooden horse.";
                    var motive = TestDict("sanitizedState", empty, "prompt", "A written gift.", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                    PromptEnvelope envelope = mode == "correspondence"
                        ? BuildCorrespondencePromptEnvelope(campaign, "npc", "Author", "player", "Player", 20d, input, profile, PromptParityCharacteristics(), empty, "", payload)
                        : mode == "in_person"
                            ? BuildDialoguePromptEnvelope(campaign, "npc", "Author", "Player", "Player", input, "On the road.", profile, PromptParityCharacteristics(), empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), payload, "", "")
                            : BuildEventPromptEnvelope(campaign, "fixture_event", "npc", "Author", "Player", "Player", input, "On the road.", payload, profile, PromptParityCharacteristics(), empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), "", "", motive);
                    Check("actual_final_prompt_" + memoryMode + "_" + mode, Json.Serialize(envelope.Messages).Contains(body)
                        && ReadBool(ReadDictionary(envelope.Diagnostics, "continuityPreflight"), "requiredSourceCoverageComplete", false));
                }

                var point = TestDict("savePointId", "letter_before", "timelineId", "restored_main", "campaignTimeDays", 20d,
                    "worldHistorySequence", 8L, "worldHistoryHeadEventId", "history_head");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "letter_before");
                using (var connection = OpenCampaignConnection(campaign))
                    futureId = ReadString(InsertLetter(connection, "npc", "Author", "player", "Player", "Future secret after the save.", "npc_initiated", "future", "", "a", "b", 21d, 24d, TestDict()), "letterId", "");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "letter_before");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var restored = new Dictionary<string, object>(context) { ["timelineId"] = "restored_main" };
                    bool Knows(string who, Dictionary<string, object> ctx) => LoadCorrespondenceMemory(connection, who,
                        who == "npc" ? "player" : "npc", "carved wooden horse", ctx, BuildKnowledgeAccessContext(who, "player", "", ctx)).Any(e => e.SourceId == letterId);
                    Check("actual_snapshot_restores_inherited_authorship_and_receipt", Knows("npc", restored) && Knows("player", restored)
                        && QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", futureId)).Count == 0);
                    var sibling = new Dictionary<string, object>(restored) { ["timelineId"] = "other_branch" };
                    Check("restore_membership_cannot_leak_to_sibling", !Knows("npc", sibling));
                    BindRecoveredLetterSources(connection, "restored_main", "bad_recovery", 7L, "wrong_head");
                    Check("recovery_requires_snapshot_sequence_and_head", Knows("npc", restored)
                        && !Knows("npc", new Dictionary<string, object>(restored) { ["timelineId"] = "bad_recovery" }));
                    BindRecoveredLetterSources(connection, "restored_main", "restored_recovered", 8L, "history_head");
                    var recovered = new Dictionary<string, object>(restored) { ["timelineId"] = "restored_recovered" };
                    Check("verified_recovery_rebinds_membership", Knows("npc", recovered) && !Knows("npc", restored));
                    ExecuteSql(connection, "UPDATE letters SET body='Altered later account.' WHERE letter_id=$id;", TestDict("id", letterId));
                    Check("changed_source_cannot_reuse_restored_membership", !Knows("npc", recovered));
                    ExecuteSql(connection, "UPDATE letters SET body=$body WHERE letter_id=$id;", TestDict("id", letterId, "body", body));
                }
                AdvanceMemoryRestoreGeneration(campaign);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var recovered = new Dictionary<string, object>(context) { ["timelineId"] = "restored_recovered" };
                    Check("restore_generation_invalidates_old_membership", !LoadCorrespondenceMemory(connection, "npc", "player", "carved wooden horse", recovered,
                        BuildKnowledgeAccessContext("npc", "player", "", recovered)).Any(e => e.SourceId == letterId));
                }
            }
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 2400)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
