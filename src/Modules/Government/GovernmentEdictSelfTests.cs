using System;
using System.Collections.Generic;
using System.Linq;
using Reign.Core.Contracts.Government;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static void AddGovernmentEdictSelfTests(Action<string, bool, string, object> add)
        {
            Func<string, GovernmentEdictState> state = id => new GovernmentEdictState
            {
                Id = "edict_" + id, EdictId = id, KingdomId = "realm", SettlementId = "town", BeneficiaryClanId = "owner", BusinessId = "vote_" + id,
                EnactedDay = 10, ExpiresDay = GovernmentEdictRules.ExpiryDay(id, 10), BudgetRemaining = GovernmentEdictRules.Find(id).Budget
            };
            add("government.edicts_eight_bounded_voted_definitions", GovernmentEdictRules.All.Count == 8
                && GovernmentEdictRules.All.Select(x => x.Id).Distinct().Count() == 8
                && GovernmentEdictRules.All.All(x => x.DurationDays > 0 && x.DurationDays <= 2 * ReignGovernmentRules.SeasonalMeetingDays
                    && GovernmentEdictRules.ValidState(state(x.Id))),
                "Every approved edict has a unique bounded duration and a valid persisted initial state; no perpetual tax or implicit renewal.", GovernmentEdictRules.All);
            var subsidy = state("extraordinary_subsidy");
            var fractional = state("castle_guard"); fractional.EnactedDay = 10.25; fractional.ExpiresDay = 20.5;
            add("government.edicts_expiry_and_daily_idempotence", GovernmentEdictRules.CanTick(subsidy, 10)
                && GovernmentEdictRules.CanTick(fractional, 10.5) && GovernmentEdictRules.CanTick(fractional, 20.49)
                && !GovernmentEdictRules.CanTick(fractional, 20.5) && !GovernmentEdictRules.CanTick(fractional, 20.75)
                && GovernmentEdictRules.CanRecruit(fractional, 20.49, true, false, true, true, 1)
                && !GovernmentEdictRules.CanRecruit(fractional, 20.5, true, false, true, true, 1)
                && !GovernmentEdictRules.Active(subsidy, 9) && GovernmentEdictRules.Active(subsidy, subsidy.ExpiresDay - .01) && !GovernmentEdictRules.Active(subsidy, subsidy.ExpiresDay)
                && GovernmentEdictRules.ExpiryDay("extraordinary_subsidy", 14) == GovernmentSessionRules.NextSession(GovernmentSessionRules.NextSession(14))
                && !GovernmentEdictRules.CanTick(new GovernmentEdictState { EdictId = subsidy.EdictId, Status = "active", EnactedDay = 10, ExpiresDay = 52, LastTickDay = 20 }, 20),
                "Effects end exactly at the deadline and a saved daily receipt prevents same-day replay.", subsidy);
            var purchases = (from budget in new[] { 0, 1, 9, 100, 1000, int.MaxValue }
                from price in new[] { 0, 1, 10, 1001, int.MaxValue }
                from stock in new[] { 0, 3, 1000 }
                from reserve in new[] { 0, 95, 100 }
                let buy = GovernmentEdictRules.GrainPurchase(budget, price, stock, reserve)
                select new { budget, price, stock, reserve, buy }).ToArray();
            add("government.edicts_granary_purchase_conservation", purchases.All(x => x.buy >= 0 && x.buy <= x.stock
                    && x.buy <= 10 && x.buy + x.reserve <= 100 && (long)x.buy * x.price <= x.budget && (x.price != 0 || x.buy == 0)),
                "The production purchase planner cannot spend beyond escrow, buy nonexistent grain, exceed ten purchases per day or overflow storage, even at extreme input prices.", purchases.Length);
            var distributions = (from stock in new[] { 0, 3, 100 } from room in new[] { 0, 5, 200 }
                from shortage in new[] { false, true } let release = GovernmentEdictRules.GrainDistribution(stock, room, shortage)
                select new { stock, room, shortage, release }).ToArray();
            add("government.edicts_granary_distribution_conservation", distributions.All(x => x.release >= 0 && x.release <= x.stock
                && x.release <= x.room && x.release <= 10 && (x.shortage || x.release == 0)),
                "Food relief consumes finite reserves, fits the food store and cannot run without an actual shortage.", distributions);
            var granary = state("public_granary");
            bool unfunded = GovernmentEdictRules.Effects(new[] { granary }, 10, false).Loyalty == 0;
            granary.LastDistributionDay = 10;
            add("government.edicts_granary_loyalty_only_distribution_day", unfunded
                && GovernmentEdictRules.Effects(new[] { granary }, 10, false).Loyalty == .5
                && GovernmentEdictRules.Effects(new[] { granary }, 11, false).Loyalty == 0,
                "A granary ordinance alone gives no loyalty; the bonus exists only on a recorded actual distribution day.", new { granary.LastDistributionDay });
            var guard = state("castle_guard");
            var exemption = state("harvest_exemption");
            var combined = GovernmentEdictRules.Effects(new[] { guard, exemption }, 10, false);
            guard.LastRecruitDay = 10;
            bool cadence = !GovernmentEdictRules.CanRecruit(guard, 12, true, false, true, true, 1)
                && GovernmentEdictRules.CanRecruit(guard, 13, true, false, true, true, 1);
            var gates = (from village in new[] { false, true } from exempt in new[] { false, true }
                from food in new[] { false, true } from wages in new[] { false, true } from room in new[] { 0, 1 }
                select GovernmentEdictRules.CanRecruit(guard, 13, village, exempt, food, wages, room) == (village && !exempt && food && wages && room > 0)).All(x => x);
            guard.ServiceRecruitsIssued = 30;
            guard.ServiceTroops.Clear(); // Paid recruits may leave normally; this never resets lifetime issuance.
            add("government.edicts_service_real_resource_gates_and_cap", cadence && gates
                && !GovernmentEdictRules.CanRecruit(guard, 13, true, false, true, true, 100)
                && combined.Production == .10 && combined.Loyalty == 0,
                "Service recruitment requires a functioning village, food, wages and space, observes its three-day cadence and thirty-issued cap even after all troops leave; harvest exemption suspends service and its burdens.", combined);
            add("government.edicts_conflicting_native_laws", GovernmentEdictRules.HasConflict("extraordinary_subsidy", new[] { "policy_war_tax" })
                && GovernmentEdictRules.HasConflict("extraordinary_subsidy", new[] { "policy_land_tax" })
                && GovernmentEdictRules.HasConflict("free_market", new[] { "policy_state_monopolies" })
                && GovernmentEdictRules.HasConflict("free_market", new[] { "policy_road_tolls" })
                && !GovernmentEdictRules.HasConflict("free_market", new[] { "policy_senate" }),
                "Extra taxation and market privileges cannot stack with their explicitly conflicting native laws; an unrelated native Senate policy does not alter this economic rule.", new { tax = "war_tax", monopoly = "state_monopolies" });
            var customs = state("local_customs");
            var domestic = GovernmentEdictRules.Effects(new[] { customs }, 10, false);
            var foreign = GovernmentEdictRules.Effects(new[] { customs }, 10, true);
            var market = GovernmentEdictRules.Effects(new[] { state("free_market") }, 10, false);
            var works = GovernmentEdictRules.Effects(new[] { state("public_works") }, 10, false);
            var billeting = GovernmentEdictRules.Effects(new[] { state("garrison_billeting") }, 10, false);
            add("government.edicts_balanced_production_effects", domestic.Loyalty == 0 && foreign.Loyalty == .75 && foreign.Tax == -.10 && foreign.RecruitReplenishment == -.10
                && market.WorkshopProduction == .10 && market.Prosperity == .3 && market.Tariffs == -.15
                && works.Construction == .15 && works.Loyalty == -.3 && works.Production == -.05
                && billeting.Wages == -.15 && billeting.Loyalty == -.5 && billeting.Production == -.05 && billeting.WorkshopProduction == -.05,
                "Each production benefit carries its approved fiscal or local burden; cultural loyalty is conditional and does not mutate culture or government seats.", new { domestic, foreign, market, works, billeting });
            var duplicate = GovernmentEdictRules.Effects(new[] { subsidy, subsidy }, 10, false);
            add("government.edicts_no_duplicate_effect_stacking", duplicate.Tax == .15 && duplicate.Loyalty == -.5,
                "Duplicate state cannot multiply the same settlement edict's effects.", duplicate);
            var invalid = new List<GovernmentEdictState>();
            var bad = state("public_granary"); bad.BudgetRemaining = 1001; invalid.Add(bad);
            bad = state("public_granary"); bad.GrainStock = 1; invalid.Add(bad);
            bad = state("public_granary"); bad.GoldSpent = int.MaxValue; invalid.Add(bad);
            bad = state("castle_guard"); bad.ServiceRecruitsIssued = 31; invalid.Add(bad);
            bad = state("castle_guard"); bad.ServiceTroops["basic"] = 1; invalid.Add(bad);
            bad = state("free_market"); bad.ExpiresDay = 999; invalid.Add(bad);
            bad = state("free_market"); bad.BusinessId = ""; invalid.Add(bad);
            bad = state("public_granary"); bad.Status = "expired"; bad.BudgetRemaining = 0; invalid.Add(bad);
            var saved = System.Text.Json.JsonSerializer.Deserialize<GovernmentEdictState>(System.Text.Json.JsonSerializer.Serialize(state("public_granary")));
            var ended = state("public_granary"); ended.Status = "expired"; ended.BudgetRemaining = 0; ended.GoldSpent = 300; ended.GoldRefunded = 700;
            ended.GrainPurchased = 20; ended.GrainReleased = 5; ended.GrainReturned = 15;
            var closedSaved = System.Text.Json.JsonSerializer.Deserialize<GovernmentEdictState>(System.Text.Json.JsonSerializer.Serialize(ended));
            var pending = state("public_granary"); pending.Status = "ownership_changed";
            pending.BudgetRemaining = 700; pending.GoldSpent = 300; pending.GrainPurchased = 20; pending.GrainReleased = 5; pending.GrainStock = 15;
            pending = System.Text.Json.JsonSerializer.Deserialize<GovernmentEdictState>(System.Text.Json.JsonSerializer.Serialize(pending))!;
            bool held = GovernmentEdictRules.ValidState(pending) && GovernmentEdictRules.HasPendingReturn(pending)
                && GovernmentEdictRules.RefundAmount(pending, false) == 0 && GovernmentEdictRules.ReturnGrainAmount(pending, false) == 0
                && !GovernmentEdictRules.CanTick(pending, 11) && GovernmentEdictRules.Effects(new[] { pending }, 11, false).Loyalty == 0;
            int refund = GovernmentEdictRules.RefundAmount(pending, true);
            pending.BudgetRemaining -= refund; pending.GoldRefunded += refund;
            bool partial = refund == 700 && GovernmentEdictRules.ValidState(pending) && GovernmentEdictRules.HasPendingReturn(pending)
                && GovernmentEdictRules.RefundAmount(pending, true) == 0 && GovernmentEdictRules.ReturnGrainAmount(pending, false) == 0;
            int returned = GovernmentEdictRules.ReturnGrainAmount(pending, true);
            pending.GrainStock -= returned; pending.GrainReturned += returned;
            add("government.edicts_closed_pending_returns", held && partial && returned == 15 && GovernmentEdictRules.ValidState(pending)
                    && !GovernmentEdictRules.HasPendingReturn(pending) && GovernmentEdictRules.ReturnGrainAmount(pending, true) == 0
                    && GovernmentEdictRules.RefundAmount(state("public_granary"), true) == 0,
                "A closed ownership-change record retains escrow and grain across reload without effects; independently available original-owner destinations release exactly the retained amount once, never funding a conqueror or refunding an active edict.", pending);
            add("government.edicts_save_conservation_guards", invalid.All(x => !GovernmentEdictRules.ValidState(x)) && GovernmentEdictRules.ValidState(saved)
                    && GovernmentEdictRules.ValidState(closedSaved) && !GovernmentEdictRules.Active(closedSaved, 11),
                "Save validation rejects invented or missing funds, inventory, troops, missing vote provenance and unbounded expiry. Active and expired/refunded stock-return histories round-trip with exact gold and grain conservation.", new { rejected = invalid.Count, roundTrip = saved, closedRoundTrip = closedSaved });
        }
    }
}
