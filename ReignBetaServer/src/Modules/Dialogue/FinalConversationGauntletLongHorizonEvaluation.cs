using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletLongHorizonEvaluation
    {
        internal static Dictionary<string, object> EvaluateKnowledgeMatrix(
            Dictionary<string, HashSet<string>> expected,
            Dictionary<string, HashSet<string>> observed)
        {
            expected = expected
                ?? new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);
            observed = observed
                ?? new Dictionary<string, HashSet<string>>(
                    StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> rows =
                new List<Dictionary<string, object>>();
            foreach (KeyValuePair<string, HashSet<string>> pair in expected)
            {
                HashSet<string> actual = observed.TryGetValue(
                    pair.Key, out HashSet<string> found)
                    ? found
                    : new HashSet<string>(
                        StringComparer.OrdinalIgnoreCase);
                List<string> missing = pair.Value
                    .Where(item => !actual.Contains(item)).ToList();
                List<string> leaked = actual
                    .Where(item => !pair.Value.Contains(item)).ToList();
                rows.Add(new Dictionary<string, object>
                {
                    ["heroId"] = pair.Key,
                    ["passed"] =
                        missing.Count == 0 && leaked.Count == 0,
                    ["missing"] = missing,
                    ["leaked"] = leaked
                });
            }
            return new Dictionary<string, object>
            {
                ["ok"] = rows.All(row =>
                    Convert.ToBoolean(row["passed"])),
                ["rows"] = rows,
                ["heroCount"] = rows.Count
            };
        }
    }
}
