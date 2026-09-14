using System;

namespace Reign.Core.Contracts.Court
{
    /// <summary>Provider-free attention and favor rules; campaign adapters own identity and receipts.</summary>
    public static class ReignFamilyVisitRules
    {
        public const int RequiredPlayerTurns = 2;
        public const double MinimumVisitorAge = 4d;
        public const int FavorContactLifetimeDays = 30;
        public static int DismissalTolerance(int patiencePercent) => (int)Math.Ceiling(Math.Max(0, Math.Min(100, patiencePercent)) / 10d);
        public static bool IsNeglected(int dismissals, int patiencePercent) => dismissals > 0 && dismissals >= DismissalTolerance(patiencePercent);
        public static int AfterSuccessfulVisit(int dismissals) => Math.Max(0, dismissals - 1);
        public static int DecayDelta(int relation, int elapsedDays) => -Math.Min(Math.Max(0, relation), Math.Max(0, elapsedDays));
        public static int PersonalAffinityForEffectiveRelation(int effectiveRelation, int publicStanding)
            => (int)Math.Max(-100L, Math.Min(100L, (long)Math.Max(-100, Math.Min(100, effectiveRelation)) - publicStanding));
        public static bool FavorExpired(double lastContactOrGraceDay, double now) => now - lastContactOrGraceDay >= FavorContactLifetimeDays;
        public static double FavorJealousyWeight(bool observerFavored, bool rivalFavored) => observerFavored && rivalFavored ? 0.5d : 1d;
        public static double NextCourtMorning(double day) => CourtDay(day) + 1d + 8d / 24d;
        public static int CourtDay(double day) => (int)Math.Floor(day - 8d / 24d + 0.0000001d);
    }
}
