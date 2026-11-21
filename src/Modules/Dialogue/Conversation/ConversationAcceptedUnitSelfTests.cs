using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ReignBeta.Shared.Dialogue;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunConversationAcceptedUnitSelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object data = null) => results.Add(TestDict("caseId", "accepted_unit_" + id, "passed", passed, "summary", id, "data", data));
            string campaign = "accepted_unit_test_" + Guid.NewGuid().ToString("N");
            var originalInputs = new Dictionary<Dictionary<string, object>, string>();
            try
            {
                string generation;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                    EnsureConversationAgencySchema(connection); EnsureIntoxicationSchema(connection);
                    EnsureAcceptedConversationUnitSchema(connection);
                    CharactersService.EnsureIdentitySchema(connection); EnsureConversationSceneStateSchema(connection);
                    ReputationService.EnsureSocialReputationSchema(connection); EnsureConversationContinuitySchema(connection);
                    DialogueSelfTestStore.InsertConversationSessions2(connection, TestDict("campaign", campaign));
                }
                AcceptedConversationUnit Unit(string turn, bool effects = false, bool group = false, bool whisper = false)
                {
                    var agency = AgencyTestContext();
                    var payload = TestDict("campaignId", campaign, "timelineId", "agency_timeline", "worldDay", 20d,
                        "sceneTurnId", turn, "turnId", turn, "conversationSessionId", "accepted_unit_session",
                        "heroStringId", "npc", "playerHeroStringId", "player", "participants", new[] { "npc", "player", "listener" },
                        "playerText", effects ? ReadString(agency, "playerText", "") : "What would you choose by the orchard?");
                    originalInputs[payload] = ReadString(payload, "playerText", "");
                    var profile = TestDict("name", "Mira", "isFemale", true, "attributes", TestDict("endurance", 2));
                    var parsed = effects ? AgencyTestReply(AgencyTestProposal(agency)) : TestDict("reply", "I might visit the orchard if there is time.");
                    if (whisper) parsed["reply"] = "I privately told you about SECRET-ORCHARD-901.";
                    if (effects) parsed["reply"] = ReadString(parsed, "reply", "") + " *She takes a sip of wine.*";
                    var motive = effects ? TestDict("conversationAgency", agency) : TestDict();
                    if (effects)
                    {
                        parsed["body"] = parsed["reply"];
                        var prepared = EnforceConversationAgencyResponse(TestDict("ok", true, "content", Json.Serialize(parsed)), TestDict(), motive,
                            payload, campaign, turn, "dialogue", "npc", _ => throw new InvalidOperationException("No provider expected."), persist: false);
                        parsed = TryParseJsonObject(ReadString(prepared, "content", ""));
                        ApplyLegacyConversationDrinking(campaign, "npc", payload, profile, null, parsed, ReadString(parsed, "reply", ""), persist: false);
                    }
                    return new AcceptedConversationUnit { Campaign = campaign, Hero = "npc", Player = "player", Location = "town",
                        PlayerName = "Player", HeroName = "Mira", EventId = AcceptedConversationEventId(campaign, "npc", payload),
                        EventType = group ? "party_chat_turn" : "dialogue_turn", PlayerText = ReadString(payload, "playerText", ""),
                        Reply = ReadString(parsed, "reply", ""), Timestamp = 20, Generation = generation, Payload = payload,
                        Motive = motive, Profile = profile, Parsed = parsed, GroupConversation = group, PrivateWhisper = whisper,
                        Participants = whisper ? new List<string> { "npc", "player" } : null,
                        Memories = new List<Dictionary<string, object>> { TestDict("text", ReadString(parsed, "reply", ""), "ownerId", "npc",
                            "sourceEvidence", TestDict("turnId", "current_npc", "speakerId", "npc", "quote", ReadString(parsed, "reply", ""))) },
                        Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                }
                Dictionary<string, object> Publish(AcceptedConversationUnit unit, Action<string> fault = null) => PublishAcceptedConversationUnit(unit,
                    accept => unit.GroupConversation
                        ? StorePartyConversationExchange(campaign, unit.Payload, "npc", "player", "Player", "Mira",
                            unit.PrivateWhisper ? "A private remark." : unit.PlayerText,
                            unit.PrivateWhisper && !string.IsNullOrWhiteSpace(unit.Reply) ? "*The speaker replies privately to the player.*" : unit.Reply, unit.EventId, unit.Timestamp, accept)
                        : unit.EventType == "social_event_turn"
                        ? StoreSocialEventConversationExchange(campaign, unit.Payload, "npc", "player", "Player", "Mira",
                            unit.PlayerText, unit.Reply, unit.EventId, "quiet_event", unit.Timestamp, accept)
                        : StoreConversationExchange(campaign, unit.Payload, "npc", "player", "Player", "Mira",
                            unit.PlayerText, unit.Reply, unit.EventId, unit.Timestamp, acceptance: accept), fault);
                int Count(string table, string field = "", string id = "")
                {
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReadInt(DialogueSelfTestStore.CountDynamicDynamic(connection, table, field.Length == 0 ? "" : " WHERE " + field + "=$id",                        field.Length == 0 ? null : TestDict("id", id)).Single(), "n", 0);
                }
                bool AcceptedSourcesMatch(AcceptedConversationUnit value, int projectionCount = 2)
                {
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    var rows = DialogueSelfTestStore.SelectConversationTurns6(connection, TestDict("event", value.EventId));
                    var active = rows.Where(row => ReadString(row, "status", "") == "active").ToList();
                    var originals = rows.Where(row => ReadString(row, "status", "") == "source_only").ToList();
                    if (rows.Count != projectionCount + 1 || active.Count != projectionCount || originals.Count != 1
                        || active.Count(row => ReadString(row, "role", "") == "player") != projectionCount / 2
                        || active.Count(row => ReadString(row, "role", "") == "npc") != projectionCount / 2
                        || !PlayerInputSources.TryGetValue(value.Payload, out var captured)) return false;
                    var source = originals.Single();
                    string id = ReadString(source, "turn_id", ""), original = originalInputs[value.Payload];
                    var metadata = TryParseJsonObject(ReadString(source, "payload_json", "{}"));
                    var authors = DialogueService.TextListFromJson(ReadString(source, "participants_json", "[]"));
                    return id == captured.SourceId && captured.Original == original && ReadString(source, "text", "") == original
                        && ReadString(source, "role", "") == "player_source" && ReadString(source, "channel", "") == "player_input_source"
                        && ReadString(source, "speaker_id", "") == value.Player && ReadInt(source, "turn_order", 0) < 0
                        && authors.Count == 1 && authors[0] == value.Player
                        && ReadString(metadata, "sourceHash", "") == PromptHash(original)
                        && ReadString(metadata, "exactTextHash", "") == ConversationExactTextHash(original)
                        && ReadString(metadata, "evidenceType", "") == "original_player_input" && !ReadBool(metadata, "retrievalEligible", true)
                        && new[] { value.Hero, "listener" }.All(observer => !PrecisionTurnVisible(source, observer,
                            DialogueService.BuildKnowledgeAccessContext(observer, value.Player, value.Location, value.Payload), connection))
                        && DialogueSelfTestStore.SelectConversationTurnFts(connection, TestDict("id", id)).Count == 0;
                }
                var unit = Unit("atomic", effects: true);
                foreach (string phase in new[] { "after_sources", "after_effects", "after_context_effects", "after_memory", "before_unit_receipt" })
                {
                    bool failed = false, invisible = false;
                    try { Publish(unit, current => { if (current != phase) return;
                        invisible = Count("conversation_turns") == 0 && Count("events") == 0
                            && Count("conversation_drinking_turns") == 0 && Count("conversation_negotiation_turns") == 0;
                        throw new InvalidOperationException("Injected unit publication fault."); }); }
                    catch (InvalidOperationException) { failed = true; }
                    Check("all_database_effects_rollback_" + phase, failed && invisible && Count("conversation_turns") == 0
                        && Count("memories") == 0 && Count("events") == 0 && Count("conversation_accepted_units") == 0
                        && Count("conversation_drinking_turns") == 0 && Count("conversation_negotiation_turns") == 0);
                    Check("request_receipts_stay_prepared_" + phase,
                        ReadString(ReadDictionary(unit.Payload, "conversationAgencyReceipt"), "commitState", "") == "prepared"
                        && ReadString(ReadDictionary(unit.Payload, "intoxicationReceipt"), "commitState", "") == "prepared");
                }
                var accepted = Publish(unit);
                int memories = Count("memories");
                Check("accepted_sources_memory_and_effects_visible_together", ReadBool(accepted, "ok", false)
                    && AcceptedSourcesMatch(unit) && Count("conversation_turns") == 3 && Count("events") == 1 && memories > 0
                    && Count("conversation_drinking_turns") == 1 && Count("conversation_negotiation_turns") == 1
                    && Count("conversation_accepted_units") == 1);
                unit.Payload["correlationId"] = "new_http_attempt";
                var replay = Publish(unit);
                Check("retry_with_new_correlation_does_not_repeat_effects", ReadBool(ReadDictionary(replay, "acceptedUnit"), "idempotentReplay", false)
                    && AcceptedSourcesMatch(unit) && Count("conversation_turns") == 3 && Count("memories") == memories
                    && Count("conversation_drinking_turns") == 1 && Count("conversation_negotiation_turns") == 1
                    && AcceptedConversationEventId(campaign, "npc", unit.Payload) == unit.EventId);
                unit.Reply += " A conflicting replacement.";
                bool conflict = false;
                try { Publish(unit); } catch (InvalidOperationException) { conflict = true; }
                Check("same_unit_cannot_replace_accepted_text", conflict && AcceptedSourcesMatch(unit)
                    && Count("conversation_turns") == 3 && Count("memories") == memories);

                var unsupported = Unit("unsupported_summary");
                unsupported.Memories = new() { TestDict("text", "Both speakers agreed to depart immediately.", "sourceEvidence",
                    TestDict("turnId", "current_player", "speakerId", "npc", "quote", unsupported.PlayerText)) };
                var unsupportedReceipt = ReadDictionary(Publish(unsupported), "acceptedUnit");
                Check("unsupported_source_marks_memory_incomplete_without_erasing_speech",
                    !ReadBool(unsupportedReceipt, "memoryUpdateComplete", true)
                    && ReadString(unsupportedReceipt, "status", "") == "committed_memory_incomplete"
                    && ReadString(ReadDictionary(unsupportedReceipt, "memory"), "sourceEvidenceStatus", "") == "incomplete"
                    && Count("conversation_turns", "event_id", unsupported.EventId) == 2);
                Check("incomplete_source_receipt_stays_incomplete_on_replay", !ReadBool(
                    ReadDictionary(Publish(unsupported), "acceptedUnit"), "memoryUpdateComplete", true));
                var aliasUnit = Unit("current_alias_summary", group: true);
                ReadDictionary(aliasUnit.Memories[0], "sourceEvidence")["turnId"] = ReadString(aliasUnit.Payload, "sceneTurnId", "");
                Check("exact_group_beat_source_is_complete", ReadBool(ReadDictionary(Publish(aliasUnit), "acceptedUnit"), "memoryUpdateComplete", false));

                Check("standalone_terms_question_cannot_hide_an_additional_request",
                    IsPureConversationRecallRequest("Before we go, what were your terms for the north gate?")
                    && !IsPureConversationRecallRequest("What were your terms for the north gate and can I bring six guards?")
                    && !IsPureConversationRecallRequest("What were your terms for the north gate, and can I bring six guards?"));
                var recall = Unit("standalone_terms_recall");
                recall.PlayerText = "Before we go, what exactly were my conditions for giving the crates to Irena?";
                recall.Payload["playerText"] = recall.PlayerText;
                recall.RetrievalEligible = !IsPureConversationRecallRequest(recall.PlayerText);
                recall.Continuity.Add(TestDict("id", "recall_only_agenda", "kind", "agenda", "status", "active",
                    "subject", "Earlier terms", "meaning", recall.Reply, "expectedRevision", 0, "evidenceQuote", recall.Reply));
                var recallReceipt = ReadDictionary(Publish(recall), "acceptedUnit");
                Check("standalone_terms_recall_keeps_speech_without_new_memory_or_continuity",
                    IsPureConversationRecallRequest(recall.PlayerText)
                    && ReadBool(recallReceipt, "memoryUpdateComplete", false)
                    && ReadBool(ReadDictionary(recallReceipt, "memory"), "auditOnly", false)
                    && Count("conversation_turns", "event_id", recall.EventId) == 2
                    && Count("events", "event_id", recall.EventId) == 0
                    && Count("memories", "event_id", recall.EventId) == 0
                    && Count("conversation_continuity", "record_id", "recall_only_agenda") == 0);

                var mixedRecall = Unit("terms_recall_with_current_report");
                mixedRecall.PlayerText = "I just counted eight sealed crates by the inner door. What were my terms from our previous conversation?";
                mixedRecall.Payload["playerText"] = mixedRecall.PlayerText;
                mixedRecall.RetrievalEligible = !IsPureConversationRecallRequest(mixedRecall.PlayerText);
                mixedRecall.Memories = ActionPipelineService.NormalizeMemoryWrites(new[] { TestDict("text", mixedRecall.PlayerText, "ownerId", "npc", "sourceEvidence",
                    TestDict("turnId", "current_player", "speakerId", "player", "quote", mixedRecall.PlayerText)) }, campaign, "npc", mixedRecall.Timestamp);
                var mixedRecallReceipt = ReadDictionary(Publish(mixedRecall), "acceptedUnit");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var report = DialogueSelfTestStore.SelectMemories2(connection, TestDict("event", mixedRecall.EventId)).SingleOrDefault();
                    var reportPayload = TryParseJsonObject(ReadString(report, "payload_json", "{}"));
                    Check("mixed_terms_recall_keeps_exact_current_player_report",
                        !IsPureConversationRecallRequest(mixedRecall.PlayerText)
                        && ReadBool(mixedRecallReceipt, "memoryUpdateComplete", false)
                        && ReadString(report, "summary", "") == mixedRecall.PlayerText
                        && ReadString(ReadDictionary(reportPayload, "sourceEvidence"), "speakerId", "") == "player"
                        && ReadString(ReadDictionary(reportPayload, "semanticAdmission"), "status", "") == "exact_reported_source");
                }

                var privateUnit = Unit("whisper", group: true, whisper: true);
                bool privateFailure = false;
                try { Publish(privateUnit, phase => { if (phase == "after_memory") throw new InvalidOperationException("Private source fault."); }); }
                catch (InvalidOperationException) { privateFailure = true; }
                Check("whisper_public_and_private_sources_rollback_together", privateFailure && Count("conversation_turns", "event_id", privateUnit.EventId) == 0);
                var whisper = Publish(privateUnit);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var rows = DialogueSelfTestStore.SelectConversationTurns7(connection, TestDict("id", privateUnit.EventId));
                    var access = DialogueService.BuildKnowledgeAccessContext("listener", "player", "", privateUnit.Payload);
                    Check("whisper_accepts_both_projections_without_private_leak", AcceptedSourcesMatch(privateUnit, 4)
                        && ReadDictionary(ReadDictionary(whisper, "acceptedUnit"), "privateExchange").Count > 0
                        && rows.Where(r => PrecisionTurnVisible(r, "listener", access)).All(r => !ReadString(r, "text", "").Contains("SECRET-ORCHARD-901")));
                }
                var concurrentA = Unit("concurrent", group: true);
                var concurrentB = Unit("concurrent", group: true);
                var attempts = new[] { Task.Run(() => Publish(concurrentA)), Task.Run(() => Publish(concurrentB)) };
                Task.WaitAll(attempts);
                Check("concurrent_same_unit_has_one_receipt", attempts.All(t => ReadBool(t.Result, "ok", false))
                    && Count("conversation_accepted_units", "unit_id", concurrentA.EventId) == 1
                    && Count("events", "event_id", concurrentA.EventId) == 1);
                Check("identical_text_new_logical_turn_is_distinct", Unit("another_turn").EventId != concurrentA.EventId);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    foreach (string id in new[] { "npc", "player", "listener" })
                        DialogueSelfTestStore.InsertIdentityRoster(connection, TestDict("id", id));
                    CharactersService.UpsertVerifiedIdentity(connection, TestDict("heroStringId", "npc"), TestDict("heroStringId", "player", "name", "Michael"), "fixture", "", 20d, "");
                    CharactersService.UpsertVerifiedIdentity(connection, TestDict("heroStringId", "npc"), TestDict("heroStringId", "listener", "name", "Listener"), "fixture", "", 20d, "");
                }
                void Context(AcceptedConversationUnit target)
                {
                    target.PublishScene = true; target.PublishIntroductions = true; target.PublishSocial = true;
                    target.ConceptionGate = TestDict("completed", false);
                    target.Payload["activeHeroIds"] = new[] { "npc", "listener" };
                    target.Payload["conversationSceneState"] = TestDict("sceneTurnId", ReadString(target.Payload, "sceneTurnId", ""),
                        "participants", new[] { TestDict("heroStringId", "npc"), TestDict("heroStringId", "listener"), TestDict("heroStringId", "player", "role", "player") });
                    target.Reply = "Let me introduce Michael. You are beautiful. *She steps into the courtyard. She remembers SECRET-THOUGHT-901.*";
                    target.Parsed["reply"] = target.Reply;
                    target.Memories = new() { TestDict("text", target.Reply, "ownerId", "npc") };
                    target.Parsed["sceneStateUpdates"] = new[] { TestDict("heroStringId", "npc", "location", "courtyard"), TestDict("heroStringId", "listener", "location", "forbidden tower") };
                    target.Parsed["socialSignals"] = new[] { TestDict("type", "flirtation", "supportingQuote", "You are beautiful.", "speakerHeroId", "npc", "targetHeroId", "player") };
                    target.Parsed["identityIntroductions"] = new[] { TestDict("observerHeroStringId", "listener", "subjectHeroStringId", "player",
                        "introducerHeroStringId", "npc", "claimedName", "Michael", "supportingQuote", "Let me introduce Michael.") };
                }
                var contextUnit = Unit("context_effects", group: true); Context(contextUnit);
                int identityBefore = Count("identity_evidence");
                var preview = ReputationService.NormalizeAndPersistConversationSocialSignals(campaign, contextUnit.Parsed, contextUnit.Payload,
                    "npc", "player", contextUnit.PlayerText, contextUnit.Reply, contextUnit.ConceptionGate, contextUnit.EventId, 20d, persist: false);
                Check("social_preview_has_no_durable_effects", ReadDictionaryList(preview, "signals").Count == 1 && Count("court_social_signal_evidence") == 0);
                bool contextFailed = false;
                try { Publish(contextUnit, phase => { if (phase == "after_context_effects") throw new InvalidOperationException("Context effect fault."); }); }
                catch (InvalidOperationException) { contextFailed = true; }
                Check("scene_social_and_identity_rollback_with_sources", contextFailed
                    && Count("conversation_turns", "event_id", contextUnit.EventId) == 0 && Count("conversation_scene_state") == 0
                    && Count("conversation_scene_handoffs") == 0 && Count("court_social_signal_evidence") == 0
                    && Count("identity_evidence") == identityBefore);
                var contextReceipt = ReadDictionary(Publish(contextUnit), "acceptedUnit");
                Check("context_effects_accept_once", Count("conversation_scene_state") == 1
                    && Count("court_social_signal_evidence") == 1 && Count("identity_evidence") == identityBefore + 1
                    && ReadDictionaryList(contextReceipt, "introductions").All(item => ReadBool(item, "ok", false))
                    && ReadDictionaryList(ReadDictionary(contextReceipt, "scene"), "denied").Count == 1);
                Publish(contextUnit);
                Check("context_effect_replay_does_not_repeat", Count("identity_evidence") == identityBefore + 1 && Count("court_social_signal_evidence") == 1);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string listener = ReadString(DialogueSelfTestStore.SelectConversationSceneHandoffs(connection).Single(), "payload_json", "");
                    string author = ReadString(DialogueSelfTestStore.SelectConversationSceneHandoffs2(connection).Single(), "payload_json", "");
                    Check("scene_handoffs_preserve_author_thought_without_broadcasting", !listener.Contains("SECRET-THOUGHT-901")
                        && listener.Contains("courtyard") && author.Contains("SECRET-THOUGHT-901"));
                }
                var whisperContext = Unit("private_context", group: true, whisper: true); Context(whisperContext);
                whisperContext.Reply += " SECRET-SPOKEN-901."; whisperContext.Parsed["reply"] = whisperContext.Reply;
                var privateContextReceipt = ReadDictionary(Publish(whisperContext), "acceptedUnit");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string listener = ReadString(DialogueSelfTestStore.SelectConversationSceneHandoffs(connection).Single(), "payload_json", "");
                    Check("private_handoff_and_introduction_do_not_reach_bystander", !listener.Contains("SECRET-SPOKEN-901")
                        && ReadDictionaryList(privateContextReceipt, "introductions").All(item => !ReadBool(item, "ok", true)));
                    var falseIntroducer = CloneDictionary(contextUnit.Parsed);
                    falseIntroducer["identityIntroductions"] = new[] { TestDict("observerId", "player", "subjectId", "npc", "introducerId", "listener",
                        "claimedName", "Michael", "supportingQuote", "Let me introduce Michael.") };
                    var invalid = CharactersService.ApplyIdentityIntroductionWrites(campaign, contextUnit.Payload, falseIntroducer, "test", "npc", "player",
                        contextUnit.PlayerText, contextUnit.Reply, new[] { "npc", "player", "listener" }, connection);
                    Check("another_present_character_is_not_the_introducer", invalid.Count == 1 && !ReadBool(invalid[0], "ok", true));
                    var thought = CharactersService.ApplyIdentityIntroductionWrites(campaign, contextUnit.Payload, contextUnit.Parsed, "test", "npc", "player", "Hello.",
                        "*She thinks: Let me introduce Michael.*", new[] { "npc", "player", "listener" }, connection);
                    Check("private_narration_cannot_verify_an_introduction", thought.Count == 1 && !ReadBool(thought[0], "ok", true));
                    var playerScene = CloneDictionary(contextUnit.Payload);
                    var playerUpdate = TestDict("sceneStateUpdates", new[] { TestDict("heroStringId", "player", "location", "garden",
                        "supportingQuote", "I walk into the garden.") });
                    var playerMoved = FinalizeConversationSceneState(campaign, playerScene, "npc", "*I walk into the garden.*",
                        "I follow beside you.", "dialogue", playerUpdate, connection, new[] { "npc", "player" });
                    Check("current_player_narrated_move_remains_supported", ReadDictionaryList(playerMoved, "applied").Any(item => ReadString(item, "heroStringId", "") == "player"));
                    var noPlayerMove = FinalizeConversationSceneState(campaign, playerScene, "npc", "I said I might walk into the garden.",
                        "It is your choice.", "dialogue", playerUpdate, connection, new[] { "npc", "player" });
                    Check("player_hypothetical_cannot_move_scene", ReadDictionaryList(noPlayerMove, "applied").Count == 0);
                    var legacy = TestDict("acceptedExchanges", new[] { TestDict("turnId", "absent_old_turn", "sessionId", "accepted_unit_session",
                        "speakerId", "npc", "playerText", "A private message.", "reply", "SECRET-LEGACY-901") });
                    Check("unverified_legacy_handoff_does_not_disclose_another_speaker", ProjectSceneHandoffExchanges(connection, legacy, "listener", contextUnit.Payload).Count == 0);
                    legacy["acceptedExchanges"] = new[] { TestDict("turnId", "context_effects", "sessionId", "accepted_unit_session",
                        "speakerId", "npc", "playerText", "A wrong old summary.", "reply", "SECRET-LEGACY-901") };
                    var recovered = ProjectSceneHandoffExchanges(connection, legacy, "listener", contextUnit.Payload);
                    Check("legacy_handoff_recovers_actual_heard_words", recovered.Count == 1
                        && ReadString(recovered[0], "reply", "").Contains("Let me introduce Michael.")
                        && !Json.Serialize(recovered).Contains("SECRET-LEGACY-901") && !Json.Serialize(recovered).Contains("SECRET-THOUGHT-901"));
                }
                var concernUnit = Unit("concern_seed");
                void SetConcernReply(AcceptedConversationUnit value, string words)
                {
                    value.Reply = words; value.Parsed["reply"] = words;
                    value.Memories = new() { TestDict("text", words, "ownerId", "npc", "sourceEvidence",
                        TestDict("turnId", "current_npc", "speakerId", "npc", "quote", words)) };
                }
                SetConcernReply(concernUnit, "I still need to understand why the western gate was closed.");
                var concern = TestDict("id", "pending_concern", "kind", "agenda", "peerId", "", "status", "active",
                    "subject", "The closed western gate", "meaning", "The reason is unknown.", "expectedRevision", 0,
                    "evidenceQuote", concernUnit.Reply);
                concernUnit.Continuity.Add(concern);
                Publish(concernUnit);
                var losing = Unit("concern_losing");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign)) CaptureContinuityReadSnapshot(connection, losing.Payload, "npc");
                var winning = Unit("concern_winning");
                SetConcernReply(winning, "I learned that the western gate was closed for repairs.");
                winning.Continuity.Add(new Dictionary<string, object>(concern) { ["expectedRevision"] = 1,
                    ["meaning"] = "The repairs are still under discussion.", ["evidenceQuote"] = winning.Reply });
                Publish(winning);
                SetConcernReply(losing, "I understand your answer about the western gate now.");
                losing.Continuity.Add(new Dictionary<string, object>(concern) { ["expectedRevision"] = 2,
                    ["status"] = "resolved", ["transitionReason"] = "Answer acknowledged.",
                    ["meaning"] = "His answer was acknowledged.", ["evidenceQuote"] = losing.Reply });
                bool pendingRolledBack = false;
                try { Publish(losing, phase => { if (phase == "after_memory") throw new InvalidOperationException("Pending recovery fault."); }); }
                catch (InvalidOperationException) { pendingRolledBack = true; }
                Check("pending_recovery_rolls_back_with_sources", pendingRolledBack
                    && Count("conversation_continuity_pending") == 0 && Count("conversation_turns", "event_id", losing.EventId) == 0);
                var incomplete = ReadDictionary(Publish(losing), "acceptedUnit");
                Check("concurrent_update_has_durable_incomplete_receipt", ReadBool(incomplete, "ok", false)
                    && !ReadBool(incomplete, "memoryUpdateComplete", true)
                    && ReadString(incomplete, "status", "") == "committed_memory_incomplete"
                    && Count("conversation_continuity_pending", "status", "pending") == 1);
                int effectCount = Count("conversation_drinking_turns");
                var losingReplay = ReadDictionary(Publish(losing), "acceptedUnit");
                Check("incomplete_replay_keeps_one_pending_source_and_no_repeated_effect", ReadBool(losingReplay, "idempotentReplay", false)
                    && !ReadBool(losingReplay, "memoryUpdateComplete", true) && Count("conversation_continuity_pending") == 1
                    && Count("conversation_drinking_turns") == effectCount);
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "pending_continuity_checkpoint");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    DialogueSelfTestStore.UpdateConversationContinuityPending(connection);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "pending_continuity_checkpoint");
                AdvanceMemoryRestoreGeneration(campaign);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                Check("snapshot_restores_pending_receipt_and_canonical_source", Count("conversation_continuity_pending", "status", "pending") == 1
                    && AcceptedSourcesMatch(losing)
                    && Count("conversation_accepted_units", "unit_id", losing.EventId) == 1);
                var resolution = Unit("concern_reconciled");
                string pendingPrompt = BuildProtectedConversationContinuity(campaign, "npc", resolution.Payload, TestDict());
                Check("pending_correction_and_current_revision_reach_next_read", pendingPrompt.Contains(losing.Reply)
                    && pendingPrompt.Contains("MEMORY UPDATE INCOMPLETE") && pendingPrompt.Contains("current revision 2")
                    && pendingPrompt.Contains("The repairs are still under discussion."));
                Check("pending_source_remains_private_and_time_scoped",
                    !BuildProtectedConversationContinuity(campaign, "listener", CloneDictionary(resolution.Payload), TestDict()).Contains("MEMORY UPDATE INCOMPLETE")
                    && !BuildProtectedConversationContinuity(campaign, "npc", new Dictionary<string, object>(resolution.Payload) { ["timelineId"] = "other" }, TestDict()).Contains("MEMORY UPDATE INCOMPLETE")
                    && !BuildProtectedConversationContinuity(campaign, "npc", new Dictionary<string, object>(resolution.Payload) { ["worldDay"] = 19d }, TestDict()).Contains("MEMORY UPDATE INCOMPLETE")
                    && !BuildProtectedConversationContinuity(campaign, "npc", new Dictionary<string, object>(resolution.Payload) { ["memoryAsOfTimestamp"] = 19L }, TestDict()).Contains("MEMORY UPDATE INCOMPLETE"));
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    using var transaction = connection.BeginTransaction();
                    DialogueSelfTestStore.UpdateConversationTurns8(connection, TestDict("event", losing.EventId));
                    var changedPayload = CloneDictionary(resolution.Payload);
                    CaptureContinuityReadSnapshot(connection, changedPayload, "npc");
                    string changedPrompt = BuildPendingContinuityPrompt(connection, changedPayload, "npc", "player", new List<string>());
                    Check("pending_changed_source_is_withheld_with_gap", changedPrompt.Length == 0
                        && ReadStringList(ReadDictionary(changedPayload, "continuityRecovery"), "coverageGaps").Count == 1);
                    transaction.Rollback();
                }
                SetConcernReply(resolution, "Your answer was clear; I only want to know when the repairs finish.");
                resolution.Continuity.Add(new Dictionary<string, object>(concern) { ["expectedRevision"] = 999,
                    ["meaning"] = "The reason is known; the completion date is a separate concern.", ["evidenceQuote"] = resolution.Reply });
                var reconciled = ReadDictionary(Publish(resolution), "acceptedUnit");
                Check("new_accepted_read_supersedes_pending_without_rebasing_old_write", ReadBool(reconciled, "memoryUpdateComplete", false)
                    && Count("conversation_continuity_pending", "status", "pending") == 0
                    && Count("conversation_continuity_pending", "status", "superseded_after_reread") == 1, reconciled);
                Check("resolved_pending_leaves_prompt_and_keeps_source", !BuildProtectedConversationContinuity(campaign, "npc", Unit("later_read").Payload, TestDict()).Contains("MEMORY UPDATE INCOMPLETE")
                    && AcceptedSourcesMatch(losing));

                var failedConversation = Unit("failed_repair_speech", effects: true);
                Context(failedConversation);
                failedConversation.Parsed = VisibleFailedRepairWithoutEffects(failedConversation.Parsed, "memory_rewrite");
                failedConversation.Reply = ReadString(failedConversation.Parsed, "reply", "");
                int beforeFailedMemories = Count("memories"), beforeFailedDrinks = Count("conversation_drinking_turns"),
                    beforeFailedNegotiations = Count("conversation_negotiation_turns"), beforeFailedSocial = Count("court_social_signal_evidence"),
                    beforeFailedIdentity = Count("identity_evidence");
                var failedReceipt = ReadDictionary(Publish(failedConversation), "acceptedUnit");
                Check("safe_speech_publication_cannot_reenable_failed_effects", ReadBool(failedReceipt, "ok", false)
                    && ReadString(ReadDictionary(failedReceipt, "effects"), "status", "") == "suppressed_by_route"
                    && Count("memories") == beforeFailedMemories && Count("conversation_drinking_turns") == beforeFailedDrinks
                    && Count("conversation_negotiation_turns") == beforeFailedNegotiations
                    && Count("court_social_signal_evidence") == beforeFailedSocial && Count("identity_evidence") == beforeFailedIdentity
                    && Count("conversation_turns", "event_id", failedConversation.EventId) == 2);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    Check("failed_effect_receipt_still_allows_spoken_history", ReadLogicalConversationTurns(connection, "accepted_unit_session", 100)
                        .Any(t => ReadString(t, "event_id", "") == failedConversation.EventId && ReadString(t, "role", "") == "npc"
                            && !ReadString(t, "text", "").Contains("[Dialogue memory correction")));
                foreach (bool group in new[] { false, true })
                {
                    var privateDrink = Unit("private_drink_" + group, group: group);
                    privateDrink.Parsed["drinkActions"] = new[] { TestDict("id", "sip_1", "operation", "consume",
                        "holderHeroId", "npc", "vesselId", "private_cup", "beverage", "wine", "serving", "sip", "count", 1, "completed", true) };
                    ApplyConversationDrinking(campaign, "npc", privateDrink.Payload, privateDrink.Profile, null,
                        privateDrink.Parsed, privateDrink.Reply, persist: false);
                    int beforePrivateDrinks = Count("conversation_drinking_turns");
                    bool privateRolledBack = false;
                    try { Publish(privateDrink, phase => { if (phase == "after_effects") throw new InvalidOperationException("Private drink publication fault."); }); }
                    catch (InvalidOperationException) { privateRolledBack = true; }
                    Check("private_drinking_rollback_with_transcript_" + group, privateRolledBack
                        && Count("conversation_drinking_turns") == beforePrivateDrinks
                        && Count("conversation_turns", "event_id", privateDrink.EventId) == 0);
                    var privateReceipt = ReadDictionary(Publish(privateDrink), "acceptedUnit");
                    Publish(privateDrink);
                    var drinking = ReadDictionary(ReadDictionary(privateReceipt, "effects"), "drinking");
                    Check("private_drinking_accepted_once_with_transcript_" + group, ReadBool(privateReceipt, "ok", false)
                        && Count("conversation_drinking_turns") == beforePrivateDrinks + 1
                        && AcceptedSourcesMatch(privateDrink)
                        && ReadString(drinking, "accountingMode", "") == PrivateDrinkActionMode
                        && ReadDouble(drinking, "consumed", -1) == .25
                        && ReadString(drinking, "acceptedReplyHash", "") == PromptHash(privateDrink.Reply), privateReceipt);
                }
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign)) RelationshipsService.EnsurePlayerApproachSchema(connection);
                foreach (string route in new[] { "party", "private_party", "social_event" })
                {
                    var silent = Unit("silent_" + route, group: route != "social_event", whisper: route == "private_party");
                    if (route == "social_event") silent.EventType = "social_event_turn";
                    silent.Reply = ""; silent.Parsed["reply"] = ""; silent.Parsed["participation"] = "quiet";
                    silent.PublishScene = silent.PublishSocial = silent.PublishIntroductions = true;
                    silent.Payload["playerAttentionPresence"] = new[] {
                        TestDict("heroId", "npc", "alive", true, "scopes", new[] { "town" }),
                        TestDict("heroId", "listener", "alive", true, "scopes", new[] { "town" }) };
                    var inputReceipt = AdmitConversationInput(campaign, silent.Payload, generation, "npc");
                    int eventCount = Count("events"), memoryCount = Count("memories"), attentionCount = Count("player_attention_events");
                    int drinkCount = Count("conversation_drinking_turns"), negotiationCount = Count("conversation_negotiation_turns");
                    bool rolledBack = false;
                    try { Publish(silent, phase => { if (phase == "before_unit_receipt") throw new InvalidOperationException("Silent acceptance fault."); }); }
                    catch (InvalidOperationException) { rolledBack = true; }
                    Check(route + "_silence_rollback_keeps_original_input", rolledBack
                        && Count("conversation_accepted_units", "unit_id", silent.EventId) == 0
                        && Count("conversation_turns", "turn_id", ReadString(inputReceipt, "sourceId", "")) == 1);
                    var silence = Publish(silent);
                    var receipt = ReadDictionary(silence, "acceptedUnit");
                    Check(route + "_silence_accepts_without_reply_effects", ReadBool(receipt, "ok", false)
                        && ReadBool(receipt, "memoryUpdateComplete", false) && ReadString(receipt, "status", "") == "committed"
                        && ReadString(ReadDictionary(receipt, "playerAttention"), "status", "") == "intentional_silence_excluded"
                        && ReadString(ReadDictionary(receipt, "npcLives"), "status", "") == "intentional_silence_excluded"
                        && ReadString(ReadDictionary(receipt, "memory"), "status", "") == "intentional_silence_excluded"
                        && Count("events") == eventCount && Count("memories") == memoryCount
                        && Count("player_attention_events") == attentionCount && Count("conversation_drinking_turns") == drinkCount
                        && Count("conversation_negotiation_turns") == negotiationCount, receipt);
                    var replayed = ReadConversationInputReplay(campaign, silent.Payload, "npc");
                    Check(route + "_silence_replay_retains_participation", ReadBool(replayed, "idempotentReplay", false)
                        && ReadString(replayed, "participation", "") == "quiet" && ReadString(replayed, "reply", "missing") == ""
                        && ReadBool(ReadDictionary(Publish(silent), "acceptedUnit"), "idempotentReplay", false));
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    var sources = DialogueSelfTestStore.SelectConversationTurns6(connection, TestDict("event", silent.EventId));
                    Check(route + "_silence_has_no_fabricated_npc_source", sources.All(row => ReadString(row, "role", "") != "npc")
                        && ReadStringList(receipt, "sourceIds").All(id => DialogueSelfTestStore.SelectConversationTurns8(connection, TestDict("id", id)).Count == 1));
                }
                var afterSilence = Unit("after_silent_turn", group: true);
                afterSilence.Payload["playerAttentionPresence"] = new[] {
                    TestDict("heroId", "npc", "alive", true, "scopes", new[] { "town" }),
                    TestDict("heroId", "listener", "alive", true, "scopes", new[] { "town" }) };
                CapturePlayerInput(afterSilence.Payload);
                var afterSilenceReceipt = ReadDictionary(Publish(afterSilence), "acceptedUnit");
                Check("spoken_turn_after_silence_still_accepts", ReadBool(afterSilenceReceipt, "ok", false) && AcceptedSourcesMatch(afterSilence)
                    && ReadString(ReadDictionary(afterSilenceReceipt, "playerAttention"), "status", "") == "committed"
                    && ReadString(ReadDictionary(afterSilenceReceipt, "playerAttention"), "sourceId", "").Length > 0, afterSilenceReceipt);
                var single = Unit("single_pass_missing_metadata");
                CapturePlayerInput(single.Payload);
                var oneAnswer = GenerateSinglePassConversation(TestDict(), single.Payload, "dialogue", responder: _ =>
                    TestDict("ok", true, "content", Json.Serialize(TestDict("reply", "I'll put it to each of them. Would you go?"))));
                single.Parsed = TryParseJsonObject(ReadString(oneAnswer, "content", ""));
                single.Reply = ReadString(single.Parsed, "reply", "");
                single.Memories.Clear();
                var singleReceipt = ReadDictionary(Publish(single), "acceptedUnit");
                Check("single_pass_missing_metadata_publishes_original_speech", ReadBool(singleReceipt, "ok", false)
                    && AcceptedSourcesMatch(single) && !single.Parsed.ContainsKey("structuredActionDecision"), singleReceipt);
                Check("single_pass_exact_replay_does_not_republish", ReadBool(ReadDictionary(Publish(single), "acceptedUnit"), "idempotentReplay", false)
                    && Count("conversation_accepted_units", "unit_id", single.EventId) == 1);
                string originalSingleReply = single.Reply;
                single.Reply += " Replaced.";
                bool singleConflict = false;
                try { Publish(single); } catch (InvalidOperationException) { singleConflict = true; }
                Check("single_pass_cannot_replace_accepted_speech", singleConflict);
                single.Reply = originalSingleReply;

                Check("view_insertion_opt_in_requires_exact_schema", IndividualViewInsertionRequested(
                    TestDict(IndividualViewInsertionOptInField, IndividualViewInsertionSchema))
                    && !IndividualViewInsertionRequested(TestDict(IndividualViewInsertionOptInField, "reign-individual-view-insertion-v2"))
                    && !IndividualViewInsertionRequested(TestDict()));
                var view = Unit("view_insertion"); view.ViewInsertionRequested = true;
                AdmitConversationInput(campaign, view.Payload, generation, "npc");
                var viewDescriptor = ReadDictionary(ReadDictionary(Publish(view), "acceptedUnit"), "viewInsertion");
                var viewReplay = ReadDictionary(ReadDictionary(ReadDictionary(ReadConversationInputReplay(campaign, view.Payload, "npc"),
                    "conversationExchange"), "acceptedUnit"), "viewInsertion");
                Check("view_insertion_descriptor_binds_exact_npc_source", viewDescriptor != null
                    && ReadString(viewDescriptor, "schema", "") == IndividualViewInsertionSchema
                    && ReadString(viewDescriptor, "sourceTurnId", "") == view.EventId + "_npc"
                    && ReadString(viewDescriptor, "speakerHeroStringId", "") == "npc" && ReadString(viewDescriptor, "playerHeroStringId", "") == "player"
                    && ReadString(viewDescriptor, "sessionId", "") == "accepted_unit_session" && ReadString(viewDescriptor, "timelineId", "") == "agency_timeline"
                    && ReadString(viewDescriptor, "restoreGeneration", "") == generation
                    && ReadString(viewDescriptor, "textSha256", "") == ConversationExactTextHash(view.Reply)
                    // The replay is read back from JSONB, which reorders object keys; compare semantically
                    // (keys sorted, values and array order kept exactly).
                    && viewReplay != null && CanonicalJson(viewReplay) == CanonicalJson(viewDescriptor), viewDescriptor);
                Dictionary<string, object> Ack(Dictionary<string, object> descriptor, string field = null, string value = null)
                {
                    var body = TestDict("schema", IndividualViewInsertionAckSchema);
                    foreach (string key in IndividualViewInsertionIdentityFields) body[key] = ReadString(descriptor, key, "");
                    if (field != null) body[field] = value;
                    return IndividualViewInsertionApi(body);
                }
                string StoredReceipt(string unitId)
                {
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    return ReadString(DialogueSelfTestStore.SelectConversationAcceptedUnits(connection, TestDict("unit", unitId)).Single(), "receipt_json", "");
                }
                string beforeAck = StoredReceipt(view.EventId);
                foreach (var (field, value, disposition) in new[] {
                    ("textSha256", ConversationExactTextHash(view.Reply + " "), "conflict"),
                    ("sourceTurnId", view.EventId + "_player", "conflict"),
                    ("speakerHeroStringId", "listener", "conflict"),
                    ("playerHeroStringId", "listener", "conflict"),
                    ("sessionId", "other_session", "conflict"),
                    ("restoreGeneration", "old_generation", "conflict"),
                    ("role", "player", "invalid"),
                    ("textSha256", ConversationExactTextHash(view.Reply).ToUpperInvariant(), "invalid") })
                    Check("view_insertion_rejects_wrong_" + field + "_" + disposition, ReadString(Ack(viewDescriptor, field, value), "disposition", "") == disposition
                        && StoredReceipt(view.EventId) == beforeAck);
                string missingCampaign = "view_insertion_missing_" + Guid.NewGuid().ToString("N");
                Check("view_insertion_unknown_campaign_is_not_created", ReadString(Ack(viewDescriptor, "campaignId", missingCampaign), "reason", "") == "unknown_campaign"
                    && !ReignPostgreSqlStorage.CampaignExists(missingCampaign));
                // Low-level registration only: a known schema with no campaign initialization.
                string coldCampaign = "view_insertion_cold_" + Guid.NewGuid().ToString("N");
                try
                {
                    using (ReignPostgreSqlStorage.OpenCampaignConnection(coldCampaign)) { }
                    int ColdTables()
                    {
                        using var metadata = ReignPostgreSqlStorage.OpenMetadataConnection();
                        return ReadInt(DialogueSelfTestStore.CountPgTables(metadata, TestDict("schema", ReignPostgreSqlStorage.CampaignSchemaName(coldCampaign))).Single(), "n", -1);
                    }
                    int coldBefore = ColdTables();
                    var coldAck = Ack(viewDescriptor, "campaignId", coldCampaign);
                    Check("view_insertion_cold_registered_campaign_is_not_initialized", ReignPostgreSqlStorage.CampaignExists(coldCampaign)
                        && coldBefore == 0 && ColdTables() == 0 && ReadString(coldAck, "disposition", "") == "unknown"
                        && ReadString(coldAck, "reason", "") == "no_view_insertion_descriptor", coldAck);
                }
                finally { ReignPostgreSqlStorage.DropCampaign(coldCampaign); }
                var extra = TestDict("schema", IndividualViewInsertionAckSchema, "acknowledgedTs", "1");
                foreach (string key in IndividualViewInsertionIdentityFields) extra[key] = ReadString(viewDescriptor, key, "");
                Check("view_insertion_rejects_extra_or_app_time_fields", ReadString(IndividualViewInsertionApi(extra), "disposition", "") == "invalid"
                    && StoredReceipt(view.EventId) == beforeAck);
                var firstAck = Ack(viewDescriptor);
                string afterAck = StoredReceipt(view.EventId);
                var storedInsertion = ReadDictionary(ReadDictionary(TryParseJsonObject(afterAck), "viewInsertion"), "insertion");
                Check("view_insertion_exact_ack_records_once", ReadString(firstAck, "disposition", "") == "inserted"
                    && ReadString(firstAck, "reason", "") == "recorded" && ReadLong(firstAck, "acknowledgedTs", 0) > 0
                    && ReadLong(storedInsertion, "acknowledgedTs", 0) == ReadLong(firstAck, "acknowledgedTs", 0)
                    && ReadString(ReadDictionary(TryParseJsonObject(afterAck), "viewInsertion"), "textSha256", "") == ReadString(viewDescriptor, "textSha256", ""), firstAck);
                var duplicateAck = Ack(viewDescriptor);
                Check("view_insertion_duplicate_ack_is_idempotent_without_overwrite", ReadString(duplicateAck, "disposition", "") == "inserted"
                    && ReadString(duplicateAck, "reason", "") == "exact_replay"
                    && ReadLong(duplicateAck, "acknowledgedTs", -1) == ReadLong(firstAck, "acknowledgedTs", -2)
                    && StoredReceipt(view.EventId) == afterAck);
                Check("view_insertion_does_not_change_source_acceptance", ReadBool(ReadDictionary(Publish(view), "acceptedUnit"), "idempotentReplay", false)
                    && Count("conversation_accepted_units", "unit_id", view.EventId) == 1);

                var noOptIn = Unit("view_insertion_without_opt_in");
                var noOptInReceipt = ReadDictionary(Publish(noOptIn), "acceptedUnit");
                var borrowed = CloneDictionary(viewDescriptor); borrowed["unitId"] = noOptIn.EventId;
                Check("view_insertion_without_descriptor_or_old_receipt_is_unknown", ReadDictionary(noOptInReceipt, "viewInsertion") == null
                    && ReadString(Ack(borrowed), "reason", "") == "no_view_insertion_descriptor"
                    && ReadString(Ack(viewDescriptor, "unitId", unit.EventId), "reason", "") == "no_view_insertion_descriptor"
                    && ReadString(Ack(viewDescriptor, "unitId", "accepted_missing_unit"), "reason", "") == "unknown_unit");
                var groupView = Unit("view_insertion_group", group: true); groupView.ViewInsertionRequested = true;
                var silentView = Unit("view_insertion_silence"); silentView.ViewInsertionRequested = true;
                silentView.Reply = ""; silentView.Parsed["reply"] = ""; silentView.Parsed["participation"] = "quiet";
                silentView.EventType = "social_event_turn";
                AdmitConversationInput(campaign, silentView.Payload, generation, "npc");
                Check("view_insertion_skips_group_and_silence", ReadDictionary(ReadDictionary(Publish(groupView), "acceptedUnit"), "viewInsertion") == null
                    && ReadDictionary(ReadDictionary(Publish(silentView), "acceptedUnit"), "viewInsertion") == null);

                foreach (bool group in new[] { false, true })
                {
                    string channel = group ? "party_chat" : "dialogue";
                    var fallback = Unit("ollama_speech_" + channel, effects: true, group: group);
                    Context(fallback);
                    bool preparedDrink = ReadString(ReadDictionary(fallback.Payload, "intoxicationReceipt"), "commitState", "") == "prepared";
                    int drinks = Count("conversation_drinking_turns"), negotiations = Count("conversation_negotiation_turns");
                    int storedMemories = Count("memories");
                    int sceneBefore = Count("conversation_scene_state"), socialBefore = Count("court_social_signal_evidence");
                    int introductionsBefore = Count("identity_evidence");
                    string SceneSnapshot()
                    {
                        using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                        return Json.Serialize(DialogueStore.SelectConversationSceneState(connection, TestDict("id", "npc")));
                    }
                    string sceneStateBefore = SceneSnapshot();
                    const string words = "I am Mira. *She takes a sip.* I agree to those terms.";
                    var delivered = GenerateSinglePassConversation(TestDict("requestType", channel), fallback.Payload, channel,
                        out fallback.DelimitedSpeechOnly, responder: request => LlmProviderService.OllamaConversationFixtureResult(request,
                            "<<<REIGN_SPEECH>>>" + words + "<<<END_REIGN_SPEECH>>>"));
                    fallback.Parsed = TryParseJsonObject(ReadString(delivered, "content", ""));
                    fallback.Reply = ReadString(fallback.Parsed, "reply", "");
                    fallback.ViewInsertionRequested = true;
                    AdmitConversationInput(campaign, fallback.Payload, generation, "npc");
                    var exchange = Publish(fallback);
                    var receipt = ReadDictionary(exchange, "acceptedUnit");
                    Check("ollama_speech_keeps_sources_and_discards_prepared_effects_" + channel,
                        preparedDrink && fallback.DelimitedSpeechOnly && fallback.Reply == words
                        && ReadBool(receipt, "ok", false) && Count("conversation_turns", "event_id", fallback.EventId) >= 2
                        && ReadString(ReadDictionary(receipt, "effects"), "status", "") == "suppressed_by_route"
                        && new[] { "memory", "playerAttention", "npcLives", "communications" }.All(key =>
                            ReadString(ReadDictionary(receipt, key), "status", "") == "speech_only_excluded")
                        && ReadDictionary(ReadDictionary(receipt, "effectPayload"), "conversationAgencyReceipt") == null
                        && ReadDictionary(ReadDictionary(receipt, "effectPayload"), "intoxicationReceipt") == null
                        && Count("conversation_drinking_turns") == drinks && Count("conversation_negotiation_turns") == negotiations
                        && Count("memories") == storedMemories && (group || ReadDictionary(receipt, "viewInsertion") != null));
                    var fallbackReplay = ReadConversationInputReplay(campaign, fallback.Payload, "npc");
                    Check("ollama_speech_preserves_disposition_on_input_replay_" + channel,
                        IsDelimitedSpeechOnlyResponse(fallbackReplay) && !WorldSimulationService.SocialConversationResultAllowsEffects(fallbackReplay)
                        && !fallback.Payload.ContainsKey("conversationAgencyReceipt") && !fallback.Payload.ContainsKey("intoxicationReceipt"));
                    Check("ollama_speech_discards_scene_social_identity_" + channel,
                        new[] { "scene", "viewWardrobe", "social" }.All(key => ReadString(ReadDictionary(receipt, key), "status", "") == "suppressed_by_route")
                        && ReadDictionaryList(receipt, "introductions").Count == 0 && Count("conversation_scene_state") == sceneBefore && SceneSnapshot() == sceneStateBefore
                        && Count("court_social_signal_evidence") == socialBefore && Count("identity_evidence") == introductionsBefore);
                    using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    {
                        string sessionId = ReadString(exchange, "sessionId", "");
                        var turns = DialogueStore.SelectConversationTurns50(connection, TestDict("id", sessionId));
                        var eligible = ConversationTurnsForMechanicalEffects(connection, sessionId, turns);
                        Check("ollama_speech_excluded_from_session_effects_" + channel,
                            turns.Any(row => ReadString(row, "event_id", "") == fallback.EventId && ReadString(row, "role", "") == "npc")
                            && !eligible.Any(row => ReadString(row, "event_id", "") == fallback.EventId && ReadString(row, "role", "") == "npc")
                            && eligible.Any(row => ReadString(row, "event_id", "") == fallback.EventId && ReadString(row, "role", "") == "player"));
                    }
                    if (!group)
                    {
                        var gift = CloneDictionary(ReadDictionary(VerificationService.DefaultActionTestSnapshot(), "payload"));
                        gift["campaignId"] = campaign; gift["timelineId"] = "agency_timeline";
                        gift["sceneTurnId"] = gift["contributionId"] = "ollama_independent_gift";
                        gift["playerText"] = "I give you 100 gold.";
                        var handover = RunPlayerActionTurn(gift, () => TestDict("ok", true, "reply", "I cannot accept your gold.", "conversationExchange", exchange));
                        var queued = ReadDictionaryList(handover, "queuedDialogueActions");
                        Check("ollama_speech_cannot_refuse_an_independent_player_gift", queued.Count == 1
                            && ReadString(ReadDictionary(queued[0], "record"), "authorizationMode", "") == PlayerActionRules.Policy
                            && ReadString(queued[0], "status", "") == "queued");
                    }
                    if (group)
                    {
                        var shared = CloneDictionary(fallback.Payload);
                        shared["mode"] = "party_chat";
                        shared["playerHeroStringId"] = "ollama_identity_listener";
                        string session = "ollama_group_identity";
                        int evidenceBefore = Count("identity_evidence");
                        UpdateSharedGroupConversationState(campaign, shared, fallback.Hero, fallback.HeroName, fallback.Reply,
                            session, fallback.Timestamp, fallback.EventId, allowSelfIntroduction: !fallback.DelimitedSpeechOnly);
                        Check("ollama_group_transcript_without_identity_effect", Count("identity_evidence") == evidenceBefore
                            && Count("group_conversation_contributions", "source_event_id", fallback.EventId) == 1);
                        shared["sceneTurnId"] = "ordinary_group_introduction";
                        UpdateSharedGroupConversationState(campaign, shared, fallback.Hero, fallback.HeroName, fallback.Reply,
                            session, fallback.Timestamp + 1, "ordinary_group_introduction");
                        Check("ordinary_group_introduction_still_witnessed", Count("identity_evidence") > evidenceBefore);
                    }
                    Check("ollama_speech_idempotent_replay_" + channel, ReadBool(ReadDictionary(Publish(fallback), "acceptedUnit"), "idempotentReplay", false));
                    fallback.DelimitedSpeechOnly = false;
                    bool differentContractRefused = false;
                    try { Publish(fallback); }
                    catch (InvalidOperationException) { differentContractRefused = true; }
                    Check("ollama_speech_cannot_replay_as_structured_" + channel, differentContractRefused);
                }
                var staleView = Unit("view_insertion_before_restore"); staleView.ViewInsertionRequested = true;
                var staleDescriptor = ReadDictionary(ReadDictionary(Publish(staleView), "acceptedUnit"), "viewInsertion");
                string beforeRestore = StoredReceipt(staleView.EventId);
                AdvanceMemoryRestoreGeneration(campaign);
                Check("view_insertion_old_restore_generation_never_writes", staleDescriptor != null
                    && ReadString(Ack(staleDescriptor), "disposition", "") == "stale" && StoredReceipt(staleView.EventId) == beforeRestore);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                var stale = Unit("pre_restore");
                GenerateSinglePassConversation(TestDict(), stale.Payload, "dialogue", responder: _ =>
                    TestDict("ok", true, "content", Json.Serialize(stale.Parsed)));
                AdvanceMemoryRestoreGeneration(campaign);
                bool restoreRejected = false;
                try { Publish(stale); } catch (InvalidOperationException) { restoreRejected = true; }
                Check("restore_rejects_entire_pending_unit", restoreRejected && Count("conversation_turns", "event_id", stale.EventId) == 0
                    && Count("events", "event_id", stale.EventId) == 0 && Count("conversation_accepted_units", "unit_id", stale.EventId) == 0);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                var edited = Unit("view_insertion_edited_source"); edited.ViewInsertionRequested = true;
                edited.Payload["timelineId"] = "main";
                var editedExchange = Publish(edited);
                var editedDescriptor = ReadDictionary(ReadDictionary(editedExchange, "acceptedUnit"), "viewInsertion");
                var line = DialogueLine(edited.Timestamp, "npc", edited.HeroName, edited.Reply);
                AttachAcceptedTranscriptSourceIds(null, line, editedExchange);
                string historyPath = CampaignFileService.CharacterFile(campaign, "npc", "history", "dialogue.jsonl");
                try
                {
                    CampaignFileService.AppendJsonLineToPath(historyPath, line);
                    line["text"] = edited.Reply + " Edited.";
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    var issues = CharactersService.ApplyEditorHistory(campaign, "npc", connection,
                        TestDict("dialogue", new[] { line }));
                    var editedAck = editedDescriptor == null ? null : Ack(editedDescriptor);
                    Check("view_insertion_editor_projection_preserves_accepted_source", issues.Count == 0
                        && editedDescriptor != null && ReadString(editedAck, "disposition", "") == "inserted", editedAck);
                }
                finally
                {
                    if (File.Exists(historyPath)) File.Delete(historyPath);
                }
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
