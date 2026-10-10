using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationDrinkSceneSelfTests()
        {
            var rows = new List<Dictionary<string, object>>();
            void Check(string id, bool passed, object actual = null) => rows.Add(TestDict("id", "drink_scene_" + id,
                "name", "drink_scene_" + id, "caseId", "drink_scene_" + id, "passed", passed, "ok", passed,
                "suite", "conversation_intoxication", "durationMs", 0, "actual", actual));
            string campaign = "drink_scene_test_" + Guid.NewGuid().ToString("N");
            var profile = TestDict("name", "Mira", "isFemale", true, "attributes", TestDict("endurance", 15));
            Dictionary<string, object> Payload(string turn, string venue = "Egan the Vintner's Tavern") => TestDict(
                "worldDay", 10d, "timelineId", "main", "turnId", turn, "conversationSessionId", "first-chat",
                "locationId", "town_B1", "playerHeroStringId", "player", "conversationSceneState", TestDict(
                    "conversationVenue", venue, "participants", new List<Dictionary<string, object>> {
                        TestDict("heroStringId", "mira", "name", "Mira"), TestDict("heroStringId", "player", "role", "player"),
                        TestDict("heroStringId", "other", "name", "Other") }));
            Dictionary<string, object> Update(string quote, string holder = "mira", string reference = "my_cup", string beverage = "wine",
                string operation = "establish", int version = 0, string quantity = "unknown") => TestDict(
                    "operation", operation, "vesselId", reference, "holderHeroId", holder, "beverage", beverage,
                    "kind", "cup", "quantity", quantity, "contentsVersion", version, "source", "reply", "supportingQuote", quote);
            Dictionary<string, object> Turn(Dictionary<string, object> payload, Dictionary<string, object> parsed, string hero = "mira", bool persist = true)
            {
                ApplyLegacyConversationDrinking(campaign, hero, payload, profile, null, parsed, ReadString(parsed, "reply", ""), persist);
                return ReadDictionary(payload, "intoxicationReceipt");
            }
            Dictionary<string, object> Scene(Dictionary<string, object> payload)
            {
                BuildIntoxicationPrompt(campaign, "mira", payload, profile, null);
                return ReadDictionary(ReadDictionary(payload, "intoxicationContext"), "drinkScene");
            }
            Dictionary<string, object> Own(Dictionary<string, object> scene, string holder = "mira") =>
                ReadDictionaryList(scene, "vessels").SingleOrDefault(v => ReadString(v, "holderHeroId", "") == holder);
            double Consumed(Dictionary<string, object> receipt) => ReadDouble(receipt, "consumed", -1);
            string Generation()
            {
                using (var connection = OpenCampaignConnection(campaign)) return ReadString(ReadMemoryPrecisionState(connection), "restore_generation", "");
            }
            try
            {
                var payload = Payload("ambient-opening");
                Check("starts_empty", ReadDictionaryList(Scene(payload), "vessels").Count == 0);
                string arrival = "The wine arrives — two clay cups, dark red, nothing fancy. She pushes one toward him and wraps her hand around the other but doesn't drink yet.";
                string opening = "*" + arrival + "*\n\nTell me about your travels.\n\n*She takes a sip — short, testing, the way you test a blade's edge with your thumb before you commit to the swing.*";
                var first = Turn(payload, TestDict("reply", opening));
                var firstScene = Scene(payload);
                string id = ReadString(Own(firstScene), "id", "");
                Check("captured_wine_arrival_then_sip", Consumed(first) == .25 && id.Length > 0
                    && ReadDictionaryList(firstScene, "vessels").Count == 2
                    && ReadString(Own(firstScene), "family", "") == "wine", first);
                Check("distinct_cups_no_player_consumption", ReadString(Own(firstScene, "player"), "id", "") != id
                    && ReadLong(ReadDictionary(first, "state"), "consumptionEvents", -1) == 1, firstScene);
                payload["conversationSessionId"] = "new-chat-with-new-logical-id";
                payload["logicalConversationId"] = "new-logical-id";
                payload["turnId"] = "reopened-sip";
                Scene(payload);
                var held = ReadDrinkVessel(ReadDictionary(ReadDictionary(payload, "intoxicationContext"), "vessel"));
                Check("reopen_retains_exact_cup", held.Id == id && held.Beverage.Family == "wine", DrinkVesselRecord(held));
                var plain = QuietDrinkFixture(held, reply: "I had wondered whether you would come back.");
                var second = Turn(payload, plain);
                Check("plain_speech_hidden_sip_after_reopen", Consumed(second) == .25
                    && ReadDouble(ReadDictionary(second, "state"), "drinks", -1) == .5
                    && !ReadString(second, "reply", "").Contains("*"), second);
                Check("retry_does_not_duplicate", Json.Serialize(second) == Json.Serialize(Turn(payload, QuietDrinkFixture(held))), second);
                payload["turnId"] = "both-channels";
                var both = QuietDrinkFixture(held, reply: "*She takes a sip from her cup.* Tell me more.");
                Check("narrated_and_hidden_once", Consumed(Turn(payload, both)) == .25);
                payload["turnId"] = "no-choice";
                Check("presence_does_not_auto_drink", Consumed(Turn(payload, TestDict("reply", "I agree."))) == 0);
                payload["turnId"] = "player-cup";
                var playerCup = ReadDrinkVessel(Own(Scene(payload), "player"));
                Check("separate_player_cup_unavailable", Consumed(Turn(payload, QuietDrinkFixture(playerCup))) == 0);

                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "drink_scene_checkpoint");
                payload["turnId"] = "leave";
                ReadDictionary(payload, "conversationSceneState")["conversationVenue"] = "the castle hall";
                Check("same_town_different_venue_empty", ReadDictionaryList(Scene(payload), "vessels").Count == 0);
                Turn(payload, TestDict("reply", "Welcome to the hall."));
                ReadDictionary(payload, "conversationSceneState")["conversationVenue"] = "Egan the Vintner's Tavern";
                Check("leave_return_does_not_resurrect", ReadDictionaryList(Scene(payload), "vessels").Count == 0);
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "drink_scene_checkpoint");
                payload["timelineId"] = "restored-branch";
                Check("snapshot_restores_scene_on_new_branch", ReadString(Own(Scene(payload)), "id", "") == id);
                payload["worldDay"] = 10d + 2d / 24d;
                Check("campaign_time_expiry", ReadDictionaryList(Scene(payload), "vessels").Count == 0);
                payload["worldDay"] = 9d;
                payload["turnId"] = "stale-scene";
                string staleQuote = "She holds a full cup of water.";
                var stale = TestDict("reply", staleQuote, "drinkSceneUpdates", new[] { Update(staleQuote, beverage: "water", quantity: "full") });
                Check("stale_clock_cannot_replace_scene", ReadDictionary(Turn(payload, stale), "drinkScene") == null);
                payload["worldDay"] = 10d;
                Check("stale_clock_preserves_committed_inventory", ReadString(Own(Scene(payload)), "id", "") == id);
                payload["worldDay"] = 9d; payload["turnId"] = "stale-new-participant";
                Check("shared_clock_blocks_stale_new_participant", ReadDictionary(Turn(payload, stale, "other"), "drinkScene") == null);
                payload["worldDay"] = 10d;
                Check("stale_new_participant_preserves_inventory", ReadString(Own(Scene(payload)), "id", "") == id);

                // Start each route empty, exercise observations plus action-free
                // consumption through the shared final-acceptance transaction.
                foreach (string mode in new[] { "dialogue", "party_chat", "castle_chat", "social_event" })
                {
                    var p = Payload("route-" + mode, "isolated-" + mode);
                    p["mode"] = mode;
                    p["campaignId"] = campaign; p["heroStringId"] = "mira";
                    p["conversationSessionId"] = "drink_scene_session_" + mode;
                    p["playerText"] = "Tell me about your clan.";
                    using (var connection = OpenCampaignConnection(campaign))
                        ExecuteSql(connection, @"INSERT INTO conversation_sessions
(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES($session,$campaign,'mira','player','open',1,1,'[""mira"",""player""]','{""timelineId"":""main""}');",
                            TestDict("session", ReadString(p, "conversationSessionId", ""), "campaign", campaign));
                    string quote = "The server places a full cup of wine beside Mira.";
                    var parsed = TestDict("reply", quote + " I remember your name.", "drinkSceneUpdates", new[] { Update(quote, quantity: "full") },
                        "drinkConsumptions", new[] { TestDict("vesselId", "my_cup", "contentsVersion", 1, "serving", "sip", "count", 1, "completed", true) });
                    var prepared = Turn(p, parsed, persist: false);
                    Check(mode + "_prepared_not_persisted", Consumed(prepared) == .25 && ReadDictionaryList(Scene(p), "vessels").Count == 0, prepared);
                    var unit = new AcceptedConversationUnit { Campaign = campaign, Hero = "mira", Player = "player", Location = "town_B1",
                        PlayerName = "Player", HeroName = "Mira", EventId = AcceptedConversationEventId(campaign, "mira", p),
                        EventType = mode == "dialogue" ? "dialogue_turn" : "party_chat_turn", PlayerText = ReadString(p, "playerText", ""),
                        Reply = ReadString(parsed, "reply", ""), Timestamp = 20, Generation = Generation(), Payload = p,
                        Motive = TestDict(), Profile = profile, Parsed = parsed, GroupConversation = mode != "dialogue",
                        Memories = new(), Beliefs = new(), Relationships = new(), Obligations = new(), Comprehensions = new(), Continuity = new() };
                    var published = PublishAcceptedConversationUnit(unit, accept => unit.GroupConversation
                        ? StorePartyConversationExchange(campaign, p, "mira", "player", "Player", "Mira", unit.PlayerText, unit.Reply, unit.EventId, unit.Timestamp, accept)
                        : StoreConversationExchange(campaign, p, "mira", "player", "Player", "Mira", unit.PlayerText, unit.Reply, unit.EventId, unit.Timestamp, acceptance: accept));
                    Check(mode + "_accepted_observation_and_hidden_choice", ReadBool(published, "ok", false)
                        && ReadDouble(Own(Scene(p)), "remaining", -1) == .75, published);
                    using (var connection = OpenCampaignConnection(campaign))
                        Check(mode + "_source_and_receipt_published_together", QuerySql(connection,
                            "SELECT turn_id FROM conversation_turns WHERE session_id=$session;", TestDict("session", ReadString(p, "conversationSessionId", ""))).Count == 2
                            && QuerySql(connection, "SELECT unit_id FROM conversation_accepted_units WHERE unit_id=$id;", TestDict("id", unit.EventId)).Count == 1);
                }

                payload = Payload("rollback", "rollback-fixture");
                string service = "The server places a full cup of wine beside Mira.";
                var serviceParsed = TestDict("reply", service, "drinkSceneUpdates", new[] { Update(service, quantity: "full") });
                Turn(payload, serviceParsed, persist: false);
                bool rolledBack = false;
                try { CommitFinalConversationEffects(campaign, "mira", payload, null, profile, null, serviceParsed, service, Generation(),
                    phase => { if (phase == "before_commit") throw new InvalidOperationException("injected rollback"); }); }
                catch (InvalidOperationException) { rolledBack = true; }
                Check("scene_rollback_is_atomic", rolledBack && ReadDictionaryList(Scene(payload), "vessels").Count == 0);
                Turn(payload, serviceParsed, persist: false);
                var corrected = CommitFinalConversationEffects(campaign, "mira", payload, null, profile, null, serviceParsed, "I remember your name.", Generation());
                Check("removed_service_cannot_commit_draft_cup", ReadDictionaryList(Scene(payload), "vessels").Count == 0
                    && Consumed(ReadDictionary(corrected, "drinking")) == 0);
                payload["turnId"] = "cleared-service-metadata";
                Turn(payload, serviceParsed, persist: false);
                serviceParsed["drinkSceneUpdates"] = new List<Dictionary<string, object>>();
                CommitFinalConversationEffects(campaign, "mira", payload, null, profile, null, serviceParsed, service, Generation());
                Check("cleared_metadata_cannot_commit_draft_cup", ReadDictionaryList(Scene(payload), "vessels").Count == 0);

                payload["turnId"] = "establish-for-versions";
                serviceParsed["drinkSceneUpdates"] = new[] { Update(service, quantity: "full") };
                Turn(payload, serviceParsed);
                held = ReadDrinkVessel(Own(Scene(payload)));
                payload["turnId"] = "refill-with-water";
                string refill = "She refills her cup with water.";
                var water = TestDict("reply", refill, "drinkSceneUpdates", new[] { Update(refill, reference: held.Id, beverage: "water", operation: "refill", version: held.ContentsVersion, quantity: "full") });
                Turn(payload, water);
                var refilled = ReadDrinkVessel(Own(Scene(payload)));
                Check("refill_retains_id_advances_contents", refilled.Id == held.Id && refilled.ContentsVersion == held.ContentsVersion + 1
                    && refilled.Beverage.Alcohol == DrinkAlcohol.No, DrinkVesselRecord(refilled));
                payload["turnId"] = "old-version";
                Check("old_revision_rejected", Consumed(Turn(payload, QuietDrinkFixture(held))) == 0);
                payload["turnId"] = "water-sip";
                var waterSip = Turn(payload, QuietDrinkFixture(refilled));
                Check("water_consumes_volume_without_intoxication", Consumed(waterSip) == 0
                    && ReadInt(ReadDictionary(waterSip, "eventAccounting"), "consumptionEvents", 0) == 1);
                payload["turnId"] = "discard";
                string discard = "She discards her cup.";
                Turn(payload, TestDict("reply", discard, "drinkSceneUpdates", new[] { Update(discard, reference: refilled.Id, operation: "discard", version: refilled.ContentsVersion) }));
                Check("discard_removes_availability", Own(Scene(payload)) == null);

                foreach (string invalidQuote in new[] { "She might hold a full cup of wine.", "She remembers holding a full cup of wine.",
                    "She doesn't hold a full cup of wine.", "She offers a full cup of wine.", "She holds a full cup of mystery tonic.",
                    "He says she holds a full cup of wine.", "\"She holds a full cup of wine.\"" })
                {
                    payload["turnId"] = "negative-" + PromptHash(invalidQuote).Substring(0, 8);
                    Turn(payload, TestDict("reply", invalidQuote, "drinkSceneUpdates", new[] { Update(invalidQuote) }));
                    Check("reject_" + ReadString(payload, "turnId", ""), Own(Scene(payload)) == null);
                }
                payload["turnId"] = "foreign-holder";
                Turn(payload, TestDict("reply", service, "drinkSceneUpdates", new[] { Update(service, holder: "absent") }));
                Check("absent_holder_rejected", Own(Scene(payload)) == null);
                payload["turnId"] = "missing-source";
                Turn(payload, TestDict("reply", "Tell me more.", "drinkSceneUpdates", new[] { Update(service) }));
                Check("missing_quote_rejected", Own(Scene(payload)) == null);
                payload["turnId"] = "stripped-memory-context";
                Turn(payload, TestDict("reply", "She remembers yesterday: " + service, "drinkSceneUpdates", new[] { Update(service) }));
                Check("partial_quote_cannot_strip_memory_context", Own(Scene(payload)) == null);
                payload["turnId"] = "future-service-not-retroactive";
                var futureService = TestDict("reply", "*She takes a sip.* *" + service + "*", "drinkSceneUpdates", new[] { Update(service) });
                Check("later_service_cannot_explain_earlier_sip", Consumed(Turn(payload, futureService)) == 0);
                held = ReadDrinkVessel(Own(Scene(payload)));
                payload["turnId"] = "metadata-transfer";
                string transfer = "She hands her cup to you.";
                Turn(payload, TestDict("reply", transfer, "drinkSceneUpdates", new[] { Update(transfer, holder: "player", reference: held.Id, operation: "transfer", version: held.ContentsVersion) }));
                Check("metadata_transfer_preserves_player_custody", ReadString(Own(Scene(payload), "player"), "id", "") == held.Id);
                payload["turnId"] = "metadata-spoken-offer"; payload["playerText"] = "Want another sip?";
                BuildIntoxicationPrompt(campaign, "mira", payload, profile, null);
                var offered = ReadDrinkVessel(ReadDictionary(ReadDictionary(payload, "intoxicationContext"), "availableDrink"));
                Check("metadata_transfer_allows_spoken_return_offer", offered.Id == held.Id && Consumed(Turn(payload, QuietDrinkFixture(offered))) == .25);
                payload.Remove("playerText");

                var wilderness = Payload("wilderness"); wilderness.Remove("locationId"); wilderness["playerPartyId"] = "party";
                wilderness["localPresence"] = TestDict("partyPositionX", 12.25, "partyPositionY", 15.5);
                Check("positioned_wilderness_scene_has_anchor", DrinkSceneScope(wilderness).Length > 0);
                wilderness["localPresence"] = TestDict();
                Check("missing_native_anchor_falls_back_conservatively", DrinkSceneScope(wilderness).Length == 0);

                var multi = Payload("multiple-cup-one", "multiple-cup-scene");
                Turn(multi, TestDict("reply", service, "drinkSceneUpdates", new[] { Update(service, quantity: "full") }));
                multi["turnId"] = "multiple-cup-two";
                string extraService = "The server places another full cup of water beside Mira.";
                Turn(multi, TestDict("reply", extraService, "drinkSceneUpdates", new[] { Update(extraService, reference: "water_cup", beverage: "water", quantity: "full") }));
                BuildIntoxicationPrompt(campaign, "mira", multi, profile, null);
                var selected = ReadDrinkVessel(ReadDictionary(ReadDictionary(multi, "intoxicationContext"), "vessel"));
                Check("multiple_cups_preserve_selected_identity_on_reopen", selected.Beverage.Alcohol == DrinkAlcohol.No
                    && ReadDictionaryList(Scene(multi), "vessels").Count == 2, DrinkVesselRecord(selected));
                multi["turnId"] = "foreign-departure";
                Turn(multi, TestDict("reply", "We are still here.", "sceneStateUpdates", new[] { TestDict("heroStringId", "other", "location", "the hall") }));
                Check("other_participants_unaccepted_move_keeps_scene", ReadDictionaryList(Scene(multi), "vessels").Count == 2);
                multi["turnId"] = "accepted-departure";
                Turn(multi, TestDict("reply", "*She leaves for the hall.*", "sceneStateUpdates", new[] { TestDict("heroStringId", "mira", "location", "the hall") }));
                Check("accepted_departure_ends_inventory", ReadDictionaryList(Scene(multi), "vessels").Count == 0);

                var a = Payload("concurrent-a", "concurrent-scene");
                var b = Payload("concurrent-b", "concurrent-scene");
                var pa = TestDict("reply", service, "drinkSceneUpdates", new[] { Update(service) });
                string otherService = "The server places a full cup of wine beside Other.";
                var pb = TestDict("reply", otherService, "drinkSceneUpdates", new[] { Update(otherService, holder: "other") });
                Turn(a, pa, persist: false); Turn(b, pb, "other", false);
                CommitFinalConversationEffects(campaign, "mira", a, null, profile, null, pa, service, Generation());
                bool staleScene = false;
                try { CommitFinalConversationEffects(campaign, "other", b, null, profile, null, pb, otherService, Generation()); }
                catch (InvalidOperationException) { staleScene = true; }
                Check("concurrent_participant_cas_rejects_stale_scene", staleScene && ReadDictionaryList(Scene(a), "vessels").Count == 1);

                var defaults = DefaultPromptTemplates();
                foreach (var kind in new[] { CodexConversationContractKind.Individual, CodexConversationContractKind.Group,
                    CodexConversationContractKind.Castle, CodexConversationContractKind.Social })
                    Check("schema_" + kind, ReadDictionary(CodexConversationSchemas.Select(kind, defaults, defaults, TestDict()).Schema, "properties").ContainsKey("drinkSceneUpdates"));
                var repaired = TestDict("reply", service);
                PreserveFormatRepairMetadata(pa, repaired);
                Check("format_repair_retains_observations", ReadDictionaryList(repaired, "drinkSceneUpdates").Count == 1);
            }
            catch (Exception ex) { Check("integration", false, ex.ToString()); }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
            return rows;
        }
    }
}
