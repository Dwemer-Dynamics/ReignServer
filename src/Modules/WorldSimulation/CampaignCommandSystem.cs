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
    internal static partial class WorldSimulationService
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

        internal static void CopyCampaignCommandTerms(Dictionary<string, object> raw,
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

        internal static Dictionary<string, object> CampaignCommandManifestApi(
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

        internal static Dictionary<string, object> CampaignCommandPrepareApi(
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

        internal static Dictionary<string, object> CampaignCommandSaveVerifyApi(
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

        internal static Dictionary<string, object> CampaignCommandProfileResultApi(
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
            Program.WriteJsonObject(replayPath, payload);
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

        internal static Dictionary<string, object> CampaignCommandStatusApi(
            Dictionary<string, string> query, bool includeReport)
        {
            string runId = QueryCampaignCommandId(query, "runId");
            Dictionary<string, object> state = ReadCampaignCommandState(runId);
            if (state.Count == 0) return CampaignCommandError("Unknown campaign-command run.");
            return CampaignCommandStatusResult(state, includeReport);
        }

        internal static Dictionary<string, object> CampaignCommandReplayApi(
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

        internal static Dictionary<string, object> CampaignCommandResumeApi(
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

        internal static Dictionary<string, object> CampaignCommandCancelApi(
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

        internal static Dictionary<string, object> CampaignCommandExecutionApi(
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

        internal static Dictionary<string, object> CampaignCommandReadinessApi(
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
            Program.WriteJsonObject(summaryPath, readiness);
            readiness["summaryPath"] = summaryPath;
            return readiness;
        }

        internal static Dictionary<string, object> CampaignCommandReportApi(
            Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string campaignId = ReadString(payload, "campaignId", "default");
            Dictionary<string, object> order = ReadDictionary(payload, "order")
                ?? new Dictionary<string, object>();
            string orderId = ReadString(order, "OrderId", ReadString(order, "orderId", "unknown"));
            string directory = Path.Combine(Program.TestsDir, "campaign-command", "runtime",
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
                Program.WriteJsonObject(Path.Combine(directory, SafeCampaignCommandSegment(orderId) + ".json"), evidence);
            }
            return new Dictionary<string, object> { ["ok"] = true, ["orderId"] = orderId };
        }

        internal static Dictionary<string, object> CampaignCommandJudgeApi(
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
            Dictionary<string, object> characterState = Program.ReadJsonObject(
                Program.CharacterFile(ReadString(payload, "campaignId", "default"), heroId, "state.json"));
            string mbti = ReadFirstString(characterState, "mbti", "mbtiType", "personalityType");
            string system = "You are an advisory military judgment layer for Bannerlord Reign. "
                + "Native state, authority, and identifiers are authoritative. Return only compact JSON with outcome and reason. "
                + "outcome must be exactly continue, adapt, request_guidance, withdraw, refuse, or deviate. "
                + "A sovereign order has heavy weight. Compassion may oppose suicidal conquest while strengthening desperate civilian relief. "
                + "Do not add identifiers, actions, transfers, surrender, or settlement capitulation.";
            string prompt = "Commander MBTI: " + FirstNonEmpty(mbti, "unknown") + "\n"
                + "Validated order and native state: " + Json.Serialize(payload) + "\n"
                + "Deterministic safety recommendation: " + deterministic;
            Dictionary<string, object> llm = Program.ChatWithLlm(new Dictionary<string, object>
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
            return Program.ReadJsonObject(Path.Combine(CampaignCommandRunDirectory(runId), "state.json"));
        }

        private static void WriteCampaignCommandState(string runId, Dictionary<string, object> state)
        {
            string directory = CampaignCommandRunDirectory(runId);
            Directory.CreateDirectory(directory);
            lock (CampaignCommandCertificationLock)
                Program.WriteJsonObject(Path.Combine(directory, "state.json"), state);
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
            return Path.Combine(Program.TestsDir, "campaign-command", "runs", SafeCampaignCommandSegment(runId));
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
    }
}
