using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>> RunFinalConversationGauntletModelSelfTests()
        {
            List<Dictionary<string, object>> checks = new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["passed"] = passed,
                    ["message"] = message
                });

            Type descriptor = typeof(Program).Assembly.GetType(
                "ReignBetaServer.FinalGauntletCaseDescriptor",
                false);
            add(
                "case_descriptor_contract_exists",
                descriptor != null,
                "The final gauntlet exposes a stable scheduled-case descriptor contract.");

            Type rules = typeof(Program).Assembly.GetType(
                "ReignBetaServer.FinalGauntletStateRules",
                false);
            MethodInfo canTransition = rules?.GetMethod(
                "CanTransition",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo isTerminal = rules?.GetMethod(
                "IsTerminal",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo isAttemptValid = rules?.GetMethod(
                "IsAttemptNumberValid",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            bool transitionsValid = canTransition != null
                && (bool)canTransition.Invoke(null, new object[]
                {
                    FinalGauntletCaseState.Planned,
                    FinalGauntletCaseState.Queued
                })
                && !(bool)canTransition.Invoke(null, new object[]
                {
                    FinalGauntletCaseState.Passed,
                    FinalGauntletCaseState.Running
                });
            add(
                "case_state_transitions_are_forward_only",
                transitionsValid,
                "Case states advance through the run and terminal cases cannot be reopened.");
            add(
                "terminal_states_include_provider_exhaustion",
                isTerminal != null
                    && (bool)isTerminal.Invoke(
                        null,
                        new object[] { FinalGauntletCaseState.ProviderExhausted }),
                "Provider exhaustion is a terminal case failure, not a silent skip.");
            add(
                "provider_attempts_are_bounded",
                isAttemptValid != null
                    && (bool)isAttemptValid.Invoke(null, new object[] { 1 })
                    && (bool)isAttemptValid.Invoke(null, new object[] { 3 })
                    && !(bool)isAttemptValid.Invoke(null, new object[] { 4 }),
                "A case accepts only delivery attempts one through three.");

            Type contracts = typeof(Program).Assembly.GetType(
                "ReignBetaServer.FinalGauntletContracts",
                false);
            MethodInfo validateDescriptors = contracts?.GetMethod(
                "ValidateDescriptors",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            FinalGauntletCaseDescriptor validDescriptor =
                new FinalGauntletCaseDescriptor
                {
                    CaseId = "CON-001",
                    Family = "output_contract",
                    EvaluationKind = "hard",
                    ExecutionKind = "live",
                    Mode = "individual_chat",
                    RequiresProvider = true,
                    RequiresGame = true,
                    RequirementIds = new[] { "CON-001" },
                    Tags = new[] { "stage_a" },
                    BehavioralRequirement = "Produce one valid production response.",
                    HardProhibitions = new[] { "Do not accept malformed output." },
                    PrerequisiteCapabilities = new[] { "dialogue_response" },
                    EvidenceNeeds = new[] { "raw_and_parsed_response" }
                };
            bool descriptorsValidated = false;
            bool futureSchemaRejected = false;
            if (validateDescriptors != null)
            {
                object validResult = validateDescriptors.Invoke(
                    null,
                    new object[] { new[] { validDescriptor }, 1 });
                object duplicateResult = validateDescriptors.Invoke(
                    null,
                    new object[] { new[] { validDescriptor, validDescriptor }, 1 });
                FinalGauntletCaseDescriptor missingRequirements =
                    new FinalGauntletCaseDescriptor
                    {
                        CaseId = "CON-002",
                        Family = "output_contract",
                        EvaluationKind = "hard",
                        ExecutionKind = "live",
                        Mode = "individual_chat"
                    };
                object incompleteResult = validateDescriptors.Invoke(
                    null,
                    new object[] { new[] { missingRequirements }, 1 });
                object futureResult = validateDescriptors.Invoke(
                    null,
                    new object[] { new[] { validDescriptor }, 2 });
                descriptorsValidated =
                    ReadEnumerableCount(validResult) == 0
                    && ReadEnumerableCount(duplicateResult) > 0
                    && ReadEnumerableCount(incompleteResult) > 0;
                futureSchemaRejected = ReadEnumerableCount(futureResult) > 0;
            }
            add(
                "case_descriptors_are_validated_before_scheduling",
                descriptorsValidated,
                "Case IDs are unique and required fixture semantics cannot be omitted.");
            add(
                "future_fixture_schema_is_rejected",
                futureSchemaRejected,
                "Unsupported future fixture schemas fail closed instead of being guessed.");

            string campaignId = "final-gauntlet-store-self-test-" + Guid.NewGuid().ToString("N");
            try
            {
                Type store = typeof(Program).Assembly.GetType(
                    "ReignBetaServer.FinalGauntletStore",
                    false);
                MethodInfo ensureSchema = store?.GetMethod(
                    "EnsureSchema",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo createRun = store?.GetMethod(
                    "CreateRun",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo transition = store?.GetMethod(
                    "TryTransitionCase",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo recordAttempt = store?.GetMethod(
                    "RecordAttempt",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                MethodInfo loadRun = store?.GetMethod(
                    "LoadRun",
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                bool persistenceValid = false;
                if (ensureSchema != null
                    && createRun != null
                    && transition != null
                    && recordAttempt != null
                    && loadRun != null)
                {
                    using (ReignDbConnection connection = OpenCampaignConnection(campaignId))
                    {
                        ensureSchema.Invoke(null, new object[] { connection });
                        FinalGauntletCaseDescriptor scheduled =
                            new FinalGauntletCaseDescriptor
                            {
                                CaseId = "CON-001",
                                Family = "output_contract",
                                EvaluationKind = "hard",
                                ExecutionKind = "live",
                                Mode = "individual_chat",
                                RequiresProvider = true,
                                RequiresGame = true,
                                RequirementIds = new[] { "CON-001" },
                                Tags = new[] { "stage_a" },
                                BehavioralRequirement = "Produce one valid production response.",
                                HardProhibitions = new[] { "Do not accept malformed output." },
                                PrerequisiteCapabilities = new[] { "dialogue_response" },
                                EvidenceNeeds = new[] { "raw_and_parsed_response" }
                            };
                        createRun.Invoke(
                            null,
                            new object[]
                            {
                                connection,
                                "run-a",
                                campaignId,
                                FinalGauntletStage.BoundedQualification,
                                "build-a",
                                new[] { scheduled }
                            });
                        bool queued = (bool)transition.Invoke(
                            null,
                            new object[]
                            {
                                connection,
                                "run-a",
                                "CON-001",
                                FinalGauntletCaseState.Planned,
                                FinalGauntletCaseState.Queued
                            });
                        bool staleRejected = !(bool)transition.Invoke(
                            null,
                            new object[]
                            {
                                connection,
                                "run-a",
                                "CON-001",
                                FinalGauntletCaseState.Planned,
                                FinalGauntletCaseState.Running
                            });
                        bool attempt1 = (bool)recordAttempt.Invoke(
                            null,
                            new object[] { connection, "run-a", "CON-001", 1, "corr-1", "failed", false });
                        FinalGauntletStore.RecordAttemptError(
                            connection,
                            "run-a",
                            "CON-001",
                            1,
                            "fixture failure evidence");
                        bool attempt2 = (bool)recordAttempt.Invoke(
                            null,
                            new object[] { connection, "run-a", "CON-001", 2, "corr-2", "failed", false });
                        bool attempt3 = (bool)recordAttempt.Invoke(
                            null,
                            new object[] { connection, "run-a", "CON-001", 3, "corr-3", "recovered", false });
                        bool fourthRejected = !(bool)recordAttempt.Invoke(
                            null,
                            new object[] { connection, "run-a", "CON-001", 4, "corr-4", "failed", false });
                        bool duplicateRejected = !(bool)recordAttempt.Invoke(
                            null,
                            new object[] { connection, "run-a", "CON-001", 3, "corr-3b", "failed", false });
                        Dictionary<string, object> loaded =
                            loadRun.Invoke(
                                null,
                                new object[] { connection, "run-a" })
                            as Dictionary<string, object>;
                        List<Dictionary<string, object>> cases =
                            ReadDictionaryList(loaded, "cases");
                        Dictionary<string, object> loadedCase = cases.FirstOrDefault();
                        string recordedError = "";
                        using (ReignDbCommand errorCommand =
                            connection.CreateCommand())
                        {
                            errorCommand.CommandText =
                                @"SELECT error FROM final_gauntlet_attempts
WHERE run_id='run-a' AND case_instance_id='CON-001'
AND attempt_number=1;";
                            recordedError = Convert.ToString(
                                errorCommand.ExecuteScalar());
                        }
                        persistenceValid = queued
                            && staleRejected
                            && attempt1
                            && attempt2
                            && attempt3
                            && fourthRejected
                            && duplicateRejected
                            && recordedError == "fixture failure evidence"
                            && cases.Count == 1
                            && ReadString(loadedCase, "state", "") == "Queued"
                            && ReadInt(loadedCase, "attemptCount", 0) == 3;
                    }
                }
                add(
                    "run_store_is_idempotent_and_persistent",
                    persistenceValid,
                    "The run ledger preserves scheduled identity, compare-and-set state, and three unique delivery attempts.");
            }
            finally
            {
                TryDeleteDirectory(CampaignDirectory(campaignId));
            }

            return checks;
        }

        private static int ReadEnumerableCount(object value)
        {
            if (!(value is System.Collections.IEnumerable enumerable)) return -1;
            int count = 0;
            foreach (object ignored in enumerable)
                count++;
            return count;
        }
    }
}
