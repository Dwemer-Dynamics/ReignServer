using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal sealed class FinalGauntletActionApplicability
    {
        public string Action = string.Empty;
        public string RequirementId = string.Empty;
        public bool Applicable;
        public string Route = string.Empty;
        public string Rationale = string.Empty;
        public bool RequiresProvider;
        public bool RequiresNativeExecution;
    }

    internal static class FinalConversationGauntletActionConformance
    {
        private static readonly HashSet<string> ProviderRows =
            new HashSet<string>(
                new[] { "ACT-002", "ACT-003", "ACT-004", "ACT-005", "ACT-006" },
                StringComparer.OrdinalIgnoreCase);

        private static readonly HashSet<string> NativeMutationRows =
            new HashSet<string>(
                new[]
                {
                    "ACT-024", "ACT-025", "ACT-029", "ACT-030",
                    "ACT-031", "ACT-032"
                },
                StringComparer.OrdinalIgnoreCase);

        internal static List<FinalGauntletActionApplicability> BuildMatrix(
            IEnumerable<string> registeredActions)
        {
            List<FinalGauntletActionApplicability> rows =
                new List<FinalGauntletActionApplicability>();
            foreach (string action in (registeredActions ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                for (int number = 1; number <= 35; number++)
                {
                    string requirement = "ACT-" + number.ToString("000");
                    bool provider = ProviderRows.Contains(requirement);
                    bool native = NativeMutationRows.Contains(requirement);
                    rows.Add(new FinalGauntletActionApplicability
                    {
                        Action = action,
                        RequirementId = requirement,
                        Applicable = true,
                        Route = provider
                            ? "production_generation"
                            : native
                                ? "production_resolver_validator_executor"
                                : "deterministic_production_component",
                        Rationale =
                            "The registered action participates in the common "
                            + (provider
                                ? "semantic selection contract."
                                : native
                                    ? "execution and receipt contract."
                                    : "resolver, validation, or safety contract."),
                        RequiresProvider = provider,
                        RequiresNativeExecution = native
                    });
                }
            }
            return rows;
        }

        internal static List<string> ValidateMatrix(
            IEnumerable<string> registeredActions,
            IEnumerable<FinalGauntletActionApplicability> matrix)
        {
            string[] actions = (registeredActions ?? Array.Empty<string>())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            List<FinalGauntletActionApplicability> rows =
                (matrix ?? Array.Empty<FinalGauntletActionApplicability>())
                .Where(row => row != null)
                .ToList();
            List<string> errors = new List<string>();
            foreach (string action in actions)
            {
                List<FinalGauntletActionApplicability> actionRows = rows
                    .Where(row => string.Equals(
                        row.Action, action, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                for (int number = 1; number <= 35; number++)
                {
                    string requirement = "ACT-" + number.ToString("000");
                    List<FinalGauntletActionApplicability> matches = actionRows
                        .Where(row => string.Equals(
                            row.RequirementId,
                            requirement,
                            StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (matches.Count != 1)
                    {
                        errors.Add(action + " " + requirement
                            + " requires exactly one applicability record.");
                        continue;
                    }
                    FinalGauntletActionApplicability row = matches[0];
                    if (string.IsNullOrWhiteSpace(row.Route)
                        || string.IsNullOrWhiteSpace(row.Rationale))
                        errors.Add(action + " " + requirement
                            + " lacks applicability evidence.");
                }
            }
            foreach (FinalGauntletActionApplicability row in rows)
                if (!actions.Contains(row.Action, StringComparer.OrdinalIgnoreCase))
                    errors.Add("Applicability row references unregistered action "
                        + row.Action + ".");
            return errors;
        }

        internal static Dictionary<string, object> ToEvidence(
            FinalGauntletActionApplicability row)
        {
            return new Dictionary<string, object>
            {
                ["action"] = row.Action,
                ["requirementId"] = row.RequirementId,
                ["applicable"] = row.Applicable,
                ["route"] = row.Route,
                ["rationale"] = row.Rationale,
                ["requiresProvider"] = row.RequiresProvider,
                ["requiresNativeExecution"] = row.RequiresNativeExecution
            };
        }
    }
}
