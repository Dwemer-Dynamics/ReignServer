using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunLegacyMemoryRecoveryAssertions()
        {
            if (string.IsNullOrWhiteSpace(CampaignsRootOverride.Value)) throw new InvalidOperationException("Legacy memory recovery requires isolated Verification Lab storage.");
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool ok, object evidence = null) => results.Add(TestDict("caseId", "memory_legacy_" + name, "passed", ok, "summary", name, "data", evidence));
            string campaign = "memory_legacy_" + Guid.NewGuid().ToString("N");
            const string story = "My horse is Tempest. He gets angry with frogs when they will not hold still to be smelled.";
            const string reply = "So Tempest fights frogs. Stone floats when she thinks no one is watching.";
            const string head = "parent_000000000007_1234abcd", fork = "parent_000000000008_1234abcd";
            bool oldOptimization = SaveSyncDisableBackgroundOptimizationForTests;
            SaveSyncDisableBackgroundOptimizationForTests = true;
            var point = TestDict("campaignId", campaign, "savePointId", "old", "timelineId", "parent", "campaignTimeDays", 20d,
                "worldHistorySequence", 7L, "worldHistoryHeadEventId", head);
            var branchPoint = TestDict("campaignId", campaign, "savePointId", "branch", "timelineId", "child", "campaignTimeDays", 21d,
                "worldHistorySequence", 9L, "worldHistoryHeadEventId", "child_000000000009_abcdef12");
            var context = TestDict("timelineId", "child", "worldDay", 21d, "playerHeroStringId", "player", "conversationSessionId", "current");
            try
            {
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, @"INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES('parent',$campaign,'','',7,$head,1,$created,$created);
INSERT INTO conversation_sessions(session_id,campaign_id,npc_id,player_id,status,start_ts,end_ts,participants_json,payload_json)
VALUES('past',$campaign,'npc','player','closed',1,4,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('current',$campaign,'npc','player','open',5,0,'[""npc"",""player""]','{""timelineId"":""child""}');
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,speaker_name,text,world_day,ts,participants_json,payload_json)
VALUES('horse','past','horse',1,'horse','player','player','player',$story,19,1,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('frogs','past','frogs',2,'horse','npc','npc','npc',$reply,19,2,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('secret','past','secret',3,'secret','npc','npc','npc','Private stable password silverfox.',19,3,'[""npc""]','{""timelineId"":""parent""}'),
('sibling','past','sibling',4,'sibling','npc','npc','npc','Tempest was sold in an abandoned future.',19,4,'[""npc"",""player""]','{""timelineId"":""sibling""}');
INSERT INTO beliefs(belief_id,believer_id,claim,ts,payload_json) VALUES('private','npc','I suspect the stablekeeper.',3,'{""timelineId"":""parent""}');
INSERT INTO memories(memory_id,owner_id,ts,summary,known_by_json,visibility,status,payload_json)
VALUES('horse_memory','npc',3,'Tempest gets angry with frogs.','[""npc""]','private','active','{""timelineId"":""parent"",""worldDay"":19}');",
                        TestDict("campaign", campaign, "head", head, "created", DateTimeOffset.UtcNow.AddDays(-1).ToString("o"), "story", story, "reply", reply));
                    ExecuteSql(connection, "INSERT INTO conversation_turn_fts(turn_id,text,speaker,participants,channel) SELECT turn_id,text,speaker_id,participants_json,channel FROM conversation_turns;");
                    foreach (var item in new[] { ("public", 1), ("private", 2), ("changed_receipt", 3), ("changed_native", 4) })
                    {
                        string id = "native_" + item.Item1;
                        ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,source,is_complete,dissemination_class,payload_json,created_utc)
VALUES($id,$campaign,'parent',$sequence,19,'tournament_finished','bannerlord_native',1,$scope,'{}',$created);
INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,role) VALUES($id,0,'player','hero','winner');",
                            TestDict("id", id, "campaign", campaign, "sequence", item.Item2, "scope", item.Item1 == "private" ? "participant_only" : "ordinary", "created", DateTimeOffset.UtcNow.ToString("o")));
                        if (item.Item1 == "private") ExecuteSql(connection, @"INSERT INTO world_history_knowledge_rules(rule_id,event_id,audience_type,audience_id,available_day,acquisition_mode)
VALUES('private_rule',$id,'entity','npc',19,'participant');", TestDict("id", id));
                        ProjectPlayerPerformanceEvent(connection, QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id=$id;", TestDict("id", id)).Single());
                    }
                }
                var registered = SaveSyncRegisterCore(point);
                Check("real_prefork_native_snapshot_registered", ReadBool(registered, "ok", false), registered);
                string oldSchema = ReignPostgreSqlStorage.SnapshotSchemaName(campaign, "old");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, "DROP TABLE " + oldSchema + ".memory_restored_sources; DROP TABLE " + oldSchema + ".memory_restored_source_context; DROP TABLE " + oldSchema + ".character_performance_events;");
                    ExecuteSql(connection, "UPDATE character_performance_events SET outcome='loss' WHERE event_id='native_changed_receipt'; UPDATE world_history_events SET payload_json='{\"altered\":true}' WHERE event_id='native_changed_native';");
                    ExecuteSql(connection, @"UPDATE world_history_timelines SET is_active=0;
INSERT INTO world_history_timelines(timeline_id,campaign_id,parent_timeline_id,fork_event_id,head_sequence,head_event_id,is_active,created_utc,updated_utc)
VALUES('child',$campaign,'parent',$fork,9,'child_000000000009_abcdef12',1,$created,$created);
INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,created_utc)
VALUES('child_000000000009_abcdef12',$campaign,'child',9,20,'test',$created);
INSERT INTO conversation_turns(turn_id,session_id,event_id,turn_order,exchange_id,role,speaker_id,text,world_day,ts,participants_json,payload_json)
VALUES('later_parent','past','later_parent',5,'later','player','player','Abandoned parent future: Tempest was given away.',20,5,'[""npc"",""player""]','{""timelineId"":""parent""}'),
('failed_question','current','failed',1,'failed','player','player','Do you remember Tempest?',20,10,'[""npc"",""player""]','{""timelineId"":""child""}'),
('failed_reply','current','failed',2,'failed','npc','npc','Tempest? I cannot recall him.',20,11,'[""npc"",""player""]','{""timelineId"":""child""}');",
                        TestDict("campaign", campaign, "fork", fork, "created", DateTimeOffset.UtcNow.AddSeconds(1).ToString("o")));
                    Check("pruned_fork_requires_immediate_child_witness", VerifiedLegacyMemoryForkSequence(connection,
                        QuerySql(connection, "SELECT * FROM world_history_timelines WHERE timeline_id='child';").Single()) == 8);
                    ExecuteSql(connection, @"INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,source,is_complete,dissemination_class,payload_json,created_utc)
VALUES('native_after_proof',$campaign,'parent',8,20,'tournament_finished','bannerlord_native',1,'ordinary','{}',$created);
INSERT INTO world_history_entities(event_id,ordinal,entity_id,entity_type,role) VALUES('native_after_proof',0,'player','hero','winner');", TestDict("campaign", campaign, "created", DateTimeOffset.UtcNow.ToString("o")));
                    ProjectPlayerPerformanceEvent(connection, QuerySql(connection, "SELECT * FROM world_history_events WHERE event_id='native_after_proof';").Single());
                }
                Check("old_branch_snapshot_registered", ReadBool(SaveSyncRegisterCore(branchPoint), "ok", false));
                string branchSchema = ReignPostgreSqlStorage.SnapshotSchemaName(campaign, "branch");
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, "DROP TABLE " + branchSchema + ".memory_restored_sources; DROP TABLE " + branchSchema + ".memory_restored_source_context;");
                    Check("preupgrade_parent_sources_invisible", !PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='horse';").Single(), "npc", BuildKnowledgeAccessContext("npc", "player", "", context), connection));
                }
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "branch");
                AdvanceMemoryRestoreGeneration(campaign, branchPoint);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var access = BuildKnowledgeAccessContext("npc", "player", "", context);
                    var original = QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='horse';").Single();
                    Check("prior_failed_recall_remains_historical_speech", ReadString(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='failed_reply';").Single(), "text", "") == "Tempest? I cannot recall him.");
                    Check("legacy_upgrade_recovers_exact_original", PrecisionTurnVisible(original, "npc", access, connection) && ReadString(original, "text", "") == story
                        && ReadString(original, "speaker_id", "") == "player");
                    foreach (string id in new[] { "sibling", "later_parent" })
                        Check("rejects_" + id, !PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id=$id;", TestDict("id", id)).Single(), "npc", access, connection));
                    var outsider = BuildKnowledgeAccessContext("other", "player", "", context);
                    Check("native_projection_recovered_from_old_schema_with_empty_checksums", ReadInt(ReadPlayerPerformanceCounts(connection, "child", "player", 21d, access), "tournamentWins", 0) == 2
                        && ReadInt(ReadPlayerPerformanceCounts(connection, "child", "player", 21d, outsider), "tournamentWins", 0) == 1);
                    Check("native_recovery_does_not_recreate_receipts_or_accept_changed_future", QuerySql(connection, "SELECT * FROM character_performance_events;").Count == 5
                        && QuerySql(connection, "SELECT * FROM memory_restored_sources WHERE source_table='character_performance_events';").Count == 2);
                    var nativeProof = VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Single();
                    string generation = ReadString(QuerySql(connection, "SELECT restore_generation FROM memory_restored_source_context WHERE state_id='current';").Single(), "restore_generation", "");
                    string privateRules = ReadString(QuerySql(connection, "SELECT knowledge_rules_json FROM character_performance_events WHERE event_id='native_private';").Single(), "knowledge_rules_json", "");
                    ExecuteSql(connection, "DELETE FROM memory_restored_sources WHERE source_table='character_performance_events'; UPDATE character_performance_events SET knowledge_rules_json=$rules WHERE event_id='native_private';",
                        TestDict("rules", Json.Serialize(new[] { TestDict("audience_type", "global", "audience_id", "all", "available_day", 19d, "acquisition_mode", "coarse_public_performance") })));
                    RecoverLegacyPerformanceSources(connection, nativeProof, generation);
                    Check("widened_native_receipt_audience_is_not_authority", QuerySql(connection, "SELECT * FROM memory_restored_sources WHERE source_table='character_performance_events';").Count == 1);
                    ExecuteSql(connection, "UPDATE character_performance_events SET knowledge_rules_json=$rules WHERE event_id='native_private';", TestDict("rules", privateRules));
                    RecoverLegacyPerformanceSources(connection, nativeProof, generation);
                    Check("recovered_whisper_keeps_audience", !PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='secret';").Single(), "other", outsider, connection));
                    var belief = QuerySql(connection, "SELECT * FROM beliefs WHERE belief_id='private';").Single();
                    Check("other_memory_store_recovered_with_privacy", KnowledgeRowVisibleToNpc("beliefs", belief, access, connection) && !KnowledgeRowVisibleToNpc("beliefs", belief, outsider, connection));
                    var early = BuildKnowledgeAccessContext("npc", "player", "", context); early.AsOfAcceptedSequence = ReadLong(original, "accepted_sequence", 0) - 1;
                    Check("same_day_sequence_cutoff_survives_recovery", !PrecisionTurnVisible(original, "npc", early, connection));
                    var poisoned = TestDict("mood", "unsettled — a gap in memory where Stone is concerned", "currentPlan", "Continue training; recover the Tempest memory; keep the boundary",
                        "currentCrisis", "Protect political confidence; no memory of any Tempest conversation; preserve the boundary", "lastKnownSettlementId", "town");
                    WriteJsonObject(CharacterFile(campaign, "npc", "state.json"), poisoned);
                    var clean = WithoutRecallAbsenceState(poisoned);
                    Check("old_absence_state_quarantined_without_erasing_other_concerns", ReadString(clean, "mood", "") == "unsettled"
                        && ReadString(clean, "currentPlan", "") == "Continue training; keep the boundary"
                        && ReadString(clean, "currentCrisis", "") == "Protect political confidence; preserve the boundary"
                        && ReadString(poisoned, "currentCrisis", "").Contains("no memory"));
                    var rejected = new List<Dictionary<string, object>>();
                    var writes = NormalizeSafeConversationStateUpdates(TestDict("currentCrisis", "no memory of any Tempest conversation", "mood", "playful"), rejected);
                    Check("failed_recall_cannot_persist_absence", !writes.ContainsKey("currentCrisis") && ReadString(writes, "mood", "") == "playful"
                        && rejected.Any(r => ReadString(r, "reason", "") == "unsupported_recall_absence"));
                    Check("unrelated_negative_fact_and_memory_intention_preserved", ReadString(WithoutRecallAbsenceState(TestDict("currentCrisis", "No supplies left; protect the memory of my father")), "currentCrisis", "") == "No supplies left; protect the memory of my father");
                    var badComprehension = TestDict("comprehension_id", "gap", "owner_id", "npc", "text", "She has no memory of anything called Tempest.");
                    var badAssertion = TestDict("assertion_id", "gap", "assertion_kind", "comprehension", "claim", "She has no memory of anything called Tempest.");
                    Check("existing_mental_absence_and_assertion_excluded", !KnowledgeRowVisibleToNpc("comprehension", badComprehension, access, connection)
                        && !MemoryTimeVisible(badAssertion, access, connection));
                    var reportedAbsence = TestDict("memory_id", "reported", "summary", "She said she has no memory of Tempest.");
                    string reportedBefore = CanonicalJson(reportedAbsence);
                    Check("derived_absence_memory_quarantined_without_mutation", IsUnsupportedRecallAbsenceDerived(reportedAbsence)
                        && CanonicalJson(reportedAbsence) == reportedBefore);
                    var mentalRejections = new List<Dictionary<string, object>>();
                    Check("future_mental_absence_write_rejected", FilterRoleplayFeedbackWrites(new() { TestDict("text", "She has no memory of Tempest.") }, "comprehension", mentalRejections).Count == 0);
                    foreach (string memoryMode in new[] { "legacy", "precision" })
                    foreach (string query in new[] { "Do you remember Tempest?", "My horse, remember? He met Stone." })
                    foreach (string route in new[] { "in_person", "party_chat", "social_event", "correspondence" })
                    {
                        var payload = PromptParityPayload(route, false); foreach (var field in context) payload[field.Key] = field.Value;
                        payload["officialMemoryFirewall"] = false; payload["mode"] = route;
                        payload["playerText"] = query;
                        var settings = TestDict("enableSemanticMemory", false, "minimeEnabled", false, "memoryPrecisionMode", memoryMode, "useMemoryLlmForConsolidation", false);
                        var packet = BuildNpcMemoryPacket(campaign, "npc", "player", "", query, 8000, payload, settings);
                        string memory = ReadString(packet, "memoryPacket", "");
                        if (packet.TryGetValue("precisionEvidence", out var precisionEvidence)) payload["memoryPrecisionEvidence"] = precisionEvidence;
                        var profile = PromptParityProfile(); profile["heroStringId"] = "npc";
                        var lines = new List<Dictionary<string, object>>(); var empty = TestDict();
                        var motive = TestDict("sanitizedState", clean, "prompt", "Discussing horses.", "relationshipNpcToTarget", empty, "relationshipNpcToSpouse", empty);
                        var envelope = route == "correspondence"
                            ? BuildCorrespondencePromptEnvelope(campaign, "npc", "Corein", "player", "Player", 21d, query, profile, PromptParityCharacteristics(), empty, memory, payload)
                            : route == "in_person"
                                ? BuildDialoguePromptEnvelope(campaign, "npc", "Corein", "Player", "Player", query, "On the road.", profile,
                                    PromptParityCharacteristics(), poisoned, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), payload, memory, "")
                                : BuildEventPromptEnvelope(campaign, "fixture", "npc", "Corein", "Player", "Player", query, "On the road.", payload, profile,
                                    PromptParityCharacteristics(), poisoned, empty, empty, lines, lines, lines, lines, PromptParityIdentity(), memory, "", motive);
                        string actual = Json.Serialize(envelope.Messages);
                        Check("serialized_recall_" + route + "_" + memoryMode + "_" + (query.Contains("Tempest") ? "name" : "horse"), actual.Contains(story) && actual.Contains(reply)
                            && !actual.Contains("no memory of any Tempest") && !actual.Contains("recover the Tempest memory") && !actual.Contains("given away"), TestDict("characters", actual.Length));
                    }
                    ExecuteSql(connection, "UPDATE conversation_turns SET text='Changed horse source.' WHERE turn_id='horse';");
                    Check("changed_source_cannot_reuse_legacy_membership", !PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='horse';").Single(), "npc", access, connection));
                    ExecuteSql(connection, "UPDATE conversation_turns SET text=$story WHERE turn_id='horse';", TestDict("story", story));
                    ExecuteSql(connection, "UPDATE world_history_events SET sequence=10 WHERE timeline_id='child';");
                    Check("missing_immediate_witness_fails_closed", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    ExecuteSql(connection, "INSERT INTO world_history_events(event_id,campaign_id,timeline_id,sequence,world_day,event_type,created_utc) VALUES($fork,$campaign,'parent',8,20,'test',$created);",
                        TestDict("fork", fork, "campaign", campaign, "created", DateTimeOffset.UtcNow.ToString("o")));
                    Check("retained_fork_is_independent_boundary_proof", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 1);
                    ExecuteSql(connection, "DELETE FROM world_history_events WHERE event_id=$fork;", TestDict("fork", fork));
                    ExecuteSql(connection, "UPDATE world_history_events SET sequence=9 WHERE timeline_id='child';");
                    var beforeProof = VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Single();
                    ExecuteSql(connection, "UPDATE reign_meta.save_sync_snapshots SET created_utc=now()+interval '1 day' WHERE campaign_id=$campaign AND save_point_id='old';", TestDict("campaign", campaign));
                    Check("postfork_snapshot_cannot_supply_old_authority", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    ExecuteSql(connection, "UPDATE reign_meta.save_sync_snapshots SET created_utc=to_timestamp($epoch) WHERE campaign_id=$campaign AND save_point_id='old';",
                        TestDict("campaign", campaign, "epoch", ReadDouble(beforeProof, "createdEpoch", 0d)));
                    var ledger = ReadSaveSyncLedger(campaign);
                    var storedPoint = SaveSyncPoint(ledger, "old");
                    string previousFingerprint = ReadString(storedPoint, "snapshotFingerprint", "");
                    storedPoint["snapshotFingerprint"] = "invalid"; WriteSaveSyncLedger(campaign, ledger);
                    Check("manifest_fingerprint_mismatch_fails_closed", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    storedPoint["snapshotFingerprint"] = previousFingerprint;
                    storedPoint["worldHistoryHeadEventId"] = "wrong"; WriteSaveSyncLedger(campaign, ledger);
                    Check("native_and_database_heads_must_match", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    storedPoint["worldHistoryHeadEventId"] = head; WriteSaveSyncLedger(campaign, ledger);
                }
                InitializeVerifiedSnapshotMemorySources(campaign, branchPoint, "branch");
                using (var connection = OpenCampaignConnection(campaign))
                    Check("verified_noop_upgrade_also_recovers_old_sources", PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='horse';").Single(), "npc", BuildKnowledgeAccessContext("npc", "player", "", context), connection));
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "second");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "second");
                AdvanceMemoryRestoreGeneration(campaign, new Dictionary<string, object>(branchPoint) { ["savePointId"] = "second" });
                using (var connection = OpenCampaignConnection(campaign))
                    Check("second_restore_retains_recovered_history", PrecisionTurnVisible(QuerySql(connection, "SELECT * FROM conversation_turns WHERE turn_id='horse';").Single(), "npc", BuildKnowledgeAccessContext("npc", "player", "", context), connection));
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); results.Last()["summary"] = "Legacy memory recovery exception: " + LimitText(ex.ToString(), 2200); }
            finally
            {
                SaveSyncDisableBackgroundOptimizationForTests = oldOptimization;
                ReignPostgreSqlStorage.DropCampaign(campaign);
                TryDeleteDirectory(SaveSyncCampaignRoot(campaign));
                TryDeleteDirectory(StrictCampaignDirectory(campaign));
            }
            results.AddRange(RunMemoryRecallRecoveryAssertions());
            return results;
        }
    }
}
