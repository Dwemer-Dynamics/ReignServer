using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunConversationRouteMemorySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object evidence = null) => results.Add(TestDict("caseId", "route_memory_" + id,
                "passed", passed, "summary", passed || evidence == null ? id : id + ": " + Json.Serialize(evidence)));
            string campaign = "route_memory_" + Guid.NewGuid().ToString("N");
            var settings = TestDict("highMemoryMode", false, "memoryPrecisionMode", "legacy", "enableSemanticMemory", false,
                "enableMinimeMemoryWorker", false, "enableMinimeMemoryReranking", false, "mainModel", "fixture-main");
            try
            {
                Dictionary<string, object> Payload(string turn) => TestDict("campaignId", campaign, "timelineId", "route_branch", "worldDay", 20d,
                    "playerHeroStringId", "player", "playerName", "Player", "turnId", turn, "sceneTurnId", turn,
                    "playerText", "Remember the old mill beside the orchard?", "participants", new[] { "npc", "player" },
                    "speaker", TestDict("heroStringId", "npc", "name", "Mira", "age", 32d));
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    ReadMemoryPrecisionState(connection);
                    DialogueSelfTestStore.InsertConversationSessions13(connection, TestDict("campaign", campaign));
                    InsertConversationTurn(connection, "mill_question", "old_mill", "mill_exchange", 0, "mill_exchange", "npc", "npc", "Mira",
                        "Which place would you choose beside the orchard?", "in_person", 10d, 1, "", new List<string> { "npc", "player" }, Payload("old"));
                    InsertConversationTurn(connection, "mill_answer", "old_mill", "mill_exchange", 1, "mill_exchange", "player", "player", "Player",
                        "The old mill beside the orchard is my choice.", "in_person", 10d, 2, "", new List<string> { "npc", "player" }, Payload("old"));
                    DialogueSelfTestSchema.CreateRouteAtomicFixture(connection);
                }
                string captured = ""; int calls = 0;
                Dictionary<string, object> Writer(Dictionary<string, object> input)
                {
                    calls++;
                    captured = string.Join("\n", ReadDictionaryList(input, "messages").Select(m => ReadString(m, "content", "")));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I remember your choice of the old mill.",
                        "emotion", "curious", "intent", "answer",
                        "memorySummary", "Invented permanent trait MUST-NOT-BE-STORED")));
                }
                int SourceCount(string channel)
                {
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReadInt(DialogueSelfTestStore.CountConversationTurns(connection, TestDict("channel", channel)).Single(), "n", 0);
                }
                var court = Payload("court_1");
                court["actorId"] = "npc"; court["matterId"] = "matter"; court["phase"] = "conversation";
                court["speakerName"] = "Mira";
                var courtReply = CourtService.CourtLifeRespond(court, settings, Writer);
                Check("court_receives_prior_answer_and_publishes_sources", ReadBool(courtReply, "ok", false) && calls == 1
                    && captured.Contains("The old mill beside the orchard is my choice.") && SourceCount("court_life") == 2);
                CourtService.CourtLifeRespond(court, settings, Writer);
                Check("court_replay_has_no_provider_or_source_duplicate", calls == 1 && SourceCount("court_life") == 2);

                var petition = Payload("petition_1");
                petition["petitionerHeroId"] = "npc"; petition["petitionerName"] = "Mira";
                petition["petitionId"] = "mill_petition"; petition["transcriptId"] = "petition_session"; petition["phase"] = "conversation";
                var petitionReply = CourtService.RulerPetitionRespond(petition, settings, Writer);
                Check("petition_receives_prior_answer_and_preserves_no_actions", ReadBool(petitionReply, "ok", false)
                    && captured.Contains("The old mill beside the orchard is my choice.") && SourceCount("ruler_petition") == 2
                    && ReadDictionaryList(petitionReply, "nativeActions").Count == 0);
                int afterPetition = calls;
                CourtService.RulerPetitionRespond(petition, settings, Writer);
                Check("petition_replay_does_not_regenerate", calls == afterPetition && SourceCount("ruler_petition") == 2);

                var council = Payload("council_1");
                council["role"] = "war"; council["councilSessionId"] = "council_session";
                council["domainPacket"] = TestDict("wars", new string[0], "treasury", "UNAUTHORIZED-TREASURY-901");
                council["presentAdvisorIds"] = new[] { "npc", "listener" };
                council["transcript"] = new[] { TestDict("role", "npc", "speaker", "Other", "speakerHeroStringId", "other",
                    "text", "*She secretly remembers PRIVATE-MARRIAGE-902.* The orchard is east.") };
                var councilReply = CourtService.RoyalCouncilRespond(council, settings, Writer);
                Check("council_uses_shared_recall_without_hidden_or_unauthorized_packet", ReadBool(councilReply, "ok", false)
                    && captured.Contains("The old mill beside the orchard is my choice.")
                    && !captured.Contains("PRIVATE-MARRIAGE-902") && !captured.Contains("UNAUTHORIZED-TREASURY-901")
                    && captured.Contains("current authoritative facts and terms remain controlling") && SourceCount("royal_council") == 2);

                var child = Payload("child_1");
                child["speaker"] = TestDict("heroStringId", "child", "name", "Pip", "age", 8d);
                child["participants"] = new[] { "child", "player" }; child["conversationSessionId"] = "family_session";
                var childReply = CourtService.FamilyChambersChildRespond(child, settings, Writer);
                Check("child_excludes_adult_lifetime_packet_and_uses_complete_sources", ReadBool(childReply, "ok", false)
                    && !captured.Contains("Which place would you choose beside the orchard?") && SourceCount("family_chambers_child") == 2);
                int afterChild = calls;
                var childReplay = CourtService.FamilyChambersChildRespond(child, settings, Writer);
                Check("child_replay_preserves_emotion_intent_without_provider", calls == afterChild
                    && ReadString(childReplay, "emotion", "") == "curious" && ReadString(childReplay, "intent", "") == "answer"
                    && ReadInt(childReplay, "providerCallCount", -1) == 0 && SourceCount("family_chambers_child") == 2);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string memory = ReadString(DialogueSelfTestStore.SelectChildhoodExperiences(connection).Single(), "summary", "");
                    Check("child_memory_is_actual_speech_not_model_invention", memory.Contains("Player said or visibly did:")
                        && memory.Contains("I remember your choice of the old mill.") && !memory.Contains("MUST-NOT-BE-STORED"));
                }
                var overlong = Payload("oversize"); overlong["petitionerHeroId"] = "npc"; overlong["petitionId"] = "oversize";
                overlong["transcriptId"] = "oversize_session";
                var refused = CourtService.RulerPetitionRespond(overlong, settings, _ => TestDict("ok", true, "content", Json.Serialize(TestDict("reply", new string('a', 601)))));
                Check("long_reply_is_rejected_whole_not_clipped", !ReadBool(refused, "ok", true) && SourceCount("ruler_petition") == 3);

                var context = PrepareRouteConversationMemory(Payload("fault"), "npc", "route_atomic", "fault_session", "fault_turn", new[] { "npc" }, settingsOverride: settings);
                var request = TestDict("campaignId", campaign, "requestType", "dialogue", "correlationId", "route_fault", "messages",
                    new List<Dictionary<string, object>> { TestDict("role", "user", "content", "Remember the old mill?") });
                var generated = GenerateRouteConversation(context, request, "No native effects.", 800, Writer);
                string accepted = ReadString(TryParseJsonObject(ReadString(generated, "content", "")), "reply", "");
                foreach (string phase in new[] { "after_sources", "after_route_write" })
                {
                    bool failed = false, invisible = false;
                    try
                    {
                        PublishRouteConversation(context, accepted, "Mira", connection => DialogueSelfTestStore.InsertRouteAtomicFixture(connection),
                            actual => { if (actual != phase) return; invisible = SourceCount("route_atomic") == 0; throw new InvalidOperationException("Injected route fault."); });
                    }
                    catch (InvalidOperationException) { failed = true; }
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    Check("source_and_route_rollback_" + phase, failed && invisible && SourceCount("route_atomic") == 0
                        && ReadInt(DialogueSelfTestStore.CountRouteAtomicFixture(connection).Single(), "n", 0) == 0
                        && ReadInt(DialogueSelfTestStore.CountConversationRouteAcceptance(connection, TestDict("unit", context.Unit)).Single(), "n", 0) == 0);
                }
                AdvanceMemoryRestoreGeneration(campaign);
                bool stale = false;
                try { PublishRouteConversation(context, accepted, "Mira", _ => { }); }
                catch (InvalidOperationException) { stale = true; }
                Check("pre_restore_route_cannot_publish", stale && SourceCount("route_atomic") == 0);
                Check("ordinary_routes_use_expected_writer_calls", calls == 5);
                var enabled = Program.CloneDictionary(settings);
                enabled["highMemoryMode"] = true; enabled["memoryCheckerModel"] = "obsolete-checker";
                enabled["memoryEvidenceModel"] = "obsolete-analyst"; enabled["memoryFinalCheckEnabled"] = true;
                var checkedPetition = Payload("retired_settings_petition");
                checkedPetition["petitionerHeroId"] = "npc"; checkedPetition["petitionerName"] = "Mira";
                checkedPetition["petitionId"] = "checked_mill"; checkedPetition["transcriptId"] = "checked_session";
                checkedPetition["phase"] = "conversation";
                var purposes = new List<string>();
                Dictionary<string, object> SingleWriter(Dictionary<string, object> input)
                {
                    purposes.Add(ReadString(input, "requestType", ""));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "The old mill remains your choice.")));
                }
                var acceptedSingle = CourtService.RulerPetitionRespond(checkedPetition, enabled, SingleWriter);
                Check("retired_memory_flags_cannot_add_route_calls", ReadBool(acceptedSingle, "ok", false)
                    && purposes.SequenceEqual(new[] { "ruler_petition_reaction" })
                    && ReadInt(acceptedSingle, "providerCallCount", 0) == 1 && !acceptedSingle.ContainsKey("memoryVerification"), acceptedSingle);
                var replaySingle = CourtService.RulerPetitionRespond(checkedPetition, enabled, SingleWriter);
                Check("route_receipt_replay_adds_no_calls", purposes.Count == 1 && ReadInt(replaySingle, "providerCallCount", -1) == 0
                    && ReadString(replaySingle, "reply", "") == ReadString(acceptedSingle, "reply", ""));
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    DialogueSelfTestStore.UpdateConversationRouteAcceptance(connection, TestDict("hash", PromptHash(ReadString(acceptedSingle, "reply", ""))));
                var corruptReplay = CourtService.RulerPetitionRespond(checkedPetition, enabled, SingleWriter);
                Check("mismatched_route_receipt_rejected_without_provider", !ReadBool(corruptReplay, "ok", true) && purposes.Count == 1);
                const string safeQuestion = "I remember your choice of the old mill. Which gate should we use?";
                var recoveryPayload = Payload("recovery_1");
                var recoveryContext = PrepareRouteConversationMemory(recoveryPayload, "npc", "recovery_route",
                    "recovery_session", "recovery_1", new[] { "npc" }, settingsOverride: settings);
                // Live providers return authored speech. Historical engine notices
                // are covered by the immutable-source recovery tests, and must not
                // be injected as provider output through the retired repair path.
                var recovered = GenerateRouteConversation(recoveryContext, Program.CloneDictionary(request), "No native effects.", 800,
                    _ => TestDict("ok", true, "content", Json.Serialize(TestDict("reply", safeQuestion))));
                string recoveredReply = ReadString(TryParseJsonObject(ReadString(recovered, "content", "")), "reply", "");
                Check("single_pass_keeps_usable_route_dialogue", ReadBool(recovered, "ok", false)
                    && ConversationSpeechForRecall(recoveredReply) == safeQuestion && recoveryContext.ProviderCalls == 1, recovered);
                if (ReadBool(recovered, "ok", false))
                    PublishRouteConversation(recoveryContext, recoveredReply, "Mira", _ => { });
                var nextPayload = Payload("recovery_2"); nextPayload["playerText"] = "Use the east gate.";
                var nextContext = PrepareRouteConversationMemory(nextPayload, "npc", "recovery_route",
                    "recovery_session", "recovery_2", new[] { "npc" }, settingsOverride: settings);
                string nextPrompt = "";
                var next = GenerateRouteConversation(nextContext, Program.CloneDictionary(request), "No native effects.", 800, input =>
                {
                    nextPrompt = string.Join("\n", ReadDictionaryList(input, "messages").Select(m => ReadString(m, "content", "")));
                    return TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "The east gate, then.")));
                });
                if (ReadBool(next, "ok", false)) PublishRouteConversation(nextContext, "The east gate, then.", "Mira", _ => { });
                Check("next_route_turn_receives_safe_prior_question", ReadBool(next, "ok", false)
                    && nextPrompt.Contains(safeQuestion) && !nextPrompt.Contains("[Dialogue memory correction could not be verified.")
                    && SourceCount("recovery_route") == 4);
                var emptyContext = PrepareRouteConversationMemory(Payload("empty_retry"), "npc", "empty_recovery",
                    "empty_session", "empty_retry", new[] { "npc" }, settingsOverride: settings);
                var empty = GenerateRouteConversation(emptyContext, Program.CloneDictionary(request), "No native effects.", 800,
                    _ => TestDict("ok", true, "content", "{}"));
                Check("empty_generation_retains_only_player_input", !ReadBool(empty, "ok", true)
                    && ReadBool(ReadDictionary(empty, "conversationRecovery"), "canContinue", false)
                    && SourceCount("empty_recovery") == 1
                    && ReadString(ReadDictionary(empty, "inputRecovery"), "status", "") == "player_input_retained");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    Check("failed_generation_has_no_npc_or_acceptance", !DialogueSelfTestStore.SelectConversationTurns51(connection).Any()
                        && !DialogueSelfTestStore.SelectConversationRouteAcceptance(connection, TestDict("id", emptyContext.Unit)).Any());
                var retried = GenerateRouteConversation(emptyContext, Program.CloneDictionary(request), "No native effects.", 800,
                    _ => TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "We can discuss the gate."))));
                if (ReadBool(retried, "ok", false)) PublishRouteConversation(emptyContext, "We can discuss the gate.", "Mira", _ => { });
                Check("same_route_can_retry_after_empty_generation", ReadBool(retried, "ok", false)
                    && SourceCount("empty_recovery") == 2 && emptyContext.ProviderCalls == 2);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
