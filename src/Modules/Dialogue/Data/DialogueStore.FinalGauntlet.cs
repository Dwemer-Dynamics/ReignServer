using System;
using System.Collections.Generic;

namespace ReignBetaServer
{
    internal static partial class DialogueStore
    {
        internal static List<Dictionary<string, object>> SelectFinalGauntletCases(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM final_gauntlet_cases
WHERE run_id=$run AND case_instance_id=$case LIMIT 1;", parameters, transaction);
        }

        internal static void UpsertFinalGauntletEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES($run,$case,'production_result',4,$payload,$ts)
ON CONFLICT(run_id,case_instance_id,evidence_key) DO UPDATE SET
schema_version=excluded.schema_version,
payload_json=excluded.payload_json,
created_ts=excluded.created_ts;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletAttempts(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM final_gauntlet_attempts
WHERE run_id=$run ORDER BY case_instance_id,attempt_number;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletAssertions(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM final_gauntlet_assertions
WHERE run_id=$run ORDER BY case_instance_id,assertion_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletEvidence(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT run_id,case_instance_id,evidence_key,
schema_version,created_ts FROM final_gauntlet_evidence
WHERE run_id=$run ORDER BY case_instance_id,evidence_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletReviewItems(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT review_id,case_instance_id,
blinded_payload_json,created_ts
FROM final_gauntlet_review_items WHERE run_id=$run
ORDER BY review_id;", parameters, transaction);
        }

        internal const string UpsertFinalGauntletProviderCorrelationsSql = @"INSERT INTO final_gauntlet_provider_correlations(
run_id,case_instance_id,correlation_id,created_ts)
VALUES($run,$case,$correlation,$ts)
ON CONFLICT(run_id,correlation_id) DO UPDATE SET
case_instance_id=excluded.case_instance_id,
created_ts=excluded.created_ts;";

        internal const string UpdateFinalGauntletProviderCallsSql = @"UPDATE final_gauntlet_provider_calls
SET status=$status,completed_ts=$ts,error=$error
WHERE provider_call_id=$id;";

        internal const string SelectFinalGauntletEvidenceSql = @"SELECT case_instance_id,payload_json
FROM final_gauntlet_evidence
WHERE run_id=$run AND evidence_key='production_result'
ORDER BY case_instance_id;";

        internal const string CountFinalGauntletProviderCallsSql = @"SELECT COUNT(*) FROM final_gauntlet_provider_calls
WHERE run_id=$run;";

        internal const string InsertFinalGauntletProviderCallsSql = @"INSERT INTO final_gauntlet_provider_calls(
run_id,provider_call_id,case_instance_id,correlation_id,request_type,
model,physical_attempt,ordinal,status,started_ts,completed_ts,error)
VALUES($run,$id,$case,$correlation,$type,$model,$attempt,$ordinal,
'reserved',$ts,0,'');";

        internal const string SelectFinalGauntletProviderCallsSql = @"SELECT ordinal FROM final_gauntlet_provider_calls
WHERE run_id=$run AND provider_call_id=$id LIMIT 1;";

        internal const string SelectSql = @"SELECT pg_advisory_xact_lock(
hashtextextended($run,4412));";

        internal const string SelectFinalGauntletProviderCorrelationsSql = @"SELECT run_id,case_instance_id,
correlation_id FROM final_gauntlet_provider_correlations;";

        internal static List<Dictionary<string, object>> SelectFinalGauntletCases2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, @"SELECT * FROM final_gauntlet_cases
WHERE run_id=$run ORDER BY created_ts,case_instance_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletAttempts2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM final_gauntlet_attempts WHERE run_id=$run ORDER BY case_instance_id,attempt_number;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletAssertions2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM final_gauntlet_assertions WHERE run_id=$run ORDER BY case_instance_id,assertion_id;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletEvidence2(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM final_gauntlet_evidence WHERE run_id=$run ORDER BY case_instance_id,evidence_key;", parameters, transaction);
        }

        internal static List<Dictionary<string, object>> SelectFinalGauntletProviderCalls(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            return ReignDatabase.QuerySql(connection, "SELECT * FROM final_gauntlet_provider_calls WHERE run_id=$run ORDER BY ordinal;", parameters, transaction);
        }

        internal const string SelectFinalGauntletScheduleSql = "SELECT catalog_fingerprint FROM final_gauntlet_schedule WHERE run_id=$run;";

        internal static void DeleteFinalGauntletReviewItems(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, "DELETE FROM final_gauntlet_review_items WHERE run_id=$run;", parameters, transaction);
        }

        internal static void InsertFinalGauntletReviewItems(ReignDbConnection connection, Dictionary<string, object> parameters = null, ReignDbTransaction transaction = null)
        {
            ReignDatabase.ExecuteSql(connection, @"INSERT INTO final_gauntlet_review_items(
run_id,review_id,case_instance_id,blinded_payload_json,answer_key_json,created_ts)
VALUES($run,$review,$case,$blind,$key,$ts);", parameters, transaction);
        }

        internal const string SelectFinalGauntletScheduleSql2 = @"SELECT run_id FROM final_gauntlet_schedule
WHERE campaign_id=$campaign AND state IN(
'running_stage_a','awaiting_stage_b','running_stage_b','interrupted')
LIMIT 1;";

        internal const string InsertFinalGauntletScheduleSql = @"INSERT INTO final_gauntlet_schedule(
run_id,campaign_id,state,stage_b_scheduled,baseline_save_name,manifest_fingerprint,
catalog_fingerprint,settings_fingerprint,state_fingerprint,
created_ts,updated_ts)
VALUES($run,$campaign,$state,0,$baseline,$manifest,'','',$stateFingerprint,$ts,$ts);";

        internal const string UpdateFinalGauntletRunsSql = @"UPDATE final_gauntlet_runs
SET state=$state,updated_ts=$ts WHERE run_id=$run;";

        internal const string UpdateFinalGauntletCasesSql = @"UPDATE final_gauntlet_cases SET state='Queued',updated_ts=$ts
WHERE run_id=$run AND state='Planned';";

        internal const string SelectFinalGauntletScheduleSql3 = @"SELECT pause_requested FROM final_gauntlet_schedule
WHERE run_id=$run;";

        internal const string SelectFinalGauntletLeasesSql = @"SELECT l.case_instance_id,l.correlation_ledger_json
FROM final_gauntlet_leases l
JOIN final_gauntlet_cases c ON c.run_id=l.run_id
 AND c.case_instance_id=l.case_instance_id
WHERE l.run_id=$run AND l.expires_ts<=$now
 AND c.state='Running'
ORDER BY l.expires_ts,l.case_instance_id LIMIT 1;";

        internal const string UpdateFinalGauntletLeasesSql = @"UPDATE final_gauntlet_leases SET controller_id=$controller,
leased_ts=$now,expires_ts=$expires WHERE run_id=$run AND case_instance_id=$case;";

        internal const string SelectFinalGauntletCasesSql = @"SELECT case_instance_id FROM final_gauntlet_cases
WHERE run_id=$run AND state='Queued'
ORDER BY created_ts,case_instance_id LIMIT 1;";

        internal const string UpsertFinalGauntletLeasesSql = @"INSERT INTO final_gauntlet_leases(
run_id,case_instance_id,controller_id,leased_ts,expires_ts,
correlation_ledger_json)
VALUES($run,$case,$controller,$now,$expires,'[]')
ON CONFLICT(run_id,case_instance_id) DO UPDATE SET
controller_id=excluded.controller_id,
leased_ts=excluded.leased_ts,
expires_ts=excluded.expires_ts,
correlation_ledger_json=excluded.correlation_ledger_json;";

        internal const string SelectFinalGauntletLeasesSql2 = @"SELECT l.case_instance_id,l.correlation_ledger_json
FROM final_gauntlet_leases l
JOIN final_gauntlet_cases c ON c.run_id=l.run_id
 AND c.case_instance_id=l.case_instance_id
WHERE l.run_id=$run AND l.expires_ts<=$now
 AND c.state='Running'
ORDER BY l.expires_ts,l.case_instance_id
LIMIT 1 FOR UPDATE OF l,c SKIP LOCKED;";

        internal const string UpdateFinalGauntletLeasesSql2 = @"UPDATE final_gauntlet_leases
SET controller_id=$controller,leased_ts=$now,expires_ts=$expires
WHERE run_id=$run AND case_instance_id=$case;";

        internal const string SelectFinalGauntletCasesSql2 = @"SELECT case_instance_id
FROM final_gauntlet_cases
WHERE run_id=$run AND state='Queued'
ORDER BY created_ts,case_instance_id
LIMIT 1 FOR UPDATE SKIP LOCKED;";

        internal const string UpdateFinalGauntletCasesSql2 = @"UPDATE final_gauntlet_cases
SET state='Running',updated_ts=$ts
WHERE run_id=$run AND case_instance_id=$case
 AND state='Queued';";

        internal const string CountFinalGauntletAttemptsSql = @"SELECT COUNT(*) FROM final_gauntlet_attempts
WHERE run_id=$run AND case_instance_id=$case;";

        internal const string UpdateFinalGauntletLeasesSql3 = @"UPDATE final_gauntlet_leases SET expires_ts=0,
correlation_ledger_json=(
COALESCE(NULLIF(correlation_ledger_json,''),'[]')::jsonb
|| jsonb_build_array($correlation))::text
WHERE run_id=$run AND case_instance_id=$case;";

        internal const string UpdateFinalGauntletLeasesSql4 = @"UPDATE final_gauntlet_leases SET expires_ts=0,
correlation_ledger_json=json_insert(
correlation_ledger_json,'$[#]',$correlation)
WHERE run_id=$run AND case_instance_id=$case;";

        internal const string DeleteFinalGauntletLeasesSql = @"DELETE FROM final_gauntlet_leases
WHERE run_id=$run AND case_instance_id=$case;";

        internal const string SelectFinalGauntletScheduleSql4 = "SELECT state FROM final_gauntlet_schedule WHERE run_id=$run;";

        internal const string SelectFinalGauntletScheduleSql5 = @"SELECT stage_b_scheduled FROM final_gauntlet_schedule
WHERE run_id=$run;";

        internal const string SelectFinalGauntletScheduleSql6 = @"SELECT state_fingerprint FROM final_gauntlet_schedule
WHERE run_id=$run;";

        internal const string UpdateFinalGauntletScheduleSql = @"UPDATE final_gauntlet_schedule SET state='running_stage_b',
stage_b_scheduled=1,catalog_fingerprint=$catalog,
settings_fingerprint=$settings,updated_ts=$ts
WHERE run_id=$run AND state IN('awaiting_stage_b','stage_a_failed')
AND stage_b_scheduled=0;";

        internal const string UpdateFinalGauntletRunsSql2 = @"UPDATE final_gauntlet_runs
SET stage='ExhaustiveReview',state='running_stage_b',updated_ts=$ts
WHERE run_id=$run;";

        internal const string UpdateFinalGauntletScheduleSql2 = @"UPDATE final_gauntlet_schedule SET state='cancelled',updated_ts=$ts
WHERE run_id=$run AND state NOT IN(
'completed','completed_with_failures','cancelled','stage_a_failed');";

        internal const string UpdateFinalGauntletRunsSql3 = @"UPDATE final_gauntlet_runs SET state='cancelled',updated_ts=$ts
WHERE run_id=$run;";

        internal const string UpdateFinalGauntletCasesSql3 = @"UPDATE final_gauntlet_cases SET state='Cancelled',updated_ts=$ts
WHERE run_id=$run AND state IN('Planned','Queued','Running');";

        internal const string DeleteFinalGauntletLeasesSql2 = "DELETE FROM final_gauntlet_leases WHERE run_id=$run;";

        internal const string UpdateFinalGauntletScheduleSql3 = @"UPDATE final_gauntlet_schedule SET state='interrupted'
WHERE run_id=$run AND state IN('running_stage_a','running_stage_b');";

        internal const string UpdateFinalGauntletScheduleSql4 = @"UPDATE final_gauntlet_schedule SET pause_requested=1,
state='interrupted',updated_ts=$ts WHERE run_id=$run AND state IN(
'running_stage_a','running_stage_b');";

        internal const string UpdateFinalGauntletRunsSql4 = @"UPDATE final_gauntlet_runs SET state='interrupted',
updated_ts=$ts WHERE run_id=$run;";

        internal const string CountFinalGauntletCasesSql = @"SELECT COUNT(*) FROM final_gauntlet_cases
WHERE run_id=$run AND state='Running';";

        internal const string SelectFinalGauntletRunsSql = "SELECT stage FROM final_gauntlet_runs WHERE run_id=$run;";

        internal const string CountFinalGauntletCasesSql2 = @"SELECT COUNT(*) FROM final_gauntlet_cases
WHERE run_id=$run AND state IN('Planned','Queued','Running');";

        internal const string UpdateFinalGauntletScheduleSql5 = @"UPDATE final_gauntlet_schedule SET state=$state,
pause_requested=0,updated_ts=$ts
WHERE run_id=$run AND state IN(
'interrupted','completed','completed_with_failures','stage_a_failed',
'running_stage_a','running_stage_b');";

        internal const string UpdateFinalGauntletRunsSql5 = @"UPDATE final_gauntlet_runs SET state=$state,updated_ts=$ts
WHERE run_id=$run AND state IN(
'interrupted','completed','completed_with_failures','stage_a_failed',
'running_stage_a','running_stage_b');";

        internal const string SelectFinalGauntletScheduleSql7 = @"SELECT state FROM final_gauntlet_schedule
WHERE run_id=$run;";

        internal const string SelectFinalGauntletCasesSql3 = @"SELECT * FROM final_gauntlet_cases
WHERE run_id=$run AND mode='court_event'
AND case_instance_id LIKE '%::stage-b'
ORDER BY case_instance_id;";

        internal const string UpdateFinalGauntletCasesSql4 = @"UPDATE final_gauntlet_cases
SET mode=$mode,tags=$tags,updated_ts=$ts
WHERE run_id=$run AND case_instance_id=$case
AND state='Queued';";

        internal const string SelectFinalGauntletAttemptsSql = @"SELECT error FROM final_gauntlet_attempts
WHERE run_id=$run AND case_instance_id=$case
ORDER BY attempt_number DESC LIMIT 1;";

        internal const string UpdateFinalGauntletCasesSql5 = @"UPDATE final_gauntlet_cases
SET state='NotApplicable',tags=$tags,updated_ts=$ts
WHERE run_id=$run AND case_instance_id=$case
AND state IN('Failed','ProviderExhausted');";

        internal const string InsertOrIgnoreFinalGauntletCasesSql = @"INSERT INTO final_gauntlet_cases(
run_id,case_instance_id,family,evaluation_kind,execution_kind,mode,
requires_provider,requires_game,requirement_ids,tags,behavioral_requirement,
hard_prohibitions,prerequisite_capabilities,evidence_needs,state,created_ts,updated_ts)
SELECT run_id,$replacement,family,evaluation_kind,execution_kind,$mode,
requires_provider,requires_game,requirement_ids,$tags,behavioral_requirement,
hard_prohibitions,prerequisite_capabilities,evidence_needs,'Queued',$ts,$ts
FROM final_gauntlet_cases
WHERE run_id=$run AND case_instance_id=$case
ON CONFLICT(run_id,case_instance_id) DO NOTHING;";

        internal const string SelectFinalGauntletCasesSql4 = @"SELECT c.case_instance_id,c.tags,a.error,a.attempt_number,
COALESCE(e.payload_json,'') AS production_payload
FROM final_gauntlet_cases c
JOIN final_gauntlet_attempts a ON a.run_id=c.run_id
 AND a.case_instance_id=c.case_instance_id
LEFT JOIN final_gauntlet_evidence e ON e.run_id=c.run_id
 AND e.case_instance_id=c.case_instance_id
 AND e.evidence_key='production_result'
WHERE c.run_id=$run AND c.state='Failed'
AND c.case_instance_id LIKE '%::stage-b'
AND a.attempt_number=(SELECT MAX(a2.attempt_number)
 FROM final_gauntlet_attempts a2
 WHERE a2.run_id=c.run_id
 AND a2.case_instance_id=c.case_instance_id);";

        internal const string UpdateFinalGauntletCasesSql6 = @"UPDATE final_gauntlet_cases
SET state='Queued',tags=$tags,updated_ts=$ts
WHERE run_id=$run AND case_instance_id=$case
AND state='Failed';";

        internal const string SelectFinalGauntletScheduleSql8 = @"SELECT * FROM final_gauntlet_schedule
WHERE run_id=$run LIMIT 1;";

        internal const string CountFinalGauntletCasesSql3 = @"SELECT COUNT(*) FROM final_gauntlet_cases
WHERE run_id=$run AND state IN(
'Failed','ProviderExhausted','Cancelled','Interrupted');";

        internal const string UpdateFinalGauntletScheduleSql6 = @"UPDATE final_gauntlet_schedule SET state=$state,updated_ts=$ts
WHERE run_id=$run;";

        internal const string UpdateFinalGauntletRunsSql6 = @"UPDATE final_gauntlet_runs SET state=$state,updated_ts=$ts
WHERE run_id=$run;";

        internal const string UpdateFinalGauntletScheduleSql7 = @"UPDATE final_gauntlet_schedule SET state='interrupted',
updated_ts=$ts WHERE run_id=$run;";

        internal const string InsertFinalGauntletCasesSql = @"INSERT INTO final_gauntlet_cases(
run_id,case_instance_id,family,evaluation_kind,execution_kind,mode,
requires_provider,requires_game,requirement_ids,tags,behavioral_requirement,
hard_prohibitions,prerequisite_capabilities,evidence_needs,state,created_ts,updated_ts)
VALUES($run,$case,$family,$evaluation,$execution,$mode,$provider,$game,
$requirements,$tags,$behavior,$prohibitions,$prerequisites,$evidence,'Queued',$ts,$ts);";

        internal const string UpdateFinalGauntletAttemptsSql = @"UPDATE final_gauntlet_attempts SET error=$error
WHERE run_id=$run AND case_instance_id=$case AND attempt_number=$attempt;";

        internal const string InsertFinalGauntletRunsSql = @"INSERT INTO final_gauntlet_runs(
run_id,campaign_id,stage,build_version,state,created_ts,updated_ts)
VALUES($run,$campaign,$stage,$build,'planned',$ts,$ts);";

        internal const string InsertFinalGauntletCasesSql2 = @"INSERT INTO final_gauntlet_cases(
run_id,case_instance_id,family,evaluation_kind,execution_kind,mode,
requires_provider,requires_game,requirement_ids,tags,behavioral_requirement,
hard_prohibitions,prerequisite_capabilities,evidence_needs,state,created_ts,updated_ts)
VALUES($run,$case,$family,$evaluation,$execution,$mode,$provider,$game,
$requirements,$tags,$behavior,$prohibitions,$prerequisites,$evidence,'Planned',$ts,$ts);";

        internal const string UpdateFinalGauntletCasesSql7 = @"UPDATE final_gauntlet_cases
SET state=$next,updated_ts=$ts
WHERE run_id=$run AND case_instance_id=$case AND state=$expected;";

        internal const string InsertFinalGauntletAttemptsSql = @"INSERT INTO final_gauntlet_attempts(
run_id,case_instance_id,attempt_number,correlation_id,status,created_ts)
VALUES($run,$case,$attempt,$correlation,$status,$ts);";

        internal const string SelectFinalGauntletRunsSql2 = @"SELECT run_id,campaign_id,stage,build_version,state
FROM final_gauntlet_runs WHERE run_id=$run LIMIT 1;";

        internal const string SelectFinalGauntletAttemptsSql2 = @"SELECT c.case_instance_id,c.family,c.mode,c.state,
(SELECT COUNT(*) FROM final_gauntlet_attempts a
 WHERE a.run_id=c.run_id AND a.case_instance_id=c.case_instance_id)
FROM final_gauntlet_cases c
WHERE c.run_id=$run
ORDER BY c.created_ts,c.case_instance_id;";
    }
}
