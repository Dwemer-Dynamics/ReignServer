using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private sealed class PriorityWorkItem
        {
            public string Id;
            public string Name;
            public int Priority;
            public Action Work;
            public TaskCompletionSource<bool> Completion;
            public DateTime EnqueuedUtc;
        }

        private static readonly object PriorityWorkLock = new object();
        private static readonly Queue<PriorityWorkItem>[] PriorityWorkQueues =
        {
            new Queue<PriorityWorkItem>(), new Queue<PriorityWorkItem>(), new Queue<PriorityWorkItem>()
        };
        private static readonly AutoResetEvent PriorityWorkSignal = new AutoResetEvent(false);
        private static Thread PriorityWorkThread;
        private static long PriorityWorkCompleted;
        private static long PriorityWorkFailed;
        private static string PriorityWorkActiveName = "";

        private static void StartPriorityBackgroundScheduler()
        {
            lock (PriorityWorkLock)
            {
                if (PriorityWorkThread != null && PriorityWorkThread.IsAlive) return;
                PriorityWorkThread = new Thread(PriorityBackgroundLoop)
                {
                    IsBackground = true,
                    Name = "Reign priority background scheduler"
                };
                PriorityWorkThread.Start();
            }
        }

        private static Task EnqueuePriorityBackgroundWork(string name, string priority, Action work)
        {
            if (work == null) return Task.FromResult(true);
            StartPriorityBackgroundScheduler();
            int level = NormalizeWorkPriority(priority);
            PriorityWorkItem item = new PriorityWorkItem
            {
                Id = "work_" + Guid.NewGuid().ToString("N"), Name = name ?? "background_work", Priority = level,
                Work = work, Completion = new TaskCompletionSource<bool>(), EnqueuedUtc = DateTime.UtcNow
            };
            lock (PriorityWorkLock) PriorityWorkQueues[level].Enqueue(item);
            PriorityWorkSignal.Set();
            return item.Completion.Task;
        }

        private static int NormalizeWorkPriority(string priority)
        {
            string value = NormalizeLookup(priority);
            return value == "interactive" || value == "high" ? 0 : value == "maintenance" || value == "low" ? 2 : 1;
        }

        private static void PriorityBackgroundLoop()
        {
            while (!ShutdownRequested)
            {
                PriorityWorkItem item = null;
                lock (PriorityWorkLock)
                {
                    for (int index = 0; index < PriorityWorkQueues.Length; index++)
                        if (PriorityWorkQueues[index].Count > 0) { item = PriorityWorkQueues[index].Dequeue(); break; }
                    PriorityWorkActiveName = item == null ? "" : item.Name;
                }
                if (item == null) { PriorityWorkSignal.WaitOne(500); continue; }
                if (item.Priority > 0)
                {
                    int quietPeriodMs = item.Priority >= 2 ? 5000 : 2000;
                    while (!ShutdownRequested && InteractiveRequestActiveOrRecent(quietPeriodMs)) Thread.Sleep(100);
                }
                try
                {
                    using (ReignTraceScope span = BeginReignSpan("background.work", new Dictionary<string, object>
                    { ["work.id"] = item.Id, ["work.name"] = item.Name, ["work.priority"] = item.Priority, ["queueDelayMs"] = (long)(DateTime.UtcNow - item.EnqueuedUtc).TotalMilliseconds }))
                    {
                        item.Work();
                    }
                    Interlocked.Increment(ref PriorityWorkCompleted);
                    item.Completion.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref PriorityWorkFailed);
                    item.Completion.TrySetException(ex);
                    LogOperational("background.work_failed", new Dictionary<string, object> { ["name"] = item.Name, ["error"] = LimitText(ex.Message, 800) });
                }
                finally { lock (PriorityWorkLock) PriorityWorkActiveName = ""; }
            }
        }

        private static bool PriorityBackgroundWorkShouldYield()
        {
            if (InteractiveRequestActiveOrRecent()) return true;
            lock (PriorityWorkLock) return PriorityWorkQueues[0].Count > 0;
        }

        private static Dictionary<string, object> PriorityBackgroundStatus()
        {
            lock (PriorityWorkLock)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true, ["running"] = PriorityWorkThread != null && PriorityWorkThread.IsAlive,
                    ["active"] = PriorityWorkActiveName, ["interactiveQueued"] = PriorityWorkQueues[0].Count,
                    ["normalQueued"] = PriorityWorkQueues[1].Count, ["maintenanceQueued"] = PriorityWorkQueues[2].Count,
                    ["completed"] = Interlocked.Read(ref PriorityWorkCompleted), ["failed"] = Interlocked.Read(ref PriorityWorkFailed),
                    ["interactiveRequests"] = Volatile.Read(ref InteractiveRequestCount)
                };
            }
        }
    }
}
