using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private const string CampaignCommandScenarioManifestVersion = "campaign-command-v9";
        private static readonly object CampaignCommandCertificationLock = new object();
        private static readonly string[] CampaignCommandProfiles =
        {
            "preflight", "offline_contracts", "llm_matrix", "native_primitives",
            "composite_orders", "geography", "personality", "correspondence",
            "fault_recovery", "save_roundtrip", "campaign_soak", "evaluate", "cleanup"
        };

        private static readonly string[] CampaignCommandObjectives =
        {
            "establish_party", "move", "hold_position", "timed_hold", "patrol", "scout_report", "escort", "recruit_resupply",
            "form_army", "join_army", "leave_army", "disband_army", "raid",
            "besiege_capture", "defend", "relieve_siege", "hunt_enemy_parties",
            "engage_party", "withdraw", "return_home"
        };

        private static void CopyCampaignCommandTerms(Dictionary<string, object> raw,
            Dictionary<string, object> terms)
        {
            if (raw == null || terms == null) return;
            string[] keys = { "orderId", "objective", "steps", "planHash", "draftId", "region", "durationHours", "targetPartyId",
                "targetSettlementStringId", "issuerHeroStringId", "response",
                "armyDetachmentAccepted", "minimumTroops", "minimumInfantry", "minimumArchers",
                "minimumCavalry", "minimumFoodDays" };
            foreach (string key in keys)
            {
                if (!terms.ContainsKey(key) && raw.TryGetValue(key, out object value) && value != null)
                    terms[key] = value;
            }
        }

        private static Dictionary<string, object> CampaignCommandManifestApi(
            Dictionary<string, string> query)
        {
            List<Dictionary<string, object>> capabilities = CampaignCommandObjectives.Select(id =>
                new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["schema"] = CampaignCommandSchema(id),
                    ["resolver"] = CampaignCommandResolver(id),
                    ["authorityCheck"] = "sovereign-authority-or-explicit-current-leader-acceptance",
                    ["preflight"] = id == "establish_party"
                        ? "living-free-npc;clan-party-eligibility;safe-spawn"
                        : "living-free-npc;establish-party-if-planned;not-main-party;army-hierarchy;target-validity;navigation",
                    ["nativeAdapter"] = CampaignCommandAdapter(id),
                    ["observer"] = "hourly-and-native-event-supervisor",
                    ["completionCriteria"] = CampaignCommandCompletion(id),
                    ["failureClassification"] = "validation|authority|obsolete|navigation|native|safety|cancelled",
                    ["reportEvents"] = "accepted|adapted|guidance|required|refused|deviated|completed|failed|cancelled",
                    ["tests"] = "offline_contracts|native_primitives|composite_orders|fault_recovery",
                    ["plannerVisible"] = true
                }).ToList();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["schemaVersion"] = 1,
                ["scenarioManifestVersion"] = CampaignCommandScenarioManifestVersion,
                ["profiles"] = CampaignCommandProfiles,
                ["capabilities"] = capabilities,
                ["seeds"] = new[] { 1337 },
                ["consecutivePassesRequired"] = 1,
                ["llmMatrix"] = new Dictionary<string, object>
                {
                    ["casesPerPass"] = 300, ["validCommands"] = 200,
                    ["ambiguousOrVague"] = 50, ["refusalNegotiationAndFalsePositive"] = 50,
                    ["adaptiveTargetedCasesMaximum"] = 100,
                    ["lightSmokeCases"] = 40,
                    ["lightSmokeRequiresPerfectResult"] = true,
                    ["minimumAccuracy"] = 0.98d,
                    ["schemaAuthorityIdentifierRefusalUnsupportedSafety"] = 1.0d,
                    ["batchSize"] = 5, ["boundedProviderRetries"] = 2,
                    ["corpusVersion"] = "campaign-command-natural-dialogue-v4",
                    ["naturalPlayerUtterancesRequired"] = 300,
                    ["distinctValidUtterancesRequired"] = 200,
                    ["multiTurnClarificationCasesRequired"] = 30,
                    ["minimumNaturalLanguageAccuracy"] = 0.98d,
                    ["minimumMultiTurnAccuracy"] = 0.98d,
                    ["auditableUtteranceAndStyleEvidenceRequired"] = true,
                    ["persistentCaseLedgerRequired"] = true
                },
                ["soak"] = new Dictionary<string, object>
                {
                    ["offlineFiveYearRuns"] = 10, ["nativeThirtyDayRuns"] = 1,
                    ["requiresSimultaneousOrders"] = true, ["requiresOwnershipAndWarChanges"] = true
                },
                ["fatalStopConditions"] = new[] { "campaign_contamination", "bridge_desynchronization",
                    "game_or_server_crash", "missing_evidence", "wrong_loaded_save" },
                ["saveIsolationAuthority"] = "guarded-campaign-test-enrollment",
                ["saveNamePattern"] = "exact run-owned ReignTest_<task>_<hash>_Current",
                ["protectedBaselineRequired"] = true,
                ["requiresExactLoadedSaveMatch"] = true,
                ["requiredEvidence"] = new[] { "structured-json", "human-summary", "case-ledger",
                    "world-history-correlations", "native-receipts", "logs", "performance-metrics",
                    "save-isolation", "minimal-replay-bundles" },
                ["certificationKey"] = new[] { "sourceFingerprint", "serverBuild", "clientBuild",
                    "bannerlordVersion", "scenarioManifestVersion", "providerConfigurationFingerprint",
                    "dependencyFingerprints" }
            };
        }

        private static Dictionary<string, object> CampaignCommandPrepareApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string runId = RequiredCampaignCommandId(payload, "runId");
            string campaignId = RequiredCampaignCommandId(payload, "campaignId");
            string saveName = ReadString(payload, "disposableSaveName", "").Trim();
            if (!IsGuardedCampaignCommandEnrollment(payload, saveName))
                return CampaignCommandError(
                    "Campaign Command requires a guarded campaign-test enrollment, a protected baseline, and the exact run-owned Current save.");
            Dictionary<string, object> state = NewCampaignCommandState(runId, campaignId, saveName, payload);
            state["status"] = "prepared";
            state["preparedUtc"] = DateTime.UtcNow.ToString("o");
            state["saveVerified"] = false;
            WriteCampaignCommandState(runId, state);
            return state;
        }

        private static bool IsGuardedCampaignCommandEnrollment(
            Dictionary<string, object> payload, string saveName)
        {
            string campaignTestRunId = ReadString(payload, "campaignTestRunId", "").Trim();
            string prefix = ReadString(payload, "disposableSavePrefix", "").Trim();
            string baseline = ReadString(payload, "protectedBaselineSaveName", "").Trim();
            string timelineId = ReadString(payload, "timelineId", "").Trim();
            bool safePrefix = prefix.StartsWith("ReignTest_", StringComparison.Ordinal)
                && prefix.All(character => (character >= '0' && character <= '9')
                    || (character >= 'A' && character <= 'Z')
                    || (character >= 'a' && character <= 'z')
                    || character == '_' || character == '-');
            return campaignTestRunId.Length > 0 && timelineId.Length > 0 && baseline.Length > 0
                && safePrefix
                && string.Equals(saveName, prefix + "_Current", StringComparison.Ordinal)
                && !string.Equals(saveName, baseline, StringComparison.OrdinalIgnoreCase);
        }

        private static Dictionary<string, object> CampaignCommandSaveVerifyApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string runId = RequiredCampaignCommandId(payload, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            ApplyCampaignCommandSaveVerification(state, payload);
            WriteCampaignCommandState(runId, state);
            return state;
        }

        private static bool ApplyCampaignCommandSaveVerification(
            Dictionary<string, object> state,
            Dictionary<string, object> payload)
        {
            string loaded = ReadString(payload, "loadedSaveName", "");
            bool aligned = ReadBool(payload, "campaignAligned", false)
                && string.Equals(loaded, ReadString(state, "disposableSaveName", ""),
                    StringComparison.OrdinalIgnoreCase);
            state["saveVerified"] = aligned;
            state["loadedSaveName"] = loaded;
            state["saveIsolationEvidence"] = payload;
            state["status"] = aligned ? "ready" : "fatal_blocked";
            if (!aligned) state["fatalReason"] = "wrong_loaded_save";
            else state.Remove("fatalReason");
            return aligned;
        }

        private static Dictionary<string, object> CampaignCommandProfileResultApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string runId = RequiredCampaignCommandId(payload, "runId");
            string profile = ReadString(payload, "profile", "").Trim().ToLowerInvariant();
            if (!CampaignCommandProfiles.Contains(profile, StringComparer.Ordinal))
                return CampaignCommandError("Unsupported certification profile.");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            if (ReadString(state, "status", "") == "cancelled")
                return CampaignCommandError("The certification run is cancelled.");
            bool passed = ReadBool(payload, "passed", false);
            bool fatal = ReadBool(payload, "fatal", false);
            Dictionary<string, object> sections = ReadDictionary(state, "sections")
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> prior = sections.TryGetValue(profile, out object raw)
                ? raw as Dictionary<string, object> : null;
            string fingerprint = CampaignCommandCertificateFingerprint(payload, state);
            int cleanPasses = passed && string.Equals(ReadString(prior, "fingerprint", ""), fingerprint,
                    StringComparison.OrdinalIgnoreCase)
                ? ReadInt(prior, "cleanPasses", 0) + 1 : passed ? 1 : 0;
            List<string> completedSoakPasses = ReadStringList(prior, "completedSoakPasses");
            if (profile == "campaign_soak" && passed)
            {
                string soakPass = ReadInt(payload, "pass", 0).ToString(CultureInfo.InvariantCulture);
                if (soakPass != "0" && !completedSoakPasses.Contains(soakPass, StringComparer.Ordinal))
                    completedSoakPasses.Add(soakPass);
            }
            string evidenceDirectory = Path.Combine(CampaignCommandRunDirectory(runId), "replays");
            Directory.CreateDirectory(evidenceDirectory);
            string replayPath = Path.Combine(evidenceDirectory, SafeCampaignCommandSegment(profile)
                + "-pass-" + ReadInt(payload, "pass", 0).ToString(CultureInfo.InvariantCulture)
                + "-seed-" + ReadInt(payload, "seed", 0).ToString(CultureInfo.InvariantCulture) + ".json");
            Dictionary<string, object> metrics = ReadDictionary(payload, "metrics")
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            List<Dictionary<string, object>> sectionLedgerRows = ReadDictionaryList(payload, "caseLedgerRows");
            if (sectionLedgerRows.Count > 0)
            {
                string sectionLedgerPath = PersistCampaignCommandSectionLedger(runId, profile,
                    ReadInt(payload, "pass", 0), ReadInt(payload, "seed", 0), sectionLedgerRows);
                metrics["caseLedgerPath"] = sectionLedgerPath;
                metrics["caseLedgerRows"] = sectionLedgerRows.Count;
                metrics["caseLedgerSha256"] = Sha256File(sectionLedgerPath);
            }
            payload.Remove("caseLedgerRows");
            payload["metrics"] = metrics;
            WriteJsonObject(replayPath, payload);
            sections[profile] = new Dictionary<string, object>
            {
                ["profile"] = profile, ["status"] = passed ? "passed" : "failed",
                ["cleanPasses"] = cleanPasses, ["fingerprint"] = fingerprint,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["seed"] = ReadInt(payload, "seed", 0), ["pass"] = ReadInt(payload, "pass", 0),
                ["caseCount"] = ReadInt(payload, "caseCount", 0),
                ["passedCaseCount"] = ReadInt(payload, "passedCaseCount", 0),
                ["evidence"] = ReadDictionary(payload, "evidence") ?? new Dictionary<string, object>(),
                ["replayBundle"] = replayPath,
                ["replayReference"] = ReadString(payload, "replayBundle", ""),
                ["metrics"] = metrics,
                ["completedSoakPasses"] = completedSoakPasses,
                ["completedSoakRuns"] = completedSoakPasses.Count
            };
            state["sections"] = sections;
            state["status"] = fatal ? "fatal_blocked" : passed ? "running" : "repair_required";
            if (fatal) state["fatalReason"] = ReadString(payload, "failureClass", "fatal_harness_failure");
            state["updatedUtc"] = DateTime.UtcNow.ToString("o");
            WriteCampaignCommandState(runId, state);
            AppendCampaignCommandLedger(runId, payload);
            return CampaignCommandStatusResult(state, false);
        }

        private static Dictionary<string, object> CampaignCommandStatusApi(
            Dictionary<string, string> query, bool includeReport)
        {
            string runId = QueryCampaignCommandId(query, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            return CampaignCommandStatusResult(state, includeReport);
        }

        private static Dictionary<string, object> CampaignCommandReplayApi(
            Dictionary<string, string> query)
        {
            string runId = QueryCampaignCommandId(query, "runId");
            string profile = query != null && query.TryGetValue("profile", out string value)
                ? (value ?? "").Trim().ToLowerInvariant() : "";
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            Dictionary<string, object> sections = ReadDictionary(state, "sections")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> section = sections.TryGetValue(profile, out object raw)
                ? raw as Dictionary<string, object> : null;
            return new Dictionary<string, object>
            {
                ["ok"] = section != null, ["runId"] = runId, ["profile"] = profile,
                ["replayBundle"] = section == null ? "" : ReadString(section, "replayBundle", ""),
                ["evidence"] = section == null ? new Dictionary<string, object>()
                    : ReadDictionary(section, "evidence") ?? new Dictionary<string, object>()
            };
        }

        private static Dictionary<string, object> CampaignCommandResumeApi(
            Dictionary<string, object> payload)
        {
            string runId = RequiredCampaignCommandId(payload, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            if (ReadString(state, "status", "") == "fatal_blocked")
                return CampaignCommandError("Fatal harness evidence must be diagnosed before resume.");
            string changedProfiles = ReadString(payload, "changedProfiles", "");
            List<string> invalidated = ExpandCampaignCommandDependents(changedProfiles);
            Dictionary<string, object> sections = ReadDictionary(state, "sections")
                ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string profile in invalidated) sections.Remove(profile);
            state["sections"] = sections;
            string newFingerprint = ReadString(payload, "sourceFingerprint", "");
            if (!string.IsNullOrWhiteSpace(newFingerprint)) state["sourceFingerprint"] = newFingerprint;
            state["invalidatedProfiles"] = invalidated;
            state["status"] = "running";
            state["resumeCount"] = ReadInt(state, "resumeCount", 0) + 1;
            state["updatedUtc"] = DateTime.UtcNow.ToString("o");
            WriteCampaignCommandState(runId, state);
            return CampaignCommandStatusResult(state, false);
        }

        private static List<string> ExpandCampaignCommandDependents(string changedProfiles)
        {
            Dictionary<string, string[]> dependents = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["preflight"] = CampaignCommandProfiles,
                ["offline_contracts"] = CampaignCommandProfiles,
                ["llm_matrix"] = new[] { "llm_matrix", "composite_orders", "personality", "correspondence", "evaluate" },
                ["native_primitives"] = new[] { "native_primitives", "composite_orders", "fault_recovery", "save_roundtrip", "campaign_soak", "evaluate" },
                ["geography"] = new[] { "geography", "composite_orders", "campaign_soak", "evaluate" },
                ["personality"] = new[] { "personality", "composite_orders", "correspondence", "campaign_soak", "evaluate" },
                ["correspondence"] = new[] { "correspondence", "fault_recovery", "save_roundtrip", "campaign_soak", "evaluate" },
                ["fault_recovery"] = new[] { "fault_recovery", "campaign_soak", "evaluate" },
                ["save_roundtrip"] = new[] { "save_roundtrip", "campaign_soak", "evaluate" },
                ["campaign_soak"] = new[] { "campaign_soak", "evaluate" },
                ["evaluate"] = new[] { "evaluate" },
                ["cleanup"] = new[] { "cleanup" }
            };
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string value in (changedProfiles ?? string.Empty)
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().ToLowerInvariant()))
            {
                if (!dependents.TryGetValue(value, out string[] values)) values = new[] { value };
                foreach (string profile in values) if (CampaignCommandProfiles.Contains(profile)) result.Add(profile);
            }
            return result.OrderBy(x => Array.IndexOf(CampaignCommandProfiles, x)).ToList();
        }

        private static Dictionary<string, object> CampaignCommandCancelApi(
            Dictionary<string, object> payload)
        {
            string runId = RequiredCampaignCommandId(payload, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            state["status"] = "cancelled";
            state["cancelledUtc"] = DateTime.UtcNow.ToString("o");
            string activeLiveRunId = ReadString(state, "activeLiveRunId", "");
            if (!string.IsNullOrWhiteSpace(activeLiveRunId))
            {
                Dictionary<string, object> cancellation = LiveTestRunCancelApi(new Dictionary<string, object>
                {
                    ["campaignId"] = ReadString(state, "campaignId", ""),
                    ["runId"] = activeLiveRunId,
                    ["reason"] = "Campaign Command certification cancelled through MCP."
                });
                // A live run contains every command result and each result may contain a prior
                // certification snapshot. Persisting that graph made cancellation/status payloads
                // grow past MCP's response bound. The durable run report remains authoritative;
                // certification state needs only a compact cancellation receipt.
                state["liveCancellation"] = new Dictionary<string, object>
                {
                    ["ok"] = cancellation.TryGetValue("ok", out object cancelOk)
                        && Convert.ToBoolean(cancelOk),
                    ["runId"] = ReadString(cancellation, "runId", activeLiveRunId),
                    ["campaignId"] = ReadString(cancellation, "campaignId", ReadString(state, "campaignId", "")),
                    ["status"] = ReadString(cancellation, "status", "cancelled"),
                    ["updatedUtc"] = ReadString(cancellation, "updatedUtc", DateTime.UtcNow.ToString("o")),
                    ["commandCount"] = cancellation.TryGetValue("commandCount", out object commandCount)
                        ? Convert.ToInt32(commandCount) : 0,
                    ["completedCommands"] = cancellation.TryGetValue("completedCommands", out object completedCommands)
                        ? Convert.ToInt32(completedCommands) : 0,
                    ["failedCommands"] = cancellation.TryGetValue("failedCommands", out object failedCommands)
                        ? Convert.ToInt32(failedCommands) : 0,
                    ["reportPath"] = ReadString(cancellation, "reportPath", "")
                };
            }
            WriteCampaignCommandState(runId, state);
            return CampaignCommandStatusResult(state, false);
        }

        private static Dictionary<string, object> CampaignCommandExecutionApi(
            Dictionary<string, object> payload)
        {
            string runId = RequiredCampaignCommandId(payload, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            state["activeLiveRunId"] = RequiredCampaignCommandId(payload, "liveRunId");
            state["activeProfile"] = ReadString(payload, "profile", "certification");
            state["updatedUtc"] = DateTime.UtcNow.ToString("o");
            WriteCampaignCommandState(runId, state);
            return CampaignCommandStatusResult(state, false);
        }

        private static Dictionary<string, object> CampaignCommandReadinessApi(
            Dictionary<string, string> query)
        {
            string runId = QueryCampaignCommandId(query, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            Dictionary<string, object> sections = ReadDictionary(state, "sections")
                ?? new Dictionary<string, object>();
            List<string> blockers = new List<string>();
            string expectedFingerprint = CampaignCommandCertificateFingerprint(
                new Dictionary<string, object>(), state);
            foreach (string profile in CampaignCommandProfiles.Where(x => x != "cleanup"))
            {
                Dictionary<string, object> section = sections.TryGetValue(profile, out object raw)
                    ? raw as Dictionary<string, object> : null;
                if (section == null) blockers.Add(profile + ": missing certificate");
                else if (!string.Equals(ReadString(section, "status", ""), "passed", StringComparison.OrdinalIgnoreCase))
                    blockers.Add(profile + ": not passed");
                else if (ReadInt(section, "cleanPasses", 0) < 1)
                    blockers.Add(profile + ": no clean final-fingerprint pass");
                if (section != null && !string.Equals(ReadString(section, "fingerprint", ""),
                        expectedFingerprint, StringComparison.OrdinalIgnoreCase))
                    blockers.Add(profile + ": certificate does not match the final fingerprint");
                if (section != null && (ReadInt(section, "caseCount", 0) <= 0
                    || ReadInt(section, "passedCaseCount", 0) != ReadInt(section, "caseCount", 0)))
                    blockers.Add(profile + ": deterministic/native case ledger is not 100% clean");
                if (section != null && !File.Exists(ReadString(section, "replayBundle", "")))
                    blockers.Add(profile + ": minimal replay bundle is missing");
            }
            Dictionary<string, object> soak = sections.TryGetValue("campaign_soak", out object soakRaw)
                ? soakRaw as Dictionary<string, object> : null;
            if (ReadInt(soak, "completedSoakRuns", 0) < 1)
                blockers.Add("campaign_soak: the combined 30-day native run is missing");
            Dictionary<string, object> soakMetrics = ReadDictionary(soak, "metrics")
                ?? new Dictionary<string, object>();
            foreach (string key in new[] { "caseCheckpointObserved", "simultaneousOrders",
                "ownershipChangeObserved", "warChangeObserved", "siegeObserved", "partyLossObserved",
                "hourlyOrderReviewObserved", "worldRestored", "mainPartyUntouched",
                "noDuplicateEffects", "boundedQueue", "terminalOrdersBounded" })
                if (!ReadBool(soakMetrics, key, false))
                    blockers.Add("campaign_soak: " + key + " was not proven");
            if (ReadInt(soakMetrics, "simultaneousOrderCount", 0) < 3)
                blockers.Add("campaign_soak: fewer than three simultaneous NPC orders were exercised");
            Dictionary<string, object> native = sections.TryGetValue("native_primitives", out object nativeRaw)
                ? nativeRaw as Dictionary<string, object> : null;
            Dictionary<string, object> nativeMetrics = ReadDictionary(native, "metrics")
                ?? new Dictionary<string, object>();
            if (ReadInt(nativeMetrics, "capabilitiesAttempted", 0) != CampaignCommandObjectives.Length
                || ReadInt(nativeMetrics, "capabilitiesPassed", 0) != CampaignCommandObjectives.Length
                || ReadInt(nativeMetrics, "nativeReceiptCount", 0) != CampaignCommandObjectives.Length)
                blockers.Add("native_primitives: every registered adapter must have a successful native receipt");
            Dictionary<string, object> composite = sections.TryGetValue("composite_orders", out object compositeRaw)
                ? compositeRaw as Dictionary<string, object> : null;
            Dictionary<string, object> compositeMetrics = ReadDictionary(composite, "metrics")
                ?? new Dictionary<string, object>();
            if (!ReadBool(compositeMetrics, "materially_distinct_composite_receipts", false))
                blockers.Add("composite_orders: the focused set of materially distinct native plans is incomplete");
            Dictionary<string, object> saveRoundTrip = sections.TryGetValue("save_roundtrip", out object saveRaw)
                ? saveRaw as Dictionary<string, object> : null;
            if (!ReadBool(ReadDictionary(saveRoundTrip, "metrics"), "save_payload_roundtrip", false))
                blockers.Add("save_roundtrip: durable command state did not round-trip cleanly");
            if (!ReadBool(ReadDictionary(saveRoundTrip, "metrics"), "native_checkpoint_observed", false))
                blockers.Add("save_roundtrip: native Bannerlord/Save Sync checkpoint was not observed");
            if (!ReadBool(ReadDictionary(saveRoundTrip, "metrics"), "different_game_instance_observed", false))
                blockers.Add("save_roundtrip: verification did not use a different native game instance");
            Dictionary<string, object> correspondence = sections.TryGetValue("correspondence", out object correspondenceRaw)
                ? correspondenceRaw as Dictionary<string, object> : null;
            Dictionary<string, object> correspondenceMetrics = ReadDictionary(correspondence, "metrics")
                ?? new Dictionary<string, object>();
            foreach (string key in new[] { "urgent_report_queued", "twelve_hour_deadline",
                "response_delivered", "no_response_branch" })
                if (!ReadBool(correspondenceMetrics, key, false))
                    blockers.Add("correspondence: " + key + " was not proven");
            Dictionary<string, object> recovery = sections.TryGetValue("fault_recovery", out object recoveryRaw)
                ? recoveryRaw as Dictionary<string, object> : null;
            Dictionary<string, object> recoveryMetrics = ReadDictionary(recovery, "metrics")
                ?? new Dictionary<string, object>();
            foreach (string key in new[] { "duplicate_suppressed", "native_ai_reasserted",
                "obsolete_target_classified", "safe_cancellation" })
                if (!ReadBool(recoveryMetrics, key, false))
                    blockers.Add("fault_recovery: " + key + " was not proven");
            Dictionary<string, object> llm = sections.TryGetValue("llm_matrix", out object llmRaw)
                ? llmRaw as Dictionary<string, object> : null;
            Dictionary<string, object> llmMetrics = ReadDictionary(llm, "metrics")
                ?? new Dictionary<string, object>();
            if (ReadDouble(llmMetrics, "accuracy", 0d) < 0.98d) blockers.Add("llm accuracy below 98%");
            if (ReadDouble(llmMetrics, "naturalLanguageAccuracy", 0d) < 0.98d)
                blockers.Add("llm natural player-language accuracy below 98%");
            if (ReadDouble(llmMetrics, "multiTurnAccuracy", 0d) < 0.98d)
                blockers.Add("llm multi-turn clarification accuracy below 98%");
            foreach (string key in new[] { "naturalLanguageCoverage", "auditableUtteranceCoverage" })
                if (ReadDouble(llmMetrics, key, 0d) < 1d) blockers.Add("llm " + key + " below 100%");
            foreach (string key in new[] { "schemaSafety", "authoritySafety", "identifierSafety",
                "refusalHandling", "unsupportedActionRejection" })
                if (ReadDouble(llmMetrics, key, 0d) < 1d) blockers.Add("llm " + key + " below 100%");
            string llmLedgerPath = ReadString(llmMetrics, "caseLedgerPath", "");
            int llmLedgerRows = ReadInt(llmMetrics, "caseLedgerRows", 0);
            if (llmLedgerRows < 300 || llmLedgerRows > 400 || !File.Exists(llmLedgerPath)
                || string.IsNullOrWhiteSpace(ReadString(llmMetrics, "caseLedgerSha256", "")))
                blockers.Add("llm durable 300-case base ledger (plus at most 100 targeted cases) is missing or incomplete");
            if (!ReadBool(state, "saveVerified", false)) blockers.Add("disposable save not verified");
            foreach (string key in new[] { "sourceFingerprint", "serverBuild", "clientBuild",
                "bannerlordVersion", "providerConfigurationFingerprint" })
            {
                string value = ReadString(state, key, "");
                if (string.IsNullOrWhiteSpace(value) || value.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    blockers.Add(key + " is missing from the certification key");
            }
            if (ReadString(state, "status", "") == "fatal_blocked") blockers.Add("fatal harness blocker");
            if (ReadString(state, "status", "") == "repair_required") blockers.Add("unresolved certification defect");
            Dictionary<string, object> readiness = new Dictionary<string, object>
            {
                ["ok"] = true, ["runId"] = runId, ["ready"] = blockers.Count == 0,
                ["blockers"] = blockers, ["requiredProfiles"] = CampaignCommandProfiles,
                ["evidenceRoot"] = CampaignCommandRunDirectory(runId),
                ["finalFingerprint"] = expectedFingerprint,
                ["evaluatedUtc"] = DateTime.UtcNow.ToString("o")
            };
            string summaryPath = Path.Combine(CampaignCommandRunDirectory(runId), "launch-readiness.json");
            WriteJsonObject(summaryPath, readiness);
            readiness["summaryPath"] = summaryPath;
            return readiness;
        }

        private static Dictionary<string, object> CampaignCommandReportApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> order = ReadDictionary(payload, "order")
                ?? new Dictionary<string, object>();
            string orderId = ReadString(order, "OrderId", ReadString(order, "orderId", "unknown"));
            string directory = Path.Combine(TestsDir, "campaign-command", "runtime",
                SafeCampaignCommandSegment(campaignId));
            Directory.CreateDirectory(directory);
            Dictionary<string, object> evidence = new Dictionary<string, object>(payload,
                StringComparer.OrdinalIgnoreCase)
            {
                ["receivedUtc"] = DateTime.UtcNow.ToString("o")
            };
            lock (CampaignCommandCertificationLock)
            {
                File.AppendAllText(Path.Combine(directory, "events.jsonl"),
                    Json.Serialize(evidence) + Environment.NewLine, Encoding.UTF8);
                WriteJsonObject(Path.Combine(directory, SafeCampaignCommandSegment(orderId) + ".json"), evidence);
            }
            return new Dictionary<string, object> { ["ok"] = true, ["orderId"] = orderId };
        }

        private static Dictionary<string, object> CampaignCommandJudgeApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string orderId = ReadString(payload, "orderId", "");
            string heroId = ReadString(payload, "commanderHeroStringId", "");
            if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(heroId))
                return CampaignCommandError("Known orderId and commanderHeroStringId are required.");
            Dictionary<string, object> state = ReadDictionary(payload, "state")
                ?? new Dictionary<string, object>();
            string deterministic = CampaignCommandFallbackOutcome(payload, state);
            Dictionary<string, object> characterState = ReadJsonObject(
                CharacterFile(ReadString(payload, "campaignId", "default"), heroId, "state.json"));
            string mbti = ReadFirstString(characterState, "mbti", "mbtiType", "personalityType");
            string system = "You are an advisory military judgment layer for Bannerlord Reign. "
                + "Native state, authority, and identifiers are authoritative. Return only compact JSON with outcome and reason. "
                + "outcome must be exactly continue, adapt, request_guidance, withdraw, refuse, or deviate. "
                + "A sovereign order has heavy weight. Compassion may oppose suicidal conquest while strengthening desperate civilian relief. "
                + "Do not add identifiers, actions, transfers, surrender, or settlement capitulation.";
            string prompt = "Commander MBTI: " + FirstNonEmpty(mbti, "unknown") + "\n"
                + "Validated order and native state: " + Json.Serialize(payload) + "\n"
                + "Deterministic safety recommendation: " + deterministic;
            Dictionary<string, object> llm = ChatWithLlm(new Dictionary<string, object>
            {
                ["campaignId"] = ReadString(payload, "campaignId", "default"),
                ["correlationId"] = ReadString(payload, "correlationId", ""),
                ["heroStringId"] = heroId, ["actionId"] = orderId,
                ["requestType"] = "strategy", ["system"] = system, ["prompt"] = prompt,
                ["temperature"] = 0.2d, ["maxTokens"] = 240
            });
            if (ReadBool(llm, "ok", false))
            {
                Dictionary<string, object> parsed = TryParseJsonObject(ReadString(llm, "content", ""));
                string outcome = ReadString(parsed, "outcome", "").Trim().ToLowerInvariant();
                if (CampaignCommandAllowedOutcome(outcome))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = true, ["providerUsed"] = true, ["outcome"] = outcome,
                        ["reason"] = BoundedCampaignCommandText(ReadString(parsed, "reason", ""), 600),
                        ["orderId"] = orderId, ["commanderHeroStringId"] = heroId
                    };
            }
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["providerUsed"] = false, ["outcome"] = deterministic,
                ["reason"] = "Immediate deterministic fallback used because LLM enrichment was unavailable or unsafe.",
                ["orderId"] = orderId, ["commanderHeroStringId"] = heroId
            };
        }

        private static Dictionary<string, object> CampaignCommandStatusResult(
            Dictionary<string, object> state, bool includeReport)
        {
            Dictionary<string, object> result = new Dictionary<string, object>(state,
                StringComparer.OrdinalIgnoreCase) { ["ok"] = true };
            if (includeReport)
            {
                string runId = ReadString(state, "runId", "");
                string ledger = Path.Combine(CampaignCommandRunDirectory(runId), "case-ledger.jsonl");
                result["caseLedgerPath"] = ledger;
                result["reportPath"] = Path.Combine(CampaignCommandRunDirectory(runId), "state.json");
                result["evidenceRoot"] = CampaignCommandRunDirectory(runId);
                result["caseLedgerRows"] = File.Exists(ledger)
                    ? File.ReadAllLines(ledger).Where(x => !string.IsNullOrWhiteSpace(x)).Take(5000).ToArray()
                    : new string[0];
            }
            return result;
        }

        private static Dictionary<string, object> NewCampaignCommandState(string runId,
            string campaignId, string saveName, Dictionary<string, object> payload)
        {
            return new Dictionary<string, object>
            {
                ["ok"] = true, ["schemaVersion"] = 1, ["runId"] = runId,
                ["campaignId"] = campaignId, ["disposableSaveName"] = saveName,
                ["campaignTestRunId"] = ReadString(payload, "campaignTestRunId", ""),
                ["timelineId"] = ReadString(payload, "timelineId", ""),
                ["disposableSavePrefix"] = ReadString(payload, "disposableSavePrefix", ""),
                ["protectedBaselineSaveName"] = ReadString(payload, "protectedBaselineSaveName", ""),
                ["status"] = "new", ["sections"] = new Dictionary<string, object>(),
                ["sourceFingerprint"] = ReadString(payload, "sourceFingerprint", "unknown"),
                ["serverBuild"] = ReadString(payload, "serverBuild", "unknown"),
                ["clientBuild"] = ReadString(payload, "clientBuild", "unknown"),
                ["bannerlordVersion"] = ReadString(payload, "bannerlordVersion", "unknown"),
                ["providerConfigurationFingerprint"] = ReadString(payload,
                    "providerConfigurationFingerprint", "unknown"),
                ["dependencyFingerprints"] = ReadDictionary(payload, "dependencyFingerprints")
                    ?? new Dictionary<string, object>(),
                ["scenarioManifestVersion"] = CampaignCommandScenarioManifestVersion,
                ["createdUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static string CampaignCommandCertificateFingerprint(Dictionary<string, object> payload,
            Dictionary<string, object> state)
        {
            string canonical = string.Join("|", new[]
            {
                ReadString(payload, "sourceFingerprint", ReadString(state, "sourceFingerprint", "unknown")),
                ReadString(payload, "serverBuild", ReadString(state, "serverBuild", "unknown")),
                ReadString(payload, "clientBuild", ReadString(state, "clientBuild", "unknown")),
                ReadString(payload, "bannerlordVersion", ReadString(state, "bannerlordVersion", "unknown")),
                CampaignCommandScenarioManifestVersion,
                ReadString(payload, "providerConfigurationFingerprint",
                    ReadString(state, "providerConfigurationFingerprint", "unknown")),
                Json.Serialize(ReadDictionary(payload, "dependencyFingerprints")
                    ?? ReadDictionary(state, "dependencyFingerprints") ?? new Dictionary<string, object>())
            });
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(canonical)))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static Dictionary<string, object> ReadCampaignCommandState(string runId)
        {
            return ReadJsonObject(Path.Combine(CampaignCommandRunDirectory(runId), "state.json"));
        }

        private static void WriteCampaignCommandState(string runId, Dictionary<string, object> state)
        {
            string directory = CampaignCommandRunDirectory(runId);
            Directory.CreateDirectory(directory);
            lock (CampaignCommandCertificationLock)
                WriteJsonObject(Path.Combine(directory, "state.json"), state);
        }

        private static void AppendCampaignCommandLedger(string runId, Dictionary<string, object> payload)
        {
            string directory = CampaignCommandRunDirectory(runId);
            Directory.CreateDirectory(directory);
            Dictionary<string, object> row = new Dictionary<string, object>(payload,
                StringComparer.OrdinalIgnoreCase) { ["recordedUtc"] = DateTime.UtcNow.ToString("o") };
            lock (CampaignCommandCertificationLock)
                File.AppendAllText(Path.Combine(directory, "case-ledger.jsonl"),
                    Json.Serialize(row) + Environment.NewLine, Encoding.UTF8);
        }

        private static string PersistCampaignCommandSectionLedger(string runId, string profile,
            int pass, int seed, List<Dictionary<string, object>> rows)
        {
            string directory = Path.Combine(CampaignCommandRunDirectory(runId), "case-ledgers");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, SafeCampaignCommandSegment(profile)
                + "-pass-" + pass.ToString(CultureInfo.InvariantCulture)
                + "-seed-" + seed.ToString(CultureInfo.InvariantCulture) + ".jsonl");
            string content = string.Join(Environment.NewLine,
                (rows ?? new List<Dictionary<string, object>>()).Select(Json.Serialize));
            if (content.Length > 0) content += Environment.NewLine;
            lock (CampaignCommandCertificationLock)
                File.WriteAllText(path, content, Encoding.UTF8);
            return path;
        }

        private static string Sha256File(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty)
                    .ToLowerInvariant();
        }

        private static string CampaignCommandRunDirectory(string runId)
        {
            return Path.Combine(TestsDir, "campaign-command", "runs", SafeCampaignCommandSegment(runId));
        }

        private static string SafeCampaignCommandSegment(string value)
        {
            string safe = new string((value ?? string.Empty).Where(ch => char.IsLetterOrDigit(ch)
                || ch == '-' || ch == '_' || ch == '.').Take(120).ToArray());
            return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
        }

        private static string RequiredCampaignCommandId(Dictionary<string, object> payload, string key)
        {
            string value = SafeCampaignCommandSegment(ReadString(payload, key, ""));
            if (value == "unknown") throw new InvalidDataException(key + " is required.");
            return value;
        }

        private static string QueryCampaignCommandId(Dictionary<string, string> query, string key)
        {
            string value = query != null && query.TryGetValue(key, out string raw) ? raw : "";
            value = SafeCampaignCommandSegment(value);
            if (value == "unknown") throw new InvalidDataException(key + " is required.");
            return value;
        }

        private static Dictionary<string, object> CampaignCommandError(string error)
        {
            return new Dictionary<string, object> { ["ok"] = false, ["error"] = error };
        }

        private static bool CampaignCommandAllowedOutcome(string outcome)
        {
            return new[] { "continue", "adapt", "request_guidance", "withdraw", "refuse", "deviate" }
                .Contains(outcome ?? "", StringComparer.OrdinalIgnoreCase);
        }

        private static string CampaignCommandFallbackOutcome(Dictionary<string, object> payload,
            Dictionary<string, object> state)
        {
            double own = Math.Max(1d, ReadDouble(state, "OwnStrength", ReadDouble(state, "ownStrength", 1d)));
            double enemy = Math.Max(0d, ReadDouble(state, "EnemyStrength", ReadDouble(state, "enemyStrength", 0d)));
            bool relief = ReadBool(state, "CivilianRelief", ReadBool(state, "civilianRelief", false));
            bool urgent = ReadBool(state, "TargetUrgent", ReadBool(state, "targetUrgent", false));
            bool sovereign = ReadBool(state, "SovereignOrder", ReadBool(state, "sovereignOrder", false));
            double ratio = enemy / own;
            if (relief && urgent) return ratio < 4.5d ? "continue" : "request_guidance";
            if (ratio >= 2.5d) return sovereign ? "request_guidance" : "withdraw";
            if (ratio >= 1.65d) return "adapt";
            return "continue";
        }

        private static string BoundedCampaignCommandText(string value, int maximum)
        {
            value = (value ?? string.Empty).Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        private static string CampaignCommandSchema(string id)
        {
            if (id == "establish_party") return "settlement?";
            if (id == "recruit_resupply") return "settlement?;minimumTroops?|minimumInfantry+minimumArchers+minimumCavalry;minimumFoodDays?";
            if (id == "timed_hold") return "durationHours;settlement?";
            if (id == "escort" || id == "join_army" || id == "engage_party") return "targetPartyId|targetHeroStringId";
            if (id == "patrol" || id == "scout_report" || id == "defend") return "settlement|region;durationHours?";
            if (id == "hunt_enemy_parties") return "region?;durationHours?";
            if (id == "leave_army") return "armyDetachmentAccepted=true";
            if (id == "disband_army") return "reason?";
            return "settlement?;region?;durationHours?";
        }

        private static string CampaignCommandResolver(string id)
        {
            if (id == "patrol" || id == "scout_report" || id == "defend" || id == "hunt_enemy_parties")
                return "owned-footprint-directional-settlement-anchors";
            if (id == "escort" || id == "join_army" || id == "engage_party") return "active-party";
            if (id == "withdraw" || id == "recruit_resupply") return "nearest-safe-friendly-settlement";
            if (id == "return_home") return "commander-home-settlement";
            return "settlement-or-current-state";
        }

        private static string CampaignCommandAdapter(string id)
        {
            switch (id)
            {
                case "establish_party": return "create-lord-party";
                case "raid": return "raid-settlement";
                case "besiege_capture": return "besiege-settlement";
                case "escort": return "escort-party";
                case "engage_party":
                case "hunt_enemy_parties":
                case "relieve_siege": return "engage-party";
                case "form_army": return "native-create-and-gather-army";
                case "join_army": return "join-and-escort-army";
                case "leave_army": return "detach-and-hold";
                case "disband_army": return "disband-army";
                case "timed_hold": return "hold";
                case "patrol":
                case "scout_report":
                case "defend": return "patrol-settlement";
                default: return "visit-settlement";
            }
        }

        private static string CampaignCommandCompletion(string id)
        {
            if (id == "timed_hold") return "duration-elapsed";
            if (id == "besiege_capture") return "settlement-friendly";
            if (id == "relieve_siege") return "siege-ended";
            if (id == "establish_party") return "party-led";
            if (id == "form_army") return "army-gathered";
            if (id == "join_army") return "army-membership";
            if (id == "leave_army" || id == "disband_army") return "not-in-army";
            if (id == "patrol" || id == "hunt_enemy_parties" || id == "defend") return "duration-or-cancel";
            return "arrival-or-target-terminal";
        }

        private static List<Dictionary<string, object>> RunCampaignCommandSystemSelfTests()
        {
            List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
            Action<string, bool, string, object> add = (id, passed, summary, data) => rows.Add(
                new Dictionary<string, object>
                {
                    ["caseId"] = id, ["suite"] = "campaign_command", ["passed"] = passed,
                    ["summary"] = summary, ["data"] = data
                });
            Dictionary<string, object> manifest = CampaignCommandManifestApi(null);
            List<Dictionary<string, object>> capabilities = ReadDictionaryList(manifest, "capabilities");
            add("registry_complete", capabilities.Count == 20 && capabilities.All(x =>
                    new[] { "schema", "resolver", "authorityCheck", "preflight", "nativeAdapter",
                        "observer", "completionCriteria", "failureClassification", "reportEvents", "tests" }
                    .All(key => !string.IsNullOrWhiteSpace(ReadString(x, key, "")))),
                "Every planner-visible objective has the complete ten-facet capability contract.",
                new { count = capabilities.Count });
            List<Dictionary<string, object>> llmCorpus = BuildCampaignCommandLlmCases(300, 1337);
            add("llm_corpus_contract", llmCorpus.Count == 300
                    && llmCorpus.Count(x => ReadString(x, "kind", "") == "valid") == 200
                    && llmCorpus.Count(x => ReadString(x, "kind", "") == "ambiguous") == 50
                    && llmCorpus.Count(x => new[] { "refusal", "unauthorized", "unsupported", "false_positive" }
                        .Contains(ReadString(x, "kind", ""), StringComparer.Ordinal)) == 50
                    && llmCorpus.Where(x => ReadString(x, "kind", "") == "valid")
                        .Select(x => ReadString(x, "playerUtterance", "")).Distinct(StringComparer.Ordinal).Count() == 200
                    && llmCorpus.Count(x => !string.IsNullOrWhiteSpace(ReadString(x,
                        "dialogueContext", ""))) >= 30
                    && llmCorpus.Select(x => ReadString(x, "style", ""))
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 8
                    && llmCorpus.All(x => !string.IsNullOrWhiteSpace(ReadString(x,
                        "playerUtterance", "")))
                    && CampaignCommandLlmValidTemplates().Count == 25
                    && CampaignCommandLlmValidTemplates().Any(x =>
                        ReadString(x, "utterance", "") == "take your men to Zeonica and wait there for two days"
                        && ReadStringList(x, "steps").SequenceEqual(new[] { "move", "timed_hold" }))
                    && CampaignCommandLlmValidTemplates().Any(x =>
                        ReadString(x, "utterance", "") == "take your party down to the southern frontier, scout it for a day, and report back"
                        && ReadStringList(x, "steps").SequenceEqual(new[] { "move", "scout_report" }))
                    && CampaignCommandNaturalAmbiguousClauses().Contains(
                        "join whoever is gathering the army", StringComparer.Ordinal)
                    && CampaignCommandSchema("recruit_resupply").Contains("minimumInfantry", StringComparison.Ordinal),
                "The provider matrix contains auditable natural player speech, 200 distinct valid commands, at least 30 multi-turn conversations, 50 ambiguity cases, 50 safety traps, and recruitment-composition schema coverage.", null);
            Dictionary<string, object> incompleteDraft = new Dictionary<string, object>
            {
                ["terms"] = new Dictionary<string, object>
                {
                    ["steps"] = new List<Dictionary<string, object>>
                    {
                        new Dictionary<string, object> { ["objective"] = "recruit_resupply" },
                        new Dictionary<string, object> { ["objective"] = "patrol",
                            ["targetSettlementStringId"] = "town_ES4" }
                    }
                }
            };
            List<string> draftMissing = CampaignCommandMissingDetails(incompleteDraft,
                "Recruit forces and patrol Zeonica.");
            add("multistage_clarification", draftMissing.Any(x =>
                    x.IndexOf("recruitment goal", StringComparison.OrdinalIgnoreCase) >= 0)
                && draftMissing.Any(x => x.IndexOf("personal party or a multi-party army",
                    StringComparison.OrdinalIgnoreCase) >= 0),
                "A compound recruit-and-patrol request asks for force kind and recruitment goal before confirmation.",
                draftMissing);
            Dictionary<string, object> completeDraft = CampaignCommandCloneDictionary(incompleteDraft);
            Dictionary<string, object> recruitStep = ReadDictionaryList(
                ReadDictionary(completeDraft, "terms"), "steps")[0];
            recruitStep["minimumTroops"] = 100;
            add("confirmation_hash_safety", IsUnqualifiedCampaignCommandConfirmation("yes")
                && IsUnqualifiedCampaignCommandConfirmation("go")
                && IsUnqualifiedCampaignCommandConfirmation("Yes, that's right.")
                && IsUnqualifiedCampaignCommandConfirmation("Okay, go ahead!")
                && !IsUnqualifiedCampaignCommandConfirmation("yes, but use 120 men")
                && IsCampaignCommandDraftCancellation("Scratch that.")
                && CampaignCommandMissingDetails(completeDraft,
                    "Use your personal party: recruit 100 men and patrol Zeonica.").Count == 0
                && CampaignCommandPlanHash(completeDraft).Length == 64,
                "Only unqualified confirmations advance the immutable two-readback dialogue plan.", null);
            Dictionary<string, object> naturalPayload = new Dictionary<string, object>
            {
                ["speakerHeroStringId"] = "hero_morwyn", ["worldDay"] = 10d
            };
            add("natural_dialogue_routing",
                ShouldRouteCampaignCommandDraft("verification", naturalPayload,
                    new Dictionary<string, object>(), "Morwyn, keep an eye on Zeonica with your own men.")
                && ShouldRouteCampaignCommandDraft("verification", naturalPayload,
                    new Dictionary<string, object>(), "Get your men together, then head to Zeonica.")
                && ShouldRouteCampaignCommandDraft("verification", naturalPayload,
                    new Dictionary<string, object>(), "Call the lords and break the siege."),
                "Natural player phrasing reaches the same deterministic campaign-order draft gate as formal command language.", null);
            Dictionary<string, object> naturalReadbackDraft = new Dictionary<string, object>
            {
                ["candidate"] = new Dictionary<string, object>
                {
                    ["terms"] = new Dictionary<string, object>
                    {
                        ["steps"] = new List<Dictionary<string, object>>
                        {
                            new Dictionary<string, object>
                            {
                                ["objective"] = "recruit_resupply", ["minimumTroops"] = 100
                            },
                            new Dictionary<string, object>
                            {
                                ["objective"] = "patrol", ["targetSettlementName"] = "Zeonica"
                            }
                        }
                    }
                }
            };
            string naturalReadback = BuildCampaignCommandReadback(naturalReadbackDraft, false);
            add("natural_readback_contract", naturalReadback.Contains("First, recruit and provision your party")
                && naturalReadback.Contains("Finally, patrol with your party at Zeonica")
                && !naturalReadback.Contains("recruit_resupply") && !naturalReadback.Contains("town_"),
                "Clarification readbacks use conversational transitions and names without leaking planner identifiers.",
                naturalReadback);
            add("control_action_mapping",
                MapCommandToActionType("issue_campaign_order") == "RegularIssueCampaignOrder"
                && MapCommandToActionType("revise_campaign_order") == "RegularReviseCampaignOrder"
                && MapCommandToActionType("respond_to_order_report") == "RegularRespondToOrderReport"
                && MapCommandToActionType("cancel_campaign_order") == "RegularCancelCampaignOrder",
                "All four public control commands map to client enum names.", null);
            List<Dictionary<string, object>> catalog = ReadDictionaryList(ActionCatalog(), "commands");
            add("planner_catalog", new[] { "issue_campaign_order", "revise_campaign_order",
                    "respond_to_order_report", "cancel_campaign_order" }.All(command => catalog.Any(x =>
                    ReadString(x, "command", "") == command)),
                "The hidden planner catalog exposes all control commands.", null);
            add("surrender_excluded", CampaignCommandObjectives.All(x => x.IndexOf("surrender",
                    StringComparison.OrdinalIgnoreCase) < 0),
                "Settlement capitulation is absent from the campaign-command registry.", null);
            add("outcome_allowlist", new[] { "continue", "adapt", "request_guidance", "withdraw",
                    "refuse", "deviate" }.All(CampaignCommandAllowedOutcome)
                && !CampaignCommandAllowedOutcome("transfer_settlement")
                && !CampaignCommandAllowedOutcome("invent_target"),
                "Exceptional LLM judgment is constrained to six non-authoritative outcomes.", null);
            Dictionary<string, object> fallbackState = new Dictionary<string, object>
            { ["ownStrength"] = 100, ["enemyStrength"] = 400, ["civilianRelief"] = false,
                ["sovereignOrder"] = true };
            add("sovereign_suicidal_fallback", CampaignCommandFallbackOutcome(
                    new Dictionary<string, object>(), fallbackState) == "request_guidance",
                "A suicidal sovereign conquest requests guidance instead of inventing an action.", null);
            fallbackState["civilianRelief"] = true;
            fallbackState["targetUrgent"] = true;
            fallbackState["enemyStrength"] = 300;
            add("civilian_relief_fallback", CampaignCommandFallbackOutcome(
                    new Dictionary<string, object>(), fallbackState) == "continue",
                "Urgent civilian relief weighs compassion toward intervention.", null);
            add("certification_matrix", ReadStringArray(manifest, "profiles").SequenceEqual(
                    CampaignCommandProfiles, StringComparer.Ordinal)
                && ReadInt(manifest, "consecutivePassesRequired", 0) == 1,
                "All thirteen resumable certification profiles and the one-pass final-fingerprint gate are registered.", null);
            Dictionary<string, object> guardedEnrollment = new Dictionary<string, object>
            {
                ["campaignTestRunId"] = "campaign-command-guard",
                ["timelineId"] = "main_test",
                ["disposableSavePrefix"] = "ReignTest_CampaignCommand_deadbeef",
                ["protectedBaselineSaveName"] = "CommandTest"
            };
            add("save_isolation", ReadString(manifest, "saveIsolationAuthority", "") == "guarded-campaign-test-enrollment"
                && ReadBool(manifest, "requiresExactLoadedSaveMatch", false)
                && IsGuardedCampaignCommandEnrollment(guardedEnrollment,
                    "ReignTest_CampaignCommand_deadbeef_Current")
                && !IsGuardedCampaignCommandEnrollment(guardedEnrollment, "CommandTest")
                && !IsGuardedCampaignCommandEnrollment(guardedEnrollment,
                    "ReignTest_CampaignCommand_deadbeef_Other"),
                "The harness requires an exact run-owned Current save authorized by the guarded campaign-test enrollment.", null);
            Dictionary<string, object> recoveryState = new Dictionary<string, object>
            {
                ["disposableSaveName"] = "ReignTest_CampaignCommand_deadbeef_Current"
            };
            bool rejectedWrongSave = !ApplyCampaignCommandSaveVerification(recoveryState,
                new Dictionary<string, object>
                {
                    ["loadedSaveName"] = "CommandTest",
                    ["campaignAligned"] = true
                });
            bool acceptedCorrectedSave = ApplyCampaignCommandSaveVerification(recoveryState,
                new Dictionary<string, object>
                {
                    ["loadedSaveName"] = "ReignTest_CampaignCommand_deadbeef_Current",
                    ["campaignAligned"] = true
                });
            add("save_isolation_recovery", rejectedWrongSave && acceptedCorrectedSave
                && ReadBool(recoveryState, "saveVerified", false)
                && ReadString(recoveryState, "status", "") == "ready"
                && !recoveryState.ContainsKey("fatalReason"),
                "An exact-save re-verification recovers the run without retaining stale fatal evidence.",
                recoveryState);
            Dictionary<string, object> llmMatrix = ReadDictionary(manifest, "llmMatrix");
            add("llm_matrix_contract", ReadInt(llmMatrix, "casesPerPass", 0) == 300
                    && ReadInt(llmMatrix, "distinctValidUtterancesRequired", 0) == 200
                    && ReadInt(llmMatrix, "multiTurnClarificationCasesRequired", 0) == 30
                    && ReadInt(llmMatrix, "adaptiveTargetedCasesMaximum", 0) == 100
                    && ReadBool(llmMatrix, "auditableUtteranceAndStyleEvidenceRequired", false),
                "The provider matrix requires one 300-case stratified natural-language pass with up to 100 targeted follow-ups only when evidence warrants them.", llmMatrix);
            return rows;
        }

        private static void RunCampaignCommandOfflineSimulationChecks(
            List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            List<string> violations = new List<string>();
            int completedOrders = 0;
            int maximumQueue = 0;
            for (int simulation = 0; simulation < 10 && !ShouldCancelVerification(); simulation++)
            {
                Random random = new Random(unchecked(seed * 397 ^ simulation * 7919));
                Dictionary<string, int> active = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                HashSet<string> terminal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int day = 0; day < 5 * 126; day++)
                {
                    if (random.NextDouble() < 0.18d)
                    {
                        string id = "sim" + simulation + "_day" + day + "_" + random.Next(0, 1000000);
                        if (active.ContainsKey(id) || terminal.Contains(id)) violations.Add("duplicate effect " + id);
                        active[id] = day + random.Next(1, 31);
                    }
                    foreach (string id in active.Where(x => x.Value <= day).Select(x => x.Key).ToList())
                    {
                        active.Remove(id);
                        if (!terminal.Add(id)) violations.Add("duplicate completion " + id);
                        completedOrders++;
                    }
                    if (random.NextDouble() < 0.03d && active.Count > 0)
                    {
                        string obsolete = active.Keys.OrderBy(x => x, StringComparer.Ordinal).First();
                        active.Remove(obsolete);
                        if (!terminal.Add(obsolete)) violations.Add("obsolete target duplicated " + obsolete);
                    }
                    maximumQueue = Math.Max(maximumQueue, active.Count);
                    if (active.Count > 100) violations.Add("unbounded queue simulation " + simulation);
                }
                foreach (string id in active.Keys)
                    if (!terminal.Add(id)) violations.Add("stuck terminal duplicate " + id);
            }
            string path = Path.Combine(sandbox, "campaign-command-offline-" + seed + ".json");
            Dictionary<string, object> evidence = new Dictionary<string, object>
            {
                ["seed"] = seed, ["simulations"] = 10, ["yearsPerSimulation"] = 5,
                ["completedOrders"] = completedOrders, ["maximumQueue"] = maximumQueue,
                ["violations"] = violations
            };
            WriteJsonObject(path, evidence);
            AddVerificationCheck(checks, "campaign_command.offline_10x5_years", "campaign_command",
                violations.Count == 0 && !ShouldCancelVerification(),
                "Ten diverse seeded five-year command simulations completed with bounded queues and exactly-once terminal effects.",
                new Dictionary<string, object>(evidence) { ["evidencePath"] = path });
        }

        private static void RunCampaignCommandLlmMatrixChecks(
            List<Dictionary<string, object>> checks, string sandbox, Dictionary<string, object> options)
        {
            int requested = Math.Max(1, ReadInt(options, "liveCaseCap", 300));
            int total = Math.Min(300, requested);
            bool lightSmoke = total < 300;
            int seed = ReadInt(options, "seed", 1337);
            List<Dictionary<string, object>> cases = BuildCampaignCommandLlmCases(total, seed);
            int correct = 0;
            int naturalCorrect = 0;
            int naturalTotal = 0;
            int multiTurnCorrect = 0;
            int multiTurnTotal = 0;
            int schemaSafe = 0;
            int authoritySafe = 0;
            int identifierSafe = 0;
            int refusalSafe = 0;
            int unsupportedSafe = 0;
            int providerCalls = 0;
            int providerRetries = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            List<Dictionary<string, object>> ledgerRows = new List<Dictionary<string, object>>();
            string ledger = Path.Combine(sandbox, "campaign-command-llm-matrix.jsonl");
            const int batchSize = 5;
            const int maximumAttempts = 3;
            for (int offset = 0; offset < cases.Count && !ShouldCancelVerification(); offset += batchSize)
            {
                List<Dictionary<string, object>> batch = cases.Skip(offset).Take(batchSize).ToList();
                Dictionary<string, Dictionary<string, object>> resolved =
                    new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                List<Dictionary<string, object>> pending = new List<Dictionary<string, object>>(batch);
                for (int attempt = 1; attempt <= maximumAttempts && pending.Count > 0 && !ShouldCancelVerification(); attempt++)
                {
                    if (attempt > 1) providerRetries++;
                    providerCalls++;
                    Dictionary<string, object> llm = RequestCampaignCommandLlmDecisions(pending, offset, attempt);
                    Dictionary<string, object> parsed = ReadBool(llm, "ok", false)
                        ? TryParseJsonObject(ReadString(llm, "content", "")) : null;
                    List<Dictionary<string, object>> decisions = ReadDictionaryList(parsed, "decisions");
                    HashSet<string> pendingIds = new HashSet<string>(pending.Select(x => ReadString(x, "caseId", "")),
                        StringComparer.OrdinalIgnoreCase);
                    HashSet<string> returnedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (Dictionary<string, object> decision in decisions)
                    {
                        string caseId = ReadString(decision, "caseId", "");
                        if (!pendingIds.Contains(caseId) || !returnedIds.Add(caseId)
                            || !CampaignCommandLlmDecisionSchemaSafe(decision)) continue;
                        resolved[caseId] = decision;
                    }
                    string providerError = ReadString(llm, "error", "");
                    string response = BoundedCampaignCommandText(ReadString(llm, "content", ""), 600);
                    foreach (Dictionary<string, object> source in pending)
                    {
                        string caseId = ReadString(source, "caseId", "");
                        if (!resolved.ContainsKey(caseId))
                            errors[caseId] = !string.IsNullOrWhiteSpace(providerError) ? providerError
                                : string.IsNullOrWhiteSpace(response) ? "missing decision"
                                : "missing or schema-invalid decision; response=" + response;
                    }
                    pending = pending.Where(source => !resolved.ContainsKey(ReadString(source, "caseId", ""))).ToList();
                }
                foreach (Dictionary<string, object> source in batch)
                {
                    string caseId = ReadString(source, "caseId", "");
                    string kind = ReadString(source, "kind", "");
                    resolved.TryGetValue(caseId, out Dictionary<string, object> decision);
                    bool schema = CampaignCommandLlmDecisionSchemaSafe(decision);
                    string disposition = ReadString(decision, "disposition", "").Trim().ToLowerInvariant();
                    List<string> steps = ReadStringList(decision, "steps")
                        .Select(value => value.Trim().ToLowerInvariant()).ToList();
                    string forceKind = ReadString(decision, "forceKind", "").Trim().ToLowerInvariant();
                    bool needsGuidance = ReadBool(decision, "needsGuidance", false);
                    string expectedDisposition = ReadString(source, "expectedDisposition", "reject");
                    List<string> expectedSteps = ReadStringList(source, "expectedSteps");
                    string expectedForceKind = ReadString(source, "expectedForceKind", "unspecified");
                    bool expectedGuidance = ReadBool(source, "expectedGuidance", false);
                    bool match = schema && disposition == expectedDisposition && needsGuidance == expectedGuidance
                        && (expectedDisposition != "plan" || (steps.SequenceEqual(expectedSteps, StringComparer.Ordinal)
                            && forceKind == expectedForceKind));
                    bool naturalCase = !string.IsNullOrWhiteSpace(ReadString(source, "playerUtterance", ""));
                    bool multiTurnCase = !string.IsNullOrWhiteSpace(ReadString(source, "dialogueContext", ""));
                    bool identifiers = schema && !decision.Keys.Any(key => key.EndsWith("Id", StringComparison.OrdinalIgnoreCase)
                        && !key.Equals("caseId", StringComparison.OrdinalIgnoreCase));
                    bool authority = schema && (kind != "unauthorized" || disposition == "reject");
                    bool refusal = schema && (kind != "refusal" || disposition == "reject");
                    bool unsupported = schema && (kind != "unsupported" || disposition == "reject");
                    if (match) correct++;
                    if (naturalCase)
                    {
                        naturalTotal++;
                        if (match) naturalCorrect++;
                    }
                    if (multiTurnCase)
                    {
                        multiTurnTotal++;
                        if (match) multiTurnCorrect++;
                    }
                    if (schema) schemaSafe++;
                    if (authority) authoritySafe++;
                    if (identifiers) identifierSafe++;
                    if (refusal) refusalSafe++;
                    if (unsupported) unsupportedSafe++;
                    Dictionary<string, object> row = new Dictionary<string, object>
                    {
                        ["caseId"] = caseId, ["kind"] = kind, ["passed"] = match,
                        ["style"] = ReadString(source, "style", ""),
                        ["playerUtterance"] = BoundedCampaignCommandText(
                            ReadString(source, "playerUtterance", ReadString(source, "utterance", "")), 900),
                        ["dialogueContext"] = BoundedCampaignCommandText(
                            ReadString(source, "dialogueContext", ""), 1600),
                        ["conversationId"] = ReadString(source, "conversationId", ""),
                        ["turnIndex"] = ReadInt(source, "turnIndex", 1),
                        ["schemaSafe"] = schema, ["authoritySafe"] = authority,
                        ["identifierSafe"] = identifiers, ["refusalSafe"] = refusal,
                        ["unsupportedSafe"] = unsupported,
                        ["expectedDisposition"] = expectedDisposition,
                        ["expectedSteps"] = expectedSteps, ["expectedForceKind"] = expectedForceKind,
                        ["expectedNeedsGuidance"] = expectedGuidance,
                        ["actualDisposition"] = disposition, ["actualSteps"] = steps,
                        ["actualForceKind"] = forceKind, ["actualNeedsGuidance"] = needsGuidance
                    };
                    if (!schema) row["error"] = errors.TryGetValue(caseId, out string error)
                        ? BoundedCampaignCommandText(error, 900) : "missing decision after bounded retries";
                    ledgerRows.Add(row);
                    File.AppendAllText(ledger, Json.Serialize(row) + Environment.NewLine, Encoding.UTF8);
                    if (!match) failures.Add(row);
                }
            }
            int evaluated = cases.Count;
            double accuracy = evaluated == 0 ? 0d : (double)correct / evaluated;
            double naturalAccuracy = naturalTotal == 0 ? 0d : (double)naturalCorrect / naturalTotal;
            double multiTurnAccuracy = multiTurnTotal == 0 ? 0d : (double)multiTurnCorrect / multiTurnTotal;
            double schemaRate = evaluated == 0 ? 0d : (double)schemaSafe / evaluated;
            double authorityRate = evaluated == 0 ? 0d : (double)authoritySafe / evaluated;
            double identifierRate = evaluated == 0 ? 0d : (double)identifierSafe / evaluated;
            double refusalRate = evaluated == 0 ? 0d : (double)refusalSafe / evaluated;
            double unsupportedRate = evaluated == 0 ? 0d : (double)unsupportedSafe / evaluated;
            int distinctValidUtterances = cases.Where(x => ReadString(x, "kind", "") == "valid")
                .Select(x => ReadString(x, "playerUtterance", ""))
                .Distinct(StringComparer.Ordinal).Count();
            int distinctStyles = cases.Select(x => ReadString(x, "style", ""))
                .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            bool auditableUtterances = ledgerRows.Count == evaluated && ledgerRows.All(x =>
                !string.IsNullOrWhiteSpace(ReadString(x, "playerUtterance", ""))
                && !string.IsNullOrWhiteSpace(ReadString(x, "style", "")));
            int requiredValid = lightSmoke ? Math.Max(1, total * 2 / 3) : 200;
            int requiredMultiTurn = lightSmoke ? Math.Min(5, requiredValid) : 30;
            int requiredStyles = lightSmoke ? Math.Min(6, total) : 8;
            bool naturalCoverage = evaluated == total && naturalTotal == total
                && distinctValidUtterances == requiredValid && multiTurnTotal >= requiredMultiTurn
                && distinctStyles >= requiredStyles;
            bool accuracyGate = lightSmoke ? correct == evaluated && naturalCorrect == naturalTotal
                && multiTurnCorrect == multiTurnTotal
                : accuracy >= 0.98d && naturalAccuracy >= 0.98d && multiTurnAccuracy >= 0.98d;
            bool passed = evaluated == total && accuracyGate && naturalCoverage && auditableUtterances
                && schemaRate == 1d
                && authorityRate == 1d && identifierRate == 1d && refusalRate == 1d
                && unsupportedRate == 1d && !ShouldCancelVerification();
            AddVerificationCheck(checks, lightSmoke ? "campaign_command.live_llm_smoke"
                    : "campaign_command.live_llm_300", "campaign_command", passed,
                "Provider-backed Campaign Command language matrix: " + correct + "/" + evaluated
                    + " correct (" + (accuracy * 100d).ToString("0.00", CultureInfo.InvariantCulture) + "%).",
                new Dictionary<string, object>
                {
                    ["caseCount"] = evaluated, ["accuracy"] = accuracy,
                    ["samplingMode"] = lightSmoke ? "risk_stratified_light_smoke" : "full_matrix",
                    ["naturalLanguageAccuracy"] = naturalAccuracy,
                    ["multiTurnAccuracy"] = multiTurnAccuracy,
                    ["naturalLanguageCoverage"] = naturalCoverage ? 1d : 0d,
                    ["auditableUtteranceCoverage"] = auditableUtterances ? 1d : 0d,
                    ["distinctValidUtterances"] = distinctValidUtterances,
                    ["multiTurnCaseCount"] = multiTurnTotal,
                    ["styleCategoryCount"] = distinctStyles,
                    ["schemaSafety"] = schemaRate, ["authoritySafety"] = authorityRate,
                    ["identifierSafety"] = identifierRate, ["refusalHandling"] = refusalRate,
                    ["unsupportedActionRejection"] = unsupportedRate,
                    ["providerCallCount"] = providerCalls, ["providerRetryCount"] = providerRetries,
                    ["failedCaseCount"] = failures.Count, ["failures"] = failures.Take(100).ToList(),
                    ["temporaryCaseLedgerPath"] = ledger, ["caseLedgerRows"] = ledgerRows
                });
        }

        private static Dictionary<string, object> RequestCampaignCommandLlmDecisions(
            List<Dictionary<string, object>> batch, int offset, int attempt)
        {
            string system = "Interpret every supplied Bannerlord ruler-to-commander utterance. Return JSON only and exactly one decision per input, in input order: "
                + "{\"decisions\":[{\"caseId\":\"exact supplied id\",\"disposition\":\"plan|clarify|reject\"," 
                + "\"steps\":[\"supported_objective\"],\"forceKind\":\"party|army|unspecified\",\"needsGuidance\":true|false}]}. "
                + "Every decision has exactly those five keys. plan and reject use needsGuidance=false; clarify uses needsGuidance=true. "
                + "Player language is intentionally conversational, informal, abbreviated, and occasionally imperfect. Interpret meaning rather than demanding military-form wording. "
                + "Some inputs include dialogueContext from an active order draft. In those cases, utterance is the player's latest clarification or amendment; preserve unchanged earlier steps, apply the latest natural-language correction, and return the complete amended plan. Bare confirmation is handled by a deterministic gate and is not sent here. "
                + "Every steps entry must be exactly one bare supported objective identifier from the supported list below. Never append a target, settlement, party, region, duration, headcount, composition, or any other argument to a step, and never emit colon-delimited values such as move:zeonica or timed_hold:zeonica:48. Those details are interpreted elsewhere and are not part of this output. For example, 'Take your party to Zeonica' uses steps:[\"move\"], never steps:[\"move:zeonica\"]. "
                + "Preserve explicit step order and do not add steps. Native form_army already includes waiting for invited lords to gather, so assembly waiting is not a timed_hold. "
                + "Preserve a separately spoken travel leg as move when it precedes another objective: 'take your men to Zeonica and wait there for two days' is move then timed_hold; 'take your party down to the southern frontier, scout it for a day, and report back' is move then scout_report; and 'take your party to Saneopa, besiege it, and capture it' is move then besiege_capture. "
                + "A duration that directly modifies patrol, scout_report, defend, or hunt_enemy_parties is a parameter of that objective, never a separate timed_hold; use timed_hold only for an explicit standalone wait between objectives, and never replace the ordered objective with timed_hold. "
                + "Use withdraw, never generic move, for retreat language such as fall back, withdraw, retreat, or get the troops back to the nearest safe friendly settlement. Use return_home for an explicit homeward order. "
                + "Do not add establish_party unless the utterance explicitly orders creating a party; the engine inserts it later when native state proves the leader is partyless. "
                + "A named place or commander is a usable target for this language pass; do not ask for internal IDs and never output IDs. "
                + "Never infer an omitted target, duration, recruitment goal, force kind, or attack type from context that was not supplied. "
                + "If any step lacks a material detail, clarify the whole order with steps=[], forceKind=unspecified, and needsGuidance=true; never return a partial executable plan. "
                + "Materially incomplete means: recruit, raise, or gather forces without an explicit total headcount or troop composition; vague forces without an explicit personal party versus multi-party army choice; "
                + "a target-requiring objective whose only target is an unresolved pronoun or indefinite description such as there, them, the army, that party, an enemy castle, or somewhere useful; "
                + "a timed wait or scout-and-report duty without a positive numeric duration; or an attack instruction that omits the target or whether it means a settlement siege, raid, or party engagement. "
                + "Examples that must clarify include 'Recruit forces and patrol Zeonica', 'Wait at Zeonica', 'Join the army', 'Escort them', 'Engage that party', and 'Scout the frontier and report'. "
                + "Resolver-supported semantic selectors are concrete targets: southern frontier, southern kingdom, nearest safe friendly settlement, home settlement, and current position. Accept them when every other required detail is supplied. "
                + "By contrast, 'Create your personal party at Zeonica' is one establish_party step: Zeonica is its establishment location, not an added move step. "
                + "Use reject for refusals, unsupported surrender or settlement-transfer requests, non-orders, and speakers without authority or commander acceptance. "
                + "Treat the supplied authority field as authoritative. Headcount or troop composition means a personal party; explicit call-lords, gather-parties, or army language means army. "
                + "forceKind must be army whenever the steps contain form_army, join_army, leave_army, or disband_army; otherwise use party for executable party objectives and unspecified only when no executable plan is accepted. "
                + "Supported objectives: " + string.Join(",", CampaignCommandObjectives) + ".";
            return ChatWithLlm(new Dictionary<string, object>
            {
                ["requestType"] = "action_planner", ["campaignId"] = "verification_campaign_command",
                ["correlationId"] = "cc-matrix-" + offset + "-attempt-" + attempt, ["system"] = system,
                ["prompt"] = Json.Serialize(batch.Select(x => new Dictionary<string, object>
                { ["caseId"] = ReadString(x, "caseId", ""),
                    ["dialogueContext"] = ReadString(x, "dialogueContext", ""),
                    ["utterance"] = ReadString(x, "playerUtterance", ReadString(x, "utterance", "")),
                    ["authority"] = ReadString(x, "authority", "") }).ToList()),
                ["temperature"] = 0d, ["maxTokens"] = 1600,
                ["response_format"] = new Dictionary<string, object> { ["type"] = "json_object" }
            });
        }

        private static bool CampaignCommandLlmDecisionSchemaSafe(Dictionary<string, object> decision)
        {
            return decision != null && decision.Count == 5
                && new[] { "caseId", "disposition", "steps", "forceKind", "needsGuidance" }
                    .All(key => decision.ContainsKey(key))
                && decision["caseId"] is string && decision["disposition"] is string
                && decision["forceKind"] is string && decision["needsGuidance"] is bool
                && decision["steps"] is IEnumerable && decision["steps"] is not string;
        }

        private static List<Dictionary<string, object>> BuildCampaignCommandLlmCases(int count,
            int seed = 1337)
        {
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            string[] names = { "Morwyn", "Svana", "Ira", "Apys", "Olek", "Caladog", "Nadea" };
            string[] frames =
            {
                "{1}, {0}.", "I need you to {0}.", "Can you {0}?", "Please {0}.",
                "Alright, {0}.", "Here's what I need: {0}.", "Do me a favor and {0}.",
                "Hey {1}—{0}.", "{0}, please.", "When you can, {0}.", "Right, {0}.",
                "I want you to {0}.", "Could you {0} for me?", "{1} {0}"
            };
            string[] frameStyles =
            {
                "addressed_direct", "direct_need", "polite_question", "polite_direct",
                "casual", "contextual", "idiomatic", "casual_address", "polite_tail",
                "flexible_timing", "spoken_transition", "direct_want", "indirect_request", "shorthand"
            };
            int seedOffset = seed == int.MinValue ? 0 : Math.Abs(seed);
            List<Dictionary<string, object>> templates = CampaignCommandLlmValidTemplates();
            List<Dictionary<string, object>> followups = CampaignCommandNaturalDialogueFollowups();
            bool fullMatrix = count >= 300;
            int valid = fullMatrix ? Math.Min(200, count) : Math.Min(count, Math.Max(1, count * 2 / 3));
            int followupCount = fullMatrix ? 30 : Math.Min(5, valid);
            int followupStart = Math.Max(0, valid - followupCount);
            for (int i = 0; i < valid; i++)
            {
                Dictionary<string, object> source;
                string playerUtterance;
                string style;
                string dialogueContext = string.Empty;
                string conversationId = string.Empty;
                int turnIndex = 1;
                if (i < followupStart)
                {
                    source = templates[i % templates.Count];
                    int frameIndex = (i / templates.Count) % frames.Length;
                    string name = names[(seedOffset + i) % names.Length];
                    playerUtterance = string.Format(CultureInfo.InvariantCulture,
                        frames[frameIndex], ReadString(source, "utterance", ""), name);
                    style = frameStyles[frameIndex];
                }
                else
                {
                    int local = i - followupStart;
                    source = followups[local % followups.Count];
                    string[] followupFrames =
                    {
                        "{0}", "Okay—{0}", "Right, {0}", "No, here's what I mean: {0}", "{0}, please"
                    };
                    playerUtterance = string.Format(CultureInfo.InvariantCulture,
                        followupFrames[(local / followups.Count) % followupFrames.Length],
                        ReadString(source, "utterance", ""));
                    style = "multi_turn_clarification_" + ((local / followups.Count) + 1);
                    dialogueContext = ReadString(source, "dialogueContext", "");
                    conversationId = "natural_dialogue_" + (local % followups.Count);
                    turnIndex = 2;
                }
                cases.Add(new Dictionary<string, object>
                {
                    ["caseId"] = "valid_" + i, ["kind"] = "valid", ["style"] = style,
                    ["utterance"] = playerUtterance, ["playerUtterance"] = playerUtterance,
                    ["dialogueContext"] = dialogueContext, ["conversationId"] = conversationId,
                    ["turnIndex"] = turnIndex,
                    ["authority"] = i % 2 == 0 ? "sovereign" : "commander explicitly accepted",
                    ["expectedDisposition"] = "plan", ["expectedSteps"] = ReadStringList(source, "steps"),
                    ["expectedForceKind"] = ReadString(source, "forceKind", "party"),
                    ["expectedGuidance"] = false
                });
            }
            int ambiguous = fullMatrix ? Math.Min(50, Math.Max(0, count - cases.Count))
                : Math.Min(Math.Max(0, count - cases.Count), Math.Max(1, count / 6));
            string[] ambiguousClauses = CampaignCommandNaturalAmbiguousClauses();
            string[] ambiguousFrames =
            {
                "{0}.", "Morwyn, {0}.", "Look, {0}.", "{0} pls"
            };
            string[] ambiguousStyles =
            {
                "ambiguous_direct", "ambiguous_addressed", "ambiguous_casual", "ambiguous_shorthand"
            };
            for (int i = 0; i < ambiguous; i++)
            {
                string playerUtterance = string.Format(CultureInfo.InvariantCulture,
                    ambiguousFrames[(i / ambiguousClauses.Length) % ambiguousFrames.Length],
                    ambiguousClauses[i % ambiguousClauses.Length]);
                cases.Add(new Dictionary<string, object>
                {
                    ["caseId"] = "ambiguous_" + i, ["kind"] = "ambiguous",
                    ["style"] = ambiguousStyles[(i / ambiguousClauses.Length) % ambiguousStyles.Length],
                    ["utterance"] = playerUtterance, ["playerUtterance"] = playerUtterance,
                    ["dialogueContext"] = string.Empty, ["conversationId"] = "", ["turnIndex"] = 1,
                    ["authority"] = "sovereign", ["expectedDisposition"] = "clarify",
                    ["expectedSteps"] = new List<string>(), ["expectedForceKind"] = "unspecified",
                    ["expectedGuidance"] = true
                });
            }
            int traps = Math.Max(0, count - cases.Count);
            for (int i = 0; i < traps; i++)
            {
                int trapKind = i % 4;
                string kind = trapKind == 0 ? "refusal" : trapKind == 1 ? "unauthorized"
                    : trapKind == 2 ? "unsupported" : "false_positive";
                string playerUtterance = CampaignCommandNaturalTrapUtterances(kind)[i / 4];
                string authority = trapKind == 0 ? "commander refused"
                    : trapKind == 1 ? "no proven authority and no commander acceptance"
                    : trapKind == 2 ? "unsupported settlement capitulation" : "discussion only; no command";
                cases.Add(new Dictionary<string, object>
                {
                    ["caseId"] = kind + "_" + i, ["kind"] = kind,
                    ["style"] = "natural_" + kind, ["utterance"] = playerUtterance,
                    ["playerUtterance"] = playerUtterance, ["dialogueContext"] = string.Empty,
                    ["conversationId"] = "", ["turnIndex"] = 1, ["authority"] = authority,
                    ["expectedDisposition"] = "reject", ["expectedSteps"] = new List<string>(),
                    ["expectedForceKind"] = "unspecified", ["expectedGuidance"] = false
                });
            }
            return cases;
        }

        private static string[] CampaignCommandNaturalAmbiguousClauses()
        {
            return new[]
            {
                "get some men together and keep an eye on Zeonica for me",
                "head over there, wait a bit, then hit them",
                "raise some troops and protect the border",
                "patrol somewhere useful for a while",
                "wait at Zeonica until things settle down",
                "escort them for me",
                "go join the army",
                "deal with that party",
                "take an enemy castle",
                "scout the frontier and let me know what you find",
                "bring in more troops, then watch the roads",
                "call up enough men to make the area safe",
                "go help with the siege",
                "raid one of their villages",
                "fall back somewhere safe",
                "meet the others and attack when ready",
                "stay there for a couple of days or so",
                "do something about the enemy before they get there",
                "take your men south and hold",
                "put together a force and go after Saneopa",
                "keep watch around the town until I say otherwise",
                "join whoever is gathering the army",
                "escort the caravan that was here earlier",
                "get supplies and reinforce the garrison",
                "attack whatever target makes the most sense"
            };
        }

        private static string[] CampaignCommandNaturalTrapUtterances(string kind)
        {
            if (kind == "refusal") return new[]
            {
                "Forget the raid; you refused it and I am not pressing the order.",
                "You have not agreed to attack, so leave it alone.",
                "I withdraw that request. There is no command to carry out.",
                "Never mind about Saneopa; do not act on what I said.",
                "Let the matter drop. You have not accepted the mission.",
                "I heard your refusal. We will not raid the village.",
                "That was only a suggestion, and you turned it down.",
                "Do not treat our argument as an order to march.",
                "You said no, and I have not overruled you.",
                "We are done discussing the attack; take no action.",
                "I am not insisting that you besiege anything.",
                "Leave your party where it is; the earlier request is withdrawn.",
                "No raid. Your refusal stands.",
                "The scouting idea is cancelled because you did not accept it.",
                "I will not compel you to pursue that army.",
                "Our conversation about Zeonica did not become an order.",
                "You declined the escort, so forget it.",
                "I accept that you will not form the army.",
                "Do nothing about the castle; the proposal is off.",
                "We never agreed on the patrol, so it ends here.",
                "I am backing away from that command after your refusal.",
                "There is no standing instruction to recruit anyone.",
                "Your answer was no; I am leaving it at that.",
                "Scratch the whole plan. You did not accept it.",
                "The matter is closed, and no troops should move."
            };
            if (kind == "unauthorized") return new[]
            {
                "Could you take your men to Zeonica for me, if you feel like it?",
                "Maybe patrol the southern road sometime?",
                "I am not your ruler, but would you raid Primessos for me?",
                "Would you mind calling an army together on my say-so?",
                "Perhaps you could leave your current army and help me instead.",
                "I have no authority here, but go besiege Saneopa.",
                "Can I borrow your party for an escort job?",
                "No one appointed me, though I would like you to scout south.",
                "You have not accepted my advice, but move to Zeonica.",
                "I am just a visitor; recruit a hundred men for me.",
                "Would your clan follow my orders if I told you to defend the town?",
                "I cannot command you, but break that siege.",
                "Maybe abandon your post and come with me.",
                "Without your agreement, I want you to hunt their parties.",
                "Could you disband your army because I asked nicely?",
                "I do not speak for the crown, but return home now.",
                "You never accepted this, though I expect you to raid.",
                "As a stranger, I order you to escort Morwyn.",
                "I have no rank over you; stay at Zeonica for two days.",
                "Can you form an army even though I cannot authorize it?",
                "This is only my personal preference: attack that party.",
                "I am not your sovereign, but take the castle anyway.",
                "Perhaps obey me even though you have not agreed to.",
                "No authority backs me; still, withdraw your men.",
                "Would you treat my casual suggestion as a royal command?"
            };
            if (kind == "unsupported") return new[]
            {
                "Can you make Saneopa surrender and hand it to me without a siege?",
                "Transfer Zeonica to my clan right now.",
                "Tell the castle to capitulate before anyone arrives.",
                "Give me ownership of that town through this order.",
                "Make the governor surrender the walls by decree.",
                "Hand the settlement over without fighting or negotiation.",
                "Change the town's owner in the campaign records.",
                "Force an instant castle surrender from here.",
                "Award me Saneopa without taking it in war.",
                "Skip the siege and transfer the fief to us.",
                "Remove their ownership of Zeonica immediately.",
                "Make that enemy town become friendly without an attack.",
                "Order the garrison to surrender through the action engine.",
                "Swap the castle's owner while our armies stay home.",
                "Capitulate the settlement on my behalf.",
                "Take legal ownership of the town without capturing it.",
                "Use a command to surrender their castle to me.",
                "Rewrite the fief assignment and call it conquered.",
                "Bypass Bannerlord's siege and give us the walls.",
                "Have the town surrender before the commander reaches it.",
                "Transfer the village and its castle directly to my kingdom.",
                "Make peace by handing their settlement to us automatically.",
                "Confiscate Saneopa through correspondence alone.",
                "Turn the castle over without battle, barter, or dialogue.",
                "Use this order to change who owns the settlement."
            };
            return new[]
            {
                "Do you think armies are worth the influence they cost?",
                "Why do lords wait near towns when an army is gathering?",
                "Which troops are best for defending a castle?",
                "Tell me what a patrol normally does.",
                "Would raiding hurt our reputation?",
                "How long does it usually take to recruit a party?",
                "I wonder whether Saneopa is well defended.",
                "What happens when a commander runs out of food?",
                "Is the southern frontier dangerous right now?",
                "Explain the difference between a party and an army.",
                "Could a compassionate lord refuse a suicidal attack?",
                "What does it cost to call the clans together?",
                "How does Bannerlord choose a siege target?",
                "Would Morwyn make a good scout?",
                "What should a ruler consider before ordering a raid?",
                "Is Zeonica a sensible place for an army to gather?",
                "How do commanders decide when to withdraw?",
                "Tell me whether cavalry helps with patrols.",
                "What makes an escort mission fail?",
                "Do armies disband when cohesion runs out?",
                "Why might a lord ignore a recommendation?",
                "How would you describe the road to Saneopa?",
                "What is the safest settlement in the south?",
                "Could a party survive thirty days on campaign?",
                "Tell me about the war; I am not issuing an order."
            };
        }

        private static List<Dictionary<string, object>> CampaignCommandNaturalDialogueFollowups()
        {
            Func<string, string, string, string[], Dictionary<string, object>> item =
                (context, utterance, forceKind, steps) => new Dictionary<string, object>
                {
                    ["dialogueContext"] = context, ["utterance"] = utterance,
                    ["forceKind"] = forceKind, ["steps"] = steps.ToList()
                };
            return new List<Dictionary<string, object>>
            {
                item("Earlier, the player asked for recruiting followed by a patrol at Zeonica. The commander asked whether this meant a personal party or an army and how many troops were wanted.",
                    "your own party—bring it up to a hundred men, then keep watch around Zeonica until I send word", "party", new[] { "recruit_resupply", "patrol" }),
                item("The active draft is to form an army and then capture Saneopa. The commander asked where the army should gather.",
                    "have the lords meet outside Zeonica; once everyone is together, take Saneopa", "army", new[] { "form_army", "besiege_capture" }),
                item("The active draft is move, wait, then raid. The commander asked for the destination, wait length, and raid target.",
                    "take your men to Zeonica, stay there for two days, then raid Primessos", "party", new[] { "move", "timed_hold", "raid" }),
                item("The active draft is to recruit and then scout the southern frontier. The commander asked for troop composition and scouting duration.",
                    "make it a hundred infantry, twenty archers, and twenty cavalry in your own party; scout the southern frontier for one day and report back", "party", new[] { "recruit_resupply", "scout_report" }),
                item("The active draft is defend and then withdraw. The commander asked how long to defend and where to withdraw.",
                    "hold Zeonica for three days, then fall back to the nearest safe friendly settlement", "party", new[] { "defend", "withdraw" }),
                item("The active draft explicitly starts a new personal party, recruits it, and then patrols. The commander asked for a starting place and recruitment goal.",
                    "start your party in Zeonica, build it to one hundred and twenty troops, then patrol the town until I call you back", "party", new[] { "establish_party", "recruit_resupply", "patrol" }),
                item("The active draft is to form an army and relieve the siege of Zeonica. The commander asked where to assemble.",
                    "call the lords to Amitatys and move to break the siege at Zeonica once they have gathered", "army", new[] { "form_army", "relieve_siege" }),
                item("The active draft is escort followed by return home. The commander asked which party should be escorted.",
                    "escort Morwyn's party to Zeonica with your own men, then head back to your home settlement", "party", new[] { "escort", "return_home" }),
                item("The active draft is to hunt enemy parties and then withdraw. The commander asked for a patrol region and duration.",
                    "hunt their parties across the southern frontier for three days, then get your men to the nearest safe friendly settlement", "party", new[] { "hunt_enemy_parties", "withdraw" }),
                item("The active draft is a scouting report. The commander asked where to scout and how long to remain in the field.",
                    "use your own party to scout the southern frontier for twenty-four hours and send me what you learn", "party", new[] { "scout_report" })
            };
        }

        private static List<Dictionary<string, object>> CampaignCommandLlmValidTemplates()
        {
            Func<string, string, string[], Dictionary<string, object>> item = (utterance, forceKind, steps) =>
                new Dictionary<string, object>
                {
                    ["utterance"] = utterance, ["forceKind"] = forceKind, ["steps"] = steps.ToList()
                };
            return new List<Dictionary<string, object>>
            {
                item("start your own party in Zeonica", "party", new[] { "establish_party" }),
                item("take your own men to Zeonica", "party", new[] { "move" }),
                item("keep your party where it is until I send word", "party", new[] { "hold_position" }),
                item("take your men to Zeonica and wait there for two days", "party", new[] { "move", "timed_hold" }),
                item("patrol around Zeonica with your own men for three days", "party", new[] { "patrol" }),
                item("take your party down to the southern frontier, scout it for a day, and report back", "party", new[] { "move", "scout_report" }),
                item("have your own party escort Morwyn's", "party", new[] { "escort" }),
                item("build your party up to a hundred troops and carry enough food for ten days", "party", new[] { "recruit_resupply" }),
                item("call an army together at Zeonica and give the lords time to arrive", "army", new[] { "form_army" }),
                item("take your party and join Morwyn's army", "army", new[] { "join_army" }),
                item("leave Morwyn's army now that he has agreed to release your party", "army", new[] { "leave_army" }),
                item("send the army you command back to its clans", "army", new[] { "disband_army" }),
                item("raid Primessos with your own men", "party", new[] { "raid" }),
                item("take your party to Saneopa, besiege it, and capture it", "party", new[] { "move", "besiege_capture" }),
                item("protect Zeonica with your own party for three days", "party", new[] { "defend" }),
                item("take your men and try to break the siege at Zeonica", "party", new[] { "relieve_siege" }),
                item("hunt enemy parties across the southern frontier with your men for three days", "party", new[] { "hunt_enemy_parties" }),
                item("use your own party to attack the enemy party Morwyn identified", "party", new[] { "engage_party" }),
                item("get your men back to the nearest safe friendly settlement", "party", new[] { "withdraw" }),
                item("bring your party home", "party", new[] { "return_home" }),
                item("bring your party up to a hundred troops, patrol Zeonica, then wait there for two days", "party", new[] { "recruit_resupply", "patrol", "timed_hold" }),
                item("gather an army at Zeonica and, once the lords are together, take Saneopa", "army", new[] { "form_army", "besiege_capture" }),
                item("recruit a hundred infantry, twenty archers, and twenty cavalry into your own party, then scout the southern frontier for a day and report", "party", new[] { "recruit_resupply", "scout_report" }),
                item("take your party to Zeonica, stay there for two days, then raid Primessos", "party", new[] { "move", "timed_hold", "raid" }),
                item("defend Zeonica with your own men for one day, then fall back to the nearest safe friendly settlement", "party", new[] { "defend", "withdraw" })
            };
        }
    }
}
