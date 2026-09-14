using System;
using System.Collections.Generic;

namespace Reign.Shared
{
    public static class ReignKingdomEventRollerCore
    {
        public const int BasisPointScale = 10000;
        public const int DailyTriggerBasisPoints = 100;

        public static uint StableHash(string campaignId, int day, string salt)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string value = (campaignId ?? string.Empty) + "|" + day + "|" + (salt ?? string.Empty);
                for (int index = 0; index < value.Length; index++)
                {
                    hash ^= value[index];
                    hash *= 16777619u;
                }

                // Avalanche the FNV state so modulo-based rolls do not inherit
                // correlations from adjacent decimal day strings.
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
                return hash;
            }
        }

        public static bool IsDailyTrigger(string campaignId, int day)
        {
            return StableHash(campaignId, day, "daily_trigger") % BasisPointScale
                < DailyTriggerBasisPoints;
        }

        public static bool IsBeneficial(string campaignId, int day)
        {
            return (StableHash(campaignId, day, "polarity") & 1u) == 0u;
        }

        public static int SelectIndex(int count, string campaignId, int day, string salt)
        {
            return count <= 0
                ? -1
                : (int)(StableHash(campaignId, day, salt) % (uint)count);
        }

        public static IReadOnlyList<int> BuildAttemptOrder(
            int count,
            string campaignId,
            int day,
            string salt)
        {
            List<int> order = new List<int>();
            if (count <= 0)
            {
                return order;
            }

            int first = SelectIndex(count, campaignId, day, salt);
            for (int offset = 0; offset < count; offset++)
            {
                order.Add((first + offset) % count);
            }
            return order;
        }
    }
}
