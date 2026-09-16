using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.TrainingYard
{
    public static class ReignTrainingYardRules
    {
        public const int RateDivisor = 6;
        public const int TierOneToSixXp = 4750;

        public static int CalculateHourlyXp(int leadership, IEnumerable<int> weaponSkills)
        {
            int bestWeapon = (weaponSkills ?? Array.Empty<int>()).DefaultIfEmpty(0).Max();
            return CalculateHourlyXp(leadership, bestWeapon);
        }

        public static int CalculateHourlyXp(int leadership, int bestWeaponSkill)
        {
            return (Math.Max(0, leadership) + Math.Max(0, bestWeaponSkill)) / RateDivisor;
        }

        public static int CalculateStackAward(int hourlyXpPerTroop, int troopCount, int gainableXp)
        {
            if (hourlyXpPerTroop <= 0 || troopCount <= 0 || gainableXp <= 0) return 0;
            long requested = (long)hourlyXpPerTroop * troopCount;
            return (int)Math.Min(gainableXp, Math.Min(int.MaxValue, requested));
        }

        public static double EstimateHours(int totalXpPerTroop, int hourlyXpPerTroop)
        {
            return totalXpPerTroop <= 0 ? 0d
                : hourlyXpPerTroop <= 0 ? double.PositiveInfinity
                : totalXpPerTroop / (double)hourlyXpPerTroop;
        }

        public static int ResolveScrollIndex(int itemCount, int visibleItems, int currentIndex, int direction)
        {
            int maximum = Math.Max(0, itemCount - Math.Max(1, visibleItems));
            return Math.Max(0, Math.Min(maximum, currentIndex + direction));
        }

        public static float ResolveScrollOffset(int itemCount, int visibleItems, int index, float maximumOffset)
        {
            int maximumIndex = Math.Max(0, itemCount - Math.Max(1, visibleItems));
            if (maximumIndex == 0 || maximumOffset <= 0f) return 0f;
            int clamped = Math.Max(0, Math.Min(maximumIndex, index));
            return maximumOffset * clamped / maximumIndex;
        }
    }
}
