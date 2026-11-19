using System;
using System.Collections.Generic;
using Npgsql;

namespace ReignBetaServer
{
    internal static class FinalGauntletStore
    {
        internal static ReignDbConnection OpenControlConnection()
        {
            return ReignPostgreSqlStorage.OpenMetadataConnection();
        }

        internal static void EnsureSchema(ReignDbConnection connection)
        {
            Execute(connection, DialogueSchema.CreateFinalGauntletRunsSql);
            Execute(connection,
                DialogueSchema.AlterFinalGauntletAttemptsSql);
        }

        internal static void RecordAttemptError(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId,
            int attemptNumber,
            string error)
        {
            Execute(connection,
                DialogueStore.UpdateFinalGauntletAttemptsSql,
                null,
                ("$error", error ?? string.Empty),
                ("$run", runId),
                ("$case", caseInstanceId),
                ("$attempt", attemptNumber));
        }

        internal static void CreateRun(
            ReignDbConnection connection,
            string runId,
            string campaignId,
            FinalGauntletStage stage,
            string buildVersion,
            IEnumerable<FinalGauntletCaseDescriptor> cases)
        {
            EnsureSchema(connection);
            List<FinalGauntletCaseDescriptor> scheduled =
                new List<FinalGauntletCaseDescriptor>(
                    cases ?? Array.Empty<FinalGauntletCaseDescriptor>());
            List<string> contractErrors = FinalGauntletContracts.ValidateDescriptors(
                scheduled,
                FinalGauntletContracts.CurrentFixtureSchemaVersion);
            if (contractErrors.Count > 0)
                throw new ArgumentException(
                    "Invalid final-gauntlet schedule: "
                    + string.Join(" ", contractErrors));
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbTransaction transaction = connection.BeginTransaction())
            {
                Execute(
                    connection,
                    DialogueStore.InsertFinalGauntletRunsSql,
                    transaction,
                    ("$run", runId),
                    ("$campaign", campaignId),
                    ("$stage", stage.ToString()),
                    ("$build", buildVersion),
                    ("$ts", now));
                foreach (FinalGauntletCaseDescriptor descriptor in scheduled)
                {
                    Execute(
                        connection,
                        DialogueStore.InsertFinalGauntletCasesSql2,
                        transaction,
                        ("$run", runId),
                        ("$case", descriptor.CaseId),
                        ("$family", descriptor.Family),
                        ("$evaluation", descriptor.EvaluationKind),
                        ("$execution", descriptor.ExecutionKind),
                        ("$mode", descriptor.Mode),
                        ("$provider", descriptor.RequiresProvider ? 1 : 0),
                        ("$game", descriptor.RequiresGame ? 1 : 0),
                        ("$requirements", string.Join("\n", descriptor.RequirementIds ?? Array.Empty<string>())),
                        ("$tags", string.Join("\n", descriptor.Tags ?? Array.Empty<string>())),
                        ("$behavior", descriptor.BehavioralRequirement),
                        ("$prohibitions", string.Join("\n", descriptor.HardProhibitions ?? Array.Empty<string>())),
                        ("$prerequisites", string.Join("\n", descriptor.PrerequisiteCapabilities ?? Array.Empty<string>())),
                        ("$evidence", string.Join("\n", descriptor.EvidenceNeeds ?? Array.Empty<string>())),
                        ("$ts", now));
                }
                transaction.Commit();
            }
        }

        internal static bool TryTransitionCase(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId,
            FinalGauntletCaseState expected,
            FinalGauntletCaseState next)
        {
            if (!FinalGauntletStateRules.CanTransition(expected, next)) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText = ReignPostgreSqlDialect.Normalize(connection, DialogueStore.UpdateFinalGauntletCasesSql7);
                Add(command, "$next", next.ToString());
                Add(command, "$ts", now);
                Add(command, "$run", runId);
                Add(command, "$case", caseInstanceId);
                Add(command, "$expected", expected.ToString());
                return command.ExecuteNonQuery() == 1;
            }
        }

        internal static bool RecordAttempt(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId,
            int attemptNumber,
            string correlationId,
            string status,
            bool allowExtendedStageBSetupLedger = false)
        {
            if ((!FinalGauntletStateRules.IsAttemptNumberValid(attemptNumber)
                    && !(allowExtendedStageBSetupLedger
                        && attemptNumber >= 1
                        && attemptNumber <= 6))
                || string.IsNullOrWhiteSpace(correlationId))
                return false;
            try
            {
                Execute(
                    connection,
                    DialogueStore.InsertFinalGauntletAttemptsSql,
                    null,
                    ("$run", runId),
                    ("$case", caseInstanceId),
                    ("$attempt", attemptNumber),
                    ("$correlation", correlationId),
                    ("$status", status ?? string.Empty),
                    ("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
                return true;
            }
            catch (PostgresException ex) when (
                ex.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                return false;
            }
        }

        internal static Dictionary<string, object> LoadRun(
            ReignDbConnection connection,
            string runId)
        {
            EnsureSchema(connection);
            Dictionary<string, object> result = new Dictionary<string, object>();
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText = ReignPostgreSqlDialect.Normalize(connection,
                    DialogueStore.SelectFinalGauntletRunsSql2);
                Add(command, "$run", runId);
                using (ReignDbDataReader reader = command.ExecuteReader())
                {
                    if (!reader.Read()) return result;
                    result["runId"] = reader.GetString(0);
                    result["campaignId"] = reader.GetString(1);
                    result["stage"] = reader.GetString(2);
                    result["buildVersion"] = reader.GetString(3);
                    result["state"] = reader.GetString(4);
                }
            }
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.CommandText = ReignPostgreSqlDialect.Normalize(connection,
                    DialogueStore.SelectFinalGauntletAttemptsSql2);
                Add(command, "$run", runId);
                using (ReignDbDataReader reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        cases.Add(new Dictionary<string, object>
                        {
                            ["caseInstanceId"] = reader.GetString(0),
                            ["family"] = reader.GetString(1),
                            ["mode"] = reader.GetString(2),
                            ["state"] = reader.GetString(3),
                            ["attemptCount"] = Convert.ToInt32(
                                reader.GetValue(4))
                        });
                    }
                }
            }
            result["cases"] = cases;
            return result;
        }

        private static void Execute(
            ReignDbConnection connection,
            string sql,
            ReignDbTransaction transaction = null,
            params (string Name, object Value)[] parameters)
        {
            using (ReignDbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = ReignPostgreSqlDialect.Normalize(connection, sql);
                foreach ((string name, object value) in parameters)
                    Add(command, name, value);
                command.ExecuteNonQuery();
            }
        }

        private static void Add(ReignDbCommand command, string name, object value)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }
}
