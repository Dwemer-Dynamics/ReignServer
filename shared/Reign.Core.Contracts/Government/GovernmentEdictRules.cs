using System;
using System.Collections.Generic;
using System.Linq;

namespace Reign.Core.Contracts.Government
{
    public sealed class GovernmentEdictDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Terms { get; set; } = string.Empty;
        public int DurationDays { get; set; }
        public int Budget { get; set; }
        public string[] ConflictingPolicies { get; set; } = Array.Empty<string>();
    }

    public sealed class GovernmentEdictState
    {
        public string Id { get; set; } = string.Empty;
        public string EdictId { get; set; } = string.Empty;
        public string KingdomId { get; set; } = string.Empty;
        public string SettlementId { get; set; } = string.Empty;
        public string BeneficiaryClanId { get; set; } = string.Empty;
        public string FunderHeroId { get; set; } = string.Empty;
        public string BusinessId { get; set; } = string.Empty;
        public string ProjectId { get; set; } = string.Empty;
        public int ProjectLevel { get; set; }
        public string Status { get; set; } = "active";
        public double EnactedDay { get; set; }
        public double ExpiresDay { get; set; }
        public int LastTickDay { get; set; } = -1;
        public int BudgetRemaining { get; set; }
        public int GoldSpent { get; set; }
        public int GoldRefunded { get; set; }
        public int GrainStock { get; set; }
        public int GrainPurchased { get; set; }
        public int GrainReleased { get; set; }
        public int GrainReturned { get; set; }
        public int LastDistributionDay { get; set; } = -1;
        public int ServiceRecruitsIssued { get; set; }
        public int LastRecruitDay { get; set; } = -1;
        public Dictionary<string, int> ServiceTroops { get; set; } = new Dictionary<string, int>(StringComparer.Ordinal);
    }

    public sealed class GovernmentEdictEffects
    {
        public double Loyalty, Prosperity, Tax, Tariffs, Production, Construction, WorkshopProduction, Wages, RecruitReplenishment;
    }

    public static class GovernmentEdictRules
    {
        public const int GranaryCapacity = 100;
        public const int GranaryDailyLimit = 10;
        public const int ServiceCap = 30;
        public static IReadOnlyList<GovernmentEdictDefinition> All { get; } = new[]
        {
            Define("castle_guard", "Castle-Guard Service", "Each functioning bound village supplies one existing basic volunteer every 3 days, up to 30 paid recruits per settlement and 42-day term. The owner pays normal recruitment and continuing wages/food; no recruits when food, wages or room are unavailable. Paid recruits use normal garrison training and transfer rules; withdrawals never reset the 30-issued limit. -0.5 loyalty/day; -5% bound-village production.", 42),
            Define("extraordinary_subsidy", "Extraordinary Subsidy", "+15% settlement tax to the named owning clan; -0.5 loyalty/day. Ends at the second regular session following enactment. Renewal needs another vote. Cannot stack with War Tax, Land Tax, Crown Duty or Debasement of the Currency.", 42, 0, "policy_war_tax", "policy_land_tax", "policy_crown_duty", "policy_debasement_of_the_currency"),
            Define("garrison_billeting", "Garrison Billeting", "-15% garrison wages; -0.5 loyalty/day; -5% local bound-village and workshop production. Ends after 42 days; renewal needs a vote.", 42),
            Define("public_works", "Public Works Obligation", "+15% construction on the named current project; -0.3 loyalty/day; -5% bound-village production. Ends when that project changes/completes or after 21 days.", 21),
            Define("free_market", "Free Market Charter", "+10% workshop production; +0.3 prosperity/day; -15% tariffs. Ends after 42 days. Cannot coexist with State Monopolies or Road Tolls; repeal those privileges through a vote first.", 42, 0, "policy_state_monopolies", "policy_road_tolls"),
            Define("public_granary", "Public Granary Ordinance", "The named owner funds 1000 gold in escrow. Buys actual local-market grain (maximum 10/day, reserve capacity 100), then distributes at most 10/day into food stocks during shortage. +0.5 loyalty/day only on distribution days. Never creates grain or spends beyond the voted budget. Ends after 42 days; unused funds and grain return to their owners.", 42, 1000),
            Define("harvest_exemption", "Harvest Exemption", "+10% bound-village production; -20% recruit replenishment; suspends Castle-Guard Service recruitment and its production/loyalty burden. Ends after 21 days.", 21),
            Define("local_customs", "Charter of Local Customs", "+0.75 loyalty/day only while settlement and owner cultures differ; -10% tax; -10% recruit replenishment. No culture conversion or change to voting membership. Ends after 42 days.", 42)
        };
        private static GovernmentEdictDefinition Define(string id, string name, string terms, int days, int budget = 0, params string[] conflicts)
            => new GovernmentEdictDefinition { Id = id, Name = name, Terms = terms, DurationDays = days, Budget = budget, ConflictingPolicies = conflicts };
        public static GovernmentEdictDefinition? Find(string id) => All.FirstOrDefault(x => x.Id == id);
        public static double ExpiryDay(string edictId, double enactedDay) => edictId == "extraordinary_subsidy"
            ? GovernmentSessionRules.NextSession(GovernmentSessionRules.NextSession(enactedDay)) : enactedDay + (Find(edictId)?.DurationDays ?? 0);
        public static bool Active(GovernmentEdictState state, double day) => state != null && state.Status == "active" && day >= state.EnactedDay && day < state.ExpiresDay;
        public static bool CanTick(GovernmentEdictState state, double day) => Active(state, day) && state.LastTickDay < (int)day;
        public static bool HasPendingReturn(GovernmentEdictState state) => state != null && state.Status != "active"
            && (state.BudgetRemaining > 0 || state.GrainStock > 0);
        public static int RefundAmount(GovernmentEdictState state, bool recipientAvailable) =>
            recipientAvailable && HasPendingReturn(state) && ValidState(state) ? state.BudgetRemaining : 0;
        public static int ReturnGrainAmount(GovernmentEdictState state, bool originalClanInventoryAvailable) =>
            originalClanInventoryAvailable && HasPendingReturn(state) && ValidState(state) ? state.GrainStock : 0;
        public static bool HasConflict(string id, IEnumerable<string> activePolicies) => Find(id)?.ConflictingPolicies.Intersect(activePolicies ?? Array.Empty<string>(), StringComparer.Ordinal).Any() == true;
        public static bool ValidState(GovernmentEdictState state) => state != null && Find(state.EdictId) != null
            && !string.IsNullOrWhiteSpace(state.Id) && !string.IsNullOrWhiteSpace(state.KingdomId) && !string.IsNullOrWhiteSpace(state.SettlementId)
            && !string.IsNullOrWhiteSpace(state.BeneficiaryClanId) && !string.IsNullOrWhiteSpace(state.BusinessId)
            && state.ExpiresDay > state.EnactedDay && state.ExpiresDay - state.EnactedDay <= 42
            && state.BudgetRemaining >= 0 && state.GoldSpent >= 0 && state.GoldRefunded >= 0
            && (long)state.BudgetRemaining + state.GoldSpent + state.GoldRefunded == (Find(state.EdictId)?.Budget ?? 0)
            && state.GrainStock >= 0 && state.GrainStock <= GranaryCapacity && state.GrainPurchased >= 0 && state.GrainReleased >= 0 && state.GrainReturned >= 0
            && (long)state.GrainPurchased - state.GrainReleased - state.GrainReturned == state.GrainStock
            && state.ServiceRecruitsIssued >= 0 && state.ServiceRecruitsIssued <= ServiceCap
            && state.ServiceTroops != null && state.ServiceTroops.All(x => !string.IsNullOrWhiteSpace(x.Key) && x.Value >= 0 && x.Value <= ServiceCap)
            && state.ServiceTroops.Values.Sum(x => (long)x) <= state.ServiceRecruitsIssued;
        public static int GrainPurchase(int budget, int unitPrice, int marketStock, int reserveStock)
            => budget <= 0 || unitPrice <= 0 || marketStock <= 0 || reserveStock < 0 ? 0
                : Math.Max(0, Math.Min(GranaryDailyLimit, Math.Min(marketStock, Math.Min(GranaryCapacity - reserveStock, budget / unitPrice))));
        public static int GrainDistribution(int stock, int foodRoom, bool shortage)
            => shortage ? Math.Max(0, Math.Min(GranaryDailyLimit, Math.Min(stock, foodRoom))) : 0;
        public static bool CanRecruit(GovernmentEdictState state, double day, bool functioningVillage, bool harvestExempt, bool hasFood, bool hasWageBudget, int room)
            => Active(state, day) && state.EdictId == "castle_guard" && functioningVillage && !harvestExempt && hasFood && hasWageBudget
                && room > 0 && state.ServiceRecruitsIssued < ServiceCap && (state.LastRecruitDay < 0 || day - state.LastRecruitDay >= 3);
        public static GovernmentEdictEffects Effects(IEnumerable<GovernmentEdictState> states, double day, bool differentCulture)
        {
            var active = states.Where(x => Active(x, day)).GroupBy(x => x.EdictId).Select(x => x.First()).ToArray();
            bool exempt = active.Any(x => x.EdictId == "harvest_exemption");
            var result = new GovernmentEdictEffects();
            foreach (var state in active)
                switch (state.EdictId)
                {
                    case "castle_guard": if (!exempt) { result.Loyalty -= .5; result.Production -= .05; } break;
                    case "extraordinary_subsidy": result.Tax += .15; result.Loyalty -= .5; break;
                    case "garrison_billeting": result.Wages -= .15; result.Loyalty -= .5; result.Production -= .05; result.WorkshopProduction -= .05; break;
                    case "public_works": result.Construction += .15; result.Loyalty -= .3; result.Production -= .05; break;
                    case "free_market": result.WorkshopProduction += .10; result.Prosperity += .3; result.Tariffs -= .15; break;
                    case "public_granary": if (state.LastDistributionDay == (int)day) result.Loyalty += .5; break;
                    case "harvest_exemption": result.Production += .10; result.RecruitReplenishment -= .20; break;
                    case "local_customs": if (differentCulture) result.Loyalty += .75; result.Tax -= .10; result.RecruitReplenishment -= .10; break;
                }
            return result;
        }
    }
}
