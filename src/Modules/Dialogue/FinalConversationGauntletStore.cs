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
            Execute(connection, @"
CREATE TABLE IF NOT EXISTS final_gauntlet_runs(
    run_id TEXT PRIMARY KEY,
    campaign_id TEXT NOT NULL,
    stage TEXT NOT NULL,
    build_version TEXT NOT NULL,
    state TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS final_gauntlet_cases(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    family TEXT NOT NULL,
    evaluation_kind TEXT NOT NULL,
    execution_kind TEXT NOT NULL,
    mode TEXT NOT NULL,
    requires_provider INTEGER NOT NULL,
    requires_game INTEGER NOT NULL,
    requirement_ids TEXT NOT NULL,
    tags TEXT NOT NULL,
    behavioral_requirement TEXT NOT NULL,
    hard_prohibitions TEXT NOT NULL,
    prerequisite_capabilities TEXT NOT NULL,
    evidence_needs TEXT NOT NULL,
    state TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    updated_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_attempts(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    attempt_number INTEGER NOT NULL,
    correlation_id TEXT NOT NULL,
    status TEXT NOT NULL,
    error TEXT NOT NULL DEFAULT '',
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id,attempt_number),
    UNIQUE(run_id,correlation_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_assertions(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    assertion_id TEXT NOT NULL,
    passed INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id,assertion_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_evidence(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    evidence_key TEXT NOT NULL,
    schema_version INTEGER NOT NULL,
    payload_json TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,case_instance_id,evidence_key));
CREATE TABLE IF NOT EXISTS final_gauntlet_review_items(
    run_id TEXT NOT NULL,
    review_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    blinded_payload_json TEXT NOT NULL,
    answer_key_json TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,review_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_provider_correlations(
    run_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    created_ts INTEGER NOT NULL,
    PRIMARY KEY(run_id,correlation_id));
CREATE TABLE IF NOT EXISTS final_gauntlet_provider_calls(
    run_id TEXT NOT NULL,
    provider_call_id TEXT NOT NULL,
    case_instance_id TEXT NOT NULL,
    correlation_id TEXT NOT NULL,
    request_type TEXT NOT NULL,
    model TEXT NOT NULL,
    physical_attempt INTEGER NOT NULL,
    ordinal INTEGER NOT NULL,
    status TEXT NOT NULL,
    started_ts INTEGER NOT NULL,
    completed_ts INTEGER NOT NULL DEFAULT 0,
    error TEXT NOT NULL DEFAULT '',
    PRIMARY KEY(run_id,provider_call_id),
    UNIQUE(run_id,ordinal));");
            Execute(connection,
                "ALTER TABLE final_gauntlet_attempts ADD COLUMN IF NOT EXISTS error TEXT NOT NULL DEFAULT '';");
        }

        internal static void RecordAttemptError(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId,
            int attemptNumber,
            string error)
        {
            Execute(connection,
                @"UPDATE final_gauntlet_attempts SET error=$error
WHERE run_id=$run AND case_instance_id=$case AND attempt_number=$attempt;",
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
                    @"INSERT INTO final_gauntlet_runs(
run_id,campaign_id,stage,build_version,state,created_ts,updated_ts)
VALUES($run,$campaign,$stage,$build,'planned',$ts,$ts);",
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
                        @"INSERT INTO final_gauntlet_cases(
run_id,case_instance_id,family,evaluation_kind,execution_kind,mode,
requires_provider,requires_game,requirement_ids,tags,behavioral_requirement,
hard_prohibitions,prerequisite_capabilities,evidence_needs,state,created_ts,updated_ts)
VALUES($run,$case,$family,$evaluation,$execution,$mode,$provider,$game,
$requirements,$tags,$behavior,$prohibitions,$prerequisites,$evidence,'Planned',$ts,$ts);",
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
                command.CommandText = ReignPostgreSqlDialect.Normalize(connection, @"UPDATE final_gauntlet_cases
SET state=$next,updated_ts=$ts
WHERE run_id=$run AND case_instance_id=$case AND state=$expected;");
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
                    @"INSERT INTO final_gauntlet_attempts(
run_id,case_instance_id,attempt_number,correlation_id,status,created_ts)
VALUES($run,$case,$attempt,$correlation,$status,$ts);",
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
                    @"SELECT run_id,campaign_id,stage,build_version,state
FROM final_gauntlet_runs WHERE run_id=$run LIMIT 1;");
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
                    @"SELECT c.case_instance_id,c.family,c.mode,c.state,
(SELECT COUNT(*) FROM final_gauntlet_attempts a
 WHERE a.run_id=c.run_id AND a.case_instance_id=c.case_instance_id)
FROM final_gauntlet_cases c
WHERE c.run_id=$run
ORDER BY c.created_ts,c.case_instance_id;");
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
