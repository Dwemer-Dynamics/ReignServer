using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        private static readonly PersonalGossipDispatch GossipDispatch = new(
            (name, priority, work, notStarted) =>
                OperationsService.TryEnqueuePriorityBackgroundWork(name, priority, work, notStarted, out _),
            campaign =>
            {
                var result = ProcessPersonalGossipTurn(campaign, ComposePersonalGossip);
                if (result.Outcome != "idle" && result.Outcome != "disabled")
                {
                    // Queue delay and active duration are recorded by the enclosing background.work
                    // span. A rejected result or a deferred row is distinct from a dispatcher fault.
                    try
                    {
                        OperationsService.LogOperational("gossip.dispatch_outcome",
                            GossipFields("campaignId", campaign, "outcome", result.Outcome, "moreDue", result.MoreDue));
                    }
                    catch { }
                }
                return result.MoreDue;
            });

        private static void SchedulePersonalGossip(string campaign)
        {
            if (campaign.StartsWith("__gossip_test_", StringComparison.Ordinal) || !string.IsNullOrWhiteSpace(Program.CampaignsRootOverride.Value)
                || Environment.GetEnvironmentVariable("REIGN_VALIDATION_MODE") == "1") return;
            GossipDispatch.Schedule(campaign);
        }

        private sealed class PersonalGossipDispatch
        {
            private readonly ConcurrentDictionary<string, object> owners = new(StringComparer.OrdinalIgnoreCase);
            private readonly Action<string, string, Action, Action<string>> enqueue;
            private readonly Func<string, bool> runTurn;

            internal PersonalGossipDispatch(Action<string, string, Action, Action<string>> enqueue, Func<string, bool> runTurn)
            {
                this.enqueue = enqueue;
                this.runTurn = runTurn;
            }

            internal void Schedule(string campaign)
            {
                var owner = new object();
                if (owners.TryAdd(campaign, owner)) EnqueueTurn(campaign, owner, "normal");
            }

            private void EnqueueTurn(string campaign, object owner, string priority)
            {
                void Release() => owners.TryRemove(new KeyValuePair<string, object>(campaign, owner));

                try
                {
                    enqueue("personal_gossip." + campaign, priority, () =>
                    {
                        try
                        {
                            if (!runTurn(campaign)) return;
                            // Transfer ownership before admission. The old callback's cleanup cannot
                            // remove the successor, even if shutdown settles it before we return.
                            var successor = new object();
                            if (owners.TryUpdate(campaign, successor, owner))
                            {
                                // Let queued normal and maintenance callbacks run before this backlog.
                                // Composition still uses its existing provider admission priority.
                                EnqueueTurn(campaign, successor, "maintenance");
                            }
                        }
                        finally { Release(); }
                    }, _ => Release());
                }
                catch
                {
                    Release();
                    throw;
                }
            }
        }
    }
}
