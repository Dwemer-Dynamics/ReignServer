using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Only the synchronous HTTP owner can suspend its read lease. Background
        // workers and write owners keep their existing snapshot exclusion.
        [ThreadStatic] private static CampaignRequestLease CurrentCampaignRequestLease;
        private static long CampaignReplacementGeneration;
        // Isolated concurrency contracts signal the actual monitor wait, rather
        // than relying on scheduling delays. Never installed by runtime code.
        [ThreadStatic] private static Action<object> CampaignWorkLockWaitObserverForTests;

        private sealed class CampaignRequestLease
        {
            internal long Generation;
            internal bool Invalid;
        }

        private sealed class CampaignRequestReplacedException : InvalidOperationException
        {
            internal CampaignRequestReplacedException()
                : base("Campaign state changed while the response was being generated. Retry the interaction in the loaded campaign.") { }
        }

        internal static void InvalidateWaitingCampaignRequests()
        {
            // A conservative process generation also covers old requests whose
            // construction payload did not carry a campaign ID. Snapshot creation
            // never increments this; restore, import and deletion do.
            Interlocked.Increment(ref CampaignReplacementGeneration);
        }

        internal static void ThrowIfCampaignRequestReplaced()
        {
            if (CurrentCampaignRequestLease != null && CurrentCampaignRequestLease.Invalid)
                throw new CampaignRequestReplacedException();
        }

        private static T WithCampaignProviderWait<T>(string requestType, Func<T> wait)
        {
            ThrowIfCampaignRequestReplaced();
            // These call sites finish their DB operations before dispatch and
            // publish only after this scope reacquires campaign access. Codex's
            // conversation-state adapter is deliberately outside this scope.
            bool eligible = requestType == "character_construction"
                || requestType == "social_event" || requestType == "social_event_focus";
            return eligible ? WithCampaignReadLeaseSuspended(wait) : wait();
        }

        private static T WithCampaignReadLeaseSuspended<T>(Func<T> wait)
        {
            ThrowIfCampaignRequestReplaced();
            CampaignRequestLease lease = CurrentCampaignRequestLease;
            if (lease == null || !CampaignDataGate.IsReadLockHeld)
                return wait();
            CampaignDataGate.ExitReadLock();
            try { return wait(); }
            finally
            {
                // Reacquire even when the provider fails. An exception after a
                // restore must not turn into an ordinary cached failure later.
                CampaignDataGate.EnterReadLock();
                if (lease.Generation != Interlocked.Read(ref CampaignReplacementGeneration))
                    lease.Invalid = true;
                ThrowIfCampaignRequestReplaced();
            }
        }

        // Weak keys do not retain abandoned request/character gates. Only scopes
        // using this helper are attributed; direct Monitor owners remain unknown.
        private static readonly ConditionalWeakTable<object, CampaignWorkLockState> CampaignWorkLockStates = new();
        private sealed class CampaignWorkLockState { internal CampaignWorkLock Current; }

        private static string CampaignWorkLockOwner(object gate)
            => CampaignWorkLockStates.TryGetValue(gate, out var state)
                ? Volatile.Read(ref state.Current)?.Source ?? "unknown" : "unknown";

        private sealed class CampaignWorkLock : IDisposable
        {
            private object gate;
            private readonly CampaignWorkLockState state;
            private readonly CampaignWorkLock parent;
            private readonly long started = Stopwatch.GetTimestamp();
            private readonly int ownerThreadId = Environment.CurrentManagedThreadId;
            internal readonly string Source;
            internal readonly long WaitMs;
            private readonly string ownerAtWaitStart;
            private readonly string stageAtWaitStart;
            private string stage = "acquired";
            internal string CurrentStage => Volatile.Read(ref stage);
            internal string OwnerAtWaitStart => ownerAtWaitStart;
            internal void SetStage(string value) => Volatile.Write(ref stage, value);
            internal CampaignWorkLock(object value, string source, long waitMs, string priorOwner, string priorStage)
            {
                gate = value; Source = source; WaitMs = waitMs;
                ownerAtWaitStart = priorOwner; stageAtWaitStart = priorStage;
                state = CampaignWorkLockStates.GetValue(value, _ => new CampaignWorkLockState());
                parent = Volatile.Read(ref state.Current);
                Volatile.Write(ref state.Current, this);
            }
            public void Dispose()
            {
                if (gate == null) return;
                if(Environment.CurrentManagedThreadId!=ownerThreadId)
                    throw new SynchronizationLockException("Campaign work ownership must be disposed on its acquiring thread.");
                double heldMs = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                Volatile.Write(ref state.Current, parent);
                Monitor.Exit(gate);
                bool stillOwned=Monitor.IsEntered(gate);
                gate = null;
                // Logging happens after releasing ownership, is bounded to slow
                // scopes, and cannot change request success or monitor release.
                if (!stillOwned && (heldMs >= 1000d || WaitMs >= 1000))
                    try { LogOperational("campaign.work_lock_slow", TestDict(
                        "owner", Source, "lastStage", Volatile.Read(ref stage),
                        "heldMs", heldMs, "waitMs", WaitMs,
                        "ownerAtWaitStart", ownerAtWaitStart, "stageAtWaitStart", stageAtWaitStart,
                        "attribution", "helper scopes only; wait-start owner is not whole-wait attribution")); }
                    catch { /* Optional diagnostics never change campaign work. */ }
            }
        }

        private static CampaignWorkLock EnterCampaignWorkLock(object gate, [CallerMemberName] string source = "")
        {
            ThrowIfCampaignRequestReplaced();
            bool entered = false;
            long waitStarted = Stopwatch.GetTimestamp();
            string priorOwner = "none", priorStage = "none";
            try
            {
                if (!Monitor.TryEnter(gate))
                {
                    var owner = CampaignWorkLockStates.TryGetValue(gate, out var observed)
                        ? Volatile.Read(ref observed.Current) : null;
                    priorOwner = owner?.Source ?? "unknown";
                    priorStage = owner?.CurrentStage ?? "unknown";
                    CampaignWorkLockWaitObserverForTests?.Invoke(gate);
                    WithCampaignReadLeaseSuspended(() => { Monitor.Enter(gate); entered = true; return true; });
                }
                else entered = true;
                return new CampaignWorkLock(gate, source,
                    (long)((Stopwatch.GetTimestamp() - waitStarted) * 1000d / Stopwatch.Frequency), priorOwner, priorStage);
            }
            catch
            {
                if (entered) Monitor.Exit(gate);
                throw;
            }
        }
    }
}
