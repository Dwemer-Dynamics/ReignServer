using System;

namespace ReignBeta.Shared
{
    public static class CorrespondenceTransitPolicy
    {
        public const int Version = 2;
        public const double Multiplier = 0.25d;
        public const double LegacyMinimumDays = 0.25d;
        public const double LegacyDefaultDays = 3d;
        public const double PollIntervalGameHours = 1d;

        public static double NormalizeBaseDays(double legacyDays)
        {
            if (double.IsNaN(legacyDays) || double.IsInfinity(legacyDays)) legacyDays = LegacyDefaultDays;
            return Math.Max(LegacyMinimumDays, legacyDays);
        }

        public static bool IsValidTravelMultiplier(double value)
            => !double.IsNaN(value) && !double.IsInfinity(value) && value >= .25d && value <= 4d
                && Math.Abs(value * 4d - Math.Round(value * 4d)) < 1e-8d;

        public static double NewTransitDays(double legacyDays, bool immediate = false, double travelTimeMultiplier = 1d)
            => immediate ? 0d : NormalizeBaseDays(legacyDays) * Multiplier
                * (IsValidTravelMultiplier(travelTimeMultiplier) ? travelTimeMultiplier : 1d);

        public static double ReplyBaseDays(int parentPolicyVersion, double parentBaseDays, double parentActualDays)
            => NormalizeBaseDays(parentPolicyVersion >= 1 && parentPolicyVersion <= Version && parentBaseDays > 0d
                ? parentBaseDays : parentActualDays);

        public static bool ShouldPollAtHour(double currentHour, double previousHour)
        {
            if (double.IsNaN(currentHour) || double.IsInfinity(currentHour)) return false;
            if (double.IsNaN(previousHour) || double.IsInfinity(previousHour)) return true;
            // Native HourlyTick supplies the cadence. Large float campaign days
            // can round an elapsed hour down enough to skip the old day gate.
            return Math.Floor(currentHour / PollIntervalGameHours + 1e-7d)
                != Math.Floor(previousHour / PollIntervalGameHours + 1e-7d);
        }
    }
}
