using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunTavernHouseRosterTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
            void Check(string id, Func<object> run)
            {
                try { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = true, ["caseId"] = "tavern_roster_" + id, ["summary"] = id, ["data"] = run() }); }
                catch (Exception ex) { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = false, ["caseId"] = "tavern_roster_" + id, ["summary"] = ex.ToString() }); }
            }
            Check("authored_native_cast_coverage", () =>
            {
                string source = FindVerificationSourceRoot();
                string path = Path.Combine(source, "ReignBeta", "ModuleData", "reign_tavern_cast.json");
                Require(File.Exists(path), "The immutable authored cast fixture is missing from the validation artifact.");
                var catalog = Json.Deserialize<TavernHouseCastCatalog>(File.ReadAllText(path));
                TavernHouseRules.ValidateCatalog(catalog);
                Require(catalog.Towns.Count == 57 && catalog.Towns.Sum(t => t.People.Count) == 284, "Supported native town coverage changed.");
                string[] expected = Enumerable.Range(1, 8).Select(i => "town_A" + i)
                    .Concat(Enumerable.Range(1, 5).Select(i => "town_B" + i))
                    .Concat(Enumerable.Range(1, 6).Select(i => "town_EN" + i))
                    .Concat(Enumerable.Range(1, 7).Select(i => "town_ES" + i))
                    .Concat(Enumerable.Range(1, 6).Select(i => "town_EW" + i))
                    .Concat(Enumerable.Range(1, 6).Select(i => "town_K" + i))
                    .Concat(Enumerable.Range(1, 4).Select(i => "town_N" + i))
                    .Concat(Enumerable.Range(1, 7).Select(i => "town_S" + i))
                    .Concat(new[] { 1, 2, 3, 5, 6, 7, 8, 9 }.Select(i => "town_V" + i)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
                Require(catalog.Towns.Select(t => t.TownId).OrderBy(x => x, StringComparer.Ordinal).SequenceEqual(expected), "A castle, village or missing native town entered the authored topology.");
                Require(catalog.Towns.Select(t => t.CultureId).Distinct().Count() == 7, "Native culture coverage is incomplete.");
                foreach (var town in catalog.Towns)
                {
                    var madam = town.People.Single(p => p.Madam);
                    Require(town.People.Where(p => !p.Madam).All(p => p.Charm < madam.Charm && p.Roguery < madam.Roguery), "Initial madam must lead both social skills.");
                    foreach (var person in town.People)
                        Require(person.CivilianEquipment.GroupBy(p => p.Slot).All(g => g.Count() == 1), "Duplicate authored equipment slot.");
                }
                return new { towns = catalog.Towns.Count, people = catalog.Towns.Sum(t => t.People.Count), nativeAppearanceKeys = 284 };
            });
            Check("permanent_succession_and_ties", () =>
            {
                var old = new TavernHousePerson { Id = "madam", Female = true, Madam = true, Initialized = true };
                var first = new TavernHousePerson { Id = "a", Female = true, Initialized = true };
                var second = new TavernHousePerson { Id = "b", Female = true, Initialized = true };
                var man = new TavernHousePerson { Id = "man", Female = false, Initialized = true };
                var retired = new TavernHousePerson { Id = "retired", Female = true, Initialized = true, Retired = true };
                var staff = new[] { old, second, first, man, retired };
                Require(TavernHouseRules.SelectMadam(staff, p => p == old ? 110 : 180, p => 100) == old, "Higher incoming skills replaced the existing madam.");
                old.Recruited = true;
                Require(TavernHouseRules.SelectMadam(staff, p => 150, p => 150) == first, "Stable identity tie-break or eligibility failed.");
                Require(TavernHouseRules.SelectMadam(staff, p => p == second ? 160 : 150, p => p == second ? 140 : 150) == second, "Charm must break equal combined-skill ties.");
                first.Madam = true;
                Require(TavernHouseRules.SelectMadam(staff, p => p == second ? 250 : 100, p => p == second ? 250 : 100) == first, "Promoted madam was demoted on maintenance.");
                return true;
            });
            Check("seven_day_vacancy_boundary", () =>
            {
                double day = 123.75;
                var slot = new TavernHouseSlot { TownId = "town_EN1", Index = 2, Female = true, ReplacementDay = TavernHouseRules.ReplacementDay(day, true) };
                Require(slot.ReplacementDay == 130.75 && !TavernHouseRules.ReplacementDue(slot, 130.749999)
                    && TavernHouseRules.ReplacementDue(slot, 130.75), "Seven elapsed days must be exact, including fractional day.");
                slot.ReplacementDay = TavernHouseRules.ReplacementDay(day, false);
                Require(TavernHouseRules.ReplacementDue(slot, day), "Retirement/death must not use recruitment delay.");
                slot.PersonId = "new-person";
                Require(!TavernHouseRules.ReplacementDue(slot, day + 100), "An occupied slot must never spawn another person.");
                Require(TavernHouseRules.RetirementAge == 40, "Retirement threshold changed.");
                return true;
            });
            TavernHouseVisitQuote Quote(int count = 1, int amount = 0) => new TavernHouseVisitQuote
            {
                AgreementId = "agreement", CampaignId = "campaign", TimelineId = "main", TownId = "town_EN1", PlayerHeroId = "player", QuoteRevision = 3,
                Charges = Enumerable.Range(0, count).Select(i => new TavernHouseVisitCharge { HeroId = "worker" + i, DisplayName = "Worker " + i, Gold = amount }).ToList(),
                TotalGold = count * amount
            };
            Check("bounded_exact_participant_prices", () =>
            {
                foreach (int count in new[] { 1, 2, 3, 4 }) Require(TavernHouseRules.ValidateQuote(Quote(count, 100), out _), "A valid participant count failed.");
                Require(TavernHouseRules.ValidateQuote(Quote(), out _), "An agreed free visit must be valid.");
                Require(!TavernHouseRules.ValidateQuote(Quote(0), out _) && !TavernHouseRules.ValidateQuote(Quote(5), out _), "Participant bounds were bypassed.");
                var repeated = Quote(2); repeated.Charges[1].HeroId = repeated.Charges[0].HeroId;
                Require(!TavernHouseRules.ValidateQuote(repeated, out _), "One character occupied two agreed slots.");
                var wrongTotal = Quote(2, 50); wrongTotal.TotalGold = 51;
                Require(!TavernHouseRules.ValidateQuote(wrongTotal, out _), "Incorrect total was accepted.");
                Require(!TavernHouseRules.ValidateQuote(Quote(1, -1), out _), "Negative gold was accepted.");
                var overflow = Quote(2); overflow.Charges.ForEach(c => c.Gold = int.MaxValue); overflow.TotalGold = -2;
                Require(!TavernHouseRules.ValidateQuote(overflow, out _), "Payment total overflow was accepted.");
                return true;
            });
            Check("receipt_scope_and_immutability", () =>
            {
                var quote = Quote(2, 15);
                var receipt = new TavernHouseVisitReceipt { VisitId = "visit", AgreementId = quote.AgreementId, TownId = quote.TownId,
                    CampaignId = quote.CampaignId, TimelineId = quote.TimelineId, PlayerHeroId = quote.PlayerHeroId, PaidGold = quote.TotalGold,
                    QuoteRevision = quote.QuoteRevision, Charges = quote.Charges.Select(c => new TavernHouseVisitCharge { HeroId = c.HeroId, Gold = c.Gold }).ToList(),
                    ParticipantHeroIds = quote.Charges.Select(c => c.HeroId).ToList(), Paid = true, StartedDay = 14.5, ConversationId = "conversation" };
                Require(TavernHouseRules.Matches(receipt, quote), "Exact saved receipt did not match its agreement.");
                quote.Charges[0].HeroId = "substitute";
                Require(!TavernHouseRules.Matches(receipt, quote), "Agreement participant substitution was accepted.");
                quote.Charges[0].HeroId = "worker0"; quote.TimelineId = "other";
                Require(!TavernHouseRules.Matches(receipt, quote), "Cross-timeline receipt was accepted.");
                quote.TimelineId = "main"; quote.QuoteRevision++;
                Require(!TavernHouseRules.Matches(receipt, quote), "Changed quote revision reused an old payment.");
                string serialized = Json.Serialize(receipt);
                var restored = Json.Deserialize<TavernHouseVisitReceipt>(serialized);
                Require(Json.Serialize(restored) == serialized && restored.Paid && restored.ConversationId == "conversation", "Native receipt roundtrip changed payment or pending confirmation context.");
                return true;
            });
            Check("unpaid_attempt_reopen_and_exact_retry", () =>
            {
                var quote = Quote(2, 15);
                var failed = new TavernHouseVisitReceipt { VisitId = "failed-payment", AgreementId = quote.AgreementId,
                    TownId = quote.TownId, CampaignId = quote.CampaignId, TimelineId = quote.TimelineId, PlayerHeroId = quote.PlayerHeroId,
                    QuoteRevision = quote.QuoteRevision, Charges = quote.Charges, ParticipantHeroIds = quote.Charges.Select(c => c.HeroId).ToList(),
                    PaidGold = quote.TotalGold, StartedDay = 14.5, Paid = false };
                var state = Json.Deserialize<TavernHouseState>(Json.Serialize(new TavernHouseState { Visits = new List<TavernHouseVisitReceipt> { failed } }));
                TavernHouseVisitReceipt Open() => TavernHouseRules.GetOpenVisit(state.Visits, quote.TownId, quote.CampaignId, quote.TimelineId);
                Require(Open() == null && state.Visits.Count == 1, "An unpaid attempt reopened as a visit or was erased from the receipt ledger.");
                var retry = state.Visits.Single(v => v.AgreementId == quote.AgreementId);
                Require(TavernHouseRules.Matches(retry, quote), "The original unpaid agreement could not be retried exactly after reload.");
                retry.Paid = true;
                Require(Open() == retry && !retry.ServerConfirmed, "A completed debit awaiting server confirmation must reopen for recovery.");
                foreach (string field in new[] { "town", "campaign", "timeline" })
                {
                    var other = Json.Deserialize<TavernHouseVisitReceipt>(Json.Serialize(retry));
                    if (field == "town") other.TownId = "different";
                    if (field == "campaign") other.CampaignId = "different";
                    if (field == "timeline") other.TimelineId = "different";
                    state.Visits.Add(other);
                }
                state.Visits.Add(failed);
                Require(Open() == retry, "Another scope or later unpaid attempt shadowed the paid visit.");
                retry.Ended = true;
                Require(Open() == null, "A completed visit reopened or a failed debit prevented a new negotiation.");
                return true;
            });
            Check("recruitment_recovers_each_native_stage_once", () =>
            {
                var person = new TavernHousePerson { Id = "pending", Initialized = true, RecruitmentReceipt = "consented-agreement", RecruitmentGold = 250 };
                Require(TavernHouseRules.HasPendingRecruitment(person), "An accepted agreement was lost before native transfer.");
                Require(!TavernHouseRules.CompleteRecruitment(person, false, false), "An untransferred agreement was marked complete.");
                person.Recruited = true; person.LeftDay = 123.75;
                double replacementDay = TavernHouseRules.ReplacementDay(person.LeftDay, true);
                Require(!TavernHouseRules.IsActive(person), "Retry would depart the recruited person and schedule a second vacancy.");
                Require(!TavernHouseRules.CompleteRecruitment(person, true, true), "A transferred companion was completed before payment.");
                person.RecruitmentGoldPaid = true;
                person = Json.Deserialize<TavernHousePerson>(Json.Serialize(person));
                Require(TavernHouseRules.HasPendingRecruitment(person), "A paid partial transfer stopped recovering after reload.");
                Require(!TavernHouseRules.CompleteRecruitment(person, true, false), "A clan companion outside the main party was silently skipped.");
                Require(!TavernHouseRules.CompleteRecruitment(person, false, true), "Party membership replaced the required companion transfer.");
                Require(TavernHouseRules.CompleteRecruitment(person, true, true), "Fully transferred and paid recruitment failed to complete.");
                person = Json.Deserialize<TavernHousePerson>(Json.Serialize(person));
                Require(!TavernHouseRules.HasPendingRecruitment(person) && person.RecruitmentGoldPaid && person.RecruitmentGold == 250
                    && person.RecruitmentReceipt == "consented-agreement", "Completed recruitment lost its once-only payment or resumed after reload.");
                Require(TavernHouseRules.CompleteRecruitment(person, false, false), "Later ordinary companion assignment reopened a completed transfer.");
                Require(TavernHouseRules.ReplacementDay(person.LeftDay, true) == replacementDay, "Recovery advanced the seven-day replacement deadline.");
                person.RecruitmentComplete = false; person.RecruitmentReceipt = "";
                Require(!TavernHouseRules.HasPendingRecruitment(person), "Recovery invented consent for a hero without an accepted receipt.");
                return true;
            });
            Check("identity_save_roundtrip_and_corruption", () =>
            {
                var person = new TavernHousePerson { Id = "person", CastId = "authored", TownId = "town_EN1", HeroId = "native-id", Slot = 0,
                    Initialized = true, Female = true, Madam = true, Body = "native-body", RecruitmentReceipt = "recruitment", RecruitmentGold = 350,
                    CivilianEquipment = new List<TavernHouseEquipmentPart> { new TavernHouseEquipmentPart { Slot = "Body", ItemId = "empire_dress" } } };
                var state = new TavernHouseState { People = new List<TavernHousePerson> { person }, InitializedTownIds = new List<string> { "town_EN1" },
                    Slots = new List<TavernHouseSlot> { new TavernHouseSlot { TownId = "town_EN1", Index = 0, Female = true, PersonId = person.Id } } };
                TavernHouseRules.ValidateState(state);
                string serialized = Json.Serialize(state);
                var restored = Json.Deserialize<TavernHouseState>(serialized);
                TavernHouseRules.ValidateState(restored);
                Require(Json.Serialize(restored) == serialized, "Save roundtrip changed identity, native body, outfit, or recruitment receipt.");
                restored.Slots[0].PersonId = "different-identity";
                bool rejected = false; try { TavernHouseRules.ValidateState(restored); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "A corrupted identity slot was silently regenerated.");
                restored = Json.Deserialize<TavernHouseState>(serialized); restored.People.Add(restored.People[0]);
                rejected = false; try { TavernHouseRules.ValidateState(restored); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Duplicate native person identity was accepted.");
                return true;
            });
            return results;
        }
    }
}
