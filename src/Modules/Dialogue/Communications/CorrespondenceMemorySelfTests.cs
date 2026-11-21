using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
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
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    letterId = ReadString(RelationshipsService.InsertLetter(connection, "npc", "Author", "player", "Player", body,
                        "npc_initiated", "gift", "", "a", "b", 10d, 13d, TestDict()), "letterId", "");
                    List<MemoryEvidence> Recall(string who, Dictionary<string, object> ctx = null) => LoadCorrespondenceMemory(connection,
                        who, who == "npc" ? "player" : "npc", "carved wooden horse", ctx ?? context,
                        DialogueService.BuildKnowledgeAccessContext(who, who == "npc" ? "player" : "npc", "", ctx ?? context));
                    var sent = Recall("npc");
                    Check("author_knows_exact_accepted_letter_at_dispatch", sent.Count == 1 && sent[0].Text.Contains(body)
                        && sent[0].Text.Contains("You authored and sent") && sent[0].Required);
                    var precision = SelectPrecisionEvidence(sent);
                    var selected = ReadDictionaryList(PrecisionEvidenceAudit(precision, new MemoryQueryPlan { Observer = "npc" }, "precision"),
                        "records").Single();
                    Check("precision_alias_matches_required_exact_letter", RequiredLetterAlreadySupplied(sent[0], selected, precision.Text));
                    Check("optional_or_changed_precision_letter_preserves_protected_copy",
                        !RequiredLetterAlreadySupplied(sent[0], new Dictionary<string, object>(selected) { ["required"] = false }, precision.Text)
                        && !RequiredLetterAlreadySupplied(sent[0], new Dictionary<string, object>(selected) { ["sourceHash"] = "changed" }, precision.Text)
                        && !RequiredLetterAlreadySupplied(sent[0], new Dictionary<string, object>(selected) { ["revision"] = 2 }, precision.Text)
                        && !RequiredLetterAlreadySupplied(sent[0], selected, precision.Text.Replace(body, "Different words.", StringComparison.Ordinal)));
                    Check("recipient_cannot_read_in_transit", Recall("player").Count == 0);
                    Check("other_group_member_cannot_read_private_letter", Recall("bystander").Count == 0);
                    Check("official_firewall", Recall("npc", new Dictionary<string, object>(context) { ["officialMemoryFirewall"] = true }).Count == 0);
                    Check("disguise_does_not_reveal_matching_private_history", Recall("npc", new Dictionary<string, object>(context) {
                        ["identityView"] = TestDict("knowsIdentity", false) }).Count == 0);
                    Check("future_dispatch_excluded", Recall("npc", new Dictionary<string, object>(context) { ["worldDay"] = 9d }).Count == 0);
                    var row = DialogueSelfTestStore.SelectLetters(connection, TestDict("id", letterId)).Single();
                    Check("same_day_future_sequence_excluded", Recall("npc", new Dictionary<string, object>(context) {
                        ["memoryAsOfAcceptedSequence"] = ReadLong(row, "dispatch_sequence", 0) - 1 }).Count == 0);
                    Check("later_recording_excluded", Recall("npc", new Dictionary<string, object>(context) {
                        ["memoryAsOfTimestamp"] = ReadLong(row, "created_ts", 0) - 1 }).Count == 0);
                    DialogueSelfTestStore.UpdateLetters(connection, TestDict("id", letterId));
                    Check("unread_receipt_is_not_body_knowledge", Recall("player").Count == 0);
                    DialogueSelfTestStore.UpdateLetters2(connection, TestDict("id", letterId));
                    Check("recipient_knows_exact_read_body", Recall("player").Single().Text.Contains(body)
                        && Recall("player").Single().Text.Contains("received and read these words on day 12"));
                    Check("author_does_not_gain_read_receipt", Recall("npc").Single().Text == sent.Single().Text);
                    Check("future_receipt_excluded", Recall("player", new Dictionary<string, object>(context) { ["worldDay"] = 11.5d }).Count == 0);

                    var source = ResolveLetterQuestionSource(connection, letterId, "Will you come through the western gate?", "npc", "npc", "player", context);
                    Check("letter_question_has_original_speaker_and_body_hash", source != null
                        && ReadString(source, "turnId", "") == letterId && ReadString(source, "sourceHash", "") == PromptHash(body)
                        && QuestionOriginalStillVerified(connection, source, "npc", DialogueService.BuildKnowledgeAccessContext("npc", "player", "", context)));
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
                    string requestLetterId = ReadString(RelationshipsService.InsertLetter(connection, "npc", "Author", "player", "Player", informationRequest,
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
                    for (int i = 0; i < 80; i++) RelationshipsService.InsertLetter(connection, "npc", "Author", "player", "Player", "A routine note about tomorrow's weather.",
                        "npc_initiated", "weather", "", "a", "b", 13d + i / 100d, 16d + i / 100d, TestDict());
                    Check("lifetime_topic_ranks_before_recent_limit", Recall("npc").Any(e => e.SourceId == letterId && e.Text.Contains(body)));
                }

                foreach (string mode in new[] { "legacy", "precision" })
                {
                    var settings = TestDict("memoryPrecisionMode", mode, "enableSemanticMemory", false, "minimeEnabled", false);
                    var packet = DialogueService.BuildNpcMemoryPacket(campaign, "npc", "player", "", "carved wooden horse", 2000, context, settings);
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

                var precisionPayload = PromptParityPayload("in_person", false);
                precisionPayload["officialMemoryFirewall"] = false; precisionPayload["timelineId"] = "main";
                precisionPayload["playerHeroStringId"] = "player"; precisionPayload["worldDay"] = 20d;
                precisionPayload["playerText"] = "You wrote to me about the carved wooden horse.";
                var precisePacket = DialogueService.BuildNpcMemoryPacket(campaign, "npc", "player", "", ReadString(precisionPayload, "playerText", ""), 4000,
                    precisionPayload, TestDict("memoryPrecisionMode", "precision", "enableSemanticMemory", false));
                precisionPayload["memoryPrecisionEvidence"] = ReadDictionary(precisePacket, "precisionEvidence");
                string priorReferenceMode = ConversationReferenceValidationMode.Value;
                try
                {
                    foreach (string referenceMode in new[] { "off", "enabled" })
                    {
                        ConversationReferenceValidationMode.Value = referenceMode;
                        var modePayload = CloneDictionary(precisionPayload);
                        var modePacket = DialogueService.BuildNpcMemoryPacket(campaign, "npc", "player", "", ReadString(modePayload, "playerText", ""), 4000,
                            modePayload, TestDict("memoryPrecisionMode", "precision", "enableSemanticMemory", false));
                        modePayload["memoryPrecisionEvidence"] = ReadDictionary(modePacket, "precisionEvidence");
                        var preciseEnvelope = BuildDialoguePromptEnvelope(campaign, "npc", "Author", "Player", "Player",
                            ReadString(modePayload, "playerText", ""), "On the road.", profile, PromptParityCharacteristics(),
                            empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), modePayload,
                            precomputedMemoryPacket: "REIGN KNOWLEDGE PACKET\n" + ReadString(modePacket, "memoryPacket", ""), precomputedGroupConversationPrompt: "");
                        var letterCopies = ContinuityPromptRecords(ConversationEvidenceText(preciseEnvelope.Diagnostics, preciseEnvelope.Messages), false)
                            .Where(record => record.Kind == "letter" && record.Text.Contains("source=" + letterId + ";", StringComparison.Ordinal)).ToList();
                        Check("final_precision_prompt_preserves_one_required_exact_letter_" + referenceMode,
                            letterCopies.Count == 1 && letterCopies[0].Required
                            && (referenceMode == "off" ? letterCopies[0].SourceId.StartsWith("m", StringComparison.Ordinal) : letterCopies[0].SourceId == letterId)
                            && letterCopies[0].Text.Contains(body, StringComparison.Ordinal)
                            && ReadBool(ReadDictionary(preciseEnvelope.Diagnostics, "continuityPreflight"), "requiredSourceCoverageComplete", false),
                            TestDict("letterCopies", letterCopies.Count, "sourceIds", letterCopies.Select(record => record.SourceId).ToList()));
                    }
                }
                finally { ConversationReferenceValidationMode.Value = priorReferenceMode; }

                const string savedSpeechTimeline = "restored_main", recoveredSpeechTimeline = "verified_a1_child";
                const string reportedSpeech = "I did not collect painted lamps. If I had, I would have kept them in the attic.";
                string proposedInterpretation = ReadString(profile, "name", "Author") + " collected painted lamps and kept them in the attic.";
                var reportedFixture = CharactersService.StoreReportedSpeechFixture(campaign, profile, "attributed-speech", "party_chat", 18,
                    reportedSpeech, reportedSpeech, TestDict("dynamicCharacteristicWrites", new[]
                    {
                        TestDict("text", proposedInterpretation, "category", "habit", "topicKey", "lantern_collecting",
                            "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", "I did not collect painted lamps."))
                    }), timeline: savedSpeechTimeline);
                Check("reported_characteristic_written_from_party_source", ReadInt(reportedFixture, "storedCount", 0) == 1, reportedFixture);
                var point = TestDict("savePointId", "letter_before", "timelineId", "restored_main", "campaignTimeDays", 20d,
                    "worldHistorySequence", 8L, "worldHistoryHeadEventId", "history_head");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "letter_before");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    futureId = ReadString(RelationshipsService.InsertLetter(connection, "npc", "Author", "player", "Player", "Future secret after the save.", "npc_initiated", "future", "", "a", "b", 21d, 24d, TestDict()), "letterId", "");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "letter_before");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var restored = new Dictionary<string, object>(context) { ["timelineId"] = "restored_main" };
                    bool Knows(string who, Dictionary<string, object> ctx) => LoadCorrespondenceMemory(connection, who,
                        who == "npc" ? "player" : "npc", "carved wooden horse", ctx, DialogueService.BuildKnowledgeAccessContext(who, "player", "", ctx)).Any(e => e.SourceId == letterId);
                    Check("actual_snapshot_restores_inherited_authorship_and_receipt", Knows("npc", restored) && Knows("player", restored)
                        && DialogueSelfTestStore.SelectLetters(connection, TestDict("id", futureId)).Count == 0);
                    var sibling = new Dictionary<string, object>(restored) { ["timelineId"] = "other_branch" };
                    Check("restore_membership_cannot_leak_to_sibling", !Knows("npc", sibling));
                    BindRecoveredLetterSources(connection, "restored_main", "bad_recovery", 7L, "wrong_head");
                    Check("recovery_requires_snapshot_sequence_and_head", Knows("npc", restored)
                        && !Knows("npc", new Dictionary<string, object>(restored) { ["timelineId"] = "bad_recovery" }));
                    BindRecoveredLetterSources(connection, "restored_main", "restored_recovered", 8L, "history_head");
                    var recovered = new Dictionary<string, object>(restored) { ["timelineId"] = "restored_recovered" };
                    Check("verified_recovery_rebinds_membership", Knows("npc", recovered) && !Knows("npc", restored));
                    DialogueSelfTestStore.UpdateLetters3(connection, TestDict("id", letterId));
                    Check("changed_source_cannot_reuse_restored_membership", !Knows("npc", recovered));
                    DialogueSelfTestStore.UpdateLetters4(connection, TestDict("id", letterId, "body", body));

                    // First qualify the exact turn in the saved timeline, then
                    // carry its existing membership to the verified recovered branch.
                    BindRecoveredMemorySources(connection, savedSpeechTimeline, recoveredSpeechTimeline, 8L, "history_head");
                    var characteristic = ReadDictionaryList(reportedFixture, "stored").SingleOrDefault();
                    string sourceId = TextListFromJson(ReadString(characteristic, "source_turn_ids_json", "[]")).SingleOrDefault() ?? "";
                    var original = DialogueStore.SelectConversationTurns6(connection, TestDict("id", sourceId)).SingleOrDefault();
                    var access = BuildKnowledgeAccessContext("npc", "main_hero", "", TestDict("timelineId", recoveredSpeechTimeline, "worldDay", 20d));
                    Check("reported_characteristic_has_verified_restore_membership", original != null
                        && PrecisionTurnVisible(original, "npc", access, connection));
                }
                var characteristicStack = PromptParityCharacteristics();
                characteristicStack["dynamicCharacteristics"] = CampaignFileService.ReadJsonObject(
                    CampaignFileService.CharacterFile(campaign, "npc", "dynamic_characteristics.json"));
                const string recall = "Tell me about the painted lamps.";
                foreach (string route in new[] { "in_person", "party_chat", "social_event", "correspondence" })
                foreach (string scope in new[] { "original_audience", "unknown_peer", "new_witness", "new_active_speaker", "inactive_attendee", "before_source", "different_timeline", "restored_source", "before_sequence", "official_role" })
                {
                    string peer = scope == "unknown_peer" ? "stranger" : "main_hero";
                    var payload = PromptParityPayload(route, false);
                    payload["officialMemoryFirewall"] = scope == "official_role"; payload["mode"] = route;
                    payload["timelineId"] = scope == "different_timeline" ? "other_branch"
                        : scope == "restored_source" ? recoveredSpeechTimeline : savedSpeechTimeline;
                    if (scope == "before_sequence") payload["memoryAsOfAcceptedSequence"] = 0L;
                    payload["worldDay"] = scope == "before_source" ? 17d : 20d;
                    payload["playerHeroStringId"] = peer;
                    payload["participants"] = scope == "new_witness" ? new[] { "npc", peer, "uninvited" } : new[] { "npc", peer };
                    if (scope == "new_active_speaker") payload["activeHeroIds"] = new[] { "npc", "uninvited" };
                    if (scope == "inactive_attendee") payload["attendees"] = new[] { TestDict("heroStringId", "uninvited") };
                    var motive = TestDict("sanitizedState", empty, "prompt", "", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                    PromptEnvelope envelope = route == "correspondence"
                        ? BuildCorrespondencePromptEnvelope(campaign, "npc", "Author", peer, "Player", ReadDouble(payload, "worldDay", 0), recall,
                            profile, characteristicStack, empty, "", payload)
                        : route == "in_person"
                            ? BuildDialoguePromptEnvelope(campaign, "npc", "Author", "Player", "Player", recall, "On the road.", profile,
                                characteristicStack, empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), payload, "", "")
                            : BuildEventPromptEnvelope(campaign, "fixture_event", "npc", "Author", "Player", "Player", recall, "On the road.", payload,
                                profile, characteristicStack, empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), "", "", motive);
                    string messages = string.Join("\n", envelope.Messages.Select(message => ReadString(message, "content", "")));
                    bool expectSpeech = scope == "original_audience" || scope == "restored_source" || scope == "inactive_attendee"
                        || (route == "correspondence" && scope == "new_witness")
                        || ((route == "in_person" || route == "correspondence") && scope == "new_active_speaker");
                    Check("reported_characteristic_final_prompt_" + route + "_" + scope,
                        messages.Contains(reportedSpeech, StringComparison.Ordinal) == expectSpeech
                        && !messages.Contains(proposedInterpretation, StringComparison.Ordinal)
                        && (!expectSpeech || messages.Contains("Prior speech by", StringComparison.Ordinal)),
                        TestDict("expectedSpeech", expectSpeech, "foundSpeech", messages.Contains(reportedSpeech, StringComparison.Ordinal)));
                }
                AdvanceMemoryRestoreGeneration(campaign);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var recovered = new Dictionary<string, object>(context) { ["timelineId"] = "restored_recovered" };
                    Check("restore_generation_invalidates_old_membership", !LoadCorrespondenceMemory(connection, "npc", "player", "carved wooden horse", recovered,
                        DialogueService.BuildKnowledgeAccessContext("npc", "player", "", recovered)).Any(e => e.SourceId == letterId));
                }
            }
            catch (Exception ex) { Check("suite_exception", false, DialogueService.LimitText(ex.ToString(), 2400)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
