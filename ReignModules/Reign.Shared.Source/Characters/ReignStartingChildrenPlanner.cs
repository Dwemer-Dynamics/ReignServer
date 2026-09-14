using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBeta.Family
{
    // Shared by the native adapter and provider-free Verification Lab. No game APIs or RNG.
    internal sealed class StartingFamilyHero
    {
        public string Id { get; set; }
        public string SpouseId { get; set; }
        public string MotherId { get; set; }
        public string FatherId { get; set; }
        public string ClanId { get; set; }
        public string Source { get; set; }
        public double Age { get; set; }
        public bool Female { get; set; }
        public bool Alive { get; set; }
        public bool EligibleParent { get; set; }
        public bool Ruler { get; set; }
        public bool PlayerHousehold { get; set; }
    }

    internal sealed class StartingChildSpec
    {
        public string SlotId { get; set; }
        public string MotherId { get; set; }
        public string FatherId { get; set; }
        public int Age { get; set; }
        public double AgeYears => Age + 0.5;
        public bool Female { get; set; }
    }

    internal sealed class StartingFamilyPlan
    {
        public string MotherId { get; set; }
        public string FatherId { get; set; }
        public string Source { get; set; }
        public int Target { get; set; }
        public int ExistingMinors { get; set; }
        public string Reason { get; set; }
        public List<StartingChildSpec> Children { get; set; } = new List<StartingChildSpec>();
    }

    internal sealed class StartingPopulationPlan
    {
        public string Schema { get; set; } = "reign-starting-children-plan-v1";
        public int Seed { get; set; }
        public int LivingBefore { get; set; }
        public int MinorsBefore { get; set; }
        public List<StartingFamilyPlan> Families { get; set; } = new List<StartingFamilyPlan>();
        public int Added => Families.Sum(f => f.Children.Count);
        public int ProjectedMinors => MinorsBefore + Added;
    }

    internal sealed class StartingChildReceipt
    {
        public string SlotId { get; set; }
        public string HeroId { get; set; }
        public bool Finalized { get; set; }
    }

    internal sealed class StartingChildrenState
    {
        public string Schema { get; set; } = "reign-starting-children-state-v1";
        public StartingPopulationPlan Plan { get; set; }
        public bool Completed { get; set; }
        public string Error { get; set; }
        public string UncertainCreationSlot { get; set; }
        public List<StartingChildReceipt> Receipts { get; set; } = new List<StartingChildReceipt>();
    }

    internal static class ReignStartingChildrenPlanner
    {
        internal const int DefaultSeed = 20260905;
        private static int Band(double age) => age < 6 ? 0 : age < 12 ? 1 : 2;
        private static bool Minor(StartingFamilyHero h) => h.Alive && h.Age >= 1 && h.Age < 18;

        internal static StartingPopulationPlan Plan(IEnumerable<StartingFamilyHero> input,
            int seed = DefaultSeed, int comesOfAge = 18)
        {
            var heroes = input.OrderBy(h => h.Id, StringComparer.Ordinal).ToList();
            if (heroes.Any(h => string.IsNullOrWhiteSpace(h.Id) || double.IsNaN(h.Age) || double.IsInfinity(h.Age)))
                throw new ArgumentException("Family census contains an invalid identity or age.");
            var byId = heroes.ToDictionary(h => h.Id, StringComparer.Ordinal);
            var maternalChildren = heroes.Where(h => !string.IsNullOrEmpty(h.MotherId)).ToLookup(h => h.MotherId);
            var plan = new StartingPopulationPlan
            {
                Seed = seed, LivingBefore = heroes.Count(h => h.Alive), MinorsBefore = heroes.Count(Minor)
            };
            int[] globalBands = Enumerable.Range(0, 3).Select(b => heroes.Count(h => Minor(h) && Band(h.Age) == b)).ToArray();
            foreach (var mother in heroes.Where(h => h.Female && h.EligibleParent))
            {
                byId.TryGetValue(mother.SpouseId ?? "", out var father);
                var family = new StartingFamilyPlan { MotherId = mother.Id, FatherId = father?.Id, Source = mother.Source };
                plan.Families.Add(family);
                if (!mother.Alive || father == null || !father.Alive) { family.Reason = "missing_or_deceased_parent"; continue; }
                if (father.Female || !father.EligibleParent || father.SpouseId != mother.Id
                    || string.IsNullOrEmpty(mother.ClanId) || mother.ClanId != father.ClanId)
                { family.Reason = "not_a_current_married_household"; continue; }
                if (mother.PlayerHousehold || father.PlayerHousehold) { family.Reason = "player_household"; continue; }
                if (mother.Ruler || father.Ruler) { family.Reason = "ruler_couple"; continue; }
                string pair = seed + ":" + mother.Id + ":" + father.Id;
                family.Target = 3 + (int)(Hash(pair) % 3);
                var siblings = maternalChildren[mother.Id].ToList();
                var shared = siblings.Where(h => h.FatherId == father.Id && Minor(h)).ToList();
                family.ExistingMinors = shared.Count;
                int[] localBands = Enumerable.Range(0, 3).Select(b => shared.Count(h => Band(h.Age) == b)).ToArray();
                var birthAges = siblings.Select(h => h.Age).ToList(); // Includes deceased children and other fathers.
                int minimumParentAge = Math.Max(18, comesOfAge);
                var ages = Enumerable.Range(1, Math.Max(0, Math.Min(17, comesOfAge - 1)))
                    .Where(age => mother.Age - (age + 0.5) >= minimumParentAge && mother.Age - (age + 0.5) <= 45
                        && father.Age - (age + 0.5) >= minimumParentAge).ToList();
                while (family.ExistingMinors + family.Children.Count < family.Target)
                {
                    var available = ages.Where(age => birthAges.All(existing => Math.Abs(existing - (age + 0.5)) >= 1.0))
                        .OrderBy(age => localBands[Band(age)])
                        .ThenBy(age => globalBands[Band(age)] / (Band(age) == 0 ? 5.0 : 6.0))
                        .ThenBy(age => Hash(pair + ":age:" + age)).ToList();
                    if (available.Count == 0) break;
                    int ageYears = available[0];
                    int index = family.Children.Count;
                    family.Children.Add(new StartingChildSpec
                    {
                        SlotId = pair + ":child:" + index, MotherId = mother.Id, FatherId = father.Id,
                        Age = ageYears, Female = ((Hash(pair + ":sex") + (uint)index) % 2) == 0
                    });
                    birthAges.Add(ageYears + 0.5);
                    localBands[Band(ageYears)]++;
                    globalBands[Band(ageYears)]++;
                }
                family.Reason = family.ExistingMinors >= family.Target ? "already_at_target"
                    : family.ExistingMinors + family.Children.Count < family.Target ? "chronology_shortfall" : "seeded";
            }
            return plan;
        }

        // A receipt is recorded immediately after allocation, before finalization. Retry finishes that
        // same hero. A loaded save is never an authorization to start or resume population seeding.
        internal static void Apply(StartingChildrenState state, bool isNewCampaign,
            Func<StartingChildSpec, string> create, Action<StartingChildSpec, string> finalize)
        {
            if (!isNewCampaign || state.Completed) return;
            if (state.Plan == null) throw new InvalidOperationException("A starting population plan is required.");
            if (state.UncertainCreationSlot != null)
                throw new InvalidOperationException("A native allocation outcome is uncertain; do not retry this campaign initialization: " + state.UncertainCreationSlot);
            try
            {
                foreach (var spec in state.Plan.Families.SelectMany(f => f.Children))
                {
                    var receipt = state.Receipts.SingleOrDefault(r => r.SlotId == spec.SlotId);
                    if (receipt == null)
                    {
                        // A native event listener can throw after allocation but before the API returns.
                        // Without an identity, fail closed instead of risking a duplicate on retry.
                        state.UncertainCreationSlot = spec.SlotId;
                        string id = create(spec);
                        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("Native child creation returned no identity.");
                        receipt = new StartingChildReceipt { SlotId = spec.SlotId, HeroId = id };
                        state.Receipts.Add(receipt);
                        state.UncertainCreationSlot = null;
                    }
                    if (receipt.Finalized) continue;
                    finalize(spec, receipt.HeroId);
                    receipt.Finalized = true;
                }
                state.Completed = true;
                state.Error = null;
            }
            catch (Exception ex) { state.Error = ex.Message; throw; }
        }

        private static uint Hash(string value)
        {
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            }
        }
    }
}
