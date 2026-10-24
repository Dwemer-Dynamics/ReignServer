using System;

namespace ReignBeta.Shared
{
    /// <summary>A bounded native gossip watch; accessed only on the campaign thread.</summary>
    public sealed class PersonalGossipWakeState
    {
        public long Revision { get; private set; }
        public DateTime UntilUtc { get; private set; } = DateTime.MinValue;
        public bool HasKnownWork { get; private set; }
        public bool IsDisabled { get; private set; }
        public DateTime NextBackgroundPollUtc { get; private set; } = DateTime.MaxValue;

        public void Request(DateTime nowUtc)
        {
            Revision++;
            UntilUtc = nowUtc.AddSeconds(120);
        }

        public void Observe(long requestedRevision, bool enabled, bool hasWork, DateTime nowUtc)
        {
            // A coalesced request made during HTTP must survive the older response.
            if (requestedRevision != Revision) return;
            IsDisabled = !enabled;
            if (!enabled)
            {
                UntilUtc = DateTime.MinValue;
                HasKnownWork = false;
                NextBackgroundPollUtc = nowUtc.AddSeconds(30);
                return;
            }
            if (hasWork && !HasKnownWork) UntilUtc = nowUtc.AddSeconds(120);
            HasKnownWork = hasWork;
            NextBackgroundPollUtc = hasWork ? nowUtc.AddSeconds(30) : DateTime.MaxValue;
            // An empty first response can precede the accepted turn's applied relation
            // receipt. Keep the bounded window; repeated pending polls never extend it.
        }

        // Disabled settings need a cheap readiness probe so a web re-enable works
        // while paused. This does not authorize a roster capture or provider work.
        // Known pending work must not be stranded when a provider or its queue
        // outlasts the fast watch. Slow readiness probes capture no roster until
        // a deadline or completed result actually requires a native clock proof.
        public bool ShouldPoll(DateTime nowUtc) => nowUtc <= UntilUtc
            || (HasKnownWork || IsDisabled) && nowUtc >= NextBackgroundPollUtc;
    }
}
