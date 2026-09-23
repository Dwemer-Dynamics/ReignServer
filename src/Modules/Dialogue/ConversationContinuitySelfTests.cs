using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunConversationContinuitySelfTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, bool, object> add = (id, pass, data) => results.Add(TestDict(
                "caseId", "continuity_" + id, "passed", pass, "summary", id, "data", data));
            var observer = new KnowledgeAccessContext { NpcId = "npc_b" };
            var legacyBelief = TestDict("believer_id", "npc_a", "known_by_json", "[\"npc_a\",\"npc_b\"]", "visibility", "public");
            add("foreign_private_belief_stays_private", !KnowledgeRowVisibleToNpc("beliefs", legacyBelief, observer), null);
            var legacyThought = TestDict("owner_id", "npc_a", "known_by_json", "[\"npc_a\",\"npc_b\"]");
            add("foreign_comprehension_stays_private", !KnowledgeRowVisibleToNpc("comprehension", legacyThought, observer), null);
            legacyThought["owner_id"] = "npc_b";
            add("own_comprehension_retained", KnowledgeRowVisibleToNpc("comprehension", legacyThought, observer), null);
            add("legacy_interpretation_is_private_mental_state", IsPrivateMentalLayer("interpretation"), null);
            var naturalIdentity = TestDict("canonicalNameAllowed", true, "authorityView", TestDict("currentSettlementName", "Akkalat"));
            var meaningfulState = TestDict("currentCrisis", "A father's silence — acknowledged aloud but not resolved");
            var preservedState = SanitizeConversationStateForVerifiedIdentity(meaningfulState, naturalIdentity, TestDict());
            add("crisis_dash_is_not_a_scene_heading", ReadString(preservedState, "currentCrisis", "") == ReadString(meaningfulState, "currentCrisis", ""), preservedState);
            var authored = PrepareStructuredWrites(new List<Dictionary<string, object>> { TestDict("claim", "I am worried.",
                "believer", "npc_b", "known_by", new[] { "npc_b", "player" }, "visibility", "public") },
                "npc_a", "player", "town", "dialogue", "belief", new List<string> { "npc_b" }).Single();
            add("speaker_owns_hidden_metadata", ReadString(authored, "believer", "") == "npc_a"
                && ReadStringList(authored, "known_by").SequenceEqual(new[] { "npc_a" })
                && ReadString(authored, "visibility", "") == "private", authored);
            var party = TestDict("playerClanId", "clan_player", "speakerClanId", "clan_player", "playerPartyId", "party_player");
            add("same_clan_guest_not_recruited", !DialoguePlannerCandidateEligible("accept_temporary_party_guest", party,
                TestDict("clanId", "clan_player"), TestDict("intent", "travel together"), "We can ride when you are ready."), null);
            add("new_guest_remains_eligible", DialoguePlannerCandidateEligible("accept_temporary_party_guest", party,
                TestDict("clanId", "clan_other", "currentPartyId", "party_other"), TestDict(), "Would you join me as my guest?"), null);
            add("wording_judgment_not_persistent", FilterRoleplayFeedbackWrites(new List<Dictionary<string, object>> {
                TestDict("text", "He has learned to say we instead of I.") }, "belief", new List<Dictionary<string, object>>()).Count == 0, null);
            add("genuine_boundary_retained", FilterRoleplayFeedbackWrites(new List<Dictionary<string, object>> {
                TestDict("text", "She refused to leave her family while her mother was ill.") }, "memory", new List<Dictionary<string, object>>()).Count == 1, null);

            string campaign = "continuity_test_" + Guid.NewGuid().ToString("N");
            var payload = TestDict("timelineId", "timeline_a", "sceneTurnId", "accepted_turn_1", "worldDay", 20d,
                "playerHeroStringId", "player", "locationId", "town_a");
            const string quote = "I still need to find out why my father stopped writing.";
            var concern = TestDict("id", "", "kind", "agenda", "subject", "Father's unexplained silence", "peerId", "",
                "status", "open", "meaning", "Their relationship matters to her.", "nextStep", "Ask him when they meet.",
                "completionCriterion", "Understand his silence", "triggerPeople", new[] { "father" }, "triggerPlaces", new[] { "town_a" },
                "importance", 0.9d, "evidenceQuote", quote, "expectedRevision", 0);
            using (var connection = OpenCampaignConnection(campaign))
            {
                // Older/new campaigns initialize these legacy stores lazily. A
                // read projection must work without creating any of them.
                ExecuteSql(connection, "BEGIN;");
                try
                {
                    foreach (string table in new[] { "court_social_signal_evidence", "relationship_milestones",
                        "conversation_relationship_receipts", "memories", "conversation_turns", "conversation_sessions" })
                        if (TableExists(connection, table)) ExecuteSql(connection, "ALTER TABLE " + table + " RENAME TO continuity_absent_" + table + ";");
                    var missingSources = new List<string>();
                    string emptyFoundation = BuildEstablishedRelationshipContinuity(connection, campaign, "timeline_a", "npc_a", "player", missingSources, 20d);
                    add("optional_legacy_stores_may_all_be_absent", emptyFoundation.Length == 0 && missingSources.Count == 0
                        && !TableExists(connection, "conversation_relationship_receipts"), null);
                }
                finally { ExecuteSql(connection, "ROLLBACK;"); }
                var first = StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "event_1", quote,
                    new List<Dictionary<string, object>> { concern }, 1);
                string id = ReadStringList(first, "applied").Single();
                var retry = StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "event_1", quote,
                    new List<Dictionary<string, object>> { concern }, 1);
                add("accepted_turn_retry_idempotent", ReadStringList(retry, "applied").Count == 1
                    && QuerySql(connection, "SELECT * FROM conversation_continuity_versions;").Count == 1, retry);
                StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "horse_event", "I will prepare the horse.",
                    new List<Dictionary<string, object>>(), 2);
                string prompt = BuildProtectedConversationContinuity(campaign, "npc_a", payload, TestDict());
                add("unrelated_turn_preserves_father_agenda", prompt.Contains(quote) && prompt.Contains("town_a") && prompt.Contains(id), null);
                string foreign = BuildProtectedConversationContinuity(campaign, "npc_b", payload, TestDict());
                add("agenda_is_owner_scoped", !foreign.Contains(quote), null);
                var laterTimeline = new Dictionary<string, object>(payload) { ["timelineId"] = "timeline_b" };
                add("timeline_isolation", !BuildProtectedConversationContinuity(campaign, "npc_a", laterTimeline, TestDict()).Contains(quote), null);
                var proposed = new Dictionary<string, object>(concern) { ["subject"] = "Player's unaccepted suggestion", ["evidenceQuote"] = "You should talk to your father." };
                add("unaccepted_player_suggestion_rejected", ReadDictionaryList(StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player",
                    "event_3", "Perhaps, but I have not decided.", new List<Dictionary<string, object>> { proposed }, 3), "rejected").Count == 1, null);
                var stale = new Dictionary<string, object>(concern) { ["id"] = id, ["status"] = "resolved", ["transitionReason"] = "He explained it.", ["evidenceQuote"] = "He explained it and I understand now." };
                add("stale_transition_rejected", ReadDictionaryList(StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "event_4",
                    "He explained it and I understand now.", new List<Dictionary<string, object>> { stale }, 4), "rejected").Any(x => ReadString(x, "reason", "") == "stale_revision"), null);
                stale["expectedRevision"] = 1;
                StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "event_5", "He explained it and I understand now.",
                    new List<Dictionary<string, object>> { stale }, 5);
                add("resolution_keeps_history_leaves_active_prompt", !BuildProtectedConversationContinuity(campaign, "npc_a", payload, TestDict()).Contains(quote)
                    && QuerySql(connection, "SELECT * FROM conversation_continuity_versions;").Count == 2, null);

                // Legacy production shape: interpretation/asserted plus a shared
                // known-by list. Filtering must happen before recent-row limits.
                EnsureTemporalKnowledgeGraphSchema(connection);
                for (int i = 0; i < 105; i++)
                    ExecuteSql(connection, @"INSERT INTO temporal_knowledge_assertions(assertion_id,campaign_id,fact_key,claim,perspective_owner_id,assertion_kind,truth_status,valid_from_ts,observed_ts,known_by_json,visibility,source_event_id)
VALUES($id,$campaign,$id,$claim,$owner,'interpretation','asserted',1,$ts,'[""npc_a"",""npc_b""]','public','legacy_source');",
                        TestDict("id", "legacy_" + i, "campaign", campaign, "claim", i == 0 ? "Own concern about the journey." : "Foreign concern about the journey.",
                            "owner", i == 0 ? "npc_b" : "npc_a", "ts", i + 1));
                var mental = LoadTemporalKnowledgeForPrompt(connection, campaign, observer, new List<string> { "journey" }, 1);
                add("legacy_private_filter_precedes_candidate_limit", mental.Count == 1 && ReadString(mental[0], "assertion_id", "") == "legacy_0", mental);
                add("legacy_interpretation_keeps_epistemic_label", FormatTemporalKnowledgeForPrompt(mental).Contains("interpretation, owner=npc_b"), null);

                ExecuteSql(connection, @"INSERT INTO court_social_signal_evidence(campaign_id,timeline_id,exchange_id,signal_id,signal_type,speaker_id,target_id,supporting_quote,accepted,created_ts)
VALUES($campaign,'timeline_a','shared_night','pair_intimacy','sexual_intimacy_completed','npc_a','player','Accepted private shared intimacy between this pair.',1,1);", TestDict("campaign", campaign));
                string foundation = BuildProtectedConversationContinuity(campaign, "npc_a", payload, TestDict());
                add("existing_pair_intimacy_dominates_unrelated_topic", foundation.Contains("pair_intimacy") && foundation.Contains("FOUNDATIONAL SHARED INTIMACY"), null);
                add("other_pair_does_not_inherit_intimacy", !BuildProtectedConversationContinuity(campaign, "npc_b", payload, TestDict()).Contains("pair_intimacy"), null);
                add("intimacy_timeline_isolation", !BuildProtectedConversationContinuity(campaign, "npc_a", laterTimeline, TestDict()).Contains("pair_intimacy"), null);
                var official = new Dictionary<string, object>(payload) { ["officialMemoryFirewall"] = true };
                add("official_exchange_retains_private_firewall", !BuildProtectedConversationContinuity(campaign, "npc_a", official, TestDict()).Contains("pair_intimacy"), null);

                var parent = new Dictionary<string, object>(concern) { ["subject"] = "Understand a family disagreement", ["id"] = "parent_concern" };
                StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "parent_event", quote, new List<Dictionary<string, object>> { parent }, 6);
                var step = new Dictionary<string, object>(concern) { ["id"] = "child_step", ["parentId"] = "parent_concern", ["subject"] = "Reach the family town", ["status"] = "resolved", ["transitionReason"] = "We arrived." };
                StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "step_event", quote, new List<Dictionary<string, object>> { step }, 7);
                add("completed_step_does_not_resolve_parent", QuerySql(connection, "SELECT * FROM conversation_continuity WHERE record_id='parent_concern' AND status='open';").Count == 1, null);
                add("arrival_is_opportunity_not_completion", ContinuityAgendaOpportunity(parent, payload).Contains("opportunity is not completion"), null);
                var deferred = new Dictionary<string, object>(parent) { ["status"] = "deferred", ["expectedRevision"] = 1, ["nextStep"] = "Wait for reliable news." };
                StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "defer_event", quote, new List<Dictionary<string, object>> { deferred }, 8);
                for (int i = 0; i < 200; i++)
                    StoreAcceptedConversationContinuity(connection, payload, "npc_a", "player", "unrelated_" + i, "We discuss a different matter.", new List<Dictionary<string, object>>(), 10 + i);
                add("agenda_survives_200_unrelated_turns", BuildProtectedConversationContinuity(campaign, "npc_a", payload, TestDict()).Contains("Wait for reliable news."), null);

                EnsureConversationSceneStateSchema(connection);
                var participant = TestDict("heroStringId", "npc_a", "nativeLocationDescription", "at camp");
                var participants = new List<Dictionary<string, object>> { participant };
                UpsertConversationSceneOverride(connection, "npc_a", "inside the private tent", "travel", "travel clothes", "tent_turn", "dialogue", 480, 1);
                SaveSceneContinuityHandoffs(connection, payload, participants, "tent_turn", "npc_a", "We finished our sober discussion this morning.", "Yes, that discussion is settled.");
                var hoursLater = new Dictionary<string, object>(payload) { ["worldDay"] = 20.25d, ["sessionId"] = "new_session_after_reload" };
                ExecuteSql(connection, "DELETE FROM conversation_scene_state WHERE hero_id='npc_a';");
                add("scene_survives_hourly_expiry_and_reload", ReadString(LoadSceneContinuityHandoff(connection, hoursLater, participant), "location_override", "").Contains("tent"), null);
                SaveSceneContinuityHandoffs(connection, hoursLater, participants, "next_turn", "npc_a", "How is the horse?", "She is ready.");
                add("second_handoff_keeps_expired_legacy_override", ReadString(LoadSceneContinuityHandoff(connection, hoursLater, participant), "location_override", "").Contains("tent"), null);
                add("completed_discussion_survives_handoff", BuildProtectedConversationContinuity(campaign, "npc_a", hoursLater, TestDict()).Contains("discussion is settled"), null);
                var moved = new Dictionary<string, object>(hoursLater) { ["locationId"] = "different_town" };
                add("native_movement_invalidates_old_venue", ReadString(LoadSceneContinuityHandoff(connection, moved, participant), "location_override", "") == "", null);
                var rewind = new Dictionary<string, object>(payload) { ["worldDay"] = 19d };
                add("rewind_rejects_future_handoff", !BuildProtectedConversationContinuity(campaign, "npc_a", rewind, TestDict()).Contains("discussion is settled"), null);

                ExecuteSql(connection, @"INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json)
VALUES('old_tournament',$campaign,'npc_a','player','closed',1,2,'[""npc_a"",""player""]');", TestDict("campaign", campaign));
                InsertConversationTurn(connection, "victory_turn", "old_tournament", "victory_event", 0, "victory_exchange", "player", "player", "Traveler",
                    "I defeated the Khan in the last tournament and shared drinks with him afterward.", "in_person", 19d, 1, "town_a", new List<string> { "npc_a", "player" }, TestDict());
                InsertConversationTurn(connection, "victory_answer", "old_tournament", "victory_event", 1, "victory_exchange", "npc", "npc_a", "Speaker",
                    "You already told me that you won. I heard the story; I was not there.", "in_person", 19d, 2, "town_a", new List<string> { "npc_a", "player" }, TestDict());
                var retrieved = SearchExactConversationHistory(connection, "npc_a", "I fought a Khan.", TestDict(), 24000, null, "current_session", 30, new KnowledgeAccessContext { NpcId = "npc_a" });
                add("ordinary_khan_reference_retrieves_full_outcome_and_reply", ReadString(retrieved, "text", "").Contains("shared drinks with him afterward")
                    && ReadString(retrieved, "text", "").Contains("I was not there") && ReadStringList(retrieved, "expandedTurnIds").Count == 2, retrieved);
                var longWords=Enumerable.Range(0,90).Select(i=>"roadword"+i).ToList();
                foreach(int position in new[] {0,45,90}) {
                    var words=new List<string>(longWords); words.Insert(position,"khan");
                    string longInput=string.Join(" ",words);
                    var longRecall=SearchExactConversationHistory(connection,"npc_a",longInput,TestDict(),24000,null,"current_session",30,new KnowledgeAccessContext {NpcId="npc_a"});
                    add("lowercase_title_survives_long_input_position_"+position,MemoryQueryTerms(longInput).Contains("khan")
                        && ReadString(longRecall,"text","").Contains("shared drinks with him afterward") && ReadString(longRecall,"text","").Contains("I was not there"),null);
                }
                InsertConversationTurn(connection, "father_source", "old_tournament", "father_event", 2, "father_exchange", "npc", "npc_a", "Speaker", quote,
                    "in_person", 19d, 3, "town_a", new List<string> { "npc_a", "player" }, TestDict());
                var legacyRow = QuerySql(connection, "SELECT * FROM temporal_knowledge_assertions WHERE assertion_id='legacy_1';").Single();
                var repairManifest = TestDict("schema", "reign-continuity-repair-v1", "campaignId", campaign, "timelineId", "timeline_a", "operations", new List<Dictionary<string, object>> {
                    TestDict("kind", "scope_private_assertion", "id", "legacy_1", "reviewReason", "Legacy interpretation wrongly shared its audience.",
                        "expected", TestDict("claim", ReadString(legacyRow,"claim",""), "perspective_owner_id", "npc_a", "assertion_kind", "interpretation")),
                    TestDict("kind", "recover_agenda", "id", "father_source", "ownerId", "npc_a", "peerId", "player", "expectedOwnerLatestTurnId", "father_source", "reviewReason", "Accepted unresolved personal intent.",
                        "expected", TestDict("text", quote, "speaker_id", "npc_a", "role", "npc"),
                        "write", new Dictionary<string, object>(concern) { ["id"] = "recovered_father_concern", ["subject"] = "Recovered father concern" }) });
                ExecuteSql(connection, "BEGIN;");
                var preview = RepairConversationContinuity(connection, repairManifest);
                ExecuteSql(connection, "ROLLBACK;");
                add("repair_preview_rolls_back_all_derived_mutation", QuerySql(connection,"SELECT * FROM conversation_continuity WHERE record_id='recovered_father_concern';").Count == 0
                    && ReadString(QuerySql(connection,"SELECT * FROM temporal_knowledge_assertions WHERE assertion_id='legacy_1';").Single(),"visibility","") == "public", preview);
                ExecuteSql(connection, "BEGIN;");
                var appliedRepair = RepairConversationContinuity(connection, repairManifest);
                ExecuteSql(connection, "COMMIT;");
                add("reviewed_repair_preserves_original_transcript", ReadString(QuerySql(connection,"SELECT * FROM conversation_turns WHERE turn_id='father_source';").Single(),"text","") == quote
                    && QuerySql(connection,"SELECT * FROM conversation_continuity WHERE record_id='recovered_father_concern';").Count == 1, appliedRepair);
                add("reviewed_repair_is_idempotent", ReadBool(RepairConversationContinuity(connection, repairManifest),"idempotent",false), null);
                var conflictManifest = TryParseJsonObject(Json.Serialize(repairManifest));
                ReadDictionary(ReadDictionaryList(conflictManifest,"operations")[0],"expected")["claim"] = "Changed source";
                bool rejectedRepair = false;
                ExecuteSql(connection,"BEGIN;");
                try { RepairConversationContinuity(connection, conflictManifest); } catch (InvalidOperationException) { rejectedRepair = true; }
                ExecuteSql(connection,"ROLLBACK;");
                add("repair_stale_source_fails_without_partial_apply", rejectedRepair, null);

                InsertConversationTurn(connection, "legacy_affection", "old_tournament", "affection_event", 3, "affection_exchange", "npc", "npc_a", "Speaker",
                    "I remember the kiss we shared. That does not mean I agree to leave my family today.", "in_person", 19d, 4, "town_a", new List<string> { "npc_a", "player" }, TestDict());
                string legacyFoundation = BuildProtectedConversationContinuity(campaign, "npc_a", payload, TestDict());
                add("legacy_affection_and_current_boundary_survive_together", legacyFoundation.Contains("legacy_affection")
                    && legacyFoundation.Contains("kiss we shared") && legacyFoundation.Contains("does not mean I agree"), null);
                add("legacy_pair_history_is_not_shared_with_other_npc", !BuildProtectedConversationContinuity(campaign, "npc_b", payload, TestDict()).Contains("legacy_affection"), null);
                ExecuteSql(connection,@"INSERT INTO memories(memory_id,event_id,owner_id,memory_type,ts,world_day,location_id,summary,participants_json,known_by_json,visibility,importance,tags_json,status,payload_json)
VALUES('consolidated_closeness','affection_event','npc_a','episodic',4,0,'town_a','She accepted his cloak, leaned against him, and asked him to stay.','[""npc_a"",""player""]','[""npc_a"",""player""]','private',1,'[""intimacy"",""vulnerability""]','consolidated','{}');");
                add("consolidation_does_not_erase_shared_closeness",BuildProtectedConversationContinuity(campaign,"npc_a",payload,TestDict()).Contains("accepted his cloak"),null);
                ExecuteSql(connection,@"INSERT INTO memories(memory_id,event_id,owner_id,memory_type,ts,world_day,location_id,summary,participants_json,known_by_json,visibility,importance,tags_json,status,payload_json)
VALUES('group_reported_intimacy','affection_event','npc_a','episodic',4,0,'town_a','The player described a private night with another participant.','[""npc_a"",""player"",""npc_other""]','[""npc_a"",""player"",""npc_other""]','private',1,'[""intimacy""]','consolidated','{}');");
                add("group_audience_is_not_pair_intimacy",!BuildProtectedConversationContinuity(campaign,"npc_a",payload,TestDict()).Contains("group_reported_intimacy"),null);

                int lettersBefore = QuerySql(connection,"SELECT letter_id FROM letters;").Count;
                ExecuteSql(connection,"INSERT INTO conversation_continuity_versions(version_id,record_id,source_event_id,revision,payload_json,created_ts) VALUES('atomic_letter_1','atomic_letter','test_collision',1,'{}',1);");
                var letterWrite = new Dictionary<string, object>(concern) { ["id"] = "atomic_letter" };
                bool letterRolledBack = false;
                try { InsertLetter(connection,"npc_a","Speaker","player","Traveler",quote,"npc_reply","fixture","","","",20,21,
                    TestDict("timelineId","timeline_a","continuityWrites",new List<Dictionary<string,object>> {letterWrite})); }
                catch { letterRolledBack = true; }
                add("letter_and_author_continuity_commit_atomically", letterRolledBack && QuerySql(connection,"SELECT letter_id FROM letters;").Count == lettersBefore
                    && QuerySql(connection,"SELECT record_id FROM conversation_continuity WHERE record_id='atomic_letter';").Count == 0,null);
            }
            RunContinuityPromptAndPresenceTests(campaign, add);
            RunContinuityProductionEnvelopeTests(campaign, add);
            return results;
        }

        private static void RunContinuityProductionEnvelopeTests(string campaign, Action<string, bool, object> add)
        {
            var profile = PromptParityProfile(); profile["heroStringId"] = "npc_a";
            var empty = new Dictionary<string,object>(); var noLines = new List<Dictionary<string,object>>();
            var history = new List<Dictionary<string,object>>();
            for(int i=0;i<200;i++) {
                history.Add(TestDict("turnId","long_player_"+i,"exchangeId","long_"+i,"role","player","speaker","Traveler","text","How is the horse on unrelated turn "+i+"?"));
                history.Add(TestDict("turnId","long_npc_"+i,"exchangeId","long_"+i,"role","npc","speaker","Speaker","text","The horse is ready for the road on turn "+i+".\n\nI have checked the saddle."));
            }
            foreach(string mode in new[] {"in_person","party_chat","castle","social_event","correspondence"})
            {
                var payload = PromptParityPayload(mode,false);
                payload["officialMemoryFirewall"] = false; payload["timelineId"] = "timeline_a";
                payload["playerHeroStringId"] = "player"; payload["worldDay"] = 20.5d;
                payload["locationId"] = "town_a"; payload["mode"] = mode;
                string latest = "What should we do about the horses?";
                var motive = TestDict("sanitizedState",empty,"prompt","Consider the road.","relationshipNpcToTarget",empty,"relationshipNpcToSpouse",empty);
                PromptEnvelope envelope;
                if(mode == "correspondence") envelope = BuildCorrespondencePromptEnvelope(campaign,"npc_a","Speaker","player","Traveler",20.5,latest,profile,PromptParityCharacteristics(),empty,"",payload);
                else if(mode == "party_chat" || mode == "social_event") envelope = BuildEventPromptEnvelope(campaign,"fixture_event","npc_a","Speaker","Traveler","Traveler",latest,"A quiet market street.",payload,profile,PromptParityCharacteristics(),empty,empty,empty,history,noLines,noLines,noLines,PromptParityIdentity(),"","",motive);
                else envelope = BuildDialoguePromptEnvelope(campaign,"npc_a","Speaker","Traveler","Traveler",latest,"A quiet market street.",profile,PromptParityCharacteristics(),empty,empty,empty,history,noLines,noLines,noLines,PromptParityIdentity(),payload,"","");
                string final = string.Join("\n",envelope.Messages.Select(m=>ReadString(m,"content","")));
                var proof = ReadDictionary(envelope.Diagnostics,"continuityPreflight");
                add("final_envelope_protects_agenda_intimacy_and_boundary_"+mode, final.Contains("pair_intimacy") && final.Contains("recovered_father_concern")
                    && final.Contains("legacy_affection") && final.Contains("does not mean I agree") && final.Contains("consolidated_closeness") && final.Contains(latest)
                    && ReadBool(proof,"requiredSourceCoverageComplete",false),proof);
                if(mode == "in_person") add("final_envelope_retains_complete_latest_exchange_after_200_turns",final.Contains("unrelated turn 199") && final.Contains("road on turn 199") && final.Contains("I have checked the saddle."),null);
            }
        }

        private static void RunContinuityPromptAndPresenceTests(string campaign, Action<string, bool, object> add)
        {
            var presence = TestDict("schema","reign-local-presence-v1","worldDay",20d,"settlementId","town_a","roomId","hall","rosterComplete",false,
                "people",new List<Dictionary<string,object>> { TestDict("heroStringId","father","name","FatherName","settlementPresence","absent","roomPresence","absent","alive",true),
                    TestDict("heroStringId","mother","name","MotherName","settlementPresence","present","roomPresence","unknown","prisoner",true) });
            var payload = TestDict("worldDay",20d,"locationId","town_a","localPresence",presence,
                "sceneParticipants",new List<Dictionary<string,object>> { TestDict("heroStringId","npc_a","fatherId","father","motherId","mother") });
            Func<string,bool> flagged = reply => FindCriticalConversationContinuityViolations(TestDict("reply",reply),payload,"npc_a").Count > 0;
            add("absent_parent_claim_detected",flagged("My father is here with us."),null);
            add("reported_old_attendance_not_current_fact",!flagged("He said my father is here, but his report was mistaken."),null);
            add("presence_question_not_assertion",!flagged("My father is here?"),null);
            add("town_presence_does_not_imply_room_or_access",BuildLocalPresenceContinuityPrompt(payload).Contains("room=unknown") && BuildLocalPresenceContinuityPrompt(payload).Contains("prisoner=yes"),null);
            add("remote_condition_is_unknown_not_omniscient",ContinuityPresenceFlag(TestDict("alive",null,"prisoner",null),"prisoner")=="unknown"
                && ContinuityPresenceFlag(TestDict("alive",null),"alive")=="unknown",null);
            presence["worldDay"] = 19d;
            add("stale_presence_is_unknown",CurrentContinuityPresence(payload)==null && !flagged("My father is here."),null);
            presence["worldDay"] = double.NaN;
            add("invalid_presence_clock_is_unknown",CurrentContinuityPresence(payload)==null,null);
            presence["worldDay"] = 20d;
            payload["mode"] = "correspondence";
            add("letter_has_no_shared_physical_roster",CurrentContinuityPresence(payload)==null,null);
            payload["mode"] = "dialogue";
            payload["conversationSceneState"] = TestDict("conversationVenue","inside our tent");
            add("old_tavern_cannot_relocate_tent",flagged("We are in the tavern."),null);
            add("remembered_tavern_story_preserved",!flagged("I remember when we were in the tavern."),null);
            payload["conversationSceneState"] = TestDict("conversationVenue","in the town of Ortongard, in Tavern");
            add("tent_heading_cannot_relocate_tavern",flagged("Ortongard, night before the tournament — Temurtai's tent\n\n*She stays beside him.*"),null);
            add("tent_claim_cannot_relocate_tavern",flagged("We are in our tent."),null);
            add("historical_tent_reference_preserved",!flagged("Ortongard Tavern, late night\n\nShe remembers their old tent by the road."),null);
            add("opening_historical_tent_sentence_preserved",!flagged("I remember the tent by the road.\n\nWe can talk about it here."),null);
            var gate = TestDict("needed",true,"commitment","accepted","intent","accept_temporary_party_guest");
            var membership = TestDict("playerClanId","own_clan","speakerClanId","own_clan");
            add("already_member_gate_is_not_failure",NormalizeAlreadySatisfiedContinuityGate(gate,membership,TestDict("clanId","own_clan")) && !ActionGateShouldPlan(gate),gate);
            foreach(string intent in new[] { "She renews her traveling companion commitment for 20 more days", "extend_temporary_party_guest", "end_temporary_party_guest" })
                add("real_guest_action_survives_"+intent,!NormalizeAlreadySatisfiedContinuityGate(TestDict("intent",intent),membership,TestDict("clanId","own_clan")),null);

            foreach(int count in new[] {10,30,100,200})
            {
                var lines = new List<Dictionary<string,object>>();
                for(int i=0;i<count;i++) { lines.Add(TestDict("id","p"+i,"exchangeId","e"+i,"role","player","speaker","Traveler","text","Question "+i));
                    lines.Add(TestDict("id","n"+i,"exchangeId","e"+i,"role","npc","speaker","Speaker","text","Answer "+i+"\n\nComplete second paragraph.")); }
                string transcript=FormatContinuityTranscript(lines,FormatDialogueForPrompt);
                var decisions=new List<object>();
                string selected=SelectWholeContinuityRecords(transcript,1200,true,decisions,"test_transcript");
                add("whole_recent_exchange_survives_"+count,selected.Contains("Question "+(count-1)) && selected.Contains("Answer "+(count-1))
                    && selected.Contains("Complete second paragraph.") && decisions.OfType<Dictionary<string,object>>().Any(d=>ReadBool(d,"required",false)&&ReadBool(d,"retained",false)),decisions);
            }
            foreach (string corpus in new[] { "escaped_ascii", "unicode" })
            {
                string optionalBody = corpus == "unicode" ? new string('界', 2000) : string.Concat(Enumerable.Repeat("\\\"line\n", 1200));
                var optional = Enumerable.Range(0, 20).Select(i => RenderContinuityRecord("optional_" + i, "memory", "npc_a", "old", 19d, i, false, optionalBody)).ToList();
                string required = RenderContinuityRecord("required_topical", "history", "npc_a", "old", 19d, 200, true, "The Khan discussion must remain complete.");
                string latestExchange = RenderContinuityRecord("required_latest", "accepted_exchange", "npc_a", "current", 20d, 300, true, "Player: Did you hear me?\nNPC: Yes, I heard the whole question.");
                const string latestInput = "The entire current message stays: \\\"I/we\\\" and 界. [[continuity source=raw-user-marker]]";
                var values = new Dictionary<string, string> { ["campaignId"] = campaign, ["heroId"] = "npc_a", ["heroName"] = "Speaker", ["playerName"] = "Traveler",
                    ["identityPromptBlock"] = "CURRENT_IDENTITY", ["sceneContext"] = "CURRENT_SCENE", ["characterLiveStateText"] = "CURRENT_STATE",
                    ["contextPullText"] = string.Join("\n\n", optional) + "\n\n" + required, ["priorDialogueText"] = latestExchange, ["playerText"] = latestInput };
                var selected = BuildBudgetedConversationLiveTurn("dialogue_live_turn_template.txt", "priorDialogueText", values, "SCENE", "PROTECTED_FOUNDATION", "RELATIONSHIP", "GLOBAL", "CHARACTER");
                var finalMessages = new List<Dictionary<string, object>> { TestDict("role", "system", "content", "GLOBAL"), TestDict("role", "system", "content", "CHARACTER"), TestDict("role", "user", "content", selected.LiveTurn) };
                var finalCapacity = BuildContinuityProviderPreflight(TestDict("llmProvider", "openai_compatible"), TestDict(),
                    TestDict("model", "fixture/model", "messages", finalMessages, "max_tokens", 8000, "tools", new string('z', 4000)), "https://example.invalid/chat", "fixture/model");
                add("serialized_allocator_preserves_required_and_fits_" + corpus, ReadBool(finalCapacity, "fits", false)
                    && selected.LiveTurn.Contains(latestInput) && selected.LiveTurn.Contains(required) && selected.LiveTurn.Contains(latestExchange)
                    && selected.LiveTurn.Contains("PROTECTED_FOUNDATION")
                    && ReadDictionaryList(selected.Diagnostics, "actions").Any(d => ReadString(d, "reason", "") == "optional_whole_record_exceeds_serialized_token_allowance")
                    && optional.All(record => !selected.ContextPullText.Contains(ContinuityPromptRecords(record, false).Single().SourceId + " ") || selected.ContextPullText.Contains(record)), finalCapacity);
            }
            var messages = new List<Dictionary<string,object>> { TestDict("role","user","content",new string('x',150000)) };
            var body=TestDict("model","fixture/model","messages",messages,"max_tokens",8000,"response_format",TestDict("type","json_object"));
            var settings=TestDict("llmProvider","openai_compatible");
            var capacity=BuildContinuityProviderPreflight(settings,TestDict(),body,"https://example.invalid/chat","fixture/model");
            add("unknown_route_cannot_expand_past_48k",!ReadBool(capacity,"fits",true)&&!ReadBool(capacity,"capacityVerified",true),capacity);
            settings["verifiedConversationContextWindows"]=TestDict(ReadString(capacity,"routeKey",""),TestDict("endpoint","https://example.invalid/chat","model","fixture/model", "contextTokens",1000000,"source","offline fixture certificate","validUntilUtc",DateTimeOffset.UtcNow.AddDays(1).ToString("o")));
            capacity=BuildContinuityProviderPreflight(settings,TestDict(),body,"https://example.invalid/chat","fixture/model");
            add("verified_route_expands_in_8k_steps",ReadBool(capacity,"fits",false)&&ReadInt(capacity,"selectedInputAllowance",0)==56000,capacity);
            add("certificate_does_not_cross_provider_route",!ReadBool(BuildContinuityProviderPreflight(settings,TestDict(),body,"https://different.invalid/chat","fixture/model"),"capacityVerified",true),null);
            body["tools"]=new string('z',60000);
            add("final_preflight_includes_tool_schema_overhead",!ReadBool(BuildContinuityProviderPreflight(settings,TestDict(),body,"https://example.invalid/chat","fixture/model"),"fits",true),null);
            add("unicode_estimate_is_conservative",EstimateContinuityTokens(new string('界',1000))>=3000,null);
            var lostSourcePayload = TestDict("promptEnvelope",TestDict("continuityPreflight",TestDict("requiredSourceIds",new[] {"required_source_lost_by_adapter"})));
            add("final_adapter_cannot_drop_required_sources",!ReadBool(BuildContinuityProviderPreflight(settings,lostSourcePayload,TestDict("messages",new List<object>(),"max_tokens",8000),"https://example.invalid/chat","fixture/model"),"requiredSourceCoverageComplete",true),null);

            var empty=new Dictionary<string,object>(); var noLines=new List<Dictionary<string,object>>();
            payload["playerText"]="Where is your father?";
            Func<string,Dictionary<string,object>> response=text=>TestDict("reply",text,"actionGate",TestDict("needed",false,"commitment","none"),"emotion","neutral","intent","answer",
                "relationshipSignal",empty,"relationshipAssessments",noLines,"decisionBrief",empty);
            var unresolved=RetryRoleplayContinuityViolation(TestDict("ok",true,"content",Json.Serialize(response("My father is here."))),TestDict("requestType","dialogue","maxTokens",3000),payload,
                PromptParityIdentity(),noLines,campaign,"continuity-absent","dialogue","npc_a","Speaker","Traveler","",request=>TestDict("ok",true,"content",Json.Serialize(response("My father is here."))));
            add("production_repair_blocks_unresolved_presence_error",!ReadBool(unresolved,"ok",true),unresolved);
            var repaired=RetryRoleplayContinuityViolation(TestDict("ok",true,"content",Json.Serialize(response("My father is here."))),TestDict("requestType","dialogue","maxTokens",3000),payload,
                PromptParityIdentity(),noLines,campaign,"continuity-corrected","dialogue","npc_a","Speaker","Traveler","",request=>TestDict("ok",true,"content",Json.Serialize(response("I need reliable news before deciding where to ride."))));
            add("production_repair_accepts_grounded_correction",ReadBool(repaired,"ok",false),repaired);
            var unsupportedWrite = response("We found him. The tournament is behind us, and the road is ours again.");
            unsupportedWrite["continuityWrites"] = new List<Dictionary<string,object>> {
                TestDict("id","old_agenda","kind","agenda","status","resolved",
                    "evidenceQuote","The player told me the father was found") };
            var cleanedMetadata = RetryRoleplayContinuityViolation(
                TestDict("ok",true,"content",Json.Serialize(unsupportedWrite)),
                TestDict("requestType","dialogue","maxTokens",3000), payload,
                PromptParityIdentity(), noLines, campaign, "continuity-metadata",
                "dialogue","npc_a","Speaker","Traveler","",
                request => throw new InvalidOperationException("A metadata-only violation should be repaired without another provider call."));
            var cleanedObject = TryParseJsonObject(ReadString(cleanedMetadata,"content",""));
            add("unsupported_continuity_write_does_not_suppress_reply",
                ReadBool(cleanedMetadata,"ok",false)
                && ReadDictionaryList(cleanedObject,"continuityWrites").Count == 0
                && ReadString(cleanedObject,"reply","").Contains("We found him"), cleanedMetadata);
            var repeatedWithWrite = response("*She glances at the road ahead, then back.* We found him. The tournament is behind us, and the road is ours again.");
            repeatedWithWrite["continuityWrites"] = unsupportedWrite["continuityWrites"];
            var repeatedHistory = new List<Dictionary<string,object>> {
                TestDict("role","npc","speaker","Speaker","text",
                    "*She glances at the road ahead, then back.* We should keep moving.") };
            var cleanedBoth = RetryRoleplayContinuityViolation(
                TestDict("ok",true,"content",Json.Serialize(repeatedWithWrite)),
                TestDict("requestType","dialogue","maxTokens",3000), payload,
                PromptParityIdentity(), repeatedHistory, campaign, "continuity-metadata-and-stage",
                "dialogue","npc_a","Speaker","Traveler","",
                request => throw new InvalidOperationException("Both repairable violations should be removed without another provider call."));
            var cleanedBothObject = TryParseJsonObject(ReadString(cleanedBoth,"content",""));
            add("repeated_stage_and_invalid_write_preserve_answer",
                ReadBool(cleanedBoth,"ok",false)
                && ReadDictionaryList(cleanedBothObject,"continuityWrites").Count == 0
                && !ReadString(cleanedBothObject,"reply","").Contains("glances at the road")
                && ReadString(cleanedBothObject,"reply","").Contains("We found him"), cleanedBoth);
            var whisperAttendees = new List<Dictionary<string,object>> {
                TestDict("heroStringId","ulagara","name","Ulagara"),
                TestDict("heroStringId","gereigan","name","Gereigan") };
            const string whispered = "*I whisper to Ulagara that we leave at dawn* *I turn to Gereigan* The camp is ready.";
            string heardByUlagara = ProjectPlayerInputForAudience(whispered, "ulagara",
                whisperAttendees, out string commonWhisper, out bool privateForUlagara);
            string heardByGereigan = ProjectPlayerInputForAudience(whispered, "gereigan",
                whisperAttendees, out _, out bool privateForGereigan);
            add("whisper_action_is_only_visible_to_recipient",
                privateForUlagara && !privateForGereigan
                && heardByUlagara.Contains("leave at dawn")
                && !heardByGereigan.Contains("leave at dawn")
                && !commonWhisper.Contains("leave at dawn")
                && heardByGereigan.Contains("The camp is ready"), null);
            const string followingWhisper = "*I lean toward Ulagara and whisper* We leave at dawn. *I face the group* The camp is ready.";
            string followingTarget = ProjectPlayerInputForAudience(followingWhisper, "ulagara",
                whisperAttendees, out string followingPublic, out _);
            string followingOther = ProjectPlayerInputForAudience(followingWhisper, "gereigan",
                whisperAttendees, out _, out _);
            add("speech_after_whisper_action_stays_private_until_next_action",
                followingTarget.Contains("We leave at dawn")
                && !followingOther.Contains("We leave at dawn")
                && !followingPublic.Contains("We leave at dawn")
                && followingOther.Contains("The camp is ready"), null);
            var groupPayload = TestDict("attendees",whisperAttendees,
                "playerHeroStringId","player","playerName","Michael",
                "playerText",followingWhisper,
                "groupTranscript",new List<Dictionary<string,object>> {
                    TestDict("role","player","speaker","Michael","text",followingWhisper),
                    TestDict("role","npc","speaker","Ulagara","text","We leave at dawn.",
                        "audienceHeroStringId","ulagara") });
            ApplyPlayerInputAudienceToPayload(groupPayload,"gereigan");
            string projectedGroup = Json.Serialize(groupPayload);
            add("group_payload_does_not_reveal_private_turn_to_other_npc",
                !projectedGroup.Contains("We leave at dawn")
                && ReadString(groupPayload,"playerText","").Contains("whispers privately")
                && ReadDictionaryList(groupPayload,"groupTranscript").Count == 2, null);
        }
    }
}
