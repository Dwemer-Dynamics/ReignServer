using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
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
                using (var connection = OpenCampaignConnection(campaign))
                {
                    generation = ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
                    EnsureConversationAgencySchema(connection); EnsureIntoxicationSchema(connection);
                    EnsureAcceptedConversationUnitSchema(connection);
                    EnsureIdentitySchema(connection); EnsureConversationSceneStateSchema(connection);
                    EnsureSocialReputationSchema(connection); EnsureConversationContinuitySchema(connection);
                    ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('accepted_unit_session',$campaign,'npc','player','open',1,1,'[""npc"",""player"",""listener""]','{""timelineId"":""agency_timeline""}');", TestDict("campaign", campaign));
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
                            unit.PrivateWhisper ? "*The speaker replies privately to the player.*" : unit.Reply, unit.EventId, unit.Timestamp, accept)
                        : StoreConversationExchange(campaign, unit.Payload, "npc", "player", "Player", "Mira",
                            unit.PlayerText, unit.Reply, unit.EventId, unit.Timestamp, acceptance: accept), fault);
                int Count(string table, string field = "", string id = "")
                {
                    using var connection = OpenCampaignConnection(campaign);
                    return ReadInt(QuerySql(connection, "SELECT COUNT(*) AS n FROM " + table + (field.Length == 0 ? "" : " WHERE " + field + "=$id") + ";",
                        field.Length == 0 ? null : TestDict("id", id)).Single(), "n", 0);
                }
                bool AcceptedSourcesMatch(AcceptedConversationUnit value, int projectionCount = 2)
                {
                    using var connection = OpenCampaignConnection(campaign);
                    var rows = QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$event;", TestDict("event", value.EventId));
                    var active = rows.Where(row => ReadString(row, "status", "") == "active").ToList();
                    var originals = rows.Where(row => ReadString(row, "status", "") == "source_only").ToList();
                    if (rows.Count != projectionCount + 1 || active.Count != projectionCount || originals.Count != 1
                        || active.Count(row => ReadString(row, "role", "") == "player") != projectionCount / 2
                        || active.Count(row => ReadString(row, "role", "") == "npc") != projectionCount / 2
                        || !PlayerInputSources.TryGetValue(value.Payload, out var captured)) return false;
                    var source = originals.Single();
                    string id = ReadString(source, "turn_id", ""), original = originalInputs[value.Payload];
                    var metadata = TryParseJsonObject(ReadString(source, "payload_json", "{}"));
                    var authors = TextListFromJson(ReadString(source, "participants_json", "[]"));
                    return id == captured.SourceId && captured.Original == original && ReadString(source, "text", "") == original
                        && ReadString(source, "role", "") == "player_source" && ReadString(source, "channel", "") == "player_input_source"
                        && ReadString(source, "speaker_id", "") == value.Player && ReadInt(source, "turn_order", 0) < 0
                        && authors.Count == 1 && authors[0] == value.Player
                        && ReadString(metadata, "sourceHash", "") == PromptHash(original)
                        && ReadString(metadata, "exactTextHash", "") == ConversationExactTextHash(original)
                        && ReadString(metadata, "evidenceType", "") == "original_player_input" && !ReadBool(metadata, "retrievalEligible", true)
                        && new[] { value.Hero, "listener" }.All(observer => !PrecisionTurnVisible(source, observer,
                            BuildKnowledgeAccessContext(observer, value.Player, value.Location, value.Payload), connection))
                        && QuerySql(connection, "SELECT turn_id FROM conversation_turn_fts WHERE turn_id=$id;", TestDict("id", id)).Count == 0;
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

                var privateUnit = Unit("whisper", group: true, whisper: true);
                bool privateFailure = false;
                try { Publish(privateUnit, phase => { if (phase == "after_memory") throw new InvalidOperationException("Private source fault."); }); }
                catch (InvalidOperationException) { privateFailure = true; }
                Check("whisper_public_and_private_sources_rollback_together", privateFailure && Count("conversation_turns", "event_id", privateUnit.EventId) == 0);
                var whisper = Publish(privateUnit);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var rows = QuerySql(connection, "SELECT * FROM conversation_turns WHERE event_id=$id;", TestDict("id", privateUnit.EventId));
                    var access = BuildKnowledgeAccessContext("listener", "player", "", privateUnit.Payload);
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
                using (var connection = OpenCampaignConnection(campaign))
                {
                    foreach (string id in new[] { "npc", "player", "listener" })
                        ExecuteSql(connection, "INSERT INTO identity_roster(hero_id,canonical_name,is_alive,is_adult) VALUES($id,$id,1,1);", TestDict("id", id));
                    UpsertVerifiedIdentity(connection, TestDict("heroStringId", "npc"), TestDict("heroStringId", "player", "name", "Michael"), "fixture", "", 20d, "");
                    UpsertVerifiedIdentity(connection, TestDict("heroStringId", "npc"), TestDict("heroStringId", "listener", "name", "Listener"), "fixture", "", 20d, "");
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
                var preview = NormalizeAndPersistConversationSocialSignals(campaign, contextUnit.Parsed, contextUnit.Payload,
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
                using (var connection = OpenCampaignConnection(campaign))
                {
                    string listener = ReadString(QuerySql(connection, "SELECT payload_json FROM conversation_scene_handoffs WHERE owner_id='listener';").Single(), "payload_json", "");
                    string author = ReadString(QuerySql(connection, "SELECT payload_json FROM conversation_scene_handoffs WHERE owner_id='npc';").Single(), "payload_json", "");
                    Check("scene_handoffs_preserve_author_thought_without_broadcasting", !listener.Contains("SECRET-THOUGHT-901")
                        && listener.Contains("courtyard") && author.Contains("SECRET-THOUGHT-901"));
                }
                var whisperContext = Unit("private_context", group: true, whisper: true); Context(whisperContext);
                whisperContext.Reply += " SECRET-SPOKEN-901."; whisperContext.Parsed["reply"] = whisperContext.Reply;
                var privateContextReceipt = ReadDictionary(Publish(whisperContext), "acceptedUnit");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    string listener = ReadString(QuerySql(connection, "SELECT payload_json FROM conversation_scene_handoffs WHERE owner_id='listener';").Single(), "payload_json", "");
                    Check("private_handoff_and_introduction_do_not_reach_bystander", !listener.Contains("SECRET-SPOKEN-901")
                        && ReadDictionaryList(privateContextReceipt, "introductions").All(item => !ReadBool(item, "ok", true)));
                    var falseIntroducer = CloneDictionary(contextUnit.Parsed);
                    falseIntroducer["identityIntroductions"] = new[] { TestDict("observerId", "player", "subjectId", "npc", "introducerId", "listener",
                        "claimedName", "Michael", "supportingQuote", "Let me introduce Michael.") };
                    var invalid = ApplyIdentityIntroductionWrites(campaign, contextUnit.Payload, falseIntroducer, "test", "npc", "player",
                        contextUnit.PlayerText, contextUnit.Reply, new[] { "npc", "player", "listener" }, connection);
                    Check("another_present_character_is_not_the_introducer", invalid.Count == 1 && !ReadBool(invalid[0], "ok", true));
                    var thought = ApplyIdentityIntroductionWrites(campaign, contextUnit.Payload, contextUnit.Parsed, "test", "npc", "player", "Hello.",
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
                using (var connection = OpenCampaignConnection(campaign)) CaptureContinuityReadSnapshot(connection, losing.Payload, "npc");
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
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "UPDATE conversation_continuity_pending SET status='discarded_test_future';");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "pending_continuity_checkpoint");
                AdvanceMemoryRestoreGeneration(campaign);
                using (var connection = OpenCampaignConnection(campaign))
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
                using (var connection = OpenCampaignConnection(campaign))
                {
                    using var transaction = connection.BeginTransaction();
                    ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed source must not authorize recovery.' WHERE event_id=$event AND speaker_id='npc';",
                        TestDict("event", losing.EventId));
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
                using (var connection = OpenCampaignConnection(campaign))
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
                var stale = Unit("pre_restore");
                AdvanceMemoryRestoreGeneration(campaign);
                bool restoreRejected = false;
                try { Publish(stale); } catch (InvalidOperationException) { restoreRejected = true; }
                Check("restore_rejects_entire_pending_unit", restoreRejected && Count("conversation_turns", "event_id", stale.EventId) == 0
                    && Count("events", "event_id", stale.EventId) == 0 && Count("conversation_accepted_units", "unit_id", stale.EventId) == 0);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
