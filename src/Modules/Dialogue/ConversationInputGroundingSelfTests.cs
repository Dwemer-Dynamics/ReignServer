using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationInputGroundingAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object evidence = null) => results.Add(TestDict("caseId", "input_grounding_" + id,
                "passed", pass, "summary", id, "data", evidence));
            var people = new List<Dictionary<string, object>> { TestDict("heroStringId", "player", "name", "Player", "role", "player"),
                TestDict("heroStringId", "npc", "name", "Mira", "role", "npc"), TestDict("heroStringId", "bystander", "name", "Arin", "role", "npc") };
            Dictionary<string, object> Payload(string text, string turn = "one") => TestDict("campaignId", "", "timelineId", "grounding_branch",
                "conversationSessionId", "grounding_session", "playerHeroStringId", "player", "heroStringId", "npc", "playerName", "Player",
                "playerText", text, "worldDay", 20d, "sceneTurnId", turn, "contributionId", turn,
                "sceneParticipants", people.Select(CloneDictionary).ToList(), "attendees", people.Select(CloneDictionary).ToList(), "participants", new[] { "player", "npc", "bystander" });
            Dictionary<string, object> InterpretAll(string text) => TestDict("narration", InputNarrationBlocks(text).Select(block =>
                TestDict("index", ReadInt(block, "index", -1), "spans", new[] { TestDict("quote", ReadString(block, "text", ""),
                    "kind", "observable", "actorId", "player", "targetIds", new[] { "npc" }, "requiresResponse", true) })).ToList(), "sceneChanges", new object[0]);
            string grass = "*I reach over and pluck a piece of grass, then use it as a sword to mock duel her blade of grass while I talk.* Good day.";
            string massage = "*I see her favoring the old injury and reach over massaging the spot in her back while I talk.* How are you?";
            foreach (string text in new[] { grass, massage })
            {
                var payload = Payload(text);
                var source = CapturePlayerInput(payload);
                source.Interpretation = ValidateNarrationInterpretation(text, InterpretAll(text), payload);
                ApplyPlayerInputAudienceToPayload(payload, "npc");
                string action = ReadString(InputNarrationBlocks(text)[0], "text", "");
                Check(text == grass ? "grass_duel_reaches_current_writer" : "back_massage_reaches_current_writer",
                    ReadString(source.Interpretation, "status", "") == "validated" && ReadString(payload, "playerText", "").Contains(action)
                    && BuildCurrentConversationGrounding(payload, "npc").Contains(action) && source.Original == text);
                var ignored = TestDict("reply", "I am well. What is on your mind?", "actionResponses", new object[0]);
                Check(text == grass ? "grass_no_mechanical_reaction_gate" : "massage_no_mechanical_reaction_gate", FindConversationGroundingViolations(ignored, payload, "npc").Count == 0);
                string quote = text == grass ? "*She parries the grass blade and laughs.*" : "*She tenses beneath his hand.* Please stop touching my back.";
                var reacted = TestDict("reply", quote, "actionResponses", new[] { TestDict("actionId", ReadString(CurrentObservableActions(payload, "npc")[0], "actionId", ""),
                    "outcome", text == grass ? "reacted" : "refused", "replyQuote", quote) });
                Check(text == grass ? "grass_visible_reaction_verified" : "massage_refusal_preserves_agency", FindConversationGroundingViolations(reacted, payload, "npc").Count == 0);
                reacted["actionResponses"] = new[] { TestDict("actionId", ReadString(CurrentObservableActions(payload, "npc")[0], "actionId", ""), "outcome", "reacted", "replyQuote", "I hear you.") };
                reacted["reply"] = "I hear you.";
                Check(text == grass ? "grass_reaction_metadata_not_required" : "massage_reaction_metadata_not_required", FindConversationGroundingViolations(reacted, payload, "npc").Count == 0);
            }
            bool unfamiliar = true;
            for (int i = 0; i < 128; i++)
            {
                string text = "  *I gestureword" + i + " beside her with a 🌿 — quietly.*\r\nYes.  ";
                var p = Payload(text); var source = CapturePlayerInput(p);
                source.Interpretation = ValidateNarrationInterpretation(text, InterpretAll(text), p);
                ApplyPlayerInputAudienceToPayload(p, "npc");
                unfamiliar &= OriginalPlayerInput(p) == text && ReadString(p, "playerText", "").Contains("gestureword" + i)
                    && ReadDictionaryList(source.Interpretation, "spans").All(span => text.Substring(ReadInt(span, "start", -1), ReadInt(span, "length", 0)) == ReadString(span, "text", ""))
                    && ReadDictionaryList(source.Interpretation, "coverageSpans").Sum(span => ReadInt(span, "length", 0)) == text.Length;
            }
            Check("generated_open_vocabulary_unicode_source_conservation", unfamiliar);
            string mixed = "*I brush her sleeve; I privately recall the secret code.* Hello.";
            var mixedPayload = Payload(mixed);
            var mixedCandidate = TestDict("narration", new[] { TestDict("index", 0, "spans", new[] {
                TestDict("quote", "I brush her sleeve;", "kind", "observable", "actorId", "player", "targetIds", new[] { "npc" }),
                TestDict("quote", "I privately recall the secret code.", "kind", "private", "actorId", "player", "targetIds", new string[0]) }) });
            CapturePlayerInput(mixedPayload).Interpretation = ValidateNarrationInterpretation(mixed, mixedCandidate, mixedPayload);
            ApplyPlayerInputAudienceToPayload(mixedPayload, "npc");
            Check("mixed_clause_privacy_and_original", ReadString(mixedPayload, "playerText", "").Contains("brush her sleeve")
                && ReadString(mixedPayload, "playerText", "") == mixed && OriginalPlayerInput(mixedPayload) == mixed);
            var invalid = InterpretAll(grass); ReadDictionaryList(ReadDictionaryList(invalid, "narration")[0], "spans")[0]["quote"] = "I reach";
            Check("unaccounted_source_is_unresolved", ReadString(ValidateNarrationInterpretation(grass, invalid, Payload(grass)), "status", "") == "unresolved");
            foreach (string text in new[] { "  *Seeing that she looks tired, I massage her back.* Hello.  ",
                "*I felt the grass bend as I pretended to duel her.*", "*I notice her smile and take her hand*",
                "*I think the map is upside down, and turn it over*", @"literal \*star\* *I nod.**I wave.* *I stop", mixed })
            {
                var story = Payload(text); CapturePlayerInput(story).Interpretation = TestDict("status", "unresolved");
                ApplyPlayerInputAudienceToPayload(story, "npc");
                var clone = CloneDictionary(story); CopyPlayerInputSource(story, clone);
                Check("complete_story_survives_projection_" + results.Count,
                    ReadString(story, "playerText", "") == text && ProjectCurrentPlayerPromptText(text, "npc", clone) == text
                    && FindConversationGroundingViolations(TestDict("reply", "She answers naturally."), story, "npc").Count == 0);
            }
            const string whisperStory = "*I climb through the window to her bedchamber, whispering quietly as I enter* Dont yell.";
            var solo = people.Where(p => CharacterIdFrom(p) != "bystander").ToList();
            Check("mixed_whisper_preserves_actions_for_recipient", ProjectPlayerInputForAudience(whisperStory, "npc", solo, out _, out _) == whisperStory);
            const string namedWhisper = "*I take her hand and whisper to Mira* Wait here.";
            string unheard = ProjectPlayerInputForAudience(namedWhisper, "bystander", people, out _, out _);
            Check("explicit_whisper_preserves_action_but_scopes_words", unheard.Contains("I take her hand") && !unheard.Contains("Wait here")
                && ProjectPlayerInputForAudience(namedWhisper, "npc", people, out _, out _) == namedWhisper);
            const string inlineWhisper = "*I take her hand and whisper to Mira that we leave at dawn. I brush her sleeve.* *I face the group* All set.";
            string inlinePublic = ProjectPlayerInputForAudience(inlineWhisper, "bystander", people, out string sharedWhisper, out _);
            Check("unquoted_inline_whisper_keeps_gestures_without_private_words", !inlinePublic.Contains("we leave at dawn")
                && !sharedWhisper.Contains("we leave at dawn") && inlinePublic.Contains("I take her hand")
                && inlinePublic.Contains("I brush her sleeve") && inlinePublic.Contains(".* *I face the group*")
                && ProjectPlayerInputForAudience(inlineWhisper, "npc", people, out _, out _) == inlineWhisper);
            Check("narrated_relocation_before_spoken_answer_is_checked", new[] {
                "*She enters the tavern.* I accept your proposal.", "*She enters the tavern* I accept your proposal." }
                .All(reply => CurrentSceneBodyContradictions("her bedchamber", reply).Count == 1));
            Check("remembered_venue_before_spoken_answer_stays_historical", new[] {
                "*She remembers sitting in the tavern.* Stay here.", "*She recalled sitting in the tavern* Stay here." }
                .All(reply => CurrentSceneBodyContradictions("her bedchamber", reply).Count == 0));
            Check("historical_hall_callback_is_not_scene_drift", CurrentSceneBodyContradictions("her bedchamber",
                "*The decision was made hours ago anyway — in a garden, in a hall, the moment she told him to find her.*").Count == 0);
            Check("historical_clause_cannot_excuse_current_relocation", CurrentSceneBodyContradictions("her bedchamber",
                "*She remembers the garden; now she walks across the tavern.*").Count == 1);
            var headingPayload = Payload("Hello.");
            headingPayload["conversationDateLabel"] = "1084 Winter 3 Night";
            headingPayload["currentSettlementName"] = "Rovalt";
            headingPayload["conversationSceneState"] = TestDict("conversationVenue", "her bedchamber");
            Check("scene_heading_retains_date_settlement_and_owner", RenderGroundedSceneHeading(headingPayload, "npc", TestDict(), "*She smiles.*")
                == "1084 Winter 3 Night — Rovalt, Mira's bedchamber\n\n*She smiles.*");
            Check("escaped_adjacent_and_unclosed_delimiters", InputNarrationBlocks(@"literal \*star\* *I nod.**I wave.* *I stop").Count == 3
                && !ReadBool(InputNarrationBlocks("*I stop").Single(), "closed", true));
            var pParty = Payload("Hello."); pParty["partyContext"] = TestDict("schema", "reign-party-context-v1", "partyId", "main_party", "worldDay", 20d,
                "complete", true, "members", new[] { TestDict("heroStringId", "npc", "membership", "temporary_guest") }, "prisoners", new[] { "bystander" });
            pParty["localPresence"] = TestDict("schema", "reign-local-presence-v1", "worldDay", 20d, "people", new[] {
                TestDict("heroStringId", "bystander", "name", "Arin", "settlementPresence", "present", "roomPresence", "unknown", "conversationWitness", false) });
            Check("town_presence_does_not_grant_membership", FindConversationGroundingViolations(TestDict("reply", "Arin is our scout at the front of our column."), pParty, "npc").Count > 0);
            Check("name_then_description_cannot_invent_shared_travel", FindConversationGroundingViolations(TestDict("reply",
                "Arin. The Battanian woman who's been riding at the front of the column since Marunath, reading every ridge and calling the road conditions before anyone asks."), pParty, "npc")
                .Any(issue => ReadString(issue, "type", "") == "critical_continuity_party_membership"));
            Check("name_description_keeps_negation_history_and_subject_boundaries",
                FindConversationGroundingViolations(TestDict("reply", "Arin. She is not with our party."), pParty, "npc").Count == 0
                && FindConversationGroundingViolations(TestDict("reply", "Arin. She was with our party last year."), pParty, "npc").Count == 0
                && FindConversationGroundingViolations(TestDict("reply", "Arin.\n\nMira is with our party."), pParty, "npc").Count == 0
                && FindConversationGroundingViolations(TestDict("reply", "Arin. Mira is with our party."), pParty, "npc").Count == 0);
            Check("correction_not_misread_as_membership", FindConversationGroundingViolations(TestDict("reply", "I was wrong. Arin is not part of our party."), pParty, "npc").Count == 0);
            foreach (string membership in new[] { "member", "temporary_guest" })
            {
                ReadDictionary(pParty, "partyContext")["members"] = new[] { TestDict("heroStringId", "bystander", "name", "Arin", "membership", membership) };
                Check(membership + "_is_current_member_without_invented_role", FindConversationGroundingViolations(TestDict("reply", "Arin is with our party."), pParty, "npc").Count == 0
                    && FindConversationGroundingViolations(TestDict("reply", "Arin is our scout."), pParty, "npc").Count > 0);
            }
            ReadDictionary(pParty, "partyContext")["members"] = new[] { TestDict("heroStringId", "bystander", "name", "Arin", "roleEvidence", new[] { "scout" }) };
            Check("native_role_evidence_supports_exact_current_role", FindConversationGroundingViolations(TestDict("reply", "Arin is our scout."), pParty, "npc").Count == 0);
            foreach (string association in new[] { "same_clan", "same_army", "prisoner", "pending_invitation", "town_visitor" })
            {
                ReadDictionary(pParty, "partyContext")["members"] = new object[0];
                ReadDictionaryList(ReadDictionary(pParty, "localPresence"), "people")[0]["association"] = association;
                Check(association + "_does_not_establish_party_membership", FindConversationGroundingViolations(TestDict("reply", "Arin is with our party."), pParty, "npc").Count > 0);
            }
            ReadDictionary(pParty, "partyContext")["complete"] = false;
            var unknownMembership = FindConversationGroundingViolations(TestDict("reply", "Arin is with our party."), pParty, "npc");
            Check("partial_roster_omission_stays_unknown", unknownMembership.Any(issue => ReadString(issue, "detail", "").Contains("no supplied current membership evidence"))
                && !unknownMembership.Any(issue => ReadString(issue, "detail", "").Contains("outside the complete")));
            ReadDictionary(pParty, "partyContext")["worldDay"] = 19d;
            Check("stale_roster_is_unknown", CurrentConversationPartyContext(pParty) == null);
            Check("body_scene_drift_caught_without_heading", CurrentSceneBodyContradictions("camp stables", "*She walks across the tavern toward him.*").Count == 1
                && CurrentSceneBodyContradictions("camp stables", "I remember yesterday in the tavern.").Count == 0);
            Check("captured_tavern_body_remains_invalid_after_heading_repair",
                CurrentSceneBodyContradictions("camp stables", "Camp Stables\n\n*She shakes her head, still amused, and settles back against the tavern wall.*\n\n"
                    + "*She's quiet for a breath. The tavern noise fills the space around them — cups, voices, the creak of settling timber.*\n\n"
                    + "And you're here, asking how I am, in a tavern where no one's watching.").Count == 3);
            Check("nearby_or_historical_venue_mentions_do_not_relocate_scene",
                CurrentSceneBodyContradictions("camp stables", "*She points toward the tavern beyond the stables.*\n\n"
                    + "*The tavern noise reaches them from across the yard.*\n\nI remember being in a tavern yesterday.").Count == 0);
            Check("tent_rejects_undated_hall_heading_and_body", CurrentSceneBodyContradictions("Miras tent", "Town — the lord's hall, after the tournament\n\nWelcome.").Count == 1
                && CurrentSceneBodyContradictions("Miras tent", "*She crosses the lord's hall toward him.*").Count == 1
                && CurrentSceneBodyContradictions("Miras tent", "*She walks across the lord's hall toward him.*").Count == 1
                && CurrentSceneBodyContradictions("Miras tent", "I remember yesterday in the lord's hall.").Count == 0);
            foreach (string words in new[] { "I walked into Miras tent yesterday.", "I would walk into Miras tent.", "I do not walk into Miras tent.",
                "I walk into Miras tent in my imagination.", "I walk into Miras empty tent.", "I walk into Arins tent." })
                Check("arrival_does_not_infer_absent_or_noncurrent_scene_" + results.Count,
                    ReadDictionaryList(ValidateNarrationInterpretation("*" + words + "*", InterpretAll("*" + words + "*"), Payload("*" + words + "*")), "sceneChanges").Count == 0);
            var unresolvedPayload = Payload("*I touch her sleeve.*");
            CapturePlayerInput(unresolvedPayload).Interpretation = TestDict("status", "unresolved");
            unresolvedPayload["conversationSceneState"] = TestDict("conversationVenue", "her bedchamber");
            var unresolvedReply = TestDict("reply", "*She enters the tavern.* I accept your proposal.", "structuredActionDecision", TestDict("kind", "accept", "valid", true));
            string unresolvedText = ReadString(unresolvedReply, "reply", "");
            Check("failed_grounding_revokes_structured_npc_agreement", !FinalizeConversationGrounding(unresolvedPayload, "npc", unresolvedReply, ref unresolvedText)
                && !ReadBool(ReadDictionary(unresolvedReply, "structuredActionDecision"), "valid", true)
                && !ReadBool(ReadDictionary(unresolvedPayload, "validatedStructuredActionDecision"), "valid", true));
            var unresolvedSocial = TestDict("ok", true, "heroStringId", "npc", "reply", "Come join our party.",
                "conversationGrounding", TestDict("status", "unresolved"),
                "relationshipAssessments", new[] { TestDict("targetHeroStringId", "player", "valence", "positive") });
            Check("unresolved_group_reply_cannot_join_or_change_relations", !SocialConversationResultAllowsEffects(unresolvedSocial)
                && ResolveSocialEventMidTurnJoins("", Payload("Hello."), people, new() { "npc" }, new(), new() { unresolvedSocial }).Count == 0
                && ReadString(CompactSocialEventTurnResult(unresolvedSocial), "groundingStatus", "") == "unresolved");

            string campaign = "input_grounding_" + Guid.NewGuid().ToString("N");
            try
            {
                string original = "  *I trace a 🌿 on her glove.*\r\nHello.\t  ";
                var payload = Payload(original); payload["campaignId"] = campaign;
                string generation;
                using (var connection = OpenCampaignConnection(campaign)) generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                var receipt = AdmitConversationInput(campaign, payload, generation, "npc");
                var pending = ReadOriginalConversationHistory(campaign, payload, "npc", "grounding_session", 100, new());
                Check("admission_before_provider_visible_after_failure", ReadBool(receipt, "persisted", false) && pending.Count == 1
                    && ReadString(pending[0], "text", "") == original && ReadString(pending[0], "deliveryStatus", "") == "submitted");
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed source' WHERE turn_id=$id;", TestDict("id", receipt["sourceId"]));
                bool corruptPendingRejected = false;
                try { ReadOriginalConversationHistory(campaign, payload, "npc", "grounding_session", 100, new()); }
                catch (InvalidOperationException) { corruptPendingRejected = true; }
                finally
                {
                    using var connection = OpenCampaignConnection(campaign);
                    ExecuteSql(connection, "UPDATE conversation_turns SET text=$text WHERE turn_id=$id;", TestDict("text", original, "id", receipt["sourceId"]));
                }
                Check("pending_history_verifies_original_hash", corruptPendingRejected);
                int calls = 0;
                Dictionary<string, object> Interpreter(Dictionary<string, object> request)
                { calls++; return TestDict("ok", true, "content", Json.Serialize(InterpretAll(original))); }
                InterpretConversationInput(campaign, payload, Interpreter);
                var same = Payload(original); same["campaignId"] = campaign; same["conversationSessionId"] = "other_observer_session";
                AdmitConversationInput(campaign, same, generation, "bystander");
                InterpretConversationInput(campaign, same, Interpreter);
                Check("one_interpretation_shared_across_speakers", calls == 1);
                ApplyPlayerInputAudienceToPayload(payload, "npc");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    string id = ReadString(receipt, "sourceId", "");
                    Check("source_only_unindexed_and_exact", ReadString(QuerySql(connection, "SELECT text FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).Single(), "text", "") == original
                        && QuerySql(connection, "SELECT turn_id FROM conversation_turn_fts WHERE turn_id=$id;", TestDict("id", id)).Count == 0);
                    InsertConversationTurn(connection, "accepted_player", "grounding_session", "accepted_event", 0, "accepted_exchange", "player", "player", "Player",
                        ReadString(payload, "playerText", ""), "in_person", 20d, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", new() { "player", "npc" }, payload);
                    BindAcceptedInputSource(connection, payload, "grounding_session", "accepted_exchange", new[] { "accepted_player" });
                }
                var history = ReadOriginalConversationHistory(campaign, payload, "npc", "grounding_session", 100, new());
                Check("accepted_history_exact_original_not_projection", history.Count == 1 && ReadString(history[0], "text", "") == original
                    && ReadString(history[0], "sourceIntegrity", "") == "original_verified");
                bool mismatch = false;
                try { var changed = Payload("Different message"); changed["campaignId"] = campaign; AdmitConversationInput(campaign, changed, generation, "npc"); }
                catch (InvalidOperationException) { mismatch = true; }
                Check("contribution_id_cannot_replace_original", mismatch);
                CompleteConversationInputDelivery(campaign, payload, "npc", TestDict("ok", true, "reply", "Your gesture makes me smile."));
                Check("completed_response_replays_without_generation", ReadBool(ReadConversationInputReplay(campaign, payload, "npc"), "idempotentReplay", false));
                using (var connection = OpenCampaignConnection(campaign))
                using (var transaction = connection.BeginTransaction())
                {
                    SaveConversationInputAcceptance(connection, new AcceptedConversationUnit { Payload = payload, Hero = "bystander", Reply = "An accepted reply before delivery.",
                        Parsed = TestDict(), Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() }, TestDict("sessionId", "other_observer_session"), TestDict());
                    transaction.Commit();
                }
                var crashReplay = ReadConversationInputReplay(campaign, same, "bystander");
                Check("accepted_before_delivery_crash_recovers_without_effect_claim", ReadString(crashReplay, "reply", "") == "An accepted reply before delivery."
                    && ReadString(ReadDictionary(crashReplay, "deliveryRecovery"), "nativeActionPublication", "") == "not_confirmed");

                var sequentialOriginals = new List<string>();
                for (int index = 0; index < 6; index++)
                {
                    string exact = "  *I trace mark " + index + " on the cloth.*\r\nNext.\t";
                    sequentialOriginals.Add(exact);
                    var submission = Payload(exact, "sequential_" + index); submission["campaignId"] = campaign;
                    submission["conversationSessionId"] = "sequential_originals";
                    AdmitConversationInput(campaign, submission, generation, "npc");
                    AdmitConversationInput(campaign, submission, generation, "npc");
                }
                System.Threading.Tasks.Parallel.For(0, 8, index =>
                {
                    var submission = Payload("*I make concurrent mark " + index + ".*", "concurrent_" + index); submission["campaignId"] = campaign;
                    submission["conversationSessionId"] = "concurrent_originals";
                    AdmitConversationInput(campaign, submission, generation, "npc");
                });
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var sequential = QuerySql(connection, "SELECT text,turn_order FROM conversation_turns WHERE session_id='sequential_originals' AND status='source_only';");
                    var concurrent = QuerySql(connection, "SELECT text,turn_order FROM conversation_turns WHERE session_id='concurrent_originals' AND status='source_only';");
                    Check("every_original_in_same_session_survives_and_retries_deduplicate", sequential.Count == sequentialOriginals.Count
                        && sequentialOriginals.All(exact => sequential.Count(row => ReadString(row, "text", "") == exact) == 1)
                        && sequential.Select(row => ReadInt(row, "turn_order", 0)).Distinct().Count() == sequential.Count);
                    Check("simultaneous_contributions_get_distinct_source_slots", concurrent.Count == 8
                        && concurrent.Select(row => ReadString(row, "text", "")).Distinct().Count() == 8
                        && concurrent.Select(row => ReadInt(row, "turn_order", 0)).Distinct().Count() == 8
                        && concurrent.All(row => ReadInt(row, "turn_order", 0) < 0));
                }

                string groupOriginal = "*and balance the bronze bell on my palm.* All set.";
                var group = Payload(groupOriginal, "grounding-floor"); group["campaignId"] = campaign;
                group["conversationSessionId"] = "grounding-group"; group["activeHeroIds"] = new[] { "npc" };
                var floor = GroupConversationFloorApi(group);
                Check("group_floor_admits_one_original_without_interpreter", ReadBool(floor, "ok", false)
                    && ReadInt(floor, "providerCalls", -1) == 0 && CapturePlayerInput(group).Interpretation == null);
                CapturePlayerInput(group).Interpretation = ValidateNarrationInterpretation(groupOriginal, InterpretAll(groupOriginal), group);
                ApplyPlayerInputAudienceToPayload(group, "npc");
                StoreGroupedConversationExchange(campaign, group, "npc", "player", "Player", "Mira", ReadString(group, "playerText", ""),
                    "That bell is unsteady.", "floor-accepted", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "party_chat", "grounding-group");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var originalRows = QuerySql(connection, "SELECT * FROM conversation_turns WHERE session_id='grounding-group' AND status='source_only';");
                    var views = QuerySql(connection, "SELECT * FROM conversation_turns WHERE exchange_id='grounding-floor' AND role='player';");
                    Check("interpreted_group_projection_replaces_only_provisional_view", originalRows.Count == 1 && ReadString(originalRows[0], "text", "") == groupOriginal
                        && views.Count == 2 && views.Count(row => ReadString(row, "status", "") == "active") == 1
                        && views.Any(row => ReadString(row, "status", "") == "active" && ReadString(row, "text", "").Contains("balance the bronze bell"))
                        && views.Any(row => ReadString(row, "status", "") == "superseded_projection" && ReadString(row, "text", "") == groupOriginal));
                }
                var groupHistory = ReadOriginalConversationHistory(campaign, group, "npc", "grounding-group", 100, new());
                Check("group_history_has_one_exact_original", groupHistory.Count(row => ReadString(row, "role", "") == "player") == 1
                    && groupHistory.Any(row => ReadString(row, "text", "") == groupOriginal));

                Dictionary<string, object> ScenePayload(string text, string turn)
                {
                    var p = Payload(text, turn); p["campaignId"] = campaign; p["locationId"] = "town";
                    foreach (var person in ReadDictionaryList(p, "sceneParticipants")) person["nativeLocationDescription"] = "Tavern";
                    return p;
                }
                var clothingOnly = ScenePayload("Hello.", "clothing_only_scene");
                clothingOnly["playerHeroStringId"] = "clothing_scene_player";
                clothingOnly["heroStringId"] = "clothing_scene_npc";
                clothingOnly["sceneParticipants"] = new[]
                {
                    TestDict("heroStringId", "clothing_scene_player", "name", "Player", "role", "player", "nativeLocationDescription", "in Zeonica"),
                    TestDict("heroStringId", "clothing_scene_npc", "name", "Mira", "role", "npc", "nativeLocationDescription", "in Amitatys")
                };
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureConversationSceneStateSchema(connection);
                    UpsertConversationSceneOverride(connection, "clothing_scene_npc", "", "", "a blue cloak", "clothing_only_scene", "dialogue",
                        ConversationWorldHour(clothingOnly), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                }
                var clothingOnlyScene = PrepareConversationSceneState(campaign, clothingOnly, "clothing_scene_npc", "Hello.", new(), "dialogue");
                var clothingOnlyState = ReadDictionaryList(clothingOnlyScene, "current").Single(s => ReadString(s, "heroStringId", "") == "clothing_scene_npc");
                string clothingOnlyPrompt = ReadString(clothingOnlyScene, "prompt", "");
                Check("clothing_revision_does_not_override_shared_venue", ReadLong(clothingOnlyState, "revision", 0) > 0
                    && !ReadBool(clothingOnlyState, "hasNarrativeLocation", true)
                    && ReadString(clothingOnlyScene, "conversationVenue", "") == "in Zeonica"
                    && clothingOnlyPrompt.Contains("- Mira: in Zeonica") && !clothingOnlyPrompt.Contains("- Mira: in Amitatys"), clothingOnlyPrompt);
                var initialMove = ScenePayload("*I walk with Mira to the courtyard.* Come along.", "initial_move_refused");
                foreach (var person in ReadDictionaryList(initialMove, "sceneParticipants"))
                    person["nativeLocationDescription"] = ReadString(person, "heroStringId", "") == "player" ? "in Zeonica" : "in Amitatys";
                var initialMoveInterpretation = InterpretAll(OriginalPlayerInput(initialMove));
                initialMoveInterpretation["sceneChanges"] = new[] { TestDict("kind", "move", "quote", "I walk with Mira to the courtyard.",
                    "location", "courtyard", "subjectIds", new[] { "player", "npc" }, "linkedWithPlayer", true) };
                CapturePlayerInput(initialMove).Interpretation = ValidateNarrationInterpretation(OriginalPlayerInput(initialMove), initialMoveInterpretation, initialMove);
                var initialMoveScene = PrepareConversationSceneState(campaign, initialMove, "npc", OriginalPlayerInput(initialMove), new(), "dialogue");
                string initialProposalId = ReadString(ReadDictionaryList(initialMoveScene, "proposals").Single(p => ReadString(p, "subject_id", "") == "npc"), "proposal_id", "");
                string initialRefusal = "I would rather stay here.";
                var initialDecision = TestDict("sceneDecisions", new[] { TestDict("proposalId", initialProposalId, "outcome", "refused", "replyQuote", initialRefusal) });
                Check("first_move_refusal_keeps_shared_venue_not_remote_native_home",
                    ReadString(initialMoveScene, "conversationVenue", "") == "courtyard"
                    && ReadString(initialMoveScene, "prompt", "").Contains("- Mira: in Zeonica | PROPOSED NOW: courtyard")
                    && ResolvedNarrativeVenue(initialMove, "npc", initialDecision, initialRefusal) == "in Zeonica"
                    && RenderGroundedSceneHeading(initialMove, "npc", initialDecision, initialRefusal).StartsWith("in Zeonica\n\n", StringComparison.Ordinal),
                    ReadString(initialMoveScene, "prompt", ""));
                var opening = ScenePayload("*I find Mira at the camp stables.* Hello.", "scene_open");
                var openingInterpretation = InterpretAll(OriginalPlayerInput(opening));
                openingInterpretation["sceneChanges"] = new[] { TestDict("kind", "establish", "quote", "I find Mira at the camp stables.",
                    "location", "camp stables", "subjectIds", new[] { "player", "npc" }, "linkedWithPlayer", true) };
                CapturePlayerInput(opening).Interpretation = ValidateNarrationInterpretation(OriginalPlayerInput(opening), openingInterpretation, opening);
                PrepareConversationSceneState(campaign, opening, "npc", OriginalPlayerInput(opening), new(), "dialogue");
                var resolution = FinalizeConversationSceneState(campaign, opening, "npc", OriginalPlayerInput(opening), "*She rests at the rail.* Hello.", "dialogue", TestDict("sceneStateUpdates", new object[0]));
                Check("opening_establishes_both_without_model_scene_updates", ReadDictionaryList(resolution, "applied").Count == 2);
                foreach (double day in new[] { 20d, 20d + 1d / 24d, 21d })
                {
                    var next = ScenePayload("Hello again.", "next_" + day); next["worldDay"] = day;
                    var scene = PrepareConversationSceneState(campaign, next, "npc", "Hello again.", new(), "dialogue");
                    Check("venue_survives_reopen_hour_" + day, ReadString(scene, "conversationVenue", "") == "camp stables");
                }
                var move = ScenePayload("*I walk with Mira to the courtyard.* Come along.", "scene_move"); move["worldDay"] = 21d;
                var moveInterpretation = InterpretAll(OriginalPlayerInput(move));
                moveInterpretation["sceneChanges"] = new[] { TestDict("kind", "move", "quote", "I walk with Mira to the courtyard.", "location", "courtyard",
                    "subjectIds", new[] { "player", "npc" }, "linkedWithPlayer", true) };
                CapturePlayerInput(move).Interpretation = ValidateNarrationInterpretation(OriginalPlayerInput(move), moveInterpretation, move);
                var prepared = PrepareConversationSceneState(campaign, move, "npc", OriginalPlayerInput(move), new(), "dialogue");
                string proposal = ReadString(ReadDictionaryList(prepared, "proposals").Single(p => ReadString(p, "subject_id", "") == "npc"), "proposal_id", "");
                string refusal = "I will stay here at the stables.";
                var denied = TestDict("sceneDecisions", new[] { TestDict("proposalId", proposal, "outcome", "refused", "replyQuote", refusal) });
                var denial = FinalizeConversationSceneState(campaign, move, "npc", OriginalPlayerInput(move), refusal, "dialogue", denied);
                Check("linked_movement_refusal_keeps_venue", ReadDictionaryList(denial, "applied").Count == 0 && ResolvedNarrativeVenue(move, "npc", denied, refusal) == "camp stables");
                var alone = ScenePayload("*I walk to the courtyard alone.*", "leave_alone"); alone["worldDay"] = 21d;
                var aloneInterpretation = InterpretAll(OriginalPlayerInput(alone));
                aloneInterpretation["sceneChanges"] = new[] { TestDict("kind", "move", "quote", "I walk to the courtyard alone.", "location", "courtyard",
                    "subjectIds", new[] { "player" }, "linkedWithPlayer", false) };
                CapturePlayerInput(alone).Interpretation = ValidateNarrationInterpretation(OriginalPlayerInput(alone), aloneInterpretation, alone);
                PrepareConversationSceneState(campaign, alone, "npc", OriginalPlayerInput(alone), new(), "dialogue");
                FinalizeConversationSceneState(campaign, alone, "npc", OriginalPlayerInput(alone), "I remain by the rail.", "dialogue", TestDict());
                var stayed = ScenePayload("Goodbye.", "stayed"); stayed["worldDay"] = 21d;
                var stayedScene = PrepareConversationSceneState(campaign, stayed, "npc", "Goodbye.", new(), "dialogue");
                Check("player_leaving_alone_does_not_relocate_npc", ReadString(stayedScene, "conversationVenue", "") == "camp stables"
                    && ReadString(stayedScene, "prompt", "").Contains("- Mira: camp stables")
                    && ReadString(stayedScene, "prompt", "").Contains("- Player: courtyard"), ReadString(stayedScene, "prompt", ""));
                var nativeMove = ScenePayload("Here we are.", "native_move"); nativeMove["worldDay"] = 21d;
                foreach (var person in ReadDictionaryList(nativeMove, "sceneParticipants")) person["nativeLocationDescription"] = "Courtyard";
                Check("genuine_native_room_transition_resets_anchor", ReadString(PrepareConversationSceneState(campaign, nativeMove, "npc", "Here we are.", new(), "dialogue"), "conversationVenue", "") == "Courtyard");
                var branch = ScenePayload("Hello.", "branch"); branch["timelineId"] = "another_branch";
                Check("scene_does_not_cross_timeline", ReadString(PrepareConversationSceneState(campaign, branch, "npc", "Hello.", new(), "dialogue"), "conversationVenue", "") == "Tavern");
                string tentText = "*I walk into Miras tent after the match, holding a new set of armor and a helm that we won in the last few sets* Hello again.";
                var tent = ScenePayload(tentText, "tent_arrival"); tent["worldDay"] = 21d; tent["conversationSessionId"] = "tent_session";
                foreach (var person in ReadDictionaryList(tent, "sceneParticipants")) person["nativeLocationDescription"] = "Lord's hall";
                CapturePlayerInput(tent).Interpretation = ValidateNarrationInterpretation(tentText, InterpretAll(tentText), tent);
                var tentScene = PrepareConversationSceneState(campaign, tent, "npc", tentText, new(), "dialogue");
                Check("named_tent_arrival_establishes_player_and_present_host", ReadString(tentScene, "conversationVenue", "") == "Miras tent"
                    && ReadDictionaryList(tentScene, "proposals").Count == 2
                    && ReadDictionaryList(tentScene, "proposals").All(p => ReadString(p, "kind", "") == "establish")
                    && ReadString(tentScene, "prompt", "").Contains("- Mira: Miras tent")
                    && ReadString(tentScene, "prompt", "").Contains("- Player: Miras tent"), ReadString(tentScene, "prompt", ""));
                string wrongHeading = "Town — the lord's hall, after the tournament\n\n*She examines the armor you brought.*";
                string renderedTent = RenderGroundedSceneHeading(tent, "npc", TestDict(), wrongHeading);
                Check("authoritative_tent_heading_replaces_undated_model_heading", renderedTent == "Miras tent\n\n*She examines the armor you brought.*"
                    && !renderedTent.Contains("hall"));
                string narratedOpening = "*She pauses — looking around the tent*\n\nWelcome.";
                Check("heading_renderer_preserves_opening_action", RenderGroundedSceneHeading(tent, "npc", TestDict(), narratedOpening)
                    == "Miras tent\n\n" + narratedOpening);
                foreach (string spokenOpening in new[] { "The tavern — that's where we first met\n\nI remember it well.",
                    "1084 was the year — the tavern had just opened\n\nA long time ago." })
                    Check("heading_renderer_preserves_dashed_spoken_line_" + results.Count,
                        RenderGroundedSceneHeading(tent, "npc", TestDict(), spokenOpening) == "Miras tent\n\n" + spokenOpening);
                var tentApplied = FinalizeConversationSceneState(campaign, tent, "npc", tentText, renderedTent, "dialogue", TestDict("sceneStateUpdates", new object[0]));
                Check("tent_saved_without_writer_scene_updates", ReadDictionaryList(tentApplied, "applied").Count == 2);
                var tentNext = ScenePayload("*I laugh.* Hello again.", "tent_followup"); tentNext["worldDay"] = 21.5d;
                foreach (var person in ReadDictionaryList(tentNext, "sceneParticipants")) person["nativeLocationDescription"] = "Lord's hall";
                Check("tent_persists_followup_reopen_and_hour_boundary", ReadString(PrepareConversationSceneState(campaign, tentNext, "npc",
                    OriginalPlayerInput(tentNext), new(), "dialogue"), "conversationVenue", "") == "Miras tent");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    InsertConversationTurn(connection, "legacy_missing", "grounding_session", "legacy_event", 1, "legacy_exchange", "player", "player", "Player",
                        "Hello.", "in_person", 20d, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), "", new() { "player", "npc" }, TestDict("timelineId", "grounding_branch"));
                    string recoveredText = "*I brush her sleeve.* Hello.";
                    var capture = TestDict("turnId", "legacy_missing", "campaignId", campaign, "timelineId", "grounding_branch", "sessionId", "grounding_session",
                        "playerId", "player", "originalText", recoveredText, "sourceHash", ConversationExactTextHash(recoveredText), "traceId", "synthetic-trace",
                        "captureFileSha256", new string('a', 64));
                    var audit = BuildConversationOriginalAudit(connection, TestDict("campaignId", campaign, "timelineId", "grounding_branch", "reviewedCaptures", new[] { capture }));
                    var manifest = ReadDictionary(audit, "repairManifest");
                    string acceptedBefore = MemorySpecialistSourceHash(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='legacy_missing';").Single());
                    Check("historical_audit_all_npcs_read_only_and_bound", ReadDictionaryList(manifest, "operations").Count == 1
                        && !ReadBool(audit, "campaignChanged", true));
                    using (var preview = connection.BeginTransaction()) { RepairConversationContinuity(connection, manifest); preview.Rollback(); }
                    Check("historical_preview_rolls_back", !ReadString(QuerySql(connection, "SELECT payload_json FROM conversation_turns WHERE turn_id='legacy_missing';").Single(), "payload_json", "").Contains("inputSourceId"));
                    using (var apply = connection.BeginTransaction()) { RepairConversationContinuity(connection, manifest); apply.Commit(); }
                    Check("historical_original_repair_idempotent", ReadBool(RepairConversationContinuity(connection, manifest), "idempotent", false)
                        && MemorySpecialistSourceHash(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='legacy_missing';").Single()) == acceptedBefore);
                    var restored = ReadOriginalConversationHistory(campaign, payload, "npc", "grounding_session", 100, new());
                    Check("historical_history_uses_exact_reviewed_capture", restored.Any(line => ReadString(line, "text", "") == recoveredText));
                }
                AdvanceMemoryRestoreGeneration(campaign);
                bool stale = false;
                try { CompleteConversationInputDelivery(campaign, payload, "npc", TestDict("ok", true, "reply", "Stale.")); } catch (InvalidOperationException) { stale = true; }
                Check("restore_boundary_rejects_late_receipt", stale);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            RunRecoveredOriginalBranchAssertions(Check);
            return results;
        }

        private static void RunRecoveredOriginalBranchAssertions(Action<string, bool, object> check)
        {
            string campaign = "input_grounding_restore_" + Guid.NewGuid().ToString("N");
            const string originalText = "  *I balance a copper cup on my palm.*\nHello.  ";
            const string secondOriginalText = "*I set the copper cup down.* Thanks.";
            var point = TestDict("savePointId", "before", "timelineId", "parent", "campaignTimeDays", 20d,
                "worldHistorySequence", 8L, "worldHistoryHeadEventId", "saved_head");
            var payload = TestDict("timelineId", "child", "worldDay", 20d, "playerHeroStringId", "player");
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('past',$campaign,'npc','player','closed',1,2,'[""npc"",""player""]','{""timelineId"":""parent""}');", TestDict("campaign", campaign));
                    InsertConversationTurn(connection, "past_player", "past", "past", 0, "past", "player", "player", "Player",
                        "Hello.", "in_person", 20d, 1L, "", new() { "player", "npc" }, TestDict("timelineId", "parent"));
                    InsertConversationTurn(connection, "past_player_next", "past", "past_next", 1, "past_next", "player", "player", "Player",
                        "Thanks.", "in_person", 20d, 2L, "", new() { "player", "npc" }, TestDict("timelineId", "parent"));
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "before");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "before");
                AdvanceMemoryRestoreGeneration(campaign, point);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    BindRecoveredMemorySources(connection, "parent", "child", 8L, "saved_head");
                    var accepted = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='past_player';").Single();
                    string acceptedHash = MemorySpecialistSourceHash(accepted);
                    var access = BuildKnowledgeAccessContext("npc", "player", "", payload);
                    check("recovery_inherited_source_has_snapshot_proof", PrecisionTurnVisible(accepted, "npc", access, connection), null);
                    var capture = TestDict("turnId", "past_player", "campaignId", campaign, "timelineId", "parent", "sessionId", "past",
                        "playerId", "player", "originalText", originalText, "sourceHash", ConversationExactTextHash(originalText),
                        "traceId", "fixture-original", "captureFileSha256", new string('b', 64));
                    var secondCapture = CloneDictionary(capture); secondCapture["turnId"] = "past_player_next";
                    secondCapture["originalText"] = secondOriginalText; secondCapture["sourceHash"] = ConversationExactTextHash(secondOriginalText);
                    var request = TestDict("campaignId", campaign, "timelineId", "child", "reviewedCaptures", new[] { capture, secondCapture });
                    var audit = BuildConversationOriginalAudit(connection, request);
                    var manifest = ReadDictionary(audit, "repairManifest");
                    check("recovery_inherited_capture_binds_original_timeline", ReadDictionaryList(manifest, "operations").Count == 2, audit);
                    using (var preview = connection.BeginTransaction()) { RepairConversationContinuity(connection, manifest); preview.Rollback(); }
                    check("recovery_inherited_preview_has_no_original_or_metadata_write", QuerySql(connection, "SELECT turn_id FROM conversation_turns WHERE status='source_only';").Count == 0
                        && MemorySpecialistSourceHash(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='past_player';").Single()) == acceptedHash, null);
                    using (var apply = connection.BeginTransaction()) { RepairConversationContinuity(connection, manifest); apply.Commit(); }
                    var after = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='past_player';").Single();
                    check("recovery_preserves_inherited_row_hash_and_npc_visibility", MemorySpecialistSourceHash(after) == acceptedHash
                        && PrecisionTurnVisible(after, "npc", access, connection), null);
                    check("recovery_rescan_has_no_duplicate_operation", ReadDictionaryList(ReadDictionary(BuildConversationOriginalAudit(connection, request), "repairManifest"), "operations").Count == 0, null);
                    var recoveredRows = QuerySql(connection, "SELECT * FROM conversation_turns WHERE status='source_only';");
                    var recovered = recoveredRows.Single(r => ReadString(RecoveredOriginalLink(r), "acceptedTurnId", "") == "past_player");
                    check("recovery_multiple_originals_in_one_session", recoveredRows.Count == 2
                        && recoveredRows.Select(r => ReadInt(r, "turn_order", 0)).Distinct().Count() == 2, null);
                    check("recovery_original_stays_private_and_unindexed", !PrecisionTurnVisible(recovered, "npc", access, connection)
                        && QuerySql(connection, "SELECT * FROM conversation_turn_fts WHERE turn_id=$id;", TestDict("id", recovered["turn_id"])).Count == 0, null);
                    var history = ReadOriginalConversationHistory(campaign, payload, "npc", "past", 100, new());
                    check("recovery_inherited_history_returns_exact_originals_once", history.Count == 2 && ReadString(history[0], "text", "") == originalText
                        && ReadString(history[1], "text", "") == secondOriginalText, history);
                    using (var invalidation = connection.BeginTransaction())
                    {
                        ExecuteSql(connection, "UPDATE conversation_turns SET text='A changed accepted turn.' WHERE turn_id='past_player';");
                        var changed = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='past_player';").Single();
                        check("recovery_link_rejects_changed_accepted_source", FindRecoveredOriginal(changed, new[] { recovered }) == null
                            && !PrecisionTurnVisible(changed, "npc", access, connection), null);
                        invalidation.Rollback();
                    }
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "recovered");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "recovered");
                point["savePointId"] = "recovered"; point["timelineId"] = "child";
                AdvanceMemoryRestoreGeneration(campaign, point);
                var restored = ReadOriginalConversationHistory(campaign, payload, "npc", "past", 100, new());
                check("recovery_original_survives_next_save_restore", restored.Count == 2 && ReadString(restored[0], "text", "") == originalText
                    && ReadString(restored[1], "text", "") == secondOriginalText, restored);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
