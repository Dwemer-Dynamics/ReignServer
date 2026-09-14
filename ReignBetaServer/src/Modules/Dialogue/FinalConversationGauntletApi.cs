using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static string FinalGauntletCatalogPath()
        {
            string directory = Path.Combine(
                AppContext.BaseDirectory, "data", "tests", "final-gauntlet");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "catalog.json");
        }

        private static Dictionary<string, object>
            FinalGauntletCatalogBuildApi(
                Dictionary<string, object> payload)
        {
            List<FinalGauntletCaseDescriptor> catalog =
                BuildFinalGauntletRuntimeCatalog();
            Dictionary<string, object> artifact =
                new Dictionary<string, object>
                {
                    ["schema"] = "reign-final-gauntlet-catalog-v1",
                    ["builtUtc"] = DateTime.UtcNow.ToString("o"),
                    ["buildVersion"] = CurrentConversationBuildVersion(),
                    ["count"] = catalog.Count,
                    ["fingerprint"] = PromptHash(
                        CanonicalJson(catalog.Select(
                            FinalGauntletDescriptorMap).ToList()))
                        .ToLowerInvariant(),
                    ["cases"] = catalog.Select(
                        FinalGauntletDescriptorMap).Cast<object>().ToList()
                };
            WriteJsonObject(FinalGauntletCatalogPath(), artifact);
            artifact["ok"] = true;
            artifact["path"] = FinalGauntletCatalogPath();
            return artifact;
        }

        private static Dictionary<string, object>
            FinalGauntletStateFingerprintApi(
                Dictionary<string, object> payload)
        {
            payload = payload
                ?? new Dictionary<string, object>();
            string campaignId =
                ReadString(payload, "campaignId", "").Trim();
            List<Dictionary<string, object>> targets =
                ReadDictionaryList(payload, "targets");
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "campaignId is required."
                };
            if (targets.Count == 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "The authoritative living-adult NPC census is empty."
                };

            List<string> errors = new List<string>();
            List<IGrouping<string, Dictionary<string, object>>>
                heroGroups = targets.GroupBy(
                    target => ReadString(target, "heroId", ""),
                    StringComparer.OrdinalIgnoreCase).ToList();
            foreach (IGrouping<string, Dictionary<string, object>>
                group in heroGroups)
            {
                if (string.IsNullOrWhiteSpace(group.Key))
                    errors.Add("A census row has no heroId.");
                else if (group.Count() != 1)
                    errors.Add(
                        "Duplicate heroId: " + group.Key + ".");
            }
            foreach (Dictionary<string, object> target in targets)
            {
                string heroId =
                    ReadString(target, "heroId", "<missing>");
                string clanId =
                    ReadString(target, "clanId", "");
                int clanTier = ReadInt(target, "clanTier", -1);
                if (!ReadBool(target, "isAlive", false)
                    || !ReadBool(target, "isAdult", false))
                    errors.Add(
                        heroId
                        + " is not an eligible living adult.");
                if (ReadBool(target, "isLord", false)
                    && string.IsNullOrWhiteSpace(clanId))
                    errors.Add(heroId + " is a lord without a clan.");
                if (clanTier < 0 || clanTier > 6)
                    errors.Add(
                        heroId + " has invalid clan tier "
                        + clanTier + ".");
                if (!string.IsNullOrWhiteSpace(clanId)
                    && string.IsNullOrWhiteSpace(
                        ReadString(target, "clanName", "")))
                    errors.Add(
                        heroId + " belongs to " + clanId
                        + " but its clan name is missing.");
                if (!string.IsNullOrWhiteSpace(clanId)
                    && string.IsNullOrWhiteSpace(
                        ReadString(target, "clanLeaderId", "")))
                    errors.Add(
                        heroId + " belongs to " + clanId
                        + " but its clan leader is missing.");
            }
            foreach (IGrouping<string, Dictionary<string, object>>
                clan in targets
                    .Where(target => !string.IsNullOrWhiteSpace(
                        ReadString(target, "clanId", "")))
                    .GroupBy(
                        target => ReadString(target, "clanId", ""),
                        StringComparer.OrdinalIgnoreCase))
            {
                if (clan.Select(target =>
                        ReadString(target, "clanName", ""))
                    .Distinct(StringComparer.Ordinal).Count() != 1)
                    errors.Add(
                        clan.Key
                        + " has inconsistent authoritative names.");
                if (clan.Select(target =>
                        ReadString(target, "clanLeaderId", ""))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count() != 1)
                    errors.Add(
                        clan.Key
                        + " has inconsistent authoritative leaders.");
            }
            if (errors.Count > 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "The authoritative campaign/clan census is invalid.",
                    ["errors"] = errors.Distinct().Take(50).ToList()
                };

            string canonical =
                "reign-final-gauntlet-state-v1\n"
                + campaignId + "\n"
                + string.Join(
                    "\n",
                    targets.OrderBy(
                            target =>
                                ReadString(target, "heroId", ""),
                            StringComparer.Ordinal)
                        .Select(target => string.Join(
                            "|",
                            new[]
                            {
                                ReadString(target, "heroId", ""),
                                ReadString(
                                    target,
                                    "characterObjectId",
                                    ""),
                                ReadString(target, "name", ""),
                                ReadString(target, "clanId", ""),
                                ReadString(target, "clanName", ""),
                                ReadString(
                                    target,
                                    "clanLeaderId",
                                    ""),
                                ReadBool(
                                    target,
                                    "isClanLeader",
                                    false) ? "1" : "0",
                                ReadString(target, "kingdomId", ""),
                                ReadString(target, "cultureId", ""),
                                ReadInt(target, "clanTier", 0)
                                    .ToString(
                                        System.Globalization
                                            .CultureInfo
                                            .InvariantCulture),
                                ReadBool(target, "isLord", false)
                                    ? "1" : "0",
                                ReadBool(target, "isRuler", false)
                                    ? "1" : "0",
                                ReadBool(target, "isNotable", false)
                                    ? "1" : "0",
                                ReadBool(target, "isWanderer", false)
                                    ? "1" : "0",
                                ReadBool(target, "isAlive", false)
                                    ? "1" : "0",
                                ReadBool(target, "isAdult", false)
                                    ? "1" : "0",
                                ReadString(target, "occupation", ""),
                                ReadString(
                                    target,
                                    "governorOfSettlementId",
                                    "")
                            })));
            string fingerprint;
            using (SHA256 sha = SHA256.Create())
                fingerprint = BitConverter.ToString(
                        sha.ComputeHash(
                            Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", string.Empty)
                    .ToLowerInvariant();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schemaVersion"] =
                    "reign-final-gauntlet-state-v1",
                ["campaignId"] = campaignId,
                ["fingerprint"] = fingerprint,
                ["heroCount"] = targets.Count,
                ["clanCount"] = targets
                    .Select(target =>
                        ReadString(target, "clanId", ""))
                    .Where(value =>
                        !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                ["nobleCount"] = targets.Count(target =>
                    ReadBool(target, "isLord", false))
            };
        }

        private static Dictionary<string, object> FinalGauntletCatalogApi(
            Dictionary<string, string> query)
        {
            Dictionary<string, object> catalog =
                ReadJsonObject(FinalGauntletCatalogPath());
            return catalog.Count > 0
                ? new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["catalog"] = catalog,
                    ["path"] = FinalGauntletCatalogPath()
                }
                : FinalGauntletCatalogBuildApi(
                    new Dictionary<string, object>());
        }

        private static Dictionary<string, object> FinalGauntletRunStartApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            if (string.IsNullOrWhiteSpace(campaignId))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "campaignId is required."
                };
            string stateFingerprint =
                ReadString(payload, "stateFingerprint", "");
            if (string.IsNullOrWhiteSpace(stateFingerprint))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] =
                        "An authoritative campaign/clan census fingerprint is required."
                };
            Dictionary<string, object> catalogArtifact =
                ReadJsonObject(FinalGauntletCatalogPath());
            if (catalogArtifact.Count == 0)
                catalogArtifact = FinalGauntletCatalogBuildApi(payload);
            List<FinalGauntletCaseDescriptor> approved =
                BuildFinalGauntletRuntimeCatalog();
            HashSet<string> requested = new HashSet<string>(
                ReadStringList(payload, "stageACaseIds"),
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> selection;
            List<FinalGauntletCaseDescriptor> stageA;
            if (requested.Count > 0)
            {
                selection =
                    FinalConversationGauntletReadinessSelection
                        .SelectRequestedReadinessCases(
                            requested, 40);
                stageA = selection.TryGetValue(
                        "descriptors", out object selected)
                    && selected is List<FinalGauntletCaseDescriptor> typed
                        ? typed
                        : new List<FinalGauntletCaseDescriptor>();
            }
            else
            {
                Dictionary<string, object> readiness =
                    ConversationReadinessStatusApi(
                        new Dictionary<string, string>
                        {
                            ["campaignId"] = campaignId,
                            ["stateFingerprint"] = stateFingerprint
                        });
                selection =
                    FinalConversationGauntletReadinessSelection
                        .SelectMissingReadinessCases(
                            approved,
                            readiness,
                            CurrentConversationBuildVersion(),
                            40);
                stageA = selection.TryGetValue(
                        "descriptors", out object selected)
                    && selected is List<FinalGauntletCaseDescriptor> typed
                        ? typed
                        : new List<FinalGauntletCaseDescriptor>();
            }
            if (!ReadBool(selection, "ok", false))
                return selection;
            string runId = FirstNonEmpty(
                ReadString(payload, "runId", ""),
                "final-gauntlet-"
                    + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                Dictionary<string, object> started =
                    FinalConversationGauntletScheduler.StartStageA(
                        connection,
                        runId,
                        campaignId,
                        CurrentConversationBuildVersion(),
                        stageA,
                        ReadString(catalogArtifact, "fingerprint", ""),
                        stateFingerprint,
                        ReadString(payload, "baselineSaveName", ""));
                started["stageACaseCount"] = stageA.Count;
                started["stageAEstimatedProviderCalls"] =
                    ReadInt(selection, "estimatedProviderCalls", 0);
                started["stageAMissingCategories"] =
                    selection.TryGetValue(
                        "missingCategories", out object missing)
                        ? missing
                        : new List<object>();
                return started;
            }
        }

        private static Dictionary<string, object> FinalGauntletLeaseApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                Dictionary<string, object> leased =
                    FinalConversationGauntletScheduler.LeaseNext(
                    connection,
                    ReadString(payload, "runId", ""),
                    ReadString(payload, "controllerId", "local-controller"),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Math.Max(15, ReadLong(payload, "leaseSeconds", 120)));
                if (ReadBool(leased, "ok", false))
                {
                    string instanceId =
                        ReadString(leased, "caseInstanceId", "");
                    leased["case"] = QuerySql(
                        connection,
                        @"SELECT * FROM final_gauntlet_cases
WHERE run_id=$run AND case_instance_id=$case LIMIT 1;",
                        new Dictionary<string, object>
                        {
                            ["run"] = ReadString(payload, "runId", ""),
                            ["case"] = instanceId
                        }).FirstOrDefault()
                        ?? new Dictionary<string, object>();
                }
                return leased;
            }
        }

        private static Dictionary<string, object>
            FinalGauntletPromoteApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            Dictionary<string, object> catalog =
                ReadJsonObject(FinalGauntletCatalogPath());
            if (catalog.Count == 0)
                catalog = FinalGauntletCatalogBuildApi(payload);
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                Dictionary<string, object> manifest =
                    FinalConversationGauntletManifest.BuildStageBManifest(
                        BuildFinalGauntletRuntimeCatalog(),
                        ReadInt(payload, "seed", 1042));
                List<FinalGauntletCaseDescriptor> descriptors =
                    manifest["descriptors"]
                        as List<FinalGauntletCaseDescriptor>;
                Dictionary<string, object> promoted =
                    FinalConversationGauntletScheduler.PromoteStageB(
                        connection,
                        ReadString(payload, "runId", ""),
                        descriptors,
                        ReadString(catalog, "fingerprint", ""),
                        ReadString(payload, "settingsFingerprint", ""),
                        ReadString(payload, "stateFingerprint", ""));
                promoted["manifest"] = manifest;
                return promoted;
            }
        }

        private static Dictionary<string, object>
            FinalGauntletResumeApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
                return FinalConversationGauntletScheduler.Resume(
                    connection,
                    ReadString(payload, "runId", ""),
                    ReadString(payload, "stateFingerprint", ""),
                    ReadStringList(payload, "supportedModes"));
        }

        private static Dictionary<string, object>
            FinalGauntletPauseApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
                return FinalConversationGauntletScheduler.RequestPause(
                    connection, ReadString(payload, "runId", ""));
        }

        private static Dictionary<string, object> FinalGauntletResultApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                PersistFinalGauntletCaseEvidence(
                    connection, campaignId, payload);
                return FinalConversationGauntletScheduler.RecordResult(
                    connection,
                    ReadString(payload, "runId", ""),
                    ReadString(payload, "caseInstanceId", ""),
                    ReadString(payload, "correlationId", ""),
                    ReadBool(payload, "passed", false),
                    ReadBool(payload, "providerFailure", false),
                    ReadString(payload, "error", ""));
            }
        }

        private static void PersistFinalGauntletCaseEvidence(
            ReignDbConnection connection,
            string campaignId,
            Dictionary<string, object> payload)
        {
            string runId = ReadString(payload, "runId", "");
            string caseInstanceId =
                ReadString(payload, "caseInstanceId", "");
            if (string.IsNullOrWhiteSpace(runId)
                || string.IsNullOrWhiteSpace(caseInstanceId))
                return;
            string liveRunId = ReadString(payload, "liveRunId", "");
            Dictionary<string, object> liveRun =
                string.IsNullOrWhiteSpace(liveRunId)
                    ? new Dictionary<string, object>()
                    : LoadLiveTestRun(campaignId, liveRunId);
            Dictionary<string, object> evidence =
                new Dictionary<string, object>
                {
                    ["schema"] =
                        "reign-final-gauntlet-case-evidence-v4",
                    ["campaignId"] = campaignId,
                    ["runId"] = runId,
                    ["caseInstanceId"] = caseInstanceId,
                    ["correlationId"] =
                        ReadString(payload, "correlationId", ""),
                    ["liveRunId"] = liveRunId,
                    ["passed"] = ReadBool(payload, "passed", false),
                    ["providerFailure"] =
                        ReadBool(payload, "providerFailure", false),
                    ["error"] = ReadString(payload, "error", ""),
                    ["liveRun"] = liveRun,
                    ["capturedUtc"] = DateTime.UtcNow.ToString("o")
                };
            if (liveRun.Count > 0)
                evidence["sourceAuditEntries"] =
                    CollectLiveTestAuditEvidence(liveRun);
            ExecuteSql(
                connection,
                @"INSERT INTO final_gauntlet_evidence(
run_id,case_instance_id,evidence_key,schema_version,payload_json,created_ts)
VALUES($run,$case,'production_result',4,$payload,$ts)
ON CONFLICT(run_id,case_instance_id,evidence_key) DO UPDATE SET
schema_version=excluded.schema_version,
payload_json=excluded.payload_json,
created_ts=excluded.created_ts;",
                new Dictionary<string, object>
                {
                    ["run"] = runId,
                    ["case"] = caseInstanceId,
                    ["payload"] = Json.Serialize(evidence),
                    ["ts"] =
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
        }

        private static Dictionary<string, object> FinalGauntletCancelApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            string runId = ReadString(payload, "runId", "");
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                Dictionary<string, object> result =
                    FinalConversationGauntletScheduler.Cancel(
                        connection, runId);
                FinalConversationGauntletFaults.ClearRun(runId);
                return result;
            }
        }

        private static Dictionary<string, object>
            FinalGauntletActionEvidenceApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string action = ReadString(payload, "action", "");
            string requirementId = ReadString(payload, "requirementId", "");
            List<FinalGauntletActionApplicability> matrix =
                FinalConversationGauntletActionConformance.BuildMatrix(
                    new[] { action });
            FinalGauntletActionApplicability row = matrix.FirstOrDefault(item =>
                string.Equals(
                    item.RequirementId,
                    requirementId,
                    StringComparison.OrdinalIgnoreCase));
            List<string> errors =
                FinalConversationGauntletActionConformance.ValidateMatrix(
                    new[] { action }, matrix);
            return new Dictionary<string, object>
            {
                ["ok"] = row != null && errors.Count == 0,
                ["evidence"] = row == null
                    ? new Dictionary<string, object>()
                    : FinalConversationGauntletActionConformance.ToEvidence(row),
                ["errors"] = errors.Cast<object>().ToList()
            };
        }

        private static Dictionary<string, object>
            FinalGauntletFaultArmApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(
                payload, "campaignId", ResolveLogCampaignId(""));
            string runId = ReadString(payload, "runId", "");
            Dictionary<string, object> runtime =
                LoadLiveTestRuntime(campaignId);
            if (!IsFreshLiveTestRuntime(runtime))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "A fresh matching game heartbeat is required."
                };
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                Dictionary<string, object> run =
                    FinalGauntletStore.LoadRun(connection, runId);
                if (run.Count == 0)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "Final gauntlet run was not found."
                    };
            }
            if (!Enum.TryParse(
                ReadString(payload, "point", ""),
                true,
                out FinalGauntletFaultPoint point))
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["error"] = "Unknown gauntlet fault point."
                };
            return FinalConversationGauntletFaults.Arm(
                runId,
                ReadString(payload, "caseInstanceId", ""),
                campaignId,
                ReadString(runtime, "gameInstanceId", ""),
                point,
                ReadString(payload, "confirmation", ""),
                DateTimeOffset.UtcNow.AddMinutes(
                    Math.Max(1, Math.Min(
                        30, ReadInt(payload, "minutes", 5)))));
        }

        private static Dictionary<string, object> FinalGauntletRunStatusApi(
            Dictionary<string, string> query,
            bool includeReport)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue(
                "campaignId", out string campaign)
                ? campaign
                : ResolveLogCampaignId("");
            string runId = query.TryGetValue("runId", out string run)
                ? run : "";
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                Dictionary<string, object> stored =
                    FinalGauntletStore.LoadRun(connection, runId);
                Dictionary<string, object> schedule =
                    FinalConversationGauntletScheduler.LoadSchedule(
                        connection, runId);
                if (stored.Count == 0)
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "Final gauntlet run was not found."
                    };
                stored["schedule"] = schedule;
                stored["ok"] = true;
                if (includeReport)
                {
                    stored["attempts"] = QuerySql(
                        connection,
                        @"SELECT * FROM final_gauntlet_attempts
WHERE run_id=$run ORDER BY case_instance_id,attempt_number;",
                        new Dictionary<string, object> { ["run"] = runId });
                    stored["assertions"] = QuerySql(
                        connection,
                        @"SELECT * FROM final_gauntlet_assertions
WHERE run_id=$run ORDER BY case_instance_id,assertion_id;",
                        new Dictionary<string, object> { ["run"] = runId });
                    stored["evidence"] = QuerySql(
                        connection,
                        @"SELECT run_id,case_instance_id,evidence_key,
schema_version,created_ts FROM final_gauntlet_evidence
WHERE run_id=$run ORDER BY case_instance_id,evidence_key;",
                        new Dictionary<string, object> { ["run"] = runId });
                    stored["artifacts"] = ExportFinalGauntletReport(
                        connection, campaignId, runId);
                }
                return stored;
            }
        }

        private static Dictionary<string, object> FinalGauntletReviewPackApi(
            Dictionary<string, string> query)
        {
            query = query ?? new Dictionary<string, string>();
            string campaignId = query.TryGetValue(
                "campaignId", out string campaign)
                ? campaign : ResolveLogCampaignId("");
            string runId = query.TryGetValue("runId", out string run)
                ? run : "";
            using (ReignDbConnection connection =
                FinalGauntletStore.OpenControlConnection())
            {
                List<Dictionary<string, object>> items = QuerySql(
                    connection,
                    @"SELECT review_id,case_instance_id,
blinded_payload_json,created_ts
FROM final_gauntlet_review_items WHERE run_id=$run
ORDER BY review_id;",
                    new Dictionary<string, object> { ["run"] = runId });
                if (items.Count == 0)
                    ExportFinalGauntletReport(
                        connection, campaignId, runId);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["runId"] = runId,
                    ["items"] = QuerySql(
                        connection,
                        @"SELECT review_id,case_instance_id,
blinded_payload_json,created_ts
FROM final_gauntlet_review_items WHERE run_id=$run
ORDER BY review_id;",
                        new Dictionary<string, object> { ["run"] = runId })
                };
            }
        }

        private static Dictionary<string, object> FinalGauntletDescriptorMap(
            FinalGauntletCaseDescriptor descriptor)
        {
            return new Dictionary<string, object>
            {
                ["caseId"] = descriptor.CaseId,
                ["family"] = descriptor.Family,
                ["evaluationKind"] = descriptor.EvaluationKind,
                ["executionKind"] = descriptor.ExecutionKind,
                ["mode"] = descriptor.Mode,
                ["requiresProvider"] = descriptor.RequiresProvider,
                ["requiresGame"] = descriptor.RequiresGame,
                ["requirementIds"] = descriptor.RequirementIds.Cast<object>().ToList(),
                ["tags"] = descriptor.Tags.Cast<object>().ToList(),
                ["behavioralRequirement"] = descriptor.BehavioralRequirement,
                ["hardProhibitions"] = descriptor.HardProhibitions.Cast<object>().ToList(),
                ["prerequisiteCapabilities"] = descriptor.PrerequisiteCapabilities.Cast<object>().ToList(),
                ["evidenceNeeds"] = descriptor.EvidenceNeeds.Cast<object>().ToList()
            };
        }

        private static List<FinalGauntletCaseDescriptor>
            BuildFinalGauntletRuntimeCatalog()
        {
            string root = FindVerificationSourceRoot();
            string enumPath = string.IsNullOrWhiteSpace(root)
                ? string.Empty
                : Path.Combine(
                    root,
                    "ReignBeta",
                    "src",
                    "World",
                    "ReignWorldActionType.cs");
            List<string> actions = DiscoverActionNames(enumPath)
                .Where(value => !string.Equals(
                    value, "Unknown",
                    StringComparison.OrdinalIgnoreCase))
                .ToList();
            return FinalConversationGauntletCatalog
                .BuildExecutableCatalog(actions);
        }
    }
}
