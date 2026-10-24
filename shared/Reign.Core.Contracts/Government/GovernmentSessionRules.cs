using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    public sealed class GovernmentWeightedBallot
    {
        public string VoterId { get; set; } = "";
        public string OptionId { get; set; } = "";
        public decimal Weight { get; set; }
    }

    public static class GovernmentSessionRules
    {
        public const double SeasonDays = 31.5;
        public const int ProtocolVersion = 1;
        public static double SessionCalendarDay(int interval) => interval % 2 == 1
            ? (interval / 2) * SeasonDays + 14
            : (interval / 2 - 1) * SeasonDays + 29;

        public static bool CanStartVoteService(double now, double deliveryDays, double dueDay, double voteDay)
        {
            if (new[] { now, deliveryDays, dueDay, voteDay }.Any(x => double.IsNaN(x) || double.IsInfinity(x) || x < 0))
                return false;
            double earliest = Math.Max(now + deliveryDays, dueDay);
            // A local immediate promise may be made while the due hearing is still
            // open. A courier or future service needs time before the vote boundary.
            return deliveryDays == 0 && dueDay <= now ? now <= voteDay : earliest < voteDay;
        }

        public static double SeasonStart(double day) => Math.Floor(Math.Max(0, day) / SeasonDays) * SeasonDays;
        public static double NextCalendarDay(double day)
        {
            double start = SeasonStart(day);
            return Math.Min(start + Math.Floor(day - start + 0.0001) + 1, start + SeasonDays);
        }
        public static double NextSession(double day)
        {
            double start = Math.Floor(Math.Max(0, day) / SeasonDays) * SeasonDays;
            foreach (double candidate in new[] { start + 14, start + 29, start + SeasonDays + 14 })
                if (candidate > day) return candidate;
            throw new InvalidOperationException("No next government session.");
        }
        public static int CrownWeight(int level) => (5 - Math.Max(1, Math.Min(5, level))) * 25;
        public static decimal MemberWeight(int level, int occupiedSeats) => occupiedSeats <= 0 ? 0 : (100m - CrownWeight(level)) / occupiedSeats;
        public static bool CanRecordBallots(string status, bool ballotsRecorded, bool effectApplied) =>
            (status == "presented" || status == "hearing") && !ballotsRecorded && !effectApplied;
        public static string Winner(IEnumerable<GovernmentWeightedBallot> ballots, IEnumerable<string> options, string statusQuo)
        {
            var valid = (options ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).ToList();
            var votes = (ballots ?? Array.Empty<GovernmentWeightedBallot>()).Where(x => x != null && !string.IsNullOrWhiteSpace(x.VoterId))
                .GroupBy(x => x.VoterId, StringComparer.Ordinal).Select(x => x.Last())
                .Where(x => x.Weight > 0 && valid.Contains(x.OptionId)).ToList();
            if (votes.Count == 0) return string.Empty;
            var totals = valid.Select(id => new { Id = id, Weight = votes.Where(x => x.OptionId == id).Sum(x => x.Weight) }).ToList();
            decimal highest = totals.Max(x => x.Weight);
            var tied = totals.Where(x => Math.Abs(x.Weight - highest) < 0.000001m).Select(x => x.Id).ToList();
            if (tied.Contains(statusQuo)) return statusQuo;
            return tied.OrderBy(x => ReignGovernmentRules.StableHash("government-tie|" + x)).ThenBy(x => x, StringComparer.Ordinal).First();
        }
        // Responsibility diminishes as the Crown loses constitutional weight. Opposition still has a
        // smaller cost when the participant wins; a zero-share recommendation can carry political blame.
        public static int OppositionRelationDelta(int level, bool crownOpposed, bool participantLost)
        {
            if (!crownOpposed) return 0;
            int index = Math.Max(1, Math.Min(5, level)) - 1;
            return -(participantLost ? new[] { 8, 8, 6, 4, 2 } : new[] { 4, 4, 3, 2, 1 })[index];
        }
    }
}
