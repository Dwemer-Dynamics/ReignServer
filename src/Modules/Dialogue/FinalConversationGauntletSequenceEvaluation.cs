using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object>
            EvaluateFinalGauntletDifferential(
                Dictionary<string, object> baseline,
                Dictionary<string, object> variant,
                Dictionary<string, object> contract)
        {
            Dictionary<string, string> left =
                FlattenFinalGauntletState(baseline);
            Dictionary<string, string> right =
                FlattenFinalGauntletState(variant);
            HashSet<string> changed = new HashSet<string>(
                left.Keys.Union(right.Keys).Where(path =>
                    !left.TryGetValue(path, out string a)
                    || !right.TryGetValue(path, out string b)
                    || !string.Equals(a, b, StringComparison.Ordinal)),
                StringComparer.Ordinal);
            HashSet<string> permitted = new HashSet<string>(
                ReadStringList(contract, "permittedChangedPaths"),
                StringComparer.Ordinal);
            List<string> invariantFailures = ReadStringList(
                    contract, "invariantPaths")
                .Where(path => changed.Contains(path))
                .ToList();
            List<string> unexpected = changed
                .Where(path => !permitted.Contains(path)
                    && !permitted.Any(prefix =>
                        path.StartsWith(prefix + ".", StringComparison.Ordinal)))
                .ToList();
            return new Dictionary<string, object>
            {
                ["passed"] = invariantFailures.Count == 0
                    && unexpected.Count == 0,
                ["changedPaths"] = changed.OrderBy(x => x)
                    .Cast<object>().ToList(),
                ["invariantFailures"] = invariantFailures.Cast<object>().ToList(),
                ["unexpectedChanges"] = unexpected.Cast<object>().ToList()
            };
        }

        private static Dictionary<string, object>
            EvaluateFinalGauntletSequence(
                List<Dictionary<string, object>> ledger,
                Dictionary<string, object> requirement)
        {
            ledger = ledger ?? new List<Dictionary<string, object>>();
            requirement = requirement ?? new Dictionary<string, object>();
            string owner = ReadString(requirement, "ownerHeroId", "");
            string source = ReadString(requirement, "sourceId", "");
            string terminal = ReadString(requirement, "terminalStatus", "");
            List<Dictionary<string, object>> relevant = ledger
                .Where(row => string.IsNullOrWhiteSpace(source)
                    || ReadString(row, "sourceId", "").Equals(
                        source, StringComparison.Ordinal))
                .OrderBy(row => ReadLong(row, "turn", 0))
                .ToList();
            bool ordered = relevant.Select(row => ReadLong(row, "turn", 0))
                .SequenceEqual(relevant.Select(row => ReadLong(row, "turn", 0))
                    .OrderBy(value => value));
            bool ownerValid = relevant.Count > 0 && relevant.All(row =>
                ReadString(row, "ownerHeroId", "").Equals(
                    owner, StringComparison.Ordinal));
            bool terminalValid = relevant.Count > 0
                && ReadString(relevant.Last(), "status", "").Equals(
                    terminal, StringComparison.Ordinal);
            return new Dictionary<string, object>
            {
                ["passed"] = ordered && ownerValid && terminalValid,
                ["ordered"] = ordered,
                ["ownerValid"] = ownerValid,
                ["terminalValid"] = terminalValid,
                ["denominator"] = relevant.Count
            };
        }

        private static Dictionary<string, object>
            EvaluateFinalGauntletStatistical(
                int successes,
                int trials,
                double expectedRate,
                double confidence)
        {
            if (trials <= 0)
                return new Dictionary<string, object>
                {
                    ["passed"] = false,
                    ["error"] = "At least one trial is required.",
                    ["denominator"] = 0
                };
            double z = confidence >= 0.99d
                ? 2.5758293035489004d
                : confidence >= 0.95d
                    ? 1.959963984540054d
                    : 1.6448536269514722d;
            double observed = (double)successes / trials;
            double z2 = z * z;
            double denominator = 1d + z2 / trials;
            double center = (observed + z2 / (2d * trials)) / denominator;
            double margin = z * Math.Sqrt(
                observed * (1d - observed) / trials
                + z2 / (4d * trials * trials)) / denominator;
            double lower = Math.Max(0d, center - margin);
            double upper = Math.Min(1d, center + margin);
            return new Dictionary<string, object>
            {
                ["passed"] = expectedRate >= lower && expectedRate <= upper,
                ["successes"] = successes,
                ["denominator"] = trials,
                ["observedRate"] = observed,
                ["expectedRate"] = expectedRate,
                ["confidence"] = confidence,
                ["lowerBound"] = lower,
                ["upperBound"] = upper
            };
        }

        private static Dictionary<string, object>
            EvaluateFinalGauntletThresholdBoundary(
                double roll,
                double threshold)
        {
            return new Dictionary<string, object>
            {
                ["roll"] = roll,
                ["threshold"] = threshold,
                ["succeeded"] = roll < threshold,
                ["boundary"] = roll < threshold
                    ? "below"
                    : roll == threshold ? "at" : "above"
            };
        }

        private static Dictionary<string, string>
            FlattenFinalGauntletState(object value, string prefix = "")
        {
            Dictionary<string, string> result =
                new Dictionary<string, string>(StringComparer.Ordinal);
            if (value is IDictionary dictionary)
            {
                foreach (DictionaryEntry entry in dictionary)
                {
                    string key = Convert.ToString(entry.Key) ?? string.Empty;
                    string path = string.IsNullOrEmpty(prefix)
                        ? key
                        : prefix + "." + key;
                    foreach (KeyValuePair<string, string> child in
                        FlattenFinalGauntletState(entry.Value, path))
                        result[child.Key] = child.Value;
                }
            }
            else if (value is IEnumerable enumerable && !(value is string))
            {
                int index = 0;
                foreach (object item in enumerable)
                {
                    foreach (KeyValuePair<string, string> child in
                        FlattenFinalGauntletState(
                            item, prefix + "[" + index + "]"))
                        result[child.Key] = child.Value;
                    index++;
                }
            }
            else result[prefix] = FinalGauntletCanonical(value);
            return result;
        }
    }
}
