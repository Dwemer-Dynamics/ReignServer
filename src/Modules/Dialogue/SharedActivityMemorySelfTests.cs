using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void RunSharedActivityMemorySelfTests(Action<string, bool, object> add)
        {
            string campaign = "shared_activity_test_" + Guid.NewGuid().ToString("N");
            const string timeline = "activity_timeline";
            Func<string, string, Dictionary<string, object>> hero = (id, role) => TestDict("entityId", id,
                "entityType", "hero", "name", id == "player" ? "Traveler" : id, "role", role, "isPlayer", id == "player");
            Func<string, string, double, Dictionary<string, object>> evt = (id, type, day) => TestDict("eventId", id,
                "eventType", type, "worldDay", day, "sequence", (long)(day * 1000), "source", "bannerlord_native",
                "phase", type.EndsWith("_started", StringComparison.Ordinal) ? "started" : "completed",
                "locationId", "town_pravend", "locationName", "Pravend", "summary", type + " at Pravend",
                "entities", new List<Dictionary<string, object>> { hero("player", "participant"), hero("npc_a", "participant") },
                "payload", TestDict("playerHeroId", "player"));
            Action<string, bool, object> check = (id, pass, data) => add("shared_activity_" + id, pass, data);
            var arrival = evt("activity_arrival", "settlement_entered", 20d);
            var win = evt("activity_team_win", "tournament_finished", 20.25d);
            win["source"] = "arena_overhaul";
            win["entities"] = new List<Dictionary<string, object>> { hero("player", "tournament winner actor"), hero("npc_a", "tournament winner teammate actor") };
            var battle = evt("activity_looters", "battle_completed", 21d);
            battle["payload"] = TestDict("playerHeroId", "player", "winningSide", "Attacker", "endedByRetreat", false, "battleType", "FieldBattle");
            ReadDictionaryList(battle, "entities").ForEach(e => e["side"] = "attacker");
            ((List<Dictionary<string, object>>)battle["entities"]).Add(TestDict("entityId", "looter_party", "entityType", "party", "name", "Looters", "role", "defender party loser", "quantity", 18));
            var initial = new List<Dictionary<string, object>> { arrival, win, battle };
            var ingest = WorldHistoryIngestBatchApi(TestDict("campaignId", campaign, "timelineId", timeline, "events", initial));
            check("canonical_ingestion", ReadBool(ingest, "ok", false), ingest);
            using (var connection = OpenCampaignConnection(campaign))
            {
                EnsureSharedActivityMemorySchema(connection);
                EnsureConversationContinuitySchema(connection);
                EnsureCategorizedMemorySchema(connection);
                var payload = TestDict("timelineId", timeline, "worldDay", 22d, "playerHeroStringId", "player", "playerText", "Thank you for the cloak.");
                var state = TestDict("currentPlan", "Travel to Pravend and enter the tournament.", "currentCrisis", "Still worried about my father.");
                string prompt = BuildProtectedConversationContinuity(campaign, "npc_a", payload, state);
                check("unrelated_turn_receives_arrival_win_and_looters", prompt.Contains("activity_arrival") && prompt.Contains("activity_team_win")
                    && prompt.Contains("Looters") && prompt.Contains("Still worried about my father"), ReadDictionary(payload, "sharedActivityEvidence"));
                check("team_win_is_personal_but_battle_roster_is_not_fighting", prompt.Contains("your role=participant")
                    && prompt.Contains("your role=accompanying_party_member") && prompt.Contains("defeat does not mean every enemy died"), null);
                check("completed_arrival_repair_is_narrow", SharedActivityReplyContradictions("What shall we do when we reach Pravend?", payload).Any()
                    && !SharedActivityReplyContradictions("When we next reach Pravend, shall we visit the market?", payload).Any()
                    && !SharedActivityReplyContradictions("I remember what you said when we reach Pravend.", payload).Any(), null);
                check("absent_and_late_joiner_do_not_inherit", !BuildProtectedConversationContinuity(campaign, "npc_absent", payload, state).Contains("activity_team_win"), null);
                var futurePayload = new Dictionary<string, object>(payload) { ["worldDay"] = 19d };
                check("rewind_filters_future_activity", !BuildProtectedConversationContinuity(campaign, "npc_a", futurePayload, state).Contains("activity_team_win"), null);
                var otherTimeline = new Dictionary<string, object>(payload) { ["timelineId"] = "other_timeline" };
                check("timeline_isolation", !BuildProtectedConversationContinuity(campaign, "npc_a", otherTimeline, state).Contains("activity_team_win"), null);
                foreach (var item in initial) ProjectSharedActivity(connection, timeline, item);
                check("duplicate_retry_is_idempotent", LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).Count == 3, null);
                var changedRetry = evt("activity_arrival", "settlement_entered", 20d);
                ((List<Dictionary<string, object>>)changedRetry["entities"]).Add(hero("npc_absent", "participant"));
                check("changed_retry_cannot_grant_attendance", !ProjectSharedActivity(connection, timeline, changedRetry)
                    && LoadSharedActivities(connection, timeline, "player", "npc_absent", 22d).Count == 0, null);
                var forged = evt("unsupported_rumor", "tournament_finished", 21d); forged["source"] = "rumor";
                check("rumor_cannot_become_confirmed_activity", !ProjectSharedActivity(connection, timeline, forged), null);
                var mission = evt("actual_mission", "battle_completed", 21.5d);
                ReadDictionaryList(mission, "entities").First(e => ReadString(e, "entityId", "") == "npc_a")["missionParticipant"] = true;
                ProjectSharedActivity(connection, timeline, mission);
                check("mission_evidence_refines_participation", LoadSharedActivities(connection, timeline, "player", "npc_a", 22d)
                    .Any(r => ReadString(r, "activity_id", "") == "actual_mission" && ReadString(r, "observer_role", "") == "participant"), null);

                var root = TestDict("id", "travel_root", "kind", "agenda", "subject", "Visit family", "status", "open", "worldDay", 19d,
                    "activityStep", TestDict("kind", "arrival", "targetId", "town_pravend", "mechanicalCompletion", true, "requiredOutcome", "finished"));
                ExecuteSql(connection, @"INSERT INTO conversation_continuity
(record_id,timeline_id,owner_id,peer_id,kind,status,importance,revision,source_event_id,source_turn_id,updated_ts,payload_json)
VALUES('travel_root',$timeline,'npc_a','player','agenda','open',0.8,1,'accepted_plan','turn_plan',1,$json);",
                    TestDict("timeline", timeline, "json", Json.Serialize(root)));
                var resolved = ReconcileSharedActivitySteps(connection, timeline, "npc_a", LoadSharedActivities(connection, timeline, "player", "npc_a", 22d));
                var rootRow = QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='travel_root';").Single();
                check("arrival_completes_step_without_emotional_closure", resolved.Count == 1 && ReadString(rootRow, "status", "") == "open"
                    && ReadString(ReadDictionary(TryParseJsonObject(ReadString(rootRow, "payload_json", "")), "activityStep"), "status", "") == "completed", rootRow);
                check("completed_step_not_revised_again", ReconcileSharedActivitySteps(connection, timeline, "npc_a", LoadSharedActivities(connection, timeline, "player", "npc_a", 22d)).Count == 0, null);
                var newTrip = new Dictionary<string, object>(root) { ["worldDay"] = 21.75d };
                check("old_arrival_cannot_complete_new_trip", !SharedActivityMatchesStep(newTrip, LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).First(r => ReadString(r, "kind", "") == "arrival")), null);
                var started = evt("battle_start_only", "battle_started", 22d); ProjectSharedActivity(connection, timeline, started);
                var step = TestDict("worldDay", 19d, "activityStep", TestDict("kind", "battle", "targetId", "town_pravend", "mechanicalCompletion", true, "requiredOutcome", "finished"));
                check("complete_payload_flag_is_not_activity_completion", !SharedActivityMatchesStep(step,
                    LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).First(r => ReadString(r, "activity_id", "") == "battle_start_only")), null);
                check("quest_outcomes_preserve_failure", SharedActivityOutcome("quest_completed", TestDict("detail", "Failed")) == "Failed"
                    && SharedActivityOutcome("siege_ended", TestDict()).Contains("unproven"), null);
                var compound = TestDict("worldDay", 19d, "activityStep", TestDict("kind", "battle", "targetId", "looter_party", "mechanicalCompletion", true, "requiredOutcome", "win", "requiredCount", 3));
                var looters = LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).First(r => ReadString(r, "activity_id", "") == "activity_looters");
                check("one_battle_cannot_complete_three_battle_hunt", !SharedActivityMatchesStep(compound, looters), null);
                ReadDictionary(compound, "activityStep")["requiredCount"] = 1;
                check("native_winning_side_proves_single_looter_victory", SharedActivityMatchesStep(compound, looters), null);
                var trade = evt("grain_trade", "item_sold", 21d);
                trade["entities"] = new List<Dictionary<string, object>> { hero("player", "buyer participant"), hero("npc_a", "seller participant"),
                    TestDict("entityId", "grain", "entityType", "item", "quantity", 4) };
                ProjectSharedActivity(connection, timeline, trade);
                var purchase = TestDict("worldDay", 19d, "activityStep", TestDict("kind", "trade", "targetId", "town_pravend", "mechanicalCompletion", true,
                    "requiredOutcome", "success", "eventType", "item_sold", "requiredPlayerRole", "buyer participant", "itemId", "grain", "requiredQuantity", 5));
                var tradeRow = LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).First(r => ReadString(r, "activity_id", "") == "grain_trade");
                check("partial_purchase_does_not_close_quantity", !SharedActivityMatchesStep(purchase, tradeRow), null);
                ReadDictionary(purchase, "activityStep")["requiredQuantity"] = 4;
                check("exact_purchase_terms_complete", SharedActivityMatchesStep(purchase, tradeRow), null);
                ReadDictionary(purchase, "activityStep")["requiredPlayerRole"] = "seller participant";
                check("buying_is_not_selling", !SharedActivityMatchesStep(purchase, tradeRow), null);
                var failedQuest = evt("quest_failure", "quest_completed", 22d); failedQuest["payload"] = TestDict("playerHeroId", "player", "questId", "quest_a", "detail", "Failed");
                ProjectSharedActivity(connection, timeline, failedQuest);
                var questStep = TestDict("worldDay", 19d, "activityStep", TestDict("kind", "quest", "targetId", "quest_a", "mechanicalCompletion", true, "requiredOutcome", "success"));
                check("failed_quest_does_not_satisfy_success_promise", !SharedActivityMatchesStep(questStep,
                    LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).First(r => ReadString(r, "activity_id", "") == "quest_failure")), null);

                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,channel,status,start_ts,start_world_day,participants_json,payload_json)
VALUES('real_chat',$campaign,'npc_a','player','in_person','open',1,10,'[""player"",""npc_a""]',$json);", TestDict("campaign", campaign, "json", Json.Serialize(TestDict("timelineId", timeline))));
                InsertConversationTurn(connection, "real_chat_player", "real_chat", "real_exchange", 0, "real_exchange", "player", "player", "Traveler", "How are you?", "in_person", 10d, 1, "", new List<string> { "player", "npc_a" }, TestDict("timelineId", timeline));
                InsertConversationTurn(connection, "real_chat_npc", "real_chat", "real_exchange", 1, "real_exchange", "npc", "npc_a", "Speaker", "I am well, thank you.", "in_person", 10d, 1, "", new List<string> { "player", "npc_a" }, TestDict("timelineId", timeline));
                for (int i = 0; i < 12 * 24; i++)
                {
                    double from = 10d + i / 24d, until = 10d + (i + 1) / 24d;
                    ProjectRoutineContact(connection, timeline, "presence_" + i, "player", until,
                        TestDict("fromDay", from, "partyId", "main_party", "eligibleHeroIds", new[] { "player", "npc_a" }));
                }
                var intervals = QuerySql(connection, "SELECT * FROM shared_travel_contact WHERE owner_id='npc_a';");
                check("twelve_day_journey_coalesces_with_exact_bounds", intervals.Count == 1 && ReadDouble(intervals.Single(), "start_day", 0d) == 10d
                    && ReadDouble(intervals.Single(), "end_day", 0d) == 22d, intervals);
                string routine = BuildProtectedConversationContinuity(campaign, "npc_a", payload, state);
                check("routine_does_not_replace_explicit_chat", routine.Contains("12 campaign days ago") && routine.Contains("Modeled routine, not a recorded conversation")
                    && routine.Contains("Do not infer neglect"), null);
                string routineAgain = BuildProtectedConversationContinuity(campaign, "npc_a", payload, state);
                check("routine_detail_stable_across_reopen", routine == routineAgain, null);
                ProjectRoutineContact(connection, timeline, "unknown_weeks", "player", 40d, TestDict("fromDay", 22d, "partyId", "main_party", "eligibleHeroIds", new[] { "player", "npc_a", "npc_absent" }));
                check("unobserved_time_not_backfilled", QuerySql(connection, "SELECT * FROM shared_travel_contact;").Count == 1, null);
                ProjectRoutineContact(connection, timeline, "reunion", "player", 24.04d, TestDict("fromDay", 24d, "partyId", "main_party", "eligibleHeroIds", new[] { "player", "npc_a" }));
                check("separation_creates_distinct_interval", QuerySql(connection, "SELECT * FROM shared_travel_contact;").Count == 2, null);
                InsertConversationTurn(connection, "only_new_input", "real_chat", "pending_exchange", 2, "pending_exchange", "player", "player", "Traveler", "When did we last talk?", "in_person", 22d, 2, "", new List<string> { "player", "npc_a" }, TestDict("timelineId", timeline));
                InsertConversationTurn(connection, "empty_attempt", "real_chat", "failed_exchange", 3, "failed_exchange", "npc", "npc_a", "Speaker", "", "in_person", 22d, 3, "", new List<string> { "player", "npc_a" }, TestDict("timelineId", timeline));
                InsertConversationTurn(connection, "delivered_letter", "real_chat", "letter_exchange", 4, "letter_exchange", "npc", "npc_a", "Speaker", "A letter from afar.", "correspondence", 22d, 4, "", new List<string> { "player", "npc_a" }, TestDict("timelineId", timeline));
                check("current_input_failure_and_letter_do_not_reset_spoken_chat", ReadString(LastExplicitSharedChat(connection, timeline, "player", "npc_a", 22d), "turn_id", "") == "real_chat_npc", null);
                check("last_chat_unknown_before_source", LastExplicitSharedChat(connection, timeline, "player", "npc_a", 9d) == null, null);
                var pendingSync = new Dictionary<string, object>(payload) { ["sharedActivitySync"] = TestDict("complete", false) };
                check("pending_sync_does_not_assert_old_plan_unfinished", BuildProtectedConversationContinuity(campaign, "npc_a", pendingSync, state).Contains("SYNCHRONIZATION IS INCOMPLETE"), null);
                check("unverified_sync_acknowledgement_is_incomplete", !SharedActivitySyncComplete(connection, timeline,
                    TestDict("complete", true, "timelineId", timeline, "eventIds", new[] { "missing_native_event" })), null);
                BuildProtectedConversationContinuity(campaign, "npc_a", payload, state);
                MarkSharedActivitiesDelivered(connection, payload, "npc_a", "accepted_delivery", "We won together.");
                check("outcome_coverage_advances_only_on_accepted_reply", LoadSharedActivities(connection, timeline, "player", "npc_a", 22d).Any(a => ReadInt(a, "delivered", 0) == 1), null);
                var official = new Dictionary<string, object>(payload) { ["officialMemoryFirewall"] = true };
                check("official_firewall_retains_private_activity_boundary", !BuildProtectedConversationContinuity(campaign, "npc_a", official, state).Contains("activity_team_win"), null);

                // Raw history can expire without erasing personal outcome capsules.
                ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id='activity_team_win';");
                check("retention_keeps_personal_milestone", BuildProtectedConversationContinuity(campaign, "npc_a", payload, state).Contains("activity_team_win"), null);
                var restored = new Dictionary<string, object>(payload) { ["worldDay"] = 15d };
                check("rewind_does_not_show_future_routine_or_outcomes", !BuildProtectedConversationContinuity(campaign, "npc_a", restored, state).Contains("activity_looters")
                    && !BuildProtectedConversationContinuity(campaign, "npc_a", restored, state).Contains("Modeled routine, not a recorded conversation"), null);
                check("obsolete_restore_generation_rejected", !SharedActivityGenerationMatches(connection, TestDict("sharedActivityGeneration", "obsolete_generation")), null);
            }
            RunSharedActivityEnvelopeTests(campaign, timeline, check);
            using (var connection = OpenCampaignConnection(campaign))
            {
                for (int i = 0; i < 100; i++) ProjectSharedActivity(connection, timeline, evt("routine_arrival_" + i, "settlement_entered", 23d + i / 100d));
                var dense = TestDict("timelineId", timeline, "worldDay", 24d, "playerHeroStringId", "player", "playerText", "Do you remember the tournament?");
                string prompt = BuildProtectedConversationContinuity(campaign, "npc_a", dense, TestDict("currentPlan", "Enter the tournament at Pravend"));
                check("routine_flood_does_not_bury_milestone", prompt.Contains("activity_team_win"), null);
            }
            RunSharedActivityRestoreTest(check);
        }

        private static void RunSharedActivityRestoreTest(Action<string, bool, object> check)
        {
            string campaign = "shared_activity_restore_" + Guid.NewGuid().ToString("N");
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    EnsureSharedActivityMemorySchema(connection);
                    ProjectRoutineContact(connection, "main", "before_save", "player", 10.04d,
                        TestDict("fromDay", 10d, "partyId", "main_party", "eligibleHeroIds", new[] { "player", "npc_a" }));
                }
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "activity_before");
                string beforeGeneration;
                using (var connection = OpenCampaignConnection(campaign))
                {
                    beforeGeneration = SharedActivityGeneration(connection);
                    ProjectRoutineContact(connection, "main", "after_save", "player", 10.08d,
                        TestDict("fromDay", 10.04d, "partyId", "main_party", "eligibleHeroIds", new[] { "player", "npc_a" }));
                    ProjectSharedActivity(connection, "main", TestDict("eventId", "future_victory", "eventType", "tournament_finished",
                        "worldDay", 11d, "source", "bannerlord_native", "entities", new[] { TestDict("entityId", "player", "entityType", "hero", "isPlayer", true) }));
                }
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "activity_before");
                AdvanceMemoryRestoreGeneration(campaign);
                using (var connection = OpenCampaignConnection(campaign))
                    check("real_schema_restore_rewinds_contact_and_activity", QuerySql(connection, "SELECT * FROM shared_activity_events;").Count == 0
                        && Math.Abs(ReadDouble(QuerySql(connection, "SELECT * FROM shared_travel_contact;").Single(), "end_day", 0d) - 10.04d) < 0.000001d
                        && !SharedActivityGenerationMatches(connection, TestDict("sharedActivityGeneration", beforeGeneration)), null);
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }

        private static void RunSharedActivityEnvelopeTests(string campaign, string timeline, Action<string, bool, object> check)
        {
            var profile = PromptParityProfile(); profile["heroStringId"] = "npc_a";
            var empty = TestDict(); var noLines = new List<Dictionary<string, object>>();
            foreach (string memoryMode in new[] { "legacy", "precision" })
            foreach (string mode in new[] { "in_person", "party_chat", "social_event", "correspondence" })
            {
                var payload = PromptParityPayload(mode, false);
                payload["officialMemoryFirewall"] = false; payload["timelineId"] = timeline;
                payload["playerHeroStringId"] = "player"; payload["worldDay"] = 22d; payload["mode"] = mode;
                if (memoryMode == "precision") payload["memoryPrecisionEvidence"] = TestDict("mode", "precision");
                const string input = "Do you like the cloak?";
                var motive = TestDict("sanitizedState", empty, "prompt", "An unrelated gift.", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                PromptEnvelope envelope = mode == "correspondence"
                    ? BuildCorrespondencePromptEnvelope(campaign, "npc_a", "Speaker", "player", "Traveler", 22d, input, profile, PromptParityCharacteristics(), empty, "", payload)
                    : mode == "in_person"
                        ? BuildDialoguePromptEnvelope(campaign, "npc_a", "Speaker", "Traveler", "Traveler", input, "On the road.", profile, PromptParityCharacteristics(), empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), payload, "", "")
                        : BuildEventPromptEnvelope(campaign, "fixture_event", "npc_a", "Speaker", "Traveler", "Traveler", input, "On the road.", payload, profile, PromptParityCharacteristics(), empty, empty, empty, noLines, noLines, noLines, noLines, PromptParityIdentity(), "", "", motive);
                string final = Json.Serialize(envelope.Messages);
                check("final_serialized_" + memoryMode + "_" + mode, final.Contains("activity_team_win") && final.Contains("activity_looters")
                    && final.Contains("12 campaign days ago") && final.Contains(input)
                    && ReadBool(ReadDictionary(envelope.Diagnostics, "continuityPreflight"), "requiredSourceCoverageComplete", false), envelope.Diagnostics);
            }
        }
    }
}
