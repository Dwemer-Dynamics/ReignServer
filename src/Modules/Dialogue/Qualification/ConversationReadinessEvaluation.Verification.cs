using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class DialogueService
    {
        internal static List<Dictionary<string, object>> RunConversationReadinessEvaluationSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) =>
                rows.Add(new Dictionary<string, object>
                {
                    ["id"] = "conversation_readiness_evaluation_" + id,
                    ["suite"] = "conversation_readiness_evaluation",
                    ["passed"] = passed,
                    ["summary"] = summary,
                    ["data"] = data
                });
            string sample = "prefix\nCHARACTER FOUNDATION - REUSABLE UNTIL THIS CHARACTER CHANGES\n"
                + "BASE PERSONALITY\nA cautious but candid speaker.\n"
                + "AUTHORITATIVE CURRENT ROLE ATTRIBUTION\nsuffix";
            string extracted = ReadinessExtractSection(sample,
                "CHARACTER FOUNDATION - REUSABLE UNTIL THIS CHARACTER CHANGES",
                "AUTHORITATIVE CURRENT ROLE ATTRIBUTION", 6500);
            add("character_card_is_bounded",
                extracted.Contains("cautious but candid", StringComparison.OrdinalIgnoreCase)
                && !extracted.Contains("suffix", StringComparison.OrdinalIgnoreCase),
                "The blinded judge receives the bounded character card rather than unrelated prompt material.",
                extracted);

            add("generic_public_recognition_flavor_is_not_a_leak",
                ConversationReadinessIdentityRecognitionRubricRule.Contains(
                    "word travels", StringComparison.OrdinalIgnoreCase)
                && ConversationReadinessIdentityRecognitionRubricRule.Contains(
                    "not a knowledge leak", StringComparison.OrdinalIgnoreCase)
                && ConversationReadinessIdentityRecognitionRubricRule.Contains(
                    "named witness", StringComparison.OrdinalIgnoreCase),
                "Authorized identity recognition permits harmless public-notoriety flavor while retaining penalties for consequential invented provenance.",
                ConversationReadinessIdentityRecognitionRubricRule);

            add("gauntlet_judges_inherit_physical_call_budget",
                ConversationReadinessBudgetCorrelation(
                    new Dictionary<string, object>
                    {
                        ["gauntletCorrelationId"] = "corr-gauntlet",
                        ["correlationId"] = "unscoped-live-run"
                    }) == "corr-gauntlet"
                && ReadString(
                    BuildConversationReadinessJudgeSchemaRepairRequest(
                        "campaign-test",
                        new Dictionary<string, object>
                        {
                            ["caseId"] = "C1"
                        },
                        new Dictionary<string, object>(),
                        new List<string> { "case_missing" },
                        "corr-gauntlet-judge-schema-0"),
                    "correlationId", "")
                    == "corr-gauntlet-judge-schema-0",
                "Blinded judge, calibration, and schema-repair calls inherit the parent gauntlet correlation and therefore consume the same physical-call ledger.",
                null);

            Dictionary<string, object> context = ReadinessCompactContextEvidence(
                new Dictionary<string, object>
                {
                    ["memorySummary"] = "summary",
                    ["memories"] = Enumerable.Range(0, 20).Select(index =>
                        new Dictionary<string, object> { ["id"] = "m" + index }).ToList(),
                    ["priorLines"] = Enumerable.Range(0, 20).Select(index =>
                        new Dictionary<string, object> { ["id"] = "t" + index }).ToList(),
                    ["eventLines"] = Enumerable.Range(0, 20).Select(index =>
                        new Dictionary<string, object>
                        {
                            ["turnId"] = "e" + index, ["speaker"] = "NPC " + index,
                            ["role"] = "npc", ["text"] = "line " + index
                        }).ToList()
                });
            add("judge_evidence_is_bounded",
                ReadDictionaryList(context, "memories").Count == 6
                && ReadDictionaryList(context, "priorLines").Count == 8
                && ReadDictionaryList(context, "eventLines").Count == 10
                && ReadString(ReadDictionaryList(context, "eventLines").First(), "id", "") == "e10",
                "Rubric evaluation preserves representative memory and attributed group-turn evidence without rebuilding an unbounded production prompt.",
                context);

            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> assertions = new List<Dictionary<string, object>>();
            AppendReadinessRubricAssertion(assertions, failures,
                new Dictionary<string, object> { ["commandId"] = "cmd" },
                "factualAccuracyRubric", false,
                new Dictionary<string, object>
                {
                    ["cases"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["caseId"] = "C1", ["factualAccuracy"] = 0.2d,
                            ["knowledgeLeak"] = true
                        }
                    }
                }, "Blinded factual-grounding accuracy");
            add("critical_rubric_flags_remain_critical",
                failures.Count == 1 && ReadBool(failures[0], "critical", false),
                "Knowledge leaks and other hard evidence violations cannot be averaged into a passing readiness score.",
                failures);
            List<Dictionary<string, object>> relationshipFailures =
                new List<Dictionary<string, object>>();
            AppendReadinessRubricAssertion(
                new List<Dictionary<string, object>>(),
                relationshipFailures,
                new Dictionary<string, object>
                {
                    ["commandId"] = "relationship-history-leak"
                },
                "relationshipHistoryRubric",
                false,
                new Dictionary<string, object>
                {
                    ["cases"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object>
                        {
                            ["caseId"] = "C1",
                            ["relationshipHistoryQuality"] = 0.95d,
                            ["relationshipHistoryLeak"] = true
                        }
                    }
                },
                "Blinded shared relationship-history quality and privacy");
            add("private_interpretation_leak_is_critical",
                relationshipFailures.Count == 1
                && ReadBool(relationshipFailures[0], "critical", false),
                "A reply that exposes the other NPC's private relationship interpretation is a critical qualification failure even when its prose quality is otherwise high.",
                relationshipFailures);

            Dictionary<string, object> expectedJudgeCase = new Dictionary<string, object>
            {
                ["caseId"] = "C1"
            };
            Dictionary<string, object> validJudgeCase = new Dictionary<string, object>
            {
                ["caseId"] = "C1",
                ["personalityConsistency"] = 0.91d,
                ["factualAccuracy"] = 0.92d,
                ["groupAwareness"] = 0.93d,
                ["relationshipHistoryQuality"] = 0.94d,
                ["clanTierRecognition"] = 1d,
                ["manipulationCapability"] = 1d,
                ["canonicalContradiction"] = false,
                ["knowledgeLeak"] = false,
                ["wrongOwner"] = false,
                ["fabricatedCaughtLie"] = false,
                ["relationshipHistoryLeak"] = false,
                ["hiddenStatusLeak"] = false,
                ["forcedManipulation"] = false,
                ["strategicAttractionPresentedAsGenuine"] = false,
                ["personalityReason"] = "grounded",
                ["factualReason"] = "grounded",
                ["groupReason"] = "grounded",
                ["relationshipHistoryReason"] = "grounded",
                ["clanTierReason"] = "not requested",
                ["manipulationReason"] = "not requested",
                ["evidenceQuotes"] = new List<object>()
            };
            add("strict_judge_schema_accepts_complete_typed_case",
                ConversationReadinessJudgeCaseSchemaViolations(
                    validJudgeCase, expectedJudgeCase).Count == 0,
                "A fully typed blinded-judge case remains eligible for scoring.",
                validJudgeCase);

            Dictionary<string, object> proseInScore = new Dictionary<string, object>(
                validJudgeCase, StringComparer.OrdinalIgnoreCase)
            {
                ["relationshipHistoryQuality"] =
                    "The reply preserves the speaker's private interpretation."
            };
            List<string> proseViolations = ConversationReadinessJudgeCaseSchemaViolations(
                proseInScore, expectedJudgeCase);
            add("prose_in_numeric_score_requires_recovery",
                proseViolations.Contains(
                    "relationshipHistoryQuality:expected_json_number_0_to_1"),
                "Evaluator prose in a numeric score is rejected for targeted recovery instead of being silently converted to zero.",
                proseViolations);

            Dictionary<string, object> glmTypeSwap =
                new Dictionary<string, object>(
                    validJudgeCase, StringComparer.OrdinalIgnoreCase)
                {
                    ["groupAwareness"] =
                        "The reply directly considers the earlier speaker.",
                    ["groupReason"] = null
                };
            List<string> glmTypeSwapViolations =
                ConversationReadinessJudgeCaseSchemaViolations(
                    glmTypeSwap, expectedJudgeCase);
            Dictionary<string, object> glmTypeSwapRepairRequest =
                BuildConversationReadinessJudgeSchemaRepairRequest(
                    "campaign-test",
                    expectedJudgeCase,
                    glmTypeSwap,
                    glmTypeSwapViolations);
            string glmTypeSwapRepairJson =
                Json.Serialize(glmTypeSwapRepairRequest);
            add("glm5_type_swap_gets_strict_bounded_schema_repair",
                glmTypeSwapViolations.Contains(
                    "groupAwareness:expected_json_number_0_to_1")
                && glmTypeSwapViolations.Contains(
                    "groupReason:expected_json_string")
                && ReadString(
                    glmTypeSwapRepairRequest,
                    "requestType", "") == "readiness_evaluation"
                && ReadLong(
                    glmTypeSwapRepairRequest,
                    "maxTokens", 0) == 3200
                && glmTypeSwapRepairJson.Contains(
                    "Every score field must be a JSON number")
                && glmTypeSwapRepairJson.Contains(
                    "groupAwareness:expected_json_number_0_to_1")
                && glmTypeSwapRepairJson.Contains(
                    "groupReason:expected_json_string"),
                "GLM-5 prose-in-score and missing-reason output is rejudged through the strict one-case typed schema instead of being coerced or failed immediately.",
                new Dictionary<string, object>
                {
                    ["violations"] = glmTypeSwapViolations,
                    ["request"] = glmTypeSwapRepairRequest
                });

            Dictionary<string, object> calibrationExpected =
                new Dictionary<string, object>
                {
                    ["caseId"] = "C1",
                    ["requestedRubrics"] =
                        new Dictionary<string, object>
                        {
                            ["personality"] = false,
                            ["factual"] = false,
                            ["group"] = false,
                            ["relationshipHistory"] = true,
                            ["clanTierRecognition"] = false,
                            ["manipulation"] = false
                        }
                };
            Dictionary<string, object> positiveBorderline =
                new Dictionary<string, object>(
                    validJudgeCase, StringComparer.OrdinalIgnoreCase)
                {
                    ["relationshipHistoryQuality"] = 0.78d,
                    ["relationshipHistoryReason"] =
                        "Correctly preserves the private boundary and names no defect."
                };
            List<string> borderlineRubrics =
                ConversationReadinessBorderlineRequestedRubrics(
                    positiveBorderline, calibrationExpected);
            Dictionary<string, object> calibrationRequest =
                BuildConversationReadinessJudgeCalibrationRequest(
                    "campaign-test",
                    calibrationExpected,
                    positiveBorderline,
                    borderlineRubrics);
            string calibrationRequestJson =
                Json.Serialize(calibrationRequest);
            positiveBorderline["relationshipHistoryQuality"] = 0.80d;
            List<string> passingRubrics =
                ConversationReadinessBorderlineRequestedRubrics(
                    positiveBorderline, calibrationExpected);
            positiveBorderline["relationshipHistoryQuality"] = 0.69d;
            List<string> clearFailureRubrics =
                ConversationReadinessBorderlineRequestedRubrics(
                    positiveBorderline, calibrationExpected);
            add("borderline_positive_judgment_gets_bounded_calibration_review",
                borderlineRubrics.Count == 1
                && borderlineRubrics[0] == "relationshipHistoryQuality"
                && passingRubrics.Count == 0
                && clearFailureRubrics.Count == 0
                && ReadString(
                    calibrationRequest,
                    "requestType", "") == "readiness_evaluation"
                && ReadLong(
                    calibrationRequest,
                    "maxTokens", 0) == 3200
                && calibrationRequestJson.Contains(
                    "keep the score below 0.80 only when")
                && calibrationRequestJson.Contains(
                    "relationshipHistoryQuality"),
                "A 0.70-0.79 requested score receives one evidence-grounded calibration review, while a clear failure or passing score is not repeatedly judged.",
                new Dictionary<string, object>
                {
                    ["borderline"] = borderlineRubrics,
                    ["passing"] = passingRubrics,
                    ["clearFailure"] = clearFailureRubrics,
                    ["request"] = calibrationRequest
                });

            Dictionary<string, object> stringBoolean = new Dictionary<string, object>(
                validJudgeCase, StringComparer.OrdinalIgnoreCase)
            {
                ["relationshipHistoryLeak"] = "false"
            };
            List<string> booleanViolations = ConversationReadinessJudgeCaseSchemaViolations(
                stringBoolean, expectedJudgeCase);
            add("quoted_boolean_requires_recovery",
                booleanViolations.Contains(
                    "relationshipHistoryLeak:expected_json_boolean"),
                "Quoted critical-safety flags cannot pass as booleans and are recovered explicitly.",
                booleanViolations);
            Dictionary<string, object> partiallyRequestedCase =
                new Dictionary<string, object>(
                    validJudgeCase, StringComparer.OrdinalIgnoreCase)
                {
                    ["manipulationCapability"] =
                        "Not requested in this case.",
                    ["manipulationReason"] = 0d
                };
            Dictionary<string, object> partiallyRequestedExpected =
                new Dictionary<string, object>
                {
                    ["caseId"] = "C1",
                    ["requestedRubrics"] =
                        new Dictionary<string, object>
                        {
                            ["personality"] = true,
                            ["factual"] = false,
                            ["group"] = true,
                            ["relationshipHistory"] = true,
                            ["clanTierRecognition"] = false,
                            ["manipulation"] = false
                        }
                };
            List<string> unrequestedViolations =
                ConversationReadinessJudgeCaseSchemaViolations(
                    partiallyRequestedCase,
                    partiallyRequestedExpected);
            add("unrequested_rubric_schema_noise_is_neutralized",
                unrequestedViolations.Count == 0
                && ReadDouble(
                    partiallyRequestedCase,
                    "manipulationCapability", 0d) == 1d
                && ReadString(
                    partiallyRequestedCase,
                    "manipulationReason", "") == "Not requested.",
                "Malformed fields for an explicitly unrequested rubric are normalized to neutral and cannot falsely fail otherwise valid requested-rubric judgments.",
                new Dictionary<string, object>
                {
                    ["violations"] = unrequestedViolations,
                    ["normalized"] = partiallyRequestedCase
                });
            add("batch_output_budget_avoids_predictable_truncation",
                ConversationReadinessJudgeMaxTokens(1) >= 2600
                && ConversationReadinessJudgeMaxTokens(4) >= 6800
                && ConversationReadinessJudgeMaxTokens(5) <= 10000,
                "Blinded batches receive enough bounded output room for every typed case while retaining a hard cap.",
                new Dictionary<string, object>
                {
                    ["oneCase"] = ConversationReadinessJudgeMaxTokens(1),
                    ["fourCases"] = ConversationReadinessJudgeMaxTokens(4),
                    ["fiveCases"] = ConversationReadinessJudgeMaxTokens(5)
                });
            add("peer_tier_rubric_preserves_other_public_power_axes",
                ConversationReadinessClanTierRubricRule.Contains(
                    "formal parity on the clan-tier axis")
                && ConversationReadinessClanTierRubricRule.Contains(
                    "holdings, kingdom office")
                && ConversationReadinessClanTierRubricRule.Contains(
                    "reject rank as an automatic entitlement")
                && ConversationReadinessClanTierRubricRule.Contains(
                    "wrong relative clan tier"),
                "The blinded rubric distinguishes equal native clan tier from unequal holdings, office, wealth, and influence, so valid court-intrigue judgments are neither flattened nor falsely failed.",
                ConversationReadinessClanTierRubricRule);
            return rows;
        }
    }
}
