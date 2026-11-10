using System;

namespace ReignBeta.Shared.Characters
{
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
