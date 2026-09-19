using System;

namespace ReignBeta.Shared.WarCouncil
{
    public enum ReignWarPartyComposition
    {
        Infantry,
        Missile,
        Mounted
    }

    public readonly struct ReignWarCouncilPoint
    {
        public ReignWarCouncilPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    public static class ReignWarCouncilRules
    {
        public const double WorldMinimum = 0d;
        public const double WorldMaximum = 1040d;
        public const double PlayableMinimumX = 13.353d;
        public const double PlayableMinimumY = 6.733d;
        public const double PlayableMaximumX = 992.535d;
        public const double PlayableMaximumY = 937.948d;
        public const double MinimumZoom = 44d;
        public const double MaximumZoom = 44d;
        public const double MapContentLeft = 1278d / 16384d;
        public const double MapContentTop = 1507d / 16384d;
        public const double MapContentRight = 15106d / 16384d;
        public const double MapContentBottom = 14877d / 16384d;
        public const double IntelligenceSweepIntervalDays = 3d;
        public const double MinimumForeignDetectionRange = 70d;
        public const double MaximumForeignDetectionRange = 520d;
        public const double MinimumForeignDetectionChance = 0.25d;
        public const double MaximumForeignDetectionChance = 0.90d;

        public static string ToRoman(int number)
        {
            if (number < 1 || number > 3999) return number.ToString();
            int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
            string[] glyphs = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
            string result = string.Empty;
            for (int i = 0; i < values.Length; i++)
            {
                while (number >= values[i])
                {
                    result += glyphs[i];
                    number -= values[i];
                }
            }
            return result;
        }

        public static ReignWarPartyComposition ClassifyComposition(int infantry, int missile, int mounted)
        {
            infantry = Math.Max(0, infantry);
            missile = Math.Max(0, missile);
            mounted = Math.Max(0, mounted);
            if (mounted > infantry && mounted >= missile) return ReignWarPartyComposition.Mounted;
            if (missile > infantry && missile > mounted) return ReignWarPartyComposition.Missile;
            return ReignWarPartyComposition.Infantry;
        }

        public static double ClampZoom(double zoom)
        {
            if (double.IsNaN(zoom) || double.IsInfinity(zoom)) return MinimumZoom;
            return Math.Max(MinimumZoom, Math.Min(MaximumZoom, zoom));
        }

        public static bool IsWorldPointValid(double x, double y)
        {
            return !double.IsNaN(x) && !double.IsInfinity(x)
                && !double.IsNaN(y) && !double.IsInfinity(y)
                && x >= PlayableMinimumX && x <= PlayableMaximumX
                && y >= PlayableMinimumY && y <= PlayableMaximumY;
        }

        public static ReignWarCouncilPoint WorldToMap(double worldX, double worldY,
            double mapWidth, double mapHeight)
        {
            double x = Math.Max(PlayableMinimumX, Math.Min(PlayableMaximumX, worldX));
            double y = Math.Max(PlayableMinimumY, Math.Min(PlayableMaximumY, worldY));
            return new ReignWarCouncilPoint(
                (MapContentLeft + (x - PlayableMinimumX) / (PlayableMaximumX - PlayableMinimumX)
                    * (MapContentRight - MapContentLeft)) * Math.Max(0d, mapWidth),
                (MapContentTop + (1d - (y - PlayableMinimumY) / (PlayableMaximumY - PlayableMinimumY))
                    * (MapContentBottom - MapContentTop)) * Math.Max(0d, mapHeight));
        }

        public static ReignWarCouncilPoint MapToWorld(double mapX, double mapY,
            double mapWidth, double mapHeight)
        {
            double safeWidth = Math.Max(1d, mapWidth);
            double safeHeight = Math.Max(1d, mapHeight);
            double contentLeft = safeWidth * MapContentLeft;
            double contentTop = safeHeight * MapContentTop;
            double contentWidth = safeWidth * (MapContentRight - MapContentLeft);
            double contentHeight = safeHeight * (MapContentBottom - MapContentTop);
            double x = Math.Max(contentLeft, Math.Min(contentLeft + contentWidth, mapX));
            double y = Math.Max(contentTop, Math.Min(contentTop + contentHeight, mapY));
            return new ReignWarCouncilPoint(
                PlayableMinimumX + (x - contentLeft) / Math.Max(1d, contentWidth)
                    * (PlayableMaximumX - PlayableMinimumX),
                PlayableMinimumY + (1d - (y - contentTop) / Math.Max(1d, contentHeight))
                    * (PlayableMaximumY - PlayableMinimumY));
        }

        public static string SelectAttackObjective(bool hostile, bool isVillage, bool isFortification)
        {
            if (!hostile) return string.Empty;
            if (isVillage) return "raid";
            if (isFortification) return "besiege_capture";
            return string.Empty;
        }

        public static bool ShouldRetainReport(double reportDay, double currentDay, int newestIndex)
        {
            return newestIndex >= 0 && newestIndex < 100 && reportDay >= currentDay - 30d;
        }

        public static double ForeignPartyDetectionRange(int tactics)
        {
            int bounded = Math.Max(0, Math.Min(300, tactics));
            return MinimumForeignDetectionRange
                + (MaximumForeignDetectionRange - MinimumForeignDetectionRange) * bounded / 300d;
        }

        public static double ForeignPartyDetectionChance(int leadership)
        {
            int bounded = Math.Max(0, Math.Min(250, leadership));
            return MinimumForeignDetectionChance
                + (MaximumForeignDetectionChance - MinimumForeignDetectionChance) * bounded / 250d;
        }

        public static bool IsWithinForeignDetectionRange(double capitalX, double capitalY,
            double partyX, double partyY, int tactics)
        {
            double deltaX = partyX - capitalX;
            double deltaY = partyY - capitalY;
            double range = ForeignPartyDetectionRange(tactics);
            return deltaX * deltaX + deltaY * deltaY <= range * range;
        }

        public static double StableDetectionRoll(string partyId, int sweepIndex)
        {
            unchecked
            {
                uint hash = 2166136261u;
                string key = (partyId ?? string.Empty) + "|" + Math.Max(0, sweepIndex);
                for (int i = 0; i < key.Length; i++)
                {
                    hash ^= key[i];
                    hash *= 16777619u;
                }
                return (hash % 1000000u) / 1000000d;
            }
        }

        public static bool DetectForeignParty(string partyId, int sweepIndex, int tactics, int leadership,
            double capitalX, double capitalY, double partyX, double partyY)
        {
            return IsWithinForeignDetectionRange(capitalX, capitalY, partyX, partyY, tactics)
                && StableDetectionRoll(partyId, sweepIndex) < ForeignPartyDetectionChance(leadership);
        }
    }
}
