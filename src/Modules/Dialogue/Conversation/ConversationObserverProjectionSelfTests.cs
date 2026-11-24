using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunConversationObserverProjectionAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "observer_projection_" + id,
                "passed", pass, "summary", id, "data", data));
            const string secret = "recognition of their private conversation about bringing flowers to someone who cares";
            const string reply = "*She turns the stem toward the light. She offers it back, her eyes meeting his with " + secret + ".* It survived the journey. Keep it.";
            const string disclosed = "I remember our private conversation about bringing flowers. I am telling you both now.";
            string publicText = ObserverConversationText(reply, "npc_a", "npc_b");
            Check("mixed_narration_preserves_action_and_speech", publicText.Contains("turns the stem") && publicText.Contains("offers it back")
                && publicText.Contains("It survived the journey. Keep it.") && !publicText.Contains(secret), publicText);
            Check("author_keeps_complete_literary_reply", ObserverConversationText(reply, "npc_a", "npc_a") == reply);
            Check("actual_spoken_disclosure_remains_audible", ObserverConversationText(disclosed, "npc_a", "npc_b") == disclosed);
            const string explicitInference = "*She regrets ever sheltering Orel. Her hands betray guilt about sheltering Orel.*";
            const string unfamiliarGesture = "*She twirls the spindle.*";
            const string attributedReport = "I heard that Orel was sheltered. She said she regretted it.";
            string directScene = unfamiliarGesture + " " + explicitInference + " " + attributedReport;
            string directView = ObserverConversationText(directScene, "npc_a", "npc_b");
            Check("standalone_inferences_do_not_become_npc_observations",
                ObserverConversationText(explicitInference + " Hello.", "npc_a", "npc_b") == "Hello."
                && directView.Contains(unfamiliarGesture) && directView.Contains(attributedReport)
                && !directView.Contains("regrets ever") && !directView.Contains("betray guilt"), directView);
            Check("standalone_inference_guard_preserves_author_player_and_mixed_source",
                ObserverConversationText(directScene, "npc_a", "npc_a") == directScene
                && ObserverConversationText(directScene, "player", "npc_b", playerNarration: true) == directScene
                && ObserverConversationText("*Her hands betray guilt while she closes the chest.* Hello.", "npc_a", "npc_b")
                    == "*Her hands betray guilt while she closes the chest.* Hello."
                && ObserverConversationText("*She regrets that she stayed, she says aloud.* Hello.", "npc_a", "npc_b")
                    == "*She regrets that she stayed, she says aloud.* Hello.");
            var inferenceSceneProjection = ProjectConversationNarration(directScene, "npc_a", "npc_b");
            var inferenceSpans = ReadDictionaryList(inferenceSceneProjection, "spans");
            Check("standalone_inference_keeps_exact_retained_spans",
                ReadDictionaryList(inferenceSceneProjection, "coverageSpans").Sum(span => ReadInt(span, "length", 0)) == directScene.Length
                && inferenceSpans.All(span => directScene.Substring(ReadInt(span, "start", 0),
                    ReadInt(span, "length", 0)) == ReadString(span, "text", ""))
                && inferenceSpans.Any(span => ReadString(span, "kind", "") == "observable_narration"
                    && ReadString(span, "text", "") == "She twirls the spindle."));
            var inferenceMemory = ScopeConversationMemoryWrites(new List<Dictionary<string, object>>
                { TestDict("text", directScene) }, "npc_a", "player", "", directScene,
                "standalone_inference_fixture", new List<string> { "npc_a", "npc_b" });
            string inferenceObserved = ReadString(inferenceMemory.Single(row =>
                ReadString(row, "observerScope", "") == "observable_source_spans"), "text", "");
            Check("standalone_inference_is_not_added_to_new_observer_memory",
                inferenceObserved.Contains(unfamiliarGesture) && inferenceObserved.Contains(attributedReport)
                && !inferenceObserved.Contains("regrets ever") && !inferenceObserved.Contains("betray guilt")
                && inferenceMemory.Any(row => ReadString(row, "observerScope", "") == "author_interpretation"
                    && ReadString(row, "text", "") == directScene), inferenceObserved);
            Check("imagined_action_does_not_become_observed", ObserverConversationText("*She holds his hand in her imagination.* Hello.", "npc_a", "npc_b") == "Hello.");
            Check("private_fear_is_not_given_to_another_actor", ObserverConversationText("*He remembers his private fear of the throne.* I will listen.", "npc_a", "npc_b") == "I will listen.");
            Check("unclassified_narration_is_not_certified_as_observable", !ObserverConversationText("*A secret meaning lives here.* Good day.", "npc_a", "npc_b").Contains("secret meaning"));
            Check("truncated_narration_cannot_become_spoken", ObserverConversationText("Good day. *She remembers the secret", "npc_a", "npc_b") == "Good day.");
            const string shiftedMarkers = "Good day. *She keeps a private thought across paragraphs.\r\n\r\nThe hidden route must never be spoken.\r\n\r\n*She lifts a finger.*\r\n\r\nThe greater answer is paid.\r\n\r\n*Her voice drops.*\r\n\r\nThe next answer stands on its own feet.\r\n\r\n*She smiles.*\r\n\r\nA later sentence.";
            var malformedProjection = ProjectConversationNarration(shiftedMarkers, "npc_a", "npc_b");
            Check("missing_early_marker_cannot_reclassify_later_private_spans", ReadString(malformedProjection, "visibleText", "") == "Good day."
                && !ReadString(malformedProjection, "visibleText", "").Contains("hidden route"));
            Check("ambiguous_projection_preserves_author_and_source_hash", ObserverConversationText(shiftedMarkers, "npc_a", "npc_a") == shiftedMarkers
                && ReadString(malformedProjection, "sourceHash", "") == PromptHash(shiftedMarkers)
                && ReadString(malformedProjection, "formatIntegrity", "") == "unbalanced_narration_markers"
                && ReadBool(malformedProjection, "sourcePreserved", false));
            var malformedCoverage = ReadDictionaryList(malformedProjection, "coverageSpans");
            Check("ambiguous_projection_accounts_for_quarantine_without_repair", malformedCoverage.Sum(s => ReadInt(s, "length", 0)) == shiftedMarkers.Length
                && malformedCoverage.Any(s => ReadString(s, "reason", "") == "ambiguous_npc_narration_markers")
                && ReadDictionaryList(malformedProjection, "spans").All(s => shiftedMarkers.Substring(ReadInt(s, "start", 0), ReadInt(s, "length", 0)) == ReadString(s, "text", "")));
            const string balancedParagraphs = "*She remembers a secret.\n\nShe thinks of the hidden route.* I heard you. *She nods.*";
            Check("balanced_multiline_private_span_stays_private", !ObserverConversationText(balancedParagraphs, "npc_a", "npc_b").Contains("hidden route")
                && ObserverConversationText(balancedParagraphs, "npc_a", "npc_b").Contains("I heard you."));
            Check("malformed_player_story_keeps_existing_explicit_audience_semantics", ObserverConversationText(shiftedMarkers, "player", "npc_b", true) == shiftedMarkers);
            Check("odd_literal_marker_is_conservatively_withheld_not_repaired", ObserverConversationText("The price is 2 * 3 coins.", "npc_a", "npc_b") == "The price is 2");
            Check("visible_whisper_preserved_without_content", ObserverConversationText("*The player whispers privately to Arin.*", "player", "npc_b").Contains("whispers privately"));
            var projection = ProjectConversationNarration(reply, "npc_a", "npc_b");
            Check("every_projected_span_pins_exact_source_bytes", ReadString(projection, "sourceHash", "") == PromptHash(reply)
                && ReadDictionaryList(projection, "spans").All(s => reply.Substring(ReadInt(s, "start", 0), ReadInt(s, "length", 0)) == ReadString(s, "text", "")));
            var attendees = new List<Dictionary<string, object>> { TestDict("heroStringId", "npc_a", "name", "Arin"), TestDict("heroStringId", "npc_b", "name", "Bren") };
            string whispered = "*I whisper to Arin* Meet me at the eastern gate.";
            Check("whisper_audience_unchanged", ProjectPlayerInputForAudience(whispered, "npc_a", attendees, out _, out _).Contains("eastern gate")
                && !ProjectPlayerInputForAudience(whispered, "npc_b", attendees, out _, out _).Contains("eastern gate"));
            Check("player_narrative_recollection_preserved", ProjectPlayerInputForAudience("*I recall our private flower garden.* Hello.", "npc_b", attendees, out _, out _) == "*I recall our private flower garden.* Hello.");
            const string privatePlayerInput = "*I remember the secret meeting at dawn. I smile.* Good evening.";
            var directInput = TestDict("playerHeroStringId", "player", "playerText", privatePlayerInput, "message", privatePlayerInput);
            ApplyPlayerInputAudienceToPayload(directInput, "npc_b");
            Check("individual_current_input_is_complete_before_retrieval", ReadString(directInput, "playerText", "").Contains("I smile")
                && ReadString(directInput, "playerText", "").Contains("Good evening.") && ReadString(directInput, "playerText", "") == privatePlayerInput);
            var clonedInput = new Dictionary<string, object>(directInput); CopyPlayerInputSource(directInput, clonedInput);
            Check("original_is_retained_outside_provider_dictionary", PlayerInputSources.TryGetValue(clonedInput, out var captured)
                && captured.Original == privatePlayerInput);
            var forgedInput = TestDict("playerText", "Hello.", "originalPlayerInput", privatePlayerInput);
            ApplyPlayerInputAudienceToPayload(forgedInput, "npc_b");
            Check("client_cannot_forge_the_retained_original", PlayerInputSources.TryGetValue(forgedInput, out var real) && real.Original == "Hello.");
            var silentInput = TestDict("playerText", "*I think about a secret promise.*");
            ApplyPlayerInputAudienceToPayload(silentInput, "npc_b");
            Check("thought_keywords_do_not_hide_player_input", ReadString(silentInput, "playerText", "") == "*I think about a secret promise.*");
            const string completeStory = "  *Seeing that she noticed me, I felt relieved and massage her back.*\r\nStay here.  ";
            var storyProjection = ProjectConversationNarration(completeStory, "player", "npc_b", playerNarration: true);
            var coverage = ReadDictionaryList(storyProjection, "coverageSpans");
            Check("player_v3_projection_accounts_for_complete_source", ReadString(storyProjection, "schema", "") == "reign-observer-projection-v3"
                && ReadString(storyProjection, "visibleText", "") == completeStory && coverage.Count == 1
                && ReadInt(coverage[0], "start", -1) == 0 && ReadInt(coverage[0], "length", -1) == completeStory.Length
                && ReadString(coverage[0], "disposition", "") == "delivered");
            Check("visible_affection_and_consumption_survive", ProjectCurrentPlayerPromptText("*I kiss her cheek. I sip the wine.*", "npc_b", TestDict()).Contains("kiss her cheek")
                && ProjectCurrentPlayerPromptText("*I kiss her cheek. I sip the wine.*", "npc_b", TestDict()).Contains("sip the wine"));
            Check("audible_answers_and_chuckles_survive", new[] { "I chuckle", "She chuckles", "I answer her plainly and calmly", "He replied softly", "She grinned" }
                .All(action => ObserverConversationText("*" + action + "* Yes.", "player", "npc") == "*" + action + "* Yes."));
            Check("remembered_chuckle_stays_private", !ObserverConversationText("*I recall how she chuckled yesterday* Yes.", "player", "npc").Contains("yesterday"));
            Check("first_and_third_person_action_inflections_survive", new[] { "I kiss her cheek.", "He kisses her cheek.", "I pass her the cup.", "He passes her the cup.", "I caress his hand.", "She caresses his hand." }
                .All(action => ProjectCurrentPlayerPromptText("*" + action + "*", "npc_b", TestDict()).Contains(action)));
            Check("ongoing_actions_remain_visible", new[] { "I keep walking with her", "I watch her carefully", "She is watching the road",
                "He is holding the cup", "I am sipping the wine", "She keeps studying the stone", "I am offering her the map" }
                .All(action => ProjectCurrentPlayerPromptText("*" + action + "*", "npc_b", TestDict()).Contains(action)));
            const string mixedInference = "*I watch her carefully as I see that she misunderstood perhaps* I meant the other road.";
            string withoutInference = ObserverConversationText(mixedInference, "player", "npc_b");
            Check("visible_prefix_survives_without_unspoken_inference", withoutInference == "*I watch her carefully* I meant the other road.", withoutInference);
            Check("new_action_forms_do_not_expose_private_thoughts", new[] {
                "I imagine walking beside her", "I ponder watching him", "She wonders whether he misunderstood her while holding the cup",
                "I suspect she remembered our hidden meeting while smiling" }
                .All(action => ObserverConversationText("*" + action + "* Hello.", "player", "npc_b") == "Hello."));
            const string nextDayReturn = "*I return to her the next day* Hello, ready to join up?";
            Check("narrated_return_keeps_relative_time_and_question", ObserverConversationText(nextDayReturn, "player", "npc_b") == nextDayReturn
                && ProjectCurrentPlayerPromptText(nextDayReturn, "npc_b", TestDict()).Contains("I return to her the next day"));
            Check("direct_movement_inflections_remain_visible", new[] {
                "She returns to the courtyard", "He arrived at the gate", "I am returning to her", "They are departing from the inn",
                "I leave the chamber", "She left the room", "We have arrived at the stable" }
                .All(action => ObserverConversationText("*" + action + "* Hello.", "player", "npc_b") == "*" + action + "* Hello."));
            var privateMovements = new[] {
                "I will return to her tomorrow", "She would arrive if invited", "I could leave tomorrow", "I plan to depart at dawn",
                "I imagine returning to her", "He remembers arriving at the gate", "I return if she permits it",
                "I returned to her in my dream", "I returned to the city three years ago", "I did not return to the gate",
                "I pretend to return to her", "I return in a rehearsal of tomorrow's meeting" }
                .Select(action => TestDict("input", action, "visible", ObserverConversationText("*" + action + "* Hello.", "player", "npc_b"))).ToList();
            Check("movement_intention_hypothesis_and_recollection_stay_private", privateMovements.All(row => ReadString(row, "visible", "") == "Hello."),
                privateMovements.Where(row => ReadString(row, "visible", "") != "Hello.").ToList());
            Check("movement_nouns_do_not_certify_an_action", new[] { "Her return is uncertain", "The leaves rustle", "Doubt returns", "I left a secret unspoken" }
                .All(action => ObserverConversationText("*" + action + "* Hello.", "player", "npc_b") == "Hello."));
            const string mixedMovement = "*I return to her as I remember our private meeting* Hello.";
            Check("movement_keeps_action_without_private_reason", ObserverConversationText(mixedMovement, "player", "npc_b") == "*I return to her* Hello."
                && ObserverConversationText(mixedMovement, "player", "player") == mixedMovement);
            var movementProjection = ProjectConversationNarration(nextDayReturn, "player", "npc_b");
            Check("movement_is_exact_source_narration_not_native_result", ReadString(movementProjection, "sourceHash", "") == PromptHash(nextDayReturn)
                && ReadDictionaryList(movementProjection, "spans").Any(s => ReadString(s, "kind", "") == "observable_narration"
                    && ReadString(s, "text", "") == "I return to her the next day")
                && ReadDictionaryList(movementProjection, "spans").All(s => nextDayReturn.Substring(ReadInt(s, "start", 0), ReadInt(s, "length", 0)) == ReadString(s, "text", "")));
            var inferenceProjection = ProjectConversationNarration(mixedInference, "player", "npc_b");
            Check("retained_action_keeps_exact_source_offsets", ReadDictionaryList(inferenceProjection, "spans")
                .All(span => mixedInference.Substring(ReadInt(span, "start", 0), ReadInt(span, "length", 0)) == ReadString(span, "text", ""))
                && ObserverConversationText(mixedInference, "player", "player") == mixedInference);
            var lines = new List<Dictionary<string, object>> { TestDict("role", "npc", "speakerId", "npc_a", "speaker", "Arin", "text", reply) };
            var projected = ProjectConversationLinesForObserver(lines, "npc_b", TestDict());
            Check("client_transcript_projection_does_not_mutate_original", !ReadString(projected[0], "text", "").Contains(secret) && ReadString(lines[0], "text", "") == reply);
            var contaminated = new List<Dictionary<string, object>> { TestDict("role", "player", "speakerId", "player", "text", "I touched your arm."),
                TestDict("role", "npc", "speakerId", "npc_a", "text", "[The NPC's decision could not be verified. No agreement or world change is confirmed. Any independent player handover has its own execution result.]"),
                TestDict("role", "npc", "speakerId", "npc_a", "text", "I heard you.") };
            var clean = ProjectConversationLinesForObserver(contaminated, "npc_a", TestDict());
            Check("legacy_technical_notice_excluded_from_next_prompt", clean.Count == 2
                && clean.All(line => !IsTechnicalConversationNotice(ReadString(line, "text", "")))
                && ReadString(clean[0], "text", "") == "I touched your arm."
                && ReadString(clean[1], "text", "") == "I heard you.");

            string campaign = "observer_projection_test_" + Guid.NewGuid().ToString("N");
            var payload = TestDict("campaignId", campaign, "timelineId", "main", "worldDay", 10d,
                "playerHeroStringId", "player", "playerText", "Here is the flower.", "mode", "party_chat",
                "conversationSessionId", "observer_scene", "sceneTurnId", "beat_one", "participantHeroIds", new[] { "player", "npc_a", "npc_b" });
            var participants = new List<string> { "player", "npc_a", "npc_b" };
            try
            {
                ConversationStartApi(TestDict("campaignId", campaign, "timelineId", "main", "sessionId", "observer_scene",
                    "npcId", "npc_a", "playerId", "player", "worldDay", 10d, "participants", participants));
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    InsertConversationTurn(connection, "observer_source", "observer_scene", "observer_event", 1, "observer_exchange",
                        "npc", "npc_a", "Arin", reply, "party_chat", 10d, 20L, "town", participants, payload);
                var writes = new List<Dictionary<string, object>> { TestDict("memoryId", "mixed_fixture", "text", "The public flower exchange included " + secret + ".",
                    "ownerId", "npc_b", "known_by", participants, "visibility", "public") };
                DialogueService.StoreTurnMemoryLayers(campaign, "observer_event", "party_chat_turn", "npc_a", "player", "town", "Here is the flower.", reply,
                    writes, new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(), "conversation:observer_scene", 20L, participants, true, payload);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var owner = DialogueService.BuildKnowledgeAccessContext("npc_a", "player", "town", payload);
                    var bystander = DialogueService.BuildKnowledgeAccessContext("npc_b", "player", "town", payload);
                    var ownerRows = DialogueService.QueryKnownRows(connection, "memories", owner, "status='active'", 50);
                    var publicRows = DialogueService.QueryKnownRows(connection, "memories", bystander, "status='active'", 50);
                    var ownerAuditRows = DialogueService.QueryKnownRows(connection, "memories", owner, "status='audit_only'", 50);
                    var bystanderAuditRows = DialogueService.QueryKnownRows(connection, "memories", bystander, "status='audit_only'", 50);
                    Check("mixed_write_owned_only_by_actual_speaker", ownerAuditRows.Any(r =>
                            ReadString(r, "memory_id", "") == "mixed_fixture" && ReadString(r, "owner_id", "") == "npc_a"
                            && ReadString(TryParseJsonObject(ReadString(r, "payload_json", "{}")), "sourceValidation", "") == "incomplete"
                            && !EmbeddingRowActive("memory", r))
                        && ownerRows.All(r => ReadString(r, "memory_id", "") != "mixed_fixture")
                        && publicRows.All(r => ReadString(r, "memory_id", "") != "mixed_fixture")
                        && bystanderAuditRows.All(r => ReadString(r, "memory_id", "") != "mixed_fixture"));
                    Check("public_observation_survives_as_separate_supported_record", publicRows.Any(r => ReadString(r, "summary", "").Contains("turns the stem"))
                        && publicRows.All(r => !ReadString(r, "summary", "").Contains(secret)), publicRows.Select(r => ReadString(r, "summary", "")).ToList());
                    var earlier = DialogueService.BuildKnowledgeAccessContext("npc_b", "player", "town", new Dictionary<string, object>(payload) { ["worldDay"] = 9d });
                    Check("accepted_world_day_prevents_future_memory_leak", DialogueService.QueryKnownRows(connection, "memories", earlier, "status='active'", 50).Count == 0);
                    var original = DialogueSelfTestStore.SelectConversationTurns13(connection).Single();
                    Check("historical_source_stays_complete_but_reader_is_scoped", ReadString(original, "text", "") == reply
                        && RenderExactHistoryTurn(original, owner).Contains(secret) && !RenderExactHistoryTurn(original, bystander).Contains(secret));
                    // Emulate an old mixed-audience record. Read-time projection
                    // repairs future use without rewriting the historical record.
                    DialogueSelfTestStore.UpdateMemories(connection, TestDict("ids", Json.Serialize(participants)));
                    var legacy = DialogueSelfTestStore.SelectMemories4(connection).Single();
                    var safe = ProjectRetrievedConversationMemories(connection, new List<Dictionary<string, object>> { legacy }, bystander);
                    Check("legacy_mixed_record_uses_observable_original", safe.Count == 1 && !ReadString(safe[0], "summary", "").Contains(secret)
                        && ReadString(safe[0], "summary", "").Contains("It survived the journey."));
                    Check("read_projection_does_not_rewrite_history", ReadString(DialogueSelfTestStore.SelectMemories5(connection).Single(), "summary", "").Contains(secret));
                    var missing = new Dictionary<string, object>(legacy) { ["event_id"] = "missing_original" };
                    Check("unverifiable_private_derivative_withheld", ProjectRetrievedConversationMemories(connection, new List<Dictionary<string, object>> { missing }, bystander).Count == 0);
                    const string unsupported = "Arin regretted sheltering Orel.";
                    var shared = TestDict("summary_id", "d2_legacy_scene", "summary_type", "scene", "owner_id", "npc_a",
                        "scope", "scene:observer_scene", "summary", unsupported, "source_events_json", Json.Serialize(new[] { "observer_event" }));
                    var bounded = ProjectRetrievedConversationMemories(connection, new() { shared }, bystander).SingleOrDefault();
                    string boundedText = ReadString(bounded, "summary", "");
                    Check("shared_scene_legacy_summary_uses_eligible_canonical_speech", boundedText.Contains("It survived the journey.")
                        && boundedText.Contains("[source=observer_source role=npc]") && !boundedText.Contains(unsupported)
                        && !boundedText.Contains(secret) && !boundedText.Contains("turns the stem")
                        && ReadString(shared, "summary", "") == unsupported);
                    var replayReader = DialogueService.BuildKnowledgeAccessContext("npc_b", "player", "town", payload);
                    replayReader.PlayerIdentityUnknown = false;
                    var replaySource = new PublishedPartyReplayRow("observer_source", ReadString(original, "exchange_id", ""),
                        "npc_a", ReadString(original, "speaker_name", ""), "npc", ConversationTurnTextForRecall(original),
                        ReadLong(original, "accepted_sequence", 0), ReadDouble(original, "world_day", 0d));
                    var replayView = new PublishedPartyReplayView(campaign, replayReader.NpcId, replayReader.PlayerId,
                        replayReader.TimelineId, "observer_scene", "D2 generated replay", replayReader.AsOfAcceptedSequence,
                        replayReader.WorldDay, participants.ToArray(), new() { replaySource }, new[] { "observer_source" }, "test", "test");
                    replayView.BindRead(campaign, replayReader, "D2 generated replay");
                    var replayBounded = ProjectRetrievedConversationMemories(connection, new() { shared }, replayReader).SingleOrDefault();
                    bool mutatedSourceRefused = false;
                    try
                    {
                        var tampered = new Dictionary<string, object>(original) { ["text"] = "It survived the journey." };
                        RenderExactHistoryTurn(tampered, replayReader, npcSpeechOnly: true);
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("isolated DB identity", StringComparison.Ordinal))
                    { mutatedSourceRefused = true; }
                    string replayText = ReadString(replayBounded, "summary", "");
                    Check("shared_scene_replay_verifies_original_before_bounding_spoken_view",
                        mutatedSourceRefused && RenderExactHistoryTurn(original, replayReader).Contains(secret)
                        && replayText.Contains("It survived the journey.") && !replayText.Contains(secret)
                        && !replayText.Contains("turns the stem") && ReadString(original, "text", "") == reply);
                    var projectedPayload = TryParseJsonObject(ReadString(bounded, "payload_json", "{}"));
                    var projectedPins = ReadDictionaryList(projectedPayload, "sourceReferences");
                    var lateEvidence = EvidenceFromRow(bounded, "summary_id", boundedText, "recent_summary", "npc_b", true, 118);
                    var missingPins = CloneDictionary(bounded);
                    projectedPayload.Remove("sourceReferences");
                    missingPins["payload_json"] = Json.Serialize(projectedPayload);
                    Check("shared_scene_late_summary_hash_commits_bounded_pins_without_full_turn_credit",
                        projectedPins.Count == 1 && ReadString(projectedPins[0], "sourceId", "") == "observer_source"
                        && ReadString(projectedPins[0], "sourceHash", "") == MemorySourceHash(new[] { original })
                        && ReadString(projectedPins[0], "viewHash", "") == PromptHash(boundedText)
                        && DialogueService.TextListFromJson(ReadString(bounded, "source_events_json", "[]")).SequenceEqual(new[] { "observer_event" })
                        && lateEvidence.SourceTurns.Count == 0
                        && lateEvidence.SourceHash != EvidenceFromRow(missingPins, "summary_id", boundedText, "recent_summary", "npc_b", true, 118).SourceHash);
                    Check("shared_scene_lexical_shortcut_never_publishes_unbound_paraphrase",
                        ProjectRetrievedConversationMemories(connection, new() { new Dictionary<string, object>(shared)
                            { ["source_events_json"] = Json.Serialize(new[] { "missing_event" }) } }, bystander).Count == 0);
                    var stagedScene = PrepareStagedSceneMemory(TestDict("npc_id", "npc_a"), new() { original }, "main");
                    var exactReference = ReadDictionaryList(stagedScene, "sourceReferences").Single();
                    var modern = new Dictionary<string, object>(shared) { ["payload_json"] = Json.Serialize(TestDict(
                        "sessionId", "observer_scene", "sourceReferences", new[] { exactReference },
                        "originalSourceRecords", new[] { unsupported })) };
                    var exactSummary = ProjectRetrievedConversationMemories(connection, new() { modern }, bystander).SingleOrDefault();
                    Check("shared_scene_modern_proof_rebinds_exact_original_and_omits_derivative_payload",
                        ReadString(exactSummary, "summary", "").Contains("It survived the journey.")
                        && !ReadString(exactSummary, "payload_json", "").Contains(unsupported));
                    exactReference["sourceHash"] = "old_source_text";
                    modern["payload_json"] = Json.Serialize(TestDict("sessionId", "observer_scene", "sourceReferences", new[] { exactReference }));
                    Check("shared_scene_stale_modern_receipt_has_no_legacy_downgrade",
                        ProjectRetrievedConversationMemories(connection, new() { modern }, bystander).Count == 0);
                    var elsewhere = new Dictionary<string, object>(shared) { ["scope"] = "scene:different_session" };
                    var outsider = DialogueService.BuildKnowledgeAccessContext("npc_outside", "player", "town", payload);
                    Check("shared_scene_rebind_still_checks_session_asof_and_audience",
                        ProjectRetrievedConversationMemories(connection, new() { elsewhere }, bystander).Count == 0
                        && ProjectRetrievedConversationMemories(connection, new() { shared }, earlier).Count == 0
                        && ProjectRetrievedConversationMemories(connection, new() { shared }, outsider).Count == 0);
                    var nativeSummary = TestDict("summary_id", "d2_native", "summary_type", "world_event", "scope", "public_calendar",
                        "visibility", "public", "summary", "Town bell sounded at dawn.");
                    Check("shared_scene_leaves_own_summary_and_native_public_evidence",
                        ReferenceEquals(ProjectRetrievedConversationMemories(connection, new() { shared }, owner).SingleOrDefault(), shared)
                        && ReferenceEquals(ProjectRetrievedConversationMemories(connection, new() { nativeSummary }, bystander).SingleOrDefault(), nativeSummary));
                    const string playerStory = "*I pass the blue spindle across the table.* This is yours.";
                    InsertConversationTurn(connection, "d2_player_source", "observer_scene", "d2_player_event", 2, "d2_player_exchange",
                        "player", "player", "Player", playerStory, "party_chat", 10d, 20L, "town", participants, payload);
                    var playerSummary = new Dictionary<string, object>(shared)
                    { ["source_events_json"] = Json.Serialize(new[] { "d2_player_event" }) };
                    Check("shared_scene_preserves_explicit_eligible_player_story",
                        ReadString(ProjectRetrievedConversationMemories(connection, new() { playerSummary }, bystander).SingleOrDefault(),
                            "summary", "").Contains(playerStory));

                    VerifyRankedSharedSceneProjection(connection, campaign, payload, bystander, original, Check);

                    var link = TestDict("relatedTurnId", "observer_source", "relationKind", "correction", "relatedQuote", secret);
                    Check("private_narration_cannot_ground_other_observer_link", ConversationContinuityLinkError(connection, link, "npc_b", "main", 10d) == "related_source_outside_observer_scope");
                }
                clonedInput["conversationSessionId"] = "observer_scene"; clonedInput["worldDay"] = 10d;
                clonedInput["timelineId"] = "main"; clonedInput["participants"] = participants;
                StoreConversationExchange(campaign, clonedInput, "npc_b", "player", "Player", "Bren",
                    ReadString(clonedInput, "playerText", ""), "Good evening to you.", "private_input_acceptance", 21L);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var source = DialogueSelfTestStore.SelectConversationTurns14(connection).Single();
                    var knowledge = DialogueService.BuildKnowledgeAccessContext("npc_b", "player", "town", payload);
                    Check("accepted_private_original_is_retained_but_not_npc_evidence", ReadString(source, "text", "") == privatePlayerInput
                        && !PrecisionTurnVisible(source, "npc_b", knowledge)
                        && DialogueSelfTestStore.SelectConversationTurnFts(connection, TestDict("id", ReadString(source, "turn_id", ""))).Count == 0);
                    Check("accepted_player_turn_preserves_complete_narration", ReadString(DialogueSelfTestStore.SelectConversationTurns15(connection).Single(), "text", "").Contains("Good evening.")
                        && ReadString(DialogueSelfTestStore.SelectConversationTurns15(connection).Single(), "text", "") == privatePlayerInput);
                    var session = DialogueSelfTestStore.SelectConversationSessions2(connection).Single();
                    var allTurns = DialogueSelfTestStore.SelectConversationTurns16(connection);
                    var scene = CreateConversationSceneSummary(campaign, session, allTurns, 22L,
                        TestDict("useMemoryLlmForConsolidation", false, "enableSemanticMemory", false));
                    Check("shared_scene_summary_cannot_publish_author_thoughts", !ReadString(scene, "summary", "").Contains(secret)
                        && ReadString(scene, "summary", "").Contains("secret meeting")
                        && ReadString(scene, "summary", "").Contains("It survived the journey.")
                        && ReadString(scene, "summary", "").Contains("Good evening."));
                    Check("source_only_records_never_become_scene_sources", !ReadStringList(scene, "sourceTurnIds").Contains(ReadString(source, "turn_id", "")));
                }
                UpdateSharedGroupConversationState(campaign, payload, "npc_a", "Arin", reply, "observer_scene", 20L, "observer_event");
                string group = BuildSharedGroupConversationPrompt(campaign, payload, "npc_b", lines, "Here is the flower.");
                Check("shared_group_context_excludes_private_cause", group.Contains("It survived the journey.") && !group.Contains(secret));
                var profile = PromptParityProfile(); profile["heroStringId"] = "npc_b";
                var empty = TestDict(); var noLines = new List<Dictionary<string, object>>();
                var eventPayload = PromptParityPayload("party_chat", false);
                eventPayload["playerHeroStringId"] = "player"; eventPayload["worldDay"] = 10d; eventPayload["timelineId"] = "main";
                var motive = TestDict("sanitizedState", empty, "prompt", "Respond to the flower.", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                var envelope = BuildEventPromptEnvelope(campaign, "fixture_event", "npc_b", "Bren", "Player", "Player", "Here is the flower.", "A public road.", eventPayload,
                    profile, PromptParityCharacteristics(), empty, empty, empty, lines, noLines, noLines, noLines, PromptParityIdentity(), "", "", motive);
                string final = Json.Serialize(envelope.Messages);
                Check("actual_group_provider_messages_preserve_secrecy_and_action", !final.Contains(secret) && final.Contains("It survived the journey."));
                var directPayload = PromptParityPayload("in_person", false);
                directPayload["playerHeroStringId"] = "player"; directPayload["worldDay"] = 10d; directPayload["timelineId"] = "main";
                var directEnvelope = BuildDialoguePromptEnvelope(campaign, "npc_b", "Bren", "Player", "Player", privatePlayerInput,
                    "A public road.", profile, PromptParityCharacteristics(), empty, empty, empty, noLines, noLines, noLines, noLines,
                    PromptParityIdentity(), directPayload, "", "");
                Check("actual_individual_provider_message_keeps_complete_player_input", Json.Serialize(directEnvelope.Messages).Contains("secret meeting")
                    && Json.Serialize(directEnvelope.Messages).Contains("Good evening."));
            }
            catch (Exception ex) { Check("suite_exception", false, DialogueService.LimitText(ex.ToString(), 2200)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }

        private static void VerifyRankedSharedSceneProjection(ReignDbConnection connection, string campaign,
            Dictionary<string, object> context, DialogueService.KnowledgeAccessContext reader,
            Dictionary<string, object> original, Action<string, bool, object> check)
        {
            const string query = "dahlia", legacyId = "d2_rank_event_only", missingId = "d2_rank_missing",
                staleId = "d2_rank_stale", ownedId = "d2_rank_owned";
            const string legacySentinel = "unboundparaphrase", missingSentinel = "missingprose",
                staleSentinel = "stalebindingprose", ownedSentinel = "authoredrecollection";
            void Seed(string id, string owner, string eventId, string marker, Dictionary<string, object> binding)
            {
                string stored = query + " " + marker;
                var known = new[] { owner, reader.NpcId };
                DialogueStore.UpsertSummaries(connection, TestDict("id", id, "scope", "scene:observer_scene", "owner", owner,
                    "summary", stored, "events", Json.Serialize(new[] { eventId }), "start", 20L, "end", 20L, "count", 1,
                    "location", "town", "participants", Json.Serialize(known), "known", Json.Serialize(known),
                    "tags", "[]", "updated", 21L, "importance", 0.5d, "confidence", 0.8d, "status", "active",
                    "source", "conversation_scene", "embedding", "not_indexed", "lane", "interpersonal_history",
                    "payload", Json.Serialize(binding)));
                DialogueService.InsertSummaryFts(connection, id, stored, new List<string>(), known.ToList());
            }

            Seed(legacyId, "npc_a", "observer_event", legacySentinel, TestDict("timelineId", "main"));
            Seed(missingId, "npc_a", "d2_absent_event", missingSentinel, TestDict("timelineId", "main"));
            var reference = ReadDictionaryList(PrepareStagedSceneMemory(TestDict("npc_id", "npc_a"), new() { original }, "main"),
                "sourceReferences").Single();
            reference["sourceHash"] = "stale_original";
            Seed(staleId, "npc_a", "observer_event", staleSentinel,
                TestDict("timelineId", "main", "sessionId", "observer_scene", "sourceReferences", new[] { reference }));
            LinkMemorySource(connection, "summary", staleId, "turn", ReadString(original, "turn_id", ""), 0);
            Seed(ownedId, reader.NpcId, "d2_owner_unbound_event", ownedSentinel, TestDict("timelineId", "main"));

            var plan = PlanMemoryQuery(reader.NpcId, reader.PlayerId, query, context);
            var lexical = SearchSummaryFts(connection, plan.Terms, 24, reader);
            var eligible = QueryKnownRows(connection, "summaries", reader, "status='active'", 16);
            var legacyRow = eligible.Single(row => ReadString(row, "summary_id", "") == legacyId);
            var staleRow = eligible.Single(row => ReadString(row, "summary_id", "") == staleId);
            check("ranked_shared_scene_fixtures_enter_fts_and_have_actual_stale_link",
                new[] { legacyId, missingId, staleId, ownedId }.All(id => lexical.Contains(id))
                && ReadConversationDerivativeOriginals(connection, "summaries", staleRow).Any(turn =>
                    ReadString(turn, "turn_id", "") == ReadString(original, "turn_id", "")), lexical);

            var settings = TestDict("enableSemanticMemory", false, "memorySelectionMode", "off", "enableMinime", false,
                "memoryPrecisionOrdinaryTokens", 8000);
            var packet = BuildPrecisionMemoryPacket(campaign, reader.NpcId, reader.PlayerId, "town", query, context, settings, shadow: false);
            var audit = ReadDictionary(packet, "precisionEvidence");
            var records = ReadDictionaryList(audit, "records");
            string prompt = ReadString(packet, "memoryPacket", "");
            var ranked = records.SingleOrDefault(record => ReadString(record, "kind", "") == "shared_scene_excerpt"
                && ReadString(record, "sourceId", "") == legacyId);
            var expected = ProjectSharedSceneSummary(connection, legacyRow, reader);
            check("ranked_shared_scene_legacy_events_reconstruct_without_full_turn_credit",
                ranked != null && ReadString(audit, "mode", "") == "precision"
                && ReadString(ranked, "rendered", "").Contains("It survived the journey.")
                && !ReadString(ranked, "rendered", "").Contains("turns the stem")
                && ReadString(ranked, "sourceHash", "") == MemorySourceHash(new[] { expected })
                && ReadDictionaryList(ranked, "sourceTurns").Count == 0 && !prompt.Contains(legacySentinel), ranked);
            check("ranked_shared_scene_missing_original_never_falls_back_to_paraphrase",
                records.All(record => ReadString(record, "sourceId", "") != missingId) && !prompt.Contains(missingSentinel), null);
            check("ranked_shared_scene_stale_modern_pin_rejects_durable_link_fallback",
                records.All(record => ReadString(record, "sourceId", "") != staleId) && !prompt.Contains(staleSentinel), null);
            check("ranked_shared_scene_keeps_reader_owned_legacy_recollection",
                records.Any(record => ReadString(record, "sourceId", "") == ownedId
                    && ReadString(record, "kind", "") == "unverified"
                    && ReadString(record, "rendered", "").Contains(ownedSentinel)), null);
        }
    }
}
