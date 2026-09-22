using System;

namespace Reign.Core.Contracts.Court
{
    public enum ReignDocketSource
    {
        Notable = 0,
        DomesticNoble = 1,
        International = 2,
        Family = 3,
        Patronage = 4,
        Visitor = 5
    }

    public static class ReignCourtLifeRules
    {
        public const int MaximumDailyEntries = 5;

        public static ReignDocketSource SourceForRoll(int roll)
        {
            if (roll < 0 || roll >= 30) throw new ArgumentOutOfRangeException(nameof(roll));
            if (roll < 6) return ReignDocketSource.Notable;
            if (roll < 12) return ReignDocketSource.Visitor;
            if (roll < 17) return ReignDocketSource.DomesticNoble;
            if (roll < 22) return ReignDocketSource.International;
            if (roll < 27) return ReignDocketSource.Family;
            return ReignDocketSource.Patronage;
        }

        public static ReignDocketSource SourceForSlot(string campaign, string timeline, int day, int slot)
        {
            return SourceForRoll(StableRoll(campaign + "|" + timeline + "|court-life-source|" + day + "|" + slot, 30));
        }

        public static int OrdinarySlots(string campaign, string timeline, int day, int dueReplies)
        {
            int available = MaximumDailyEntries - Math.Max(0, Math.Min(MaximumDailyEntries, dueReplies));
            if (available == MaximumDailyEntries) return ReignRulerDocketRules.DailyOpportunityCount(campaign, timeline, day);
            return available == 0 ? 0 : 1 + StableRoll(campaign + "|" + timeline + "|court-life-replies|" + day, available);
        }

        public static int StableRoll(string seed, int exclusiveMaximum)
        {
            if (exclusiveMaximum <= 0) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in seed ?? string.Empty) { hash ^= c; hash *= 16777619; }
                return (int)(hash % (uint)exclusiveMaximum);
            }
        }
    }
}
