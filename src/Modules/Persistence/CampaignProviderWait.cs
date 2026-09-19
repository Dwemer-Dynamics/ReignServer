using System;
using System.Threading;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        // Only the synchronous HTTP owner can suspend its read lease. Background
        // workers and write owners keep their existing snapshot exclusion.
        [ThreadStatic] private static CampaignRequestLease CurrentCampaignRequestLease;
        private static long CampaignReplacementGeneration;

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

        private sealed class CampaignWorkLock : IDisposable
        {
            private object gate;
            internal CampaignWorkLock(object value) { gate = value; }
            public void Dispose()
            {
                if (gate == null) return;
                Monitor.Exit(gate);
                gate = null;
            }
        }

        private static IDisposable EnterCampaignWorkLock(object gate)
        {
            ThrowIfCampaignRequestReplaced();
            bool entered = false;
            try
            {
                if (!Monitor.TryEnter(gate))
                    WithCampaignReadLeaseSuspended(() => { Monitor.Enter(gate); entered = true; return true; });
                else entered = true;
                return new CampaignWorkLock(gate);
            }
            catch
            {
                if (entered) Monitor.Exit(gate);
                throw;
            }
        }
    }
}
