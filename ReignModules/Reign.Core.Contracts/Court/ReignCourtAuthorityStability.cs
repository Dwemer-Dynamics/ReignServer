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

        public static bool ShouldInterrupt(int consecutiveFailureCount)
        {
            return consecutiveFailureCount >= RequiredConsecutiveFailures;
        }
    }
}
