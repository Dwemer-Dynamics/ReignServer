using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string ConversationReadinessClanTierRubricRule =
            "Equal clan tier means formal parity on the clan-tier axis, not "
            + "equal holdings, kingdom office, council access, public wealth, "
            + "renown, troops, marriage connections, or practical influence. "
            + "An NPC may correctly judge a same-tier character weaker or "
            + "stronger on those separately supplied public axes, reject rank "
            + "as an automatic entitlement, or demand useful business without "
            + "claiming a false clan-tier difference. Do not score that below "
            + "0.80 unless the reply actually denies the known affiliation, "
            + "states or clearly asserts the wrong relative clan tier, or "
            + "otherwise contradicts the authoritative standing evidence. ";

        private const string ConversationReadinessIdentityRecognitionRubricRule =
            "When observer-specific identity evidence authorizes recognition, "
            + "generic flavor such as 'word travels', public reputation, or "
            + "recognizing a well-known face is acceptable and is not a "
            + "knowledge leak merely because the internal deterministic "
            + "recognition source has a different mechanical label. Penalize "
            + "recognition provenance only when the reply invents a "
            + "consequential specific fact, such as an actual prior meeting, "
            + "a named witness, a private report, a particular portrait, or "
            + "another source whose existence matters to the conversation. ";

        private static Dictionary<string, object> ConversationReadinessEvaluateRunApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", ResolveLogCampaignId(""));
            string runId = ReadString(payload, "runId", "");
            Dictionary<string, object> run = LoadLiveTestRun(campaignId, runId);
            if (run.Count == 0)
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Live-test run was not found." };
            if (!ReadString(run, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase))
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "The run must complete before blinded evaluation." };

            List<Dictionary<string, object>> existing = ReadDictionaryList(run, "readinessEvaluations");
            HashSet<string> evaluatedCommands = new HashSet<string>(
                existing.Select(row => ReadString(row, "commandId", "")),
                StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> assertions = ReadDictionaryList(run, "assertions");
            List<Dictionary<string, object>> failures = ReadDictionaryList(run, "failures");
            List<Dictionary<string, object>> audit = CollectLiveTestRunAuditEvidence(run);
            List<Dictionary<string, object>> pendingCommands = ReadDictionaryList(run, "commands")
                .Where(row => ReadString(row, "operation", "").Equals("send", StringComparison.OrdinalIgnoreCase)
                    && ReadString(row, "status", "").Equals("completed", StringComparison.OrdinalIgnoreCase)
                    && !evaluatedCommands.Contains(ReadString(row, "commandId", "")))
                .Where(command =>
                {
                    List<string> categories = ReadStringList(command, "readinessCategories");
                    return categories.Contains("personality_consistency", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("detailed_factual_accuracy", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("world_local_knowledge", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("identity_role_authority", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("group_awareness", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("shared_relationship_history", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("clan_tier_recognition", StringComparer.OrdinalIgnoreCase)
                        || categories.Contains("manipulation_capabilities", StringComparer.OrdinalIgnoreCase);
                }).ToList();
            Dictionary<string, Dictionary<string, object>> batchEvaluations =
                EvaluateConversationReadinessCommandBatch(campaignId, run, pendingCommands, audit);
            int evaluated = 0;

            foreach (Dictionary<string, object> command in pendingCommands)
            {
                string commandId = ReadString(command, "commandId", "");
                List<string> categories = ReadStringList(command, "readinessCategories");
                bool personality = categories.Contains("personality_consistency", StringComparer.OrdinalIgnoreCase);
                bool factual = categories.Contains("detailed_factual_accuracy", StringComparer.OrdinalIgnoreCase)
                    || categories.Contains("world_local_knowledge", StringComparer.OrdinalIgnoreCase)
                    || categories.Contains("identity_role_authority", StringComparer.OrdinalIgnoreCase);
                bool group = categories.Contains("group_awareness", StringComparer.OrdinalIgnoreCase);
                bool relationshipHistory = categories.Contains(
                    "shared_relationship_history", StringComparer.OrdinalIgnoreCase);
                bool clanTier = categories.Contains("clan_tier_recognition", StringComparer.OrdinalIgnoreCase);
                bool manipulation = categories.Contains("manipulation_capabilities", StringComparer.OrdinalIgnoreCase);
                Dictionary<string, object> evaluation = batchEvaluations.TryGetValue(
                    commandId, out Dictionary<string, object> found)
                    ? found
                    : new Dictionary<string, object>
                    {
                        ["ok"] = false, ["commandId"] = commandId,
                        ["error"] = "The batched evaluator returned no cases for this command.",
                        ["cases"] = new List<object>()
                    };
                existing.Add(evaluation);
                evaluated++;

                List<Dictionary<string, object>> cases = ReadDictionaryList(evaluation, "cases");
                bool providerOk = ReadBool(evaluation, "ok", false);
                if (personality)
                    AppendReadinessRubricAssertion(assertions, failures, command, "personalityRubric",
                        providerOk && cases.Count > 0 && cases.All(row =>
                            ReadDouble(row, "personalityConsistency", 0d) >= 0.80d
                            && !ReadBool(row, "canonicalContradiction", false)),
                        evaluation, "Blinded personality consistency");
                if (factual)
                    AppendReadinessRubricAssertion(assertions, failures, command, "factualAccuracyRubric",
                        providerOk && cases.Count > 0 && cases.All(row =>
                            ReadDouble(row, "factualAccuracy", 0d) >= 0.80d
                            && !ReadBool(row, "canonicalContradiction", false)
                            && !ReadBool(row, "knowledgeLeak", false)
                            && !ReadBool(row, "wrongOwner", false)
                            && !ReadBool(row, "fabricatedCaughtLie", false)),
                        evaluation, "Blinded factual-grounding accuracy");
                if (group)
                    AppendReadinessRubricAssertion(assertions, failures, command, "groupAwarenessRubric",
                        providerOk && cases.Count > 0 && cases.All(row =>
                            ReadDouble(row, "groupAwareness", 0d) >= 0.80d),
                        evaluation, "Blinded group conversational awareness");
                if (relationshipHistory)
                    AppendReadinessRubricAssertion(assertions, failures, command,
                        "relationshipHistoryRubric",
                        providerOk && cases.Count > 0 && cases.All(row =>
                            ReadDouble(row, "relationshipHistoryQuality", 0d) >= 0.80d
                            && !ReadBool(row, "relationshipHistoryLeak", false)
                            && !ReadBool(row, "canonicalContradiction", false)),
                        evaluation, "Blinded shared relationship-history quality and privacy");
                if (clanTier)
                    AppendReadinessRubricAssertion(assertions, failures, command, "clanTierRecognitionRubric",
                        providerOk && cases.Count > 0 && cases.All(row =>
                            ReadDouble(row, "clanTierRecognition", 0d) >= 0.80d
                            && !ReadBool(row, "hiddenStatusLeak", false)),
                        evaluation, "Blinded identity-gated relative clan-tier recognition");
                if (manipulation)
                    AppendReadinessRubricAssertion(assertions, failures, command, "manipulationRubric",
                        providerOk && cases.Count > 0 && cases.All(row =>
                            ReadDouble(row, "manipulationCapability", 0d) >= 0.80d
                            && !ReadBool(row, "forcedManipulation", false)
                            && !ReadBool(row, "strategicAttractionPresentedAsGenuine", false)),
                        evaluation, "Blinded personality-grounded manipulation capability");
            }

            run["readinessEvaluations"] = existing;
            run["assertions"] = assertions;
            run["failures"] = failures;
            run["readinessEvaluatedUtc"] = DateTimeOffset.UtcNow.ToString("o");
            SaveLiveTestRun(run);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = runId,
                ["evaluatedCommandCount"] = evaluated,
                ["evaluationCount"] = existing.Count,
                ["failedRubricCount"] = failures.Count(row =>
                    ReadString(row, "assertion", "").EndsWith("Rubric", StringComparison.OrdinalIgnoreCase)),
                ["evaluations"] = existing
            };
        }

        private static Dictionary<string, Dictionary<string, object>> EvaluateConversationReadinessCommandBatch(
            string campaignId,
            Dictionary<string, object> run,
            List<Dictionary<string, object>> commands,
            List<Dictionary<string, object>> audit)
        {
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            Dictionary<string, string> commandByCase = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> expectedByCommand = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int globalOrdinal = 0;
            foreach (Dictionary<string, object> command in commands)
            {
                string commandId = ReadString(command, "commandId", "");
                List<string> categories = ReadStringList(command, "readinessCategories");
                bool personality = categories.Contains("personality_consistency", StringComparer.OrdinalIgnoreCase);
                bool factual = categories.Contains("detailed_factual_accuracy", StringComparer.OrdinalIgnoreCase)
                    || categories.Contains("world_local_knowledge", StringComparer.OrdinalIgnoreCase)
                    || categories.Contains("identity_role_authority", StringComparer.OrdinalIgnoreCase);
                bool group = categories.Contains("group_awareness", StringComparer.OrdinalIgnoreCase);
                bool relationshipHistory = categories.Contains(
                    "shared_relationship_history", StringComparer.OrdinalIgnoreCase);
                bool clanTier = categories.Contains("clan_tier_recognition", StringComparer.OrdinalIgnoreCase);
                bool manipulation = categories.Contains("manipulation_capabilities", StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> replies = ConversationReadinessEvaluationReplies(command);
                expectedByCommand[commandId] = replies.Count;
                int replyOrdinal = 0;
                foreach (Dictionary<string, object> reply in replies)
                {
                    replyOrdinal++;
                    globalOrdinal++;
                    string caseId = "C" + globalOrdinal;
                    Dictionary<string, object> raw = ReadDictionary(reply, "rawResponse") ?? reply;
                    string correlationId = FirstNonEmpty(
                        ReadString(raw, "correlationId", ""),
                        ReadString(reply, "correlationId", ""),
                        ReadStringList(command, "correlationIds").Skip(replyOrdinal - 1).FirstOrDefault());
                    Dictionary<string, object> promptRow = audit.LastOrDefault(row =>
                        ReadString(row, "phase", "").Equals("prompt.built", StringComparison.OrdinalIgnoreCase)
                        && ReadString(row, "correlationId", "").Equals(correlationId, StringComparison.OrdinalIgnoreCase));
                    Dictionary<string, object> contextRow = audit.LastOrDefault(row =>
                        ReadString(row, "phase", "").Equals("context.loaded", StringComparison.OrdinalIgnoreCase)
                        && ReadString(row, "correlationId", "").Equals(correlationId, StringComparison.OrdinalIgnoreCase));
                    string prompt = string.Join("\n", ReadDictionaryList(
                            ReadDictionary(promptRow, "data") ?? new Dictionary<string, object>(), "messages")
                        .Select(message => ReadString(message, "content", "")));
                    string foundation = ReadinessExtractSection(prompt,
                        "CHARACTER FOUNDATION - REUSABLE UNTIL THIS CHARACTER CHANGES",
                        "AUTHORITATIVE CURRENT ROLE ATTRIBUTION", 6500);
                    Dictionary<string, object> scene = ReadDictionary(raw, "conversationSceneState")
                        ?? new Dictionary<string, object>();
                    Dictionary<string, object> context = ReadDictionary(contextRow, "data")
                        ?? new Dictionary<string, object>();
                    cases.Add(new Dictionary<string, object>
                    {
                        ["caseId"] = caseId,
                        ["playerText"] = ReadString(command, "text", ""),
                        ["reply"] = FirstNonEmpty(ReadString(reply, "text", ""), ReadString(raw, "reply", "")),
                        ["characterCard"] = LimitText(foundation, 5500),
                        ["decisionEvidence"] = ReadDictionary(raw, "decisionBrief") ?? new Dictionary<string, object>(),
                        ["selectedContextPulls"] = ReadStringList(raw, "selectedContextPulls"),
                        ["contextBundleEvidence"] = LimitText(
                            Json.Serialize(ReadDictionaryList(raw, "contextBundles")), 5500),
                        ["identityEvidence"] = ReadDictionary(raw, "identityView") ?? new Dictionary<string, object>(),
                        ["motiveEvidence"] = ReadinessCompactMotiveEvidence(
                            ReadDictionary(raw, "motiveDecision") ?? new Dictionary<string, object>()),
                        ["sceneEvidence"] = ReadinessCompactSceneEvidence(scene, ReadDictionaryList(raw, "lines")),
                        ["retrievalEvidence"] = ReadinessCompactContextEvidence(context),
                        ["fixtureObjective"] = ReadString(
                            ReadDictionary(command, "assertions"),
                            "hiddenFixtureObjective", ""),
                        ["representedRequirementIds"] = ReadStringList(
                            ReadDictionary(command, "assertions"),
                            "representedRequirementIds"),
                        ["relationshipHistoryEvidence"] =
                            ReadinessCompactSharedRelationshipHistoryEvidence(
                                campaignId, raw, promptRow),
                        ["requestedRubrics"] = new Dictionary<string, object>
                        {
                            ["personality"] = personality, ["factual"] = factual, ["group"] = group,
                            ["relationshipHistory"] = relationshipHistory,
                            ["clanTierRecognition"] = clanTier, ["manipulation"] = manipulation
                        }
                    });
                    commandByCase[caseId] = commandId;
                }
            }
            if (cases.Count == 0)
                return new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

            // Stable rotation prevents the evaluator from inheriting production response order while
            // preserving deterministic replay for the same qualification run.
            int rotation = ReadString(run, "runId", "")
                .Select(character => (int)character).Aggregate(0, (sum, value) => (sum + value) % cases.Count);
            List<Dictionary<string, object>> blinded = cases.Skip(rotation).Concat(cases.Take(rotation)).ToList();
            List<Dictionary<string, object>> judged = new List<Dictionary<string, object>>();
            bool allProviderCallsOk = true;
            long providerDurationMs = 0;
            long requestChars = 0;
            int providerCallCount = 0;
            int individualRecoveryCallCount = 0;
            int calibrationReviewCallCount = 0;
            int schemaRepairCallCount = 0;
            int initialSchemaViolationCount = 0;
            string providerModel = "";
            string finishReason = "";
            string providerError = "";
            string budgetCorrelation =
                ConversationReadinessBudgetCorrelation(run);
            HashSet<string> expectedCaseIds = new HashSet<string>(
                blinded.Select(row => ReadString(row, "caseId", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            for (int start = 0; start < blinded.Count; start += 5)
            {
                List<Dictionary<string, object>> batch = blinded.Skip(start).Take(5).ToList();
                Dictionary<string, object> llm = CallConversationReadinessJudge(
                    campaignId,
                    batch,
                    string.IsNullOrWhiteSpace(budgetCorrelation)
                        ? string.Empty
                        : budgetCorrelation + "-judge-" + (start / 5));
                providerCallCount++;
                allProviderCallsOk &= ReadBool(llm, "ok", false);
                providerDurationMs += ReadLong(llm, "durationMs", 0);
                requestChars += ReadLong(llm, "requestChars", 0);
                providerModel = FirstNonEmpty(providerModel, ReadString(llm, "model", ""));
                finishReason = ReadString(llm, "finishReason", finishReason);
                providerError = FirstNonEmpty(providerError, ReadString(llm, "error", ""));
                Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
                List<Dictionary<string, object>> batchJudged = parsed == null
                    ? new List<Dictionary<string, object>>()
                    : ReadDictionaryList(parsed, "cases");
                MergeConversationReadinessJudgeCases(judged, batchJudged, expectedCaseIds);
            }

            Dictionary<string, Dictionary<string, object>> expectedCasesById = blinded
                .Where(row => !string.IsNullOrWhiteSpace(ReadString(row, "caseId", "")))
                .GroupBy(row => ReadString(row, "caseId", ""), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> initiallyInvalid = judged.Where(row =>
            {
                string caseId = ReadString(row, "caseId", "");
                return !expectedCasesById.TryGetValue(caseId, out Dictionary<string, object> expected)
                    || ConversationReadinessJudgeCaseSchemaViolations(row, expected).Count > 0;
            }).ToList();
            initialSchemaViolationCount = initiallyInvalid.Count;
            foreach (Dictionary<string, object> invalid in initiallyInvalid)
                judged.Remove(invalid);

            // Providers sometimes return a valid JSON object whose score fields
            // contain explanatory prose, or omit one case from a multi-case
            // response. Preserve valid siblings and recover only missing or
            // schema-invalid case IDs with small, individually bounded calls.
            // Invalid values are never silently coerced to zero.
            foreach (Dictionary<string, object> missing in MissingConversationReadinessJudgeCases(blinded, judged))
            {
                Dictionary<string, object> llm = CallConversationReadinessJudge(
                    campaignId,
                    new List<Dictionary<string, object>> { missing },
                    string.IsNullOrWhiteSpace(budgetCorrelation)
                        ? string.Empty
                        : budgetCorrelation + "-judge-recovery-"
                            + individualRecoveryCallCount);
                providerCallCount++;
                individualRecoveryCallCount++;
                allProviderCallsOk &= ReadBool(llm, "ok", false);
                providerDurationMs += ReadLong(llm, "durationMs", 0);
                requestChars += ReadLong(llm, "requestChars", 0);
                providerModel = FirstNonEmpty(providerModel, ReadString(llm, "model", ""));
                finishReason = ReadString(llm, "finishReason", finishReason);
                providerError = FirstNonEmpty(providerError, ReadString(llm, "error", ""));
                Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
                List<Dictionary<string, object>> recovered = parsed == null
                    ? new List<Dictionary<string, object>>()
                    : ReadDictionaryList(parsed, "cases");
                MergeConversationReadinessJudgeCases(judged, recovered, expectedCaseIds);
            }

            // A score just below the per-reply gate is not useful evidence when
            // its matching reason identifies no defect and instead says the
            // requested behavior was correct. Give every schema-valid
            // borderline requested rubric one independent, bounded calibration
            // review. The model can keep a genuine failure below 0.80, but must
            // then name the concrete deficiency. No numeric score is raised by
            // deterministic code.
            foreach (Dictionary<string, object> expected in blinded)
            {
                string caseId = ReadString(expected, "caseId", "");
                Dictionary<string, object> found = judged.LastOrDefault(row =>
                    ReadString(row, "caseId", "").Equals(
                        caseId, StringComparison.OrdinalIgnoreCase));
                if (found == null
                    || ConversationReadinessJudgeCaseSchemaViolations(
                        found, expected).Count > 0)
                    continue;
                List<string> borderlineRubrics =
                    ConversationReadinessBorderlineRequestedRubrics(
                        found, expected);
                if (borderlineRubrics.Count == 0) continue;
                Dictionary<string, object> llm =
                    CallConversationReadinessJudgeCalibrationReview(
                        campaignId, expected, found, borderlineRubrics,
                        string.IsNullOrWhiteSpace(budgetCorrelation)
                            ? string.Empty
                            : budgetCorrelation + "-judge-calibration-"
                                + calibrationReviewCallCount);
                providerCallCount++;
                calibrationReviewCallCount++;
                allProviderCallsOk &= ReadBool(llm, "ok", false);
                providerDurationMs += ReadLong(llm, "durationMs", 0);
                requestChars += ReadLong(llm, "requestChars", 0);
                providerModel = FirstNonEmpty(
                    providerModel, ReadString(llm, "model", ""));
                finishReason = ReadString(
                    llm, "finishReason", finishReason);
                providerError = FirstNonEmpty(
                    providerError, ReadString(llm, "error", ""));
                Dictionary<string, object> parsed =
                    TryParseJsonObject(ReadString(llm, "content", ""));
                List<Dictionary<string, object>> reviewed =
                    parsed == null
                        ? new List<Dictionary<string, object>>()
                        : ReadDictionaryList(parsed, "cases");
                MergeConversationReadinessJudgeCases(
                    judged, reviewed, expectedCaseIds);
            }

            // A provider can repeat the same type swap even in the individually
            // bounded recovery call (for example, prose in groupAwareness and
            // no groupReason). Give only still-invalid cases a stricter,
            // schema-repair pass. This remains evidence-grounded: the full
            // blinded case is supplied again, and no invalid score is coerced
            // or assigned by deterministic code.
            for (int repairPass = 0; repairPass < 2; repairPass++)
            {
                List<Tuple<Dictionary<string, object>, Dictionary<string, object>, List<string>>>
                    repairCases = new List<Tuple<Dictionary<string, object>,
                        Dictionary<string, object>, List<string>>>();
                foreach (Dictionary<string, object> expected in blinded)
                {
                    string caseId = ReadString(expected, "caseId", "");
                    Dictionary<string, object> found = judged.LastOrDefault(row =>
                        ReadString(row, "caseId", "").Equals(
                            caseId, StringComparison.OrdinalIgnoreCase));
                    List<string> violations = found == null
                        ? new List<string> { "case_missing" }
                        : ConversationReadinessJudgeCaseSchemaViolations(
                            found, expected);
                    if (violations.Count > 0)
                        repairCases.Add(Tuple.Create(
                            expected,
                            found ?? new Dictionary<string, object>
                            {
                                ["caseId"] = caseId
                            },
                            violations));
                }
                if (repairCases.Count == 0) break;
                foreach (Tuple<Dictionary<string, object>,
                    Dictionary<string, object>, List<string>> repair
                    in repairCases)
                {
                    Dictionary<string, object> llm =
                        CallConversationReadinessJudgeSchemaRepair(
                            campaignId, repair.Item1, repair.Item2,
                            repair.Item3,
                            string.IsNullOrWhiteSpace(budgetCorrelation)
                                ? string.Empty
                                : budgetCorrelation + "-judge-schema-"
                                    + schemaRepairCallCount);
                    providerCallCount++;
                    schemaRepairCallCount++;
                    allProviderCallsOk &= ReadBool(llm, "ok", false);
                    providerDurationMs += ReadLong(llm, "durationMs", 0);
                    requestChars += ReadLong(llm, "requestChars", 0);
                    providerModel = FirstNonEmpty(
                        providerModel, ReadString(llm, "model", ""));
                    finishReason = ReadString(
                        llm, "finishReason", finishReason);
                    providerError = FirstNonEmpty(
                        providerError, ReadString(llm, "error", ""));
                    Dictionary<string, object> parsed =
                        TryParseJsonObject(ReadString(llm, "content", ""));
                    List<Dictionary<string, object>> recovered =
                        parsed == null
                            ? new List<Dictionary<string, object>>()
                            : ReadDictionaryList(parsed, "cases");
                    MergeConversationReadinessJudgeCases(
                        judged, recovered, expectedCaseIds);
                }
            }
            Dictionary<string, List<string>> unresolvedSchemaViolations = new Dictionary<string, List<string>>(
                StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> expected in blinded)
            {
                string caseId = ReadString(expected, "caseId", "");
                Dictionary<string, object> found = judged.LastOrDefault(row =>
                    ReadString(row, "caseId", "").Equals(caseId, StringComparison.OrdinalIgnoreCase));
                List<string> violations = found == null
                    ? new List<string> { "case_missing" }
                    : ConversationReadinessJudgeCaseSchemaViolations(found, expected);
                if (violations.Count > 0)
                    unresolvedSchemaViolations[caseId] = violations;
            }
            foreach (Dictionary<string, object> row in judged)
            {
                row["personalityConsistency"] = ClampDouble(ReadDouble(row, "personalityConsistency", 0d), 0d, 1d);
                row["factualAccuracy"] = ClampDouble(ReadDouble(row, "factualAccuracy", 0d), 0d, 1d);
                row["groupAwareness"] = ClampDouble(ReadDouble(row, "groupAwareness", 0d), 0d, 1d);
                row["relationshipHistoryQuality"] = ClampDouble(
                    ReadDouble(row, "relationshipHistoryQuality", 0d), 0d, 1d);
                row["clanTierRecognition"] = ClampDouble(ReadDouble(row, "clanTierRecognition", 0d), 0d, 1d);
                row["manipulationCapability"] = ClampDouble(ReadDouble(row, "manipulationCapability", 0d), 0d, 1d);
            }
            Dictionary<string, Dictionary<string, object>> results =
                new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> command in commands)
            {
                string commandId = ReadString(command, "commandId", "");
                List<Dictionary<string, object>> commandCases = judged.Where(row =>
                    commandByCase.TryGetValue(ReadString(row, "caseId", ""), out string owner)
                    && owner.Equals(commandId, StringComparison.OrdinalIgnoreCase)).ToList();
                bool expectedKnown = expectedByCommand.TryGetValue(
                    commandId, out int expectedCaseCount);
                bool commandSchemaValid = commandCases.All(row =>
                    !unresolvedSchemaViolations.ContainsKey(
                        ReadString(row, "caseId", "")));
                results[commandId] = new Dictionary<string, object>
                {
                    ["ok"] = expectedKnown
                        && commandSchemaValid
                        && commandCases.Count == expectedCaseCount,
                    ["commandId"] = commandId,
                    ["batchCaseCount"] = cases.Count,
                    ["providerCallCount"] = providerCallCount,
                    ["individualRecoveryCallCount"] = individualRecoveryCallCount,
                    ["calibrationReviewCallCount"] = calibrationReviewCallCount,
                    ["schemaRepairCallCount"] = schemaRepairCallCount,
                    ["initialSchemaViolationCount"] = initialSchemaViolationCount,
                    ["unresolvedSchemaViolations"] = unresolvedSchemaViolations,
                    ["allProviderCallsOk"] = allProviderCallsOk,
                    ["providerModel"] = providerModel,
                    ["providerDurationMs"] = providerDurationMs,
                    ["requestChars"] = requestChars,
                    ["finishReason"] = finishReason,
                    ["cases"] = commandCases,
                    ["error"] = expectedKnown
                        && commandSchemaValid
                        && commandCases.Count == expectedCaseCount
                            ? ""
                            : FirstNonEmpty(
                                providerError,
                                "One or more evaluator cases remained missing or schema-invalid after targeted recovery.")
                };
            }
            return results;
        }

        private static List<string> ConversationReadinessJudgeCaseSchemaViolations(
            Dictionary<string, object> judged,
            Dictionary<string, object> expected)
        {
            List<string> violations = new List<string>();
            judged = judged ?? new Dictionary<string, object>();
            expected = expected ?? new Dictionary<string, object>();
            NormalizeUnrequestedConversationReadinessJudgeFields(
                judged, expected);
            string expectedCaseId = ReadString(expected, "caseId", "");
            string actualCaseId = ReadString(judged, "caseId", "");
            if (string.IsNullOrWhiteSpace(actualCaseId)
                || !actualCaseId.Equals(expectedCaseId, StringComparison.OrdinalIgnoreCase))
                violations.Add("caseId");

            string[] scoreFields =
            {
                "personalityConsistency", "factualAccuracy", "groupAwareness",
                "relationshipHistoryQuality", "clanTierRecognition", "manipulationCapability"
            };
            foreach (string field in scoreFields)
            {
                if (!TryReadStrictJsonScore(judged, field, out double value)
                    || value < 0d || value > 1d)
                    violations.Add(field + ":expected_json_number_0_to_1");
            }

            string[] booleanFields =
            {
                "canonicalContradiction", "knowledgeLeak", "wrongOwner",
                "fabricatedCaughtLie", "relationshipHistoryLeak", "hiddenStatusLeak",
                "forcedManipulation", "strategicAttractionPresentedAsGenuine"
            };
            foreach (string field in booleanFields)
            {
                if (!judged.TryGetValue(field, out object value) || !(value is bool))
                    violations.Add(field + ":expected_json_boolean");
            }

            string[] reasonFields =
            {
                "personalityReason", "factualReason", "groupReason",
                "relationshipHistoryReason", "clanTierReason", "manipulationReason"
            };
            foreach (string field in reasonFields)
            {
                if (!judged.TryGetValue(field, out object value) || !(value is string))
                    violations.Add(field + ":expected_json_string");
            }

            if (!judged.TryGetValue("evidenceQuotes", out object quotes)
                || quotes == null
                || quotes is string
                || !(quotes is IEnumerable))
                violations.Add("evidenceQuotes:expected_json_array");
            return violations;
        }

        private static void
            NormalizeUnrequestedConversationReadinessJudgeFields(
                Dictionary<string, object> judged,
                Dictionary<string, object> expected)
        {
            Dictionary<string, object> requested =
                ReadDictionary(expected, "requestedRubrics");
            if (judged == null || requested == null
                || requested.Count == 0)
                return;
            Dictionary<string, Tuple<string, string>> fields =
                new Dictionary<string, Tuple<string, string>>(
                    StringComparer.OrdinalIgnoreCase)
                {
                    ["personality"] = Tuple.Create(
                        "personalityConsistency", "personalityReason"),
                    ["factual"] = Tuple.Create(
                        "factualAccuracy", "factualReason"),
                    ["group"] = Tuple.Create(
                        "groupAwareness", "groupReason"),
                    ["relationshipHistory"] = Tuple.Create(
                        "relationshipHistoryQuality",
                        "relationshipHistoryReason"),
                    ["clanTierRecognition"] = Tuple.Create(
                        "clanTierRecognition", "clanTierReason"),
                    ["manipulation"] = Tuple.Create(
                        "manipulationCapability", "manipulationReason")
                };
            foreach (KeyValuePair<string, Tuple<string, string>> field
                in fields)
            {
                if (ReadBool(requested, field.Key, false)) continue;
                judged[field.Value.Item1] = 1d;
                judged[field.Value.Item2] = "Not requested.";
            }
        }

        private static bool TryReadStrictJsonScore(
            Dictionary<string, object> row, string key, out double value)
        {
            value = 0d;
            if (row == null || !row.TryGetValue(key, out object raw)
                || raw == null || raw is bool || raw is string)
                return false;
            TypeCode code = Type.GetTypeCode(raw.GetType());
            if (code != TypeCode.Byte && code != TypeCode.SByte
                && code != TypeCode.Int16 && code != TypeCode.UInt16
                && code != TypeCode.Int32 && code != TypeCode.UInt32
                && code != TypeCode.Int64 && code != TypeCode.UInt64
                && code != TypeCode.Single && code != TypeCode.Double
                && code != TypeCode.Decimal)
                return false;
            try
            {
                value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }
            catch
            {
                value = 0d;
                return false;
            }
        }

        private static void MergeConversationReadinessJudgeCases(
            List<Dictionary<string, object>> destination,
            IEnumerable<Dictionary<string, object>> incoming,
            HashSet<string> expectedCaseIds)
        {
            if (destination == null) return;
            expectedCaseIds = expectedCaseIds
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Dictionary<string, object> row in incoming
                ?? Enumerable.Empty<Dictionary<string, object>>())
            {
                string caseId = ReadString(row, "caseId", "");
                if (string.IsNullOrWhiteSpace(caseId)
                    || !expectedCaseIds.Contains(caseId))
                    continue;
                int existing = destination.FindIndex(item =>
                    ReadString(item, "caseId", "")
                        .Equals(caseId, StringComparison.OrdinalIgnoreCase));
                if (existing >= 0) destination[existing] = row;
                else destination.Add(row);
            }
        }

        private static List<Dictionary<string, object>> MissingConversationReadinessJudgeCases(
            IEnumerable<Dictionary<string, object>> expected,
            IEnumerable<Dictionary<string, object>> judged)
        {
            HashSet<string> present = new HashSet<string>(
                (judged ?? Enumerable.Empty<Dictionary<string, object>>())
                    .Select(row => ReadString(row, "caseId", ""))
                    .Where(value => !string.IsNullOrWhiteSpace(value)),
                StringComparer.OrdinalIgnoreCase);
            return (expected ?? Enumerable.Empty<Dictionary<string, object>>())
                .Where(row =>
                {
                    string caseId = ReadString(row, "caseId", "");
                    return !string.IsNullOrWhiteSpace(caseId)
                        && !present.Contains(caseId);
                }).ToList();
        }

        private static Dictionary<string, object> CallConversationReadinessJudge(
            string campaignId,
            List<Dictionary<string, object>> blinded,
            string correlationId = "")
        {
            return ChatWithLlm(new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId ?? string.Empty,
                ["requestType"] = "readiness_evaluation",
                ["model"] = ReadString(LoadSettings(), "dialogueModel", ""),
                ["temperature"] = 0d,
                ["maxTokens"] = ConversationReadinessJudgeMaxTokens(blinded.Count),
                ["reasoningEffort"] = "none",
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" },
                ["messages"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "You are an independent blinded dialogue qualification judge. Character names and case order are not evaluation signals. "
                            + "Use only each case's supplied character card, authoritative context, attributed retrieval evidence, player text, and reply. "
                            + "When fixtureObjective and representedRequirementIds are supplied, judge whether the natural player turn and reply actually demonstrate those mapped behaviors; never award coverage merely because an ID was attached. "
                            + "Do not reward style alone. Score personality consistency, factual accuracy, group awareness, shared relationship-history quality, clan-tier recognition, and manipulation capability from 0.00 to 1.00. "
                            + "Shared relationship-history quality requires modest plausible objective conduct, behavior compatible with the speaker's own private interpretation, and no use of the other character's forbidden private interpretation unless independently established by visible evidence. "
                            + "Group awareness requires later speakers to engage relevant earlier contributions while preserving distinct agency. Penalize repeated questions or concerns, recitation of a player's nonanswer, convergence on an unsupported shared invention, copied conclusions, and recycled gestures or formulas. Legitimate agreement still needs the later NPC's own motive, knowledge, qualification, or consequence. "
                            + "Clan-tier recognition means the reply responds appropriately to the supplied relative public standing only when identity evidence permits it; dismissal, caution, parity, deference, rivalry, patronage, or calculated courtship may all be correct depending on the character. "
                            + ConversationReadinessClanTierRubricRule
                            + ConversationReadinessIdentityRecognitionRubricRule
                            + "For manipulation, treat the supplied Court Character cell as the fixed Honor-Boldness posture. Low honor supports opportunism but never forces a pointless or irrational scheme; Boldness changes directness and risk rather than morality. An active tactic must belong to that cell. "
                            + "When economic capacity is hidden, dialogue must not know or imply the hidden wallet amount. Demonstrated wealth, visible presentation, public clan backing, rank, and witnessed transactions remain usable evidence. "
                            + "Manipulation capability means the NPC uses—or deliberately does not use—flattery, selective disclosure, bargaining, seduction, obligation, pressure, or other influence consistently with supplied ambition, motives, scruples, relationship, opportunity, and exposure. Do not reward manipulation when the character evidence supports honesty or restraint. "
                            + "Calculated romantic strategy must not be misreported as genuine attraction, and rank alone never mandates seduction. "
                            + "A proud or status-conscious noble may dismiss a known low-tier petitioner who offers little value, while a lower-ranked noble may respond to a more powerful known player with caution, deference, rivalry, patronage, or opportunism. Rank is one influence rather than a fixed script, and visible presentation cannot reveal an unknown true clan tier. "
                            + "When deterministic evidence recommends manipulation, merely discussing an opportunity without actually attempting a fitting form of influence is insufficient. "
                            + "A plausible current-world claim without supplied evidence is unsupported. Soft personal history is allowed only when compatible and presented as the speaker's own history. "
                            + "Set canonicalContradiction, knowledgeLeak, wrongOwner, fabricatedCaughtLie, relationshipHistoryLeak, hiddenStatusLeak, forcedManipulation, or strategicAttractionPresentedAsGenuine only when the supplied evidence demonstrates it. "
                            + "SCORE CALIBRATION: 1.0 means the requested behavior is fully and strongly demonstrated; 0.80 means it is adequately demonstrated with no material defect. A requested rubric may score below 0.80 only when its matching *Reason names a concrete, evidence-grounded deficiency in the reply. If the reason says the reply correctly satisfies the requested behavior and identifies no material defect, that score must be at least 0.80. Do not raise a score merely to make it pass. "
                            + "SCHEMA TYPE RULE: every field whose example value is 0.0 must contain a JSON number from 0.0 through 1.0, never prose. Put all explanations only in the matching *Reason string. Every true/false field must contain a JSON boolean, never a quoted string. Include every property for every case. "
                            + "Keep each *Reason under 350 characters. Return at most three evidenceQuotes per case, each under 180 characters. "
                            + "Return JSON exactly as {\"cases\":[{\"caseId\":\"C1\",\"personalityConsistency\":0.0,\"factualAccuracy\":0.0,\"groupAwareness\":0.0,\"relationshipHistoryQuality\":0.0,\"clanTierRecognition\":0.0,\"manipulationCapability\":0.0,"
                            + "\"canonicalContradiction\":false,\"knowledgeLeak\":false,\"wrongOwner\":false,\"fabricatedCaughtLie\":false,"
                            + "\"relationshipHistoryLeak\":false,\"hiddenStatusLeak\":false,\"forcedManipulation\":false,\"strategicAttractionPresentedAsGenuine\":false,"
                            + "\"personalityReason\":\"\",\"factualReason\":\"\",\"groupReason\":\"\",\"relationshipHistoryReason\":\"\",\"clanTierReason\":\"\",\"manipulationReason\":\"\",\"evidenceQuotes\":[]}]}."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = Json.Serialize(new Dictionary<string, object>
                        {
                            ["cases"] = blinded,
                            ["instructions"] = "Judge every case independently. For an unrequested rubric, return 1.0 and state not requested."
                        })
                    }
                }
            });
        }

        private static string ConversationReadinessBudgetCorrelation(
            Dictionary<string, object> run)
        {
            run = run ?? new Dictionary<string, object>();
            return FirstNonEmpty(
                ReadString(run, "gauntletCorrelationId", ""),
                ReadString(run, "correlationId", ""));
        }

        private static Dictionary<string, object>
            CallConversationReadinessJudgeSchemaRepair(
                string campaignId,
                Dictionary<string, object> blindedCase,
                Dictionary<string, object> invalidOutput,
                List<string> violations,
                string correlationId = "")
        {
            return ChatWithLlm(
                BuildConversationReadinessJudgeSchemaRepairRequest(
                    campaignId, blindedCase, invalidOutput, violations,
                    correlationId));
        }

        private static Dictionary<string, object>
            BuildConversationReadinessJudgeSchemaRepairRequest(
                string campaignId,
                Dictionary<string, object> blindedCase,
                Dictionary<string, object> invalidOutput,
                List<string> violations,
                string correlationId = "")
        {
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId ?? string.Empty,
                ["requestType"] = "readiness_evaluation",
                ["model"] = ReadString(
                    LoadSettings(), "dialogueModel", ""),
                ["temperature"] = 0d,
                ["maxTokens"] = 3200,
                ["reasoningEffort"] = "none",
                ["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_object"
                },
                ["messages"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "Repair and reissue exactly one blinded dialogue-judge case. "
                            + "Use the supplied blinded case as the evidence source and preserve the prior judgment's meaning where it is valid. "
                            + "The prior output failed the listed schema checks; do not copy a malformed value. "
                            + ConversationReadinessIdentityRecognitionRubricRule
                            + "Every score field must be a JSON number from 0.0 through 1.0: personalityConsistency, factualAccuracy, groupAwareness, relationshipHistoryQuality, clanTierRecognition, manipulationCapability. "
                            + "Every *Reason field must be a JSON string. Every safety flag must be a JSON boolean. evidenceQuotes must be a JSON array of strings. "
                            + "If prose was placed in a score field, move that prose into the matching *Reason and independently provide the numeric score supported by the case evidence. "
                            + "For an unrequested rubric, use numeric 1.0 and the exact reason Not requested. "
                            + "Return one case only, with the exact supplied caseId and every required property. "
                            + "Return JSON exactly as {\"cases\":[{\"caseId\":\"C1\",\"personalityConsistency\":0.0,\"factualAccuracy\":0.0,\"groupAwareness\":0.0,\"relationshipHistoryQuality\":0.0,\"clanTierRecognition\":0.0,\"manipulationCapability\":0.0,"
                            + "\"canonicalContradiction\":false,\"knowledgeLeak\":false,\"wrongOwner\":false,\"fabricatedCaughtLie\":false,"
                            + "\"relationshipHistoryLeak\":false,\"hiddenStatusLeak\":false,\"forcedManipulation\":false,\"strategicAttractionPresentedAsGenuine\":false,"
                            + "\"personalityReason\":\"\",\"factualReason\":\"\",\"groupReason\":\"\",\"relationshipHistoryReason\":\"\",\"clanTierReason\":\"\",\"manipulationReason\":\"\",\"evidenceQuotes\":[]}]}."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = Json.Serialize(
                            new Dictionary<string, object>
                            {
                                ["case"] = blindedCase
                                    ?? new Dictionary<string, object>(),
                                ["invalidPriorOutput"] = invalidOutput
                                    ?? new Dictionary<string, object>(),
                                ["schemaViolations"] = violations
                                    ?? new List<string>(),
                                ["instructions"] =
                                    "Rejudge only this case and return the complete corrected typed schema."
                            })
                    }
                }
            };
        }

        private static List<string>
            ConversationReadinessBorderlineRequestedRubrics(
                Dictionary<string, object> judged,
                Dictionary<string, object> expected)
        {
            Dictionary<string, object> requested =
                ReadDictionary(expected, "requestedRubrics")
                ?? new Dictionary<string, object>();
            List<Tuple<string, string>> rubricScores =
                new List<Tuple<string, string>>
                {
                    Tuple.Create("personality", "personalityConsistency"),
                    Tuple.Create("factual", "factualAccuracy"),
                    Tuple.Create("group", "groupAwareness"),
                    Tuple.Create(
                        "relationshipHistory",
                        "relationshipHistoryQuality"),
                    Tuple.Create(
                        "clanTierRecognition",
                        "clanTierRecognition"),
                    Tuple.Create(
                        "manipulation",
                        "manipulationCapability")
                };
            List<string> borderline = new List<string>();
            foreach (Tuple<string, string> rubric in rubricScores)
            {
                if (!ReadBool(requested, rubric.Item1, false)) continue;
                double score = ReadDouble(judged, rubric.Item2, 0d);
                if (score >= 0.70d && score < 0.80d)
                    borderline.Add(rubric.Item2);
            }
            return borderline;
        }

        private static Dictionary<string, object>
            CallConversationReadinessJudgeCalibrationReview(
                string campaignId,
                Dictionary<string, object> blindedCase,
                Dictionary<string, object> priorJudgment,
                List<string> borderlineRubrics,
                string correlationId = "")
        {
            return ChatWithLlm(
                BuildConversationReadinessJudgeCalibrationRequest(
                    campaignId,
                    blindedCase,
                    priorJudgment,
                    borderlineRubrics,
                    correlationId));
        }

        private static Dictionary<string, object>
            BuildConversationReadinessJudgeCalibrationRequest(
                string campaignId,
                Dictionary<string, object> blindedCase,
                Dictionary<string, object> priorJudgment,
                List<string> borderlineRubrics,
                string correlationId = "")
        {
            return new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["correlationId"] = correlationId ?? string.Empty,
                ["requestType"] = "readiness_evaluation",
                ["model"] = ReadString(
                    LoadSettings(), "dialogueModel", ""),
                ["temperature"] = 0d,
                ["maxTokens"] = 3200,
                ["reasoningEffort"] = "none",
                ["response_format"] = new Dictionary<string, object>
                {
                    ["type"] = "json_object"
                },
                ["messages"] = new ArrayList
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "Independently review exactly one borderline blinded dialogue judgment using only the supplied case evidence. "
                            + "The readiness pass boundary is 0.80. A score of 1.0 means the requested behavior is fully and strongly demonstrated; 0.80 means it is adequately demonstrated with no material defect. "
                            + "For every listed borderline rubric, keep the score below 0.80 only when the matching *Reason names a concrete, evidence-grounded deficiency in the reply. "
                            + ConversationReadinessClanTierRubricRule
                            + ConversationReadinessIdentityRecognitionRubricRule
                            + "If the matching reason instead says the reply correctly satisfies the requested behavior and identifies no material defect, the score must be at least 0.80. "
                            + "Do not increase a score merely to make it pass, do not infer missing evidence, and do not remove a demonstrated safety flag. "
                            + "Preserve non-borderline judgments unless the supplied case proves a clear error. "
                            + "Every score must be a JSON number from 0.0 through 1.0, every *Reason a string, every safety flag a boolean, and evidenceQuotes an array of strings. "
                            + "For an unrequested rubric, use numeric 1.0 and the exact reason Not requested. "
                            + "Return one case only, with the exact supplied caseId and every required property. "
                            + "Return JSON exactly as {\"cases\":[{\"caseId\":\"C1\",\"personalityConsistency\":0.0,\"factualAccuracy\":0.0,\"groupAwareness\":0.0,\"relationshipHistoryQuality\":0.0,\"clanTierRecognition\":0.0,\"manipulationCapability\":0.0,"
                            + "\"canonicalContradiction\":false,\"knowledgeLeak\":false,\"wrongOwner\":false,\"fabricatedCaughtLie\":false,"
                            + "\"relationshipHistoryLeak\":false,\"hiddenStatusLeak\":false,\"forcedManipulation\":false,\"strategicAttractionPresentedAsGenuine\":false,"
                            + "\"personalityReason\":\"\",\"factualReason\":\"\",\"groupReason\":\"\",\"relationshipHistoryReason\":\"\",\"clanTierReason\":\"\",\"manipulationReason\":\"\",\"evidenceQuotes\":[]}]}."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = Json.Serialize(
                            new Dictionary<string, object>
                            {
                                ["case"] = blindedCase
                                    ?? new Dictionary<string, object>(),
                                ["priorJudgment"] = priorJudgment
                                    ?? new Dictionary<string, object>(),
                                ["borderlineRubrics"] = borderlineRubrics
                                    ?? new List<string>(),
                                ["instructions"] =
                                    "Review the listed borderline scores against the explicit 0.80 calibration rule and return the complete typed case."
                            })
                    }
                }
            };
        }

        private static int ConversationReadinessJudgeMaxTokens(int caseCount)
        {
            // A four-case blinded batch previously reached a 2,900-token cap
            // while returning otherwise useful judgments. Leave enough bounded
            // room for the complete typed schema so predictable truncation does
            // not fan out into one provider recovery call per reply.
            int boundedCases = Math.Max(1, Math.Min(5, caseCount));
            return Math.Max(2600, Math.Min(10000, 1200 + boundedCases * 1400));
        }

        private static List<Dictionary<string, object>> ConversationReadinessEvaluationReplies(
            Dictionary<string, object> command)
        {
            Dictionary<string, object> result = ReadDictionary(command, "result") ?? new Dictionary<string, object>();
            List<Dictionary<string, object>> replies = ReadDictionaryList(result, "replies");
            if (replies.Count > 0) return replies;
            return new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["text"] = FirstNonEmpty(ReadString(result, "text", ""), ReadString(result, "reply", "")),
                    ["correlationId"] = ReadString(result, "correlationId", ""),
                    ["rawResponse"] = ReadDictionary(result, "rawResponse") ?? result
                }
            };
        }

        private static Dictionary<string, object> ReadinessCompactContextEvidence(
            Dictionary<string, object> context)
        {
            return new Dictionary<string, object>
            {
                ["memorySummary"] = LimitText(ReadString(context, "memorySummary", ""), 2200),
                ["memories"] = ReadDictionaryList(context, "memories").Take(6).Select(row =>
                    new Dictionary<string, object>
                    {
                        ["id"] = ReadFirstString(row, "id", "memory_id", "event_id"),
                        ["text"] = LimitText(ReadFirstString(row, "text", "summary", "content"), 650),
                        ["source"] = ReadFirstString(row, "source", "source_event_id", "source_session_id")
                    }).ToList(),
                ["priorLines"] = ReadDictionaryList(context, "priorLines").Take(8).Select(row =>
                    new Dictionary<string, object>
                    {
                        ["id"] = ReadFirstString(row, "id", "turn_id"),
                        ["speaker"] = ReadFirstString(row, "speaker", "speaker_id"),
                        ["text"] = LimitText(ReadString(row, "text", ""), 650)
                    }).ToList(),
                ["eventLines"] = ReadDictionaryList(context, "eventLines")
                    .Skip(Math.Max(0, ReadDictionaryList(context, "eventLines").Count - 10))
                    .Select(row => new Dictionary<string, object>
                    {
                        ["id"] = ReadFirstString(row, "turnId", "turn_id", "id"),
                        ["exchangeId"] = ReadFirstString(row, "exchangeId", "exchange_id"),
                        ["speakerId"] = ReadFirstString(row, "speakerHeroStringId", "speaker_id", "heroStringId"),
                        ["speaker"] = ReadString(row, "speaker", ""),
                        ["role"] = ReadString(row, "role", ""),
                        ["text"] = LimitText(ReadString(row, "text", ""), 800)
                    }).ToList(),
                ["sourceSummaryIds"] = ReadStringList(context, "sourceSummaryIds").Take(12).ToList(),
                ["matchedTurnIds"] = ReadStringList(context, "matchedTurnIds").Take(20).ToList(),
                ["expandedTurnIds"] = ReadStringList(context, "expandedTurnIds").Take(20).ToList()
            };
        }

        private static Dictionary<string, object> ReadinessCompactSharedRelationshipHistoryEvidence(
            string campaignId,
            Dictionary<string, object> rawReply,
            Dictionary<string, object> promptRow)
        {
            Dictionary<string, object> promptData =
                ReadDictionary(promptRow, "data") ?? new Dictionary<string, object>();
            Dictionary<string, object> envelope =
                ReadDictionary(promptData, "promptEnvelope") ?? new Dictionary<string, object>();
            Dictionary<string, object> relationshipPrompt =
                ReadDictionary(envelope, "npcRelationshipPrompt")
                ?? new Dictionary<string, object>();
            string speakerId = ReadFirstString(
                rawReply ?? new Dictionary<string, object>(),
                "heroStringId", "speakerHeroStringId", "heroId");
            List<Dictionary<string, object>> chapters = new List<Dictionary<string, object>>();
            try
            {
                using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                {
                    EnsureSharedRelationshipHistorySchema(connection);
                    foreach (Dictionary<string, object> target in
                        ReadDictionaryList(relationshipPrompt, "targets"))
                    {
                        string targetId = ReadString(target, "targetHeroStringId", "");
                        foreach (string historyId in ReadStringList(
                            target, "sharedRelationshipHistoryIds"))
                        {
                            Dictionary<string, object> row = QuerySql(connection,
                                "SELECT * FROM shared_relationship_history_chapters WHERE history_id=$id LIMIT 1;",
                                new Dictionary<string, object> { ["id"] = historyId })
                                .FirstOrDefault();
                            if (row == null) continue;
                            bool speakerIsA = ReadString(row, "hero_a_id", "")
                                .Equals(speakerId, StringComparison.OrdinalIgnoreCase);
                            chapters.Add(new Dictionary<string, object>
                            {
                                ["historyId"] = historyId,
                                ["targetHeroId"] = targetId,
                                ["chapterOrdinal"] = ReadInt(row, "chapter_ordinal", 0),
                                ["canonStatus"] = ReadString(row, "canon_status", ""),
                                ["generationStatus"] = ReadString(row, "generation_status", ""),
                                ["objectiveConduct"] = LimitText(
                                    ReadString(row, "objective_summary", ""), 900),
                                ["speakerPrivateInterpretation"] = LimitText(
                                    ReadString(row,
                                        speakerIsA ? "a_interpretation" : "b_interpretation", ""), 750),
                                ["otherPrivateInterpretationForbiddenToSpeaker"] = LimitText(
                                    ReadString(row,
                                        speakerIsA ? "b_interpretation" : "a_interpretation", ""), 750)
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object>
                {
                    ["available"] = false,
                    ["error"] = LimitText(ex.Message, 500),
                    ["chapters"] = chapters
                };
            }
            Dictionary<string, object> generation =
                ReadDictionary(relationshipPrompt, "historyGeneration")
                ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["available"] = chapters.Count > 0,
                ["speakerHeroId"] = speakerId,
                ["chapters"] = chapters.Take(9).ToList(),
                ["generatedCount"] = ReadInt(generation, "generatedCount", 0),
                ["reusedCount"] = ReadInt(generation, "reusedCount", 0),
                ["fallbackCount"] = ReadInt(generation, "fallbackCount", 0),
                ["providerCallCount"] = ReadInt(generation, "providerCallCount", 0),
                ["durationMs"] = ReadLong(generation, "durationMs", 0),
                ["providerDurationMs"] = ReadLong(generation, "providerDurationMs", 0)
            };
        }

        private static Dictionary<string, object> ReadinessCompactMotiveEvidence(
            Dictionary<string, object> motive)
        {
            motive = motive ?? new Dictionary<string, object>();
            Dictionary<string, object> romance = ReadDictionary(motive, "romance")
                ?? new Dictionary<string, object>();
            return new Dictionary<string, object>
            {
                ["activeDomains"] = ReadDictionaryList(motive, "activeDomains").Take(3)
                    .Select(domain => new Dictionary<string, object>
                    {
                        ["id"] = ReadString(domain, "id", ""),
                        ["instruction"] = LimitText(ReadString(domain, "instruction", ""), 420)
                    }).ToList(),
                ["highlightedScores"] = (ReadDictionary(motive, "highlightedScores")
                    ?? new Dictionary<string, object>()).Take(20)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
                ["opportunity"] = ReadDictionary(motive, "opportunity") ?? new Dictionary<string, object>(),
                ["courtCharacter"] = ReadDictionary(motive, "courtCharacter")
                    ?? new Dictionary<string, object>(),
                ["manipulation"] = ReadDictionary(motive, "manipulation") ?? new Dictionary<string, object>(),
                ["romance"] = new Dictionary<string, object>
                {
                    ["genuineInterest"] = ReadDouble(romance, "genuineInterest", 0d),
                    ["strategicInterest"] = ReadDouble(romance, "strategicInterest", 0d),
                    ["receptivity"] = ReadDouble(romance, "receptivity", 0d),
                    ["presentation"] = ReadString(romance, "presentation", ""),
                    ["posture"] = ReadString(romance, "posture", "")
                },
                ["relationshipNpcToTarget"] = ReadDictionary(motive, "relationshipNpcToTarget")
                    ?? new Dictionary<string, object>(),
                ["scene"] = ReadDictionary(motive, "scene") ?? new Dictionary<string, object>()
            };
        }

        private static Dictionary<string, object> ReadinessCompactSceneEvidence(
            Dictionary<string, object> scene,
            List<Dictionary<string, object>> lines)
        {
            Func<Dictionary<string, object>, Dictionary<string, object>> compactParticipant = row =>
                new Dictionary<string, object>
                {
                    ["id"] = ReadFirstString(row, "heroStringId", "heroId", "speakerHeroStringId"),
                    ["name"] = ReadString(row, "name", ""),
                    ["role"] = ReadString(row, "role", ""),
                    ["location"] = FirstNonEmpty(ReadString(row, "location", ""), ReadString(row, "nativeLocationDescription", ""))
                };
            return new Dictionary<string, object>
            {
                ["current"] = ReadDictionaryList(scene, "current").Take(6).Select(compactParticipant).ToList(),
                ["participants"] = ReadDictionaryList(scene, "participants").Take(6).Select(compactParticipant).ToList(),
                ["lines"] = (lines ?? new List<Dictionary<string, object>>())
                    .Skip(Math.Max(0, (lines ?? new List<Dictionary<string, object>>()).Count - 8))
                    .Select(row =>
                    new Dictionary<string, object>
                    {
                        ["role"] = ReadString(row, "role", ""),
                        ["speaker"] = ReadString(row, "speaker", ""),
                        ["text"] = LimitText(ReadString(row, "text", ""), 800)
                    }).ToList()
            };
        }

        private static string ReadinessExtractSection(
            string text, string startMarker, string endMarker, int maxChars)
        {
            text = text ?? "";
            int start = text.IndexOf(startMarker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return "";
            int end = text.IndexOf(endMarker, start + startMarker.Length, StringComparison.OrdinalIgnoreCase);
            if (end < 0) end = Math.Min(text.Length, start + maxChars);
            return LimitText(text.Substring(start, Math.Max(0, end - start)), maxChars);
        }

        private static void AppendReadinessRubricAssertion(
            List<Dictionary<string, object>> assertions,
            List<Dictionary<string, object>> failures,
            Dictionary<string, object> command,
            string assertion,
            bool passed,
            Dictionary<string, object> evaluation,
            string label)
        {
            string commandId = ReadString(command, "commandId", "");
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["assertion"] = assertion,
                ["expected"] = true,
                ["passed"] = passed,
                ["details"] = label + ": " + Json.Serialize(ReadDictionaryList(evaluation, "cases")),
                ["evaluatedUtc"] = DateTimeOffset.UtcNow.ToString("o")
            };
            assertions.Add(row);
            if (passed) return;
            bool critical = ReadDictionaryList(evaluation, "cases").Any(item =>
                ReadBool(item, "canonicalContradiction", false)
                || ReadBool(item, "knowledgeLeak", false)
                || ReadBool(item, "relationshipHistoryLeak", false)
                || ReadBool(item, "wrongOwner", false)
                || ReadBool(item, "fabricatedCaughtLie", false)
                || ReadBool(item, "hiddenStatusLeak", false));
            failures.Add(new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["assertion"] = assertion,
                ["critical"] = critical,
                ["error"] = label + " did not meet the 0.80 per-reply gate.",
                ["evidence"] = evaluation
            });
        }

        private static List<Dictionary<string, object>> CollectLiveTestRunAuditEvidence(
            Dictionary<string, object> run)
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> command in ReadDictionaryList(run, "commands"))
                rows.AddRange(CollectLiveTestCommandAuditEvidence(run, command));
            return rows.GroupBy(row => ReadString(row, "auditId", ""))
                .Select(group => group.First()).ToList();
        }

        private static List<Dictionary<string, object>> RunConversationReadinessEvaluationSelfTests()
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
