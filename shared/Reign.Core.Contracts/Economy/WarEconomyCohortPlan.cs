using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;

namespace Reign.Core.Contracts.Economy
{
    // Pure diagnostic policy, shared by admission, native adapters and offline contracts.
    public sealed class WarEconomyCohortPlan
    {
        public const int MaximumSessions = 128;
        public const int RetainedSamples = 256;
        public double DurationDays { get; }
        public double WallHours { get; }
        public double SampleIntervalDays { get; }
        public bool BlockOtherWars { get; }
        public bool SuppressRebellions { get; }
        public string PeaceKingdomIds { get; }
        public string WarPairs { get; }
        public IReadOnlyList<string>? SelectedPeace { get; }
        public IReadOnlyList<string>? SelectedWars { get; }

        public WarEconomyCohortPlan(double durationDays = 126, double wallHours = 6,
            double sampleIntervalDays = 1, bool blockOtherWars = true, bool suppressRebellions = true,
            string peaceKingdomIds = "auto", string warPairs = "auto")
        {
            DurationDays = Range(durationDays, 1, 6300, nameof(durationDays));
            WallHours = Range(wallHours, .25, 12, nameof(wallHours));
            SampleIntervalDays = Range(sampleIntervalDays, .25, 7, nameof(sampleIntervalDays));
            BlockOtherWars = blockOtherWars; SuppressRebellions = suppressRebellions;
            PeaceKingdomIds = peaceKingdomIds ?? throw new ArgumentNullException(nameof(peaceKingdomIds));
            WarPairs = warPairs ?? throw new ArgumentNullException(nameof(warPairs));
            SelectedPeace = Selection(peaceKingdomIds, false);
            SelectedWars = Selection(warPairs, true);
        }

        private static double Range(double value, double min, double max, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < min || value > max)
                throw new ArgumentOutOfRangeException(name);
            return value;
        }
        private static IReadOnlyList<string>? Selection(string text, bool pairs)
        {
            if (text.Length > 16384) throw new ArgumentException("Cohort selection exceeds16384 characters.");
            if (text == "auto") return null;
            if (text == "none") return Array.Empty<string>();
            string[] rows = text.Split(',');
            if (rows.Length == 0 || rows.Length > (pairs ? 2016 : 64)) throw new ArgumentException("Too many cohort entries.");
            var values = new HashSet<string>(StringComparer.Ordinal);
            foreach (string raw in rows)
            {
                string value = raw.Trim();
                if (pairs)
                {
                    var ids = value.Split(':');
                    if (ids.Length != 2) throw new ArgumentException("War pairs use first:second, separated by commas.");
                    value = Pair(ids[0], ids[1]);
                }
                else ValidateId(value);
                if (!values.Add(value)) throw new ArgumentException("Duplicate cohort entry: " + value);
            }
            return new ReadOnlyCollection<string>(values.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        }
        private static void ValidateId(string id)
        {
            if (id == null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_.-]{1,128}\z"))
                throw new ArgumentException("Invalid exact kingdom ID.");
        }
        public static string Pair(string first, string second)
        {
            ValidateId(first); ValidateId(second);
            if (first == second) throw new ArgumentException("A war pair requires two distinct kingdoms.");
            return string.CompareOrdinal(first, second) < 0 ? first + ":" + second : second + ":" + first;
        }
        internal static HashSet<string> CanonicalWars(IEnumerable<string> rows)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            int count = 0;
            foreach (var row in rows)
            {
                if (++count > 2016) throw new ArgumentException("War observation exceeds the64-kingdom pair bound.");
                var ids = row?.Split(':');
                if (ids == null || ids.Length != 2) throw new ArgumentException("Observed wars require two exact kingdom IDs.");
                result.Add(Pair(ids[0], ids[1]));
            }
            return result;
        }
        public WarEconomyCohortScope Resolve(IEnumerable<string> npcKingdoms, IEnumerable<string> actualWars)
        {
            var kingdoms = new HashSet<string>(npcKingdoms, StringComparer.Ordinal);
            if (kingdoms.Count < 2 || kingdoms.Count > 64) throw new ArgumentException("Requires2-64 current NPC kingdoms.");
            foreach (var id in kingdoms) ValidateId(id);
            var actual = CanonicalWars(actualWars);
            var peace = new HashSet<string>(SelectedPeace ?? kingdoms.Where(id => !actual.Any(w => w.Split(':').Contains(id))), StringComparer.Ordinal);
            var wars = new HashSet<string>(SelectedWars ?? actual.Where(w => !w.Split(':').Any(peace.Contains)), StringComparer.Ordinal);
            if (peace.Any(id => !kingdoms.Contains(id)) || wars.Any(w => w.Split(':').Any(id => !kingdoms.Contains(id))))
                throw new ArgumentException("Cohort contains unknown, eliminated or player kingdoms.");
            if (wars.Any(w => w.Split(':').Any(peace.Contains))) throw new ArgumentException("A kingdom cannot be in both peace and war controls.");
            return new WarEconomyCohortScope(this, kingdoms, peace, wars, actual);
        }
    }

    public sealed class WarEconomyCohortScope
    {
        private readonly HashSet<string> _kingdoms, _peace, _wars, _openingWars;
        private readonly WarEconomyCohortPlan _plan;
        public IReadOnlyList<string> Kingdoms => _kingdoms.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        public IReadOnlyList<string> Peace => _peace.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        public IReadOnlyList<string> Wars => _wars.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        internal WarEconomyCohortScope(WarEconomyCohortPlan plan, HashSet<string> kingdoms, HashSet<string> peace,
            HashSet<string> wars, HashSet<string> actual)
        { _plan = plan; _kingdoms = kingdoms; _peace = peace; _wars = wars; _openingWars = actual; }
        public bool BlocksWar(string first, string second)
            => first != second && _kingdoms.Contains(first) && _kingdoms.Contains(second)
                && (_peace.Contains(first) || _peace.Contains(second)
                    || (_plan.BlockOtherWars && !_openingWars.Contains(WarEconomyCohortPlan.Pair(first, second))));
        public bool BlocksPeace(string first, string second)
            => first != second && _kingdoms.Contains(first) && _kingdoms.Contains(second)
                && _wars.Contains(WarEconomyCohortPlan.Pair(first, second));
        public string[] Violations(IEnumerable<string> currentKingdoms, IEnumerable<string> actualWars)
        {
            var current = new HashSet<string>(currentKingdoms, StringComparer.Ordinal);
            var actual = WarEconomyCohortPlan.CanonicalWars(actualWars);
            var errors = new List<string>();
            if (_plan.SuppressRebellions)
                errors.AddRange(current.Except(_kingdoms).Select(id => "unexpected_kingdom:" + id));
            errors.AddRange(_peace.Where(id => !current.Contains(id)).Select(id => "peace_kingdom_missing:" + id));
            errors.AddRange(_wars.Where(w => !actual.Contains(w)).Select(w => "locked_war_missing:" + w));
            // A newly created rebel kingdom is outside the captured prefix scope, but can still invalidate a control.
            errors.AddRange(actual.Where(w => w.Split(':').Any(_peace.Contains)
                || (_plan.BlockOtherWars && !_openingWars.Contains(w))).Select(w => "unexpected_war:" + w));
            return errors.OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
    }
}
