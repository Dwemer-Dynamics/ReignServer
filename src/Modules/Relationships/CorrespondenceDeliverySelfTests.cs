using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reign.Core.Contracts.Family;

namespace ReignBetaServer
{
    internal static partial class RelationshipsService
    {
        private static List<Dictionary<string, object>> RunCorrespondenceDeliveryAssertions()
        {
            var results = new List<Dictionary<string, object>>();
            void Check(string id, bool pass, object data = null) => results.Add(TestDict("caseId", "courier_delivery_" + id,
                "passed", pass, "summary", id, "data", data));
            string campaign = "courier_delivery_test_" + Guid.NewGuid().ToString("N");
            int composeCalls = 0, acceptCalls = 0;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            Task work = null;
            try
            {
                string outgoing, incoming, second;
                Dictionary<string, object> Dispatch(string sender, string recipient, double day)
                {
                    using var connection = ReignDatabase.OpenCampaignConnection(campaign);
                    return InsertLetter(connection, sender, sender, recipient, recipient,
                        "An accepted complete letter about the western gate.", "player", "fixture", "", "a", "b", day, day + .25d, TestDict());
                }
                outgoing = ReadString(Dispatch("player", "npc", 10d), "letterId", "");
                incoming = ReadString(Dispatch("npc2", "player", 10d), "letterId", "");
                var payload = TestDict("campaignId", campaign, "playerId", "player", "timelineId", "main", "worldDay", 10.1d, "phase", "delivery");
                var first = CorrespondenceTickApi(payload);
                Check("fast_phase_has_no_provider_and_all_arrivals", ReadBool(first, "ok", false)
                    && ReadDictionaryList(first, "deliveredLetters").Count == 1 && ReadInt(first, "pendingReplyCount", 0) == 1);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    Check("recipient_knows_letter_before_writer_finishes", RelationshipsSelfTestStore.SelectLetters(connection, TestDict("id", outgoing)).Count == 1);
                work = Task.Run(() => ProcessPendingCorrespondence(campaign, "player", 10.1d, "main", letter => {
                    Interlocked.Increment(ref composeCalls); entered.Set();
                    if (!release.Wait(TimeSpan.FromSeconds(20))) throw new TimeoutException("Test writer gate expired.");
                    return TestDict("shouldReply", false, "body", "");
                }, (letter, reply) => { Interlocked.Increment(ref acceptCalls); return TestDict("nativeRelationChanges", new[] {
                    TestDict("subjectId", "npc", "targetId", "player", "eventId", "mail_delivery_" + outgoing, "delta", 1) }); }));
                Check("controlled_writer_started", entered.Wait(TimeSpan.FromSeconds(10)));
                second = ReadString(Dispatch("npc3", "player", 10.1d), "letterId", "");
                payload["worldDay"] = 10.2d;
                payload["deliveryReceiptAcks"] = ReadStringList(first, "deliveryReceiptIds");
                var during = CorrespondenceTickApi(payload);
                Check("slow_writer_does_not_block_later_delivery", !work.IsCompleted
                    && ReadDictionaryList(during, "deliveredLetters").Any(r => ReadString(r, "letter_id", "") == second));
                ProcessPendingCorrespondence(campaign, "player", 10.2d, "main", _ => { composeCalls++; return TestDict(); });
                Check("concurrent_reply_claim_is_exact_once", composeCalls == 1);
                release.Set(); work.GetAwaiter().GetResult();
                var finished = ReadCorrespondenceDeliveryResults(campaign, "player", "main", ReadStringList(during, "deliveryReceiptIds"));
                Check("accepted_result_durable_until_ack", acceptCalls == 1 && ReadDictionaryList(finished, "nativeRelationChanges").Count == 1
                    && ReadStringList(finished, "deliveryReceiptIds").Contains(outgoing));
                var replay = ReadCorrespondenceDeliveryResults(campaign, "player", "main", new List<string>());
                Check("lost_response_replays_same_effect_receipt", Json.Serialize(ReadDictionaryList(replay, "nativeRelationChanges"))
                    == Json.Serialize(ReadDictionaryList(finished, "nativeRelationChanges")));
                var acked = ReadCorrespondenceDeliveryResults(campaign, "player", "main", ReadStringList(finished, "deliveryReceiptIds"));
                Check("acknowledged_result_not_delivered_again", ReadDictionaryList(acked, "nativeRelationChanges").Count == 0);
                payload["timelineId"] = "unrelated_branch";
                Check("stale_native_timeline_rejected", !ReadBool(CorrespondenceTickApi(payload), "ok", true));
                payload["timelineId"] = "main";
                string failed = ReadString(Dispatch("player", "npc", 11d), "letterId", "");
                DeliverDueCorrespondence(campaign, "player", 11.1d, "main");
                ProcessPendingCorrespondence(campaign, "player", 11.1d, "main", _ => { composeCalls++; return TestDict("error", "writer unavailable"); });
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var row = RelationshipsSelfTestStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", failed)).Single();
                    Check("failure_preserves_delivered_letter_for_bounded_retry", ReadString(row, "status", "") == "failed" && ReadInt(row, "attempt_count", 0) == 1);
                    RelationshipsSelfTestStore.UpdateCorrespondenceDeliveryJobs(connection, TestDict("id", failed));
                }
                ProcessPendingCorrespondence(campaign, "player", 11.1d, "main", _ => TestDict("error", "still unavailable"));
                ProcessPendingCorrespondence(campaign, "player", 11.1d, "main", _ => { composeCalls += 100; return TestDict(); });
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    Check("two_attempt_limit_is_explicit_incomplete", ReadString(RelationshipsSelfTestStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", failed)).Single(), "status", "") == "incomplete" && composeCalls == 2);

                string interrupted = ReadString(Dispatch("player", "npc", 12d), "letterId", "");
                DeliverDueCorrespondence(campaign, "player", 12.1d, "main");
                int composed = 0, effects = 0;
                ProcessPendingCorrespondence(campaign, "player", 12.1d, "main", _ => { composed++; return TestDict("shouldReply", false, "body", ""); },
                    (_, _) => { effects++; throw new InvalidOperationException("Acceptance interrupted after caching draft."); });
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    RelationshipsSelfTestStore.UpdateCorrespondenceDeliveryJobs(connection, TestDict("id", interrupted));
                ProcessPendingCorrespondence(campaign, "player", 12.1d, "main", _ => { composed++; return TestDict(); },
                    (_, _) => { effects++; return EmptyCorrespondenceDeliveryResult(); });
                Check("acceptance_retry_reuses_saved_draft", composed == 1 && effects == 2);
                string saved = ReadString(Dispatch("npc4", "player", 13d), "letterId", "");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "courier_before_arrival");
                DeliverDueCorrespondence(campaign, "player", 13.1d, "main");
                string future = ReadString(Dispatch("npc5", "player", 14d), "letterId", "");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "courier_before_arrival");
                DialogueService.AdvanceMemoryRestoreGeneration(campaign, TestDict("savePointId", "courier_before_arrival", "timelineId", "main",
                    "campaignTimeDays", 13d, "worldHistorySequence", 0L, "worldHistoryHeadEventId", ""));
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    Check("actual_snapshot_restores_pending_eta_without_future_letter", RelationshipsSelfTestStore.SelectLetters2(connection, TestDict("id", future)).Count == 0
                        && ReadString(RelationshipsSelfTestStore.SelectLetters2(connection, TestDict("id", saved)).Single(), "status", "") == "in_transit");
                    Check("saved_acknowledgments_survive_restore", ReadInt(RelationshipsSelfTestStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", outgoing)).Single(), "acknowledged", 0) == 1);
                }
                DeliverDueCorrespondence(campaign, "player", 13.1d, "main");
                Check("restored_letter_delivers_once_at_preserved_eta", ReadDictionaryList(ReadCorrespondenceDeliveryResults(campaign, "player", "main", new List<string>()), "deliveredLetters")
                    .Count(r => ReadString(r, "letter_id", "") == saved) == 1);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    RelationshipsSelfTestStore.UpdateCorrespondenceDeliveryJobs2(connection, TestDict("id", interrupted, "until", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 100));
                    var old = RelationshipsSelfTestStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", interrupted)).Single();
                    RebindRestoredCorrespondenceJobs(connection, "main", "restored_generation");
                    Check("old_worker_cannot_publish_after_snapshot_restore", !CorrespondenceDeliveryFence(connection, old, "old_worker"));
                    Check("restored_inflight_job_is_pending", ReadString(RelationshipsSelfTestStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", interrupted)).Single(), "status", "") == "pending");
                    RelationshipsSelfTestStore.UpdateCorrespondenceDeliveryJobs3(connection, TestDict("id", interrupted));
                    RebindRestoredCorrespondenceJobs(connection, "main", "second_restored_generation");
                    Check("exhausted_restored_work_is_explicitly_incomplete", ReadString(RelationshipsSelfTestStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", interrupted)).Single(), "status", "") == "incomplete");
                }
                VerifyCorrespondenceArrivalPublication(Check);
                VerifyCorrespondenceArrivalQueueFailures(Check);
            }
            catch (Exception ex) { Check("suite_exception", false, DialogueService.LimitText(ex.ToString(), 2400)); }
            finally { release.Set(); if (work != null) work.Wait(TimeSpan.FromSeconds(25)); ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
        private static void VerifyCorrespondenceArrivalPublication(Action<string, bool, object> check)
        {
            string campaign = "courier_arrival_test_" + Guid.NewGuid().ToString("N");
            const string player = "player", ruler = "ruler", timeline = "main", pregnancy = "pregnancy";
            const string admission = "I am pregnant.";
            const double arrivalDay = 28.2d;
            string historyPath = CampaignFileService.CharacterFile(campaign, ruler, "history", "dialogue.jsonl");
            try
            {
                string letterId, turnId, generation, legacyId;
                Dictionary<string, object> ProjectionScope(string gen) => TestDict("player", player, "timeline", timeline,
                    "generation", gen, "now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                List<Dictionary<string, object>> History(string id) => Program.ReadJsonLinesFromPath(historyPath)
                    .Where(row => ReadString(row, "letterId", "") == id).ToList();
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var facts = new PregnancyFacts
                    {
                        PregnancyId = pregnancy, MotherId = ruler, MotherName = ruler,
                        Active = true, TimingKnown = true, ConceptionDay = 10, DueDay = 46, WorldDay = 28
                    };
                    RelationshipsStore.UpsertConceptionContexts(connection, TestDict("timeline", timeline,
                        "id", pregnancy, "json", Json.Serialize(facts), "day", 28d));
                    GrantPregnancyKnowledge(connection, timeline, facts, ruler, false, false, "native", "native_hash", 28d);
                    CharactersService.UpsertIdentityRosterHero(connection, TestDict("heroStringId", ruler, "name", ruler,
                        "isAlive", true, "isAdult", true, "isRuler", true), 1);
                    string parent = ReadString(InsertLetter(connection, player, player, ruler, ruler,
                        "The household is well.", "player", "test_parent", "", "a", "b", 25d, 25.1d, TestDict()), "letterId", "");
                    DeliverDueCorrespondence(campaign, player, 26d, timeline);
                    ReconcileCorrespondenceArrivalHistory(campaign, player, timeline);
                    letterId = ReadString(InsertLetter(connection, ruler, ruler, player, player, admission, "npc_reply",
                        "test_arrival", parent, "b", "a", 28d, 28.1d, TestDict("pregnancyDisclosures",
                            new[] { TestDict("pregnancyId", pregnancy, "kind", "pregnancy", "evidenceQuote", admission) })), "letterId", "");
                    turnId = "turn_" + DialogueService.SafeMemoryKey(letterId);
                    generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");

                    // An old delivered job has no proof that any effect is missing.
                    legacyId = ReadString(InsertLetter(connection, ruler, ruler, player, player, "An older delivery.", "npc_initiated",
                        "test_legacy", "", "b", "a", 26d, 26.1d, TestDict()), "letterId", "");
                    RelationshipsStore.UpdateLetters(connection, TestDict("id", legacyId, "status", "delivered", "day", 27d,
                        "read", 0d, "timeline", timeline, "ts", 1L));
                    RelationshipsStore.InsertOrIgnoreCorrespondenceDeliveryJobs(connection, TestDict("id", legacyId, "player", player,
                        "timeline", timeline, "generation", generation, "hash", "legacy", "status", "completed", "result", "{}", "now", 1L, "history", ""));
                    check("arrival_history_migration_is_ready_on_fresh_schema",
                        PersistenceService.IsCampaignMigrationReady(connection, PersistenceService.CorrespondenceArrivalHistoryMigrationVersion)
                        && RelationshipsStore.SelectPendingCorrespondenceArrivalHistory(connection, ProjectionScope(generation)).Count == 0, null);
                    RelationshipsSchema.DowngradeCorrespondenceArrivalHistoryForTests(connection);
                }
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var legacy = RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", legacyId)).Single();
                    var applied = new List<int>();
                    check("arrival_history_migration_keeps_legacy_rows_ineligible",
                        PersistenceService.IsCampaignMigrationReady(connection, PersistenceService.CorrespondenceArrivalHistoryMigrationVersion)
                        && PersistenceService.ApplyCampaignSchemaMigrations(connection, applied) == PersistenceService.LatestCampaignMigrationVersion
                        && applied.Count == 0 && ReadString(legacy, "arrival_history_json", "missing").Length == 0
                        && RelationshipsStore.SelectPendingCorrespondenceArrivalHistory(connection, ProjectionScope(generation)).Count == 0, null);
                }

                const string simulatedFailure = "Interrupted before arrival was committed.";
                bool interrupted = false;
                try
                {
                    DeliverDueCorrespondence(campaign, player, arrivalDay, timeline,
                        () => throw new InvalidOperationException(simulatedFailure));
                }
                catch (InvalidOperationException ex) when (ex.Message == simulatedFailure) { interrupted = true; }

                var favor = TestDict("campaign", campaign, "timeline", timeline, "ruler", ruler,
                    "favorite", player, "contact", "correspondence:" + letterId);
                var favorPair = TestDict("campaign", campaign, "timeline", timeline, "keys",
                    Json.Serialize(new[] { TestDict("ruler_id", ruler, "favorite_id", player) }));
                var privateLine = TestDict("id", "saved_private", "channel", "private_whisper", "text", "Reserved evidence.");
                CampaignFileService.AppendJsonLineToPath(historyPath, privateLine);
                File.AppendAllText(historyPath, "{unreadable legacy row}" + Environment.NewLine + "{\"id\":\"interrupted", Encoding.UTF8);
                byte[] originalHistory = File.ReadAllBytes(historyPath);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var letter = RelationshipsStore.SelectLetters7(connection, TestDict("id", letterId)).Single();
                    check("arrival_failure_rolls_back_all_effects", interrupted
                        && ReadString(letter, "status", "") == "in_transit"
                        && RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", letterId)).Count == 0
                        && DialogueStore.SelectConversationTurns6(connection, TestDict("id", turnId)).Count == 0
                        && ReputationStore.SelectCourtRulerFavorContactReceipts(connection, favor).Count == 0
                        && ReputationStore.SelectCourtRulerFavorContact(connection, favorPair).Count == 0
                        && !ReadPregnancyKnowledge(connection, timeline, pregnancy, player).Pregnancy
                        && History(letterId).Count == 0, null);
                }

                // Only the DB phase completes, as if the process stopped before file publication.
                DeliverDueCorrespondence(campaign, player, arrivalDay, timeline);
                Dictionary<string, object> pendingLine;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var receipt = RelationshipsStore.SelectPendingCorrespondenceArrivalHistory(connection, ProjectionScope(generation)).Single();
                    pendingLine = TryParseJsonObject(ReadString(receipt, "arrival_history_json", ""));
                    var received = ReadPregnancyKnowledge(connection, timeline, pregnancy, player);
                    check("committed_arrival_contains_sql_effects_and_pending_history",
                        ReadString(receipt, "letter_id", "") == letterId && ReadString(pendingLine, "letterId", "") == letterId
                        && ReadString(RelationshipsStore.SelectLetters7(connection, TestDict("id", letterId)).Single(), "status", "") == "delivered"
                        && DialogueStore.SelectConversationTurns6(connection, TestDict("id", turnId)).Count == 1
                        && ReputationStore.SelectCourtRulerFavorContactReceipts(connection, favor).Count == 1
                        && ReadDouble(ReputationStore.SelectCourtRulerFavorContact(connection, favorPair).SingleOrDefault(),
                            "last_contact_day", -1d) == arrivalDay
                        && received.Pregnancy && received.EvidenceId == "delivered_letter:" + letterId
                        && !ReadPregnancyKnowledge(connection, timeline, pregnancy, "unaddressed").Pregnancy
                        && History(letterId).Count == 0, null);
                    RebindRestoredCorrespondenceJobs(connection, timeline, "different_generation");
                }

                // Current polling cannot publish a receipt owned by another generation.
                var poll = TestDict("campaignId", campaign, "playerId", player, "timelineId", timeline,
                    "worldDay", arrivalDay + 2d, "phase", "delivery");
                CorrespondenceTickApi(poll);
                check("arrival_projection_requires_current_restore_generation", History(letterId).Count == 0, null);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                    RebindRestoredCorrespondenceJobs(connection, timeline, generation);

                // Publish bytes, then interrupt before clearing the matching DB receipt.
                PublishCorrespondenceArrivalHistory(campaign, ruler, letterId, pendingLine);
                var replay = CorrespondenceTickApi(poll);
                poll["deliveryReceiptAcks"] = ReadStringList(replay, "deliveryReceiptIds");
                var acknowledged = CorrespondenceTickApi(poll);
                var lines = History(letterId);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    check("arrival_replay_keeps_one_original_history_line",
                        ReadDictionaryList(replay, "deliveredLetters").Any(row => ReadString(row, "letter_id", "") == letterId)
                        && !ReadStringList(acknowledged, "deliveryReceiptIds").Contains(letterId)
                        && RelationshipsStore.SelectPendingCorrespondenceArrivalHistory(connection, ProjectionScope(generation)).Count == 0
                        && lines.Count == 1 && ReadString(lines[0], "id", "") == ReadString(pendingLine, "id", "")
                        && ReadDouble(lines[0], "worldDay", -1d) == arrivalDay && History(legacyId).Count == 0, null);
                    check("arrival_history_preserves_legacy_torn_bytes_and_valid_rows",
                        File.ReadAllBytes(historyPath).Take(originalHistory.Length).SequenceEqual(originalHistory)
                        && Program.ReadJsonLinesFromPath(historyPath).Any(row => CanonicalJson(row) == CanonicalJson(privateLine))
                        && lines.Count == 1, null);
                }
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
        }

        private static void VerifyCorrespondenceArrivalQueueFailures(Action<string, bool, object> check)
        {
            string campaign = "courier_arrival_queue_test_" + Guid.NewGuid().ToString("N");
            const string player = "player", npc = "npc", timeline = "main";
            try
            {
                string older, later;
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    string Dispatch(string text, double day) => ReadString(InsertLetter(connection, npc, npc, player, player,
                        text, "npc_initiated", "fixture", "", "a", "b", day, day + .2d, TestDict()), "letterId", "");
                    older = Dispatch("An older letter.", 1d);
                    later = Dispatch("A later letter.", 2d);
                    var otherWords = new Dictionary<string, object>(
                        RelationshipsStore.SelectLetters7(connection, TestDict("id", older)).Single())
                    {
                        ["body"] = "Different words already bound to this source."
                    };
                    using (var transaction = connection.BeginTransaction())
                    {
                        DialogueService.StoreCorrespondenceTurn(connection, campaign, npc, player, otherWords, 1d, 1L);
                        transaction.Commit();
                    }
                    string generation = ReadString(DialogueService.ReadMemoryPrecisionState(connection), "restore_generation", "");
                    // The oldest page has irrecoverable projection receipts whose
                    // source letters are absent. None should remain in the due queue.
                    for (int i = 0; i < 100; i++)
                        RelationshipsStore.InsertOrIgnoreCorrespondenceDeliveryJobs(connection, TestDict(
                            "id", "orphan_history_" + i.ToString("D3"), "player", player, "timeline", timeline,
                            "generation", generation, "hash", "unavailable", "status", "completed", "result", "{}", "now", 1L, "history", "{}"));
                }

                DeliverDueCorrespondence(campaign, player, 4d, timeline);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var oldRow = RelationshipsStore.SelectLetters7(connection, TestDict("id", older)).Single();
                    var lateRow = RelationshipsStore.SelectLetters7(connection, TestDict("id", later)).Single();
                    check("arrival_failure_defers_only_old_letter",
                        ReadString(oldRow, "status", "") == "in_transit" && ReadLong(oldRow, "arrival_retry_after_ts", 0) > 0
                        && RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", older)).Count == 0
                        && ReadString(lateRow, "status", "") == "delivered"
                        && RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", later)).Count == 1, null);
                }

                var poll = TestDict("campaignId", campaign, "playerId", player, "timelineId", timeline, "worldDay", 4d, "phase", "delivery");
                var first = CorrespondenceTickApi(poll);
                poll["deliveryReceiptAcks"] = ReadStringList(first, "deliveryReceiptIds");
                var second = CorrespondenceTickApi(poll);
                using (var connection = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    var excluded = RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", "orphan_history_099")).Single();
                    var published = RelationshipsStore.SelectCorrespondenceDeliveryJobs(connection, TestDict("id", later)).Single();
                    string path = CampaignFileService.CharacterFile(campaign, npc, "history", "dialogue.jsonl");
                    check("arrival_history_terminal_old_page_does_not_starve_newer_receipt",
                        ReadString(excluded, "arrival_history_terminal_error", "") == "source_mismatch"
                        && ReadString(excluded, "arrival_history_json", "") == "{}"
                        && ReadString(published, "arrival_history_json", "missing").Length == 0
                        && ReadDictionaryList(second, "deliveredLetters").Any(row => ReadString(row, "letter_id", "") == later)
                        && Program.ReadJsonLinesFromPath(path).Count(row => ReadString(row, "letterId", "") == later) == 1, null);
                }
            }
            finally
            {
                ReignPostgreSqlStorage.DropCampaign(campaign);
                PersistenceService.TryDeleteDirectory(CampaignFileService.CampaignDirectory(campaign));
            }
        }
    }
}
