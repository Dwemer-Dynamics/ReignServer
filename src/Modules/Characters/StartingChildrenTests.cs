using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;
using ReignBeta.Family;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunStartingChildrenTests()
        {
            var results = new List<Dictionary<string, object>>();
            Action<string, Func<object>> test = (id, run) =>
            {
                try
                {
                    results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = true,
                        ["suite"] = "characters", ["caseId"] = "starting_children_" + id,
                        ["summary"] = id, ["data"] = run(), ["durationMs"] = 0 });
                }
                catch (Exception ex)
                {
                    results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = false,
                        ["suite"] = "characters", ["caseId"] = "starting_children_" + id,
                        ["summary"] = ex.Message, ["data"] = new { error = ex.ToString() }, ["durationMs"] = 0 });
                }
            };
            test("three_sources_and_age_bands", () =>
            {
                var input = StartingCouple("native").Concat(StartingCouple("war_sails"))
                    .Concat(StartingCouple("reign")).ToList();
                var p = ReignStartingChildrenPlanner.Plan(input);
                RequireStarting(p.Families.Count == 3 && p.Families.All(f => f.Children.Count >= 3 && f.Children.Count <= 5), "Each source must be seeded.");
                RequireStarting(p.Families.All(f => f.Children.Any(c => c.Age <= 5)
                    && f.Children.Any(c => c.Age >= 6 && c.Age <= 11) && f.Children.Any(c => c.Age >= 12)), "Each unconstrained family needs all three age bands.");
                RequireStarting(p.Families.All(f => Math.Abs(f.Children.Count(c => c.Female) - f.Children.Count(c => !c.Female)) <= 1), "Family sexes must be balanced.");
                return p;
            });
            test("existing_children_top_up_without_removal", () =>
            {
                var input = StartingCouple("existing");
                input.Add(StartingSibling("existing", "one", 4.5));
                var first = ReignStartingChildrenPlanner.Plan(input).Families.Single();
                RequireStarting(first.Children.Count == first.Target - 1, "Existing shared minors count toward the target.");
                for (int i = 0; i < 6; i++) input.Add(StartingSibling("existing", "extra" + i, 6 + i));
                var p = ReignStartingChildrenPlanner.Plan(input);
                RequireStarting(p.Added == 0 && p.Families.Single().ExistingMinors == 7, "An over-target family must not be changed.");
                return p;
            });
            test("ruler_couples_not_entire_clans", () =>
            {
                var ruler = StartingCouple("ruler"); ruler[1].Ruler = true;
                var relative = StartingCouple("relative"); relative.ForEach(h => h.ClanId = ruler[0].ClanId);
                var femaleRuler = StartingCouple("queen"); femaleRuler[0].Ruler = true;
                var p = ReignStartingChildrenPlanner.Plan(ruler.Concat(relative).Concat(femaleRuler));
                RequireStarting(p.Families.Count(f => f.Reason == "ruler_couple") == 2, "Both ruler sexes are excluded.");
                RequireStarting(p.Families.Single(f => f.MotherId == "relative_m").Children.Count >= 3, "Non-ruler relatives remain eligible in a ruling clan.");
                return p;
            });
            test("ineligible_and_player_households", () =>
            {
                var input = new List<StartingFamilyHero>();
                var dead = StartingCouple("dead"); dead[1].Alive = false; input.AddRange(dead);
                var widow = StartingCouple("widow"); widow.RemoveAt(1); input.AddRange(widow);
                var former = StartingCouple("former"); former[1].SpouseId = "another"; input.AddRange(former);
                var player = StartingCouple("player"); player[1].PlayerHousehold = true; input.AddRange(player);
                var child = StartingCouple("underage"); child[0].Age = 17; input.AddRange(child);
                var clans = StartingCouple("different_clans"); clans[1].ClanId = "other"; input.AddRange(clans);
                var p = ReignStartingChildrenPlanner.Plan(input);
                RequireStarting(p.Added == 0, "Excluded households must receive no children.");
                return p;
            });
            test("chronology_and_maternal_history", () =>
            {
                for (int age = 18; age <= 68; age++)
                {
                    var input = StartingCouple("ages", age, age + 2);
                    // A deceased child of another father still occupies a historical birth slot.
                    var older = StartingSibling("ages", "deceased", 8.2); older.Alive = false; older.FatherId = "former_father";
                    input.Add(older);
                    var newborn = StartingSibling("ages", "infant", 0.8); input.Add(newborn);
                    var family = ReignStartingChildrenPlanner.Plan(input, age).Families.Single();
                    var ages = new List<double> { older.Age, newborn.Age };
                    foreach (var c in family.Children)
                    {
                        RequireStarting(c.Age >= 1 && c.Age <= 17 && age - c.AgeYears >= 18
                            && age - c.AgeYears <= 45 && age + 2 - c.AgeYears >= 18, "Parent ages at birth must be plausible.");
                        RequireStarting(ages.All(a => Math.Abs(a - c.AgeYears) >= 1), "Birth spacing must include all maternal children.");
                        ages.Add(c.AgeYears);
                    }
                    RequireStarting(family.ExistingMinors == 0, "Neither infants nor deceased half siblings count toward the target.");
                }
                RequireStarting(ReignStartingChildrenPlanner.Plan(StartingCouple("old", 68, 70)).Added == 0, "A mother beyond the plausible range is skipped.");
                return new { maternalAgesTested = 51 };
            });
            test("deterministic_census_order_and_empty_dlc", () =>
            {
                var input = Enumerable.Range(0, 120).SelectMany(i => StartingCouple("reign_" + i)).ToList();
                var json = new JavaScriptSerializer();
                var p = ReignStartingChildrenPlanner.Plan(input);
                RequireStarting(json.Serialize(p) == json.Serialize(ReignStartingChildrenPlanner.Plan(input.AsEnumerable().Reverse())), "Enumeration order must not change the plan.");
                RequireStarting(p.Added >= 360 && p.Added <= 600, "120 empty added households require 360–600 minors.");
                RequireStarting(p.Families.Select(f => f.Target).Distinct().Count() == 3, "Targets must vary between 3, 4 and 5.");
                RequireStarting(ReignStartingChildrenPlanner.Plan(new StartingFamilyHero[0]).Added == 0, "No loaded families is a valid empty plan.");
                return new { households = 120, additions = p.Added };
            });
            test("completed_save_reload_and_old_save_noop", () =>
            {
                var s = new StartingChildrenState { Plan = ReignStartingChildrenPlanner.Plan(StartingCouple("save")) };
                int created = 0, finalized = 0;
                Func<StartingChildSpec, string> create = c => "hero_" + (++created);
                Action<StartingChildSpec, string> finish = (c, id) => finalized++;
                ReignStartingChildrenPlanner.Apply(s, false, create, finish);
                RequireStarting(created == 0 && !s.Completed, "A preexisting save cannot seed even with a populated plan.");
                ReignStartingChildrenPlanner.Apply(s, true, create, finish);
                int count = created;
                var json = new JavaScriptSerializer();
                s = json.Deserialize<StartingChildrenState>(json.Serialize(s));
                ReignStartingChildrenPlanner.Apply(s, true, create, finish);
                ReignStartingChildrenPlanner.Apply(s, false, create, finish);
                ReignStartingChildrenPlanner.Apply(new StartingChildrenState(), false, create, finish);
                RequireStarting(count == created && finalized == count && s.Completed, "Round-trip and repeated events must retain identity and completion without allocation.");
                return s;
            });
            test("partial_failure_receipt_resume", () =>
            {
                var s = new StartingChildrenState { Plan = ReignStartingChildrenPlanner.Plan(StartingCouple("retry")) };
                int created = 0; bool fail = true;
                Func<StartingChildSpec, string> create = c => "retry_" + (++created);
                Action<StartingChildSpec, string> finish = (c, id) => { if (fail) throw new InvalidOperationException("injected initialization failure"); };
                try { ReignStartingChildrenPlanner.Apply(s, true, create, finish); } catch (InvalidOperationException) { }
                RequireStarting(created == 1 && s.Receipts.Count == 1 && !s.Completed && s.Error != null, "Allocation must be retained when finalization fails.");
                ReignStartingChildrenPlanner.Apply(s, false, create, finish);
                RequireStarting(created == 1, "Loading a partial state must not resume campaign mutations.");
                fail = false;
                ReignStartingChildrenPlanner.Apply(s, true, create, finish);
                RequireStarting(created == s.Plan.Added && s.Receipts.All(r => r.Finalized) && s.Error == null, "New-game retry must reuse the allocated child.");
                return s;
            });
            test("invalid_census_and_native_age_limit", () =>
            {
                bool rejected = false;
                var input = StartingCouple("duplicate"); input.Add(input[0]);
                try { ReignStartingChildrenPlanner.Plan(input); } catch (ArgumentException) { rejected = true; }
                RequireStarting(rejected, "Duplicate hero identities must fail before mutation.");
                var p = ReignStartingChildrenPlanner.Plan(StartingCouple("age_limit"), comesOfAge: 16);
                RequireStarting(p.Families.SelectMany(f => f.Children).All(c => c.Age < 16), "Never create an adult under a changed native age model.");
                return p;
            });
            test("uncertain_native_allocation_fails_closed", () =>
            {
                var s = new StartingChildrenState { Plan = ReignStartingChildrenPlanner.Plan(StartingCouple("uncertain")) };
                int calls = 0;
                Func<StartingChildSpec, string> create = c => { calls++; throw new InvalidOperationException("native event failed after allocation"); };
                for (int i = 0; i < 2; i++)
                    try { ReignStartingChildrenPlanner.Apply(s, true, create, (c, id) => { }); } catch (InvalidOperationException) { }
                RequireStarting(calls == 1 && s.UncertainCreationSlot != null && !s.Completed, "Uncertain allocations must never be retried as fresh children.");
                return s;
            });
            test("native_initialization_and_catalog_contract", () =>
            {
                string root = FindVerificationSourceRoot();
                string adapter = File.ReadAllText(Path.Combine(root, "ReignBeta", "src", "Modules", "Characters", "Campaign", "ReignStartingChildrenCampaignBehavior.cs"));
                string submodule = File.ReadAllText(Path.Combine(root, "ReignBeta", "src", "Modules", "Platform", "SubModule.cs"));
                string catalog = File.ReadAllText(Path.Combine(root, "reign.testing.json"));
                RequireStarting(adapter.Contains("HeroCreator.CreateChild(") && adapter.Contains("if (_loadedSave || _state.Completed) return;")
                    && !adapter.Contains("OnGameLoadedEvent.Add") && !adapter.Contains("DailyTickEvent.Add")
                    && !adapter.Contains("DeliverOffSpring(") && !adapter.Contains(".IsPregnant =")
                    && !adapter.Contains(".OnGivenBirth(") && !adapter.Contains("ChangeState("), "Seeding must be new-game-only and preserve pregnancy and native child lifecycle.");
                RequireStarting(submodule.IndexOf("new ReignStartingChildrenCampaignBehavior()", StringComparison.Ordinal)
                    > submodule.IndexOf("new ReignCourtNobleCampaignBehavior()", StringComparison.Ordinal), "Court household initialization must precede child creation.");
                RequireStarting(catalog.Contains("contracts.starting_children") && catalog.Contains("reign-starting-children-runtime-v1")
                    && catalog.Contains("reign-starting-children-evidence-v1"), "Testing catalog must document the check and observation schemas.");
                return new { nativeExecutionRequired = true, sourceContractOnly = true };
            });
            return results;
        }

        private static List<StartingFamilyHero> StartingCouple(string id, double motherAge = 42, double fatherAge = 44) => new List<StartingFamilyHero>
        {
            new StartingFamilyHero { Id = id + "_m", SpouseId = id + "_f", ClanId = id, Source = id,
                Age = motherAge, Female = true, Alive = true, EligibleParent = true },
            new StartingFamilyHero { Id = id + "_f", SpouseId = id + "_m", ClanId = id, Source = id,
                Age = fatherAge, Alive = true, EligibleParent = true }
        };

        private static StartingFamilyHero StartingSibling(string family, string id, double age) => new StartingFamilyHero
        {
            Id = family + "_" + id, MotherId = family + "_m", FatherId = family + "_f", ClanId = family,
            Age = age, Alive = true
        };

        private static void RequireStarting(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
