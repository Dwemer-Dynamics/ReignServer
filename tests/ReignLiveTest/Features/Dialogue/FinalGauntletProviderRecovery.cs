using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static bool IsFinalGauntletProviderFailure(
            Dictionary<string, object> result)
        {
            if (result == null) return false;
            if (String(result, "errorCode").Equals(
                    "controller_wait_timeout",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            string status = String(result, "status");
            bool hasRecordedFailures =
                ReadObjects(result, "failures").Any();
            bool hasFailedCommands =
                ReadObjects(result, "commands").Any(command =>
                    String(command, "status").Equals(
                        "failed",
                        StringComparison.OrdinalIgnoreCase)
                    || !string.IsNullOrWhiteSpace(
                        String(command, "error")));
            if (IsOk(result)
                && !hasRecordedFailures
                && !hasFailedCommands
                && !status.Equals(
                    "failed", StringComparison.OrdinalIgnoreCase)
                && !status.Equals(
                    "provider_exhausted",
                    StringComparison.OrdinalIgnoreCase))
                return false;

            List<string> failureText = new List<string>();
            AddFinalGauntletFailureText(
                failureText, String(result, "error"));
            foreach (Dictionary<string, object> failure in
                ReadObjects(result, "failures"))
                AddFinalGauntletFailureText(
                    failureText,
                    string.Join(
                        " ",
                        new[]
                        {
                            String(failure, "error"),
                            String(failure, "message"),
                            String(failure, "summary"),
                            String(failure, "status")
                        }));
            foreach (Dictionary<string, object> command in
                ReadObjects(result, "commands"))
            {
                if (!String(command, "status").Equals(
                        "failed", StringComparison.OrdinalIgnoreCase)
                    && string.IsNullOrWhiteSpace(
                        String(command, "error")))
                    continue;
                AddFinalGauntletFailureText(
                    failureText,
                    string.Join(
                        " ",
                        new[]
                        {
                            String(command, "error"),
                            String(command, "message"),
                            FlattenFinalGauntletText(
                                ReadObject(command, "result"))
                        }));
            }
            if (failureText.Count == 0 && !IsOk(result))
                AddFinalGauntletFailureText(
                    failureText, FlattenFinalGauntletText(result));

            string text = string.Join(" ", failureText)
                .ToLowerInvariant();
            return new[]
            {
                "provider failure", "provider failed",
                "provider unavailable", "provider is unavailable",
                "provider timeout", "rate limit", "rate-limit", "quota",
                "timed out", "timeout", "connection was closed",
                "temporarily unavailable", "http 429", "http 502",
                "http 503", "http 504", "empty model response",
                "truncated model response"
            }.Any(text.Contains);
        }

        private static void AddFinalGauntletFailureText(
            List<string> values,
            string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                values.Add(value);
        }

        private static string FlattenFinalGauntletText(object value)
        {
            if (value == null) return string.Empty;
            if (value is Dictionary<string, object> map)
                return string.Join(
                    " ",
                    map.Select(pair =>
                        pair.Key + " "
                        + FlattenFinalGauntletText(pair.Value)));
            if (value is System.Collections.ArrayList list)
                return string.Join(
                    " ",
                    list.Cast<object>().Select(FlattenFinalGauntletText));
            if (value is object[] array)
                return string.Join(
                    " ",
                    array.Select(FlattenFinalGauntletText));
            return Convert.ToString(value) ?? string.Empty;
        }

        private static string StableGauntletCorrelation(
            string runId,
            string caseInstanceId,
            int attempt)
        {
            string normalized = new string(
                (caseInstanceId ?? string.Empty)
                    .Where(char.IsLetterOrDigit)
                    .Take(42)
                    .ToArray());
            return "fg-" + Math.Abs(
                (runId ?? string.Empty).GetHashCode()).ToString("x")
                + "-" + normalized.ToLowerInvariant()
                + "-a" + attempt;
        }
    }
}
