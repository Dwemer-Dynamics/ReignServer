using System;
using System.Collections.Generic;
using System.Linq;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static Dictionary<string, object> FinalGauntletControl(
            string[] args)
        {
            string operation = args.Skip(1)
                .FirstOrDefault(value =>
                    !value.StartsWith("-", StringComparison.Ordinal))
                ?? "status";
            if (operation.Equals("plan", StringComparison.OrdinalIgnoreCase))
                return Has(args, "--rebuild")
                    ? Post(
                        "/tests/final-gauntlet/catalog/build",
                        new Dictionary<string, object>())
                    : Get("/tests/final-gauntlet/catalog");
            if (operation.Equals(
                    "roleplay-plan", StringComparison.OrdinalIgnoreCase))
                return FinalGauntletRoleplayPlan();

            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            string runId = Value(args, "--run", "");
            string query = "?campaignId="
                + Uri.EscapeDataString(campaignId)
                + (string.IsNullOrWhiteSpace(runId)
                    ? string.Empty
                    : "&runId=" + Uri.EscapeDataString(runId));
            switch (operation.Trim().ToLowerInvariant())
            {
                case "status":
                    return Get(
                        "/tests/final-gauntlet/run/status" + query);
                case "report":
                    return Get(
                        "/tests/final-gauntlet/run/report" + query);
                case "review-pack":
                    return Get(
                        "/tests/final-gauntlet/review-pack" + query);
                case "roleplay":
                    return RunFinalGauntletRoleplayReview(
                        args, runtime, campaignId);
                case "cancel":
                    return Post(
                        "/tests/final-gauntlet/run/cancel",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["runId"] = RequireGauntletRunId(runId)
                        });
                case "pause":
                    return Post(
                        "/tests/final-gauntlet/run/pause",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["runId"] = RequireGauntletRunId(runId)
                        });
                case "resume":
                    {
                        Post(
                            "/tests/live/arm",
                            new Dictionary<string, object>
                            {
                                ["confirmation"] = "arm",
                                ["minutes"] =
                                    IntValue(args, "--minutes", 720),
                                ["requestedBy"] =
                                    "ReignLiveTest final gauntlet resume"
                            });
                        Dictionary<string, object> resumeStatus = Get(
                            "/tests/final-gauntlet/run/status?campaignId="
                            + Uri.EscapeDataString(campaignId)
                            + "&runId="
                            + Uri.EscapeDataString(
                                RequireGauntletRunId(runId)));
                        string resumeBaseline = FirstNonEmpty(
                            String(
                                ReadObject(resumeStatus, "schedule"),
                                "baseline_save_name"),
                            "ConvTest");
                        FinalGauntletSavePreflight(
                            campaignId,
                            resumeBaseline);
                        string resumedStateFingerprint =
                            FinalGauntletStateFingerprint(
                                campaignId);
                        Dictionary<string, object> resumed = Post(
                            "/tests/final-gauntlet/run/resume",
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId,
                                ["runId"] =
                                    RequireGauntletRunId(runId),
                                ["stateFingerprint"] =
                                    resumedStateFingerprint,
                                ["supportedModes"] =
                                    FinalGauntletRuntimeModes(runtime)
                            });
                        if (!IsOk(resumed) || Has(args, "--no-wait"))
                            return resumed;
                        return RunFinalGauntletLoop(
                            args,
                            campaignId,
                            runId,
                            Value(args, "--stage", "all"));
                    }
                case "start":
                    {
                        Post(
                            "/tests/live/arm",
                            new Dictionary<string, object>
                            {
                                ["confirmation"] = "arm",
                                ["minutes"] =
                                    IntValue(args, "--minutes", 720),
                                ["requestedBy"] =
                                    "ReignLiveTest final gauntlet"
                            });
                        string baselineSaveName = FirstNonEmpty(
                            Value(args, "--baseline-save", ""),
                            "ConvTest");
                        FinalGauntletSavePreflight(
                            campaignId,
                            baselineSaveName);
                        string stateFingerprint =
                            FinalGauntletStateFingerprint(
                                campaignId);
                        Dictionary<string, object> started = Post(
                            "/tests/final-gauntlet/run/start",
                            new Dictionary<string, object>
                            {
                                ["campaignId"] = campaignId,
                                ["runId"] = runId,
                                ["gameInstanceId"] =
                                    RuntimeInstance(runtime),
                                ["baselineSaveName"] =
                                    baselineSaveName,
                                ["stateFingerprint"] =
                                    stateFingerprint,
                                ["stage"] =
                                    Value(args, "--stage", "all"),
                                ["stageACaseIds"] =
                                    Values(args, "--stage-a-case")
                                        .ToArray()
                            });
                        if (!IsOk(started) || Has(args, "--no-wait"))
                            return started;
                        runId = String(started, "runId");
                        return RunFinalGauntletLoop(
                            args,
                            campaignId,
                            runId,
                            Value(args, "--stage", "all"));
                    }
                default:
                    throw new InvalidOperationException(
                        "Unknown gauntlet operation '" + operation
                        + "'. Use plan, roleplay-plan, roleplay, start, status, resume, cancel, report, or review-pack.");
            }
        }

        private static Dictionary<string, object> RunFinalGauntletLoop(
            string[] args,
            string campaignId,
            string runId,
            string requestedStage)
        {
            string controllerId = "ReignLiveTest-"
                + System.Diagnostics.Process.GetCurrentProcess().Id + "-"
                + Guid.NewGuid().ToString("N").Substring(0, 8);
            string stage = (requestedStage ?? "all").Trim().ToLowerInvariant();
            while (true)
            {
                Dictionary<string, object> status = Get(
                    "/tests/final-gauntlet/run/status?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&runId=" + Uri.EscapeDataString(runId));
                string scheduleState = String(
                    ReadObject(status, "schedule"),
                    "state");
                if (scheduleState == "awaiting_stage_b"
                    || scheduleState == "stage_a_failed")
                {
                    if (stage == "a")
                        return status;
                    Dictionary<string, object> promoted = Post(
                        "/tests/final-gauntlet/run/promote",
                        new Dictionary<string, object>
                        {
                            ["campaignId"] = campaignId,
                            ["runId"] = runId,
                            ["settingsFingerprint"] =
                                String(status, "settingsFingerprint"),
                            ["stateFingerprint"] =
                                FinalGauntletStateFingerprint(
                                    campaignId)
                        });
                    if (!IsOk(promoted)) return promoted;
                    continue;
                }
                if (new[]
                    {
                        "completed",
                        "completed_with_failures", "cancelled",
                        "interrupted"
                    }.Contains(
                        scheduleState,
                        StringComparer.OrdinalIgnoreCase))
                    return Get(
                        "/tests/final-gauntlet/run/report?campaignId="
                        + Uri.EscapeDataString(campaignId)
                        + "&runId=" + Uri.EscapeDataString(runId));

                Dictionary<string, object> lease = Post(
                    "/tests/final-gauntlet/case/lease",
                    new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["runId"] = runId,
                        ["controllerId"] = controllerId,
                        ["leaseSeconds"] = 1800
                    });
                if (!IsOk(lease))
                {
                    System.Threading.Thread.Sleep(500);
                    continue;
                }
                ExecuteFinalGauntletLease(
                    args, campaignId, runId, lease);
            }
        }

        private static void ExecuteFinalGauntletLease(
            string[] args,
            string campaignId,
            string runId,
            Dictionary<string, object> lease)
        {
            string caseInstanceId =
                String(lease, "caseInstanceId");
            Dictionary<string, object> descriptor =
                ReadObject(lease, "case");
            int priorAttempts = ReadObjects(
                Get(
                    "/tests/final-gauntlet/run/report?campaignId="
                    + Uri.EscapeDataString(campaignId)
                    + "&runId=" + Uri.EscapeDataString(runId)),
                "attempts").Count(attempt =>
                    String(attempt, "case_instance_id")
                        .Equals(
                            caseInstanceId,
                            StringComparison.OrdinalIgnoreCase));
            int attempt = priorAttempts + 1;
            string correlation = StableGauntletCorrelation(
                runId, caseInstanceId, attempt);
            Dictionary<string, object> result;
            try
            {
                result = ExecuteFinalGauntletCase(
                    args,
                    campaignId,
                    runId,
                    caseInstanceId,
                    descriptor,
                    correlation,
                    attempt);
            }
            catch (Exception ex)
            {
                // Stage B cases are intentionally single-execution. Fixture
                // ineligibility or another local harness exception is the result
                // of this case, not a reason to terminate the catalog runner and
                // leave its lease stranded. Record it once under the stable
                // correlation and continue to the next scheduled case.
                result = new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["status"] = "controller_exception",
                    ["caseInstanceId"] = caseInstanceId,
                    ["correlationId"] = correlation,
                    ["error"] = ex.Message,
                    ["exceptionType"] = ex.GetType().FullName
                };
            }
            bool providerFailure =
                IsFinalGauntletProviderFailure(result);
            bool passed = FinalGauntletCasePassed(
                result, providerFailure);
            Dictionary<string, object> recorded = Post(
                "/tests/final-gauntlet/case/result",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["runId"] = runId,
                    ["caseInstanceId"] = caseInstanceId,
                    ["correlationId"] = correlation,
                    ["liveRunId"] = String(result, "runId"),
                    ["passed"] = passed,
                    ["providerFailure"] = providerFailure,
                    ["error"] = passed
                        ? string.Empty
                        : FirstNonEmpty(
                            String(result, "error"),
                            FlattenFinalGauntletText(result))
                });
            if (!IsOk(recorded))
                throw new InvalidOperationException(
                    "The final gauntlet production result was not committed: "
                    + Json.Serialize(recorded));
        }

        private static Dictionary<string, object> ExecuteFinalGauntletCase(
            string[] args,
            string campaignId,
            string gauntletRunId,
            string caseInstanceId,
            Dictionary<string, object> descriptor,
            string correlation,
            int attempt)
        {
            string baseCaseId = (caseInstanceId ?? string.Empty)
                .Split(new[] { "::" }, StringSplitOptions.None)[0];
            if (baseCaseId == "LNG-001")
                return ExecuteFocalNpcLongHorizon(
                    args, campaignId, gauntletRunId,
                    caseInstanceId, correlation,
                    caseInstanceId.EndsWith(
                        "::stage-b",
                        StringComparison.OrdinalIgnoreCase)
                        ? 100 : 24);
            if (baseCaseId == "LNG-002")
                return ExecuteTwentyNpcCourtSoak(
                    args, campaignId, gauntletRunId,
                    caseInstanceId, correlation,
                    caseInstanceId.EndsWith(
                        "::stage-b",
                        StringComparison.OrdinalIgnoreCase)
                        ? 20 : 12);
            if (baseCaseId.StartsWith(
                "ACTION-", StringComparison.OrdinalIgnoreCase))
            {
                Dictionary<string, object> actionEvidence =
                    ExecuteFinalGauntletActionEvidence(descriptor);
                bool providerAction = Convert.ToInt32(
                    descriptor.TryGetValue(
                        "requires_provider",
                        out object actionProvider)
                        ? actionProvider : 0) != 0;
                if (!providerAction || !IsOk(actionEvidence))
                    return actionEvidence;
            }
            bool requiresGame =
                Convert.ToInt32(
                    descriptor.TryGetValue(
                        "requires_game",
                        out object game) ? game : 0) != 0;
            bool requiresProvider =
                Convert.ToInt32(
                    descriptor.TryGetValue(
                        "requires_provider",
                        out object provider) ? provider : 0) != 0;
            if (!requiresGame && !requiresProvider)
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "completed",
                    ["execution"] = "deterministic_server_component",
                    ["caseInstanceId"] = caseInstanceId
                };

            Dictionary<string, object> runtime = Runtime(args);
            string mode = baseCaseId.StartsWith(
                    "GAUNTLET-", StringComparison.OrdinalIgnoreCase)
                ? FinalGauntletSceneMode(
                    FinalGauntletSceneOrdinal(baseCaseId))
                : NormalizeFinalGauntletMode(
                    String(descriptor, "mode"));
            string readinessCategory =
                ExtractGauntletTag(
                    FirstNonEmpty(
                        String(descriptor, "tags_json"),
                        String(descriptor, "tags")),
                    "readiness:");
            string liveRunId = "fg-live-"
                + Math.Abs(
                    (gauntletRunId + caseInstanceId + attempt)
                        .GetHashCode()).ToString("x");
            string requirement = FirstNonEmpty(
                String(descriptor, "behavioral_requirement"),
                "Respond naturally and remain grounded in the current scene.");
            List<string> requirementIds = QualificationReadStrings(
                    descriptor, "requirement_ids")
                .SelectMany(value => value.Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries))
                .ToList();
            List<Dictionary<string, object>> steps =
                string.IsNullOrWhiteSpace(readinessCategory)
                    ? BuildFinalGauntletGeneralSteps(
                        campaignId,
                        caseInstanceId,
                        mode,
                        requirement,
                        requirementIds,
                        FirstNonEmpty(
                            String(ReadObject(runtime, "runtime"), "playerName"),
                            "traveler"),
                        String(
                            ReadObject(
                                ReadObject(runtime, "runtime"),
                                "location"),
                            "settlementName"))
                    : BuildFinalGauntletReadinessSteps(
                        campaignId,
                        caseInstanceId,
                        readinessCategory,
                        ref mode);
            int commandOrdinal = 0;
            foreach (Dictionary<string, object> step in steps)
            {
                commandOrdinal++;
                step["commandId"] = correlation + "-"
                    + commandOrdinal.ToString("00");
            }
            bool wildernessCase = mode.Equals(
                "wilderness_event",
                StringComparison.OrdinalIgnoreCase);
            try
            {
            Dictionary<string, object> started = Post(
                "/tests/live/run/start",
                new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["runId"] = liveRunId,
                    ["mode"] = mode,
                    ["presentation"] = "headless",
                    ["effects"] = "guarded",
                    ["label"] = "Final gauntlet "
                        + caseInstanceId,
                    ["autoCompleteWhenIdle"] = true,
                    ["gauntletRunId"] = gauntletRunId,
                    ["caseInstanceId"] = caseInstanceId,
                    ["correlationId"] = correlation,
                    ["qualificationId"] = gauntletRunId,
                    ["steps"] = steps.ToArray()
                });
            if (!IsOk(started)) return started;
            Dictionary<string, object> completed = WaitForRun(
                campaignId,
                liveRunId,
                string.Empty,
                IntValue(args, "--case-timeout", 3600),
                true);
            if (!IsOk(completed)
                || !String(completed, "status").Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
                return completed;
            Dictionary<string, object> report = Get(
                "/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(liveRunId));
            Dictionary<string, object> evaluation =
                EvaluateQualificationRunWithReconciliation(
                    campaignId,
                    liveRunId,
                    report);
            if (!IsOk(evaluation))
                return evaluation;
            return Get(
                "/tests/live/run/report?campaignId="
                + Uri.EscapeDataString(campaignId)
                + "&runId=" + Uri.EscapeDataString(liveRunId));
            }
            finally
            {
                if (wildernessCase)
                    RestoreFinalGauntletSettlement(
                        campaignId,
                        caseInstanceId + "-cleanup");
            }
        }

        private static bool FinalGauntletCasePassed(
            Dictionary<string, object> result,
            bool providerFailure)
        {
            if (!IsOk(result)
                || providerFailure
                || !String(result, "status").Equals(
                    "completed",
                    StringComparison.OrdinalIgnoreCase))
                return false;
            if (ReadObjects(result, "failures").Any())
                return false;
            return ReadObjects(result, "assertions")
                .All(row => ReadBoolean(row, "passed"));
        }

        private static List<Dictionary<string, object>>
            BuildFinalGauntletReadinessSteps(
                string campaignId,
                string caseInstanceId,
                string category,
                ref string mode)
        {
            int ordinal = StableFinalGauntletOrdinal(caseInstanceId);
            bool group = category.Equals(
                    "group_awareness",
                    StringComparison.OrdinalIgnoreCase)
                || category.Equals(
                    "shared_relationship_history",
                    StringComparison.OrdinalIgnoreCase);
            mode = group ? "party_chat" : "individual_chat";
            bool sharedRelationshipHistory = category.Equals(
                "shared_relationship_history",
                StringComparison.OrdinalIgnoreCase);
            List<string> targets = sharedRelationshipHistory
                ? FinalGauntletColdRelationshipHistoryTargets(
                    campaignId)
                : FinalGauntletTargets(
                    campaignId, mode, caseInstanceId, group ? 2 : 1);
            List<Dictionary<string, object>> steps =
                new List<Dictionary<string, object>>
                {
                    OpenStep(mode, targets, group ? 2 : 1)
                };
            switch ((category ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "identity_evidence":
                    steps.Add(IndividualProbe(ordinal % 5, 0, 0));
                    break;
                case "identity_role_authority":
                    steps.Add(IdentityAuthorityProbe(
                        "Good day. I am not sure whether word of my arrival "
                        + "reached you. How do you know me, and what brings you "
                        + "to speak with me here?"));
                    break;
                case "short_cross_scene_memory":
                    steps.Add(IndividualProbe(ordinal % 5, 0, 1));
                    steps.Add(IndividualProbe(ordinal % 5, 2, 0));
                    break;
                case "long_term_memory":
                    steps.Add(IndividualProbe(ordinal % 5, 0, 1));
                    steps.Add(CloseStep(mode));
                    steps.Add(OpenStep(mode, targets));
                    steps.Add(IndividualProbe(ordinal % 5, 7, 2));
                    break;
                case "dynamic_characteristics":
                    steps.Add(IndividualProbe(ordinal % 5, 0, 3));
                    steps.Add(IndividualProbe(ordinal % 5, 1, 3));
                    break;
                case "shared_relationship_history":
                    steps.Add(GroupRelationshipHistoryProbe(
                        targets, "cold", false, "cold_initial"));
                    steps.Add(GroupRelationshipHistoryProbe(
                        targets, "reuse", false, "cold_followup"));
                    break;
                case "group_awareness":
                    steps.Add(GroupProbe(
                        ordinal % 3, 0, 1, targets, mode));
                    break;
                case "world_local_knowledge":
                    steps.Add(IndividualProbe(ordinal % 5, 0, 2));
                    break;
                case "lie_relationship":
                    steps.Add(LieRelationshipProbe(14 + ordinal % 6));
                    break;
                case "clan_tier_recognition":
                    steps.Add(ClanTierRecognitionProbe(ordinal));
                    break;
                case "manipulation_capabilities":
                    steps.Add(ManipulationProbe(ordinal));
                    break;
                case "personality_consistency":
                    steps.Add(IndividualProbe(ordinal % 5, 5, 3));
                    break;
                case "detailed_factual_accuracy":
                    steps.Add(IndividualProbe(ordinal % 5, 3, 0));
                    break;
                case "performance_resilience":
                    steps.Add(IndividualProbe(ordinal % 5, 0, 2));
                    break;
                default:
                    steps.Add(IndividualProbe(ordinal % 5, 0, 0));
                    break;
            }
            int sendCount = steps.Count(step =>
                String(step, "operation").Equals(
                    "send", StringComparison.OrdinalIgnoreCase));
            if (sendCount < 2)
                steps.Add(FinalGauntletReadinessFollowup(
                    category, mode, targets, ordinal));
            if (!steps.Last().TryGetValue(
                    "operation",
                    out object operation)
                || !Convert.ToString(operation).Equals(
                    "close",
                    StringComparison.OrdinalIgnoreCase))
                steps.Add(CloseStep(mode));
            return steps;
        }

        private static Dictionary<string, object>
            FinalGauntletReadinessFollowup(
                string category,
                string mode,
                List<string> targets,
                int ordinal)
        {
            string normalized = (category ?? string.Empty)
                .Trim().ToLowerInvariant();
            string text;
            switch (normalized)
            {
                case "identity_evidence":
                    text = "Now that we have exchanged names, how would you address me if we met again tomorrow?";
                    break;
                case "identity_role_authority":
                    text = "If I asked you to act on my authority here, which office or allegiance would make that request lawful—and which would not?";
                    break;
                case "group_awareness":
                    text = "That first answer changes the choice before us. What consequence has it raised for the rest of the company?";
                    break;
                case "world_local_knowledge":
                case "detailed_factual_accuracy":
                    text = "Before I choose a lodging, who owns this settlement now, who governs its daily affairs, and what part of the answer remains uncertain?";
                    break;
                case "lie_relationship":
                    text = "I have admitted the falsehood. Tell me what the deception cost between us, not merely whether the claim itself was wrong.";
                    break;
                case "clan_tier_recognition":
                    text = "If we were seen entering the hall together, would the association raise your standing, lower it, or change nothing?";
                    break;
                case "manipulation_capabilities":
                    text = "You have heard what I might offer. If you still want my help, make the bargain you believe I am most likely to accept.";
                    break;
                case "personality_consistency":
                    text = "I will press once more: set aside your first answer and give me what I want. Will you?";
                    break;
                case "performance_resilience":
                    text = "One last matter before I leave: what concern of your own have we not yet addressed?";
                    break;
                default:
                    text = "That answer leaves one practical question: what would you have us do next, and what would you refuse to do?";
                    break;
            }
            bool group = !mode.Equals(
                "individual_chat", StringComparison.OrdinalIgnoreCase);
            Dictionary<string, object> assertions = BaseAssertions(
                group ? Math.Max(2, targets?.Count ?? 0) : 1, true);
            assertions["visiblePlayerTextPolicy"] =
                "in_world_roleplay_only";
            assertions["hiddenFixtureObjective"] =
                "Provide fresh same-build evidence for " + normalized + ".";
            assertions["requiresPersonalityEvidence"] = true;
            if (group)
            {
                assertions["requiresGroupAwareness"] = true;
                assertions["requiresGroupDivergence"] = true;
            }
            if (normalized == "identity_role_authority")
                assertions["requiresPoliticalAuthorityEvidence"] = true;
            if (normalized == "world_local_knowledge"
                || normalized == "detailed_factual_accuracy")
                assertions["requiredContextPulls"] =
                    new[] { "current_settlement_facts" };
            if (normalized == "lie_relationship")
                assertions["requiresRelationshipReceipt"] = true;
            if (normalized == "clan_tier_recognition")
                assertions["requiresClanTierRecognitionEvidence"] = true;
            if (normalized == "manipulation_capabilities")
                assertions["requiresManipulationEvidence"] = true;
            return SendStep(
                mode,
                text,
                new List<string>
                {
                    "structural_pipeline",
                    normalized,
                    "personality_consistency",
                    "performance_resilience"
                },
                assertions);
        }

        private static List<Dictionary<string, object>>
            BuildFinalGauntletGeneralSteps(
                string campaignId,
                string caseInstanceId,
                string mode,
                string requirement,
                IEnumerable<string> requirementIds,
                string playerName,
                string settlementName)
        {
            bool group = mode.Equals("party_chat", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("social_event", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("wilderness_event", StringComparison.OrdinalIgnoreCase);
            bool denseGroup = caseInstanceId.StartsWith(
                "LIVE-GROUP-", StringComparison.OrdinalIgnoreCase);
            int participantCount = group ? (denseGroup ? 2 : 3) : 1;
            bool requiresFirstMeeting = (requirementIds
                    ?? Enumerable.Empty<string>())
                .Contains("IDN-010", StringComparer.OrdinalIgnoreCase);
            List<string> targets = requiresFirstMeeting
                    && mode.Equals(
                        "individual_chat",
                        StringComparison.OrdinalIgnoreCase)
                ? new List<string>
                {
                    FinalGauntletFreshIndividualTarget(campaignId)
                }
                : FinalGauntletTargets(
                    campaignId, mode, caseInstanceId, participantCount);
            List<Dictionary<string, object>> steps =
                new List<Dictionary<string, object>>();
            if (mode.Equals(
                "wilderness_event",
                StringComparison.OrdinalIgnoreCase))
            {
                steps.Add(new Dictionary<string, object>
                {
                    ["schemaVersion"] = 2,
                    ["operation"] = "prepare_wilderness",
                    ["mode"] = mode,
                    ["targetSearches"] = targets.ToArray(),
                    ["timeoutSeconds"] = 120
                });
            }
            steps.Add(OpenStep(mode, targets, participantCount));
            List<string> turns = FinalGauntletRoleplayTurns(
                caseInstanceId,
                requirement,
                requirementIds,
                playerName,
                settlementName);
            bool finalScene = caseInstanceId.StartsWith(
                "GAUNTLET-", StringComparison.OrdinalIgnoreCase);
            if (finalScene && group)
                turns = turns.Take(1).ToList();
            else if (finalScene)
                turns.Add(FinalGauntletSceneClosingPrompt(
                    FinalGauntletSceneOrdinal(caseInstanceId)));
            int turnOrdinal = 0;
            foreach (string playerText in turns)
            {
                turnOrdinal++;
                List<string> turnCategories = FinalGauntletCategories(
                    caseInstanceId, requirementIds);
                bool deferSemanticJudgment =
                    (caseInstanceId.StartsWith(
                            "LIVE-", StringComparison.OrdinalIgnoreCase)
                        || finalScene)
                    && turnOrdinal < turns.Count;
                if (deferSemanticJudgment)
                    turnCategories = new List<string>
                    {
                        "structural_pipeline",
                        "performance_resilience"
                    };
                Dictionary<string, object> send = SendStep(
                    mode,
                    playerText,
                    turnCategories,
                    BaseAssertions(
                        group ? targets.Count : 1, true));
                Dictionary<string, object> assertions = ReadObject(
                    send, "assertions");
                assertions["hiddenFixtureObjective"] = requirement;
                assertions["representedRequirementIds"] =
                    (requirementIds ?? Enumerable.Empty<string>()).ToArray();
                assertions["visiblePlayerTextPolicy"] =
                    "in_world_roleplay_only";
                assertions["denseSceneTurn"] = turnOrdinal;
                if (group)
                {
                    assertions["requiresGroupAwareness"] = true;
                    assertions["requiresGroupDivergence"] = true;
                }
                steps.Add(send);
            }
            steps.Add(CloseStep(mode));
            return steps;
        }

        private static void RestoreFinalGauntletSettlement(
            string campaignId,
            string caseInstanceId)
        {
            try
            {
                Dictionary<string, object> runtime = Runtime(
                    new string[0]);
                string runId = "fg-restore-"
                    + Math.Abs((caseInstanceId ?? string.Empty)
                        .GetHashCode()).ToString("x");
                Dictionary<string, object> started = Post(
                    "/tests/live/run/start",
                    new Dictionary<string, object>
                    {
                        ["schemaVersion"] = 2,
                        ["campaignId"] = campaignId,
                        ["gameInstanceId"] = RuntimeInstance(runtime),
                        ["runId"] = runId,
                        ["mode"] = "wilderness_event",
                        ["presentation"] = "headless",
                        ["effects"] = "guarded",
                        ["label"] = "Final gauntlet wilderness cleanup",
                        ["autoCompleteWhenIdle"] = true,
                        ["steps"] = new object[]
                        {
                            new Dictionary<string, object>
                            {
                                ["schemaVersion"] = 2,
                                ["operation"] = "restore_settlement",
                                ["mode"] = "wilderness_event",
                                ["timeoutSeconds"] = 120
                            }
                        }
                    });
                if (IsOk(started))
                    WaitForRun(campaignId, runId, string.Empty,
                        180, true);
            }
            catch
            {
                // The original case retains its failure evidence. A later
                // preflight will fail closed if native state could not be
                // restored, rather than silently running in the wrong mode.
            }
        }

        private static List<string> FinalGauntletTargets(
            string campaignId,
            string mode,
            string caseInstanceId,
            int count)
        {
            List<Dictionary<string, object>> available =
                QualificationTargets(
                    campaignId, mode, 500, null, false);
            if (available.Count < count
                && mode.Equals(
                    "party_chat",
                    StringComparison.OrdinalIgnoreCase))
            {
                List<string> candidates = QualificationTargets(
                        campaignId,
                        "individual_chat",
                        20,
                        null,
                        false)
                    .Select(row => String(row, "heroId"))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .ToList();
                EnsureQualificationPartyFixture(
                    campaignId,
                    "final-gauntlet-party-fixture",
                    candidates);
                available = QualificationTargets(
                    campaignId, mode, 500, null, false);
            }
            if (available.Count < count)
                throw new InvalidOperationException(
                    "Final gauntlet requires " + count
                    + " eligible " + mode + " targets but found "
                    + available.Count + ".");
            int offset = StableFinalGauntletOrdinal(caseInstanceId)
                % available.Count;
            return available
                .Skip(offset)
                .Concat(available.Take(offset))
                .Take(count)
                .Select(row => String(row, "heroId"))
                .ToList();
        }

        private static List<string>
            FinalGauntletColdRelationshipHistoryTargets(
                string campaignId)
        {
            List<string> partyHeroIds = QualificationTargets(
                    campaignId, "party_chat", 500, null, false)
                .Select(row => String(row, "heroId"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (partyHeroIds.Count < 2)
                throw new InvalidOperationException(
                    "Shared Relationship History cold-start qualification "
                    + "requires two eligible party-chat NPCs.");

            List<string> selected =
                SelectQualificationRelationshipHistoryPairs(
                    campaignId, partyHeroIds, 1)[0];
            Dictionary<string, object> existing = Post(
                "/relationships/history/query",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["heroStringId"] = selected[0],
                    ["otherHeroStringId"] = selected[1]
                });
            if (!IsOk(existing))
                throw new InvalidOperationException(
                    "Could not verify the selected cold relationship-history "
                    + "pair: " + Json.Serialize(existing));
            if (ReadLong(existing, "count", 0) != 0)
                throw new InvalidOperationException(
                    "No unused party-chat relationship pair remains for the "
                    + "required cold first-use qualification. Start from the "
                    + "protected ConvTest baseline or provide a fresh pair.");
            return selected;
        }

        private static string FinalGauntletFreshIndividualTarget(
            string campaignId)
        {
            Dictionary<string, object> fresh = QualificationTargets(
                    campaignId,
                    "individual_chat",
                    16,
                    null,
                    true)
                .Where(row => ReadLong(
                    row, "priorDialogueLineCount", 500) == 0)
                .OrderBy(row => String(row, "heroId"),
                    StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            string heroId = String(fresh, "heroId");
            if (string.IsNullOrWhiteSpace(heroId))
                throw new InvalidOperationException(
                    "IDN-010 requires a living adult NPC with zero prior dialogue lines. The fixture refuses to relabel an acquaintance as a first meeting.");
            return heroId;
        }

        private static int StableFinalGauntletOrdinal(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char character in value ?? string.Empty)
                {
                    hash ^= character;
                    hash *= 16777619;
                }
                return (int)(hash & 0x7fffffff);
            }
        }

        private static List<string> FinalGauntletCategories(
            string caseInstanceId,
            IEnumerable<string> requirementIds)
        {
            string family = (caseInstanceId ?? string.Empty)
                .Split('-').FirstOrDefault()?.ToUpperInvariant()
                ?? string.Empty;
            if (family == "LIVE")
                family = (requirementIds ?? Enumerable.Empty<string>())
                    .Select(value => (value ?? string.Empty)
                        .Split('-').FirstOrDefault()?.ToUpperInvariant())
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                    ?? family;
            List<string> categories = new List<string>
            {
                "structural_pipeline",
                "performance_resilience"
            };
            if (family == "GAUNTLET")
            {
                switch (FinalGauntletSceneOrdinal(caseInstanceId))
                {
                    case 1:
                    case 2:
                        categories.AddRange(new[]
                        {
                            "personality_consistency",
                            "manipulation_capabilities",
                            "lie_relationship"
                        });
                        break;
                    case 3:
                    case 12:
                        categories.AddRange(new[]
                        {
                            "world_local_knowledge",
                            "detailed_factual_accuracy"
                        });
                        break;
                    case 6:
                        categories.AddRange(new[]
                        {
                            "group_awareness",
                            "personality_consistency",
                            "detailed_factual_accuracy"
                        });
                        break;
                    case 7:
                        categories.AddRange(new[]
                        {
                            "group_awareness",
                            "personality_consistency"
                        });
                        break;
                    case 8:
                    case 17:
                    case 19:
                        categories.AddRange(new[]
                        {
                            "long_term_memory",
                            "detailed_factual_accuracy"
                        });
                        break;
                    case 9:
                    case 10:
                    case 11:
                        categories.AddRange(new[]
                        {
                            "group_awareness",
                            "detailed_factual_accuracy"
                        });
                        break;
                    case 13:
                        categories.AddRange(new[]
                        {
                            "personality_consistency",
                            "detailed_factual_accuracy"
                        });
                        break;
                    case 16:
                        categories.AddRange(new[]
                        {
                            "identity_role_authority",
                            "detailed_factual_accuracy"
                        });
                        break;
                    case 20:
                        categories.AddRange(new[]
                        {
                            "group_awareness",
                            "long_term_memory",
                            "personality_consistency",
                            "detailed_factual_accuracy"
                        });
                        break;
                    default:
                        categories.Add("detailed_factual_accuracy");
                        break;
                }
            }
            if (family == "IDN") categories.Add("identity_role_authority");
            if (family == "SIT" || family == "WLD")
                categories.AddRange(new[]
                {
                    "world_local_knowledge",
                    "detailed_factual_accuracy"
                });
            if (family == "PER")
                categories.Add("personality_consistency");
            if (family == "REL" || family == "ROM"
                || family == "STA")
                categories.AddRange(new[]
                {
                    "lie_relationship", "personality_consistency"
                });
            if (family == "MEM")
                categories.AddRange(new[]
                {
                    "long_term_memory",
                    "detailed_factual_accuracy"
                });
            if (family == "GRP" || family == "MOD")
                categories.Add("group_awareness");
            if (family == "SOC")
                categories.Add("manipulation_capabilities");
            if (family == "RUM")
                categories.AddRange(new[]
                {
                    "world_local_knowledge",
                    "detailed_factual_accuracy"
                });
            return categories.Distinct(
                StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static int FinalGauntletSceneOrdinal(string caseId)
        {
            string value = (caseId ?? string.Empty)
                .Split(new[] { "::" },
                    StringSplitOptions.None)[0];
            if (value.StartsWith(
                "GAUNTLET-", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(
                    value.Substring("GAUNTLET-".Length),
                    out int ordinal))
                return ordinal;
            return 0;
        }

        private static Dictionary<string, object>
            ExecuteFinalGauntletActionEvidence(
                Dictionary<string, object> descriptor)
        {
            string tagsJson = FirstNonEmpty(
                String(descriptor, "tags_json"),
                String(descriptor, "tags"));
            string action = ExtractGauntletTag(tagsJson, "action:");
            string requirement = ExtractGauntletTag(
                tagsJson, "requirement:");
            if (string.IsNullOrWhiteSpace(action)
                || string.IsNullOrWhiteSpace(requirement))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "Action conformance case lacks action/requirement tags."
                };
            return Post(
                "/tests/final-gauntlet/action/evidence",
                new Dictionary<string, object>
                {
                    ["action"] = action,
                    ["requirementId"] = requirement
                });
        }

        private static string ExtractGauntletTag(
            string tagsJson,
            string prefix)
        {
            if (string.IsNullOrWhiteSpace(tagsJson)) return string.Empty;
            try
            {
                object parsed = Json.DeserializeObject(tagsJson);
                foreach (object value in parsed as object[] ?? Array.Empty<object>())
                {
                    string text = Convert.ToString(value) ?? string.Empty;
                    if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        return text.Substring(prefix.Length);
                }
            }
            catch
            {
            }
            foreach (string value in tagsJson.Split(
                new[] { '\r', '\n', ',' },
                StringSplitOptions.RemoveEmptyEntries))
            {
                string text = value.Trim().Trim('"');
                if (text.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase))
                    return text.Substring(prefix.Length);
            }
            return string.Empty;
        }

        private static string NormalizeFinalGauntletMode(string mode)
        {
            string normalized = NormalizeMode(mode);
            if (normalized == "individual") return "individual_chat";
            if (normalized == "party") return "party_chat";
            if (new[]
                {
                    "individual_chat", "party_chat", "social_event",
                    "wilderness_event", "correspondence"
                }.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                return normalized;
            return "individual_chat";
        }

        private static string FinalGauntletSceneMode(int ordinal)
        {
            if (new[] { 1, 2, 6, 7, 9 }
                .Contains(ordinal))
                return "party_chat";
            if (new[] { 10, 11, 12, 13, 20 }
                .Contains(ordinal))
                return "social_event";
            return "individual_chat";
        }

        private static string[] FinalGauntletRuntimeModes(
            Dictionary<string, object> runtime)
        {
            Dictionary<string, object> gameRuntime =
                ReadObject(runtime, "runtime");
            List<string> modes = QualificationReadStrings(
                    gameRuntime, "capabilities")
                .Where(value => !value.Equals(
                    "social_balance",
                    StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (modes.Count == 0)
                modes = new List<string>
                {
                    "individual_chat", "party_chat", "social_event",
                    "wilderness_event", "correspondence"
                };
            return modes.ToArray();
        }

        private static string BuildFinalGauntletPlayerPrompt(
            string caseInstanceId,
            string requirement,
            IEnumerable<string> requirementIds,
            string playerName,
            string settlementName)
        {
            return FinalGauntletRoleplayPrompt(
                caseInstanceId,
                requirement,
                requirementIds,
                playerName,
                settlementName);
        }

        private static string RequireGauntletRunId(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId))
                throw new InvalidOperationException(
                    "--run is required for this gauntlet operation.");
            return runId;
        }

        private static string FinalGauntletStateFingerprint(
            string campaignId)
        {
            List<Dictionary<string, object>> targets =
                QualificationTargets(
                    campaignId,
                    "individual_chat",
                    2000,
                    null,
                    false);
            if (targets.Count == 0)
                throw new InvalidOperationException(
                    "The authoritative living-adult NPC census is empty.");
            string[] fields =
            {
                "heroId", "characterObjectId", "name", "clanId",
                "clanName", "clanLeaderId", "isClanLeader",
                "kingdomId", "cultureId", "clanTier", "isLord",
                "isRuler", "isNotable", "isWanderer", "isAlive",
                "isAdult", "occupation", "governorOfSettlementId"
            };
            List<Dictionary<string, object>> census =
                targets.Select(target =>
                {
                    Dictionary<string, object> row =
                        new Dictionary<string, object>(
                            StringComparer.OrdinalIgnoreCase);
                    foreach (string field in fields)
                        if (target.TryGetValue(
                                field,
                                out object value))
                            row[field] = value;
                    return row;
                }).ToList();
            Dictionary<string, object> result = Post(
                "/tests/final-gauntlet/state/fingerprint",
                new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["targets"] = census
                });
            if (!IsOk(result))
                throw new InvalidOperationException(
                    "The authoritative campaign/clan census was rejected: "
                    + Json.Serialize(result));
            string fingerprint = String(result, "fingerprint");
            if (string.IsNullOrWhiteSpace(fingerprint))
                throw new InvalidOperationException(
                    "The server did not return a campaign/clan census fingerprint.");
            return fingerprint;
        }
    }
}
