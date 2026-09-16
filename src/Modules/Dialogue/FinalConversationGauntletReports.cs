using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Npgsql;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static Dictionary<string, object>
            ExportFinalGauntletReport(
                ReignDbConnection connection,
                string campaignId,
                string runId)
        {
            Dictionary<string, object> run =
                FinalGauntletStore.LoadRun(connection, runId);
            if (run.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Final gauntlet run was not found."
                };
            List<Dictionary<string, object>> cases = QuerySql(
                connection,
                @"SELECT * FROM final_gauntlet_cases
WHERE run_id=$run ORDER BY created_ts,case_instance_id;",
                new Dictionary<string, object> { ["run"] = runId });
            List<Dictionary<string, object>> attempts = QuerySql(
                connection,
                "SELECT * FROM final_gauntlet_attempts WHERE run_id=$run ORDER BY case_instance_id,attempt_number;",
                new Dictionary<string, object> { ["run"] = runId });
            List<Dictionary<string, object>> assertions = QuerySql(
                connection,
                "SELECT * FROM final_gauntlet_assertions WHERE run_id=$run ORDER BY case_instance_id,assertion_id;",
                new Dictionary<string, object> { ["run"] = runId });
            List<Dictionary<string, object>> evidence = QuerySql(
                connection,
                "SELECT * FROM final_gauntlet_evidence WHERE run_id=$run ORDER BY case_instance_id,evidence_key;",
                new Dictionary<string, object> { ["run"] = runId });
            List<Dictionary<string, object>> providerCalls = QuerySql(
                connection,
                "SELECT * FROM final_gauntlet_provider_calls WHERE run_id=$run ORDER BY ordinal;",
                new Dictionary<string, object> { ["run"] = runId });
            string root = FinalGauntletRunArtifactRoot(campaignId, runId);
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "evidence"));
            Directory.CreateDirectory(Path.Combine(root, "replays"));

            Dictionary<string, object> scorecard =
                BuildFinalGauntletScorecard(run, cases, assertions);
            Dictionary<string, object> providerBudget =
                BuildFinalGauntletProviderBudget(providerCalls, cases);
            List<Dictionary<string, object>> coverageMap =
                BuildFinalGauntletCoverageMap(cases);
            List<string> unexecuted = coverageMap
                .Where(row => !ReadBool(row, "evidenceComplete", false))
                .Select(row => ReadString(row, "requirementId", ""))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToList();
            Dictionary<string, object> completion =
                BuildFinalGauntletCompletionEligibility(
                    scorecard, assertions, providerBudget, unexecuted);
            Dictionary<string, object> manifest =
                new Dictionary<string, object>
                {
                    ["schema"] = "reign-final-gauntlet-manifest-v1",
                    ["run"] = run,
                    ["catalogFingerprint"] = ReadScalarString(
                        connection,
                        "SELECT catalog_fingerprint FROM final_gauntlet_schedule WHERE run_id=$run;",
                        runId),
                    ["exportedUtc"] = DateTime.UtcNow.ToString("o")
                };
            Dictionary<string, object> report =
                new Dictionary<string, object>
                {
                    ["schema"] = "reign-final-gauntlet-report-v1",
                    ["run"] = run,
                    ["scorecard"] = scorecard,
                    ["cases"] = cases,
                    ["attempts"] = attempts,
                    ["assertions"] = assertions,
                    ["evidenceIndex"] = evidence.Select(row =>
                        new Dictionary<string, object>
                        {
                            ["caseInstanceId"] =
                                ReadString(row, "case_instance_id", ""),
                            ["evidenceKey"] =
                                ReadString(row, "evidence_key", ""),
                            ["schemaVersion"] =
                                ReadInt(row, "schema_version", 0)
                        }).Cast<object>().ToList(),
                    ["providerBudget"] = providerBudget,
                    ["coverageMap"] = coverageMap.Cast<object>().ToList(),
                    ["partitionRollup"] =
                        BuildFinalGauntletPartitionRollup(cases, providerCalls),
                    ["unexecutedRequirements"] =
                        unexecuted.Cast<object>().ToList(),
                    ["completionEligibility"] = completion,
                    ["reviewRequired"] = true,
                    ["stageBPolicy"] =
                        "Every case runs once; failures are retained and execution continues."
                };
            WriteJsonObject(Path.Combine(root, "manifest.json"), manifest);
            WriteGauntletNdjson(
                Path.Combine(root, "cases.ndjson"), cases);
            WriteGauntletNdjson(
                Path.Combine(root, "assertions.ndjson"), assertions);
            WriteJsonObject(
                Path.Combine(root, "scorecard.json"), scorecard);
            WriteJsonObject(
                Path.Combine(root, "report.json"), report);
            foreach (Dictionary<string, object> row in evidence)
            {
                string caseId = ReadString(
                    row, "case_instance_id", "case");
                string key = ReadString(row, "evidence_key", "evidence");
                string payload = ReadString(row, "payload_json", "{}");
                File.WriteAllText(
                    Path.Combine(
                        root,
                        "evidence",
                        "ev-" + PromptHash(caseId + "|" + key)
                            .Substring(0, 16).ToLowerInvariant() + ".json"),
                    payload);
            }
            Dictionary<string, object> review =
                BuildFinalGauntletReviewPack(
                    connection, campaignId, runId, cases, evidence);
            report["reviewPack"] = review;
            WriteJsonObject(
                Path.Combine(root, "report.json"), report);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["root"] = root,
                ["manifestPath"] = Path.Combine(root, "manifest.json"),
                ["casesPath"] = Path.Combine(root, "cases.ndjson"),
                ["assertionsPath"] = Path.Combine(root, "assertions.ndjson"),
                ["scorecardPath"] = Path.Combine(root, "scorecard.json"),
                ["reportPath"] = Path.Combine(root, "report.json"),
                ["reviewPackPath"] = Path.Combine(root, "review-pack.json"),
                ["scorecard"] = scorecard
            };
        }

        private static Dictionary<string, object>
            BuildFinalGauntletProviderBudget(
                List<Dictionary<string, object>> calls,
                List<Dictionary<string, object>> cases)
        {
            calls = calls ?? new List<Dictionary<string, object>>();
            int used = calls.Count;
            return new Dictionary<string, object>
            {
                ["limit"] = 500,
                ["used"] = used,
                ["remaining"] = Math.Max(0, 500 - used),
                ["denied"] = calls.Count(row =>
                    ReadString(row, "status", "").Equals(
                        "denied", StringComparison.OrdinalIgnoreCase)),
                ["exhaustedCases"] = (cases
                    ?? new List<Dictionary<string, object>>()).Count(row =>
                        ReadString(row, "state", "").Equals(
                            "ProviderExhausted",
                            StringComparison.OrdinalIgnoreCase)),
                ["byRequestType"] = GroupProviderCalls(
                    calls, "request_type"),
                ["byModel"] = GroupProviderCalls(calls, "model"),
                ["byCase"] = GroupProviderCalls(
                    calls, "case_instance_id"),
                ["calls"] = calls.Cast<object>().ToList()
            };
        }

        private static List<object> GroupProviderCalls(
            List<Dictionary<string, object>> calls,
            string key)
        {
            return calls.GroupBy(row =>
                    ReadString(row, key, "unknown"),
                    StringComparer.OrdinalIgnoreCase)
                .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                .Select(group => (object)new Dictionary<string, object>
                {
                    ["key"] = group.Key,
                    ["count"] = group.Count(),
                    ["failed"] = group.Count(row =>
                        !ReadString(row, "status", "").Equals(
                            "completed",
                            StringComparison.OrdinalIgnoreCase))
                }).ToList();
        }

        private static List<Dictionary<string, object>>
            BuildFinalGauntletCoverageMap(
                List<Dictionary<string, object>> cases)
        {
            Dictionary<string, Dictionary<string, object>> result =
                new Dictionary<string, Dictionary<string, object>>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (FinalGauntletCaseDescriptor descriptor in
                BuildFinalGauntletRuntimeCatalog())
            {
                foreach (string requirement in descriptor.RequirementIds
                    ?? Array.Empty<string>())
                {
                    bool deterministic = descriptor.CoverageKind.Equals(
                        FinalGauntletCoverageKind.Deterministic.ToString(),
                        StringComparison.OrdinalIgnoreCase);
                    List<Dictionary<string, object>> representatives =
                        (cases ?? new List<Dictionary<string, object>>())
                        .Where(row => ReadString(
                                row, "requirement_ids", "")
                            .Split(new[] { '\n' },
                                StringSplitOptions.RemoveEmptyEntries)
                            .Contains(
                                requirement,
                                StringComparer.OrdinalIgnoreCase))
                        .ToList();
                    result[requirement] =
                        new Dictionary<string, object>
                        {
                            ["requirementId"] = requirement,
                            ["coverageKind"] =
                                descriptor.CoverageKind,
                            ["evidenceComplete"] = deterministic
                                || representatives.Any(row =>
                                    ReadString(row, "state", "")
                                        .Equals(
                                            "Passed",
                                            StringComparison.OrdinalIgnoreCase)),
                            ["caseInstanceIds"] = representatives.Select(row =>
                                ReadString(
                                    row, "case_instance_id", ""))
                                .Cast<object>().ToList()
                        };
                }
            }
            return result.Values.OrderBy(row =>
                ReadString(row, "requirementId", ""),
                StringComparer.Ordinal).ToList();
        }

        private static Dictionary<string, object>
            BuildFinalGauntletPartitionRollup(
                List<Dictionary<string, object>> cases,
                List<Dictionary<string, object>> calls)
        {
            Dictionary<string, string> partitions =
                (cases ?? new List<Dictionary<string, object>>())
                .ToDictionary(
                    row => ReadString(row, "case_instance_id", ""),
                    row => ReadString(row, "tags", "")
                        .Split(new[] { '\n' },
                            StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault(tag => tag.StartsWith(
                            "partition:",
                            StringComparison.OrdinalIgnoreCase))
                        ?.Substring("partition:".Length)
                        ?? "stageA",
                    StringComparer.OrdinalIgnoreCase);
            return (calls ?? new List<Dictionary<string, object>>())
                .GroupBy(row =>
                {
                    string id = ReadString(
                        row, "case_instance_id", "");
                    return partitions.TryGetValue(id, out string value)
                        ? value : "stageA";
                }, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => (object)new Dictionary<string, object>
                    {
                        ["physicalCalls"] = group.Count(),
                        ["failedCalls"] = group.Count(row =>
                            !ReadString(row, "status", "").Equals(
                                "completed",
                                StringComparison.OrdinalIgnoreCase))
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object>
            BuildFinalGauntletCompletionEligibility(
                Dictionary<string, object> scorecard,
                List<Dictionary<string, object>> assertions,
                Dictionary<string, object> providerBudget,
                List<string> unexecuted)
        {
            int qualitative = assertions.Count(row =>
                ReadString(row, "assertion_id", "").IndexOf(
                    "semantic",
                    StringComparison.OrdinalIgnoreCase) >= 0);
            int qualitativePassed = assertions.Count(row =>
                ReadString(row, "assertion_id", "").IndexOf(
                    "semantic",
                    StringComparison.OrdinalIgnoreCase) >= 0
                && ReadInt(row, "passed", 0) == 1);
            double qualitativeRate = qualitative == 0
                ? 1d : (double)qualitativePassed / qualitative;
            bool eligible = ReadInt(scorecard, "pending", 0) == 0
                && ReadInt(scorecard, "failed", 0) == 0
                && ReadInt(scorecard, "zeroToleranceFailures", 0) == 0
                && ReadInt(providerBudget, "used", 501) <= 500
                && ReadInt(providerBudget, "exhaustedCases", 0) == 0
                && unexecuted.Count == 0
                && qualitativeRate >= 0.95d;
            return new Dictionary<string, object>
            {
                ["eligibleForHumanReview"] = eligible,
                ["releaseEligible"] = false,
                ["humanReviewRequired"] = true,
                ["qualitativeSuccessRate"] = qualitativeRate,
                ["unexecutedRequirementCount"] = unexecuted.Count,
                ["reason"] = eligible
                    ? "Automated gates passed; blinded human review remains."
                    : "One or more automated completion gates failed."
            };
        }

        private static Dictionary<string, object>
            BuildFinalGauntletScorecard(
                Dictionary<string, object> run,
                List<Dictionary<string, object>> cases,
                List<Dictionary<string, object>> assertions)
        {
            string[] failingStates =
            {
                "Failed", "ProviderExhausted", "Interrupted", "Cancelled"
            };
            List<Dictionary<string, object>> scoredCases = cases
                .Where(row =>
                    !ReadString(row, "state", "").Equals(
                        "Skipped", StringComparison.OrdinalIgnoreCase)
                    && !ReadString(row, "state", "").Equals(
                        "NotApplicable", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int excluded = cases.Count - scoredCases.Count;
            int passed = scoredCases.Count(row =>
                ReadString(row, "state", "") == "Passed");
            int failed = scoredCases.Count(row => failingStates.Contains(
                ReadString(row, "state", ""),
                StringComparer.OrdinalIgnoreCase));
            int pending = scoredCases.Count - passed - failed;
            List<Dictionary<string, object>> failedAssertions = assertions
                .Where(row => ReadInt(row, "passed", 0) == 0)
                .ToList();
            int exhausted = scoredCases.Count(row => string.Equals(
                ReadString(row, "state", ""),
                "ProviderExhausted",
                StringComparison.OrdinalIgnoreCase));
            string state = pending > 0
                ? "incomplete"
                : failed > 0 || failedAssertions.Count > 0
                    ? "completed_with_failures"
                    : "passed_pending_human_review";
            return new Dictionary<string, object>
            {
                ["schema"] = "reign-final-gauntlet-scorecard-v1",
                ["runId"] = ReadString(run, "runId", ""),
                ["state"] = state,
                ["total"] = scoredCases.Count,
                ["excluded"] = excluded,
                ["passed"] = passed,
                ["failed"] = failed,
                ["pending"] = Math.Max(0, pending),
                ["providerExhausted"] = exhausted,
                ["failedAssertions"] = failedAssertions.Count,
                ["zeroToleranceFailures"] = failedAssertions.Count(row =>
                    IsFinalGauntletZeroToleranceAssertion(
                        ReadString(row, "assertion_id", ""))),
                ["humanReviewRequired"] = true,
                ["releaseEligible"] = false,
                ["families"] = scoredCases
                    .GroupBy(row => ReadString(row, "family", "unknown"))
                    .ToDictionary(
                        group => group.Key,
                        group => (object)new Dictionary<string, object>
                        {
                            ["total"] = group.Count(),
                            ["passed"] = group.Count(row =>
                                ReadString(row, "state", "") == "Passed"),
                            ["failed"] = group.Count(row =>
                                failingStates.Contains(
                                    ReadString(row, "state", ""),
                                    StringComparer.OrdinalIgnoreCase))
                        },
                        StringComparer.OrdinalIgnoreCase)
            };
        }

        private static bool IsFinalGauntletZeroToleranceAssertion(string id)
        {
            string value = (id ?? string.Empty).ToLowerInvariant();
            return new[]
            {
                "invented_id", "wrong_target", "duplicate_action",
                "save_corruption", "identity_swap", "secret_leak",
                "player_narration", "mechanics_exposure",
                "malformed_action", "unsupported_world_mutation",
                "memory_contamination"
            }.Any(value.Contains);
        }

        private static string FinalGauntletRunArtifactRoot(
            string campaignId,
            string runId)
        {
            return CampaignFile(
                campaignId,
                "tests",
                "fg",
                PromptHash(runId ?? string.Empty)
                    .Substring(0, 16).ToLowerInvariant());
        }

        private static void WriteGauntletNdjson(
            string path,
            IEnumerable<Dictionary<string, object>> rows)
        {
            File.WriteAllLines(
                path,
                (rows ?? Array.Empty<Dictionary<string, object>>())
                    .Select(row => Json.Serialize(row)));
        }

        private static string ReadScalarString(
            ReignDbConnection connection,
            string sql,
            string runId)
        {
            try
            {
                using (ReignDbCommand command = connection.CreateCommand())
                {
                    command.CommandText =
                        ReignPostgreSqlDialect.Normalize(
                            connection, sql);
                    command.Parameters.AddWithValue("$run", runId);
                    object value = command.ExecuteScalar();
                    return value == null || value == DBNull.Value
                        ? string.Empty : Convert.ToString(value);
                }
            }
            catch (PostgresException)
            {
                return string.Empty;
            }
        }
    }
}
