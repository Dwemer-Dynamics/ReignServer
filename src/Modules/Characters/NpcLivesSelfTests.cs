using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using ReignBeta.Shared.Characters;
using Reign.Core.Contracts.Actions;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunNpcLivesSelfTests()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("NPC Lives checks require the isolated Verification Lab.");
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict("caseId", "npc_lives_" + id, "passed", passed, "summary", id, "data", data));
            string campaign = "npc_lives_test_" + Guid.NewGuid().ToString("N");
            try
            {
                string fixture = Path.Combine(VerificationDir, "npc-lives-options-fixture.json");
                WriteJsonObject(fixture, TestDict("markup", ReignXpOptionsHtml(), "script", ReignXpOptionsScript(),
                    "diagnosticsMarkup", NpcLivesDiagnosticsHtml(), "diagnosticsScript", NpcStoryEditorScript(),
                    "definitions", NpcStoryCatalog.All));
                Check("production_options_browser_fixture", File.Exists(fixture), fixture);
                string simpleOptions = ReignXpOptionsHtml(), diagnostics = NpcLivesDiagnosticsHtml();
                Check("story_editor_and_advanced_controls_live_in_diagnostics", !simpleOptions.Contains("npcStoryJson")
                    && !simpleOptions.Contains("npcLivesApproachProvider") && !simpleOptions.Contains("npcLivesResumeHours")
                    && simpleOptions.Contains("npcLivesSummonsTravelTimePercent") && simpleOptions.Contains("npcLivesChance20")
                    && diagnostics.Contains("npcStoryJson") && diagnostics.Contains("npcLivesResumeHours")
                    && diagnostics.Contains("npcLivesApproachProvider") && !diagnostics.Contains("npcLivesFrequencyPercent"));
                using var c = OpenCampaignConnection(campaign);
                EnsureAcceptedConversationUnitSchema(c); EnsureConversationAgencySchema(c);
                string generation = NpcLivesGeneration(c);
                var state = ReadNpcLives(c, campaign, "test");
                var options = new NpcLivesOptions();
                results.AddRange(RunNpcApproachConversationSelfTests(campaign));
                results.AddRange(RunNpcGroupApproachSessionSelfTests(campaign));
                results.AddRange(RunNpcStoryInvitationSelfTests(campaign, c));
                var initialApproachOff = new NpcLivesOptions(k => k != NpcLivesOptions.InitialApproachKey, _ => null);
                Check("initial_approach_switch_is_simple_independent_and_validated", simpleOptions.Contains("NPC speaks first when approached")
                    && simpleOptions.Contains("id='" + NpcLivesOptions.InitialApproachKey + "'")
                    && simpleOptions.Contains("Wilderness events, social events")
                    && !diagnostics.Contains("id='" + NpcLivesOptions.InitialApproachKey + "'")
                    && !initialApproachOff.Allows("approach") && initialApproachOff.Allows("major")
                    && initialApproachOff.Allows("announcement") && initialApproachOff.Allows("reaction")
                    && ValidateNpcLivesOptions(TestDict(NpcLivesOptions.InitialApproachKey, false)).Length == 0
                    && ValidateNpcLivesOptions(TestDict(NpcLivesOptions.InitialApproachKey, "false")).Length > 0
                    && !ReadBool(NpcLivesOptionsForClient(TestDict(NpcLivesOptions.InitialApproachKey, false)), NpcLivesOptions.InitialApproachKey, true));
                Check("approach_has_no_short_timeout_control", !NpcLivesOptions.Numbers.Any(s => s.Key == "npcLivesApproachTimeoutMs")
                    && !simpleOptions.Contains("npcLivesApproachTimeoutMs") && !diagnostics.Contains("npcLivesApproachTimeoutMs"));
                var fixtureState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                var authored = NpcStoryCatalog.All.First(d => d.Category == "brief" && d.Phases[0].LocationKind == "current");
                string prepared = PrepareNpcLivesStoryInvitation(fixtureState, authored, 0, "companion", new List<NpcStoryLocation>(), "town", "fixture_1", 10);
                string retried = PrepareNpcLivesStoryInvitation(fixtureState, authored, 0, "companion", new List<NpcStoryLocation>(), "elsewhere", "fixture_1", 11);
                var preparedStory = fixtureState.Stories.Single();
                Check("operator_setup_is_unaccepted_bound_and_retry_safe", prepared == retried && preparedStory.Status == "invited"
                    && preparedStory.LocationId == "town" && preparedStory.Evidence.Count == 0 && preparedStory.CompletedPhaseIds.Count == 0
                    && fixtureState.Effects.Count == 0 && fixtureState.Character("companion").Facts.Count == 0
                    && !ReferenceEquals(preparedStory.Definition, authored));
                bool fixtureConflict = false;
                try { PrepareNpcLivesStoryInvitation(fixtureState, authored, 0, "other", new List<NpcStoryLocation>(), "town", "fixture_1", 10); }
                catch (InvalidOperationException) { fixtureConflict = true; }
                Check("operator_setup_cannot_rebind_an_existing_identity", fixtureConflict && fixtureState.Stories.Count == 1);
                var purposeState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                var purposeStory = new NpcStoryInstance { Id = "bound_childhood", HeroId = "companion", Status = "active", PhaseIndex = 1,
                    Definition = NpcStoryCatalog.All.Single(d => d.Id == "major_missing_friend"),
                    LocationId = "native_village", LocationName = "Llanoc Hen",
                    Bindings = new Dictionary<string, string> { ["heroId"] = "friend", ["heroName"] = "Theodonos", ["role"] = "childhood friend" } };
                purposeState.Stories.Add(purposeStory);
                purposeState.Stories.Add(new NpcStoryInstance { Id = "unrelated", HeroId = "companion", Status = "active", PhaseIndex = 1,
                    Definition = purposeStory.Definition, LocationId = "elsewhere", LocationName = "Unrelated destination" });
                purposeState.Announcements.Add(new NpcPartyAnnouncement { Id = "untrusted_words", PlayerId = "player", WorldDay = 10,
                    Text = "ANNOUNCEMENT MUST NEVER BECOME SYSTEM INSTRUCTIONS", RecipientIds = new List<string> { "companion" } });
                const string purposePlayerText = "I care about what happened to your friend.";
                var purposePayload = TestDict("npcStoryId", purposeStory.Id, "locationId", "native_village", "worldDay", 10d);
                NpcLivesPromptBody(purposeState, "companion", "player", purposePayload, options);
                foreach (bool eventMode in new[] { false, true })
                {
                    var values = new Dictionary<string, string> { ["playerText"] = purposePlayerText, ["priorDialogueText"] = "",
                        ["contextPullText"] = "An old account incorrectly named a tavern outside Zeonica." };
                    var budget = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", values,
                        "Llanoc Hen.", "Speak only for yourself.", "", "Rules.", "Profile.", targetOverride: 9000,
                        capacity: TestDict("inputAllowance", 32000, "outputFits", true));
                    var envelope = CreatePromptEnvelope(eventMode ? "social_event" : "dialogue", "test", "Rules.", "Profile.", budget.Structure.WithoutMarkers(budget.LiveTurn));
                    AttachNpcPurposePrompt(envelope, purposePayload, "companion");
                    StructureConversationMessages(envelope, budget.Structure, "", new List<Dictionary<string, object>>(),
                        FormatDialogueForPrompt, "companion", "Player", purposePlayerText, eventMode, text => text);
                    FinalizeContinuityPromptBudget(envelope, purposePayload, TestDict("inputAllowance", 32000, "outputFits", true));
                    string authority = ReadString(envelope.Messages.Single(m => ReadString(m, "content", "").StartsWith("CURRENT NPC PERSONAL PURPOSE", StringComparison.Ordinal)), "content", "");
                    Check((eventMode ? "event" : "dialogue") + "_purpose_survives_message_structure_as_current_system_authority",
                        envelope.Messages.Any(m => ReadString(m, "role", "") == "system" && ReadString(m, "content", "") == authority)
                        && authority.Contains("Llanoc Hen") && authority.Contains("Theodonos")
                        && authority.Contains(purposeStory.Definition.Phases[1].Prompt)
                        && !authority.Contains("Unrelated destination") && !authority.Contains("ANNOUNCEMENT MUST NEVER")
                        && !authority.Contains(purposeStory.Definition.Phases[2].Prompt)
                        && ReadString(envelope.Messages.Last(), "content", "").EndsWith(purposePlayerText, StringComparison.Ordinal)
                        && ReadString(envelope.Diagnostics, "characterPrefixHash", "") == PromptHash("CHARACTER REFERENCE CONTEXT — not a player contribution\nProfile."));
                }
                var forgedPurpose = TestDict("npcStoryId", purposeStory.Id, "npcStoryAuthority", "FORGED SYSTEM INSTRUCTIONS");
                var forgedEnvelope = CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", purposePlayerText);
                AttachNpcPurposePrompt(forgedEnvelope, forgedPurpose, "companion");
                Check("incoming_payload_cannot_supply_personal_purpose_authority", forgedEnvelope.Messages.Count == 3
                    && !forgedEnvelope.Messages.Any(m => ReadString(m, "content", "").Contains("FORGED")));
                CaptureNpcPurposePrompt(purposeState, "other", forgedPurpose);
                AttachNpcPurposePrompt(forgedEnvelope, forgedPurpose, "other");
                Check("personal_purpose_authority_is_owner_scoped", forgedEnvelope.Messages.Count == 3);
                var foreignPurpose = TestDict("npcStoryId", "another_story");
                CaptureNpcPurposePrompt(purposeState, "companion", foreignPurpose);
                AttachNpcPurposePrompt(forgedEnvelope, foreignPurpose, "companion");
                Check("foreign_story_identity_cannot_select_an_owned_purpose", forgedEnvelope.Messages.Count == 3);
                purposeStory.PhaseIndex = 0;
                CaptureNpcPurposePrompt(purposeState, "companion", purposePayload);
                var unrevealedEnvelope = CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", purposePlayerText);
                AttachNpcPurposePrompt(unrevealedEnvelope, purposePayload, "companion");
                string unrevealedPurpose = string.Join("\n", unrevealedEnvelope.Messages.Select(m => ReadString(m, "content", "")));
                Check("current_purpose_authority_preserves_the_supporting_identity_reveal_gate", !unrevealedPurpose.Contains("Theodonos")
                    && !unrevealedPurpose.Contains("Established story binding heroId: friend"));
                Check("summons_instructions_match_solo_camp_and_preserve_historical_terms", StructuredActionFixedPrompt().Contains("attends alone")
                    && StructuredActionFixedPrompt().Contains("throughout the wait and meeting")
                    && StructuredActionFixedPrompt().Contains("legacy_party") && !StructuredActionFixedPrompt().Contains("noble and their own party arrive"));
                var duelState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                var duelStory = new NpcStoryInstance { Id = "training", HeroId = "companion", Status = "active", PhaseIndex = 1,
                    Definition = NpcStoryCatalog.All.Single(d => d.Id == "major_training"), LocationId = "town" };
                duelState.Stories.Add(duelStory);
                var duplicateDuel = new ActionDecision { Kind = "propose", Clauses = new List<ActionProposedTerm> {
                    new ActionProposedTerm { ActionId = "duel_player", ActorId = "companion", RecipientId = "player" } } };
                Check("native_story_duel_cannot_publish_a_second_ordinary_duel", IsDuplicateNpcStoryDuelProposal(duelState,
                    "companion", "player", TestDict("locationId", "town"), duplicateDuel));
                Check("native_story_duel_guard_preserves_foreign_context_and_consent", !IsDuplicateNpcStoryDuelProposal(duelState,
                    "companion", "player", TestDict("locationId", "elsewhere"), duplicateDuel)
                    && !IsDuplicateNpcStoryDuelProposal(duelState, "other", "player", TestDict("locationId", "town"), duplicateDuel)
                    && !IsDuplicateNpcStoryDuelProposal(duelState, "companion", "player", TestDict("locationId", "town", "npcStoryId", "foreign"), duplicateDuel));
                duplicateDuel.Kind = "accept";
                Check("story_duel_guard_does_not_replace_existing_action_acceptance", !IsDuplicateNpcStoryDuelProposal(duelState,
                    "companion", "player", TestDict("npcStoryId", "training"), duplicateDuel));
                duplicateDuel.Kind = "propose"; duelStory.Status = "completed";
                Check("completed_story_releases_ordinary_duel_proposals", !IsDuplicateNpcStoryDuelProposal(duelState,
                    "companion", "player", TestDict("npcStoryId", "training"), duplicateDuel));
                duelStory.Status = "active"; duplicateDuel.Clauses[0].ActionId = "gift_gold";
                Check("native_story_duel_guard_preserves_unrelated_actions", !IsDuplicateNpcStoryDuelProposal(duelState,
                    "companion", "player", TestDict("npcStoryId", "training"), duplicateDuel));
                var leaveFixtureState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                var outing = NpcStoryCatalog.All.First(d => d.Category == "outing" && !d.Phases[0].RequiresCoast);
                var candidate = new NpcLifeCandidate { HeroId = "companion", Affinity = 31, Honor = 64, Compassion = 77, DutyPressure = 1 };
                var nearby = new[] { new NpcStoryLocation { Id = "nearby_town", Kind = "town", TravelDays = .25 } };
                string leaveFixtureId = PrepareNpcLivesLeaveInvitation(leaveFixtureState, outing, candidate, nearby, "town", "leave_fixture", 10);
                var preparedLeave = leaveFixtureState.Leaves.Single();
                Check("authored_leave_setup_preserves_personality_and_grants_no_departure", preparedLeave.Id == leaveFixtureId
                    && preparedLeave.Status == "discussing" && preparedLeave.Trust == 31 && preparedLeave.Honor == 64
                    && preparedLeave.Compassion == 77 && preparedLeave.DutyPressure == 1 && preparedLeave.Purpose.Contains(outing.Title)
                    && preparedLeave.DestinationId == "nearby_town" && !preparedLeave.DepartureDay.HasValue
                    && !preparedLeave.DepartedDay.HasValue && !preparedLeave.ReturnDay.HasValue && !preparedLeave.IndependentDecision
                    && leaveFixtureState.Stories.Count == 0 && leaveFixtureState.Effects.Count == 0);
                Check("authored_leave_setup_retries_preserve_exact_request", PrepareNpcLivesLeaveInvitation(leaveFixtureState,
                    outing, candidate, nearby, "elsewhere", "leave_fixture", 11) == leaveFixtureId && leaveFixtureState.Leaves.Count == 1);
                bool leaveConflict = false, infeasibleLeave = false;
                try { PrepareNpcLivesLeaveInvitation(leaveFixtureState, outing, candidate, nearby, "town", "other_fixture", 10); }
                catch (InvalidOperationException) { leaveConflict = true; }
                try { PrepareNpcLivesLeaveInvitation(new NpcLivesState(), outing, candidate,
                    new[] { new NpcStoryLocation { Id = "far", Kind = "town", TravelDays = 100 } }, "town", "far_fixture", 10); }
                catch (InvalidOperationException) { infeasibleLeave = true; }
                Check("authored_leave_setup_rejects_conflicts_and_infeasible_travel", leaveConflict && infeasibleLeave);
                var invitationPrompt = new System.Text.StringBuilder();
                AppendNpcStoryPrompt(invitationPrompt, fixtureState, "companion", TestDict("locationId", "town"));
                Check("invitation_is_optional_reference_without_activity_or_progress_instructions", invitationPrompt.ToString().Contains(authored.Phases[0].Title)
                    && invitationPrompt.ToString().Contains("PENDING PERSONAL INVITATION")
                    && invitationPrompt.ToString().Contains("not a current conversation objective")
                    && !invitationPrompt.ToString().Contains(authored.Phases[0].Prompt)
                    && invitationPrompt.ToString().Contains("Do not claim acceptance, progress or rewards")
                    && invitationPrompt.ToString().Contains("Do not also assign relationship changes")
                    && !invitationPrompt.ToString().Contains("CONVERSATION OBJECTIVE"));
                var identityDefinition = NpcStoryCatalog.All.Single(d => d.Id == "major_missing_friend");
                var identityStory = new NpcStoryInstance { Id = "unbound_friend", HeroId = "companion", Status = "invited",
                    Definition = identityDefinition, PhaseIndex = identityDefinition.SupportingRevealPhase, LocationId = "town" };
                var identityState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                identityState.Stories.Add(identityStory);
                string IdentityPrompt()
                {
                    var text = new System.Text.StringBuilder();
                    AppendNpcStoryPrompt(text, identityState, "companion", TestDict("locationId", "town"));
                    return text.ToString();
                }
                string unboundInvitation = IdentityPrompt();
                identityStory.Status = "active";
                string unboundAcceptance = IdentityPrompt();
                Check("unbound_supporting_identity_is_deferred_in_invitation_and_acceptance",
                    unboundInvitation.Contains("Refer to this person only by that relationship")
                    && unboundAcceptance.Contains("Do not invent a name, nickname, gender, appearance or hometown")
                    && unboundAcceptance.Contains("do not accept an invented identity suggested by the player"));
                identityStory.Bindings["heroId"] = "native_friend"; identityStory.Bindings["heroName"] = "Established Friend";
                string boundIdentity = IdentityPrompt();
                identityStory.PhaseIndex = 0;
                string earlierIdentity = IdentityPrompt();
                Check("bound_supporting_identity_replaces_deferral_without_revealing_future_secrets",
                    boundIdentity.Contains("Established story binding heroName: Established Friend")
                    && !boundIdentity.Contains("has not been established yet")
                    && identityStory.PhaseIndex < identityDefinition.SupportingRevealPhase
                    && !earlierIdentity.Contains("Established Friend") && !earlierIdentity.Contains("native_friend"));
                identityStory.PhaseIndex = identityDefinition.SupportingRevealPhase;
                var nativePlaces = new[] { new NpcStoryLocation { Id = "town", Name = "Established Village", Kind = "village" } };
                NpcLivesEngine.Evaluate(identityState, new List<NpcLifeCandidate>(), NpcStoryCatalog.All, nativePlaces, options, 10, "elsewhere");
                string canonicalIdentity = IdentityPrompt();
                var restoredIdentity = Json.Deserialize<NpcLivesState>(Json.Serialize(identityState)).Stories.Single();
                Check("native_destination_label_upgrades_without_rebinding_or_revealing_other_chapters",
                    identityStory.LocationId == "town" && restoredIdentity.LocationName == "Established Village"
                    && canonicalIdentity.Contains("fixed native destination is Established Village")
                    && canonicalIdentity.Contains("take precedence over conflicting older dialogue, memories or player suggestions")
                    && canonicalIdentity.Contains("Established story binding heroName: Established Friend")
                    && identityStory.Evidence.Count == 0 && identityState.Effects.Count == 0);
                bool invitedOwner = NpcLivesOwnsRelationshipReward(fixtureState, "companion", TestDict("npcStoryId", prepared))
                    && !NpcLivesOwnsRelationshipReward(fixtureState, "companion", TestDict());
                preparedStory.Status = "active";
                Check("personal_reward_owner_is_bound_to_story_character_and_actual_place", invitedOwner
                    && NpcLivesOwnsRelationshipReward(fixtureState, "companion", TestDict("locationId", "town"))
                    && !NpcLivesOwnsRelationshipReward(fixtureState, "companion", TestDict("locationId", "elsewhere"))
                    && !NpcLivesOwnsRelationshipReward(fixtureState, "other", TestDict("locationId", "town", "npcStoryId", prepared))
                    && !NpcLivesOwnsRelationshipReward(fixtureState, "companion", TestDict("locationId", "town", "npcStoryId", "foreign_story")));
                preparedStory.Status = "completed";
                Check("completed_personal_story_releases_routine_relationships", !NpcLivesOwnsRelationshipReward(fixtureState, "companion",
                    TestDict("locationId", "town", "npcStoryId", prepared)));
                preparedStory.Status = "invited";
                var ownedAssessments = new List<Dictionary<string, object>> {
                    TestDict("targetHeroStringId", "player", "actKind", "meaningful_support", "valence", "positive"),
                    TestDict("targetHeroStringId", "other", "actKind", "routine_conversation", "valence", "positive") };
                Check("committed_personal_receipt_prevents_duplicate_player_affinity_only", ApplyNpcLivesRelationshipOwnership(
                    TestDict("acceptedUnit", TestDict("ok", true, "personalRelationshipOwner", "npc_lives")), ownedAssessments, "player")
                    && ownedAssessments.Count == 1 && ReadString(ownedAssessments.Single(), "targetHeroStringId", "") == "other");
                bool forgedOwnerRejected = new[] { TestDict("personalRelationshipOwner", "npc_lives"),
                    TestDict("acceptedUnit", TestDict("ok", false, "personalRelationshipOwner", "npc_lives")),
                    TestDict("acceptedUnit", TestDict("ok", true, "personalRelationshipOwner", "other")) }.All(exchange => {
                        var rows = new List<Dictionary<string, object>> { TestDict("targetHeroStringId", "player", "valence", "positive") };
                        return !ApplyNpcLivesRelationshipOwnership(exchange, rows, "player") && rows.Count == 1;
                    });
                Check("uncommitted_or_foreign_owner_cannot_suppress_relationships", forgedOwnerRejected);
                EnsureCategorizedMemorySchema(c);
                var repeatDefinition = Json.Deserialize<NpcStoryDefinition>(Json.Serialize(NpcStoryCatalog.All.Single(d => d.Id == "brief_cooking_01")));
                repeatDefinition.Phases[0].Objectives[0].RequiredProgress = 3;
                string repeatObjective = repeatDefinition.Phases[0].Objectives[0].Id;
                var repeatStory = new NpcStoryInstance { Id = "repeat_story", HeroId = "companion", Definition = repeatDefinition,
                    Status = "active", LocationId = "town" };
                repeatStory.Evidence.Add(new NpcStoryEvidence { ObjectiveId = repeatObjective, SourceId = "repeat_original",
                    HeroId = "companion", LocationId = "town", Quote = "Add unsalted broth." });
                var repeatState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                repeatState.Stories.Add(repeatStory);
                ExecuteSql(c, @"INSERT INTO conversation_turns(turn_id,session_id,turn_order,role,speaker_id,text,ts)
VALUES('repeat_original','repeat_session',0,'player','player','Add unsalted broth. Then taste it.',1);");
                void SubmitRepeatProbe(string sourceId, string contributionText, string excerpt)
                {
                    var unit = new AcceptedConversationUnit { Campaign = campaign, Player = "player", Hero = "companion",
                        Payload = TestDict("locationId", "town", "worldDay", 10d),
                        Parsed = TestDict("npcStoryAssessments", new[] { TestDict("storyId", repeatStory.Id, "revision", 1,
                            "objectiveId", repeatObjective, "outcome", "progress", "evidenceQuote", excerpt) }) };
                    AcceptNpcStoryAssessment(c, repeatState, unit, new List<Dictionary<string, object>> {
                        TestDict("turn_id", sourceId, "speaker_id", "player", "role", "player", "text", contributionText) });
                }
                SubmitRepeatProbe("repeat_second", "Add unsalted broth. Then taste it.", "Then taste it.");
                SubmitRepeatProbe("repeat_whitespace", "  Add unsalted broth.\nThen taste it.  ", "Then taste it.");
                Check("same_accepted_contribution_cannot_progress_by_changing_its_quote", repeatStory.Evidence.Count == 1
                    && repeatState.Effects.Count == 0 && repeatStory.Status == "active");
                SubmitRepeatProbe("repeat_new", "Keep half aside and compare it after diluting the rest.", "Keep half aside and compare it after diluting the rest.");
                Check("different_accepted_contribution_still_progresses", repeatStory.Evidence.Count == 2
                    && !RepeatedNpcStoryContribution(c, repeatStory, repeatObjective, "foreign_player", "Add unsalted broth. Then taste it."));
                const string conditionalAdoption = "But if it passes — then yes. Clover travels with us, and the feeding and the water and the gnawing stick are my responsibility, not yours.";
                Check("conditional_adoption_and_cherry_picked_yes_are_not_final_evidence", !NpcStoryResolutionMatches(conditionalAdoption, conditionalAdoption)
                    && !NpcStoryResolutionMatches(conditionalAdoption, "Clover travels with us, and the feeding and the water and the gnawing stick are my responsibility, not yours.")
                    && !NpcStoryResolutionMatches("Let's go meet this keeper, then.", "Let's go meet this keeper, then."));
                foreach (string outcome in new[] { "adopted", "declined" })
                {
                    var petDefinition = NpcStoryCatalog.All.Single(d => d.Id == "outing_pet_adoption");
                    var petStory = new NpcStoryInstance { Id = "pet_resolution_" + outcome, HeroId = "pet_npc_" + outcome,
                        Definition = petDefinition, Status = "active", LocationId = "town" };
                    var petState = new NpcLivesState { CampaignId = campaign, TimelineId = "test", Stories = new List<NpcStoryInstance> { petStory } };
                    var activity = petDefinition.Phases[0].Objectives.Single(o => !o.Native);
                    petStory.Evidence.Add(new NpcStoryEvidence { ObjectiveId = petDefinition.Phases[0].Objectives.Single(o => o.Native).Id,
                        SourceId = "arrival", HeroId = petStory.HeroId, Kind = "native", LocationId = "town" });
                    void SubmitPet(string id, string words, string result, string reply, string conclusion)
                    {
                        var unit = new AcceptedConversationUnit { Campaign = campaign, Hero = petStory.HeroId, Player = "player", Reply = reply,
                            Payload = TestDict("locationId", "town", "npcStoryId", petStory.Id, "worldDay", 10d),
                            Parsed = TestDict("npcStoryAssessments", new[] { TestDict("storyId", petStory.Id, "revision", 1,
                                "objectiveId", activity.Id, "outcome", result, "evidenceQuote", words, "resolutionQuote", conclusion) }) };
                        AcceptNpcStoryAssessment(c, petState, unit, new List<Dictionary<string, object>> {
                            TestDict("turn_id", id, "speaker_id", "player", "role", "player", "text", words) });
                    }
                    SubmitPet("care", "Hay, shade and water keep a rabbit healthy on the road.", "progress", "I agree about care.", "");
                    SubmitPet("plan", "We can meet the keeper and inspect the carrier before deciding.", "progress", conditionalAdoption, "");
                    Check("pet_participation_without_a_final_choice_is_unfinished_" + outcome,
                        petStory.Status == "active" && petState.Effects.Count == 0 && petState.Character(petStory.HeroId).Facts.Count == 0);
                    SubmitPet("conditional", "I support adopting the rabbit if its health is good.", outcome, conditionalAdoption, conditionalAdoption);
                    SubmitPet("foreign_quote", "The keeper has shown us the rabbit and we have made our choice.", outcome, "That decision is still ahead.", "I have adopted Clover as my companion.");
                    Check("uncertain_or_fabricated_resolution_does_not_publish_facts_" + outcome,
                        petStory.Status == "active" && petStory.Evidence.Count == 3 && petState.Effects.Count == 0);
                    string conclusion = outcome == "adopted" ? "I have adopted Clover as my companion." : "I have decided against adopting this rabbit.";
                    SubmitPet("final", "The keeper has shown us the rabbit and we have made our final choice.", outcome, conclusion, conclusion);
                    Check("verified_pet_choice_publishes_only_matching_facts_and_rewards_" + outcome, petStory.Status == "completed"
                        && petState.Character(petStory.HeroId).Facts.Any(f => f.Kind == "pet") == (outcome == "adopted")
                        && petState.Character(petStory.HeroId).Facts.Any(f => f.Id.EndsWith(":rabbit_declined", StringComparison.Ordinal)) == (outcome == "declined")
                        && petState.Effects.Count == 2 && !petState.Advance(petStory, 11));
                }
                var destinationChoices = new[] { new NpcStoryLocation { Id = "town", Kind = "town", Available = true },
                    new NpcStoryLocation { Id = "camp", Kind = "hideout", Available = true },
                    new NpcStoryLocation { Id = "blocked", Kind = "town", Available = false } };
                Check("operator_destination_selection_uses_only_real_matching_available_candidates",
                    SelectNpcLivesFixtureLocations(new NpcStoryPhase { LocationKind = "town" }, destinationChoices, "town", "town").Single().Id == "town"
                    && SelectNpcLivesFixtureLocations(new NpcStoryPhase { LocationKind = "hideout" }, destinationChoices, "town", "camp").Single().Id == "camp"
                    && SelectNpcLivesFixtureLocations(authored.Phases[0], destinationChoices, "town", "town").Single().Id == "town");
                bool destinationsRejected = true;
                foreach (var selection in new[] { "unknown", "camp", "blocked" })
                { try { SelectNpcLivesFixtureLocations(new NpcStoryPhase { LocationKind = "town" }, destinationChoices, "town", selection); destinationsRejected = false; }
                    catch (InvalidOperationException) { } }
                try { SelectNpcLivesFixtureLocations(authored.Phases[0], destinationChoices, "elsewhere", "town"); destinationsRejected = false; }
                catch (InvalidOperationException) { }
                try { SelectNpcLivesFixtureLocations(new NpcStoryPhase { LocationKind = "town", RequiresCoast = true }, destinationChoices, "town", "town"); destinationsRejected = false; }
                catch (InvalidOperationException) { }
                Check("operator_destination_rejects_unknown_wrong_kind_blocked_coast_and_current_mismatch", destinationsRejected);
                var caseFields = TestDict("caseId", "fixture", "phase", "prepare", "heroId", "companion", "templateId", authored.Id, "chapterId", authored.Phases[0].Id);
                var fixtureCommand = TestDict("commandId", "command", "operation", "npc_lives_test", "status", "leased", "leaseId", "lease",
                    "leaseExpiresUtc", DateTimeOffset.UtcNow.AddMinutes(2).ToString("o"), "testCase", caseFields);
                var fixtureRun = TestDict("runId", "run", "campaignId", campaign, "gameInstanceId", "game", "mode", "npc_lives", "status", "running",
                    "enrollment", TestDict("source", "reign_mcp_npc_lives", "campaignId", campaign, "campaignTestRunId", "enrolled", "timelineId", "test",
                        "disposableSaveName", "Current", "protectedBaselineSaveName", "Baseline", "recoveryCheckpointVerified", true), "commands", new[] { fixtureCommand });
                var fixtureRuntime = TestDict("campaignId", campaign, "gameInstanceId", "game", "activeSaveName", "Current", "receivedUtc", DateTimeOffset.UtcNow.ToString("o"),
                    "saveSync", TestDict("ready", true, "alignmentPending", false, "timelineId", "test"), "nativeSave", TestDict("isSaving", false));
                var fixtureArm = TestDict("expiresUtc", DateTimeOffset.UtcNow.AddMinutes(2).ToString("o"));
                var fixtureRequest = TestDict("campaignId", campaign, "timelineId", "test", "gameInstanceId", "game", "runId", "run", "commandId", "command",
                    "leaseId", "lease", "activeSaveName", "Current", "timeMode", "Stop", "nativeSaveInProgress", false, "testCase", caseFields);
                Check("operator_setup_requires_exact_durable_command_and_current", NpcLivesFixtureAuthorized(fixtureRequest, fixtureRun, fixtureRuntime, fixtureArm));
                bool executingStatesAccepted = true;
                foreach (string executing in new[] { "leased", "accepted", "running" })
                {
                    fixtureCommand["status"] = executing;
                    executingStatesAccepted &= NpcLivesFixtureAuthorized(fixtureRequest, fixtureRun, fixtureRuntime, fixtureArm);
                }
                Check("operator_setup_accepts_native_acknowledgment_with_same_live_lease", executingStatesAccepted);
                bool inactiveStatesRejected = true;
                foreach (string inactive in new[] { "queued", "completed", "failed", "cancelled", "interrupted", "needs_input", "" })
                {
                    fixtureCommand["status"] = inactive;
                    inactiveStatesRejected &= !NpcLivesFixtureAuthorized(fixtureRequest, fixtureRun, fixtureRuntime, fixtureArm);
                }
                fixtureCommand["status"] = "accepted";
                fixtureCommand["leaseExpiresUtc"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToString("o");
                inactiveStatesRejected &= !NpcLivesFixtureAuthorized(fixtureRequest, fixtureRun, fixtureRuntime, fixtureArm);
                fixtureCommand["leaseExpiresUtc"] = DateTimeOffset.UtcNow.AddMinutes(2).ToString("o");
                Check("operator_setup_rejects_inactive_commands_and_expired_acknowledged_lease", inactiveStatesRejected);
                bool allRejected = true;
                foreach (var change in new[] { TestDict("leaseId", "forged"), TestDict("activeSaveName", "Baseline"), TestDict("timelineId", "foreign"),
                    TestDict("gameInstanceId", "old"), TestDict("nativeSaveInProgress", true), TestDict("timeMode", "FastForward") })
                { var invalidFixture = CloneDictionary(fixtureRequest); foreach (var field in change) invalidFixture[field.Key] = field.Value;
                    allRejected &= !NpcLivesFixtureAuthorized(invalidFixture, fixtureRun, fixtureRuntime, fixtureArm); }
                var altered = CloneDictionary(fixtureRequest); var alteredCase = CloneDictionary(caseFields); alteredCase["chapterId"] = "forged"; altered["testCase"] = alteredCase;
                Check("operator_setup_rejects_forged_authority_changed_chapter_and_stale_runtime", allRejected
                    && !NpcLivesFixtureAuthorized(altered, fixtureRun, fixtureRuntime, fixtureArm)
                    && !NpcLivesFixtureAuthorized(fixtureRequest, fixtureRun, TestDict(), fixtureArm)
                    && !NpcLivesFixtureAuthorized(fixtureRequest, fixtureRun, fixtureRuntime, TestDict()));
                Dictionary<string, object> Member(string id, string party, bool present = true) => TestDict("heroId", id, "partyId", party, "inMainParty", present, "alive", true, "free", true);
                string announcementId = "announcement_" + Guid.NewGuid().ToString("N");
                var announcement = TestDict("operationId", announcementId, "text", "We leave at dawn and stop at the eastern gate.",
                    "partyId", "main", "playerName", "Player", "campaignId", campaign, "timelineId", "test", "worldDay", 10d,
                    "members", new[] { Member("companion", "main"), Member("guest", "main"), Member("town_notable", "other"), Member("absent", "main", false) });
                var announcementReceipt = NpcLivesAnnouncement(c, state, announcement, "player", options, 10);
                announcement["members"] = new[] { Member("late_recruit", "main") };
                NpcLivesAnnouncement(c, state, announcement, "player", options, 10);
                var a = state.Announcements.Single();
                Check("announcement_freezes_actual_roster_and_deduplicates", a.RecipientIds.SequenceEqual(new[] { "companion", "guest" })
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$id AND status='active';", TestDict("id", announcementId)).Count == 1);
                var originals = QuerySql(c, "SELECT text,participants_json FROM conversation_turns WHERE session_id=$id AND status='source_only';", TestDict("id", announcementId));
                var published = QuerySql(c, "SELECT payload_json FROM conversation_turns WHERE turn_id=$id;", TestDict("id", announcementId + "_source")).Single();
                Check("announcement_receipt_retains_exact_author_original_without_duplicate_visible_line", ReadBool(announcementReceipt, "persisted", false)
                    && originals.Count == 1 && ReadString(originals[0], "text", "") == a.Text
                    && ReadString(originals[0], "participants_json", "") == Json.Serialize(new[] { "player" })
                    && ReadString(TryParseJsonObject(ReadString(published, "payload_json", "{}")), "inputSourceId", "") == ReadString(announcementReceipt, "sourceId", "")
                    && QuerySql(c, "SELECT input_id FROM conversation_input_receipts WHERE session_id=$id;", TestDict("id", announcementId)).Count == 1);
                bool changedRejected = false;
                announcement["text"] = "Different words";
                try { NpcLivesAnnouncement(c, state, announcement, "player", options, 10); } catch (InvalidOperationException) { changedRejected = true; }
                Check("announcement_identity_cannot_change_words", changedRejected && a.Text.Contains("dawn"));
                Check("announcement_no_immediate_reaction_or_reward", a.Reactions.Count == 0 && state.Effects.Count == 0);
                Check("non_audience_prompt_cannot_see_announcement", !NpcLivesPromptBody(state, "town_notable", "player", TestDict(), options).Contains("eastern gate"));
                state.Character("companion").Facts.Add(new NpcLifeFact { Id = "rabbit", Kind = "pet", Text = "You keep a pet rabbit called Bramble with you." });
                Check("pet_is_protected_across_unrelated_places", new[] { "town_a", "castle_b", "" }.All(place =>
                    NpcLivesPromptBody(state, "companion", "player", TestDict("locationId", place), options).Contains("Bramble")));
                for (int i = 0; i < 100; i++) state.Character("companion").Facts.Add(new NpcLifeFact
                    { Id = "memory_" + i, Kind = "experience", Text = "Completed experience number " + i + "." });
                string compact = NpcLivesPromptBody(state, "companion", "player", TestDict(), options);
                Check("prompt_keeps_permanent_facts_and_bounded_recent_experiences", compact.Contains("Bramble")
                    && !compact.Contains("experience number 0.") && compact.Contains("experience number 99.")
                    && state.Character("companion").Facts.Count == 101);
                WriteNpcLives(c, state);
                string protectedLife = NpcLivesPrompt(campaign, "companion", TestDict("playerId", "player", "timelineId", "test", "worldDay", 10d));
                var lifePromptValues = new Dictionary<string, string> { ["playerText"] = "How are you?", ["priorDialogueText"] = "",
                    ["contextPullText"] = protectedLife + "\n\n" + RenderContinuityRecord("optional_old", "reported_exchange", "companion", "old", 1, 10, false, new string('x', 12000)) };
                var compactedLife = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", lifePromptValues,
                    "A quiet street.", "Speak only for yourself.", "", "Rules.", "Profile.", targetOverride: 9000,
                    capacity: TestDict("inputAllowance", 32000, "outputFits", true));
                var lifeEnvelope = CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", compactedLife.LiveTurn);
                string finalLifeMessages = string.Join("\n", lifeEnvelope.Messages.Select(m => ReadString(m, "content", "")));
                Check("provider_prompt_compaction_retains_personal_facts_and_announcement_review", finalLifeMessages.Contains("Bramble")
                    && finalLifeMessages.Contains("announcementReview") && finalLifeMessages.Contains("eastern gate")
                    && !finalLifeMessages.Contains(new string('x', 12000)));
                Check("personal_life_is_a_required_owner_only_prompt_record", ContinuityPromptRecords(protectedLife, false).Count == 1
                    && ContinuityPromptRecords(protectedLife, false).Single().Required
                    && NpcLivesPrompt(campaign, "town_notable", TestDict("playerId", "player", "timelineId", "test", "worldDay", 10d)).Length == 0);
                var opening = NpcEncounter(state, "player", "companion", TestDict());
                opening.BeginOpening("opening_fixture", 10, 1); opening.OpeningDay = 10; opening.OpeningGeneration = generation;
                opening.SessionId = "opening_session"; opening.OpeningPayload = Json.Serialize(TestDict("playerId", "player", "heroName", "Mira",
                    "campaignId", campaign, "timelineId", "test", "worldDay", 10d, "locationId", "town"));
                ExecuteSql(c, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('opening_session',$campaign,'companion','player','open',1,1,'[""player"",""companion""]','{""timelineId"":""test""}');", TestDict("campaign", campaign));
                CompleteNpcOpening(c, state, opening, TestDict(), TestDict(), generation);
                string fallback = opening.OpeningText;
                CompleteNpcOpening(c, state, opening, TestDict(), TestDict("reply", "A late conflicting reply", "activity", "arguing"), generation);
                Check("failed_opening_does_not_invent_or_accept_fallback", opening.OpeningText.Length == 0
                    && !opening.OpeningPending && !opening.LastAcceptedDay.HasValue
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id='opening_session';").Count == 0
                    && !ReadBool(NpcOpeningStatus(opening, generation), "ok", true));
                Check("failed_opening_does_not_claim_announcement_reaction", a.ReactionPending("companion"));
                foreach (bool foreignSessionTimeline in new[] { true, false })
                {
                    string fencedId = foreignSessionTimeline ? "foreign_timeline" : "changed_audience";
                    opening.BeginOpening(fencedId, 10, 1);
                    var fencedPayload = BuildNpcOpeningRequest(state, opening, TestDict(), "player", "companion", fencedId, 10);
                    ExecuteSql(c, "UPDATE conversation_sessions SET payload_json=$payload,participants_json=$audience WHERE session_id='opening_session';",
                        TestDict("payload", Json.Serialize(TestDict("timelineId", foreignSessionTimeline ? "foreign" : "test")),
                            "audience", Json.Serialize(foreignSessionTimeline ? new[] { "player", "companion" } : new[] { "player", "companion", "unexpected_listener" })));
                    CompleteNpcOpening(c, state, opening, TestDict(),
                        SealNpcApproachDraft(fencedPayload, TestDict("reply", "An unpublished fenced opening.", "activity", "sorting herbs")), generation);
                    Check("publication_rejects_actual_session_" + fencedId, !opening.OpeningPending && !opening.LastAcceptedDay.HasValue
                        && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id='opening_session';").Count == 0);
                }
                ExecuteSql(c, "UPDATE conversation_sessions SET payload_json=$payload,participants_json=$audience WHERE session_id='opening_session';",
                    TestDict("payload", Json.Serialize(TestDict("timelineId", "test")), "audience", Json.Serialize(new[] { "player", "companion" })));
                opening.BeginOpening("grounded_retry", 10, 1);
                var retryPayload = BuildNpcOpeningRequest(state, opening, TestDict(), "player", "companion", "grounded_retry", 10);
                var retryDraft = SealNpcApproachDraft(retryPayload, TestDict("reply", "A little company would be welcome.", "activity", "sorting herbs"));
                CompleteNpcOpening(c, state, opening, TestDict(), retryDraft, generation);
                Check("grounded_retry_publishes_once_without_player_speech", opening.OpeningText == "A little company would be welcome."
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id='opening_session';").Count == 1);
                Check("silent_resume_uses_elapsed_game_time", !opening.BeginOpening("too_soon", 10 + 59d / 1440, 1)
                    && opening.BeginOpening("due", 10 + 1d / 24, 1));
                opening.OpeningDay = 10.5;
                var request = BuildNpcOpeningRequest(state, opening,
                    TestDict("profile", TestDict("occupation", "a careful herbalist", "currentPartyId", "main",
                        "inventory", new string('x', 10000), "appearance", new string('y', 10000)),
                        "partyId", "main", "location", "the town apothecary",
                        "sceneContext", "Inside the town apothecary.", "npcEffectiveAffinity", 63),
                    "player", "companion", "due", 10.5);
                Check("opening_uses_full_native_context_and_server_owned_arrival",
                    IsNpcLifeApproach(request) && ReadString(request, "playerText", "missing") == ""
                    && ReadBool(request, "suppressPlayerTranscript", false)
                    && ReadString(ReadDictionary(request, "hero"), "inventory", "").Length == 10000
                    && ReadString(ReadDictionary(request, "hero"), "appearance", "").Length == 10000
                    && ReadString(request, "sceneContext", "").Contains("town apothecary")
                    && ReadString(request, "sceneContext", "").Contains("12.0")
                    && !request.ContainsKey("maxTokens") && !request.ContainsKey("singleProviderAttempt")
                    && !request.ContainsKey("reasoningDisabled"));
                var settledOpeningSource = TestDict("structuredActionContext", TestDict("playerId", "player", "npcId", "companion",
                    "fulfilledPaymentHistory", TestDict("schema", AgreementPaymentHistory.Schema, "source", "native_agreement_journal",
                        "total", 1, "truncated", false, "rows", new[] { TestDict("agreementId", "settled-ballot-fee", "clauseId", "fee",
                            "termsHash", new string('a', 64), "status", "applied", "fromHeroStringId", "player", "toHeroStringId", "companion",
                            "amount", 30000L, "quantityPerInstallment", 30000, "fulfilledInstallments", 1, "scheduledInstallments", 1,
                            "associatedActionIds", new[] { "government.support_vote" }) })));
                string settledOpeningBefore = Json.Serialize(settledOpeningSource);
                var settledOpening = BuildNpcOpeningRequest(state, opening, settledOpeningSource, "player", "companion", "settled-opening", 10.5);
                var settledFacts = VerifiedAgreementPaymentHistory(settledOpening, "companion", "player");
                string settledOpeningPrompt = BuildConversationDecisionPrompt(TestDict("fulfilledPaymentHistory", settledFacts));
                var settledOpeningResponseContract = TestDict();
                AttachStructuredActionResponseContract(settledOpeningResponseContract, settledOpening);
                Check("arrival_remembers_delivered_ballot_fee_without_new_bargain",
                    ReadDictionaryList(settledFacts, "rows").Count == 1
                    && settledOpeningPrompt.Contains("settled-ballot-fee") && settledOpeningPrompt.Contains("30000")
                    && settledOpeningPrompt.Contains("an earlier conditional offer cannot make a verified delivery still unpaid")
                    && !UsesStructuredActions(settledOpening) && StructuredActionPrompt(settledOpening) == ""
                    && settledOpeningResponseContract.Count == 0
                    && ReadDictionary(settledOpening, "structuredActionContext").Count == 3
                    && ReadString(settledOpening, "playerText", "missing") == ""
                    && Json.Serialize(settledOpeningSource) == settledOpeningBefore);
                Check("arrival_payment_history_remains_private_to_the_actual_pair",
                    VerifiedAgreementPaymentHistory(settledOpening, "other", "player").Count == 0
                    && VerifiedAgreementPaymentHistory(settledOpening, "companion", "other").Count == 0);
                var completedVoteSource = TestDict("channel", "in_person", "nativePoliticalContext",
                    TestDict("authoritative", true, "observedWorldDay", 200d, "observerGovernment",
                        TestDict("available", true, "authoritative", true, "knowledgeHolderHeroId", "companion",
                            "kingdomId", "empire", "governmentBusiness", new[] {
                                TestDict("businessId", "completed-mandate", "title", "Civic mandate", "status", "decided",
                                    "decisionDay", 110d, "resolvedDay", 110d,
                                    "winningOptionId", "grant", "executedOptionId", "grant",
                                    "ballots", new[] { TestDict("memberHeroId", "companion", "optionId", "grant") }) })));
                var completedVoteArrival = BuildNpcOpeningRequest(state, opening, completedVoteSource,
                    "player", "companion", "completed-vote-opening", 200d);
                string completedVotePrompt = BuildGovernmentPrivateConversationPrompt(completedVoteArrival);
                Check("arrival_preserves_native_closed_vote_evidence_without_new_consent",
                    IsNpcLifeApproach(completedVoteArrival) && completedVotePrompt.Contains("completed-mandate")
                    && completedVotePrompt.Contains("\"closed\":true") && completedVotePrompt.Contains("\"ownRecordedBallot\"")
                    && completedVotePrompt.Contains("\"resolvedDay\":110")
                    && !completedVotePrompt.Contains("PRIVATE GOVERNMENT BUSINESS")
                    && !UsesStructuredActions(completedVoteArrival)
                    && ReadString(completedVoteArrival, "playerText", "missing") == "");
                var reviewState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                for (int i = 0; i < 15; i++) reviewState.Announcements.Add(new NpcPartyAnnouncement {
                    Id = "batch_" + i, PlayerId = "player", Text = "Pending subject " + i + ".", WorldDay = 10,
                    RecipientIds = new List<string> { "companion", "guest" } });
                reviewState.Announcements.Add(new NpcPartyAnnouncement { Id = "private", PlayerId = "player", Text = "A private subject.", WorldDay = 10,
                    RecipientIds = new List<string> { "guest" } });
                string batchPrompt = NpcLivesPromptBody(reviewState, "companion", "player", TestDict("worldDay", 10d), options);
                Check("announcement_prompt_includes_entire_pending_batch_and_optional_silence", Enumerable.Range(0, 15).All(i => batchPrompt.Contains("Pending subject " + i + "."))
                    && !batchPrompt.Contains("A private subject") && batchPrompt.Contains("Consider ALL 15") && batchPrompt.Contains("no_comment"));
                PrepareNpcLivesStoryInvitation(reviewState, authored, 0, "companion", new List<NpcStoryLocation>(), "town", "prompt_fixture", 10);
                reviewState.Stories.Single().Status = "active";
                WriteNpcLives(c, reviewState);
                string protectedBatch = NpcLivesPrompt(campaign, "companion", TestDict("playerId", "player", "timelineId", "test", "worldDay", 10d));
                var batchValues = new Dictionary<string, string> { ["playerText"] = "Tell me more.", ["priorDialogueText"] = "",
                    ["contextPullText"] = protectedBatch + "\n\n" + RenderContinuityRecord("optional_batch", "reported_exchange", "companion", "old", 1, 10, false, new string('x', 12000)) };
                var compactedBatch = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", batchValues,
                    "A quiet street.", "Speak only for yourself.", "", "Rules.", "Profile.", targetOverride: 9000,
                    capacity: TestDict("inputAllowance", 32000, "outputFits", true));
                string finalBatchMessages = string.Join("\n", CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", compactedBatch.LiveTurn).Messages.Select(m => ReadString(m, "content", "")));
                Check("provider_compaction_keeps_entire_review_window_and_strict_active_story", Enumerable.Range(0, 15).All(i => finalBatchMessages.Contains("Pending subject " + i + "."))
                    && finalBatchMessages.Contains("npcStoryAssessments") && finalBatchMessages.Contains("no_comment")
                    && finalBatchMessages.Contains(authored.Phases[0].Prompt) && !finalBatchMessages.Contains("A private subject")
                    && !finalBatchMessages.Contains(new string('x', 12000)));
                WriteNpcLives(c, state);
                reviewState.Announcements.Add(new NpcPartyAnnouncement { Id = "late", PlayerId = "player", Text = "Sent while the reply was generating.", WorldDay = 10,
                    RecipientIds = new List<string> { "companion" } });
                var noComment = TestDict("announcementReview", TestDict("throughId", "batch_14", "disposition", "no_comment"));
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "empty", "", noComment, 10);
                var inventedQuote = TestDict("announcementReview", TestDict("throughId", "batch_14", "disposition", "commented", "evidenceQuote", "words never spoken"));
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "invalid", "I am busy sorting my pack.", inventedQuote, 10);
                Check("announcement_empty_or_unsupported_reply_cannot_consume_batch", reviewState.Announcements.All(n => n.Reactions.Count == 0));
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "quiet_turn", "*He keeps sorting his pack, with nothing to add.*", noComment, 10);
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "retry", "*He keeps sorting his pack.*", noComment, 10);
                var restoredReviews = Json.Deserialize<NpcLivesState>(Json.Serialize(reviewState));
                Check("announcement_quiet_review_is_durable_exactly_once_and_keeps_later_sources_pending", restoredReviews.Announcements.Take(15).All(n =>
                    !n.ReactionPending("companion") && n.Reactions["companion"] == "quiet_turn" && n.ReviewChoices["companion"] == "no_comment" && n.ReactionPending("guest"))
                    && restoredReviews.Announcements.Single(n => n.Id == "late").ReactionPending("companion")
                    && restoredReviews.Announcements.Single(n => n.Id == "private").Reactions.Count == 0);
                var comment = TestDict("announcementReview", TestDict("throughId", "batch_14", "disposition", "commented", "evidenceQuote", "Those plans suit me"));
                AcceptNpcAnnouncementReactions(reviewState, "guest", "player", "guest_turn", "Those plans suit me, though I shall keep an eye on the weather.", comment, 10);
                Check("announcement_each_hearer_chooses_independently", reviewState.Announcements.Take(15).All(n => n.ReviewChoices["guest"] == "commented"
                    && n.ReviewChoices["companion"] == "no_comment") && reviewState.Announcements.Single(n => n.Id == "private").ReactionPending("guest"));
                var unknown = TestDict("announcementReview", TestDict("throughId", "unknown", "disposition", "no_comment"));
                var foreign = TestDict("announcementReview", TestDict("throughId", "private", "disposition", "no_comment"));
                var future = TestDict("announcementReview", TestDict("throughId", "late", "disposition", "no_comment"));
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "unknown_turn", "*He continues working.*", unknown, 10);
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "foreign_turn", "*He continues working.*", foreign, 10);
                AcceptNpcAnnouncementReactions(reviewState, "companion", "other_player", "wrong_player", "*He continues working.*", future, 10);
                AcceptNpcAnnouncementReactions(reviewState, "companion", "player", "rewound_turn", "*He continues working.*", future, 9);
                Check("announcement_unknown_foreign_future_or_other_player_review_cannot_consume", reviewState.Announcements.Single(n => n.Id == "late").ReactionPending("companion")
                    && reviewState.Announcements.Single(n => n.Id == "private").Reactions.Count == 0);
                var capturedState = new NpcLivesState { CampaignId = campaign, TimelineId = "test" };
                for (int i = 0; i < 13; i++) capturedState.Announcements.Add(new NpcPartyAnnouncement {
                    Id = "captured_" + i, PlayerId = "player", WorldDay = 10, Text = "Captured subject " + i,
                    RecipientIds = new List<string> { "companion", "guest" } });
                var capturedPayload = TestDict("worldDay", 10d, "announcementReviewWindow",
                    TestDict("AnnouncementIds", new[] { "forged" }));
                var reviewedAll = TestDict("announcementReview", TestDict("reviewedAll", true, "throughId", "malformed_uuid",
                    "disposition", "no_comment", "evidenceQuote", ""));
                AcceptNpcAnnouncementReactions(capturedState, "companion", "player", "uncaptured", "*She checks her pack.*", reviewedAll, 10,
                    CapturedNpcAnnouncementReview(capturedPayload, "companion", "player"));
                Check("caller_supplied_review_window_cannot_authorize_acknowledgment", capturedState.Announcements.All(a => a.Reactions.Count == 0));
                string capturedPrompt = NpcLivesPromptBody(capturedState, "companion", "player", capturedPayload, options);
                var capturedWindow = CapturedNpcAnnouncementReview(capturedPayload, "companion", "player");
                var savedWindow = Json.Deserialize<NpcAnnouncementReviewWindow>(Json.Serialize(capturedWindow));
                Check("review_contract_uses_server_captured_ids_without_model_uuid_copying", capturedWindow.AnnouncementIds.Count == 13
                    && capturedPrompt.Contains("reviewedAll:true") && !capturedPrompt.Contains("throughId:")
                    && savedWindow.AnnouncementIds.SequenceEqual(capturedWindow.AnnouncementIds));
                capturedState.Announcements.Add(new NpcPartyAnnouncement { Id = "during_generation", PlayerId = "player", WorldDay = 10,
                    Text = "Arrived during generation", RecipientIds = new List<string> { "companion" } });
                capturedState.Announcements.Add(new NpcPartyAnnouncement { Id = "other_audience", PlayerId = "player", WorldDay = 10,
                    Text = "Never heard", RecipientIds = new List<string> { "guest" } });
                AcceptNpcAnnouncementReactions(capturedState, "companion", "player", "empty_captured", "", reviewedAll, 10, savedWindow);
                AcceptNpcAnnouncementReactions(capturedState, "guest", "player", "foreign_captured", "*She checks her pack.*", reviewedAll, 10, savedWindow);
                AcceptNpcAnnouncementReactions(capturedState, "companion", "player", "rewound_captured", "*She checks her pack.*", reviewedAll, 9, savedWindow);
                var foreignTimeline = Json.Deserialize<NpcAnnouncementReviewWindow>(Json.Serialize(savedWindow)); foreignTimeline.TimelineId = "other";
                AcceptNpcAnnouncementReactions(capturedState, "companion", "player", "foreign_timeline", "*She checks her pack.*", reviewedAll, 10, foreignTimeline);
                Check("captured_review_rejects_empty_foreign_and_rewound_publication", capturedState.Announcements.All(a => a.Reactions.Count == 0));
                AcceptNpcAnnouncementReactions(capturedState, "companion", "player", "captured_quiet", "*She checks her pack.*", reviewedAll, 10, savedWindow);
                AcceptNpcAnnouncementReactions(capturedState, "companion", "player", "captured_retry", "*She checks her pack.*", reviewedAll, 10, savedWindow);
                var reloadedCaptured = Json.Deserialize<NpcLivesState>(Json.Serialize(capturedState));
                Check("captured_review_ignores_bad_uuid_and_leaves_inflight_sources_unread_across_reload",
                    reloadedCaptured.Announcements.Take(13).All(a => a.Reactions["companion"] == "captured_quiet"
                        && a.ReviewChoices["companion"] == "no_comment" && a.ReactionPending("guest"))
                    && reloadedCaptured.Announcements.Single(a => a.Id == "during_generation").ReactionPending("companion")
                    && reloadedCaptured.Announcements.Single(a => a.Id == "other_audience").Reactions.Count == 0);
                WriteNpcLives(c, state);
                var priorProvider = NpcOpeningTestProvider; int calls = 0;
                try
                {
                    string pendingJob = campaign + "|test|" + opening.Id;
                    opening.OpeningDeadlineUtcMs = 1; // An expired deadline from an older save is not authority to publish.
                    NpcOpeningJobs[pendingJob] = opening.OpeningOperationId + "|" + generation;
                    WriteNpcLives(c, state);
                    var poll = NpcLivesOpenApi("opening", TestDict("campaignId", campaign, "timelineId", "test", "playerId", "player",
                        "heroId", "companion", "operationId", opening.OpeningOperationId, "generation", generation, "worldDay", 10.5d));
                    Check("expired_legacy_deadline_keeps_live_provider_pending", ReadString(poll, "status", "") == "pending"
                        && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id='opening_session';").Count == 1);
                    var slowProvider = System.Diagnostics.Stopwatch.StartNew();
                    NpcOpeningTestProvider = (_, _) => { calls++; System.Threading.Thread.Sleep(3100); return TestDict("ok", true, "content",
                        Json.Serialize(TestDict("reply", "I am sorting herbs. A little company would be welcome.", "activity", "sorting herbs", "receptiveness", "receptive"))); };
                    GenerateNpcOpening(campaign, "test", "player", "companion", "due", generation, request,
                        campaign + "|test|" + opening.Id, opening.Id).GetAwaiter().GetResult();
                    slowProvider.Stop();
                    var slowResult = ReadNpcLives(c, campaign, "test").Encounters.Single(e => e.Id == opening.Id);
                    Check("slow_provider_reply_is_preserved_after_retired_cutoff", slowProvider.ElapsedMilliseconds >= 3000
                        && slowResult.OpeningText == "I am sorting herbs. A little company would be welcome." && !slowResult.OpeningPending,
                        TestDict("providerWaitMs", slowProvider.ElapsedMilliseconds, "retiredCutoffMs", 2500));
                }
                finally { NpcOpeningTestProvider = priorProvider; }
                state = ReadNpcLives(c, campaign, "test"); opening = state.Encounters.Single(e => e.Id == opening.Id);
                Check("mocked_fast_provider_commits_once_and_resume_remains_silent", calls == 1 && opening.Activity == "sorting herbs"
                    && !opening.OpeningPending && !opening.BeginOpening("reopen", 10.5, 1)
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id='opening_session';").Count == 2);
                var disabledRequest = TestDict("campaignId", campaign, "timelineId", "test", "playerId", "player",
                    "heroId", "switch_npc", "worldDay", 12d);
                int acceptedBefore = QuerySql(c, "SELECT turn_id FROM conversation_turns;").Count;
                var disabledOpen = NpcLivesOpenApi("open", disabledRequest, initialApproachOff);
                Check("disabled_initial_approach_starts_no_job_or_accepted_turn", ReadString(disabledOpen, "status", "") == "disabled"
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns;").Count == acceptedBefore
                    && !NpcOpeningJobs.ContainsKey(campaign + "|test|player:switch_npc")
                    && !ReadNpcLives(c, campaign, "test").Encounters.Any(e => e.HeroId == "switch_npc"));
                var toggled = NpcEncounter(state, "player", "switch_npc", TestDict());
                toggled.BeginOpening("before_switch_off", 12, 1); toggled.OpeningGeneration = generation;
                toggled.SessionId = "opening_session"; toggled.OpeningPayload = Json.Serialize(TestDict("playerId", "player"));
                string toggledJob = campaign + "|test|" + toggled.Id;
                NpcOpeningJobs[toggledJob] = toggled.OpeningOperationId + "|" + generation;
                state.Announcements.Add(new NpcPartyAnnouncement { Id = "unconsumed_at_switch_off", PlayerId = "player", WorldDay = 12,
                    Text = "We leave after breakfast.", RecipientIds = new List<string> { "switch_npc" } });
                WriteNpcLives(c, state);
                var disabledPoll = NpcLivesOpenApi("opening", disabledRequest, initialApproachOff);
                state = ReadNpcLives(c, campaign, "test"); toggled = state.Encounters.Single(e => e.HeroId == "switch_npc");
                CompleteNpcOpening(c, state, toggled, TestDict(), TestDict("reply", "A late greeting."), generation);
                Check("switch_off_poll_cancels_pending_draft_without_acceptance", ReadString(disabledPoll, "status", "") == "disabled"
                    && !toggled.OpeningPending && toggled.OpeningOperationId.Length == 0 && !toggled.LastAcceptedDay.HasValue
                    && !NpcOpeningJobs.ContainsKey(toggledJob) && QuerySql(c, "SELECT turn_id FROM conversation_turns;").Count == acceptedBefore);
                toggled.BeginOpening("inflight_before_switch_off", 12, 1); toggled.OpeningGeneration = generation;
                toggled.OpeningPayload = Json.Serialize(TestDict("playerId", "player"));
                NpcOpeningJobs[toggledJob] = toggled.OpeningOperationId + "|" + generation;
                CompleteNpcOpening(c, state, toggled, TestDict(), TestDict("reply", "An unpublished provider greeting."), generation, initialApproachOff);
                var restoredToggle = ReadNpcLives(c, campaign, "test");
                Check("switch_off_publication_does_not_consume_pending_reactions_or_clock", !toggled.OpeningPending
                    && !restoredToggle.Encounters.Single(e => e.HeroId == "switch_npc").LastAcceptedDay.HasValue
                    && QuerySql(c, "SELECT turn_id FROM conversation_turns;").Count == acceptedBefore
                    && restoredToggle.Announcements.Single(n => n.Id == "unconsumed_at_switch_off").ReactionPending("switch_npc")
                    && !NpcOpeningJobs.ContainsKey(toggledJob)
                    && NpcLivesPromptBody(restoredToggle, "switch_npc", "player", TestDict("worldDay", 12d), initialApproachOff).Contains("We leave after breakfast."));
                var privateVisit = TestDict("npcLifeContext", "home:town", "npcLifeAudienceIds", new[] { "player", "companion" });
                var publicVisit = TestDict("npcLifeContext", "party", "npcLifeAudienceIds", new[] { "guest", "player", "companion" });
                var reordered = TestDict("npcLifeContext", "party", "npcLifeAudienceIds", new[] { "companion", "player", "guest" });
                Check("resumption_separates_privacy_and_canonicalizes_audience", NpcLifeContextKey(privateVisit, "player", "companion") != NpcLifeContextKey(publicVisit, "player", "companion")
                    && NpcLifeContextKey(publicVisit, "player", "companion") == NpcLifeContextKey(reordered, "player", "companion"));
                var leave = new NpcPersonalLeave { Id = "leave", HeroId = "companion", ProposalId = "proposal", Purpose = "Visit my sister", Trust = 60, Honor = 70, Compassion = 90 };
                state.Leaves.Add(leave); WriteNpcLives(c, state);
                string words = "Please stay until the wounded have recovered.";
                var p = TestDict("campaignId", campaign, "timelineId", "test", "playerText", words, "worldDay", 10d);
                var assessment = TestDict("leaveId", "leave", "revision", 1, "reasonKind", "medical_need", "evidenceQuote", words, "compelling", true);
                var parsed = TestDict("npcLeaveAssessment", assessment, "reply", "I abandon the visit forever.");
                string reply = ReadString(parsed, "reply", "");
                ConstrainNpcLeaveReply(p, "companion", parsed, ref reply);
                bool persuaded = NpcLeaveNegotiation.Persuaded(leave, "medical_need", true, campaign + "|test");
                Check("contradictory_leave_reply_gets_bounded_matching_repair", reply.Contains(NpcLeaveNegotiation.Response(persuaded, 7))
                    && !reply.Contains("abandon") && ReadNpcLives(c, campaign, "test").Leaves.Single().Status == "discussing");
                var invalid = TestDict("npcLeaveAssessment", TestDict("leaveId", "leave", "revision", 0, "reasonKind", "medical_need", "evidenceQuote", words, "compelling", true));
                string staleReply = "I will stay.";
                ConstrainNpcLeaveReply(p, "companion", invalid, ref staleReply);
                Check("stale_argument_clarifies_without_state_change", !invalid.ContainsKey("npcLeaveAssessment") && staleReply.Contains("unresolved"));
                Check("catalogue_structural_validation", NpcStoryCatalog.All.Count == 240 && NpcStoryCatalog.Validate(NpcStoryCatalog.All).Count == 0
                    && NpcStoryCatalog.ValidateLinks(NpcStoryCatalog.All).Count == 0);
                foreach (int index in new[] { 0, 31, 65, 99, 130, 180 })
                {
                    var definition = NpcStoryCatalog.All.Where(d => d.Category == "brief").ElementAt(index);
                    var sample = new NpcStoryInstance { Id = "prompt_sample_" + index, HeroId = "sample_npc_" + index,
                        Status = "active", Definition = definition, LocationId = "town" };
                    state.Stories.Add(sample);
                    var phase = definition.Phases[0]; var payload = TestDict("worldDay", 12d, "locationId", "town", "npcStoryId", sample.Id);
                    string prompt = NpcLivesPromptBody(state, sample.HeroId, "player", payload, options);
                    bool promptMatches = prompt.Contains(phase.Prompt) && prompt.Contains("two different substantive")
                        && prompt.Contains("no required phrase") && prompt.Contains("Never assess native objectives")
                        && (!phase.Objectives.Any(o => !o.Native && o.CompletionOutcomes.Count > 0)
                            || prompt.Contains("copy that entire paragraph exactly into resolutionQuote"));
                    bool rejected = true;
                    foreach (var objective in phase.Objectives)
                    {
                        void Submit(string sourceId, string quote, string acceptedWords, int revision, string outcome = "progress", string speaker = "player")
                        {
                            const string conclusion = "I have reached my personal conclusion after our discussion.";
                            var unit = new AcceptedConversationUnit { Campaign = campaign, Hero = sample.HeroId, Player = "player", Payload = payload, Reply = conclusion,
                                Parsed = TestDict("npcStoryAssessments", new[] { TestDict("storyId", sample.Id, "revision", revision,
                                    "objectiveId", objective.Id, "outcome", outcome, "evidenceQuote", quote, "resolutionQuote", outcome == "completed" ? conclusion : "") }) };
                            AcceptNpcStoryAssessment(c, state, unit, new List<Dictionary<string, object>>
                                { TestDict("turn_id", sourceId, "speaker_id", speaker, "role", speaker == "player" ? "player" : "npc", "text", acceptedWords) });
                        }
                        string first = "For " + phase.Title + ", I would begin by asking what matters most to you.";
                        string second = "I see another possibility for " + phase.Title + "; let us compare it with your idea.";
                        int before = sample.Evidence.Count;
                        Submit("wrong_speaker", first, first, sample.Revision, speaker: "bystander");
                        Submit("invented_quote", first, "Unrelated words about the weather.", sample.Revision);
                        Submit("stale", first, first, sample.Revision - 1);
                        Submit("tangent", first, first, sample.Revision, "tangent");
                        rejected &= sample.Evidence.Count == before;
                        Submit(sample.Id + objective.Id + "_one", first, first, sample.Revision);
                        Submit(sample.Id + objective.Id + "_one", first, first, sample.Revision);
                        rejected &= sample.Evidence.Count == before + 1 && sample.Status == "active";
                        Submit(sample.Id + objective.Id + "_two", second, second, sample.Revision, "completed");
                    }
                    Check("sampled_prompt_and_accepted_evidence_" + index, promptMatches && rejected && sample.Status == "completed"
                        && state.Effects.Count(e => e.StoryId == sample.Id && e.Kind == "skill_xp") == 1,
                        TestDict("template", definition.Id, "title", phase.Title, "providerCalls", 0));
                }
                var duelDefinition = NpcStoryCatalog.All.First(d => d.Phases.Any(phase => phase.Objectives.Any(o => o.Kind == "duel")));
                int duelPhase = duelDefinition.Phases.FindIndex(phase => phase.Objectives.Any(o => o.Kind == "duel"));
                var nativeStory = new NpcStoryInstance { Id = "native_guard", HeroId = "companion", Status = "active",
                    Definition = duelDefinition, PhaseIndex = duelPhase, LocationId = "town", NativeEncounterId = "mission" };
                state.Stories.Add(nativeStory); int effectsBefore = state.Effects.Count; bool unsupportedResult = false;
                try { NpcLivesNativeReceipt(c, state, TestDict("kind", "duel", "id", nativeStory.Id, "heroId", "companion",
                    "revision", nativeStory.Revision, "objectiveId", duelDefinition.Phases[duelPhase].Objectives.First(o => o.Kind == "duel").Id,
                    "operationId", "unknown_result", "nativeReceipt", "unknown_result", "encounterId", "mission", "locationId", "town",
                    "outcome", "promised"), "player", 12); }
                catch (InvalidOperationException) { unsupportedResult = true; }
                Check("unknown_native_result_cannot_progress_or_reward", unsupportedResult && nativeStory.Evidence.Count == 0
                    && nativeStory.Status == "active" && state.Effects.Count == effectsBefore && !state.OperationReceipts.ContainsKey("unknown_result"));
                var broken = Json.Deserialize<NpcStoryDefinition>(Json.Serialize(NpcStoryCatalog.All[0]));
                broken.Phases[0].Objectives[0].RequiredOutcome = null;
                Check("editor_rejects_null_outcome_before_runtime", NpcStoryCatalog.Validate(new[] { broken }).Count > 0);
                var active = new NpcStoryInstance { Id = "frozen", HeroId = "companion", Definition = Json.Deserialize<NpcStoryDefinition>(Json.Serialize(NpcStoryCatalog.All[0])) };
                string oldPrompt = active.Definition.Phases[0].Prompt; broken.Phases[0].Prompt = "An edited future version";
                Check("active_definition_snapshot_is_independent", active.Definition.Phases[0].Prompt == oldPrompt);
                var meeting = new NpcSummonedMeeting { Id = "meeting", IssuerId = "leader", TargetId = "vassal", DestinationId = "town", Purpose = "Council", ProposalId = "original", StayDays = 3 };
                Dictionary<string, object> Receipt(NpcSummonedMeeting m, string hash = "accepted") => TestDict("meeting", Json.Deserialize<Dictionary<string, object>>(Json.Serialize(m)), "agreementHash", hash);
                AcceptNpcMeetingReceipt(state, Receipt(meeting), "meeting", "meeting_agreed", 10);
                var revision = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(meeting));
                revision.Revision = 2; revision.ProposalId = "changed"; revision.DestinationId = "field:100:200:land"; revision.StayDays = 5;
                bool unsignedRevision = false;
                try { AcceptNpcMeetingReceipt(state, Receipt(revision, ""), "meeting", "meeting_revised", 11); } catch (InvalidOperationException) { unsignedRevision = true; }
                AcceptNpcMeetingReceipt(state, Receipt(revision), "meeting", "meeting_revised", 11);
                Check("meeting_revision_needs_new_acceptance_and_preserves_wait", unsignedRevision && state.Meetings.Single().Revision == 2
                    && state.Meetings.Single().NeedsHold("leader", 10000) && !state.Meetings.Single().ReleaseDay.HasValue);
                bool staleMeeting = false;
                try { AcceptNpcMeetingReceipt(state, Receipt(meeting), "meeting", "meeting_arrived", 12); } catch (InvalidOperationException) { staleMeeting = true; }
                var cancelled = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(revision)); cancelled.Status = "cancelled"; cancelled.Interruption = "Mutual cancellation";
                AcceptNpcMeetingReceipt(state, Receipt(cancelled), "meeting", "meeting_cancelled", 12);
                Check("meeting_cancellation_releases_both_and_rejects_stale_arrival", staleMeeting && !state.Meetings.Single().NeedsHold("leader", 12)
                    && !state.Meetings.Single().NeedsHold("vassal", 12));
                var scheduledState = new NpcLivesState();
                var scheduled = new NpcSummonedMeeting { Id = "scheduled", IssuerId = "leader", TargetId = "vassal",
                    DestinationId = "town", Purpose = "Council", ProposalId = "scheduled_action", StayDays = 3, SoloAttendance = true };
                scheduled.ScheduleArrival(20, 4);
                var forged = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(scheduled)); forged.ScheduledArrivalDay = 21;
                bool rejectedDate = false;
                try { AcceptNpcMeetingReceipt(scheduledState, Receipt(forged), scheduled.Id, "meeting_agreed", 20); }
                catch (InvalidOperationException) { rejectedDate = true; }
                AcceptNpcMeetingReceipt(scheduledState, Receipt(scheduled), scheduled.Id, "meeting_agreed", 20);
                scheduledState = Json.Deserialize<NpcLivesState>(Json.Serialize(scheduledState));
                Check("solo_meeting_arrangement_survives_reload", scheduledState.Meetings.Single().SoloAttendance);
                var early = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(scheduled));
                early.ArrivalDay = 23; early.ReleaseDay = 26; early.Status = "meeting";
                bool rejectedEarly = false;
                try { AcceptNpcMeetingReceipt(scheduledState, Receipt(early), scheduled.Id, "meeting_arrived", 23); }
                catch (InvalidOperationException) { rejectedEarly = true; }
                Check("scheduled_meeting_rejects_forged_date_and_early_arrival_after_reload", rejectedDate && rejectedEarly
                    && scheduledState.Meetings.Single().ScheduledArrivalDay == 24 && !scheduledState.Meetings.Single().ArrivalDay.HasValue);
                var delayed = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(scheduled));
                delayed.Arrive("vassal", "town", 25, true);
                var alteredAttendance = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(delayed)); alteredAttendance.SoloAttendance = false;
                bool rejectedAttendance = false;
                try { AcceptNpcMeetingReceipt(scheduledState, Receipt(alteredAttendance), scheduled.Id, "meeting_arrived", 25); }
                catch (InvalidOperationException) { rejectedAttendance = true; }
                Check("meeting_arrival_cannot_replace_solo_attendance_terms", rejectedAttendance && scheduledState.Meetings.Single().SoloAttendance
                    && !scheduledState.Meetings.Single().ArrivalDay.HasValue);
                var alteredSchedule = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(delayed)); alteredSchedule.ArrivalDelayDays = 3;
                bool rejectedAlteration = false;
                try { AcceptNpcMeetingReceipt(scheduledState, Receipt(alteredSchedule), scheduled.Id, "meeting_arrived", 25); }
                catch (InvalidOperationException) { rejectedAlteration = true; }
                AcceptNpcMeetingReceipt(scheduledState, Receipt(delayed), scheduled.Id, "meeting_arrived", 25);
                bool rejectedShortStay = false;
                try { AcceptNpcMeetingReceipt(scheduledState, Receipt(delayed), scheduled.Id, "meeting_completed", 27); }
                catch (InvalidOperationException) { rejectedShortStay = true; }
                AcceptNpcMeetingReceipt(scheduledState, Receipt(delayed), scheduled.Id, "meeting_completed", 28);
                Check("scheduled_meeting_keeps_exact_eta_and_full_stay_from_actual_arrival", rejectedAlteration && rejectedShortStay
                    && scheduledState.Meetings.Single().ReleaseDay == 28 && scheduledState.Meetings.Single().Status == "completed"
                    && !scheduledState.Meetings.Single().NeedsHold("leader", 28) && !scheduledState.Meetings.Single().NeedsHold("vassal", 28));
                var rescheduledState = new NpcLivesState();
                AcceptNpcMeetingReceipt(rescheduledState, Receipt(scheduled), scheduled.Id, "meeting_agreed", 20);
                var fresh = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(scheduled));
                fresh.Revision++; fresh.ProposalId = "new_action"; fresh.DestinationId = "castle";
                fresh.ArrivalDelayDays = fresh.ScheduledArrivalDay = null; fresh.ScheduleArrival(21, 2);
                var switchedAttendance = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(fresh)); switchedAttendance.SoloAttendance = false;
                bool rejectedRevisionAttendance = false;
                try { AcceptNpcMeetingReceipt(rescheduledState, Receipt(switchedAttendance), fresh.Id, "meeting_revised", 21); }
                catch (InvalidOperationException) { rejectedRevisionAttendance = true; }
                Check("meeting_revision_preserves_attendance_arrangement", rejectedRevisionAttendance && rescheduledState.Meetings.Single().SoloAttendance
                    && rescheduledState.Meetings.Single().Revision == 1);
                AcceptNpcMeetingReceipt(rescheduledState, Receipt(fresh), fresh.Id, "meeting_revised", 21);
                fresh.Status = "cancelled"; fresh.Interruption = "Mutual cancellation before the scheduled date";
                AcceptNpcMeetingReceipt(rescheduledState, Receipt(fresh), fresh.Id, "meeting_cancelled", 22);
                bool rejectedCancelledArrival = false;
                fresh.Status = "agreed"; fresh.Arrive("vassal", "castle", 23, true);
                try { AcceptNpcMeetingReceipt(rescheduledState, Receipt(fresh), fresh.Id, "meeting_arrived", 23); }
                catch (InvalidOperationException) { rejectedCancelledArrival = true; }
                Check("scheduled_revision_needs_fresh_terms_and_cancel_prevents_arrival", rejectedCancelledArrival
                    && rescheduledState.Meetings.Single().ScheduledArrivalDay == 23 && rescheduledState.Meetings.Single().Status == "cancelled");
                var extensionState = new NpcLivesState();
                var extensionBase = new NpcSummonedMeeting { Id = "extension", IssuerId = "leader", TargetId = "vassal",
                    DestinationId = "town", Purpose = "Survey", ProposalId = "survey", StayDays = 3, SoloAttendance = true };
                extensionBase.ScheduleArrival(20, 4);
                AcceptNpcMeetingReceipt(extensionState, Receipt(extensionBase), extensionBase.Id, "meeting_agreed", 20);
                var extensionArrival = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(extensionBase));
                extensionArrival.Arrive("vassal", "town", 25, true);
                AcceptNpcMeetingReceipt(extensionState, Receipt(extensionArrival), extensionBase.Id, "meeting_arrived", 25);
                var extension = extensionArrival.Revise("survey_extension", "town", 5, 26, 0, true);
                foreach (string extensionMutation in new[] { "arrival", "release", "place", "schedule", "delivery", "unsigned", "stale" })
                {
                    var bad = Json.Deserialize<NpcSummonedMeeting>(Json.Serialize(extension));
                    if (extensionMutation == "arrival") bad.ArrivalDay = 26;
                    if (extensionMutation == "release") bad.ReleaseDay = 31;
                    if (extensionMutation == "place") bad.DestinationId = "castle";
                    if (extensionMutation == "schedule") bad.ScheduledArrivalDay = 25;
                    if (extensionMutation == "delivery") bad.DeliveryDay = 26;
                    if (extensionMutation == "stale") bad.Revision--;
                    bool refused = false;
                    try { AcceptNpcMeetingReceipt(extensionState, extensionMutation == "unsigned" ? Receipt(bad, "") : Receipt(bad),
                        bad.Id, "meeting_revised", 26); }
                    catch (InvalidOperationException) { refused = true; }
                    Check("meeting_extension_rejects_" + extensionMutation, refused && extensionState.Meetings.Single().Revision == 1
                        && extensionState.Meetings.Single().ArrivalDay == 25 && extensionState.Meetings.Single().ReleaseDay == 28);
                }
                AcceptNpcMeetingReceipt(extensionState, Receipt(extension), extension.Id, "meeting_revised", 26);
                extensionState = Json.Deserialize<NpcLivesState>(Json.Serialize(extensionState));
                Check("meeting_extension_preserves_arrival_and_holds_after_reload", extensionState.Meetings.Single().Status == "meeting"
                    && extensionState.Meetings.Single().Revision == 2 && extensionState.Meetings.Single().ArrivalDay == 25
                    && extensionState.Meetings.Single().ReleaseDay == 30 && extensionState.Meetings.Single().ScheduledArrivalDay == 24
                    && extensionState.Meetings.Single().NeedsHold("leader", 29) && extensionState.Meetings.Single().NeedsHold("vassal", 29));
                bool earlyExtensionEnd = false;
                try { AcceptNpcMeetingReceipt(extensionState, Receipt(extension), extension.Id, "meeting_completed", 29); }
                catch (InvalidOperationException) { earlyExtensionEnd = true; }
                AcceptNpcMeetingReceipt(extensionState, Receipt(extension), extension.Id, "meeting_completed", 30);
                Check("meeting_extension_releases_at_original_arrival_plus_new_stay", earlyExtensionEnd
                    && extensionState.Meetings.Single().Status == "completed" && !extensionState.Meetings.Single().NeedsHold("leader", 30)
                    && !extensionState.Meetings.Single().NeedsHold("vassal", 30));
            }
            catch (Exception ex)
            {
                Check("fixture_completed", false, LimitText(ex.ToString(), 3000));
                results.Last()["summary"] = "fixture_completed: " + LimitText(ex.Message, 600);
            }
            finally
            {
                try { WriteJsonObject(Path.Combine(VerificationDir, "npc-lives-self-tests.json"), TestDict(
                    "schema", "reign-npc-lives-offline-evidence-v1", "passed", results.All(CheckPassed), "checks", results)); }
                finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunNpcGroupApproachSessionSelfTests(string campaign)
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed) => results.Add(TestDict("caseId", "npc_lives_group_approach_" + id,
                "passed", passed, "summary", id));
            const string timeline = "group_approach_branch";
            using var c = OpenCampaignConnection(campaign);
            var options = new NpcLivesOptions();
            foreach (string context in new[] { "party", "home:town", "castle:hall" })
            {
                string prefix = context.Replace(':', '_'), session = "group_session_" + prefix;
                string[] npcs = { prefix + "_ira", prefix + "_rhagaea" };
                var audience = npcs.Concat(new[] { "player" }).ToArray();
                var start = ConversationStartApi(TestDict("campaignId", campaign, "timelineId", timeline,
                    "sessionId", session, "npcId", npcs[0], "playerId", "player", "channel", "party_chat",
                    "worldDay", 10d, "participants", audience));
                var saved = QuerySql(c, "SELECT * FROM conversation_sessions WHERE session_id=$id;", TestDict("id", session)).Single();
                Check(prefix + "_session_retains_timeline_and_full_group", ReadBool(start, "ok", false)
                    && ReadString(TryParseJsonObject(ReadString(saved, "payload_json", "{}")), "timelineId", "") == timeline
                    && TextListFromJson(ReadString(saved, "participants_json", "[]")).OrderBy(id => id)
                        .SequenceEqual(audience.OrderBy(id => id)));
                foreach (string hero in npcs)
                {
                    var p = TestDict("campaignId", campaign, "timelineId", timeline, "playerId", "player",
                        "heroId", hero, "heroName", hero, "conversationSessionId", session, "worldDay", 10d,
                        "npcLifeContext", context, "npcLifeAudienceIds", audience.Reverse().ToArray());
                    var state = ReadNpcLives(c, campaign, timeline);
                    var encounter = NpcEncounter(state, "player", hero, p);
                    string operation = "group_opening_" + hero;
                    encounter.BeginOpening(operation, 10, 1);
                    encounter.SessionId = session; encounter.OpeningDay = 10; encounter.OpeningGeneration = NpcLivesGeneration(c);
                    encounter.OpeningPayload = Json.Serialize(p);
                    var request = BuildNpcOpeningRequest(state, encounter, p, "player", hero, operation, 10);
                    var draft = SealNpcApproachDraft(request, TestDict("reply", "Welcome, both of you. " + hero,
                        "activity", "waiting in the room", "receptiveness", "receptive"));
                    CompleteNpcOpening(c, state, encounter, p, draft, encounter.OpeningGeneration, options);
                    CompleteNpcOpening(c, state, encounter, p, draft, encounter.OpeningGeneration, options);
                    var accepted = QuerySql(c, "SELECT * FROM conversation_turns WHERE session_id=$session AND speaker_id=$hero;",
                        TestDict("session", session, "hero", hero));
                    Check(prefix + "_" + hero + "_publishes_once_to_whole_group", !encounter.OpeningPending
                        && encounter.LastAcceptedDay == 10 && accepted.Count == 1
                        && TextListFromJson(ReadString(accepted.Single(), "participants_json", "[]")).OrderBy(id => id)
                            .SequenceEqual(audience.OrderBy(id => id)));
                    var resume = NpcLivesOpenApi("open", p, options);
                    Check(prefix + "_" + hero + "_reopens_silently", ReadBool(resume, "ok", false)
                        && ReadString(resume, "status", "") == "resume");
                }
                Check(prefix + "_greetings_do_not_invent_player_speech", QuerySql(c,
                    "SELECT turn_id FROM conversation_turns WHERE session_id=$id AND role='player';", TestDict("id", session)).Count == 0);
                foreach (string mismatch in new[] { "timeline", "player", "missing_listener", "extra_listener", "missing_timeline" })
                {
                    var p = TestDict("campaignId", campaign, "timelineId", mismatch == "timeline" ? "other_branch" : timeline,
                        "playerId", mismatch == "player" ? "other_player" : "player", "heroId", npcs[0],
                        "conversationSessionId", session, "worldDay", 11d, "npcLifeContext", context,
                        "npcLifeAudienceIds", mismatch == "missing_listener" ? new[] { "player", npcs[0] }
                            : mismatch == "extra_listener" ? audience.Concat(new[] { "unexpected_listener" }).ToArray() : audience);
                    if (mismatch == "missing_timeline")
                        ExecuteSql(c, "UPDATE conversation_sessions SET payload_json='{}' WHERE session_id=$id;", TestDict("id", session));
                    var rejected = NpcLivesOpenApi("open", p, options);
                    Check(prefix + "_rejects_" + mismatch + "_before_generation", !ReadBool(rejected, "ok", true)
                        && ReadString(rejected, "error", "").Contains("audience or timeline")
                        && !NpcOpeningJobs.Keys.Any(key => key.StartsWith(campaign + "|", StringComparison.Ordinal))
                        && QuerySql(c, "SELECT turn_id FROM conversation_turns WHERE session_id=$id;", TestDict("id", session)).Count == 2);
                    if (mismatch == "missing_timeline")
                        ExecuteSql(c, "UPDATE conversation_sessions SET payload_json=$payload WHERE session_id=$id;",
                            TestDict("id", session, "payload", ReadString(saved, "payload_json", "{}")));
                }
            }
            return results;
        }

        private static List<Dictionary<string, object>> RunNpcStoryInvitationSelfTests(string campaign, ReignDbConnection c)
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict("caseId", "npc_lives_" + id, "passed", passed, "summary", id, "data", data));
            var definition = NpcStoryCatalog.All.Single(d => d.Phases[0].Title == "Words from another life");
            var state = Json.Deserialize<NpcLivesState>(Json.Serialize(new NpcLivesState { CampaignId = campaign, TimelineId = "test",
                Stories = new List<NpcStoryInstance> { new NpcStoryInstance { Id = "pending_music", HeroId = "corein", Definition = definition,
                    Status = "invited", LocationId = "town_V8", LocationName = "Ostican", ProposalId = "music_offer" } } }));
            var story = state.Stories.Single();
            string before = Json.Serialize(state);
            const string priorPlayer = "Sometimes home isnt a place either, sometimes its the right people.";
            const string priorNpc = "If home is the right people, I'll stop asking the song where it lives. I'll just listen to it with you.";
            string previousConversation = RenderContinuityRecord("previous_music_exchange", "reported_exchange", "corein", "test", 9, 121, true,
                "Earlier accepted conversation in Ostican. Player: " + priorPlayer + "\nCorein: " + priorNpc);
            foreach (bool eventMode in new[] { false, true })
            foreach (bool reviewingOffer in new[] { false, true })
            {
                string playerText = reviewingOffer ? "Tell me about that invitation." : "I didnt know that you had a green thumb Corein.";
                var payload = TestDict("playerId", "player", "timelineId", "test", "conversationSessionId", "later_camp_session", "worldDay", 10d);
                if (reviewingOffer) payload["npcStoryId"] = story.Id;
                string body = NpcLivesPromptBody(state, "corein", "player", payload, new NpcLivesOptions());
                string personalLife = RenderContinuityRecord("pending_music_context", "npc_personal_life", "corein", "test", 10, 140, true, body);
                var values = new Dictionary<string, string> { ["playerText"] = playerText, ["priorDialogueText"] = "",
                    ["contextPullText"] = previousConversation + "\n" + personalLife + "\n"
                        + RenderContinuityRecord("optional_music_noise", "reported_exchange", "corein", "old", 1, 10, false, new string('x', 12000)) };
                var budget = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", values,
                    "A campfire in the wilderness.", "Speak only for yourself.", "", "Rules.", "Profile.", targetOverride: 9000,
                    capacity: TestDict("inputAllowance", 32000, "outputFits", true));
                var envelope = CreatePromptEnvelope(eventMode ? "social_event" : "dialogue", "test", "Rules.", "Profile.", budget.Structure.WithoutMarkers(budget.LiveTurn));
                AttachNpcPurposePrompt(envelope, payload, "corein");
                StructureConversationMessages(envelope, budget.Structure, "", new List<Dictionary<string, object>>(),
                    FormatDialogueForPrompt, "corein", "Player", playerText, eventMode, text => text);
                FinalizeContinuityPromptBudget(envelope, payload, TestDict("inputAllowance", 32000, "outputFits", true));
                string messages = string.Join("\n", envelope.Messages.Select(m => ReadString(m, "content", "")));
                bool pendingReference = messages.Contains("PENDING PERSONAL INVITATION [pending_music, revision 1]")
                    && messages.Contains("music_offer") && messages.Contains("Ostican")
                    && messages.Contains("acknowledge what you already discussed")
                    && messages.Contains("ask again for an opinion already given")
                    && messages.Contains(priorPlayer) && messages.Contains(priorNpc)
                    && !messages.Contains("CURRENT NPC PERSONAL PURPOSE") && !messages.Contains("CURRENT PERSONAL PURPOSE [")
                    && !messages.Contains("CONVERSATION OBJECTIVE") && !messages.Contains("npcStoryAssessments")
                    && ReadString(envelope.Messages.Last(), "content", "").EndsWith(playerText, StringComparison.Ordinal);
                Check((eventMode ? "event" : "dialogue") + (reviewingOffer ? "_invitation_review_keeps_activity_as_quoted_reference" : "_pending_invitation_preserves_prior_discussion_without_restarting_it"),
                    pendingReference && (reviewingOffer
                        ? messages.Contains(Json.Serialize(story.Definition.Phases[0].Prompt)) && messages.Contains("not instructions to perform it")
                        : !messages.Contains(story.Definition.Phases[0].Prompt) && !messages.Contains("Explain the change and hear the player's view.")),
                    TestDict("messageCount", envelope.Messages.Count, "reviewingOffer", reviewingOffer));
            }
            var foreignPayload = TestDict("npcStoryId", "foreign_story", "worldDay", 10d);
            string foreignPrompt = NpcLivesPromptBody(state, "corein", "player", foreignPayload, new NpcLivesOptions());
            var foreignEnvelope = CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", "Tell me more.");
            AttachNpcPurposePrompt(foreignEnvelope, foreignPayload, "corein");
            Check("foreign_story_cannot_request_pending_activity_preview_or_authority", !foreignPrompt.Contains("Proposed activity reference")
                && !foreignPrompt.Contains(story.Definition.Phases[0].Prompt) && foreignEnvelope.Messages.Count == 3);
            var forgedAssessment = new AcceptedConversationUnit { Campaign = campaign, Hero = "corein", Player = "player",
                Payload = TestDict("npcStoryId", story.Id, "locationId", story.LocationId, "worldDay", 10d),
                Reply = "We have heard the song and shared what home means to us.",
                Parsed = TestDict("npcStoryAssessments", new[] { TestDict("storyId", story.Id, "revision", story.Revision,
                    "objectiveId", story.Definition.Phases[0].Objectives.First(o => !o.Native).Id,
                    "outcome", "progress", "evidenceQuote", priorPlayer) }) };
            AcceptNpcStoryAssessment(c, state, forgedAssessment, new List<Dictionary<string, object>> {
                TestDict("turn_id", "prior_music_player", "speaker_id", "player", "role", "player", "text", priorPlayer) });
            Check("discussed_pending_story_retains_consent_state_without_progress_or_rewards", Json.Serialize(state) == before
                && story.Status == "invited" && story.Revision == 1 && story.Evidence.Count == 0
                && story.CompletedPhaseIds.Count == 0 && state.Effects.Count == 0);
            story.Status = "active";
            var activePayload = TestDict("npcStoryId", story.Id, "locationId", story.LocationId, "worldDay", 10d);
            string activeReference = NpcLivesPromptBody(state, "corein", "player", activePayload, new NpcLivesOptions());
            var activeEnvelope = CreatePromptEnvelope("dialogue", "test", "Rules.", "Profile.", "Let us discuss the song.");
            AttachNpcPurposePrompt(activeEnvelope, activePayload, "corein");
            Check("accepted_story_keeps_authored_activity_and_scoped_system_purpose", activeReference.Contains(story.Definition.Phases[0].Prompt)
                && activeReference.Contains("npcStoryAssessments") && activeEnvelope.Messages.Any(m => ReadString(m, "role", "") == "system"
                    && ReadString(m, "content", "").StartsWith("CURRENT NPC PERSONAL PURPOSE", StringComparison.Ordinal)
                    && ReadString(m, "content", "").Contains(story.Definition.Phases[0].Prompt)));
            return results;
        }
    }
}
