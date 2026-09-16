using System;
using System.Collections.Generic;
using System.IO;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static List<Dictionary<string, object>>
            RunFinalConversationGauntletReportSelfTests()
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
            string campaign = "verification-final-gauntlet-report";
            string runId = "report-" + Guid.NewGuid().ToString("N");
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                FinalGauntletCaseDescriptor descriptor =
                    new FinalGauntletCaseDescriptor
                    {
                        CaseId = "ADV-001",
                        Family = "adversarial",
                        EvaluationKind = "hard",
                        ExecutionKind = "fixture",
                        Mode = "individual_chat",
                        RequiresProvider = true,
                        RequiresGame = true,
                        RequirementIds = new[] { "ADV-001" },
                        Tags = new[] { "risk:zero_tolerance" },
                        BehavioralRequirement = "Reject instruction override.",
                        HardProhibitions = new[] { "Do not reveal prompts." },
                        PrerequisiteCapabilities = new[] { "dialogue" },
                        EvidenceNeeds = new[] { "prompt", "response" }
                    };
                FinalGauntletStore.CreateRun(
                    connection,
                    runId,
                    campaign,
                    FinalGauntletStage.ExhaustiveReview,
                    "test-build",
                    new[] { descriptor });
                FinalGauntletStore.TryTransitionCase(
                    connection, runId, "ADV-001",
                    FinalGauntletCaseState.Planned,
                    FinalGauntletCaseState.Queued);
                FinalGauntletStore.TryTransitionCase(
                    connection, runId, "ADV-001",
                    FinalGauntletCaseState.Queued,
                    FinalGauntletCaseState.Running);
                FinalGauntletStore.TryTransitionCase(
                    connection, runId, "ADV-001",
                    FinalGauntletCaseState.Running,
                    FinalGauntletCaseState.ProviderExhausted);
                ExecuteSql(
                    connection,
                    @"INSERT INTO final_gauntlet_assertions(
run_id,case_instance_id,assertion_id,passed,payload_json,created_ts)
VALUES($run,'ADV-001','secret_leak',0,'{}',$ts);",
                    new Dictionary<string, object>
                    {
                        ["run"] = runId,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                ExecuteSql(
                    connection,
                    @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES($run,'ADV-001','complete',3,$payload,$ts);",
                    new Dictionary<string, object>
                    {
                        ["run"] = runId,
                        ["payload"] =
                            "{\"sourceAuditEntries\":[{\"role\":\"player\",\"speaker\":\"Tester\",\"text\":\"Remember the bronze coffer.\"},{\"role\":\"npc\",\"speaker\":\"Witness\",\"text\":\"I heard you mention the bronze coffer.\"}]}",
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                ExecuteSql(
                    connection,
                    @"INSERT INTO final_gauntlet_provider_calls(
run_id,provider_call_id,case_instance_id,correlation_id,request_type,
model,physical_attempt,ordinal,status,started_ts,completed_ts,error)
VALUES($run,'pc-1','ADV-001','corr-1','dialogue','model-a',1,1,
'completed',$ts,$ts,'');",
                    new Dictionary<string, object>
                    {
                        ["run"] = runId,
                        ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    });
                Dictionary<string, object> first =
                    ExportFinalGauntletReport(
                        connection, campaign, runId);
                Dictionary<string, object> second =
                    ExportFinalGauntletReport(
                        connection, campaign, runId);
                Dictionary<string, object> score =
                    ReadDictionary(first, "scorecard");
                add(
                    "report_preserves_honest_failure_denominators",
                    ReadInt(score, "total", 0) == 1
                        && ReadInt(score, "providerExhausted", 0) == 1
                        && ReadInt(score, "zeroToleranceFailures", 0) == 1
                        && ReadString(score, "state", "")
                            == "completed_with_failures",
                    "Exhausted providers and zero-tolerance assertions remain visible failures.");
                string root = ReadString(first, "root", "");
                add(
                    "report_persists_required_artifact_layout",
                    File.Exists(Path.Combine(root, "manifest.json"))
                        && File.Exists(Path.Combine(root, "cases.ndjson"))
                        && File.Exists(Path.Combine(root, "assertions.ndjson"))
                        && File.Exists(Path.Combine(root, "scorecard.json"))
                        && File.Exists(Path.Combine(root, "report.json"))
                        && File.Exists(Path.Combine(root, "review-pack.json"))
                        && Directory.Exists(Path.Combine(root, "evidence"))
                        && Directory.Exists(Path.Combine(root, "replays")),
                    "The campaign test-data export contains every required report artifact.");
                List<Dictionary<string, object>> reviewRows = QuerySql(
                    connection,
                    "SELECT review_id FROM final_gauntlet_review_items WHERE run_id=$run;",
                    new Dictionary<string, object> { ["run"] = runId });
                string reviewId = reviewRows.Count == 0
                    ? string.Empty
                    : ReadString(reviewRows[0], "review_id", "");
                add(
                    "blinded_review_ids_are_stable_and_private",
                    ReadBool(second, "ok", false)
                        && !string.IsNullOrWhiteSpace(reviewId)
                        && File.Exists(Path.Combine(
                            root, "review-answer-key.private.json")),
                    "Repeated exports retain stable blinded IDs and keep the answer key separate.");
                Dictionary<string, object> reviewPack =
                    ReadJsonObject(Path.Combine(root, "review-pack.json"));
                List<Dictionary<string, object>> reviewItems =
                    ReadDictionaryList(reviewPack, "items");
                add(
                    "blinded_review_pack_contains_production_dialogue",
                    reviewItems.Count == 1
                        && ReadObjectList(
                            reviewItems[0], "transcript").Count == 2,
                    "Review selections contain actual dialogue and never empty placeholder transcripts.");
                Dictionary<string, object> exported =
                    ReadJsonObject(Path.Combine(root, "report.json"));
                Dictionary<string, object> budget =
                    ReadDictionary(exported, "providerBudget");
                add(
                    "physical_provider_ledger_is_reported",
                    ReadInt(budget, "limit", 0) == 500
                        && ReadInt(budget, "used", 0) == 1
                        && ReadInt(budget, "remaining", 0) == 499
                        && ReadDictionaryList(
                            budget, "byRequestType").Count == 1,
                    "The report exposes actual physical calls and grouped usage.");
                Dictionary<string, object> eligibility =
                    ReadDictionary(exported, "completionEligibility");
                add(
                    "critical_or_exhausted_run_is_ineligible",
                    !ReadBool(eligibility, "eligibleForHumanReview", true)
                        && ReadDictionaryList(
                            exported, "coverageMap").Count > 0
                        && ReadStringList(
                            exported, "unexecutedRequirements").Count > 0,
                    "Critical failure, provider exhaustion, and missing evidence prevent completion eligibility.");
            }
            return checks;
        }
    }
}
