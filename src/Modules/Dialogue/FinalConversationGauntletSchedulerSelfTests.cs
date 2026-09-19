using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletSchedulerSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id, ["passed"] = passed, ["message"] = message
                });
            Type type = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletScheduler");
            MethodInfo start = type?.GetMethod("StartStageA");
            MethodInfo lease = type?.GetMethod("LeaseNext");
            MethodInfo result = type?.GetMethod("RecordResult");
            MethodInfo promote = type?.GetMethod("PromoteStageB");
            MethodInfo cancel = type?.GetMethod("Cancel");
            MethodInfo loadSchedule = type?.GetMethod("LoadSchedule");
            MethodInfo singleExecutionStageB =
                type?.GetMethod("IsSingleExecutionStageBCase");
            MethodInfo reconcileModes =
                type?.GetMethod("ReconcileUnsupportedStageBFixtures");
            add(
                "scheduler_contracts_are_registered",
                start != null && lease != null && result != null
                    && promote != null && cancel != null
                    && loadSchedule != null
                    && singleExecutionStageB != null
                    && reconcileModes != null,
                "The two-stage scheduler exposes start, lease, result, promotion, cancellation, and safe status-read contracts.");
            if (start == null || lease == null || result == null
                || promote == null || cancel == null
                || loadSchedule == null
                || singleExecutionStageB == null
                || reconcileModes == null)
                return checks;

            add(
                "all_stage_b_cases_have_one_logical_execution",
                (bool)singleExecutionStageB.Invoke(
                    null, new object[] { "CON-001::stage-b" })
                && (bool)singleExecutionStageB.Invoke(
                    null, new object[] { "GAUNTLET-01::stage-b" })
                && (bool)singleExecutionStageB.Invoke(
                    null, new object[] { "LNG-001::stage-b" })
                && !(bool)singleExecutionStageB.Invoke(
                    null, new object[] { "CON-001" }),
                "Representative, final-scene, and long-horizon Stage B cases each execute once; provider delivery retries stay inside that correlated execution.");

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                ResetFinalGauntletSelfTestRuns(
                    connection,
                    "run-a",
                    "run-b",
                    "run-empty-state",
                    "run-wrong-baseline");
                Dictionary<string, object> missingSchedule =
                    loadSchedule.Invoke(
                        null,
                        new object[] { connection, "not-started" })
                    as Dictionary<string, object>;
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*)
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_type='BASE TABLE'
  AND table_name='final_gauntlet_schedule';";
                    add(
                        "status_read_initializes_scheduler_schema",
                        missingSchedule != null
                            && missingSchedule.Count == 0
                            && Convert.ToInt64(command.ExecuteScalar()) == 1,
                        "Reading status before the first run creates the scheduler-owned schema and returns an empty result.");
                }
                List<FinalGauntletCaseDescriptor> stageA =
                    new List<FinalGauntletCaseDescriptor>
                    {
                        FinalGauntletSchedulerDescriptor("A-1", true),
                        FinalGauntletSchedulerDescriptor("A-2", false)
                    };
                Dictionary<string, object> started = start.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-a", "campaign-a", "build-a",
                        stageA, "fingerprint-a", "state-a",
                        "ConvTest"
                    }) as Dictionary<string, object>;
                Dictionary<string, object> missingFingerprint =
                    start.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-empty-state",
                            "campaign-empty-state", "build-a",
                            stageA, "fingerprint-a", "",
                            "ConvTest"
                        }) as Dictionary<string, object>;
                Dictionary<string, object> unprotectedBaseline =
                    start.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-wrong-baseline",
                            "campaign-wrong-baseline", "build-a",
                            stageA, "fingerprint-a", "state-a",
                            "BaseOne"
                        }) as Dictionary<string, object>;
                Dictionary<string, object> duplicate = start.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-b", "campaign-a", "build-a",
                        stageA, "fingerprint-a", "state-a",
                        "ConvTest"
                    }) as Dictionary<string, object>;
                add(
                    "single_active_run_is_enforced",
                    ReadBool(started, "ok", false)
                        && !ReadBool(duplicate, "ok", true),
                    "Only one active final gauntlet may own a campaign.");
                add(
                    "baseline_state_fingerprint_is_required",
                    !ReadBool(missingFingerprint, "ok", true)
                        && ReadString(
                            missingFingerprint, "error", "")
                            .IndexOf(
                                "fingerprint",
                                StringComparison.OrdinalIgnoreCase) >= 0,
                    "A gauntlet cannot start without an authoritative campaign/clan census fingerprint.");
                add(
                    "convtest_is_the_only_gauntlet_baseline",
                    !ReadBool(unprotectedBaseline, "ok", true)
                        && ReadString(
                            unprotectedBaseline,
                            "error",
                            "").IndexOf(
                                "ConvTest",
                                StringComparison.OrdinalIgnoreCase)
                            >= 0,
                    "The gauntlet cannot repurpose BaseOne, BaseTwo, BaseThree, or another save as its baseline.");
                Dictionary<string, object> persistedSchedule =
                    loadSchedule.Invoke(
                        null,
                        new object[] { connection, "run-a" })
                    as Dictionary<string, object>;
                add(
                    "baseline_save_name_is_persisted",
                    ReadString(
                        persistedSchedule,
                        "baseline_save_name",
                        "") == "ConvTest",
                    "Run status preserves the immutable baseline identity across pause and resume.");

                Dictionary<string, object> firstLease = lease.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-a", "controller-a", 1000L, 30L
                    }) as Dictionary<string, object>;
                Dictionary<string, object> recoveredLease = lease.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-a", "controller-b", 1031L, 30L
                    }) as Dictionary<string, object>;
                add(
                    "expired_lease_recovers_same_case_instance",
                    ReadString(firstLease, "caseInstanceId", "")
                        == ReadString(recoveredLease, "caseInstanceId", "")
                        && ReadBool(recoveredLease, "recovered", false),
                    "Lease recovery resumes the same case instance and correlation ledger.");

                string caseId = ReadString(firstLease, "caseInstanceId", "");
                for (int attempt = 1; attempt <= 3; attempt++)
                    result.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-a", caseId,
                            "corr-" + attempt, false, true,
                            "provider failure " + attempt
                        });
                Dictionary<string, object> run =
                    FinalGauntletStore.LoadRun(connection, "run-a");
                add(
                    "provider_failure_retries_then_exhausts",
                    ReadDictionaryList(run, "cases").Find(row =>
                        ReadString(row, "caseInstanceId", "") == caseId) is
                        Dictionary<string, object> exhausted
                        && ReadString(exhausted, "state", "")
                            == FinalGauntletCaseState.ProviderExhausted.ToString()
                        && ReadLong(exhausted, "attemptCount", 0) == 3,
                    "Provider faults receive three recorded attempts and become provider_exhausted only after the third failure.");

                Dictionary<string, object> secondLease = lease.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-a", "controller-a", 1100L, 30L
                    }) as Dictionary<string, object>;
                result.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-a",
                        ReadString(secondLease, "caseInstanceId", ""),
                        "corr-success", true, false, ""
                    });
                Dictionary<string, object> continuedPromotion = promote.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-a",
                        new List<FinalGauntletCaseDescriptor>
                        {
                            FinalGauntletSchedulerDescriptor("B-1", false)
                        },
                        "catalog-a", "settings-a", "state-a"
                    }) as Dictionary<string, object>;
                add(
                    "stage_b_preserves_stage_a_failures",
                    ReadBool(continuedPromotion, "ok", false)
                        && ReadString(continuedPromotion, "state", "")
                            == "running_stage_b"
                        && ReadLong(continuedPromotion, "scheduled", 0) == 1,
                    "A completed Stage A promotes into the one-pass final ledger even when earlier failures remain recorded.");
            }

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                ResetFinalGauntletSelfTestRuns(
                    connection, "run-one-shot");
                start.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-one-shot", "campaign-one-shot",
                        "build-a",
                        new List<FinalGauntletCaseDescriptor>
                        {
                            FinalGauntletSchedulerDescriptor(
                                "REP-001::stage-b", true)
                        },
                        "fingerprint-one-shot", "state-one-shot",
                        "ConvTest"
                    });
                Dictionary<string, object> oneShotLease =
                    lease.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-one-shot",
                            "controller-one-shot", 2000L, 30L
                        }) as Dictionary<string, object>;
                Dictionary<string, object> oneShotResult =
                    result.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-one-shot",
                            ReadString(
                                oneShotLease, "caseInstanceId", ""),
                            "corr-one-shot", false, true,
                            "provider delivery exhausted"
                        }) as Dictionary<string, object>;
                Dictionary<string, object> oneShotRun =
                    FinalGauntletStore.LoadRun(
                        connection, "run-one-shot");
                Dictionary<string, object> oneShotCase =
                    ReadDictionaryList(oneShotRun, "cases")
                        .Find(row => ReadString(
                            row, "caseInstanceId", "")
                            == "REP-001::stage-b");
                Dictionary<string, object> noSecondLease =
                    lease.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-one-shot",
                            "controller-one-shot", 2031L, 30L
                        }) as Dictionary<string, object>;
                add(
                    "stage_b_provider_exhaustion_cannot_replay_case",
                    ReadString(oneShotResult, "state", "")
                        == FinalGauntletCaseState.ProviderExhausted
                            .ToString()
                        && ReadString(oneShotCase, "state", "")
                            == FinalGauntletCaseState.ProviderExhausted
                                .ToString()
                        && ReadLong(oneShotCase, "attemptCount", 0) == 1
                        && !ReadBool(noSecondLease, "ok", true),
                    "After the correlated in-request provider attempts are exhausted, a Stage B case becomes terminal after one logical attempt and cannot be leased again.");
            }

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                ResetFinalGauntletSelfTestRuns(
                    connection, "run-mode-reconcile");
                FinalGauntletCaseDescriptor invalidCourt =
                    FinalGauntletSchedulerDescriptor(
                        "LIVE-GROUP-003::stage-b", true);
                invalidCourt.Mode = "court_event";
                start.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-mode-reconcile", "campaign-mode",
                        "build-a",
                        new List<FinalGauntletCaseDescriptor>
                        {
                            invalidCourt,
                            FinalGauntletSchedulerDescriptor(
                                "PENDING::stage-b", false)
                        },
                        "fingerprint-mode", "state-mode", "ConvTest"
                    });
                Dictionary<string, object> invalidLease = lease.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-mode-reconcile",
                        "controller-mode", 3000L, 30L
                    }) as Dictionary<string, object>;
                result.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-mode-reconcile",
                        ReadString(invalidLease, "caseInstanceId", ""),
                        "corr-invalid-mode", false, false,
                        "Unsupported or currently unavailable interaction mode."
                    });
                Dictionary<string, object> reconciled =
                    reconcileModes.Invoke(
                        null,
                        new object[]
                        {
                            connection, "run-mode-reconcile",
                            new[]
                            {
                                "individual_chat", "party_chat",
                                "social_event", "wilderness_event"
                            }
                        }) as Dictionary<string, object>;
                Dictionary<string, object> reconciledRun =
                    FinalGauntletStore.LoadRun(
                        connection, "run-mode-reconcile");
                List<Dictionary<string, object>> reconciledCases =
                    ReadDictionaryList(reconciledRun, "cases");
                add(
                    "unsupported_court_fixture_is_excluded_and_replaced",
                    ReadBool(reconciled, "ok", false)
                        && ReadInt(reconciled, "excluded", 0) == 1
                        && reconciledCases.Any(row =>
                            ReadString(row, "caseInstanceId", "")
                                == "LIVE-GROUP-003::stage-b"
                            && ReadString(row, "state", "")
                                == "NotApplicable")
                        && reconciledCases.Any(row =>
                            ReadString(row, "caseInstanceId", "")
                                .Contains("SUPPORTED-SOCIAL-EVENT")
                            && ReadString(row, "mode", "")
                                == "social_event"
                            && ReadString(row, "state", "")
                                == "Queued"),
                    "An attempted fixture for an unimplemented court adapter remains auditable but is excluded and replaced once on a supported group adapter.");
            }
            return checks;
        }

        private static void ResetFinalGauntletSelfTestRuns(
            ReignDbConnection connection,
            params string[] runIds)
        {
            FinalGauntletStore.EnsureSchema(connection);
            foreach (string runId in runIds ?? Array.Empty<string>())
            {
                ExecuteSql(
                    connection,
                    "DELETE FROM final_gauntlet_runs WHERE run_id=$run;",
                    new Dictionary<string, object> { ["run"] = runId });
            }
        }

        private static FinalGauntletCaseDescriptor
            FinalGauntletSchedulerDescriptor(
                string id,
                bool provider)
        {
            return new FinalGauntletCaseDescriptor
            {
                CaseId = id,
                Family = "fixture",
                EvaluationKind = "hard",
                ExecutionKind = "offline",
                Mode = "individual_chat",
                RequiresProvider = provider,
                RequiresGame = false,
                RequirementIds = new[] { id },
                Tags = new[] { "fixture" },
                BehavioralRequirement = "Fixture requirement.",
                HardProhibitions = new[] { "No unsafe mutation." },
                PrerequisiteCapabilities = new[] { "fixture" },
                EvidenceNeeds = new[] { "fixture evidence" }
            };
        }
    }
}
