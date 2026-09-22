using System;
using System.Collections.Generic;
using System.Linq;
using ReignBeta.Shared.Characters;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunWandererPopulationTests()
        {
            var results = new List<Dictionary<string, object>>();
            void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
            void Check(string id, Func<object> run)
            {
                try { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = true, ["caseId"] = "wanderer_" + id, ["summary"] = id, ["data"] = run() }); }
                catch (Exception ex) { results.Add(new Dictionary<string, object> { ["ok"] = true, ["passed"] = false, ["caseId"] = "wanderer_" + id, ["summary"] = ex.ToString() }); }
            }
            WandererPresence Present(string hero, string town) => new WandererPresence { HeroId = hero, TownId = town, Alive = true, Adult = true, Free = true, Independent = true, Available = true, SafeToMove = true };
            var towns = Enumerable.Range(0, 60).Select(i => new WandererTown { Id = "town" + i, Safe = true }).ToArray();
            Check("current_town_menu_priority_and_mission_gate", () => {
                Require(WandererPopulationRules.CanMaintainTown(true, false, false, false), "A safe current town menu was excluded from maintenance.");
                Require(!WandererPopulationRules.CanMaintainTown(true, false, false, true), "An active mission allowed population mutation.");
                Require(!WandererPopulationRules.CanMaintainTown(true, true, false, false)
                    && !WandererPopulationRules.CanMaintainTown(true, false, true, false), "A siege allowed population mutation.");
                var localFirst = WandererPopulationRules.Plan(
                    new[] { new WandererTown { Id = "other", Safe = true }, new WandererTown { Id = "current", Safe = true } },
                    new[] { Present("native-current", "current") }, 1, "current");
                Require(localFirst.SpawnTowns.SequenceEqual(new[] { "current" }), "The player's underfilled current town was not topped up first.");
                return localFirst;
            });
            Check("fill_sixty_towns_and_recruitment", () => {
                var people = new List<WandererPresence>();
                for (int round = 0; round < 60; round++)
                {
                    var plan = WandererPopulationRules.Plan(towns, people);
                    Require(plan.SpawnTowns.Count + plan.Moves.Count <= 4, "Creation exceeded its batch budget.");
                    foreach (string town in plan.SpawnTowns) people.Add(Present("person" + people.Count, town));
                }
                Require(people.Count == 240 && towns.All(t => people.Count(p => p.TownId == t.Id) == 4), "Taverns failed to fill to four.");
                Require(WandererPopulationRules.Plan(towns, people).SpawnTowns.Count == 0, "Repeated initialization duplicates heroes.");
                people[0].Independent = false;
                var replacement = WandererPopulationRules.Plan(towns, people);
                Require(replacement.SpawnTowns.SequenceEqual(new[] { people[0].TownId }), "Recruitment did not free exactly one slot.");
                return new { towns = towns.Length, available = people.Count, replacement = replacement.SpawnTowns };
            });
            Check("relocate_before_spawn_and_unsafe_towns", () => {
                var locations = new[] { new WandererTown { Id = "a", Safe = true }, new WandererTown { Id = "b", Safe = true }, new WandererTown { Id = "siege", Safe = false } };
                var people = Enumerable.Range(0, 6).Select(i => Present("p" + i, "a")).Concat(Enumerable.Range(6, 2).Select(i => Present("p" + i, "b"))).ToArray();
                var plan = WandererPopulationRules.Plan(locations, people);
                Require(plan.Moves.Count == 2 && plan.SpawnTowns.Count == 0, "Surplus should move, not clone.");
                Require(plan.Counts["a"] == 4 && plan.Counts["b"] == 4 && plan.Counts["siege"] == 0, "Unsafe target modified.");
                Require(plan.Moves.Select(m => m.HeroId).Distinct().Count() == 2, "Same individual moved twice.");
                return plan;
            });
            Check("availability_exclusions", () => {
                foreach (string exclusion in new[] { "dead", "child", "prisoner", "recruited", "absent" })
                {
                    var p = Present("p", "a");
                    if (exclusion == "dead") p.Alive = false; if (exclusion == "child") p.Adult = false;
                    if (exclusion == "prisoner") p.Free = false; if (exclusion == "recruited") p.Independent = false;
                    if (exclusion == "absent") p.Available = false;
                    Require(!WandererPopulationRules.Counts(p), exclusion + " filled a tavern slot.");
                }
                return true;
            });
            Check("rotation_contact_and_unknown_history", () => {
                var person = new WandererIdentity { Id = "one", HeroId = "hero", Initialized = true, CreatedDay = 0 };
                var p = Present("hero", "town");
                Require(!WandererPopulationRules.CanRotate(person, p, true, 40), "Unchecked history allowed retirement.");
                person.HistoryChecked = true;
                Require(WandererPopulationRules.CanRotate(person, p, true, 40), "Eligible uncontacted person cannot rotate.");
                Require(!WandererPopulationRules.CanRotate(person, p, false, 40) && !WandererPopulationRules.CanRotate(person, p, true, 29), "Quest/young residence guard lost.");
                Require(!WandererPopulationRules.Protect(person, "", 40), "Inspection/empty receipt counted as contact.");
                Require(WandererPopulationRules.Protect(person, "exchange-123", 40), "First contact failed.");
                Require(!WandererPopulationRules.Protect(person, "retry-456", 50) && person.ContactReceipt == "exchange-123" && person.ContactDay == 40, "Retry changed original contact.");
                Require(!WandererPopulationRules.CanRotate(person, p, true, 1000), "Established person was rotated.");
                p.Independent = false; p.Independent = true;
                Require(person.Protected, "Dismissal erased protection.");
                return person;
            });
            Check("old_age_only_death_policy", () => {
                foreach (string reason in new[] { "None", "Murdered", "DiedInLabor", "DiedOfOldAge", "DiedInBattle", "WoundedInBattle", "Executed", "ExecutionAfterMapEvent", "Lost" })
                {
                    Require(WandererPopulationRules.AllowsDeath(false, reason), "Nonprotected death changed.");
                    Require(WandererPopulationRules.AllowsDeath(true, reason) == (reason == "DiedOfOldAge"), "Protected death policy failed: " + reason);
                }
                return true;
            });
            Check("protected_capacity_overflow", () => {
                var people = Enumerable.Range(0, 8).Select(i => Present("known" + i, "a")).ToArray();
                var plan = WandererPopulationRules.Plan(new[] { new WandererTown { Id = "a", Safe = true } }, people);
                Require(plan.Overflow == 3 && plan.SpawnTowns.Count == 0 && plan.Moves.Count == 0, "Overflow should be reported without retirement.");
                return plan;
            });
            Check("every_template_every_culture", () => {
                foreach (int size in new[] { 1, 17, 67, 100 })
                {
                    int cultures = WandererNames.Cultures.Length;
                    var seen = new HashSet<string>();
                    for (long i = 0; i < size * cultures; i++) { var pair = WandererPopulationRules.Combination(i, size, cultures); Require(seen.Add(pair.Item1 + ":" + pair.Item2), "Combination repeated before cycle complete."); }
                    Require(seen.Count == size * cultures && WandererPopulationRules.Combination(size * cultures, size, cultures).Equals(Tuple.Create(0, 0)), "Cycle lost coverage.");
                }
                Require(WandererNames.Supports("nord") && !WandererNames.Supports("unreviewed"), "Unsupported culture silently borrows names.");
                return WandererNames.Cultures;
            });
            Check("fresh_distinct_names_all_cultures", () => {
                var reserved = new List<string> { "Derthert", "Caladog", "Raganvad", "Rhagaea", "Lucon", "Garios", "Monchug", "Unqid", "Marius", "Basil", "Bjorn" };
                var samples = new Dictionary<string, object>();
                foreach (string culture in WandererNames.Cultures)
                {
                    var local = new List<string>();
                    for (int i = 0; i < 48; i++)
                    {
                        string name = WandererNames.Choose(culture, i % 2 == 0, culture + ":" + i, reserved);
                        Require(!reserved.Any(old => EncounteredResidentRules.NamesTooSimilar(name, old)), "Duplicate or confusing name escaped rejection.");
                        reserved.Add(name); local.Add(name);
                    }
                    samples[culture] = local.Take(8).ToArray();
                }
                Require(reserved.Distinct().Count() == reserved.Count, "Cross-culture collision.");
                return samples;
            });
            Check("shape_similarity_not_only_byte_equality", () => {
                float[] original = Enumerable.Repeat(.5f, 20).ToArray();
                float[] near = original.Select((x, i) => x + (i % 2 == 0 ? .01f : -.01f)).ToArray();
                float[] different = original.Select((x, i) => i % 2 == 0 ? .2f : .8f).ToArray();
                Require(WandererPopulationRules.FaceTooClose(original, original) && WandererPopulationRules.FaceTooClose(original, near), "Near clone passed.");
                Require(!WandererPopulationRules.FaceTooClose(original, different), "Distinct structure rejected.");
                Require(WandererPopulationRules.FaceTooClose(original, null), "Unknown face treated as proven unique.");
                return true;
            });
            Check("identity_ledger_roundtrip_and_reserved_retired_names", () => {
                var state = new WandererPopulationState { Cursor = 321, LastDailyDay = 44 };
                state.People.Add(new WandererIdentity { Id = "identity", HeroId = "hero", Name = "Eryalon", Body = "saved-body", FaceKey = "saved-face", FaceShape = new[] { .2f, .4f },
                    Protected = true, ContactReceipt = "receipt", ContactDay = 42, Initialized = true, HistoryChecked = true, Retired = true });
                string serialized = Json.Serialize(state);
                var restored = Json.Deserialize<WandererPopulationState>(serialized);
                Require(Json.Serialize(restored) == serialized && restored.People[0].Protected, "Save roundtrip altered identity or protection.");
                string candidate = WandererNames.Choose("empire", false, "next", restored.People.Select(p => p.Name));
                Require(!EncounteredResidentRules.NamesTooSimilar(candidate, "Eryalon"), "Retired name reused.");
                return true;
            });
            Check("profile_canon_and_reused_hero_id", () => {
                Dictionary<string, object> Profile(string identity, string name) => new Dictionary<string, object> { ["name"] = name, ["wandererIdentity"] = new Dictionary<string, object> {
                    ["schema"] = WandererPopulationRules.Version, ["identityId"] = identity, ["name"] = name, ["generated"] = true,
                    ["cultureId"] = "nord", ["biography"] = "original life", ["protected"] = true, ["contactReceipt"] = "original-receipt" } };
                var old = Profile("one", "Eiralmund"); var incoming = Profile("one", "WrongName");
                ReadDictionary(incoming, "wandererIdentity")["protected"] = false;
                PreserveWandererIdentity(incoming, old);
                Require(ReadString(incoming, "name", "") == "Eiralmund" && ReadBool(ReadDictionary(incoming, "wandererIdentity"), "protected", false), "Profile refresh erased canon.");
                bool rejected = false; try { PreserveWandererIdentity(Profile("two", "Other"), old); } catch (InvalidOperationException) { rejected = true; }
                Require(rejected, "Hero ID reuse silently absorbed another identity.");
                return true;
            });
            Check("durable_contact_history_scope", () => {
                string campaign = "verify_wanderer_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                using (var c = OpenCampaignConnection(campaign)) EnsureCategorizedMemorySchema(c);
                var request = new Dictionary<string, object> { ["eventId"] = "wanderer_event", ["sceneTurnId"] = "wanderer_turn", ["timelineId"] = "main",
                    ["worldDay"] = 42d, ["activeHeroIds"] = new List<string> { "speaker", "bystander" }, ["attendees"] = new List<Dictionary<string, object>>() };
                StoreSocialEventConversationExchange(campaign, request, "speaker", "player", "Player", "Traveler", "Tell me about your travels.", "I have worked along the coast.", "wanderer-test", "wanderer_event", 300);
                Dictionary<string, object> Read(string hero, string timeline = "main", string player = "player") => WandererContactHistory(new Dictionary<string, object> {
                    ["campaignId"] = campaign, ["timelineId"] = timeline, ["heroStringId"] = hero, ["playerHeroStringId"] = player });
                Require(ReadBool(Read("speaker"), "hasContact", false), "Actual speaker lost durable contact.");
                Require(!ReadBool(Read("bystander"), "hasContact", false), "Bystander incorrectly became permanent.");
                Require(!ReadBool(Read("speaker", "other-timeline"), "ok", true), "Wrong timeline was read.");
                Require(!ReadBool(Read("speaker", "main", "other-player"), "hasContact", false), "Wrong player inherited contact.");
                Require(ReadString(Read("speaker"), "receipt", "") == ReadString(Read("speaker"), "receipt", ""), "Observational retry changed receipt.");
                return true;
            });
            return results;
        }
    }
}
