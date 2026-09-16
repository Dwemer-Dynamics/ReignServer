using System;
using System.Collections.Generic;
using System.Reflection;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletProviderBudgetSelfTests()
        {
            List<Dictionary<string, object>> checks =
                new List<Dictionary<string, object>>();
            Action<string, bool, string> add = (id, passed, message) =>
                checks.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["passed"] = passed,
                    ["message"] = message
                });

            Type type = Type.GetType(
                "ReignBetaServer.FinalConversationGauntletProviderBudget");
            MethodInfo register = type?.GetMethod(
                "RegisterCorrelation",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo reserve = type?.GetMethod(
                "TryReservePhysicalDispatch",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo complete = type?.GetMethod(
                "CompletePhysicalDispatch",
                BindingFlags.Static | BindingFlags.Public);
            MethodInfo registered = type?.GetMethod(
                "IsRegisteredCorrelation",
                BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo backfill = type?.GetMethod(
                "BackfillAuditedProviderCalls",
                BindingFlags.Static | BindingFlags.NonPublic);
            add(
                "provider_budget_contract_exists",
                register != null && reserve != null && complete != null
                    && registered != null && backfill != null,
                "The final gauntlet exposes correlation registration, physical dispatch reservation, and completion contracts.");

            const string evidenceSql = @"SELECT case_instance_id,payload_json
FROM final_gauntlet_evidence
WHERE run_id=$run AND evidence_key='production_result'
ORDER BY case_instance_id;";
            using (NpgsqlConnection postgreSql = new NpgsqlConnection())
            {
                string normalized = ReignPostgreSqlDialect.Normalize(
                    postgreSql, evidenceSql);
                add(
                    "provider_budget_backfill_query_uses_postgresql_parameter_syntax",
                    normalized.Contains("run_id=@run")
                        && !normalized.Contains("run_id=$run"),
                    "The paused-run provider-audit backfill normalizes its parameter marker before PostgreSQL executes it.");
            }

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                FinalGauntletStore.EnsureSchema(connection);
                bool ledgerExists;
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*)
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_type='BASE TABLE'
  AND table_name='final_gauntlet_provider_calls';";
                    ledgerExists = Convert.ToInt64(
                        command.ExecuteScalar()) == 1;
                }
                add(
                    "provider_budget_ledger_schema_exists",
                    ledgerExists,
                    "The campaign database contains the durable physical provider-call ledger.");
            }

            if (register == null || reserve == null || complete == null
                || registered == null || backfill == null)
                return checks;

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                FinalGauntletStore.EnsureSchema(connection);
                ResetFinalGauntletProviderBudgetSelfTestCase(
                    connection, "run-audit", "case-audit");
                register.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-audit", "case-audit",
                        "corr-audit"
                    });
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES('run-audit','case-audit','production_result',4,@payload,1);";
                    command.Parameters.AddWithValue(
                        "payload",
                        "{\"sourceAuditEntries\":["
                        + "{\"auditId\":\"request-audit\","
                        + "\"correlationId\":\"live-corr-audit-02\","
                        + "\"phase\":\"llm.request\","
                        + "\"status\":\"started\","
                        + "\"data\":{\"requestType\":\"dialogue\","
                        + "\"model\":\"model-a\"}},"
                        + "{\"auditId\":\"response-audit\","
                        + "\"correlationId\":\"live-corr-audit-02\","
                        + "\"phase\":\"llm.response\","
                        + "\"status\":\"completed\",\"data\":{}}]}");
                    command.ExecuteNonQuery();
                }
                Dictionary<string, object> backfilled = backfill.Invoke(
                    null,
                    new object[] { connection, "run-audit" })
                    as Dictionary<string, object>;
                long auditedCalls;
                string auditedStatus;
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText = @"SELECT COUNT(*),MAX(status)
FROM final_gauntlet_provider_calls WHERE run_id='run-audit';";
                    using (ReignDbDataReader reader = command.ExecuteReader())
                    {
                        reader.Read();
                        auditedCalls = Convert.ToInt64(reader.GetValue(0));
                        auditedStatus = Convert.ToString(reader.GetValue(1));
                    }
                }
                add(
                    "audited_live_prefixed_calls_backfill_into_budget",
                    ReadBool(backfilled, "ok", false)
                        && ReadInt(backfilled, "backfilled", 0) == 1
                        && auditedCalls == 1
                        && auditedStatus == "completed",
                    "Existing production evidence is reconciled into the durable provider budget before a paused gauntlet resumes.");
            }

            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                FinalGauntletStore.EnsureSchema(connection);
                ResetFinalGauntletProviderBudgetSelfTestCase(
                    connection, "run-budget", "case-budget");
                register.Invoke(
                    null,
                    new object[]
                    {
                        connection, "run-budget", "case-budget",
                        "corr-budget"
                    });
                add(
                    "registered_correlation_enables_in_request_retries",
                    (bool)registered.Invoke(
                        null, new object[] { "corr-budget" })
                    && (bool)registered.Invoke(
                        null, new object[] { "corr-budget-repair" })
                    && (bool)registered.Invoke(
                        null, new object[] { "live-corr-budget-02" })
                    && (bool)registered.Invoke(
                        null, new object[]
                        {
                            "live-corr-budget-02-s1-t1-npc-hero_a"
                        })
                    && !(bool)registered.Invoke(
                        null, new object[] { "unrelated-correlation" }),
                    "The provider middleware recognizes both server and game-prefixed gauntlet correlations and keeps retries inside the same logical case execution.");
                bool firstFiveHundredAllowed = true;
                int lastOrdinal = 0;
                for (int attempt = 1; attempt <= 500; attempt++)
                {
                    Dictionary<string, object> result = reserve.Invoke(
                        null,
                        new object[]
                        {
                            connection, "corr-budget", "dialogue",
                            "model-a", attempt
                        }) as Dictionary<string, object>;
                    firstFiveHundredAllowed &=
                        ReadBool(result, "allowed", false);
                    lastOrdinal = ReadInt(result, "ordinal", 0);
                }
                Dictionary<string, object> denied = reserve.Invoke(
                    null,
                    new object[]
                    {
                        connection, "corr-budget", "dialogue",
                        "model-a", 501
                    }) as Dictionary<string, object>;
                add(
                    "provider_budget_allows_exactly_five_hundred",
                    firstFiveHundredAllowed
                        && lastOrdinal == 500
                        && !ReadBool(denied, "allowed", true)
                        && ReadInt(denied, "used", 0) == 500,
                    "Physical provider calls 1 through 500 are allowed and call 501 is refused before dispatch.");
            }

            return checks;
        }

        private static void ResetFinalGauntletProviderBudgetSelfTestCase(
            ReignDbConnection connection,
            string runId,
            string caseInstanceId)
        {
            Dictionary<string, object> parameters =
                new Dictionary<string, object>
                {
                    ["run"] = runId,
                    ["case"] = caseInstanceId
                };
            foreach (string table in new[]
            {
                "final_gauntlet_provider_calls",
                "final_gauntlet_provider_correlations",
                "final_gauntlet_evidence",
                "final_gauntlet_assertions",
                "final_gauntlet_attempts",
                "final_gauntlet_review_items"
            })
            {
                ExecuteSql(connection,
                    "DELETE FROM " + table + " WHERE run_id=$run;",
                    parameters);
            }
            ExecuteSql(connection,
                "DELETE FROM final_gauntlet_cases WHERE run_id=$run;",
                parameters);
            ExecuteSql(connection,
                "DELETE FROM final_gauntlet_runs WHERE run_id=$run;",
                parameters);
            ExecuteSql(connection, @"
INSERT INTO final_gauntlet_runs(
run_id,campaign_id,stage,build_version,state,created_ts,updated_ts)
VALUES($run,'self-test','stage-a','self-test','running',1,1);",
                parameters);
            ExecuteSql(connection, @"
INSERT INTO final_gauntlet_cases(
run_id,case_instance_id,family,evaluation_kind,execution_kind,mode,
requires_provider,requires_game,requirement_ids,tags,
behavioral_requirement,hard_prohibitions,prerequisite_capabilities,
evidence_needs,state,created_ts,updated_ts)
VALUES($run,$case,'self-test','deterministic','server','individual',
1,0,'[]','[]','self-test','[]','[]','[]','ready',1,1);",
                parameters);
        }
    }
}
