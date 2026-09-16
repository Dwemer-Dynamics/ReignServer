using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletEvaluationSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type program = typeof(Program);
            BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            MethodInfo evaluate = program.GetMethod(
                "EvaluateFinalGauntletCase", flags);
            MethodInfo differential = program.GetMethod(
                "EvaluateFinalGauntletDifferential", flags);
            MethodInfo sequence = program.GetMethod(
                "EvaluateFinalGauntletSequence", flags);
            MethodInfo statistical = program.GetMethod(
                "EvaluateFinalGauntletStatistical", flags);
            MethodInfo rubric = program.GetMethod(
                "BuildFinalGauntletSemanticRubricCase", flags);

            add(
                "evaluator_contracts_are_registered",
                evaluate != null && differential != null && sequence != null
                    && statistical != null && rubric != null,
                "Hard, semantic, differential, sequence, and statistical evaluator seams are present.");
            if (evaluate == null || differential == null || sequence == null
                || statistical == null || rubric == null)
                return checks;

            Dictionary<string, object> fixture =
                FinalGauntletEvaluatorFixture();
            Dictionary<string, object> cleanEvidence =
                FinalGauntletEvaluatorEvidence("npc_a", false);
            Dictionary<string, object> clean = evaluate.Invoke(
                null,
                new object[]
                {
                    fixture,
                    cleanEvidence,
                    FinalGauntletPerfectSemanticScores()
                }) as Dictionary<string, object>;
            add(
                "hard_assertions_accept_grounded_case",
                ReadBool(clean, "passed", false)
                    && ReadBool(clean, "hardPassed", false),
                "A grounded, schema-valid, correctly targeted case passes its authoritative assertions.");

            Dictionary<string, object> unsafeEvidence =
                FinalGauntletEvaluatorEvidence("wrong_npc", true);
            Dictionary<string, object> unsafeResult = evaluate.Invoke(
                null,
                new object[]
                {
                    fixture,
                    unsafeEvidence,
                    FinalGauntletPerfectSemanticScores()
                }) as Dictionary<string, object>;
            add(
                "semantic_scores_cannot_override_zero_tolerance_failure",
                !ReadBool(unsafeResult, "passed", true)
                    && !ReadBool(unsafeResult, "hardPassed", true)
                    && ReadDictionaryList(unsafeResult, "assertions")
                        .Any(row => ReadBool(row, "critical", false)
                            && !ReadBool(row, "passed", true)),
                "Wrong-target execution and private knowledge leakage remain release-blocking despite perfect semantic scores.");

            Dictionary<string, object> diff = differential.Invoke(
                null,
                new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["identity"] = "npc_a", ["relationship"] = -10,
                        ["location"] = "town_a"
                    },
                    new Dictionary<string, object>
                    {
                        ["identity"] = "npc_a", ["relationship"] = 10,
                        ["location"] = "town_a"
                    },
                    new Dictionary<string, object>
                    {
                        ["permittedChangedPaths"] =
                            new List<object> { "relationship" },
                        ["invariantPaths"] =
                            new List<object> { "identity", "location" }
                    }
                }) as Dictionary<string, object>;
            add(
                "differential_assertions_use_declared_invariants",
                ReadBool(diff, "passed", false),
                "Paired cases judge declared state invariants rather than wording similarity.");

            Dictionary<string, object> sequenceResult = sequence.Invoke(
                null,
                new object[]
                {
                    new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["turn"] = 1, ["ownerHeroId"] = "npc_a",
                            ["sourceId"] = "promise-1", ["status"] = "open"
                        },
                        new Dictionary<string, object>
                        {
                            ["turn"] = 2, ["ownerHeroId"] = "npc_a",
                            ["sourceId"] = "promise-1", ["status"] = "fulfilled"
                        }
                    },
                    new Dictionary<string, object>
                    {
                        ["ownerHeroId"] = "npc_a",
                        ["sourceId"] = "promise-1",
                        ["terminalStatus"] = "fulfilled"
                    }
                }) as Dictionary<string, object>;
            add(
                "sequence_assertions_follow_evidence_ledger",
                ReadBool(sequenceResult, "passed", false),
                "Sequence evaluation verifies ownership, lineage, order, and terminal state against the accumulated ledger.");

            Dictionary<string, object> stats = statistical.Invoke(
                null,
                new object[] { 500, 10000, 0.05d, 0.95d })
                as Dictionary<string, object>;
            add(
                "statistical_assertions_emit_wilson_interval",
                ReadBool(stats, "passed", false)
                    && ReadDouble(stats, "lowerBound", 0d) < 0.05d
                    && ReadDouble(stats, "upperBound", 0d) > 0.05d,
                "RNG-only checks report an honest Wilson confidence interval without provider calls.");

            Dictionary<string, object> rubricCase = rubric.Invoke(
                null, new object[] { fixture, cleanEvidence })
                as Dictionary<string, object>;
            add(
                "semantic_adapter_covers_twelve_roleplay_dimensions",
                ReadStringList(rubricCase, "dimensions").Count == 12
                    && !ReadBool(rubricCase, "authoritative", true),
                "The blinded semantic adapter requests all twelve role-play dimensions and remains subordinate to hard assertions.");
            return checks;
        }

        private static Dictionary<string, object> FinalGauntletEvaluatorFixture()
        {
            return new Dictionary<string, object>
            {
                ["caseId"] = "ACT-024",
                ["expected"] = new Dictionary<string, object>
                {
                    ["hardRequirements"] = new List<object>
                    {
                        new Dictionary<string, object>
                        {
                            ["kind"] = "path_equals",
                            ["path"] = "authoritativeAfter.gold",
                            ["expected"] = 100
                        },
                        new Dictionary<string, object>
                        {
                            ["kind"] = "action_target",
                            ["expected"] = "npc_a"
                        }
                    },
                    ["forbiddenContent"] =
                        new List<object> { "system prompt", "memory score" }
                }
            };
        }

        private static Dictionary<string, object>
            FinalGauntletEvaluatorEvidence(
                string resolvedTarget,
                bool leakSecret)
        {
            return new Dictionary<string, object>
            {
                ["schema"] = "reign-final-gauntlet-evidence-v3",
                ["authoritativeAfter"] =
                    new Dictionary<string, object> { ["gold"] = 100 },
                ["modelEvidence"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["data"] = new Dictionary<string, object>
                        {
                            ["parsedResponse"] =
                                new Dictionary<string, object>
                                {
                                    ["reply"] = leakSecret
                                        ? "The private memory score is seven."
                                        : "I will consider the offer."
                                }
                        }
                    }
                },
                ["actionResolverValidationEvidence"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["data"] = new Dictionary<string, object>
                        {
                            ["resolvedTargetId"] = resolvedTarget,
                            ["validatorResult"] = "accepted",
                            ["executed"] = true
                        }
                    }
                }
            };
        }

        private static Dictionary<string, object>
            FinalGauntletPerfectSemanticScores()
        {
            Dictionary<string, object> scores =
                new Dictionary<string, object>();
            foreach (string dimension in new[]
            {
                "factualGrounding", "epistemicRealism", "characterFidelity",
                "socialAwareness", "emotionalContinuity", "agency",
                "situationalEmbodiment", "memoryIntegration", "responsiveness",
                "linguisticNaturalness", "specificity", "nonRepetition"
            })
                scores[dimension] = 4;
            return scores;
        }
    }
}
