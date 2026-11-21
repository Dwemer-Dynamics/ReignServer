using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        // Older persistence fixtures drain a bounded batch synchronously before asserting on
        // the native clock. Production enters the one-item seam through PersonalGossipDispatch.
        private static void RunPersonalGossipWork(string campaign, Func<Dictionary<string, object>, bool, Dictionary<string, object>> compose)
        {
            for (int item = 0; item < 8; item++)
                if (!ProcessPersonalGossipTurn(campaign, compose).MoreDue) break;
        }

        private static void RunPersonalGossipSchedulingTests(Action<string, bool, object> check)
        {
            try { RunPersonalGossipFairnessTest(check); }
            catch (Exception ex) { check("dispatch_fairness_fixture", false, ex.ToString()); }
            try { RunPersonalGossipDispatchPersistenceTest(check); }
            catch (Exception ex) { check("dispatch_persistence_fixture", false, ex.ToString()); }
        }

        private static void RunPersonalGossipFairnessTest(Action<string, bool, object> check)
        {
            var scheduler = new OperationsService.BoundedPriorityScheduler("gossip dispatch test", 8, 1, null, false);
            var order = new List<string>();
            using var composing = new ManualResetEventSlim();
            using var releaseComposer = new ManualResetEventSlim();
            using var maintaining = new ManualResetEventSlim();
            using var releaseMaintenance = new ManualResetEventSlim();
            using var finished = new ManualResetEventSlim();
            var timeout = TimeSpan.FromSeconds(10);
            int items = 0;
            var dispatch = new PersonalGossipDispatch((name, priority, work, notStarted) =>
                scheduler.TryEnqueue(name, priority == "maintenance" ? 2 : 1, work, notStarted, out _),
                _ =>
                {
                    int item = ++items;
                    order.Add("gossip_" + item);
                    if (item == 1)
                    {
                        composing.Set();
                        if (!releaseComposer.Wait(timeout)) throw new TimeoutException("Composer was not released.");
                    }
                    if (item == 3) finished.Set();
                    return item < 3;
                });
            try
            {
                dispatch.Schedule("campaign");
                if (!composing.Wait(timeout)) throw new TimeoutException("Composer did not start.");
                scheduler.TryEnqueue("maintenance", 2, () =>
                {
                    order.Add("maintenance");
                    maintaining.Set();
                    if (!releaseMaintenance.Wait(timeout)) throw new TimeoutException("Maintenance was not released.");
                }, null, out _);
                scheduler.TryEnqueue("verification", 1, () => order.Add("verification"), null, out _);
                dispatch.Schedule("CAMPAIGN");
                var blocked = scheduler.Status();
                bool coalescedWhileActive = ReadInt(blocked, "queued", -1) == 2;
                check("dispatch_reports_active_duration_and_oldest_queued_age", ReadString(blocked, "active", "") == "personal_gossip.campaign"
                    && ReadLong(blocked, "activeDurationMs", -1) >= 0 && ReadLong(blocked, "oldestQueuedAgeMs", -1) >= 0, blocked);
                releaseComposer.Set();
                if (!maintaining.Wait(timeout)) throw new TimeoutException("Maintenance did not start.");
                dispatch.Schedule("campaign");
                bool coalescedWhileQueued = ReadInt(scheduler.Status(), "queued", -1) == 1;
                releaseMaintenance.Set();
                if (!finished.Wait(timeout)) throw new TimeoutException("Continuation did not finish.");
                if (!scheduler.Stop("gossip_test_drained").Wait(timeout)) throw new TimeoutException("Scheduler did not settle.");
                check("dispatch_yields_one_item_to_verification_and_maintenance", order.SequenceEqual(new[]
                    { "gossip_1", "verification", "maintenance", "gossip_2", "gossip_3" }), order);
                check("dispatch_coalesces_active_and_transferred_ownership", coalescedWhileActive && coalescedWhileQueued
                    && ReadInt(scheduler.Status(), "accepted", -1) == 5, scheduler.Status());
            }
            finally
            {
                releaseComposer.Set();
                releaseMaintenance.Set();
                scheduler.Stop("gossip_test_cleanup").Wait(timeout);
            }
        }

        private static void RunPersonalGossipDispatchPersistenceTest(Action<string, bool, object> check)
        {
            string campaign = "__gossip_test_" + Guid.NewGuid().ToString("N");
            string[] ids = { "dispatch_a", "dispatch_b", "dispatch_c" };
            var speaker = GossipTestHero("source");
            var recipient = GossipTestHero("target");
            var player = GossipTestHero("player");
            var clock = GossipFields("campaignId", campaign, "timelineId", "main", "worldDay", 20d,
                "gossipPresence", GossipFields("schema", "reign-gossip-presence-v1", "timelineId", "main", "worldDay", 20d,
                    "rosterComplete", true, "people", new[] { speaker, recipient, player }));
            var queued = new Queue<(Action Run, Action<string> NotStarted)>();
            string refusal = "";
            string outcome = "";
            int composerCalls = 0;
            Func<Dictionary<string, object>, bool, Dictionary<string, object>> compose = (_, _) =>
            {
                composerCalls++;
                return GossipFields("body", "The traveler made a good impression.");
            };
            PersonalGossipDispatch NewDispatch() => new((_, _, run, release) =>
            {
                if (refusal.Length != 0) release(refusal);
                else queued.Enqueue((run, release));
            }, name =>
            {
                var result = ProcessPersonalGossipTurn(name, compose);
                outcome = result.Outcome;
                return result.MoreDue;
            });
            Dictionary<string, object> Row(string id)
            {
                using var c = ReignDatabase.OpenCampaignConnection(campaign);
                return DialogueSelfTestStore.SelectPersonalGossipMessages9(c, GossipFields("id", id)).Single();
            }
            bool Is(string id, string status, int attempts)
            {
                var row = Row(id);
                return ReadString(row, "status", "") == status && ReadInt(row, "attempts", -1) == attempts;
            }
            try
            {
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    EnsurePersonalGossipSchema(c);
                    DialogueSelfTestStore.InsertOrIgnoreConversationSessions(c, GossipFields("id", "dispatch_source", "campaign", campaign, "day", 20d));
                    var source = StoreConversationExchange(campaign, GossipFields("campaignId", campaign, "conversationSessionId", "dispatch_source",
                        "timelineId", "main", "worldDay", 20d, "channel", "in_person"), "source", "player", "Traveler", "Source",
                        "I enjoyed the tournament.", "So did I.", "dispatch_exchange", 1);
                    foreach (string id in ids)
                    {
                        InsertPersonalGossipMessage(c, GossipFields("id", id, "rootId", id, "timelineId", "main", "worldDay", 20d,
                            "dueDay", 20d, "expiresDay", 30d, "local", true, "speakerId", "source", "recipientId", "target", "playerId", "player",
                            "speaker", speaker, "recipient", recipient, "type", "impression", "claimKind", "impression", "delta", 10,
                            "sourceTurnIds", ReadStringList(source, "turnIds")), id, false);
                    }
                    if (!GossipAcceptPresence(c, clock)) throw new InvalidOperationException("Dispatch fixture clock was rejected.");
                }

                var dispatch = NewDispatch();
                dispatch.Schedule(campaign);
                refusal = OperationsService.PriorityWorkNotStartedBusy;
                queued.Dequeue().Run();
                check("dispatch_busy_continuation_preserves_other_rows", composerCalls == 1 && queued.Count == 0
                    && Is(ids[0], "awaiting_native_clock", 1) && Is(ids[1], "pending", 0) && Is(ids[2], "pending", 0), null);
                refusal = "";
                dispatch.Schedule(campaign);
                queued.Dequeue().NotStarted(OperationsService.PriorityWorkNotStartedCanceled);
                refusal = OperationsService.PriorityWorkNotStartedStopping;
                dispatch.Schedule(campaign);
                check("dispatch_stop_before_start_leaves_pending_rows_unleased", composerCalls == 1 && queued.Count == 0
                    && Is(ids[1], "pending", 0) && Is(ids[2], "pending", 0), null);

                refusal = "";
                dispatch = NewDispatch();
                var successfulComposer = compose;
                compose = (data, reply) =>
                {
                    using var c = ReignDatabase.OpenCampaignConnection(campaign);
                    string generation = ReadString(ReadMemoryPrecisionState(c), "restore_generation", "");
                    DialogueStore.UpdatePersonalGossipMessages2(c, GossipFields("id", ids[1], "token", generation + ":replacement",
                        "until", DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600));
                    return successfulComposer(data, reply);
                };
                dispatch.Schedule(campaign);
                queued.Dequeue().Run();
                check("dispatch_abandons_stale_lease_without_publishing", outcome == "stale_lease" && queued.Count == 0
                    && Is(ids[1], "generating", 2) && Is(ids[2], "pending", 0), null);

                compose = (data, reply) =>
                {
                    using var c = ReignDatabase.OpenCampaignConnection(campaign);
                    DialogueSelfTestStore.UpdateMemoryPrecisionState2(c, GossipFields("generation", "dispatch_restored"));
                    return successfulComposer(data, reply);
                };
                dispatch.Schedule(campaign);
                queued.Dequeue().Run();
                check("dispatch_discards_stale_generation_and_queues_due_work", outcome == "stale_generation" && queued.Count == 1
                    && Is(ids[1], "generating", 2) && Is(ids[2], "generating", 1), null);

                compose = successfulComposer;
                queued.Dequeue().Run();
                bool reclaimedOne = queued.Count == 1 && Is(ids[1], "awaiting_native_clock", 3) && Is(ids[2], "generating", 1);
                queued.Dequeue().Run();
                check("dispatch_rediscovers_restored_rows_one_at_a_time", reclaimedOne && queued.Count == 0
                    && Is(ids[2], "awaiting_native_clock", 2), null);
                var readiness = PersonalGossipStatusApi(new Dictionary<string, string> { ["campaignId"] = campaign, ["deliveryOnly"] = "true" });
                clock["gossipClockEpoch"] = ReadString(readiness, "clockEpoch", "");
                clock["gossipReadyNonces"] = ReadStringList(readiness, "readyNonces");
                var tick = PersonalGossipTickApi(clock);
                using (var c = ReignDatabase.OpenCampaignConnection(campaign))
                {
                    check("dispatch_recovered_rows_publish_on_native_clock_and_deliver", ReadInt(tick, "delivered", -1) == ids.Length
                        && ids.All(id => DialogueSelfTestStore.SelectPersonalGossipHistory8(c, GossipFields("message", id)).Count == 1), tick);
                }
            }
            finally { ReignPostgreSqlStorage.DropCampaign(campaign); }
        }
    }
}
