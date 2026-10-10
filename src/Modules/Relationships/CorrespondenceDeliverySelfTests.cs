using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
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
                    using var connection = OpenCampaignConnection(campaign);
                    return InsertLetter(connection, sender, sender, recipient, recipient,
                        "An accepted complete letter about the western gate.", "player", "fixture", "", "a", "b", day, day + .25d, TestDict());
                }
                outgoing = ReadString(Dispatch("player", "npc", 10d), "letterId", "");
                incoming = ReadString(Dispatch("npc2", "player", 10d), "letterId", "");
                var payload = TestDict("campaignId", campaign, "playerId", "player", "timelineId", "main", "worldDay", 10.1d, "phase", "delivery");
                var first = CorrespondenceTickApi(payload);
                Check("fast_phase_has_no_provider_and_all_arrivals", ReadBool(first, "ok", false)
                    && ReadDictionaryList(first, "deliveredLetters").Count == 1 && ReadInt(first, "pendingReplyCount", 0) == 1);
                using (var connection = OpenCampaignConnection(campaign))
                    Check("recipient_knows_letter_before_writer_finishes", QuerySql(connection, "SELECT * FROM letters WHERE status='read' AND letter_id=$id;", TestDict("id", outgoing)).Count == 1);
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
                using (var connection = OpenCampaignConnection(campaign))
                {
                    var row = QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", TestDict("id", failed)).Single();
                    Check("failure_preserves_delivered_letter_for_bounded_retry", ReadString(row, "status", "") == "failed" && ReadInt(row, "attempt_count", 0) == 1);
                    ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET next_attempt_ts=0 WHERE letter_id=$id;", TestDict("id", failed));
                }
                ProcessPendingCorrespondence(campaign, "player", 11.1d, "main", _ => TestDict("error", "still unavailable"));
                ProcessPendingCorrespondence(campaign, "player", 11.1d, "main", _ => { composeCalls += 100; return TestDict(); });
                using (var connection = OpenCampaignConnection(campaign))
                    Check("two_attempt_limit_is_explicit_incomplete", ReadString(QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", TestDict("id", failed)).Single(), "status", "") == "incomplete" && composeCalls == 2);

                string interrupted = ReadString(Dispatch("player", "npc", 12d), "letterId", "");
                DeliverDueCorrespondence(campaign, "player", 12.1d, "main");
                int composed = 0, effects = 0;
                ProcessPendingCorrespondence(campaign, "player", 12.1d, "main", _ => { composed++; return TestDict("shouldReply", false, "body", ""); },
                    (_, _) => { effects++; throw new InvalidOperationException("Acceptance interrupted after caching draft."); });
                using (var connection = OpenCampaignConnection(campaign))
                    ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET next_attempt_ts=0 WHERE letter_id=$id;", TestDict("id", interrupted));
                ProcessPendingCorrespondence(campaign, "player", 12.1d, "main", _ => { composed++; return TestDict(); },
                    (_, _) => { effects++; return EmptyCorrespondenceDeliveryResult(); });
                Check("acceptance_retry_reuses_saved_draft", composed == 1 && effects == 2);
                string saved = ReadString(Dispatch("npc4", "player", 13d), "letterId", "");
                ReignPostgreSqlStorage.CloneCampaignToSnapshot(campaign, "courier_before_arrival");
                DeliverDueCorrespondence(campaign, "player", 13.1d, "main");
                string future = ReadString(Dispatch("npc5", "player", 14d), "letterId", "");
                ReignPostgreSqlStorage.RestoreCampaignSnapshot(campaign, "courier_before_arrival");
                AdvanceMemoryRestoreGeneration(campaign, TestDict("savePointId", "courier_before_arrival", "timelineId", "main",
                    "campaignTimeDays", 13d, "worldHistorySequence", 0L, "worldHistoryHeadEventId", ""));
                using (var connection = OpenCampaignConnection(campaign))
                {
                    Check("actual_snapshot_restores_pending_eta_without_future_letter", QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", future)).Count == 0
                        && ReadString(QuerySql(connection, "SELECT * FROM letters WHERE letter_id=$id;", TestDict("id", saved)).Single(), "status", "") == "in_transit");
                    Check("saved_acknowledgments_survive_restore", ReadInt(QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", TestDict("id", outgoing)).Single(), "acknowledged", 0) == 1);
                }
                DeliverDueCorrespondence(campaign, "player", 13.1d, "main");
                Check("restored_letter_delivers_once_at_preserved_eta", ReadDictionaryList(ReadCorrespondenceDeliveryResults(campaign, "player", "main", new List<string>()), "deliveredLetters")
                    .Count(r => ReadString(r, "letter_id", "") == saved) == 1);
                using (var connection = OpenCampaignConnection(campaign))
                {
                    ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET status='running',attempt_count=1,lease_token='old_worker',lease_until=$until WHERE letter_id=$id;", TestDict("id", interrupted, "until", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 100));
                    var old = QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", TestDict("id", interrupted)).Single();
                    RebindRestoredCorrespondenceJobs(connection, "main", "restored_generation");
                    Check("old_worker_cannot_publish_after_snapshot_restore", !CorrespondenceDeliveryFence(connection, old, "old_worker"));
                    Check("restored_inflight_job_is_pending", ReadString(QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", TestDict("id", interrupted)).Single(), "status", "") == "pending");
                    ExecuteSql(connection, "UPDATE correspondence_delivery_jobs SET status='running',attempt_count=2 WHERE letter_id=$id;", TestDict("id", interrupted));
                    RebindRestoredCorrespondenceJobs(connection, "main", "second_restored_generation");
                    Check("exhausted_restored_work_is_explicitly_incomplete", ReadString(QuerySql(connection, "SELECT * FROM correspondence_delivery_jobs WHERE letter_id=$id;", TestDict("id", interrupted)).Single(), "status", "") == "incomplete");
                }
            }
            catch (Exception ex) { Check("suite_exception", false, LimitText(ex.ToString(), 2400)); }
            finally { release.Set(); if (work != null) work.Wait(TimeSpan.FromSeconds(25)); ReignPostgreSqlStorage.DropCampaign(campaign); }
            return results;
        }
    }
}
