using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Characters
{
    public static class TavernHouseRules
    {
        public const int Version = 1;
        public const int RetirementAge = 40;
        public const int ReplacementDelayDays = 7;
        public const int MaximumParticipants = 4;

        public static bool IsActive(TavernHousePerson? person) => person != null && person.Initialized && !person.Retired && !person.Recruited;
        public static double ReplacementDay(double departureDay, bool recruited) => departureDay + (recruited ? ReplacementDelayDays : 0);
        public static bool ReplacementDue(TavernHouseSlot slot, double day) => slot != null && string.IsNullOrEmpty(slot.PersonId) && day >= slot.ReplacementDay;

        public static TavernHouseVisitReceipt? GetOpenVisit(IEnumerable<TavernHouseVisitReceipt> visits, string townId, string campaignId, string timelineId)
            => visits.LastOrDefault(v => v.Paid && !v.Ended && v.TownId == townId && v.CampaignId == campaignId && v.TimelineId == timelineId);

        public static bool HasPendingRecruitment(TavernHousePerson? person) => person != null && person.Initialized
            && !person.RecruitmentComplete && !string.IsNullOrWhiteSpace(person.RecruitmentReceipt);

        public static bool CompleteRecruitment(TavernHousePerson person, bool companionTransferred, bool partyTransferred)
        {
            if (person.RecruitmentComplete) return true;
            if (!HasPendingRecruitment(person) || !person.Recruited || !person.RecruitmentGoldPaid || !companionTransferred || !partyTransferred) return false;
            person.RecruitmentComplete = true;
            return true;
        }

        public static TavernHousePerson? SelectMadam(IEnumerable<TavernHousePerson> staff, Func<TavernHousePerson, int> charm, Func<TavernHousePerson, int> roguery)
        {
            var eligible = staff.Where(p => IsActive(p) && p.Female).ToArray();
            return eligible.FirstOrDefault(p => p.Madam) ?? eligible.OrderByDescending(p => charm(p) + roguery(p))
                .ThenByDescending(charm).ThenBy(p => p.Id, StringComparer.Ordinal).FirstOrDefault();
        }

        public static bool ValidateQuote(TavernHouseVisitQuote quote, out string reason)
        {
            reason = "";
            if (quote == null || string.IsNullOrWhiteSpace(quote.AgreementId) || string.IsNullOrWhiteSpace(quote.TownId)
                || string.IsNullOrWhiteSpace(quote.PlayerHeroId) || string.IsNullOrWhiteSpace(quote.CampaignId) || string.IsNullOrWhiteSpace(quote.TimelineId))
                reason = "The visit agreement is missing its identity.";
            else if (quote.Charges == null || quote.Charges.Count < 1 || quote.Charges.Count > MaximumParticipants
                || quote.Charges.Any(c => c == null || string.IsNullOrWhiteSpace(c.HeroId) || c.Gold < 0)
                || quote.Charges.Select(c => c.HeroId).Distinct(StringComparer.Ordinal).Count() != quote.Charges.Count)
                reason = "Agree to one through four distinct participants and an exact nonnegative price for each.";
            else if (quote.Charges.Sum(c => (long)c.Gold) > int.MaxValue || quote.Charges.Sum(c => (long)c.Gold) != quote.TotalGold)
                reason = "The agreement total does not match the individual prices.";
            return reason.Length == 0;
        }

        public static bool Matches(TavernHouseVisitReceipt receipt, TavernHouseVisitQuote quote) => receipt != null && quote != null
            && receipt.AgreementId == quote.AgreementId && receipt.TownId == quote.TownId && receipt.CampaignId == quote.CampaignId
            && receipt.TimelineId == quote.TimelineId && receipt.PlayerHeroId == quote.PlayerHeroId && receipt.PaidGold == quote.TotalGold
            && receipt.QuoteRevision == quote.QuoteRevision && receipt.Charges.Count == quote.Charges.Count
            && receipt.Charges.Zip(quote.Charges, (a, b) => a.HeroId == b.HeroId && a.Gold == b.Gold).All(x => x);

        public static void ValidateCatalog(TavernHouseCastCatalog catalog)
        {
            if (catalog == null || catalog.Version != Version || catalog.Towns == null || catalog.Towns.Count == 0
                || catalog.Towns.Any(t => t == null || string.IsNullOrWhiteSpace(t.TownId) || string.IsNullOrWhiteSpace(t.CultureId) || t.People == null)
                || catalog.Towns.Select(t => t.TownId).Distinct(StringComparer.Ordinal).Count() != catalog.Towns.Count)
                throw new InvalidOperationException("The authored tavern cast catalog is invalid.");
            var all = catalog.Towns.SelectMany(t => t.People).ToArray();
            if (all.Any(p => p == null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Name)
                || string.IsNullOrWhiteSpace(p.AppearanceSeed) || string.IsNullOrWhiteSpace(p.Biography) || p.Age < 19 || p.Age > 30
                || p.BodyKey == null || p.BodyKey.Length != 128 || p.BodyKey.Any(c => !Uri.IsHexDigit(c))
                || double.IsNaN(p.BodyWeight) || double.IsInfinity(p.BodyWeight) || p.BodyWeight < 0 || p.BodyWeight > 1
                || double.IsNaN(p.BodyBuild) || double.IsInfinity(p.BodyBuild) || p.BodyBuild < 0 || p.BodyBuild > 1
                || p.CivilianEquipment == null || !p.CivilianEquipment.Any(e => e != null && e.Slot == "Body" && !string.IsNullOrWhiteSpace(e.ItemId))
                || !p.CivilianEquipment.Any(e => e != null && e.Slot == "Leg" && !string.IsNullOrWhiteSpace(e.ItemId)))
                || all.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != all.Length
                || all.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length
                || all.Select(p => p.BodyKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != all.Length)
                throw new InvalidOperationException("The authored tavern cast has invalid or duplicate adult identities.");
            foreach (var town in catalog.Towns)
            {
                int women = town.People.Count(p => p.Female), men = town.People.Count - women;
                var madams = town.People.Where(p => p.Madam).ToArray();
                if (women < 3 || women > 4 || men < 1 || men > 2 || madams.Length != 1 || !madams[0].Female
                    || town.People.Any(p => p.Charm < (p.Madam ? 200 : 100) || p.Charm > (p.Madam ? 250 : 180)
                        || p.Roguery < (p.Madam ? 200 : 100) || p.Roguery > (p.Madam ? 250 : 180)))
                    throw new InvalidOperationException("The authored tavern cast composition or skills are invalid: " + town.TownId);
            }
        }

        public static void ValidateState(TavernHouseState state)
        {
            if (state == null || state.Version != Version || state.People == null || state.Slots == null || state.Visits == null
                || state.InitializedTownIds == null || state.People.Any(p => p == null || string.IsNullOrEmpty(p.Id))
                || state.People.Select(p => p.Id).Distinct().Count() != state.People.Count
                || state.People.Where(p => !string.IsNullOrEmpty(p.HeroId)).GroupBy(p => p.HeroId).Any(g => g.Count() != 1)
                || state.Slots.Any(s => s == null) || state.Slots.GroupBy(s => s.TownId + ":" + s.Index).Any(g => g.Count() != 1)
                || state.Visits.Any(v => v == null || string.IsNullOrEmpty(v.AgreementId))
                || state.Visits.GroupBy(v => v.CampaignId + ":" + v.TimelineId + ":" + v.AgreementId).Any(g => g.Count() != 1))
                throw new InvalidOperationException("Tavern house save identity ledger is invalid; refusing regeneration.");
            foreach (var slot in state.Slots.Where(s => !string.IsNullOrEmpty(s.PersonId)))
                if (!state.People.Any(p => p.Id == slot.PersonId && p.TownId == slot.TownId && p.Slot == slot.Index && p.Female == slot.Female))
                    throw new InvalidOperationException("Tavern house slot identity is invalid; refusing regeneration.");
        }
    }
}
