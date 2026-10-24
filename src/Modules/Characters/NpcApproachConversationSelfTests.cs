using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunNpcApproachConversationSelfTests(string campaign)
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) =>
                results.Add(TestDict("caseId", "npc_approach_" + id, "passed", pass, "summary", id, "data", data));
            var state = new NpcLivesState { CampaignId = campaign, TimelineId = "approach_test" };
            var encounter = new NpcConversationEncounter { HeroId = "ingunde_fixture", SessionId = "arrival_session",
                OpeningGeneration = "generation_fixture", OpeningDay = 20.75 };
            encounter.BeginOpening("arrival_fixture", encounter.OpeningDay, 1);
            var profile = TestDict("heroStringId", encounter.HeroId, "name", "Ingunde", "isLord", true,
                "isFemale", true, "age", 21, "occupation", "Lord", "relationToPlayer", 63,
                "cultureId", "vlandia", "governorSettlementName", "Ostican", "mainHeroStringId", "player");
            var source = TestDict("profile", profile, "npcEffectiveAffinity", 63, "playerName", "Michael",
                "playerText", "FORGED_PLAYER_SPEECH", "sceneContext", "Ingunde's office in Ostican, beside her desk.",
                "npcLifeContext", "in_person", "npcLifeAudienceIds", new[] { "player", encounter.HeroId },
                "playerPartyId", "main_party", "partyContext", TestDict("schema", "reign-party-context-v1",
                    "partyId", "main_party", "worldDay", encounter.OpeningDay, "complete", true, "members", new object[0]));
            var p = CreateNpcApproachPayload(state, encounter, source, "player", encounter.HeroId, encounter.OpeningOperationId, encounter.OpeningDay);
            Check("arrival_has_no_invented_player_contribution", ReadString(p, "playerText", "missing") == ""
                && ReadBool(p, "suppressPlayerTranscript", false) && IsNpcLifeApproach(p)
                && ReadString(source, "playerText", "") == "FORGED_PLAYER_SPEECH");
            Check("serialized_flags_cannot_grant_draft_authority", !IsNpcLifeApproach(TryParseJsonObject(Json.Serialize(p))));
            var admission = System.Threading.Tasks.Task.Run(() => {
                int before = System.Threading.Volatile.Read(ref InteractiveRequestCount);
                var result = RunNpcApproachDialogue(p); // Its fixture generation is deliberately obsolete.
                return TestDict("status", ReadString(result, "status", ""), "ok", ReadBool(result, "ok", true),
                    "leaseReleased", !CampaignDataGate.IsReadLockHeld && CurrentCampaignRequestLease == null,
                    "noPreparationStarted", before == System.Threading.Volatile.Read(ref InteractiveRequestCount));
            }).GetAwaiter().GetResult();
            Check("obsolete_generation_stops_before_character_preparation_and_releases_lease",
                !ReadBool(admission, "ok", true) && ReadString(admission, "status", "") == "superseded"
                && ReadBool(admission, "leaseReleased", false) && ReadBool(admission, "noPreparationStarted", false));

            Dictionary<string, object> validAdmission;
            using (var admissionConnection = OpenCampaignConnection(campaign))
            {
                var admissionState = new NpcLivesState { CampaignId = campaign, TimelineId = "approach_admission" };
                var admissionEncounter = new NpcConversationEncounter { Id = "admission", HeroId = encounter.HeroId,
                    SessionId = "admission_session", OpeningGeneration = NpcLivesGeneration(admissionConnection), OpeningDay = 20.75 };
                admissionEncounter.BeginOpening("admission_fixture", 20.75, 1); admissionState.Encounters.Add(admissionEncounter);
                WriteNpcLives(admissionConnection, admissionState);
                validAdmission = CreateNpcApproachPayload(admissionState, admissionEncounter, source, "player", encounter.HeroId,
                    "admission_fixture", 20.75);
            }
            var admitted = System.Threading.Tasks.Task.Run(() => {
                var result = RunNpcApproachDialogue(validAdmission, _ => TestDict("ok", true,
                    "held", CampaignDataGate.IsReadLockHeld && CurrentCampaignRequestLease != null));
                bool successReleased = !CampaignDataGate.IsReadLockHeld && CurrentCampaignRequestLease == null;
                bool threw = false;
                try { RunNpcApproachDialogue(validAdmission, _ => throw new InvalidOperationException("fixture preparation failure")); }
                catch (InvalidOperationException) { threw = true; }
                return TestDict("held", ReadBool(result, "held", false), "successReleased", successReleased,
                    "exceptionReleased", threw && !CampaignDataGate.IsReadLockHeld && CurrentCampaignRequestLease == null);
            }).GetAwaiter().GetResult();
            Check("current_preparation_has_campaign_admission_and_releases_on_success_and_exception",
                ReadBool(admitted, "held", false) && ReadBool(admitted, "successReleased", false) && ReadBool(admitted, "exceptionReleased", false));

            var characteristics = PromptParityCharacteristics();
            characteristics["traits"] = TestDict("basePersonalitySummary", "Patient, compassionate and careful with difficult truths.");
            characteristics["background"] = TestDict("summary", "Her responsibility for Ostican weighs heavily on her.");
            characteristics["voice"] = TestDict("speechStyle", "Direct, familiar with trusted friends.");
            var live = TestDict("mood", "worried about the town's stores", "currentPlan", "await Michael's return with supplies or news", "currentCrisis", "uncertain supplies");
            var identity = PromptParityIdentity(); identity["usableName"] = "Michael";
            const string expected = "Come back with wagons or come back with word. A rider ahead of the wagons.";
            const string letter = "I sent a letter asking for news; delivery and reading remain unknown.";
            const string revised = "The old exact arrival date was withdrawn. No new date is fixed.";
            string memory = RenderContinuityRecord("arrival_commitment", "obligation", encounter.HeroId, state.TimelineId, 19, 120, true, expected)
                + "\n" + RenderContinuityRecord("arrival_letter", "letter", encounter.HeroId, state.TimelineId, 20, 120, true, letter)
                + "\n" + RenderContinuityRecord("arrival_revision", "correction", encounter.HeroId, state.TimelineId, 20, 120, true, revised);
            var prior = new List<Dictionary<string, object>> {
                TestDict("turnId", "arrival_old_npc", "role", "npc", "speakerHeroStringId", encounter.HeroId, "speaker", "Ingunde",
                    "text", "Go safely, friend. I will set aside an evening when you return."),
                TestDict("turnId", "arrival_old_player", "role", "player", "speakerHeroStringId", "player", "speaker", "Michael",
                    "text", "I will be back. I do not know the date yet.")
            };
            var empty = TestDict(); var noLines = new List<Dictionary<string, object>>();
            PromptEnvelope Envelope(Dictionary<string, object> payload) => BuildDialoguePromptEnvelope(campaign,
                encounter.HeroId, "Ingunde", "Michael", "Michael", NpcLifeArrivalCue, ReadString(payload, "sceneContext", ""),
                profile, characteristics, live, empty, empty, prior, noLines, noLines, noLines, identity, payload, memory, "");
            var ordinary = DeepCloneProfileDictionary(p);
            var ordinaryEnvelope = Envelope(ordinary);
            var approachEnvelope = Envelope(p);
            string final = string.Join("\n", approachEnvelope.Messages.Select(m => ReadString(m, "content", "")));
            string normal = string.Join("\n", ordinaryEnvelope.Messages.Select(m => ReadString(m, "content", "")));
            Check("same_dialogue_foundation_state_history_and_required_sources", new[] {
                "Patient, compassionate", "responsibility for Ostican", "worried about the town",
                "set aside an evening", "I do not know the date yet", expected, letter, revised, "Ingunde's office"
            }.All(text => final.Contains(text) && normal.Contains(text)), TestDict("ordinaryCharacters", normal.Length, "approachCharacters", final.Length));
            Check("known_return_and_relationship_guide_first_reaction", final.Contains("NPC LIVES ARRIVAL")
                && final.Contains("63") && final.Contains("expected return") && final.Contains("Warmth and availability")
                && final.Contains("neutral score does not erase acquaintance") && final.Contains("Preserve uncertainty and audience privacy"));
            Check("old_minimal_prompt_and_tiny_output_budget_retired", !final.Contains("under 240 characters")
                && !final.Contains("activity already occupying you"));
            var request = BuildConversationGenerationRequest(TestDict("maxTokens", 900), approachEnvelope, campaign, "arrival_fixture", encounter.HeroId, "", false);
            ConfigureNpcApproachGeneration(request, p);
            Check("normal_schema_budget_and_repair_policy_remain", ReadInt(request, "maxTokens", 0) >= 8000
                && ReadString(request, "requestType", "") == "npc_approach"
                && !ReadBool(request, "singleProviderAttempt", false) && !ReadBool(request, "reasoningDisabled", false)
                && ReadString(request, "heroStringId", "") == encounter.HeroId && ReadString(request, "heroName", "") == "Ingunde");
            var wireSettings = TestDict("llmProvider", NanoGptProvider, "reasoningMode", "always", "reasoningEffort", "medium");
            request["model"] = "approach_fixture_model";
            var wire = ReadDictionary(CaptureMemoryReplayProviderRequest(request, wireSettings), "providerWireCapture");
            var wireBody = TryParseJsonObject(ReadString(wire, "requestJson", ""));
            string wireText = string.Join("\n", ReadDictionaryList(wireBody, "messages").Select(m => ReadString(m, "content", "")));
            Check("actual_provider_wire_retains_history_and_required_return_sources", ReadString(wire, "status", "") == "captured"
                && ReadBool(ReadDictionary(wire, "providerPreflight"), "fits", false) && ReadInt(wire, "providerCalls", -1) == 0
                && new[] { expected, letter, revised, "set aside an evening", "NPC LIVES ARRIVAL" }.All(wireText.Contains)
                && ReadInt(wireBody, "max_tokens", 0) >= 8000);
            Check("actual_provider_wire_keeps_complete_native_speaker_politics",
                wireText.Contains("REQUIRED PRIVATE POLITICAL BELIEFS")
                && Enum.GetValues(typeof(Reign.Core.Contracts.Government.ReignGovernmentPlank))
                    .Cast<Reign.Core.Contracts.Government.ReignGovernmentPlank>().All(value => wireText.Contains(value + ":")),
                "An opening carries its actual native speaker into political construction even before a stored profile exists.");
            var provider = BanterProviderSettings(TestDict("requestType", "npc_approach"), TestDict("reasoningMode", "always"));
            Check("approach_preserves_reasoning_preference", ReadString(provider, "reasoningMode", "") == "always"
                && ReadString(BanterProviderSettings(TestDict("requestType", "party_banter"), TestDict("reasoningMode", "always")), "reasoningMode", "") == "off");
            var repairProvider = BanterProviderSettings(TestDict("requestType", "npc_approach_continuity_repair"),
                TestDict("npcLivesApproachModel", "approach_fixture_model", "reasoningMode", "always"));
            Check("repairs_retain_configured_approach_model", ReadString(repairProvider, "dialogueModel", "") == "approach_fixture_model");

            foreach (var sample in new[] { ("in_person", 63), ("party", 0), ("home:town", -50), ("castle:hall", 25) })
            {
                var input = DeepCloneProfileDictionary(source); input["npcLifeContext"] = sample.Item1;
                input["npcLifeAudienceIds"] = new[] { "player", encounter.HeroId, "other_listener" };
                input["npcEffectiveAffinity"] = sample.Item2;
                var scoped = CreateNpcApproachPayload(state, encounter, input, "player", encounter.HeroId, encounter.OpeningOperationId, encounter.OpeningDay);
                string instructions = NpcApproachInstructions(scoped);
                Check("audience_and_affinity_" + sample.Item1.Replace(':', '_'), instructions.Contains(sample.Item1)
                    && instructions.Contains(sample.Item2.ToString()) && instructions.Contains("other_listener")
                    && instructions.Contains("protect private memories") && instructions.Contains("Do not force friendliness"));
            }

            var draft = SealNpcApproachDraft(p, TestDict("reply", "Michael—you are back. What news of the supplies?",
                "activity", "working at her desk", "receptiveness", "receptive"));
            var saved = TestDict("playerId", "player");
            Check("only_server_validated_matching_draft_can_publish", NpcApproachDraftMatches(draft, state, encounter, saved,
                encounter.OpeningGeneration, out _) && !NpcApproachDraftMatches(TryParseJsonObject(Json.Serialize(draft)),
                    state, encounter, saved, encounter.OpeningGeneration, out _));
            NpcApproachDraftMatches(draft, state, encounter, saved, encounter.OpeningGeneration, out var publication);
            p["sceneContext"] = "A later unrelated location.";
            Check("publication_retains_immutable_native_scene_without_player_speech",
                ReadString(publication.PublicationContext, "sceneContext", "").Contains("Ingunde's office")
                && ReadString(publication.PublicationContext, "playerText", "missing") == ""
                && ReadBool(publication.PublicationContext, "suppressPlayerTranscript", false)
                && ReadDictionary(publication.PublicationContext, "partyContext") != null);
            Check("generation_session_and_player_fences", !NpcApproachDraftMatches(draft, state, encounter, saved, "rewound", out _)
                && !NpcApproachDraftMatches(draft, state, encounter, TestDict("playerId", "foreign"), encounter.OpeningGeneration, out _));
            string session = encounter.SessionId; encounter.SessionId = "changed_audience_session";
            Check("changed_session_rejects_draft", !NpcApproachDraftMatches(draft, state, encounter, saved, encounter.OpeningGeneration, out _));
            encounter.SessionId = session;
            draft["reply"] = "You delivered every wagon already.";
            Check("modified_visible_reply_loses_publication_authority", !NpcApproachDraftMatches(draft, state, encounter, saved, encounter.OpeningGeneration, out _));

            var parsed = TestDict("reply", "Michael—you are back. What news of the supplies?", "activity", "working at her desk", "receptiveness", "receptive");
            var motive = TestDict();
            var accepted = BuildNpcApproachDraft(p, TestDict("ok", true), parsed, ReadString(parsed, "reply", ""), motive, identity, prior, encounter.HeroId, "Ingunde", "Michael");
            Check("grounded_draft_passes_shared_validation", ReadBool(accepted, "ok", false));
            foreach (var failed in new[] { TestDict("visibleRepairFailure", true), TestDict("conversationAgency", TestDict("visibleFailure", true)) })
                Check("unresolved_" + failed.Keys.First(), !ReadBool(BuildNpcApproachDraft(p, failed, parsed,
                    ReadString(parsed, "reply", ""), motive, identity, prior, encounter.HeroId, "Ingunde", "Michael"), "ok", true));
            Check("malformed_technical_or_missing_output_is_not_scene_history",
                !NpcApproachReplyShape(TestDict()) && !NpcApproachReplyShape(TestDict("reply", "[Bannerlord Reign: provider unavailable]"))
                && !ReadBool(BuildNpcApproachDraft(p, TestDict("ok", true), null, "", motive, identity, prior, encounter.HeroId, "Ingunde", "Michael"), "ok", true));
            Check("accurate_opening_is_not_clipped_at_240_characters", NpcApproachReplyShape(TestDict("reply", new string('a', 500))));
            return results;
        }
    }
}
