using System;
using System.Linq;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AddGovernmentSessionSelfTests(Action<string, bool, string, object> add)
        {
            AddGovernmentParticipationSelfTests(add);
            var states = new[] { "queued", "presented", "hearing", "execution_failed", "invalidated", "authorized", "awaiting_counterpart", "decided" };
            add("government.recorded_vote_cannot_be_retallied_after_effect_failure", states.All(status =>
                !GovernmentSessionRules.CanRecordBallots(status, true, false)
                && !GovernmentSessionRules.CanRecordBallots(status, false, true)
                && GovernmentSessionRules.CanRecordBallots(status, false, false) == (status == "presented" || status == "hearing")),
                "Saved ballots remain authoritative even if funding or another adapter fails before an effect. Neither a stale hearing state nor a completed effect permits another vote.", states);
            add("government.failed_execution_closure_distinguishes_known_no_effect_from_quarantine",
                GovernmentBusinessRules.IsClosed("invalidated") && !GovernmentBusinessRules.IsClosed("execution_failed")
                && !GovernmentSessionRules.CanRecordBallots("invalidated", true, false)
                && !GovernmentSessionRules.CanRecordBallots("execution_failed", true, false),
                "Proven pre-effect refusals can close as invalidated and permit fresh proposals. Uncertain execution remains quarantined; neither state permits replaying the recorded vote.", null);
            var weights = Enumerable.Range(1, 5).Select(level => GovernmentSessionRules.CrownWeight(level)).ToArray();
            add("government.session_crown_and_occupied_seat_weights", weights.SequenceEqual(new[] { 100, 75, 50, 25, 0 })
                && Enumerable.Range(1, 5).All(level => Math.Abs(GovernmentSessionRules.MemberWeight(level, 7) * 7
                    + GovernmentSessionRules.CrownWeight(level) - 100m) < 0.000001m)
                && GovernmentSessionRules.MemberWeight(3, 0) == 0,
                "The Crown holds the exact constitutional share; the remainder splits equally among occupied seats, and empty institutions invent no seat weight.", weights);

            var schedule = new[] { 0d, 14, 28.9, 29, 31.5, 45.5, 60.5, 125.9, 126 };
            var expected = new[] { 14d, 29, 29, 45.5, 45.5, 60.5, 77, 140, 140 };
            var actual = schedule.Select(GovernmentSessionRules.NextSession).ToArray();
            double campaignYear = 1084 * 126d;
            add("government.session_calendar_fifteenth_thirtieth", actual.SequenceEqual(expected)
                && GovernmentSessionRules.NextSession(13.99999) == 14 && GovernmentSessionRules.NextSession(14) == 29
                && GovernmentSessionRules.NextSession(28.99999) == 29 && GovernmentSessionRules.NextSession(29) == 45.5
                && GovernmentSessionRules.NextSession(campaignYear + 31.5) == campaignYear + 45.5
                && GovernmentSessionRules.NextSession(GovernmentSessionRules.NextSession(campaignYear + 5)) == campaignYear + 29,
                "Calendar offsets14/29 are displayed days15/30; fractional season boundaries and year rollovers preserve introduce-at-N/decide-at-N+1.", new { schedule, actual });
            add("government.news_next_calendar_day_handles_half_day_season",
                GovernmentSessionRules.NextCalendarDay(30.75) == 31
                && GovernmentSessionRules.NextCalendarDay(31.1) == 31.5
                && GovernmentSessionRules.NextCalendarDay(31.5) == 32.5
                && GovernmentSessionRules.NextCalendarDay(campaignYear + 62.9) == campaignYear + 63,
                "The next Reign calendar day respects the half-day end of a season rather than flooring native campaign timestamps.", null);

            var split = new[] { Ballot("a", "claimant_a", 45), Ballot("b", "claimant_b", 35), Ballot("c", "claimant_c", 20) };
            string[] candidates = { "claimant_a", "claimant_b", "claimant_c" };
            add("government.weighted_plurality_needs_no_majority", GovernmentSessionRules.Winner(split, candidates, "") == "claimant_a"
                && GovernmentSessionRules.Winner(split.Reverse(), candidates.Reverse(), "") == "claimant_a",
                "A45/35/20 split selects45 immediately; there is no majority threshold, runoff, ordering dependency or ruler override.", split);

            decimal frozenSeatWeight = GovernmentSessionRules.MemberWeight(2, 5);
            var vacancy = new[] { Ballot("crown", "accept", 75), Ballot("seat_a", "reject", frozenSeatWeight), Ballot("vacated", "", frozenSeatWeight) };
            add("government.frozen_vacancy_share_abstains", frozenSeatWeight == 5
                && GovernmentSessionRules.Winner(vacancy, new[] { "accept", "reject" }, "reject") == "accept"
                && GovernmentSessionRules.Winner(new[] { Ballot("seat_a", "accept", 5), Ballot("crown", "reject", 0) }, new[] { "accept", "reject" }, "reject") == "accept",
                "Frozen vacant shares abstain without redistribution; a zero-weight Crown recommendation cannot defeat a positive member ballot.", vacancy);

            var revoked = new[] { Ballot("a", "accept", 75), Ballot("a", "", 75), Ballot("b", "reject", 25) };
            add("government.weighted_ballot_reentry_and_abstention", GovernmentSessionRules.Winner(revoked, new[] { "accept", "reject" }, "reject") == "reject"
                && GovernmentSessionRules.Winner(new[] { Ballot("", "accept", 100), Ballot("a", "unknown", 100) }, new[] { "accept", "reject" }, "reject") == ""
                && GovernmentSessionRules.Winner(new[] { Ballot("a", "accept", 0) }, new[] { "accept", "reject" }, "reject") == "",
                "The latest ballot per voter is authoritative, including abstention; invalid identities/options and zero-share ballots do not manufacture a decision.", revoked);

            var tied = new[] { Ballot("a", "accept", 50), Ballot("b", "reject", 50) };
            string allocation = GovernmentSessionRules.Winner(new[] { Ballot("a", candidates[0], 50), Ballot("b", candidates[1], 50) }, candidates, "");
            add("government.weighted_ties_preserve_status_quo_or_stable_allocation", GovernmentSessionRules.Winner(tied, new[] { "accept", "reject" }, "reject") == "reject"
                && candidates.Contains(allocation) && allocation == GovernmentSessionRules.Winner(new[] { Ballot("b", candidates[1], 50), Ballot("a", candidates[0], 50) }, candidates.Reverse(), ""),
                "Exact ties retain an available status quo; mandatory allocations choose a stable eligible candidate without a fabricated extra Crown vote.", new { allocation });

            var politicalCosts = Enumerable.Range(2, 4).Select(level => new {
                level, victorious = GovernmentSessionRules.OppositionRelationDelta(level, true, false),
                defeated = GovernmentSessionRules.OppositionRelationDelta(level, true, true) }).ToArray();
            add("government.crown_opposition_responsibility_tapers", politicalCosts.Select(x => x.victorious).SequenceEqual(new[] { -4, -3, -2, -1 })
                && politicalCosts.Select(x => x.defeated).SequenceEqual(new[] { -8, -6, -4, -2 })
                && Enumerable.Range(1, 5).All(level => GovernmentSessionRules.OppositionRelationDelta(level, false, true) == 0),
                "Opposing a participant has a smaller relationship cost even when they win, a greater cost when defeated, and strictly less Crown blame as its constitutional share falls.", politicalCosts);
        }

        private static GovernmentWeightedBallot Ballot(string voter, string option, decimal weight) =>
            new GovernmentWeightedBallot { VoterId = voter, OptionId = option, Weight = weight };
    }
}
