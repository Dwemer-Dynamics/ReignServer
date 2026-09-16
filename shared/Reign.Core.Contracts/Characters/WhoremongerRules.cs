using System;

namespace ReignBeta.Shared.Characters
{
    /// <summary>Provider-free rules. A streak ends after inactivity, not at a rolling calendar boundary.</summary>
    public static class WhoremongerRules
    {
        public const string TagId = "whoremonger";
        public const double InactivityDays = 30d;

        public static double PlayerExposureChance(int confirmedVisits)
        {
            return Math.Min(1d, Math.Max(0d, ((double)confirmedVisits - 3d) / 10d));
        }

        public static double NpcExposureChance(double? honor, double? judgment)
        {
            if (!honor.HasValue || !judgment.HasValue
                || double.IsNaN(honor.Value) || double.IsNaN(judgment.Value)
                || honor.Value < 0d || judgment.Value < 0d
                || honor.Value > 40d || judgment.Value > 40d) return 0d;
            return (50d - honor.Value) * (50d - judgment.Value) / 10000d;
        }

        public static bool InactivityElapsed(double lastActivityDay, double currentDay)
        {
            return lastActivityDay >= 0d && currentDay >= lastActivityDay + InactivityDays;
        }

        public static double PromotionChance(int successfulExposures)
        {
            return successfulExposures <= 1 ? 0d : successfulExposures == 2 ? 0.30d
                : successfulExposures == 3 ? 0.60d : 0.90d;
        }

        public static int BaseContribution(bool establishedReputation, bool currentSpouse)
        {
            return currentSpouse ? -20 : establishedReputation ? -10 : -5;
        }
    }
}
