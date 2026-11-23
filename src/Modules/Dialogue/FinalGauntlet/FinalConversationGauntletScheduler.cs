using System;
using System.Collections.Generic;
using System.Linq;
using System.Web.Script.Serialization;

namespace ReignBetaServer
{
    internal static class FinalConversationGauntletScheduler
    {
        public static Dictionary<string, object> StartStageA(
            ReignDbConnection connection,
            string runId,
            string campaignId,
            string buildVersion,
            List<FinalGauntletCaseDescriptor> cases,
            string manifestFingerprint,
            string stateFingerprint,
            string baselineSaveName)
        {
            EnsureSchema(connection);
            if (string.IsNullOrWhiteSpace(stateFingerprint))
                return Result(
                    false,
                    "An authoritative baseline state fingerprint is required.");
            if (!string.Equals(
                    baselineSaveName,
                    "ConvTest",
                    StringComparison.OrdinalIgnoreCase))
                return Result(
                    false,
                    "The final gauntlet baseline must be the protected ConvTest save.");
            string active = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql2,
                ("$campaign", campaignId));
            if (!string.IsNullOrWhiteSpace(active))
                return Result(
                    false,
                    "A final gauntlet is already active for this campaign.",
                    ("activeRunId", active));
            try
            {
                FinalGauntletStore.CreateRun(
                    connection,
                    runId,
                    campaignId,
                    FinalGauntletStage.BoundedQualification,
                    buildVersion,
                    cases);
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                string initialState = cases.Count == 0
                    ? "awaiting_stage_b"
                    : "running_stage_a";
                Execute(
                    connection,
                    DialogueStore.InsertFinalGauntletScheduleSql,
                    ("$run", runId),
                    ("$campaign", campaignId),
                    ("$state", initialState),
                    ("$baseline", baselineSaveName),
                    ("$manifest", manifestFingerprint ?? string.Empty),
                    ("$stateFingerprint", stateFingerprint),
                    ("$ts", now));
                Execute(
                    connection,
                    DialogueStore.UpdateFinalGauntletRunsSql,
                    ("$run", runId), ("$state", initialState), ("$ts", now));
                Execute(
                    connection,
                    DialogueStore.UpdateFinalGauntletCasesSql,
                    ("$run", runId), ("$ts", now));
                return Result(
                    true, "", ("runId", runId),
                    ("state", initialState));
            }
            catch (Exception ex)
            {
                return Result(false, ex.Message);
            }
        }

        public static Dictionary<string, object> LeaseNext(
            ReignDbConnection connection,
            string runId,
            string controllerId,
            long now,
            long leaseSeconds)
        {
            EnsureSchema(connection);
            if (ScalarLong(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql3,
                ("$run", runId)) != 0)
                return Result(false, "The final gauntlet is paused.");
            if (ReignPostgreSqlDialect.IsPostgreSql(connection))
                return LeaseNextPostgreSql(
                    connection, runId, controllerId, now, leaseSeconds);
            Dictionary<string, object> expired = QueryOne(
                connection,
                DialogueStore.SelectFinalGauntletLeasesSql,
                ("$run", runId), ("$now", now));
            if (expired.Count > 0)
            {
                string caseId = Text(expired, "case_instance_id");
                Execute(
                    connection,
                    DialogueStore.UpdateFinalGauntletLeasesSql,
                    ("$controller", controllerId),
                    ("$now", now),
                    ("$expires", now + Math.Max(1, leaseSeconds)),
                    ("$run", runId),
                    ("$case", caseId));
                return Result(
                    true, "", ("runId", runId),
                    ("caseInstanceId", caseId),
                    ("recovered", true),
                    ("correlationLedgerJson",
                        Text(expired, "correlation_ledger_json")));
            }
            Dictionary<string, object> next = QueryOne(
                connection,
                DialogueStore.SelectFinalGauntletCasesSql,
                ("$run", runId));
            if (next.Count == 0)
                return Result(false, "No queued case is available.");
            string nextId = Text(next, "case_instance_id");
            if (!FinalGauntletStore.TryTransitionCase(
                connection,
                runId,
                nextId,
                FinalGauntletCaseState.Queued,
                FinalGauntletCaseState.Running))
                return Result(false, "The queued case was leased concurrently.");
            Execute(
                connection,
                DialogueStore.UpsertFinalGauntletLeasesSql,
                ("$run", runId), ("$case", nextId),
                ("$controller", controllerId), ("$now", now),
                ("$expires", now + Math.Max(1, leaseSeconds)));
            return Result(
                true, "", ("runId", runId),
                ("caseInstanceId", nextId), ("recovered", false),
                ("correlationLedgerJson", "[]"));
        }

        private static Dictionary<string, object> LeaseNextPostgreSql(
            ReignDbConnection connection,
            string runId,
            string controllerId,
            long now,
            long leaseSeconds)
        {
            using (ReignDbTransaction transaction =
                connection.BeginTransaction())
            {
                Dictionary<string, object> expired = QueryOne(
                    connection,
                    transaction,
                    DialogueStore.SelectFinalGauntletLeasesSql2,
                    ("$run", runId), ("$now", now));
                if (expired.Count > 0)
                {
                    string recoveredId =
                        Text(expired, "case_instance_id");
                    Execute(
                        connection,
                        transaction,
                        DialogueStore.UpdateFinalGauntletLeasesSql2,
                        ("$controller", controllerId),
                        ("$now", now),
                        ("$expires",
                            now + Math.Max(1, leaseSeconds)),
                        ("$run", runId),
                        ("$case", recoveredId));
                    transaction.Commit();
                    return Result(
                        true, "", ("runId", runId),
                        ("caseInstanceId", recoveredId),
                        ("recovered", true),
                        ("correlationLedgerJson",
                            Text(expired,
                                "correlation_ledger_json")));
                }

                Dictionary<string, object> next = QueryOne(
                    connection,
                    transaction,
                    DialogueStore.SelectFinalGauntletCasesSql2,
                    ("$run", runId));
                if (next.Count == 0)
                {
                    transaction.Commit();
                    return Result(
                        false, "No queued case is available.");
                }
                string nextId = Text(next, "case_instance_id");
                int transitioned = ExecuteCount(
                    connection,
                    transaction,
                    DialogueStore.UpdateFinalGauntletCasesSql2,
                    ("$ts",
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    ("$run", runId), ("$case", nextId));
                if (transitioned != 1)
                {
                    transaction.Rollback();
                    return Result(
                        false,
                        "The queued case was leased concurrently.");
                }
                Execute(
                    connection,
                    transaction,
                    DialogueStore.UpsertFinalGauntletLeasesSql,
                    ("$run", runId), ("$case", nextId),
                    ("$controller", controllerId), ("$now", now),
                    ("$expires",
                        now + Math.Max(1, leaseSeconds)));
                transaction.Commit();
                return Result(
                    true, "", ("runId", runId),
                    ("caseInstanceId", nextId),
                    ("recovered", false),
                    ("correlationLedgerJson", "[]"));
            }
        }

        public static Dictionary<string, object> RecordResult(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId,
            string correlationId,
            bool passed,
            bool providerFailure,
            string error)
        {
            EnsureSchema(connection);
            bool singleExecutionStageB =
                IsSingleExecutionStageBCase(caseInstanceId);
            int attempt = (int)ScalarLong(
                connection,
                DialogueStore.CountFinalGauntletAttemptsSql,
                ("$run", runId), ("$case", caseInstanceId)) + 1;
            // Stage B semantic cases still execute once. Their bounded
            // infrastructure-only setup attempts are retained in the same
            // audit ledger, so the one terminal semantic result may follow as
            // attempt 4-6 without being mistaken for a provider retry.
            bool validStageBTerminalAttempt =
                singleExecutionStageB && attempt <= 6;
            if (!FinalGauntletStateRules.IsAttemptNumberValid(attempt)
                && !validStageBTerminalAttempt)
                return Result(false, "The case already exhausted all provider attempts.");
            if (!FinalGauntletStore.RecordAttempt(
                connection,
                runId,
                caseInstanceId,
                attempt,
                correlationId,
                passed ? "passed" : providerFailure
                    ? "provider_failure"
                    : "failed",
                validStageBTerminalAttempt))
                return Result(false, "The attempt or correlation was already recorded.");
            FinalGauntletStore.RecordAttemptError(
                connection,
                runId,
                caseInstanceId,
                attempt,
                error);
            if (providerFailure && attempt < 3
                && !singleExecutionStageB)
            {
                string appendCorrelationSql =
                    ReignPostgreSqlDialect.IsPostgreSql(connection)
                        ? DialogueStore.UpdateFinalGauntletLeasesSql3
                        : DialogueStore.UpdateFinalGauntletLeasesSql4;
                Execute(
                    connection,
                    appendCorrelationSql,
                    ("$correlation", correlationId),
                    ("$run", runId), ("$case", caseInstanceId));
                return Result(
                    true, "", ("state", "retrying_provider"),
                    ("attempt", attempt), ("error", error ?? string.Empty));
            }
            FinalGauntletCaseState terminal = passed
                ? FinalGauntletCaseState.Passed
                : providerFailure
                    ? FinalGauntletCaseState.ProviderExhausted
                    : FinalGauntletCaseState.Failed;
            if (!FinalGauntletStore.TryTransitionCase(
                connection,
                runId,
                caseInstanceId,
                FinalGauntletCaseState.Running,
                terminal))
                return Result(false, "The case is no longer running.");
            Execute(
                connection,
                DialogueStore.DeleteFinalGauntletLeasesSql,
                ("$run", runId), ("$case", caseInstanceId));
            AdvanceRunIfStageComplete(connection, runId);
            return Result(
                true, "", ("state", terminal.ToString()),
                ("attempt", attempt), ("error", error ?? string.Empty));
        }

        public static bool IsSingleExecutionStageBCase(
            string caseInstanceId)
        {
            return !string.IsNullOrWhiteSpace(caseInstanceId)
                && caseInstanceId.EndsWith(
                    "::stage-b", StringComparison.OrdinalIgnoreCase);
        }

        public static Dictionary<string, object> PromoteStageB(
            ReignDbConnection connection,
            string runId,
            List<FinalGauntletCaseDescriptor> stageBCases,
            string catalogFingerprint,
            string settingsFingerprint,
            string stateFingerprint)
        {
            EnsureSchema(connection);
            string state = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql4,
                ("$run", runId));
            if (state != "awaiting_stage_b"
                && state != "stage_a_failed")
                return Result(
                    false,
                    "Stage B requires a completed Stage A.",
                    ("state", state));
            if (ScalarLong(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql5,
                ("$run", runId)) != 0)
                return Result(false, "Stage B was already scheduled.", ("state", state));
            string baselineStateFingerprint = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql6,
                ("$run", runId));
            if (string.IsNullOrWhiteSpace(stateFingerprint)
                || !string.Equals(
                    baselineStateFingerprint,
                    stateFingerprint,
                    StringComparison.Ordinal))
                return Result(
                    false,
                    "The live campaign/clan census no longer matches the gauntlet baseline. Start a new run instead of reusing stale evidence.",
                    ("expectedStateFingerprint", baselineStateFingerprint),
                    ("actualStateFingerprint", stateFingerprint ?? string.Empty),
                    ("state", state));
            List<string> errors = FinalGauntletContracts.ValidateDescriptors(
                stageBCases,
                FinalGauntletContracts.CurrentFixtureSchemaVersion);
            if (errors.Count > 0)
                return Result(false, string.Join(" ", errors));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbTransaction transaction = connection.BeginTransaction())
            {
                foreach (FinalGauntletCaseDescriptor descriptor in stageBCases)
                    InsertStageBCase(connection, transaction, runId, descriptor, now);
                Execute(
                    connection,
                    transaction,
                    DialogueStore.UpdateFinalGauntletScheduleSql,
                    ("$catalog", catalogFingerprint ?? string.Empty),
                    ("$settings", settingsFingerprint ?? string.Empty),
                    ("$ts", now), ("$run", runId));
                Execute(
                    connection,
                    transaction,
                    DialogueStore.UpdateFinalGauntletRunsSql2,
                    ("$ts", now), ("$run", runId));
                transaction.Commit();
            }
            return Result(
                true, "", ("runId", runId),
                ("state", "running_stage_b"),
                ("scheduled", stageBCases.Count));
        }

        public static Dictionary<string, object> Cancel(
            ReignDbConnection connection,
            string runId)
        {
            EnsureSchema(connection);
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletScheduleSql2,
                ("$ts", now), ("$run", runId));
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletRunsSql3,
                ("$ts", now), ("$run", runId));
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletCasesSql3,
                ("$ts", now), ("$run", runId));
            Execute(
                connection,
                DialogueStore.DeleteFinalGauntletLeasesSql2,
                ("$run", runId));
            return Result(true, "", ("runId", runId), ("state", "cancelled"));
        }

        public static Dictionary<string, object> Interrupt(
            ReignDbConnection connection,
            string runId)
        {
            EnsureSchema(connection);
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletScheduleSql3,
                ("$run", runId));
            return Result(true, "", ("runId", runId), ("state", "interrupted"));
        }

        public static Dictionary<string, object> RequestPause(
            ReignDbConnection connection,
            string runId)
        {
            EnsureSchema(connection);
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletScheduleSql4,
                ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ("$run", runId));
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletRunsSql4,
                ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ("$run", runId));
            long running = ScalarLong(
                connection,
                DialogueStore.CountFinalGauntletCasesSql,
                ("$run", runId));
            return Result(
                true, "", ("runId", runId),
                ("state", "interrupted"),
                ("reconcilingActiveCases", running));
        }

        public static Dictionary<string, object> Resume(
            ReignDbConnection connection,
            string runId,
            string stateFingerprint,
            IReadOnlyCollection<string> supportedModes = null)
        {
            EnsureSchema(connection);
            string expectedStateFingerprint = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql6,
                ("$run", runId));
            if (string.IsNullOrWhiteSpace(stateFingerprint)
                || !string.Equals(
                    expectedStateFingerprint,
                    stateFingerprint,
                    StringComparison.Ordinal))
                return Result(
                    false,
                    "Resume rejected because the live campaign/clan census differs from the run baseline.",
                    ("expectedStateFingerprint", expectedStateFingerprint),
                    ("actualStateFingerprint", stateFingerprint ?? string.Empty),
                    ("state", "interrupted"));
            Dictionary<string, object> providerBudgetBackfill =
                FinalConversationGauntletProviderBudget
                    .BackfillAuditedProviderCalls(connection, runId);
            if (!Convert.ToBoolean(providerBudgetBackfill["ok"]))
                return Result(
                    false,
                    Convert.ToString(providerBudgetBackfill["error"])
                        ?? "Provider-budget backfill failed.",
                    ("state", "interrupted"),
                    ("providerBudgetBackfill", providerBudgetBackfill));
            Dictionary<string, object> modeReconciliation =
                ReconcileUnsupportedStageBFixtures(
                    connection,
                    runId,
                    supportedModes ?? new[]
                    {
                        "individual_chat", "party_chat", "social_event",
                        "wilderness_event", "correspondence"
                    });
            int infrastructureRequeued =
                RequeueInfrastructureOnlyStageBFailures(
                    connection, runId);
            string stage = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletRunsSql,
                ("$run", runId));
            string state = stage == FinalGauntletStage.ExhaustiveReview.ToString()
                ? "running_stage_b"
                : "running_stage_a";
            long pendingWork = ScalarLong(
                connection,
                DialogueStore.CountFinalGauntletCasesSql2,
                ("$run", runId));
            if (pendingWork > 0)
            {
                Execute(
                    connection,
                    DialogueStore.UpdateFinalGauntletScheduleSql5,
                    ("$state", state),
                    ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    ("$run", runId));
                Execute(
                    connection,
                    DialogueStore.UpdateFinalGauntletRunsSql5,
                    ("$state", state),
                    ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    ("$run", runId));
            }
            state = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql7,
                ("$run", runId));
            Dictionary<string, object> resumed = Result(
                true, "", ("runId", runId), ("state", state),
                ("infrastructureCasesRequeued", infrastructureRequeued));
            resumed["modeReconciliation"] = modeReconciliation;
            resumed["providerBudgetBackfill"] = providerBudgetBackfill;
            return resumed;
        }

        public static Dictionary<string, object>
            ReconcileUnsupportedStageBFixtures(
                ReignDbConnection connection,
                string runId,
                IReadOnlyCollection<string> supportedModes)
        {
            EnsureSchema(connection);
            HashSet<string> supported = new HashSet<string>(
                supportedModes ?? Array.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            if (supported.Contains("court_event"))
                return Result(
                    true, "", ("excluded", 0), ("replacements", 0),
                    ("queuedRemapped", 0));
            string fallback = new[]
                {
                    "social_event", "wilderness_event", "party_chat"
                }
                .FirstOrDefault(supported.Contains);
            if (string.IsNullOrWhiteSpace(fallback))
                return Result(
                    false,
                    "No implemented group-conversation mode is available "
                    + "to replace unsupported court-event fixtures.");

            List<Dictionary<string, object>> rows = QueryRows(
                connection,
                DialogueStore.SelectFinalGauntletCasesSql3,
                ("$run", runId));
            int excluded = 0;
            int replacements = 0;
            int queuedRemapped = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> row in rows)
            {
                string caseId = Text(row, "case_instance_id");
                string caseState = Text(row, "state");
                if (caseState.Equals(
                        FinalGauntletCaseState.Queued.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                {
                    Execute(
                        connection,
                        DialogueStore.UpdateFinalGauntletCasesSql4,
                        ("$mode", fallback),
                        ("$tags", AppendTag(
                            Text(row, "tags"),
                            "mode_remapped_from:court_event")),
                        ("$ts", now), ("$run", runId),
                        ("$case", caseId));
                    queuedRemapped++;
                    continue;
                }
                if (!caseState.Equals(
                        FinalGauntletCaseState.Failed.ToString(),
                        StringComparison.OrdinalIgnoreCase)
                    && !caseState.Equals(
                        FinalGauntletCaseState.ProviderExhausted.ToString(),
                        StringComparison.OrdinalIgnoreCase))
                    continue;
                string error = ScalarText(
                    connection,
                    DialogueStore.SelectFinalGauntletAttemptsSql,
                    ("$run", runId), ("$case", caseId));
                if (error.IndexOf(
                        "Unsupported or currently unavailable interaction mode",
                        StringComparison.OrdinalIgnoreCase) < 0
                    && error.IndexOf(
                        "Unsupported live-test mode",
                        StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                string replacementId = caseId.Substring(
                        0,
                        caseId.Length - "::stage-b".Length)
                    + "-SUPPORTED-"
                    + fallback.Replace('_', '-').ToUpperInvariant()
                    + "::stage-b";
                using (ReignDbTransaction transaction =
                    connection.BeginTransaction())
                {
                    Execute(
                        connection,
                        transaction,
                        DialogueStore.UpdateFinalGauntletCasesSql5,
                        ("$tags", AppendTag(
                            Text(row, "tags"),
                            "invalid_fixture:court_event_unimplemented")),
                        ("$ts", now), ("$run", runId),
                        ("$case", caseId));
                    Execute(
                        connection,
                        transaction,
                        DialogueStore.InsertOrIgnoreFinalGauntletCasesSql,
                        ("$replacement", replacementId),
                        ("$mode", fallback),
                        ("$tags", AppendTag(
                            AppendTag(
                                Text(row, "tags"),
                                "replacement_for:" + caseId),
                            "supported_mode:" + fallback)),
                        ("$ts", now), ("$run", runId),
                        ("$case", caseId));
                    transaction.Commit();
                }
                excluded++;
                replacements++;
            }
            return Result(
                true, "", ("excluded", excluded),
                ("replacements", replacements),
                ("queuedRemapped", queuedRemapped),
                ("replacementMode", fallback));
        }

        private static int RequeueInfrastructureOnlyStageBFailures(
            ReignDbConnection connection,
            string runId)
        {
            List<Dictionary<string, object>> rows = QueryRows(
                connection,
                DialogueStore.SelectFinalGauntletCasesSql4,
                ("$run", runId));
            int requeued = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (Dictionary<string, object> row in rows)
            {
                string error = Text(row, "error");
                string tags = Text(row, "tags");
                int attemptNumber = Convert.ToInt32(
                    row.TryGetValue("attempt_number", out object rawAttempt)
                        ? rawAttempt
                        : 0);
                bool transientSetupFailure = error.IndexOf(
                        "heartbeat remained offline",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || error.IndexOf(
                        "No fresh loaded-game heartbeat",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || error.IndexOf(
                        "required adult distinct heroes",
                        StringComparison.OrdinalIgnoreCase) >= 0
                    || error.IndexOf(
                        "task was canceled",
                        StringComparison.OrdinalIgnoreCase) >= 0;
                if (!transientSetupFailure
                    || ProductionExecutionStarted(
                        Text(row, "production_payload"))
                    // Permit at most five setup attempts. These do not consume
                    // the case's one semantic execution because the persisted
                    // envelope must explicitly prove liveRun is empty.
                    || attemptNumber >= 5)
                    continue;
                Execute(
                    connection,
                    DialogueStore.UpdateFinalGauntletCasesSql6,
                    ("$tags", AppendTag(
                        tags,
                        "infrastructure_retry_without_production_execution:attempt-"
                            + (attemptNumber + 1).ToString())),
                    ("$ts", now), ("$run", runId),
                    ("$case", Text(row, "case_instance_id")));
                requeued++;
            }
            return requeued;
        }

        private static bool ProductionExecutionStarted(
            string productionPayload)
        {
            if (string.IsNullOrWhiteSpace(productionPayload))
                return false;
            try
            {
                Dictionary<string, object> evidence =
                    new JavaScriptSerializer
                    {
                        MaxJsonLength = int.MaxValue,
                        RecursionLimit = 512
                    }.DeserializeObject(productionPayload)
                        as Dictionary<string, object>;
                if (evidence == null
                    || !evidence.TryGetValue("liveRun", out object raw))
                    return true;
                Dictionary<string, object> liveRun =
                    raw as Dictionary<string, object>;
                return liveRun == null || liveRun.Count > 0;
            }
            catch
            {
                // Ambiguous evidence fails closed. A fixture is requeued only
                // when its persisted evidence explicitly proves that no live
                // production run ever started.
                return true;
            }
        }

        private static string AppendTag(string tags, string tag)
        {
            List<string> values = (tags ?? string.Empty).Split(
                    new[] { '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries)
                .Select(value => value.Trim())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            if (!values.Contains(tag, StringComparer.OrdinalIgnoreCase))
                values.Add(tag);
            return string.Join("\n", values);
        }

        public static Dictionary<string, object> LoadSchedule(
            ReignDbConnection connection,
            string runId)
        {
            EnsureSchema(connection);
            return QueryOne(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql8,
                ("$run", runId ?? string.Empty));
        }

        private static void AdvanceRunIfStageComplete(
            ReignDbConnection connection,
            string runId)
        {
            long pauseRequested = ScalarLong(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql3,
                ("$run", runId));
            long runningCases = ScalarLong(
                connection,
                DialogueStore.CountFinalGauntletCasesSql,
                ("$run", runId));
            if (pauseRequested != 0 && runningCases == 0)
            {
                MarkPaused(connection, runId);
                return;
            }
            long remaining = ScalarLong(
                connection,
                DialogueStore.CountFinalGauntletCasesSql2,
                ("$run", runId));
            if (remaining != 0) return;
            long failures = ScalarLong(
                connection,
                DialogueStore.CountFinalGauntletCasesSql3,
                ("$run", runId));
            string current = ScalarText(
                connection,
                DialogueStore.SelectFinalGauntletScheduleSql4,
                ("$run", runId));
            string next = current == "running_stage_a"
                ? failures == 0 ? "awaiting_stage_b" : "stage_a_failed"
                : failures == 0 ? "completed" : "completed_with_failures";
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletScheduleSql6,
                ("$state", next),
                ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ("$run", runId));
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletRunsSql6,
                ("$state", next),
                ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                ("$run", runId));
        }

        private static void MarkPaused(
            ReignDbConnection connection,
            string runId)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletScheduleSql7,
                ("$ts", now), ("$run", runId));
            Execute(
                connection,
                DialogueStore.UpdateFinalGauntletRunsSql4,
                ("$ts", now), ("$run", runId));
        }

        private static void EnsureSchema(ReignDbConnection connection)
        {
            FinalGauntletStore.EnsureSchema(connection);
            Execute(
                connection,
                DialogueSchema.CreateFinalGauntletScheduleSql);
            Execute(
                connection,
                DialogueSchema.AlterFinalGauntletScheduleSql);
            Execute(
                connection,
                DialogueSchema.AlterFinalGauntletScheduleSql2);
        }

        private static void InsertStageBCase(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string runId,
            FinalGauntletCaseDescriptor descriptor,
            long now)
        {
            string stageBInstanceId =
                descriptor.CaseId + "::stage-b";
            Execute(
                connection,
                transaction,
                DialogueStore.InsertFinalGauntletCasesSql,
                ("$run", runId), ("$case", stageBInstanceId),
                ("$family", descriptor.Family),
                ("$evaluation", descriptor.EvaluationKind),
                ("$execution", descriptor.ExecutionKind),
                ("$mode", descriptor.Mode),
                ("$provider", descriptor.RequiresProvider ? 1 : 0),
                ("$game", descriptor.RequiresGame ? 1 : 0),
                ("$requirements", string.Join("\n", descriptor.RequirementIds)),
                ("$tags", string.Join("\n", descriptor.Tags)),
                ("$behavior", descriptor.BehavioralRequirement),
                ("$prohibitions", string.Join("\n", descriptor.HardProhibitions)),
                ("$prerequisites", string.Join("\n", descriptor.PrerequisiteCapabilities)),
                ("$evidence", string.Join("\n", descriptor.EvidenceNeeds)),
                ("$ts", now));
        }

        private static Dictionary<string, object> QueryOne(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            return QueryOne(
                connection, null, sql, parameters);
        }

        private static List<Dictionary<string, object>> QueryRows(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                Add(command, parameters);
                using (ReignDbDataReader reader = command.ExecuteReader())
                {
                    List<Dictionary<string, object>> rows =
                        new List<Dictionary<string, object>>();
                    while (reader.Read())
                    {
                        Dictionary<string, object> row =
                            new Dictionary<string, object>(
                                StringComparer.OrdinalIgnoreCase);
                        for (int index = 0;
                            index < reader.FieldCount;
                            index++)
                            row[reader.GetName(index)] =
                                reader.IsDBNull(index)
                                    ? null : reader.GetValue(index);
                        rows.Add(row);
                    }
                    return rows;
                }
            }
        }

        private static Dictionary<string, object> QueryOne(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                Add(command, parameters);
                using (ReignDbDataReader reader = command.ExecuteReader())
                {
                    Dictionary<string, object> row =
                        new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (!reader.Read()) return row;
                    for (int index = 0; index < reader.FieldCount; index++)
                        row[reader.GetName(index)] = reader.IsDBNull(index)
                            ? null : reader.GetValue(index);
                    return row;
                }
            }
        }

        private static string ScalarText(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                Add(command, parameters);
                return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
            }
        }

        private static long ScalarLong(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                Add(command, parameters);
                object value = command.ExecuteScalar();
                return value == null || value == DBNull.Value
                    ? 0L : Convert.ToInt64(value);
            }
        }

        private static void Execute(
            ReignDbConnection connection,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            Execute(connection, null, sql, parameters);
        }

        private static void Execute(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                Add(command, parameters);
                command.ExecuteNonQuery();
            }
        }

        private static int ExecuteCount(
            ReignDbConnection connection,
            ReignDbTransaction transaction,
            string sql,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    ReignPostgreSqlDialect.Normalize(connection, sql);
                Add(command, parameters);
                return command.ExecuteNonQuery();
            }
        }

        private static void Add(
            ReignDbCommand command,
            IEnumerable<(string Name, object Value)> parameters)
        {
            foreach ((string name, object value) in parameters)
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        private static string Text(
            Dictionary<string, object> row,
            string key)
        {
            return row != null && row.TryGetValue(key, out object value)
                ? Convert.ToString(value) ?? string.Empty
                : string.Empty;
        }

        private static Dictionary<string, object> Result(
            bool ok,
            string error,
            params (string Key, object Value)[] values)
        {
            Dictionary<string, object> result =
                new Dictionary<string, object>
                {
                    ["ok"] = ok,
                    ["error"] = error ?? string.Empty
                };
            foreach ((string key, object value) in values)
                result[key] = value;
            return result;
        }
    }
}
