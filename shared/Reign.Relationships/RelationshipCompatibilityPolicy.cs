using System;
using System.Collections.Generic;

namespace Reign.Relationships
{
    public static class RelationshipCompatibilityPolicy
    {
        public const int UnknownBaseScore = 60;
        public const int MinimumAdjustedScore = 19;
        public const int MaximumAdjustedScore = 81;

        private const int LegacyPenalty = 10;
        private const int LegacyMinimum = 36;
        private const int LegacyMaximum = 83;

        private static readonly string[] TypesInternal =
        {
            "INTJ", "INTP", "ENTJ", "ENTP",
            "INFJ", "INFP", "ENFJ", "ENFP",
            "ISTJ", "ISFJ", "ESTJ", "ESFJ",
            "ISTP", "ISFP", "ESTP", "ESFP"
        };

        private static readonly int[,] Scores =
        {
            {70,68,88,60,70,62,62,54,70,54,62,46,62,88,86,92},
            {68,70,60,92,62,70,87,62,62,88,54,88,70,54,62,46},
            {91,60,70,68,62,87,70,62,62,46,70,54,54,91,62,90},
            {60,93,68,70,54,62,62,70,87,91,62,89,62,46,70,54},
            {70,62,62,54,70,68,92,60,54,70,46,62,92,62,89,87},
            {62,70,85,62,68,70,60,91,88,62,89,54,54,70,46,62},
            {62,88,70,62,90,60,70,68,46,62,54,70,89,54,91,62},
            {54,62,62,70,60,91,68,70,92,87,91,62,46,62,54,70},
            {70,62,62,88,54,90,46,89,70,76,93,68,62,62,54,54},
            {54,89,46,90,70,62,62,84,76,70,68,89,62,62,54,54},
            {62,54,70,62,46,91,54,90,92,68,70,76,54,85,62,62},
            {46,93,54,92,62,54,70,62,68,89,76,70,85,54,62,62},
            {62,70,54,62,88,54,92,46,62,62,54,86,70,76,91,68},
            {89,54,92,46,62,70,54,62,62,62,88,54,76,70,68,91},
            {88,62,62,70,90,46,92,54,54,54,62,62,92,68,70,76},
            {91,46,88,54,86,62,62,70,54,54,62,62,68,88,76,70}
        };

        public static IReadOnlyList<string> Types => TypesInternal;

        public static int BaseScore(string? observerType, string? targetType)
        {
            var observer = Array.FindIndex(TypesInternal,
                value => value.Equals(observerType,
                    StringComparison.OrdinalIgnoreCase));
            var target = Array.FindIndex(TypesInternal,
                value => value.Equals(targetType,
                    StringComparison.OrdinalIgnoreCase));
            return observer < 0 || target < 0
                ? UnknownBaseScore
                : Scores[observer, target];
        }

        public static int AdjustedScore(int baseScore)
        {
            var legacyChance = Clamp(baseScore - LegacyPenalty, 0, 100);
            var position = (legacyChance - LegacyMinimum)
                / (double)(LegacyMaximum - LegacyMinimum);
            return Clamp(RoundAwayFromZero(MinimumAdjustedScore
                    + position * (MaximumAdjustedScore - MinimumAdjustedScore)),
                MinimumAdjustedScore,
                MaximumAdjustedScore);
        }

        public static IReadOnlyList<int> AllBaseScores()
        {
            var result = new int[TypesInternal.Length * TypesInternal.Length];
            var index = 0;
            for (var row = 0; row < TypesInternal.Length; row++)
            for (var column = 0; column < TypesInternal.Length; column++)
                result[index++] = Scores[row, column];
            return result;
        }

        private static int Clamp(int value, int minimum, int maximum) =>
            Math.Max(minimum, Math.Min(maximum, value));

        private static int RoundAwayFromZero(double value) =>
            (int)Math.Round(value, MidpointRounding.AwayFromZero);
    }
}
