using System;

namespace ReignBeta.Shared.WarCouncil
{
    /// <summary>
    /// Deterministic row-boundary rules shared by the native War Council roster
    /// widget and its provider-free verification harness.
    /// </summary>
    public static class ReignWarCouncilScrollRules
    {
        public const double LordRowStride = 160d;
        public const double LordViewportHeight = 640d;
        public const int LordVisibleRowCount = 4;

        private const double BoundaryEpsilon = 0.0001d;

        public static double MaximumWholeRowOffset(double maximumOffset)
        {
            if (double.IsNaN(maximumOffset) || double.IsInfinity(maximumOffset) || maximumOffset <= 0d)
                return 0d;

            return Math.Max(0d,
                Math.Floor((maximumOffset + BoundaryEpsilon) / LordRowStride) * LordRowStride);
        }

        public static double SnapToWholeRow(double offset, double maximumOffset)
        {
            double maximum = MaximumWholeRowOffset(maximumOffset);
            double bounded = Bound(offset, maximum);
            double snapped = Math.Round(bounded / LordRowStride, MidpointRounding.AwayFromZero)
                * LordRowStride;
            return Math.Max(0d, Math.Min(maximum, snapped));
        }

        public static double StepWholeRow(double currentOffset, double wheelDelta, double maximumOffset)
        {
            double maximum = MaximumWholeRowOffset(maximumOffset);
            double current = Bound(currentOffset, maximum);
            if (double.IsNaN(wheelDelta) || double.IsInfinity(wheelDelta) || Math.Abs(wheelDelta) < double.Epsilon)
                return SnapToWholeRow(current, maximum);

            double currentRow = current / LordRowStride;
            double targetRow = wheelDelta > 0d
                ? Math.Ceiling(currentRow - BoundaryEpsilon) - 1d
                : Math.Floor(currentRow + BoundaryEpsilon) + 1d;
            return Math.Max(0d, Math.Min(maximum, targetRow * LordRowStride));
        }

        public static double SelectedRowOffset(int selectedIndex, double maximumOffset)
        {
            if (selectedIndex < 0) return 0d;
            int firstVisibleRow = Math.Max(0, selectedIndex - LordVisibleRowCount / 2);
            return SnapToWholeRow(firstVisibleRow * LordRowStride, maximumOffset);
        }

        private static double Bound(double offset, double maximum)
        {
            if (double.IsNaN(offset) || double.IsInfinity(offset)) return 0d;
            return Math.Max(0d, Math.Min(maximum, offset));
        }
    }
}
