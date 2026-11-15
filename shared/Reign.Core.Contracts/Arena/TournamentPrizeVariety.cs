using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBeta.Shared.Arena
{
    public static class TournamentPrizeVariety
    {
        public const int HistoryLimit = 10;

        public static bool ShouldReroll(int mode, int previousHeroes, int currentHeroes) => mode switch
        {
            0 => false,
            1 => previousHeroes < 4 && currentHeroes >= 4,
            2 => previousHeroes < currentHeroes,
            3 => previousHeroes != currentHeroes,
            _ => true
        };

        public static List<string> NormalizeHistory(IEnumerable<string>? ids)
        {
            var result = new List<string>();
            foreach (string id in ids ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.RemoveAll(value => string.Equals(value, id, StringComparison.Ordinal));
                result.Add(id);
            }
            return result.Skip(Math.Max(0, result.Count - HistoryLimit)).ToList();
        }

        public static List<T> FreshPool<T>(IEnumerable<T> source, Func<T, string> id, IEnumerable<string>? recent)
        {
            var all = source.GroupBy(id, StringComparer.Ordinal).Select(group => group.First()).ToList();
            var blocked = new HashSet<string>(recent ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var fresh = all.Where(item => !blocked.Contains(id(item))).ToList();
            return fresh.Count >= Math.Min(8, all.Count) ? fresh : all;
        }

    }
}
