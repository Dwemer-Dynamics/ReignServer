using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ReignBeta.Shared.Characters;

namespace Reign.Core.Contracts.Actions
{
    public sealed class TavernHouseActionPerson
    {
        public string HeroId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Available { get; set; }
        public bool Adult { get; set; }
    }

    public sealed class TavernHouseActionContext
    {
        public string CampaignId { get; set; } = "";
        public string TimelineId { get; set; } = "";
        public string TownId { get; set; } = "";
        public string SessionId { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string MadamId { get; set; } = "";
        public List<TavernHouseActionPerson> Roster { get; set; } = new List<TavernHouseActionPerson>();
        public string RosterHash => TavernHouseVisitAction.Hash(new[] { MadamId }.Concat(Roster.OrderBy(p => p.HeroId, StringComparer.Ordinal)
            .SelectMany(p => new[] { p.HeroId, p.Name, p.Available ? "1" : "0", p.Adult ? "1" : "0" })));
    }

    public static class TavernHouseVisitAction
    {
        public const string Id = "tavern.visit.pay";
        public static bool Contains(ActionAgreement? agreement) => agreement?.Clauses?.Any(c => c != null && c.ActionId == Id) == true;
        public static bool Visible(ActionDefinition definition, TavernHouseActionContext? scope, bool playerActs, int existingTerms)
            => definition.Id != Id || scope != null && playerActs && existingTerms == 0;
        public static string Arg(ActionClause c, string key) => c.Arguments.TryGetValue(key, out var value) ? value : "";
        public static string Hash(IEnumerable<string> fields)
        {
            var b = new StringBuilder();
            foreach (string field in fields) { string s = field ?? ""; b.Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(s); }
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(b.ToString()))).Replace("-", "").ToLowerInvariant();
        }
        public static void Bind(ActionClause c, TavernHouseActionContext scope)
        {
            c.Arguments["campaignId"] = scope.CampaignId; c.Arguments["timelineId"] = scope.TimelineId;
            c.Arguments["townId"] = scope.TownId; c.Arguments["sessionId"] = scope.SessionId;
            c.Arguments["rosterHash"] = scope.RosterHash;
        }
        public static List<TavernHouseVisitCharge> Charges(ActionClause c)
        {
            var result = new List<TavernHouseVisitCharge>(); bool ended = false;
            for (int i = 1; i <= 4; i++)
            {
                string id = Arg(c, "participant" + i), price = Arg(c, "price" + i);
                if (id.Length == 0 && price.Length == 0) { ended = true; continue; }
                if (ended || id.Length == 0 || !int.TryParse(price, NumberStyles.None, CultureInfo.InvariantCulture, out int gold))
                    throw new ArgumentException("Select one to four named participants with an exact nonnegative whole-denar price for each.");
                result.Add(new TavernHouseVisitCharge { HeroId = id, Gold = gold });
            }
            if (result.Count == 0 || result.Select(p => p.HeroId).Distinct(StringComparer.Ordinal).Count() != result.Count)
                throw new ArgumentException("A visit requires one to four distinct participants.");
            if (result.Sum(p => (long)p.Gold) > int.MaxValue) throw new ArgumentException("The visit total is too large.");
            return result;
        }
        public static List<string> Validate(ActionAgreement a, TavernHouseActionContext? scope = null)
        {
            var errors = new List<string>();
            if (!Contains(a)) return errors;
            if (a.Clauses.Count != 1 || a.AdvancePayment || a.Origin != ActionOrigin.Conversation)
                errors.Add("A Tavern House booking is one standalone conversation action, including its entire payment.");
            var c = a.Clauses.First(x => x.ActionId == Id);
            if (c.ActorId != a.PlayerId || c.RecipientId != a.NpcId) errors.Add("The player pays the current madam for this visit.");
            if (c.Timing != TermTiming.Immediate || c.DueDay != 0 || c.DurationDays != 0 || c.Installments != 1
                || c.IntervalDays != 0 || c.ConditionKind.Length != 0 || c.ConditionTargetId.Length != 0)
                errors.Add("Tavern House visits cannot be scheduled, conditional or paid in installments.");
            try
            {
                var charges = Charges(c);
                if (!int.TryParse(Arg(c, "totalGold"), NumberStyles.None, CultureInfo.InvariantCulture, out int total)
                    || total != charges.Sum(p => (long)p.Gold)) errors.Add("The total must equal every selected participant's price.");
                if (scope != null)
                {
                    if (a.PlayerId != scope.PlayerId || a.NpcId != scope.MadamId
                        || Arg(c, "campaignId") != scope.CampaignId || Arg(c, "timelineId") != scope.TimelineId
                        || Arg(c, "townId") != scope.TownId || Arg(c, "sessionId") != scope.SessionId || Arg(c, "rosterHash") != scope.RosterHash)
                        errors.Add("This booking belongs to a different or changed Tavern House session.");
                    if (charges.Any(p => scope.Roster.Count(r => r.HeroId == p.HeroId && r.Adult && r.Available) != 1))
                        errors.Add("Every selected participant must be an available adult in this house.");
                }
            }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
            return errors;
        }
        public static string Fingerprint(ActionAgreement a)
        {
            var c = a.Clauses.Single(x => x.ActionId == Id);
            return Hash(new[] { "reign-tavern-visit-v1", a.PlayerId, a.NpcId, Arg(c, "campaignId"), Arg(c, "timelineId"),
                Arg(c, "townId"), Arg(c, "sessionId"), Arg(c, "rosterHash"), Arg(c, "totalGold") }
                .Concat(Charges(c).OrderBy(p => p.HeroId, StringComparer.Ordinal).SelectMany(p => new[] { p.HeroId, p.Gold.ToString(CultureInfo.InvariantCulture) })));
        }
        public static TavernHouseVisitQuote Quote(ActionAgreement a, TavernHouseActionContext scope)
        {
            var errors = Validate(a, scope); if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
            var charges = Charges(a.Clauses.Single());
            foreach (var charge in charges) charge.DisplayName = scope.Roster.Single(p => p.HeroId == charge.HeroId).Name;
            return new TavernHouseVisitQuote { AgreementId = a.Id, QuoteRevision = a.Revision, CampaignId = scope.CampaignId,
                TimelineId = scope.TimelineId, TownId = scope.TownId, PlayerHeroId = scope.PlayerId, Charges = charges,
                TotalGold = checked((int)charges.Sum(p => (long)p.Gold)) };
        }
        public static bool TryPay(TavernHouseVisitReceipt receipt, Func<int> wallet, Action transfer)
        {
            if (receipt.PaymentState == "paid") return receipt.Paid;
            if (receipt.PaymentState.Length != 0) return false;
            receipt.PaymentBeforeGold = wallet(); receipt.PaymentState = "applying";
            try
            {
                transfer(); receipt.PaymentAfterGold = wallet(); receipt.Paid = true; receipt.PaymentState = "paid";
                return true;
            }
            catch
            {
                receipt.PaymentAfterGold = wallet(); receipt.Paid = false; receipt.PaymentState = "recovery_required";
                return false;
            }
        }
        public static string Sentence(ActionAgreement a, TavernHouseActionContext scope)
        {
            var quote = Quote(a, scope);
            string names = string.Join(", ", quote.Charges.Select(p => p.DisplayName));
            string prices = quote.Charges.Count > 1 ? " (" + string.Join("; ", quote.Charges.Select(p => p.DisplayName + ": " + p.Gold.ToString("N0", CultureInfo.InvariantCulture) + " denars")) + ")" : "";
            return quote.TotalGold == 0 ? "I wish to accept the agreed free private visit with " + names
                : "I wish to pay you " + quote.TotalGold.ToString("N0", CultureInfo.InvariantCulture) + " denars for a private visit with " + names + prices;
        }
    }
}
