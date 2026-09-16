using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object> EvaluateFinalGauntletCase(
            Dictionary<string, object> fixture,
            Dictionary<string, object> evidence,
            Dictionary<string, object> semanticScores)
        {
            fixture = fixture ?? new Dictionary<string, object>();
            evidence = evidence ?? new Dictionary<string, object>();
            semanticScores = semanticScores ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> assertions =
                EvaluateFinalGauntletHardAssertions(fixture, evidence);
            bool hardPassed = assertions.All(row =>
                ReadBool(row, "passed", false));
            List<string> dimensions = FinalGauntletRubricDimensions();
            bool semanticComplete = dimensions.All(name =>
                semanticScores.ContainsKey(name)
                && ReadDouble(semanticScores, name, -1d) >= 0d
                && ReadDouble(semanticScores, name, -1d) <= 4d);
            bool semanticPassed = semanticComplete && dimensions.All(name =>
                ReadDouble(semanticScores, name, 0d) >= 3d);
            return new Dictionary<string, object>
            {
                ["caseId"] = ReadString(fixture, "caseId", ""),
                ["passed"] = hardPassed && semanticPassed,
                ["hardPassed"] = hardPassed,
                ["semanticPassed"] = semanticPassed,
                ["semanticComplete"] = semanticComplete,
                ["semanticScores"] = semanticScores,
                ["assertions"] = assertions,
                ["criticalFailureCount"] = assertions.Count(row =>
                    ReadBool(row, "critical", false)
                    && !ReadBool(row, "passed", true))
            };
        }

        private static Dictionary<string, object>
            EvaluateFinalGauntletFamilyRollup(
                string family,
                IEnumerable<Dictionary<string, object>> caseResults)
        {
            List<Dictionary<string, object>> results =
                (caseResults ?? Array.Empty<Dictionary<string, object>>())
                .ToList();
            int passed = results.Count(row => ReadBool(row, "passed", false));
            Dictionary<string, object> interval =
                EvaluateFinalGauntletStatistical(
                    passed, results.Count, results.Count == 0
                        ? 0d
                        : (double)passed / results.Count, 0.95d);
            return new Dictionary<string, object>
            {
                ["family"] = family ?? string.Empty,
                ["denominator"] = results.Count,
                ["passed"] = passed,
                ["failed"] = results.Count - passed,
                ["passRate"] = results.Count == 0
                    ? 0d
                    : (double)passed / results.Count,
                ["wilsonLowerBound"] = ReadDouble(
                    interval, "lowerBound", 0d),
                ["wilsonUpperBound"] = ReadDouble(
                    interval, "upperBound", 0d),
                ["criticalFailureCount"] = results.Sum(row =>
                    (int)ReadLong(row, "criticalFailureCount", 0))
            };
        }
    }
}
