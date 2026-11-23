using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static List<Dictionary<string, object>> RunLegacyMemoryRecoveryAssertions()
        {
            if (string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)) throw new InvalidOperationException("Legacy memory recovery requires isolated Verification Lab storage.");
            var results = new List<Dictionary<string, object>>();
            void Check(string name, bool ok, object evidence = null) => results.Add(TestDict("caseId", "memory_legacy_" + name, "passed", ok, "summary", name, "data", evidence));
            string campaign = "memory_legacy_" + Guid.NewGuid().ToString("N");
            const string story = "My horse is Tempest. He gets angry with frogs when they will not hold still to be smelled.";
            const string reply = "So Tempest fights frogs. Stone floats when she thinks no one is watching.";
            const string head = "parent_000000000007_1234abcd", fork = "parent_000000000008_1234abcd";
            bool oldOptimization = PersistenceService.SaveSyncDisableBackgroundOptimizationForTests;
            PersistenceService.SaveSyncDisableBackgroundOptimizationForTests = true;
            var point = TestDict("campaignId", campaign, "savePointId", "old", "timelineId", "parent", "campaignTimeDays", 20d,
                "worldHistorySequence", 7L, "worldHistoryHeadEventId", head);
            var branchPoint = TestDict("campaignId", campaign, "savePointId", "branch", "timelineId", "child", "campaignTimeDays", 21d,
                "worldHistorySequence", 9L, "worldHistoryHeadEventId", "child_000000000009_abcdef12");
            var context = TestDict("timelineId", "child", "worldDay", 21d, "playerHeroStringId", "player", "conversationSessionId", "current");
            try
            {
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    DialogueSelfTestStore.InsertWorldHistoryTimelines(connection, TestDict("campaign", campaign, "head", head, "created", DateTimeOffset.UtcNow.AddDays(-1).ToString("o"), "story", story, "reply", reply));
                    DialogueSelfTestStore.InsertConversationTurnFts2(connection);
                    foreach (var item in new[] { ("public", 1), ("private", 2), ("changed_receipt", 3), ("changed_native", 4) })
                    {
                        string id = "native_" + item.Item1;
                        DialogueSelfTestStore.InsertWorldHistoryEvents(connection, TestDict("id", id, "campaign", campaign, "sequence", item.Item2, "scope", item.Item1 == "private" ? "participant_only" : "ordinary", "created", DateTimeOffset.UtcNow.ToString("o")));
                        if (item.Item1 == "private") DialogueSelfTestStore.InsertWorldHistoryKnowledgeRules(connection, TestDict("id", id));
                        ProjectPlayerPerformanceEvent(connection, DialogueSelfTestStore.SelectWorldHistoryEvents2(connection, TestDict("id", id)).Single());
                    }
                }
                var registered = PersistenceService.SaveSyncRegisterCore(point);
                Check("real_prefork_native_snapshot_registered", ReadBool(registered, "ok", false), registered);
                string oldSchema = ReignPostgreSqlStorage.SnapshotSchemaName(campaign, "old");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    DialogueSelfTestSchema.DropMemoryRestoredSources(connection, oldSchema, oldSchema, oldSchema);
                    DialogueSelfTestStore.UpdateCharacterPerformanceEvents2(connection);
                    DialogueSelfTestStore.UpdateWorldHistoryTimelines(connection, TestDict("campaign", campaign, "fork", fork, "created", DateTimeOffset.UtcNow.AddSeconds(1).ToString("o")));
                    Check("pruned_fork_requires_immediate_child_witness", VerifiedLegacyMemoryForkSequence(connection,
                        DialogueSelfTestStore.SelectWorldHistoryTimelines(connection).Single()) == 8);
                    DialogueSelfTestStore.InsertWorldHistoryEvents2(connection, TestDict("campaign", campaign, "created", DateTimeOffset.UtcNow.ToString("o")));
                    ProjectPlayerPerformanceEvent(connection, DialogueSelfTestStore.SelectWorldHistoryEvents3(connection).Single());
                }
                Check("old_branch_snapshot_registered", ReadBool(PersistenceService.SaveSyncRegisterCore(branchPoint), "ok", false));
                string branchSchema = ReignPostgreSqlStorage.SnapshotSchemaName(campaign, "branch");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    DialogueSelfTestSchema.DropMemoryRestoredSources2(connection, branchSchema, branchSchema);
                    Check("preupgrade_parent_sources_invisible", !PrecisionTurnVisible(DialogueSelfTestStore.SelectConversationTurns61(connection).Single(), "npc", DialogueService.BuildKnowledgeAccessContext("npc", "player", "", context), connection));
                }
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "branch");
                AdvanceMemoryRestoreGeneration(campaign, branchPoint);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var access = DialogueService.BuildKnowledgeAccessContext("npc", "player", "", context);
                    var original = DialogueSelfTestStore.SelectConversationTurns61(connection).Single();
                    Check("prior_failed_recall_remains_historical_speech", ReadString(DialogueSelfTestStore.SelectConversationTurns62(connection).Single(), "text", "") == "Tempest? I cannot recall him.");
                    Check("legacy_upgrade_recovers_exact_original", PrecisionTurnVisible(original, "npc", access, connection) && ReadString(original, "text", "") == story
                        && ReadString(original, "speaker_id", "") == "player");
                    foreach (string id in new[] { "sibling", "later_parent" })
                        Check("rejects_" + id, !PrecisionTurnVisible(DialogueSelfTestStore.SelectConversationTurns(connection, TestDict("id", id)).Single(), "npc", access, connection));
                    var outsider = DialogueService.BuildKnowledgeAccessContext("other", "player", "", context);
                    Check("native_projection_recovered_from_old_schema_with_empty_checksums", ReadInt(ReadPlayerPerformanceCounts(connection, "child", "player", 21d, access), "tournamentWins", 0) == 2
                        && ReadInt(ReadPlayerPerformanceCounts(connection, "child", "player", 21d, outsider), "tournamentWins", 0) == 1);
                    Check("native_recovery_does_not_recreate_receipts_or_accept_changed_future", DialogueSelfTestStore.SelectCharacterPerformanceEvents2(connection).Count == 5
                        && DialogueSelfTestStore.SelectMemoryRestoredSources(connection).Count == 2);
                    var nativeProof = VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Single();
                    string generation = ReadString(DialogueSelfTestStore.SelectMemoryRestoredSourceContext2(connection).Single(), "restore_generation", "");
                    string privateRules = ReadString(DialogueSelfTestStore.SelectCharacterPerformanceEvents3(connection).Single(), "knowledge_rules_json", "");
                    DialogueSelfTestStore.DeleteMemoryRestoredSources(connection, TestDict("rules", Json.Serialize(new[] { TestDict("audience_type", "global", "audience_id", "all", "available_day", 19d, "acquisition_mode", "coarse_public_performance") })));
                    RecoverLegacyPerformanceSources(connection, nativeProof, generation);
                    Check("widened_native_receipt_audience_is_not_authority", DialogueSelfTestStore.SelectMemoryRestoredSources(connection).Count == 1);
                    DialogueSelfTestStore.UpdateCharacterPerformanceEvents3(connection, TestDict("rules", privateRules));
                    RecoverLegacyPerformanceSources(connection, nativeProof, generation);
                    Check("recovered_whisper_keeps_audience", !PrecisionTurnVisible(DialogueSelfTestStore.SelectConversationTurns63(connection).Single(), "other", outsider, connection));
                    var belief = DialogueSelfTestStore.SelectBeliefs(connection).Single();
                    Check("other_memory_store_recovered_with_privacy", DialogueService.KnowledgeRowVisibleToNpc("beliefs", belief, access, connection) && !DialogueService.KnowledgeRowVisibleToNpc("beliefs", belief, outsider, connection));
                    var early = DialogueService.BuildKnowledgeAccessContext("npc", "player", "", context); early.AsOfAcceptedSequence = ReadLong(original, "accepted_sequence", 0) - 1;
                    Check("same_day_sequence_cutoff_survives_recovery", !PrecisionTurnVisible(original, "npc", early, connection));
                    var poisoned = TestDict("mood", "unsettled — a gap in memory where Stone is concerned", "currentPlan", "Continue training; recover the Tempest memory; keep the boundary",
                        "currentCrisis", "Protect political confidence; no memory of any Tempest conversation; preserve the boundary", "lastKnownSettlementId", "town");
                    CampaignFileService.WriteJsonObject(CampaignFileService.CharacterFile(campaign, "npc", "state.json"), poisoned);
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
                    Check("existing_mental_absence_and_assertion_excluded", !DialogueService.KnowledgeRowVisibleToNpc("comprehension", badComprehension, access, connection)
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
                        var packet = DialogueService.BuildNpcMemoryPacket(campaign, "npc", "player", "", query, 8000, payload, settings);
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
                    DialogueSelfTestStore.UpdateConversationTurns50(connection);
                    Check("changed_source_cannot_reuse_legacy_membership", !PrecisionTurnVisible(DialogueSelfTestStore.SelectConversationTurns61(connection).Single(), "npc", access, connection));
                    DialogueSelfTestStore.UpdateConversationTurns51(connection, TestDict("story", story));
                    DialogueSelfTestStore.UpdateWorldHistoryEvents(connection);
                    Check("missing_immediate_witness_fails_closed", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    DialogueSelfTestStore.InsertWorldHistoryEvents3(connection, TestDict("fork", fork, "campaign", campaign, "created", DateTimeOffset.UtcNow.ToString("o")));
                    Check("retained_fork_is_independent_boundary_proof", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 1);
                    DialogueSelfTestStore.DeleteWorldHistoryEvents2(connection, TestDict("fork", fork));
                    DialogueSelfTestStore.UpdateWorldHistoryEvents2(connection);
                    var beforeProof = VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Single();
                    DialogueSelfTestStore.UpdateSaveSyncSnapshots(connection, TestDict("campaign", campaign));
                    Check("postfork_snapshot_cannot_supply_old_authority", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    DialogueSelfTestStore.UpdateSaveSyncSnapshots2(connection, TestDict("campaign", campaign, "epoch", ReadDouble(beforeProof, "createdEpoch", 0d)));
                    var ledger = PersistenceService.ReadSaveSyncLedger(campaign);
                    var storedPoint = PersistenceService.SaveSyncPoint(ledger, "old");
                    string previousFingerprint = ReadString(storedPoint, "snapshotFingerprint", "");
                    storedPoint["snapshotFingerprint"] = "invalid"; PersistenceService.WriteSaveSyncLedger(campaign, ledger);
                    Check("manifest_fingerprint_mismatch_fails_closed", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    storedPoint["snapshotFingerprint"] = previousFingerprint;
                    storedPoint["worldHistoryHeadEventId"] = "wrong"; PersistenceService.WriteSaveSyncLedger(campaign, ledger);
                    Check("native_and_database_heads_must_match", VerifiedLegacyMemorySnapshots(connection, campaign, branchPoint).Count == 0);
                    storedPoint["worldHistoryHeadEventId"] = head; PersistenceService.WriteSaveSyncLedger(campaign, ledger);
                }
                InitializeVerifiedSnapshotMemorySources(campaign, branchPoint, "branch");
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    Check("verified_noop_upgrade_also_recovers_old_sources", PrecisionTurnVisible(DialogueSelfTestStore.SelectConversationTurns61(connection).Single(), "npc", DialogueService.BuildKnowledgeAccessContext("npc", "player", "", context), connection));
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "second");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "second");
                AdvanceMemoryRestoreGeneration(campaign, new Dictionary<string, object>(branchPoint) { ["savePointId"] = "second" });
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    Check("second_restore_retains_recovered_history", PrecisionTurnVisible(DialogueSelfTestStore.SelectConversationTurns61(connection).Single(), "npc", DialogueService.BuildKnowledgeAccessContext("npc", "player", "", context), connection));
            }
            catch (Exception ex) { Check("suite_exception", false, ex.ToString()); results.Last()["summary"] = "Legacy memory recovery exception: " + DialogueService.LimitText(ex.ToString(), 2200); }
            finally
            {
                PersistenceService.SaveSyncDisableBackgroundOptimizationForTests = oldOptimization;
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(PersistenceService.SaveSyncCampaignRoot(campaign));
                PersistenceService.TryDeleteDirectory(PersistenceService.StrictCampaignDirectory(campaign));
            }
            results.AddRange(RunMemoryRecallRecoveryAssertions());
            return results;
        }
    }
}
