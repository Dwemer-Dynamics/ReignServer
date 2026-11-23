using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueSelfTestStore
    {
        internal static List<Dictionary<string, object>> SelectStatement3(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT current_schema() AS schema;", parameters, transaction);
        }

        internal static void UpdateGauntletRollbackProbe(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"UPDATE gauntlet_rollback_probe
SET value='after' WHERE id='state';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountFinalGauntletRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM final_gauntlet_runs WHERE run_id=$run;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountFinalGauntletProviderCalls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM final_gauntlet_provider_calls WHERE run_id=$run;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectGauntletRollbackProbe(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT value FROM gauntlet_rollback_probe
WHERE id='state';", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> CountTables(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT COUNT(*) AS count
FROM information_schema.tables
WHERE table_schema=current_schema()
AND table_name LIKE 'final_gauntlet_%';", parameters, transaction);
        }

        internal static void DeleteFinalGauntletRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"DELETE FROM final_gauntlet_runs
WHERE run_id=$run;", parameters, transaction);
        }

        internal const string InsertFinalGauntletEvidenceSql = @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES('pause-heartbeat-run','LIVE-SINGLE-031::stage-b',
'production_result',4,'{""liveRun"":{}}',1);";

        internal const string UpdateFinalGauntletRunsSql = @"UPDATE final_gauntlet_runs
SET stage='ExhaustiveReview',state='completed_with_failures'
WHERE run_id='pause-heartbeat-run';
UPDATE final_gauntlet_schedule
SET state='completed_with_failures',pause_requested=0
WHERE run_id='pause-heartbeat-run';";

        internal const string SelectFinalGauntletAttemptsSql = @"SELECT error FROM final_gauntlet_attempts
WHERE run_id='run-a' AND case_instance_id='CON-001'
AND attempt_number=1;";

        internal static void DeleteDynamic(ReignDbConnection connection, object table, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM " + table + " WHERE run_id=$run;", parameters, transaction);
        }

        internal static void DeleteFinalGauntletCases(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM final_gauntlet_cases WHERE run_id=$run;", parameters, transaction);
        }

        internal static void DeleteFinalGauntletRuns2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM final_gauntlet_runs WHERE run_id=$run;", parameters, transaction);
        }

        internal static void InsertFinalGauntletRuns(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO final_gauntlet_runs(
run_id,campaign_id,stage,build_version,state,created_ts,updated_ts)
VALUES($run,'self-test','stage-a','self-test','running',1,1);", parameters, transaction);
        }

        internal static void InsertFinalGauntletCases(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"
INSERT INTO final_gauntlet_cases(
run_id,case_instance_id,family,evaluation_kind,execution_kind,mode,
requires_provider,requires_game,requirement_ids,tags,
behavioral_requirement,hard_prohibitions,prerequisite_capabilities,
evidence_needs,state,created_ts,updated_ts)
VALUES($run,$case,'self-test','deterministic','server','individual',
1,0,'[]','[]','self-test','[]','[]','[]','ready',1,1);", parameters, transaction);
        }

        internal const string SelectFinalGauntletEvidenceSql = @"SELECT case_instance_id,payload_json
FROM final_gauntlet_evidence
WHERE run_id=$run AND evidence_key='production_result'
ORDER BY case_instance_id;";

        internal const string CountTablesSql = @"SELECT COUNT(*)
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_type='BASE TABLE'
  AND table_name='final_gauntlet_provider_calls';";

        internal const string InsertFinalGauntletEvidenceSql2 = @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES('run-audit','case-audit','production_result',4,@payload,1);";

        internal const string CountFinalGauntletProviderCallsSql = @"SELECT COUNT(*),MAX(status)
FROM final_gauntlet_provider_calls WHERE run_id='run-audit';";

        internal static void InsertFinalGauntletAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO final_gauntlet_assertions(
run_id,case_instance_id,assertion_id,passed,payload_json,created_ts)
VALUES($run,'ADV-001','secret_leak',0,'{}',$ts);", parameters, transaction);
        }

        internal static void InsertFinalGauntletEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES($run,'ADV-001','complete',3,$payload,$ts);", parameters, transaction);
        }

        internal static void InsertFinalGauntletProviderCalls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO final_gauntlet_provider_calls(
run_id,provider_call_id,case_instance_id,correlation_id,request_type,
model,physical_attempt,ordinal,status,started_ts,completed_ts,error)
VALUES($run,'pc-1','ADV-001','corr-1','dialogue','model-a',1,1,
'completed',$ts,$ts,'');", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletReviewItems(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT review_id FROM final_gauntlet_review_items WHERE run_id=$run;", parameters, transaction);
        }

        internal const string CountTablesSql2 = @"SELECT COUNT(*)
FROM information_schema.tables
WHERE table_schema=current_schema()
  AND table_type='BASE TABLE'
  AND table_name='final_gauntlet_schedule';";
    }
}
