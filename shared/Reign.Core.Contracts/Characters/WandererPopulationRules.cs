using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ReignBeta.Shared.Characters
{
    public readonly struct PopulationFaceSchemaKey : IEquatable<PopulationFaceSchemaKey>
    {
        public readonly int Race, Age, WeightCount;
        public readonly bool Female;
        public PopulationFaceSchemaKey(int race, bool female, int age, int weightCount)
        { Race = race; Female = female; Age = age; WeightCount = weightCount; }
        public bool Equals(PopulationFaceSchemaKey other) => Race == other.Race && Female == other.Female
            && Age == other.Age && WeightCount == other.WeightCount;
        public override bool Equals(object? obj) => obj is PopulationFaceSchemaKey other && Equals(other);
        public override int GetHashCode()
        { unchecked { return ((Race * 397 ^ (Female ? 1 : 0)) * 397 ^ Age) * 397 ^ WeightCount; } }
    }

    // Native-thread, session-scoped catalog memoization. Values are positions,
    // never body weights or Hero references. A verified catalog change disables
    // memoization for the session; callers then use freshly read indices.
    public sealed class PopulationFaceSchemaCache
    {
        private sealed class Entry
        {
            public readonly IReadOnlyList<int> Indices;
            public int Hits;
            public bool Verified;
            public Entry(IReadOnlyList<int> indices) { Indices = indices; }
        }
        private readonly Dictionary<PopulationFaceSchemaKey, Entry> entries = new Dictionary<PopulationFaceSchemaKey, Entry>();
        private readonly int maximumEntries;
        private readonly long maximumPayloadBytes;
        public int Count => entries.Count;
        public long PayloadBytes { get; private set; }
        public long Hits { get; private set; }
        public long Misses { get; private set; }
        public long VerificationReads { get; private set; }
        public long Mismatches { get; private set; }
        public bool Disabled { get; private set; }
        public PopulationFaceSchemaCache(int maximumEntries = 512, long maximumPayloadBytes = 1024 * 1024)
        {
            if (maximumEntries < 1 || maximumPayloadBytes < 1) throw new ArgumentOutOfRangeException();
            this.maximumEntries = maximumEntries; this.maximumPayloadBytes = maximumPayloadBytes;
        }
        private static IReadOnlyList<int> Own(int[] indices) => Array.AsReadOnly((int[])indices.Clone());
        private static long Bytes(int count) => 64L + count * 4L;
        public IReadOnlyList<int> Get(PopulationFaceSchemaKey key, Func<PopulationFaceSchemaKey, int[]> readIndices)
        {
            if (!Disabled && entries.TryGetValue(key, out Entry? entry))
            {
                Hits++;
                // No random draws: check first reuse, then each 256th reuse.
                entry.Hits++;
                if (!entry.Verified || (entry.Hits & 255) == 0)
                {
                    entry.Verified = false;
                    VerificationReads++;
                    int[] fresh = readIndices(key);
                    if (!entry.Indices.SequenceEqual(fresh))
                    {
                        Mismatches++; Disabled = true; entries.Clear(); PayloadBytes = 0;
                        return Own(fresh);
                    }
                    entry.Verified = true;
                }
                return entry.Indices;
            }
            Misses++;
            IReadOnlyList<int> indices = Own(readIndices(key)); // Failed reads publish nothing.
            long bytes = Bytes(indices.Count);
            if (!Disabled && bytes <= maximumPayloadBytes)
            {
                if (entries.Count >= maximumEntries || PayloadBytes + bytes > maximumPayloadBytes)
                { entries.Clear(); PayloadBytes = 0; }
                entries.Add(key, new Entry(indices)); PayloadBytes += bytes;
            }
            return indices;
        }
        public void Clear()
        { entries.Clear(); PayloadBytes = Hits = Misses = VerificationReads = Mismatches = 0; Disabled = false; }
    }

    public sealed class WandererIdentity
    {
        public string Id = "", HeroId = "", TemplateId = "", CultureId = "", Name = "", Body = "", Biography = "", BirthplaceId = "";
        public string FaceKey = "", ContactReceipt = "", LastTownId = "";
        public float[] FaceShape = Array.Empty<float>();
        public int Race;
        public bool Female, Generated, Initialized, Retired, Protected, HistoryChecked;
        public double CreatedDay, ContactDay, LastMoveDay;
    }

    public sealed class WandererPopulationState
    {
        public string Version = WandererPopulationRules.Version;
        public long Cursor;
        public double LastDailyDay = -1;
        public List<WandererIdentity> People = new List<WandererIdentity>();
    }

    public sealed class WandererPresence
    {
        public string HeroId = "", TownId = "";
        public bool Alive, Adult, Free, Independent, Available, SafeToMove;
    }

    public sealed class WandererTown
    {
        public string Id = "";
        public bool Safe;
    }

    public sealed class WandererMove
    {
        public string HeroId = "", TownId = "";
    }

    public sealed class WandererPopulationPlan
    {
        public List<WandererMove> Moves = new List<WandererMove>();
        public List<string> SpawnTowns = new List<string>();
        public Dictionary<string, int> Counts = new Dictionary<string, int>(StringComparer.Ordinal);
        public int Overflow;
    }

    /// <summary>Pure policy. Native adapters alone create/move heroes; saves own identities and the generation cursor.</summary>
    public static class WandererPopulationRules
    {
        public const string Version = "reign-wanderer-population-v1";
        public const int Minimum = 3, Target = 4, Maximum = 5, BatchSize = 4;
        public const int MinimumResidenceDays = 7, MinimumRotationAgeDays = 30;

        public static bool CanMaintainTown(bool isTown, bool underSiege, bool hasSiegeEvent, bool activeMission)
            => isTown && !underSiege && !hasSiegeEvent && !activeMission;

        public static bool Counts(WandererPresence p) => p != null && p.Alive && p.Adult && p.Free
            && p.Independent && p.Available && !string.IsNullOrEmpty(p.TownId);

        public static WandererPopulationPlan Plan(IEnumerable<WandererTown> towns, IEnumerable<WandererPresence> heroes,
            int budget = BatchSize, string preferredTownId = "")
        {
            var locations = towns.OrderBy(t => t.Id, StringComparer.Ordinal).ToArray();
            var people = heroes.ToArray();
            if (locations.Select(t => t.Id).Distinct().Count() != locations.Length
                || people.Select(p => p.HeroId).Distinct().Count() != people.Length)
                throw new ArgumentException("Population census contains duplicate identities.");
            var result = new WandererPopulationPlan();
            foreach (var town in locations) result.Counts[town.Id] = people.Count(p => Counts(p) && p.TownId == town.Id);
            var safe = new HashSet<string>(locations.Where(t => t.Safe).Select(t => t.Id));
            var moved = new HashSet<string>();
            foreach (var town in locations.Where(t => t.Safe)
                .OrderBy(t => string.Equals(t.Id, preferredTownId, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(t => result.Counts[t.Id]).ThenBy(t => t.Id, StringComparer.Ordinal))
            {
                while (result.Counts[town.Id] < Target && budget > 0)
                {
                    var donor = people.Where(p => Counts(p) && p.SafeToMove && safe.Contains(p.TownId)
                            && !moved.Contains(p.HeroId) && p.TownId != town.Id && result.Counts[p.TownId] > Target)
                        .OrderByDescending(p => result.Counts[p.TownId]).ThenBy(p => p.HeroId, StringComparer.Ordinal).FirstOrDefault();
                    if (donor != null)
                    {
                        result.Moves.Add(new WandererMove { HeroId = donor.HeroId, TownId = town.Id });
                        result.Counts[donor.TownId]--; moved.Add(donor.HeroId);
                    }
                    else result.SpawnTowns.Add(town.Id);
                    result.Counts[town.Id]++; budget--;
                }
            }
            result.Overflow = result.Counts.Values.Sum(n => Math.Max(0, n - Maximum));
            return result;
        }

        public static bool CanRotate(WandererIdentity person, WandererPresence presence, bool questSafe, double day)
            => person != null && person.Initialized && !person.Retired && !person.Protected && person.HistoryChecked
                && Counts(presence) && presence.SafeToMove && questSafe && day - person.CreatedDay >= MinimumRotationAgeDays;

        public static bool Protect(WandererIdentity person, string receipt, double day)
        {
            if (person == null || person.Retired || string.IsNullOrWhiteSpace(receipt)) return false;
            person.HistoryChecked = true;
            if (person.Protected) return false;
            person.Protected = true; person.ContactReceipt = receipt; person.ContactDay = day;
            return true;
        }

        // Player requested old-age-only survival after Reign contact. Do not reclassify removal as old age.
        public static bool AllowsDeath(bool protectedPerson, string reason) => !protectedPerson || reason == "DiedOfOldAge";

        public static Tuple<int, int> Combination(long cursor, int templates, int cultures)
        {
            if (cursor < 0 || templates < 1 || cultures < 1) throw new ArgumentOutOfRangeException();
            return Tuple.Create((int)((cursor / cultures) % templates), (int)(cursor % cultures));
        }

        public static string NormalizeName(string value)
        {
            var result = new StringBuilder();
            foreach (char c in (value ?? "").Normalize(NormalizationForm.FormD))
                if (char.IsLetter(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                    result.Append(char.ToLowerInvariant(c));
            return result.ToString();
        }

        public static bool FaceTooClose(float[]? a, float[]? b)
        {
            if (a == null || b == null || a.Length == 0 || a.Length != b.Length) return true;
            double sum = 0; int changed = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i]) || float.IsNaN(b[i]) || float.IsInfinity(b[i])) return true;
                double delta = Math.Abs(a[i] - b[i]); sum += delta * delta; if (delta >= .10) changed++;
            }
            return Math.Sqrt(sum / a.Length) < .12 || changed < Math.Min(5, a.Length);
        }

        public static int Seed(string value)
        {
            unchecked { uint hash = 2166136261; foreach (char c in value ?? "") { hash ^= c; hash *= 16777619; } return (int)(hash & 0x7fffffff); }
        }

        public static string Biography(string name, string culture, string birthplace, string profession, int seed)
        {
            string[] past = { "learned the trade from an exacting mentor", "worked beside traveling craftspeople", "earned a living with caravans", "served a small household before taking to the road", "practiced the trade in market towns", "spent several years working along the frontier" };
            string[] aim = { "hopes to earn enough for a home", "seeks dependable companions and steady work", "wants to be known for honest work", "is saving to establish an independent livelihood", "hopes to find a place worth settling in", "is looking for a fresh start" };
            var rng = new Random(seed);
            return name + " is a " + culture + " traveler from " + birthplace + ", skilled in " + profession
                + ". This wanderer " + past[rng.Next(past.Length)] + " and " + aim[rng.Next(aim.Length)] + ".";
        }
    }
}
