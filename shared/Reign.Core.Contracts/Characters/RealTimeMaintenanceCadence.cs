using System;

namespace ReignBeta.Shared.Characters
{
    // A finite pending cohort can drain without advancing the simulation clock.
    // Completion stops application-tick polling; gameplay can request another pass.
    public sealed class PausedPopulationCatchup
    {
        private readonly object gate = new object();
        private bool pending = true, blocked;
        private int failures;
        public bool Pending { get { lock (gate) return pending; } }
        public bool Blocked { get { lock (gate) return blocked; } }
        public void Request() { lock (gate) { pending = true; blocked = false; failures = 0; } }
        public void Progress() { lock (gate) { blocked = false; failures = 0; } }
        public void Observe(bool workRemaining, bool failed)
        {
            lock (gate)
            {
                pending = workRemaining || failed;
                if (failed) { failures++; blocked = failures >= 3; }
                else if (!pending) { failures = 0; blocked = false; }
            }
        }
        public bool CanPoll(bool currentSession, bool ready, bool inMission, bool saving, bool aligned)
        { lock (gate) return pending && !blocked && currentSession && ready && !inMission && !saving && aligned; }
    }

    // Population upkeep is host polling, not a campaign-time simulation step.
    // Callers pass Stopwatch timestamps so fast-forward cannot multiply scans.
    public sealed class RealTimeMaintenanceCadence
    {
        private bool initialized;
        private long lastObserved;
        private long nextDue;

        public bool TryTake(long monotonicTicks, long ticksPerSecond, bool campaignTimeAdvanced = true)
        {
            if (monotonicTicks < 0) throw new ArgumentOutOfRangeException(nameof(monotonicTicks));
            if (ticksPerSecond <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerSecond));
            if (!campaignTimeAdvanced) return false;
            if (!initialized || monotonicTicks < lastObserved)
            {
                initialized = true;
                lastObserved = monotonicTicks;
                nextDue = checked(monotonicTicks + ticksPerSecond);
                return false;
            }
            lastObserved = monotonicTicks;
            if (monotonicTicks < nextDue) return false;
            // Resume with one bounded pass; never replay every missed poll.
            nextDue = checked(monotonicTicks + ticksPerSecond);
            return true;
        }
    }
}
