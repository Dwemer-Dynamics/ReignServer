using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
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
            Check("imagined_action_does_not_become_observed", ObserverConversationText("*She holds his hand in her imagination.* Hello.", "npc_a", "npc_b") == "Hello.");
            Check("private_fear_is_not_given_to_another_actor", ObserverConversationText("*He remembers his private fear of the throne.* I will listen.", "npc_a", "npc_b") == "I will listen.");
            Check("unclassified_narration_is_not_certified_as_observable", !ObserverConversationText("*A secret meaning lives here.* Good day.", "npc_a", "npc_b").Contains("secret meaning"));
            Check("truncated_narration_cannot_become_spoken", ObserverConversationText("Good day. *She remembers the secret", "npc_a", "npc_b") == "Good day.");
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
                using (var connection = OpenCampaignConnection(campaign))
                    InsertConversationTurn(connection, "observer_source", "observer_scene", "observer_event", 1, "observer_exchange",
                        "npc", "npc_a", "Arin", reply, "party_chat", 10d, 20L, "town", participants, payload);
                var writes = new List<Dictionary<string, object>> { TestDict("memoryId", "mixed_fixture", "text", "The public flower exchange included " + secret + ".",
                    "ownerId", "npc_b", "known_by", participants, "visibility", "public") };
                StoreTurnMemoryLayers(campaign, "observer_event", "party_chat_turn", "npc_a", "player", "town", "Here is the flower.", reply,
                    writes, new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(), new List<Dictionary<string, object>>(),
                    new List<Dictionary<string, object>>(), "conversation:observer_scene", 20L, participants, true, payload);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var owner = BuildKnowledgeAccessContext("npc_a", "player", "town", payload);
                    var bystander = BuildKnowledgeAccessContext("npc_b", "player", "town", payload);
                    var ownerRows = QueryKnownRows(connection, "memories", owner, "status='active'", 50);
                    var publicRows = QueryKnownRows(connection, "memories", bystander, "status='active'", 50);
                    Check("mixed_write_owned_only_by_actual_speaker", ownerRows.Any(r => ReadString(r, "memory_id", "") == "mixed_fixture" && ReadString(r, "owner_id", "") == "npc_a")
                        && publicRows.All(r => ReadString(r, "memory_id", "") != "mixed_fixture"));
                    Check("public_observation_survives_as_separate_supported_record", publicRows.Any(r => ReadString(r, "summary", "").Contains("turns the stem"))
                        && publicRows.All(r => !ReadString(r, "summary", "").Contains(secret)), publicRows.Select(r => ReadString(r, "summary", "")).ToList());
                    var earlier = BuildKnowledgeAccessContext("npc_b", "player", "town", new Dictionary<string, object>(payload) { ["worldDay"] = 9d });
                    Check("accepted_world_day_prevents_future_memory_leak", QueryKnownRows(connection, "memories", earlier, "status='active'", 50).Count == 0);
                    var original = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='observer_source';").Single();
                    Check("historical_source_stays_complete_but_reader_is_scoped", ReadString(original, "text", "") == reply
                        && RenderExactHistoryTurn(original, owner).Contains(secret) && !RenderExactHistoryTurn(original, bystander).Contains(secret));
                    // Emulate an old mixed-audience record. Read-time projection
                    // repairs future use without rewriting the historical record.
                    ExecuteSql(connection, "UPDATE memories SET participants_json=$ids,known_by_json=$ids,hidden_from_json='[]' WHERE memory_id='mixed_fixture';", TestDict("ids", Json.Serialize(participants)));
                    var legacy = QuerySql(connection, "SELECT * FROM memories WHERE memory_id='mixed_fixture';").Single();
                    var safe = ProjectRetrievedConversationMemories(connection, new List<Dictionary<string, object>> { legacy }, bystander);
                    Check("legacy_mixed_record_uses_observable_original", safe.Count == 1 && !ReadString(safe[0], "summary", "").Contains(secret)
                        && ReadString(safe[0], "summary", "").Contains("It survived the journey."));
                    Check("read_projection_does_not_rewrite_history", ReadString(QuerySql(connection, "SELECT summary FROM memories WHERE memory_id='mixed_fixture';").Single(), "summary", "").Contains(secret));
                    var missing = new Dictionary<string, object>(legacy) { ["event_id"] = "missing_original" };
                    Check("unverifiable_private_derivative_withheld", ProjectRetrievedConversationMemories(connection, new List<Dictionary<string, object>> { missing }, bystander).Count == 0);
                    var link = TestDict("relatedTurnId", "observer_source", "relationKind", "correction", "relatedQuote", secret);
                    Check("private_narration_cannot_ground_other_observer_link", ConversationContinuityLinkError(connection, link, "npc_b", "main", 10d) == "related_source_outside_observer_scope");
                }
                clonedInput["conversationSessionId"] = "observer_scene"; clonedInput["worldDay"] = 10d;
                clonedInput["timelineId"] = "main"; clonedInput["participants"] = participants;
                StoreConversationExchange(campaign, clonedInput, "npc_b", "player", "Player", "Bren",
                    ReadString(clonedInput, "playerText", ""), "Good evening to you.", "private_input_acceptance", 21L);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var source = QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id='private_input_acceptance' AND status='source_only';").Single();
                    var knowledge = BuildKnowledgeAccessContext("npc_b", "player", "town", payload);
                    Check("accepted_private_original_is_retained_but_not_npc_evidence", ReadString(source, "text", "") == privatePlayerInput
                        && !PrecisionTurnVisible(source, "npc_b", knowledge)
                        && QuerySql(connection, "SELECT turn_id FROM conversation_turn_fts WHERE turn_id=$id;", TestDict("id", ReadString(source, "turn_id", ""))).Count == 0);
                    Check("accepted_player_turn_preserves_complete_narration", ReadString(QuerySql(connection,
                        "SELECT text FROM conversation_turns WHERE turn_id='private_input_acceptance_player';").Single(), "text", "").Contains("Good evening.")
                        && ReadString(QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id='private_input_acceptance_player';").Single(), "text", "") == privatePlayerInput);
                    var session = QuerySql(connection, "SELECT * FROM conversation_sessions WHERE session_id='observer_scene';").Single();
                    var allTurns = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='observer_scene' ORDER BY turn_order;");
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
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 2200)); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
