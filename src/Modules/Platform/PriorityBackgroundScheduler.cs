using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal enum PriorityWorkAdmission
    {
        Accepted = 0,
        Busy = 1,
        Stopping = 2
    }

    internal static partial class Program
    {
        // Provisional bound on queued items (at most one more is active). Interactive work may use
        // all 128 slots, normal work is admitted below 112 and maintenance below 96, reserving 16
        // slots for each higher priority. 96 covers one memory reconciliation batch (LIMIT 50) plus
        // a replay/enrichment burst. There is no measured queue-depth baseline yet.
        private const int PriorityWorkCapacity = 128;
        private const int PriorityWorkReserve = 16;

        internal const string PriorityWorkNotStartedBusy = "busy";
        internal const string PriorityWorkNotStartedStopping = "stopping";
        internal const string PriorityWorkNotStartedCanceled = "canceled_before_start";

        private static readonly Task PriorityWorkRefused = Task.FromCanceled(new CancellationToken(true));

        private sealed class PriorityWorkItem
        {
            public string Id;
            public string Name;
            public int Priority;
            public Action Work;
            public Action<string> NotStarted;
            public TaskCompletionSource<bool> Completion;
            public DateTime EnqueuedUtc;
        }

        /// <summary>
        /// One worker, three FIFO queues (0 interactive, 1 normal, 2 maintenance), one lock.
        /// Admission, claim and stop are linearized on <c>gate</c>: an item the worker dequeues
        /// under the gate starts and reports its real outcome; an item still queued when stop
        /// takes the gate is detached and never starts. Completions use asynchronous
        /// continuations and, like NotStarted releases and logging, are settled outside the gate.
        /// The first stop transition publishes one fence under the gate; every Stop returns it.
        /// </summary>
        private sealed class BoundedPriorityScheduler
        {
            private const int IdleWaitMs = 250;
            private const int QuietPollMs = 100;

            private readonly object gate = new object();
            private readonly Queue<PriorityWorkItem>[] queues =
            {
                new Queue<PriorityWorkItem>(), new Queue<PriorityWorkItem>(), new Queue<PriorityWorkItem>()
            };
            private readonly string threadName;
            private readonly int[] limits;
            private readonly Func<int, bool> quietPeriodPending;
            private readonly bool production;
            private Thread worker;
            private int queued;
            private PriorityWorkItem active;
            private bool stopping;
            // Created once, under the gate, by the stop transition; never replaced.
            private TaskCompletionSource<bool> stopFence;
            private long accepted, completed, failed, canceledBeforeStart, rejectedBusy, rejectedStopping;

            internal BoundedPriorityScheduler(string threadName, int capacity, int reserve,
                Func<int, bool> quietPeriodPending, bool production)
            {
                this.threadName = threadName;
                limits = new[] { capacity, capacity - reserve, capacity - 2 * reserve };
                this.quietPeriodPending = quietPeriodPending;
                this.production = production;
            }

            internal void Start()
            {
                lock (gate) EnsureWorkerLocked();
            }

            /// <summary>
            /// Never blocks. Refusal allocates nothing: <paramref name="notStarted"/> runs on this
            /// thread and <paramref name="completion"/> is an already canceled task.
            /// </summary>
            internal PriorityWorkAdmission TryEnqueue(string name, int level, Action work, Action<string> notStarted, out Task completion)
            {
                PriorityWorkAdmission admission;
                lock (gate)
                {
                    if (stopping)
                    {
                        rejectedStopping++;
                        admission = PriorityWorkAdmission.Stopping;
                    }
                    else if (queued >= limits[level])
                    {
                        rejectedBusy++;
                        admission = PriorityWorkAdmission.Busy;
                    }
                    else
                    {
                        PriorityWorkItem item = new PriorityWorkItem
                        {
                            Id = "work_" + Guid.NewGuid().ToString("N"), Name = name, Priority = level, Work = work,
                            NotStarted = notStarted, EnqueuedUtc = DateTime.UtcNow,
                            Completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
                        };
                        queues[level].Enqueue(item);
                        queued++;
                        accepted++;
                        EnsureWorkerLocked();
                        // Wakes a quiet-period wait so higher-priority work starts promptly.
                        Monitor.Pulse(gate);
                        completion = item.Completion.Task;
                        return PriorityWorkAdmission.Accepted;
                    }
                }
                completion = PriorityWorkRefused;
                Release(name, notStarted, admission == PriorityWorkAdmission.Busy ? PriorityWorkNotStartedBusy : PriorityWorkNotStartedStopping);
                return admission;
            }

            internal bool HasQueuedInteractiveOrStopping()
            {
                lock (gate) return stopping || queues[0].Count > 0;
            }

            /// <summary>
            /// Closes admission and cancels every queued item before it can start. Stop does not
            /// wait for the active callback. The first stopper synchronously settles the detached
            /// queued items' releases outside the lock before returning; later callers return
            /// the shared fence immediately. Stop is the only way to close admission; every caller
            /// shares one fence created on the first transition. It completes (never faults) only after every queued item's
            /// NotStarted release has returned and its accepted task is canceled, and the item
            /// active at the transition has returned or thrown and its own task is terminal. That
            /// task keeps its real outcome; an active fault is observed here, not propagated.
            /// </summary>
            internal Task Stop(string reason)
            {
                List<PriorityWorkItem> detached;
                Task activeCompletion, fence;
                lock (gate)
                {
                    detached = BeginStopLocked(out activeCompletion);
                    fence = stopFence.Task;
                }
                SettleDetached(detached, activeCompletion, reason);
                return fence;
            }

            internal Dictionary<string, object> Status()
            {
                lock (gate)
                {
                    return new Dictionary<string, object>
                    {
                        ["running"] = worker != null && worker.IsAlive, ["active"] = active?.Name ?? "",
                        ["interactiveQueued"] = queues[0].Count, ["normalQueued"] = queues[1].Count,
                        ["maintenanceQueued"] = queues[2].Count, ["queued"] = queued,
                        ["interactiveLimit"] = limits[0], ["normalLimit"] = limits[1], ["maintenanceLimit"] = limits[2],
                        ["accepted"] = accepted, ["completed"] = completed, ["failed"] = failed,
                        ["canceledBeforeStart"] = canceledBeforeStart,
                        ["rejectedBusy"] = rejectedBusy, ["rejectedStopping"] = rejectedStopping, ["stopping"] = stopping
                    };
                }
            }

            private void EnsureWorkerLocked()
            {
                if (stopping || (worker != null && worker.IsAlive)) return;
                worker = new Thread(Loop) { IsBackground = true, Name = threadName };
                worker.Start();
            }

            // Returns the detached items only on the stop transition; null otherwise. The caller
            // that receives them owns settling them and completing the fence.
            private List<PriorityWorkItem> BeginStopLocked(out Task activeCompletion)
            {
                activeCompletion = null;
                if (stopping) return null;
                stopping = true;
                stopFence = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                // Execute clears active only after its task is terminal (see Loop).
                activeCompletion = active?.Completion.Task;
                List<PriorityWorkItem> detached = new List<PriorityWorkItem>(queued);
                foreach (Queue<PriorityWorkItem> queue in queues)
                    while (queue.Count > 0) detached.Add(queue.Dequeue());
                queued = 0;
                canceledBeforeStart += detached.Count;
                Monitor.PulseAll(gate);
                return detached;
            }

            private void SettleDetached(List<PriorityWorkItem> detached, Task activeCompletion, string reason)
            {
                if (detached == null) return;
                // Published under the gate by this thread's own transition.
                TaskCompletionSource<bool> fence = stopFence;
                foreach (PriorityWorkItem item in detached)
                {
                    Action<string> release = item.NotStarted;
                    item.NotStarted = null;
                    item.Work = null;
                    Release(item.Name, release, PriorityWorkNotStartedCanceled);
                    item.Completion.TrySetCanceled();
                }
                Log("background.scheduler_stopping", () => new Dictionary<string, object>
                { ["reason"] = reason ?? "", ["canceledBeforeStart"] = detached.Count, ["activeAtStop"] = activeCompletion != null });
                if (activeCompletion == null)
                {
                    fence.TrySetResult(true);
                    return;
                }
                // Async join, inline if already terminal. The active task completes outside the gate
                // with asynchronous continuations, so this never runs under the gate or on the worker
                // that owes it; the fence's own continuations are asynchronous too.
                activeCompletion.ContinueWith(task =>
                {
                    _ = task.Exception;
                    fence.TrySetResult(true);
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

            private void Release(string name, Action<string> release, string reason)
            {
                if (release == null) return;
                try { release(reason); }
                catch (Exception ex)
                {
                    Log("background.work_release_failed", () => new Dictionary<string, object>
                    { ["name"] = name, ["reason"] = reason, ["error"] = LimitText(ex.Message, 400) });
                }
            }

            // Telemetry runs after settlement and can never throw into the worker or a caller.
            private void Log(string eventType, Func<Dictionary<string, object>> data)
            {
                if (!production) return;
                try { LogOperational(eventType, data()); } catch { }
            }

            private bool QuietPeriodPending(int level)
            {
                try { return quietPeriodPending != null && quietPeriodPending(level); }
                catch { return false; }
            }

            private void Loop()
            {
                while (true)
                {
                    PriorityWorkItem item = null;
                    bool exit;
                    lock (gate)
                    {
                        // The previous item's task is already terminal; only now does it stop being
                        // attributable, so a stop transition before this point still joins it.
                        active = null;
                        exit = stopping;
                        if (!exit)
                        {
                            int waitMs = IdleWaitMs;
                            // Strict priority, FIFO per level. A non-empty level still in its quiet
                            // period stays queued and blocks lower levels; admission pulses the gate.
                            for (int level = 0; level < queues.Length; level++)
                            {
                                if (queues[level].Count == 0) continue;
                                if (level > 0 && QuietPeriodPending(level)) { waitMs = QuietPollMs; break; }
                                item = queues[level].Dequeue();
                                queued--;
                                active = item;
                                break;
                            }
                            if (item == null) Monitor.Wait(gate, waitMs);
                        }
                    }
                    if (exit) return;
                    if (item != null) Execute(item);
                }
            }

            private void Execute(PriorityWorkItem item)
            {
                Action work = item.Work;
                item.Work = null;
                item.NotStarted = null;
                ReignTraceScope span = null;
                if (production)
                {
                    try
                    {
                        span = BeginReignSpan("background.work", new Dictionary<string, object>
                        {
                            ["work.id"] = item.Id, ["work.name"] = item.Name, ["work.priority"] = item.Priority,
                            ["queueDelayMs"] = (long)(DateTime.UtcNow - item.EnqueuedUtc).TotalMilliseconds
                        });
                    }
                    catch { }
                }
                Exception error = null;
                try { work(); }
                catch (Exception ex) { error = ex; }
                try { span?.Dispose(); } catch { }
                // Counters first, then the item's task; Loop clears active after this returns.
                lock (gate)
                {
                    if (error == null) completed++;
                    else failed++;
                }
                if (error == null)
                {
                    item.Completion.TrySetResult(true);
                    return;
                }
                Log("background.work_failed", () => new Dictionary<string, object>
                { ["name"] = item.Name, ["error"] = LimitText(error.Message, 800) });
                item.Completion.TrySetException(error);
            }
        }

        // Admission closes only through StopPriorityBackgroundScheduler, which Program calls from
        // RequestServerShutdown and from the listener teardown (covering exceptional listener exit).
        private static readonly BoundedPriorityScheduler PriorityWork = new BoundedPriorityScheduler(
            "Reign priority background scheduler", PriorityWorkCapacity, PriorityWorkReserve,
            level => InteractiveRequestActiveOrRecent(level >= 2 ? 5000 : 2000), true);

        private static void StartPriorityBackgroundScheduler() => PriorityWork.Start();

        // Program stop seam; see BoundedPriorityScheduler.Stop for the shared settlement fence.
        private static Task StopPriorityBackgroundScheduler(string reason) => PriorityWork.Stop(reason);

        // Legacy admission for callers without ownership to release: on refusal the callback never
        // runs and the returned task is already canceled.
        private static Task EnqueuePriorityBackgroundWork(string name, string priority, Action work)
        {
            if (work == null) return Task.FromResult(true);
            TryEnqueuePriorityBackgroundWork(name, priority, work, null, out Task completion);
            return completion;
        }

        /// <summary>
        /// Bounded admission. Accepted work's completion settles once: success, fault, or canceled
        /// (never started). <paramref name="notStarted"/> runs exactly once, outside the scheduler
        /// lock, iff the work never starts: on this thread before a Busy/Stopping return, or on the
        /// stopping thread for queued work. It must be a cheap, non-blocking ownership release.
        /// </summary>
        private static PriorityWorkAdmission TryEnqueuePriorityBackgroundWork(string name, string priority, Action work,
            Action<string> notStarted, out Task completion)
        {
            return PriorityWork.TryEnqueue(name ?? "background_work", NormalizeWorkPriority(priority), work, notStarted, out completion);
        }

        private static int NormalizeWorkPriority(string priority)
        {
            string value = NormalizeLookup(priority);
            return value == "interactive" || value == "high" ? 0 : value == "maintenance" || value == "low" ? 2 : 1;
        }

        private static bool PriorityBackgroundWorkShouldYield()
        {
            if (ShutdownRequested || InteractiveRequestActiveOrRecent()) return true;
            return PriorityWork.HasQueuedInteractiveOrStopping();
        }

        private static Dictionary<string, object> PriorityBackgroundStatus()
        {
            Dictionary<string, object> status = PriorityWork.Status();
            status["ok"] = true;
            status["chatViewPending"] = ChatViewWorkers.Count;
            status["interactiveRequests"] = Volatile.Read(ref InteractiveRequestCount);
            status["reputationReasonResumeOutcome"] = Volatile.Read(ref ReputationReasonResumeOutcome) ?? "";
            return status;
        }
    }
}
