using System;

namespace Reign.Core.Contracts.Court
{
    public static class ReignCourtAuthorityStability
    {
        public const int RequiredConsecutiveFailures = 3;

        public static int RecordFailure(int currentFailureCount)
        {
            if (currentFailureCount < 0) currentFailureCount = 0;
            return Math.Min(RequiredConsecutiveFailures, currentFailureCount + 1);
        }

        public static int RecordObservedFailure(int currentFailureCount, ref object requestOwner)
        {
            // Grace keeps the Court open, but recovery must never revive an outstanding request.
            requestOwner = new object();
            return RecordFailure(currentFailureCount);
        }

        public static bool ShouldInterrupt(int consecutiveFailureCount)
        {
            return consecutiveFailureCount >= RequiredConsecutiveFailures;
        }
    }
}
