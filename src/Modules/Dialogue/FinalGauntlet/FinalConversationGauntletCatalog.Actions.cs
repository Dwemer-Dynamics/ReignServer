using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class FinalConversationGauntletCatalog
    {
        private static void AddActionRequirementMatrix(
            List<FinalGauntletCaseDescriptor> target,
            IReadOnlyCollection<string> registeredActions)
        {
            string[] requirements = ApprovedScenarioTitles["ACT"];
            foreach (string action in (registeredActions ?? Array.Empty<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                for (int index = 0; index < requirements.Length; index++)
                {
                    string requirementId = "ACT-" + (index + 1).ToString("000");
                    target.Add(new FinalGauntletCaseDescriptor
                    {
                        CaseId = "ACTION-" + Slug(action).ToUpperInvariant()
                            + "-" + (index + 1).ToString("000"),
                        Family = "action_conformance",
                        EvaluationKind = "hard",
                        ExecutionKind = "generated_action_fixture",
                        Mode = "varied",
                        RequiresProvider = index >= 1 && index <= 5,
                        RequiresGame = true,
                        RequirementIds = new[] { requirementId },
                        Tags = new[]
                        {
                            "matrix:action",
                            "action:" + action,
                            "requirement:" + requirementId
                        },
                        BehavioralRequirement =
                            "Apply " + requirements[index] + " to registered action '"
                            + action + "'.",
                        HardProhibitions = new[]
                        {
                            "Do not silently exclude a registered action.",
                            "Do not execute an invalid or duplicate action."
                        },
                        PrerequisiteCapabilities = new[]
                        {
                            "action_registry", "resolver", "validator", "executor"
                        },
                        EvidenceNeeds = new[]
                        {
                            "registry_entry", "resolver_audit", "validation_result",
                            "state_before_and_after", "execution_receipt"
                        }
                    });
                }
            }
        }

        private static void AddHonorBoldnessMatrix(
            List<FinalGauntletCaseDescriptor> target)
        {
            int[] values = { 0, 25, 50, 75, 100 };
            foreach (string gender in new[] { "male", "female" })
                foreach (int honor in values)
                    foreach (int boldness in values)
                        target.Add(GeneratedDescriptor(
                            "HB-" + gender.ToUpperInvariant() + "-"
                            + honor.ToString("000") + "-" + boldness.ToString("000"),
                            "personality_matrix",
                            "Hold all other state constant and verify the interaction of Honor "
                            + honor + " and Boldness " + boldness + " for a " + gender + " noble.",
                            new[]
                            {
                                "matrix:honor_boldness",
                                "gender:" + gender,
                                "honor:" + honor,
                                "boldness:" + boldness
                            }));
        }

        private static void AddPairwiseMatrix(
            List<FinalGauntletCaseDescriptor> target)
        {
            Dictionary<string, string[]> axes = new Dictionary<string, string[]>
            {
                ["mode"] = new[] { "individual", "party", "social_event", "wilderness_event", "correspondence", "other" },
                ["npc_type"] = new[] { "ruler", "lord", "lady", "wanderer", "notable", "governor", "ambassador", "prisoner", "companion" },
                ["relationship"] = new[] { "hostile", "negative", "neutral", "positive", "loyal" },
                ["honor"] = new[] { "0", "25", "50", "75", "100" },
                ["boldness"] = new[] { "0", "25", "50", "75", "100" },
                ["other_traits"] = new[] { "low", "medium", "high" },
                ["status"] = new[] { "npc_above", "equal", "player_above" },
                ["privacy"] = new[] { "private", "limited", "public" },
                ["familiarity"] = new[] { "first_meeting", "acquaintance", "close_history" },
                ["political"] = new[] { "same_faction", "neutral_foreign", "allied", "enemy" },
                ["family"] = new[] { "unrelated", "spouse", "parent", "child", "sibling", "in_law" },
                ["time"] = new[] { "morning", "afternoon", "evening", "night" },
                ["location"] = new[] { "court", "private_room", "social_room", "exterior", "traveling", "ship" },
                ["action_state"] = new[] { "eligible", "ambiguous", "invalid", "unavailable" },
                ["memory_state"] = new[] { "none", "relevant", "conflicting", "stale", "overloaded" },
                ["rumor_state"] = new[] { "none", "known", "reputation", "refuted" }
            };
            List<string> names = axes.Keys.ToList();
            int caseNumber = 0;
            for (int left = 0; left < names.Count; left++)
            {
                for (int right = left + 1; right < names.Count; right++)
                {
                    foreach (string leftValue in axes[names[left]])
                    {
                        foreach (string rightValue in axes[names[right]])
                        {
                            target.Add(GeneratedDescriptor(
                                "PAIR-" + (++caseNumber).ToString("00000"),
                                "pairwise_matrix",
                                "Verify the isolated interaction between "
                                + names[left] + "=" + leftValue + " and "
                                + names[right] + "=" + rightValue + ".",
                                new[]
                                {
                                    "matrix:pairwise",
                                    names[left] + ":" + leftValue,
                                    names[right] + ":" + rightValue
                                }));
                        }
                    }
                }
            }
        }
    }
}
