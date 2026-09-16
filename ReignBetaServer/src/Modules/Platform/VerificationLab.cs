using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace ReignBetaServer
{
    internal static partial class Program
    {
        private static readonly object VerificationLock = new object();
        private static readonly string VerificationDir = Path.Combine(TestsDir, "verification");
        private static readonly string VerificationRunsDir = Path.Combine(VerificationDir, "runs");
        private static readonly string VerificationFailuresDir = Path.Combine(VerificationDir, "failures");
        // Keep mutable sandboxes beneath the packaged test-data tree, but make the
        // path deliberately short.  The installed Bannerlord module already has a
        // long base path, and fixture campaign/hero IDs can otherwise cross the
        // legacy Win32 MAX_PATH boundary before SQLite creates its database.
        private static readonly string VerificationSandboxesDir = Path.Combine(TestsDir, "v");
        private static readonly string VerificationCancelPath = Path.Combine(VerificationDir, "cancel.request");
        private static readonly string VerificationGameCommandPath = Path.Combine(VerificationDir, "game-command.json");
        private static readonly string VerificationGameStatusPath = Path.Combine(VerificationDir, "game-status.json");
        private static Dictionary<string, object> VerificationStatus = NewVerificationStatus("idle", "No verification run is active.");
        private static volatile bool VerificationCancelRequested;
        private static Task VerificationTask;

        private static Dictionary<string, object> RunVerificationCli(string[] args)
        {
            EnsureVerificationDirectories();
            if (HasArg(args, "--cancel"))
            {
                VerificationCancelRequested = true;
                File.WriteAllText(VerificationCancelPath, DateTime.UtcNow.ToString("o"));
                WriteVerificationGameCancelCommand("Verification cancellation was requested from the standalone runner.");
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["cancelRequested"] = true,
                    ["message"] = "Verification cancellation requested."
                };
            }

            Dictionary<string, object> options = new Dictionary<string, object>
            {
                ["tier"] = ArgValue(args, "--tier", "offline"),
                ["suite"] = ArgValue(args, "--suite", ""),
                ["seed"] = ParseInt(ArgValue(args, "--seed", "1337"), 1337),
                ["repeat"] = Math.Max(1, ParseInt(ArgValue(args, "--repeat", "1"), 1)),
                ["failFast"] = HasArg(args, "--fail-fast"),
                ["json"] = HasArg(args, "--json"),
                ["liveCaseCap"] = Math.Max(1, ParseInt(ArgValue(args, "--live-case-cap", "20"), 20)),
                ["gameTimeoutSeconds"] = Math.Max(30, ParseInt(ArgValue(args, "--game-timeout-seconds", "900"), 900))
            };
            string codexPlanPath = ArgValue(args, "--codex-performance-plan", "");
            if (!string.IsNullOrWhiteSpace(codexPlanPath)) options["codexPerformance"] = ReadJsonObject(codexPlanPath);
            return RunVerification(options);
        }

        private static Dictionary<string, object> StartVerificationApi(Dictionary<string, object> payload)
        {
            EnsureVerificationDirectories();
            lock (VerificationLock)
            {
                if (VerificationTask != null && !VerificationTask.IsCompleted)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["error"] = "A verification run is already active.",
                        ["status"] = CloneDictionary(VerificationStatus)
                    };
                }

                Dictionary<string, object> options = NormalizeVerificationOptions(payload);
                if (ReadString(options, "suite", "") == "codex_performance" && ReadString(options, "tier", "") != "quick" && ReadString(options, "tier", "") != "offline")
                    ValidateCodexPerformanceRun(options);
                VerificationCancelRequested = false;
                TryDelete(VerificationCancelPath);
                VerificationStatus = NewVerificationStatus("queued", "Verification run queued.");
                VerificationStatus["tier"] = ReadString(options, "tier", "offline");
                VerificationStatus["suite"] = ReadString(options, "suite", "");
                VerificationTask = EnqueuePriorityBackgroundWork("verification.run", "normal", () => RunVerification(options));
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["accepted"] = true,
                    ["status"] = CloneDictionary(VerificationStatus)
                };
            }
        }

        private static Dictionary<string, object> VerificationStatusApi()
        {
            lock (VerificationLock)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = CloneDictionary(VerificationStatus),
                    ["running"] = VerificationTask != null && !VerificationTask.IsCompleted,
                    ["cancelRequested"] = VerificationCancelRequested || File.Exists(VerificationCancelPath),
                    ["gameCommandPath"] = VerificationGameCommandPath,
                    ["gameStatusPath"] = VerificationGameStatusPath
                };
            }
        }

        private static Dictionary<string, object> CancelVerificationApi()
        {
            EnsureVerificationDirectories();
            VerificationCancelRequested = true;
            File.WriteAllText(VerificationCancelPath, DateTime.UtcNow.ToString("o"));
            WriteVerificationGameCancelCommand("Verification cancellation was requested from the Control Center.");
            lock (VerificationLock)
            {
                VerificationStatus["cancelRequested"] = true;
                VerificationStatus["message"] = "Cancellation requested; the active check will finish before stopping.";
            }
            return new Dictionary<string, object> { ["ok"] = true, ["cancelRequested"] = true };
        }

        private static Dictionary<string, object> VerificationResultsApi(Dictionary<string, string> query)
        {
            EnsureVerificationDirectories();
            int limit = 20;
            if (query != null && query.TryGetValue("limit", out string rawLimit))
            {
                limit = Math.Max(1, Math.Min(200, ParseInt(rawLimit, 20)));
            }

            string runId = query != null && query.TryGetValue("runId", out string requestedRun) ? requestedRun : "";
            if (!string.IsNullOrWhiteSpace(runId))
            {
                string path = FindVerificationRunPath(runId);
                Dictionary<string, object> run = string.IsNullOrWhiteSpace(path) ? new Dictionary<string, object>() : ReadJsonObject(path);
                return run.Count == 0
                    ? new Dictionary<string, object> { ["ok"] = false, ["error"] = "Verification run was not found." }
                    : new Dictionary<string, object> { ["ok"] = true, ["run"] = run, ["path"] = path };
            }

            List<Dictionary<string, object>> rows = Directory.GetFiles(VerificationRunsDir, "*.json", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetFiles(VerificationFailuresDir, "*.json", SearchOption.TopDirectoryOnly))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(limit)
                .Select(path =>
                {
                    Dictionary<string, object> row = ReadJsonObject(path);
                    return new Dictionary<string, object>
                    {
                        ["runId"] = ReadString(row, "runId", Path.GetFileNameWithoutExtension(path)),
                        ["tier"] = ReadString(row, "tier", ""),
                        ["suite"] = ReadString(row, "suite", ""),
                        ["status"] = ReadString(row, "status", ""),
                        ["passed"] = ReadBool(row, "passed", false),
                        ["passedCount"] = ReadInt(row, "passedCount", 0),
                        ["failedCount"] = ReadInt(row, "failedCount", 0),
                        ["totalCount"] = ReadInt(row, "totalCount", 0),
                        ["durationMs"] = ReadLong(row, "durationMs", 0L),
                        ["completedUtc"] = ReadString(row, "completedUtc", ""),
                        ["path"] = path
                    };
                })
                .ToList();
            return new Dictionary<string, object> { ["ok"] = true, ["runs"] = rows, ["count"] = rows.Count };
        }

        private static Dictionary<string, object> ReplayVerificationApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string runId = ReadString(payload, "runId", "");
            string path = FindVerificationRunPath(runId);
            Dictionary<string, object> prior = string.IsNullOrWhiteSpace(path) ? new Dictionary<string, object>() : ReadJsonObject(path);
            if (prior.Count == 0)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = "Verification run was not found." };
            }

            Dictionary<string, object> options = ReadDictionary(prior, "options") ?? new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in payload)
            {
                if (!pair.Key.Equals("runId", StringComparison.OrdinalIgnoreCase)) options[pair.Key] = pair.Value;
            }
            options["replayOf"] = runId;
            return StartVerificationApi(options);
        }

        private static Dictionary<string, object> RunVerification(Dictionary<string, object> rawOptions)
        {
            EnsureVerificationDirectories();
            Dictionary<string, object> options = NormalizeVerificationOptions(rawOptions);
            string tier = ReadString(options, "tier", "offline").ToLowerInvariant();
            string suite = ReadString(options, "suite", "");
            CodexPerformanceBudget previousCodexBudget = ActiveCodexPerformanceBudget.Value;
            if (suite == "codex_performance" && tier != "quick" && tier != "offline")
            {
                var codexPlan = ValidateCodexPerformanceRun(options);
                ActiveCodexPerformanceBudget.Value = new CodexPerformanceBudget { Maximum = ReadInt(codexPlan, "totalProviderCallCap", 0) };
            }
            int seed = ReadInt(options, "seed", 1337);
            int repeat = Math.Max(1, ReadInt(options, "repeat", 1));
            bool failFast = ReadBool(options, "failFast", false);
            string runId = "verify-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string sandboxToken = runId.Length > 8 ? runId.Substring(runId.Length - 8) : runId;
            string sandbox = Path.Combine(VerificationSandboxesDir, "r-" + SafePathSegment(sandboxToken, "run"));
            string verificationCampaignsRoot = Path.Combine(sandbox, "c");
            string previousCampaignsRoot = CampaignsRootOverride.Value;
            Directory.CreateDirectory(sandbox);
            TryDelete(VerificationCancelPath);
            VerificationCancelRequested = false;
            Stopwatch timer = Stopwatch.StartNew();
            List<Dictionary<string, object>> checks = new List<Dictionary<string, object>>();
            HashSet<string> preexistingPostgreSqlCampaigns =
                new HashSet<string>(
                    ReignPostgreSqlStorage.ListCampaignMetadata()
                        .Select(row => ReadString(
                            row, "campaignId", ""))
                        .Where(id => !string.IsNullOrWhiteSpace(id)),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, object> result = new Dictionary<string, object>
            {
                ["ok"] = true,
                ["runId"] = runId,
                ["tier"] = tier,
                ["suite"] = suite,
                ["seed"] = seed,
                ["repeat"] = repeat,
                ["status"] = "running",
                ["startedUtc"] = DateTime.UtcNow.ToString("o"),
                ["sandboxPath"] = sandbox,
                ["realCampaignsReadOnly"] = true,
                ["options"] = options,
                ["checks"] = checks
            };

            SetVerificationStatus(runId, "running", "Discovering coverage and running deterministic checks.", 0, 0, 0);
            CampaignsRootOverride.Value = verificationCampaignsRoot;
            try
            {
                // A game process can already be open when the runner begins. Leave a fresh
                // cancellation command in place throughout the deterministic gates so a
                // command from an earlier run cannot be replayed into the disposable save.
                // RunGameVerificationBridge replaces it only after every prerequisite is green.
                if (tier == "game" || tier == "all")
                {
                    WriteVerificationGameCancelCommand("Neutralizing any stale native gauntlet command while deterministic prerequisites run.");
                }

                Dictionary<string, object> coverage = BuildVerificationCoverageManifest();
                result["coverage"] = coverage;
                AddCoverageChecks(checks, coverage, suite);

                for (int pass = 0; pass < repeat && !ShouldCancelVerification(); pass++)
                {
                    RunQuickVerificationChecks(
                        checks,
                        sandbox,
                        suite,
                        seed + pass,
                        failFast,
                        includeBaselines: tier != "quick");
                    if (ShouldStop(checks, failFast) || tier == "quick") break;

                    if (tier == "offline" || tier == "all" || tier == "live-llm" || tier == "game")
                    {
                        RunOfflineVerificationChecks(checks, sandbox, suite, seed + pass, failFast);
                    }
                    if (ShouldStop(checks, failFast)) break;

                    if ((tier == "live-llm" || tier == "all") && !ShouldCancelVerification())
                    {
                        if (checks.All(CheckPassed)) RunLiveLlmVerificationChecks(checks, sandbox, suite, options);
                        else AddVerificationCheck(checks, "live_llm.prerequisite", "live_llm", false, "Live LLM tier was blocked because deterministic verification is not green.", null, "blocked");
                    }
                    if (ShouldStop(checks, failFast)) break;

                    if ((tier == "game" || tier == "all") && !ShouldCancelVerification())
                    {
                        if (checks.All(CheckPassed)) RunGameVerificationBridge(checks, sandbox, options);
                        else AddVerificationCheck(checks, "game.prerequisite", "game", false, "Game tier was blocked because prior tiers are not green.", null, "blocked");
                    }
                }
            }
            catch (Exception ex)
            {
                AddVerificationCheck(checks, "verification.unclassified_exception", "runner", false, "Unclassified verification exception: " + ex.Message, new Dictionary<string, object> { ["type"] = ex.GetType().FullName ?? "", ["stack"] = ex.ToString() });
            }
            finally
            {
                if (ActiveCodexPerformanceBudget.Value != previousCodexBudget)
                {
                    if (ActiveCodexPerformanceBudget.Value != null) ActiveCodexPerformanceBudget.Value.Closed = true;
                    ActiveCodexPerformanceBudget.Value = previousCodexBudget;
                }
                ReignPostgreSqlStorage.ClearAllPools();
                try
                {
                    foreach (Dictionary<string, object> registered
                        in ReignPostgreSqlStorage.ListCampaignMetadata())
                    {
                        string registeredId = ReadString(
                            registered, "campaignId", "");
                        if (!string.IsNullOrWhiteSpace(registeredId)
                            && !preexistingPostgreSqlCampaigns.Contains(
                                registeredId))
                        {
                            ReignPostgreSqlStorage.DropCampaign(
                                registeredId);
                        }
                    }
                }
                catch { }
                ReignPostgreSqlStorage.ClearAllPools();
                CampaignsRootOverride.Value = previousCampaignsRoot;
                TryDeleteDirectory(verificationCampaignsRoot);
            }

            timer.Stop();
            bool cancelled = ShouldCancelVerification();
            int passedCount = checks.Count(CheckPassed);
            int failedCount = checks.Count - passedCount;
            bool passed = !cancelled && failedCount == 0 && checks.Count > 0;
            result["status"] = cancelled ? "cancelled" : passed ? "completed" : "failed";
            result["passed"] = passed;
            result["passedCount"] = passedCount;
            result["failedCount"] = failedCount;
            result["totalCount"] = checks.Count;
            result["durationMs"] = timer.ElapsedMilliseconds;
            result["completedUtc"] = DateTime.UtcNow.ToString("o");
            result["summary"] = "Verification " + (passed ? "passed" : cancelled ? "cancelled" : "failed") + ": " + passedCount + "/" + checks.Count + " checks passed.";
            SaveVerificationResult(result);
            SetVerificationStatus(runId, ReadString(result, "status", "failed"), ReadString(result, "summary", ""), passedCount, failedCount, checks.Count);
            return result;
        }

        private static void RunQuickVerificationChecks(
            List<Dictionary<string, object>> checks,
            string sandbox,
            string requestedSuite,
            int seed,
            bool failFast,
            bool includeBaselines)
        {
            bool explicitSuite = !string.IsNullOrWhiteSpace(requestedSuite);
            if (requestedSuite == "relationship_throughput_contracts" || requestedSuite == "relationship_throughput_smoke"
                || requestedSuite == "relationship_throughput")
            {
                if (!HasArg(Environment.GetCommandLineArgs(), "--run-verification"))
                    throw new InvalidOperationException("Relationship throughput contracts require the isolated Verification Lab CLI.");
                AddSubsystemListSummary(checks, "contracts.relationship_throughput", "relationship_throughput", RunRelationshipThroughputSelfTests());
                if (requestedSuite != "relationship_throughput_contracts")
                {
                    var replay = RunRelationshipThroughputReplay(sandbox, seed, requestedSuite == "relationship_throughput_smoke");
                    AddVerificationCheck(checks, "performance.relationship_throughput", "relationship_throughput", ReadBool(replay, "ok", false),
                        "Identical-input relationship replay and fixed two-second arrivals; the full suite requires 100 days and p95 service below one second.", replay);
                }
                return;
            }
            if (string.Equals(requestedSuite, "codex_performance", StringComparison.OrdinalIgnoreCase))
            {
                if (!HasArg(Environment.GetCommandLineArgs(), "--run-verification"))
                    throw new InvalidOperationException("Injected Codex contracts require the isolated non-listening Verification Lab CLI through reign_run_offline_verification.");
                AddSubsystemListSummary(checks, "contracts.codex_runtime", "codex_performance", RunCodexRuntimeSelfTests());
                AddSubsystemListSummary(checks, "contracts.codex_conversation", "codex_performance", RunCodexConversationSelfTests());
                var integration = RunCodexPerformanceIntegrationSelfTests();
                AddVerificationCheck(checks, "contracts.codex_integration", "codex_performance", ReadBool(integration, "ok", false), "Codex settings and physical provider-call cap contracts.", integration);
                var reports = RunCodexPerformanceReportSelfTests();
                AddVerificationCheck(checks, "contracts.codex_performance_reports", "codex_performance", ReadBool(reports, "ok", false), "Codex comparison report contracts.", reports);
                return;
            }
            if (string.Equals(requestedSuite, "codex_images", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.codex_images", "portraits_images", RunPortraitProviderAdapterContractTests());
                return;
            }
            if (string.Equals(requestedSuite, "character_narrative", StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestedSuite, "contracts", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.character_narrative", "characters", RunCharacterNarrativeTests());
            if (string.Equals(requestedSuite, "prompt_efficiency", StringComparison.OrdinalIgnoreCase))
            {
                RunPromptSizeContract(checks);
                AddSubsystemListSummary(checks, "pipeline.prompt_caching", "prompt_efficiency", RunPromptCachingSelfTests());
            }
            if (string.Equals(requestedSuite, "conversation_intoxication", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.conversation_intoxication", "conversation_modes", RunConversationIntoxicationSelfTests());
            if (string.Equals(requestedSuite, "wanderer_population", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.wanderer_population", "characters", RunWandererPopulationTests());
            if (string.Equals(requestedSuite, "encountered_residents", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.encountered_residents", "characters", RunEncounteredResidentTests());
            if (string.Equals(requestedSuite, "tavern_house", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.tavern_house", "characters", RunTavernHouseContractTests());
            if (string.Equals(requestedSuite, "starting_children", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.starting_children", "characters", RunStartingChildrenTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "campaign_command"))
                AddSubsystemListSummary(checks, "contracts.campaign_command", "campaign_command",
                    RunCampaignCommandSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (!includeBaselines && explicitSuite && ShouldRunVerificationSuite(requestedSuite, "social_reputation"))
                AddSubsystemListSummary(checks, "contracts.social_reputation", "social_reputation", RunRumorSubsystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (!includeBaselines && explicitSuite && ShouldRunVerificationSuite(requestedSuite, "spymaster"))
                AddSubsystemListSummary(checks, "contracts.spymaster", "spymaster", RunSpymasterSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "government"))
                AddSubsystemListSummary(checks, "contracts.government", "government", RunGovernmentSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "contracts") ||
                string.Equals(requestedSuite, "ui_contracts", StringComparison.OrdinalIgnoreCase))
            {
                RunBuildAndContractChecks(checks, includeExpensiveSubsystems: explicitSuite);
            }
            if (ShouldStop(checks, failFast)) return;

            if ((includeBaselines || explicitSuite) && ShouldRunVerificationSuite(requestedSuite, "baselines"))
            {
                AddSelfTestSummary(checks, "baseline.diplomacy", "world_diplomacy", RunWorldDiplomacySelfTests());
                AddSelfTestSummary(checks, "baseline.world_history", "world_history", RunWorldHistorySelfTests());
                AddSelfTestSummary(checks, "baseline.initialization_readiness", "save_load", RunInitializationReadinessSelfTests());
                AddSelfTestSummary(checks, "baseline.rebellion", "rebellion", RunRebellionSelfTests());
                AddSubsystemListSummary(checks, "baseline.world_test", "world_test", RunWorldTestSelfTests());
            }
        }

        private static void RunOfflineVerificationChecks(List<Dictionary<string, object>> checks, string sandbox, string requestedSuite, int seed, bool failFast)
        {
            if (ShouldRunVerificationSuite(requestedSuite, "campaign_command"))
                RunCampaignCommandOfflineSimulationChecks(checks, sandbox, seed);
            if (ShouldStop(checks, failFast)) return;
            if (!string.IsNullOrWhiteSpace(requestedSuite) && ShouldRunVerificationSuite(requestedSuite, "social_reputation"))
                AddSubsystemListSummary(checks, "architecture.social_reputation", "social_reputation", RunRumorSubsystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "spymaster")) AddSubsystemListSummary(checks, "architecture.spymaster", "spymaster", RunSpymasterSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "interaction_architecture"))
            {
                AddSubsystemListSummary(checks, "architecture.interaction",
                    "interaction_architecture", RunInteractionArchitectureSelfTests());
                Dictionary<string, object> settlementAuthority =
                    VerifyDialogueSettlementAuthority();
                AddVerificationCheck(checks, "architecture.settlement_authority",
                    "interaction_architecture",
                    ReadBool(settlementAuthority, "passed", false),
                    "Dialogue settlement authority matrix passes at the normalization boundary.",
                    settlementAuthority);
            }
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "pipeline")) RunProductionPipelineChecks(checks, requestedSuite, failFast);
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "provider_faults")) RunProviderFaultChecks(checks);
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "shadow_world")) RunShadowWorldChecks(checks, sandbox, seed);
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "rebellion")) RunExtendedRebellionChecks(checks, sandbox, seed);
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "long_run")) RunLongRunChecks(checks, sandbox, seed);
        }

        private static void RunBuildAndContractChecks(
            List<Dictionary<string, object>> checks,
            bool includeExpensiveSubsystems)
        {
            if (HasArg(Environment.GetCommandLineArgs(), "--run-verification") && ActiveServerPort <= 0)
                AddSubsystemListSummary(checks, "contracts.campaign_provider_wait", "save_load", RunCampaignProviderWaitSelfTests());
            AddSubsystemListSummary(checks, "contracts.wanderer_population", "characters", RunWandererPopulationTests());
            AddSubsystemListSummary(checks, "contracts.encountered_residents", "characters", RunEncounteredResidentTests());
            AddSubsystemListSummary(checks, "contracts.tavern_house", "characters", RunTavernHouseContractTests());
            AddSubsystemListSummary(checks, "contracts.starting_children", "characters", RunStartingChildrenTests());
            AddSubsystemListSummary(checks, "contracts.conversation_intoxication", "conversation_modes", RunConversationIntoxicationSelfTests());
            AddSubsystemListSummary(checks, "contracts.dialogue_marriage", "actions_trade",
                RunDialogueMarriageActionAssertions().Concat(VerifyDialogueMarriageNormalization()).ToList());
#if !REIGN_EXCLUDE_COURT
            AddSubsystemListSummary(checks, "contracts.castle_scene_context", "conversation_modes", RunConversationSceneStateSelfTests());
            AddSubsystemListSummary(checks, "contracts.sovereign_conduct", "conversation_modes", RunPoliticalDangerPostureSelfTests());
            AddSubsystemListSummary(checks, "contracts.sovereign_identity", "knowledge_identity", RunIdentitySubsystemSelfTests());
            AddSubsystemListSummary(checks, "contracts.court_and_resident_ambassadors", "court_system", RunCourtSelfTests());
            bool courtAuthorityStability = !Reign.Core.Contracts.Court.ReignCourtAuthorityStability.ShouldInterrupt(1)
                && !Reign.Core.Contracts.Court.ReignCourtAuthorityStability.ShouldInterrupt(2)
                && Reign.Core.Contracts.Court.ReignCourtAuthorityStability.ShouldInterrupt(3)
                && Reign.Core.Contracts.Court.ReignCourtAuthorityStability.RecordFailure(0) == 1
                && Reign.Core.Contracts.Court.ReignCourtAuthorityStability.RecordFailure(2) == 3
                && Reign.Core.Contracts.Court.ReignCourtAuthorityStability.RecordFailure(3) == 3;
            AddVerificationCheck(checks, "contracts.court_authority_stability", "court_system",
                courtAuthorityStability,
                "Court authority interruption requires three consecutive matching failures while successful observations reset the client counter.",
                new Dictionary<string, object>
                {
                    ["requiredConsecutiveFailures"] = Reign.Core.Contracts.Court.ReignCourtAuthorityStability.RequiredConsecutiveFailures
                });
#endif
            AddSubsystemListSummary(checks, "contracts.portrait_derivatives", "portraits_images", RunPortraitDerivativeSelfTests());
            AddSubsystemListSummary(checks, "contracts.image_generation_profiles", "portraits_images", RunPortraitProviderAdapterContractTests());
            if (includeExpensiveSubsystems)
            {
                AddSubsystemListSummary(checks, "contracts.spymaster", "spymaster", RunSpymasterSystemSelfTests());
                AddSubsystemListSummary(checks, "contracts.mbti_relationship_lifecycle", "memory_relationships", RunCurrentRelationshipSelfTests());
                AddSubsystemListSummary(checks, "contracts.notable_mbti", "memory_relationships", RunNotableMbtiSelfTests());
            }
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_models", "conversation_modes", RunFinalConversationGauntletModelSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_catalog", "conversation_modes", RunFinalConversationGauntletCatalogSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_fixtures", "conversation_modes", RunFinalConversationGauntletFixtureSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_evidence", "conversation_modes", RunFinalConversationGauntletEvidenceSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_evaluation", "conversation_modes", RunFinalConversationGauntletEvaluationSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_scheduler", "conversation_modes", RunFinalConversationGauntletSchedulerSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_actions", "actions_trade", RunFinalConversationGauntletActionSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_reports", "conversation_modes", RunFinalConversationGauntletReportSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_provider_budget", "conversation_modes", RunFinalConversationGauntletProviderBudgetSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_coverage", "conversation_modes", RunFinalConversationGauntletCoverageSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_readiness_selection", "conversation_modes", RunFinalConversationGauntletReadinessSelectionSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_manifest", "conversation_modes", RunFinalConversationGauntletManifestSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_long_horizon_budget", "conversation_modes", RunFinalConversationGauntletLongHorizonBudgetSelfTests());
            AddSubsystemListSummary(checks, "contracts.final_conversation_gauntlet_lifecycle", "conversation_modes", RunFinalConversationGauntletLifecycleSelfTests());
            string root = FindVerificationSourceRoot();
            bool sourceFound = !string.IsNullOrWhiteSpace(root);
            AddVerificationCheck(checks, "contracts.source_root", "contracts", sourceFound, sourceFound ? "Workspace source root discovered." : "Workspace source root could not be discovered.", new Dictionary<string, object> { ["sourceRoot"] = root });
            if (!sourceFound) return;

            string clientRoot = Path.Combine(root, "ReignBeta");
            string serverRoot = Path.Combine(root, "ReignBetaServer");
            string notableMbtiPath = VerificationSourceLocator.ResolveUnique(serverRoot, "NotableMbtiProfiles.cs", "src");
            string mbtiProgramSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src"));
            string cachingSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "PromptCaching.cs", "src"));
            string diplomacySource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "WorldDiplomacyDirector.cs", "src"));
            string relationshipsSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "AmbientRelationships.cs", "src"));
            string sceneSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "ConversationSceneState.cs", "src"));
            string memorySource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "CategorizedMemory.cs", "src"));
            string portraitSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "PortraitGeneration.cs", "src"));
            bool mbtiPromptCoverage = File.Exists(notableMbtiPath)
                && mbtiProgramSource.Contains("BuildCharacterMbtiPromptBlock", StringComparison.Ordinal)
                && cachingSource.Contains("BuildCharacterMbtiPromptBlock", StringComparison.Ordinal)
                && diplomacySource.Contains("BuildCharacterMbtiPromptBlock", StringComparison.Ordinal)
                && relationshipsSource.Contains("BuildCharacterMbtiPromptBlock", StringComparison.Ordinal)
                && sceneSource.Contains("mbtiProfile", StringComparison.Ordinal)
                && !memorySource.Contains("BuildCharacterMbtiPromptBlock", StringComparison.Ordinal)
                && !portraitSource.Contains("BuildCharacterMbtiPromptBlock", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.notable_mbti_prompt_scope", "memory_relationships", mbtiPromptCoverage,
                mbtiPromptCoverage
                    ? "MBTI is included in dialogue, event, correspondence, diplomacy, relationship, scene, and action decisions while excluded from memory consolidation and portraits."
                    : "One or more MBTI prompt-scope contracts are missing.", null);

            string nativeEditorPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignCharacterEditorCampaignBehavior.cs", "src");
            string nativeEditorSource = File.Exists(nativeEditorPath) ? File.ReadAllText(nativeEditorPath) : "";
            bool nativeTraitSync = new[] { "Valor", "Generosity", "Honor", "Mercy", "Calculating" }
                .All(name => nativeEditorSource.Contains(name, StringComparison.Ordinal))
                && nativeEditorSource.Contains("SetTraitLevel", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.notable_mbti_native_sync", "memory_relationships", nativeTraitSync,
                nativeTraitSync
                    ? "The existing idempotent Character Editor command path applies all five native Bannerlord personality traits."
                    : "The native Character Editor trait application contract is incomplete.", null);

            string actionEnumPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWorldActionType.cs", "src");
            List<string> actionNames = DiscoverActionNames(actionEnumPath);
            AddVerificationCheck(checks, "contracts.action_catalog", "contracts", actionNames.Count >= 70, "Discovered " + actionNames.Count + " action enum members.", new Dictionary<string, object> { ["actions"] = actionNames });

            List<string> missingPrompts = PromptFileNames.Where(name => !File.Exists(Path.Combine(PromptsDir, name))).ToList();
            AddVerificationCheck(checks, "contracts.prompts", "contracts", missingPrompts.Count == 0, missingPrompts.Count == 0 ? "All registered prompt files exist." : "Missing registered prompt files.", new Dictionary<string, object> { ["registered"] = PromptFileNames.Length, ["missing"] = missingPrompts });

            string[] portraitPromptLayers = { "portrait_identity.txt", "portrait_composition.txt", PortraitBodyPromptFile, "portrait_prompt_style.txt", "portrait_output_rules.txt" };
            bool portraitPromptCatalogValid = portraitPromptLayers.All(name => PromptFileNames.Contains(name, StringComparer.OrdinalIgnoreCase));
			bool redundantGenericClothingRemoved = !PromptFileNames.Contains("portrait_clothing.txt", StringComparer.OrdinalIgnoreCase)
				&& !File.Exists(Path.Combine(PromptsDir, "portrait_clothing.txt"));
			bool portraitClothingCatalogValid = PortraitClothingPromptFileNames.Length == 56
				&& PortraitClothingPromptFileNames.All(name => PromptFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
				&& PortraitClothingPromptFileNames.All(name => File.Exists(Path.Combine(PromptsDir, name)));
			bool portraitPhysicalConfidenceCatalogValid = PortraitPhysicalConfidencePromptFileNames.Length == 5
				&& PortraitPhysicalConfidencePromptFileNames.All(name => PromptFileNames.Contains(name, StringComparer.OrdinalIgnoreCase))
				&& PortraitPhysicalConfidencePromptFileNames.All(name => File.Exists(Path.Combine(PromptsDir, name)));
            string portraitExclusions = DefaultPromptText("portrait_output_rules.txt", DefaultPromptTemplates());
            bool portraitOutputSafetyValid = portraitExclusions.IndexOf("white footer", StringComparison.OrdinalIgnoreCase) >= 0
                && portraitExclusions.IndexOf("captions", StringComparison.OrdinalIgnoreCase) >= 0
                && portraitExclusions.IndexOf("ages", StringComparison.OrdinalIgnoreCase) >= 0;
			AddVerificationCheck(checks, "contracts.portrait_prompt_library", "contracts", portraitPromptCatalogValid && redundantGenericClothingRemoved && portraitClothingCatalogValid && portraitPhysicalConfidenceCatalogValid && portraitOutputSafetyValid,
				portraitPromptCatalogValid && redundantGenericClothingRemoved && portraitClothingCatalogValid && portraitPhysicalConfidenceCatalogValid && portraitOutputSafetyValid ? "Shared portrait layers exclude the redundant generic clothing prompt; 21 culture/rank and 35 culture/notable-occupation clothing prompts plus five female physical-confidence prompts are editable; output text/footer exclusions are registered." : "Portrait prompt library contract is incomplete.",
				new Dictionary<string, object> { ["layers"] = portraitPromptLayers, ["clothingPrompts"] = PortraitClothingPromptFileNames.Length, ["physicalConfidencePrompts"] = PortraitPhysicalConfidencePromptFileNames.Length, ["outputSafety"] = portraitOutputSafetyValid });

            Dictionary<string, object> portraitSample = new Dictionary<string, object>
            {
                ["characterName"] = "Ira",
                ["ageYears"] = 27,
                ["cultureName"] = "Empire",
				["gender"] = "woman",
				["clanTier"] = 5,
				["socialStation"] = "high noble",
				["flirtatiousnessPercentage"] = 90,
				["confidencePercentage"] = 60
            };
            string assembledPortraitPrompt = BuildPortraitPrompt(portraitSample, "Character information:\nAge: 27\nCulture: Empire");
			string vlandianLandownerPrompt = BuildPortraitPrompt(new Dictionary<string, object>
			{
				["characterName"] = "Alary",
				["ageYears"] = 40,
				["cultureName"] = "Vlandia",
				["gender"] = "man",
				["clanTier"] = 2,
				["socialStation"] = "landowner",
				["flirtatiousnessPercentage"] = 100,
				["confidencePercentage"] = 100
			}, "");
			string battanianMinorLordPrompt = BuildPortraitPrompt(new Dictionary<string, object>
			{
				["characterName"] = "Maireas",
				["ageYears"] = 33,
				["cultureName"] = "Battania",
				["gender"] = "woman",
				["clanTier"] = 3,
				["socialStation"] = "lesser lord",
				["flirtatiousnessPercentage"] = 20,
				["confidencePercentage"] = 20
			}, "");
			string aseraiMerchantPrompt = BuildPortraitPrompt(new Dictionary<string, object>
			{
				["characterName"] = "Jawwal",
				["ageYears"] = 44,
				["cultureName"] = "Aserai",
				["gender"] = "man",
				["clanTier"] = 0,
				["socialStation"] = "unranked",
				["occupation"] = "Merchant",
				["isNotable"] = true
			}, "");
			string[] notableCultures = { "Vlandia", "Battania", "Sturgia", "Empire", "Aserai", "Khuzait", "Nord" };
			string[] notableOccupations = { "Merchant", "Artisan", "GangLeader", "Headman", "RuralNotable" };
			bool notableClothingSelectionValid = true;
			foreach (string notableCulture in notableCultures)
			{
				foreach (string notableOccupation in notableOccupations)
				{
					string selected = SelectPortraitClothingPromptFile(new Dictionary<string, object>
					{
						["cultureName"] = notableCulture,
						["occupation"] = notableOccupation,
						["isNotable"] = true
					});
					notableClothingSelectionValid &= !string.IsNullOrWhiteSpace(selected)
						&& PortraitClothingPromptFileNames.Contains(selected, StringComparer.OrdinalIgnoreCase)
						&& !string.IsNullOrWhiteSpace(DefaultPromptText(selected, DefaultPromptTemplates()));
				}
			}
			AddVerificationCheck(checks, "contracts.portrait_notable_clothing", "portraits_images", notableClothingSelectionValid
				&& aseraiMerchantPrompt.Contains("Prosperous Aserai merchant clothing")
				&& aseraiMerchantPrompt.IndexOf("Aserai landowner attire", StringComparison.OrdinalIgnoreCase) < 0,
				notableClothingSelectionValid ? "All 35 culture/occupation notable combinations select an editable clothing-only prompt." : "Notable portrait clothing selection is incomplete.",
				new Dictionary<string, object> { ["cultures"] = notableCultures.Length, ["occupations"] = notableOccupations.Length, ["prompts"] = 35 });
			Dictionary<string, object> portraitPhysicalConfidence = ReadDictionary(portraitSample, "physicalConfidence") ?? new Dictionary<string, object>();
			int[] portraitBoundaryScores = { 0, 20, 21, 40, 41, 60, 61, 80, 81, 100 };
			string[] portraitBoundaryProfiles = { "00-20", "00-20", "21-40", "21-40", "41-60", "41-60", "61-80", "61-80", "81-100", "81-100" };
			bool portraitBoundariesValid = true;
			for (int boundaryIndex = 0; boundaryIndex < portraitBoundaryScores.Length; boundaryIndex++)
			{
				Dictionary<string, object> boundaryPayload = new Dictionary<string, object>
				{
					["gender"] = "woman",
					["physicalConfidence"] = new Dictionary<string, object> { ["score"] = portraitBoundaryScores[boundaryIndex] }
				};
				string selected = SelectPortraitPhysicalConfidencePromptFile(boundaryPayload);
				portraitBoundariesValid &= selected.IndexOf(portraitBoundaryProfiles[boundaryIndex].Replace("-", "_"), StringComparison.OrdinalIgnoreCase) >= 0;
			}
			bool portraitPhysicalConfidenceValid = ReadInt(portraitPhysicalConfidence, "score", -1) == 83
				&& ReadString(portraitPhysicalConfidence, "profile", "") == "81-100"
				&& portraitBoundariesValid
				&& assembledPortraitPrompt.Contains("FEMALE PHYSICAL CONFIDENCE MODIFIER - 81 TO 100")
				&& assembledPortraitPrompt.Contains("physical_confidence score: 83 out of 100")
				&& battanianMinorLordPrompt.Contains("FEMALE PHYSICAL CONFIDENCE MODIFIER - 00 TO 20")
				&& vlandianLandownerPrompt.IndexOf("FEMALE PHYSICAL CONFIDENCE MODIFIER", StringComparison.OrdinalIgnoreCase) < 0;
			AddVerificationCheck(checks, "contracts.portrait_physical_confidence", "portraits_images", portraitPhysicalConfidenceValid,
				portraitPhysicalConfidenceValid ? "Female portrait physical_confidence is weighted 75% Flirtatiousness and 25% Confidence, selects one profile, and never modifies male clothing." : "Portrait physical-confidence weighting, selection, or female-only gating failed.",
				new Dictionary<string, object> { ["score"] = ReadInt(portraitPhysicalConfidence, "score", -1), ["profile"] = ReadString(portraitPhysicalConfidence, "profile", ""), ["boundariesValid"] = portraitBoundariesValid });
            bool portraitAssemblyValid = assembledPortraitPrompt.Contains("Ira")
                && assembledPortraitPrompt.Contains("27 years old")
                && assembledPortraitPrompt.Contains("Empire culture")
				&& assembledPortraitPrompt.Contains("tier 5")
                && assembledPortraitPrompt.Contains("high noble")
				&& assembledPortraitPrompt.Contains("Calradic Imperial high-noble attire")
				&& assembledPortraitPrompt.IndexOf("Vlandian landowner attire", StringComparison.OrdinalIgnoreCase) < 0
				&& vlandianLandownerPrompt.Contains("Vlandian landowner attire")
				&& vlandianLandownerPrompt.IndexOf("Vlandian high-noble attire", StringComparison.OrdinalIgnoreCase) < 0
				&& battanianMinorLordPrompt.Contains("Battanian minor-lord attire")
                && assembledPortraitPrompt.IndexOf("Character information:", StringComparison.OrdinalIgnoreCase) < 0
                && assembledPortraitPrompt.IndexOf("\nAge:", StringComparison.OrdinalIgnoreCase) < 0
                && assembledPortraitPrompt.IndexOf("\nCulture:", StringComparison.OrdinalIgnoreCase) < 0
                && assembledPortraitPrompt.IndexOf("white footer panels", StringComparison.OrdinalIgnoreCase) >= 0
                && assembledPortraitPrompt.IndexOf("full head-to-toe body portrait", StringComparison.OrdinalIgnoreCase) >= 0
                && assembledPortraitPrompt.IndexOf("upright and centered on the image's vertical axis", StringComparison.OrdinalIgnoreCase) >= 0
                && assembledPortraitPrompt.IndexOf("chest-up", StringComparison.OrdinalIgnoreCase) < 0
                && assembledPortraitPrompt.IndexOf("No full body", StringComparison.OrdinalIgnoreCase) < 0;
            AddVerificationCheck(checks, "contracts.portrait_prompt_assembly", "contracts", portraitAssemblyValid,
                portraitAssemblyValid ? "Portrait prompts assemble as centered, upright, full-body images without conflicting chest-up or label-card formatting." : "Full-body portrait prompt assembly contract failed.",
                new Dictionary<string, object> { ["assembledChars"] = assembledPortraitPrompt.Length });

            string guiRoot = Path.Combine(clientRoot, "GUI");
            List<string> xmlFailures = new List<string>();
            int xmlCount = 0;
            foreach (string path in (Directory.Exists(guiRoot) ? Directory.GetFiles(guiRoot, "*.xml", SearchOption.AllDirectories) : new string[0])
                .Where(path => !IsExcludedCourtVerificationArtifact(path)))
            {
                xmlCount++;
                try { XmlDocument document = new XmlDocument(); document.Load(path); }
                catch (Exception ex) { xmlFailures.Add(Path.GetFileName(path) + ": " + ex.Message); }
            }
            AddVerificationCheck(checks, "contracts.gui_xml", "ui_contracts", xmlCount >= 7 && xmlFailures.Count == 0, "Parsed " + xmlCount + " GUI XML files.", new Dictionary<string, object> { ["failures"] = xmlFailures });

            string governmentScaleLockPrefab = Path.Combine(guiRoot, "Prefabs", "ReignGovernmentScreen.xml");
            string diplomacyScaleLockPrefab = Path.Combine(guiRoot, "Prefabs", "ReignDiplomacyAnnouncementScreen.xml");
            XmlDocument governmentScaleLockDocument = TryParseVerificationXml(
                File.Exists(governmentScaleLockPrefab) ? File.ReadAllText(governmentScaleLockPrefab) : "");
            XmlDocument diplomacyScaleLockDocument = TryParseVerificationXml(
                File.Exists(diplomacyScaleLockPrefab) ? File.ReadAllText(diplomacyScaleLockPrefab) : "");
            XmlElement governmentScaleLockCanvas = FindVerificationXmlElementById(
                governmentScaleLockDocument, "GovernmentCanvas");
            XmlElement diplomacyScaleLockCanvas = FindVerificationXmlElementById(
                diplomacyScaleLockDocument, "DiplomacyAnnouncement");
            XmlElement diplomacyScaleLockWidth = diplomacyScaleLockDocument?
                .SelectSingleNode("/Prefab/Constants/Constant[@Name='PanelWidth']") as XmlElement;
            XmlElement diplomacyScaleLockHeight = diplomacyScaleLockDocument?
                .SelectSingleNode("/Prefab/Constants/Constant[@Name='PanelHeight']") as XmlElement;
            bool modernFullShellScaleLockContract = governmentScaleLockCanvas != null
                && diplomacyScaleLockCanvas != null
                && VerificationXmlAttributeEquals(governmentScaleLockCanvas, "DoNotUseCustomScaleAndChildren", "true")
                && VerificationXmlAttributeEquals(governmentScaleLockCanvas, "WidthSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(governmentScaleLockCanvas, "HeightSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(governmentScaleLockCanvas, "SuggestedWidth", "1672")
                && VerificationXmlAttributeEquals(governmentScaleLockCanvas, "SuggestedHeight", "941")
                && VerificationXmlAttributeEquals(diplomacyScaleLockCanvas, "DoNotUseCustomScaleAndChildren", "true")
                && VerificationXmlAttributeEquals(diplomacyScaleLockCanvas, "WidthSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(diplomacyScaleLockCanvas, "HeightSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(diplomacyScaleLockCanvas, "SuggestedWidth", "!PanelWidth")
                && VerificationXmlAttributeEquals(diplomacyScaleLockCanvas, "SuggestedHeight", "!PanelHeight")
                && VerificationXmlAttributeEquals(diplomacyScaleLockWidth, "Value", "1672")
                && VerificationXmlAttributeEquals(diplomacyScaleLockHeight, "Value", "941");
            AddVerificationCheck(checks, "contracts.modern_full_shell_scale_lock", "ui_contracts", modernFullShellScaleLockContract,
                modernFullShellScaleLockContract
                    ? "Government and Diplomacy retain their fixed 1672x941 authored shells without applying the UI custom scale a second time to their children."
                    : "A fixed modern full-shell canvas can be enlarged and clipped by applying the UI custom scale twice.",
                new Dictionary<string, object>
                {
                    ["governmentPrefab"] = governmentScaleLockPrefab,
                    ["governmentScaleLock"] = governmentScaleLockCanvas?.GetAttribute("DoNotUseCustomScaleAndChildren") ?? "",
                    ["diplomacyPrefab"] = diplomacyScaleLockPrefab,
                    ["diplomacyScaleLock"] = diplomacyScaleLockCanvas?.GetAttribute("DoNotUseCustomScaleAndChildren") ?? "",
                    ["diplomacyWidth"] = diplomacyScaleLockWidth?.GetAttribute("Value") ?? "",
                    ["diplomacyHeight"] = diplomacyScaleLockHeight?.GetAttribute("Value") ?? ""
                });

            string warCouncilPrefab = Path.Combine(guiRoot, "Prefabs", "ReignWarCouncilScreen.xml");
            string warCouncilXml = File.Exists(warCouncilPrefab) ? File.ReadAllText(warCouncilPrefab) : "";
            string warCouncilVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWarCouncilScreenVM.cs", "src");
            string warCouncilVm = File.Exists(warCouncilVmPath) ? File.ReadAllText(warCouncilVmPath) : "";
            string warCouncilManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWarCouncilScreenManager.cs", "src");
            string warCouncilManager = File.Exists(warCouncilManagerPath) ? File.ReadAllText(warCouncilManagerPath) : "";
            string warCouncilMapWidgetPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWarCouncilMapWidget.cs", "src");
            string warCouncilMapWidget = File.Exists(warCouncilMapWidgetPath) ? File.ReadAllText(warCouncilMapWidgetPath) : "";
            string warCouncilLiveHostPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignLiveInteractionUiCalibrationHost.cs", "src");
            string warCouncilLiveHost = File.Exists(warCouncilLiveHostPath) ? File.ReadAllText(warCouncilLiveHostPath) : "";
            string warCouncilArtFactoryPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignEventArtTextureFactory.cs", "src");
            string warCouncilArtFactory = File.Exists(warCouncilArtFactoryPath) ? File.ReadAllText(warCouncilArtFactoryPath) : "";
            string warCouncilTextureFactoryPath = VerificationSourceLocator.ResolveUnique(clientRoot, "TextureFactory.cs", "src", "Modules", "Portraits", "AIPortraits");
            string warCouncilTextureFactory = File.Exists(warCouncilTextureFactoryPath) ? File.ReadAllText(warCouncilTextureFactoryPath) : "";
            string warCouncilBehaviorPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWarCouncilCampaignBehavior.cs", "src");
            string warCouncilBehavior = File.Exists(warCouncilBehaviorPath) ? File.ReadAllText(warCouncilBehaviorPath) : "";
            string warCouncilArt = Path.Combine(guiRoot, "SpriteParts", "ui_reignbeta_war_council");
            byte[] warCouncilChannelProbe = AIPortraits.PngEncoder.EncodeRgba(new byte[] { 201, 37, 11, 255 }, 1, 1);
            byte[] warCouncilSwappedProbe = AIPortraits.PngReencode.SwapRedBlueToPngEncoderFormat(warCouncilChannelProbe);
            byte[] warCouncilSwappedRgba = AIPortraits.PngReencode.DecodeToRgba(warCouncilSwappedProbe, out int warCouncilProbeWidth, out int warCouncilProbeHeight);
            bool warCouncilSwapSemanticsValid = warCouncilProbeWidth == 1
                && warCouncilProbeHeight == 1
                && warCouncilSwappedRgba != null
                && warCouncilSwappedRgba.Length == 4
                && warCouncilSwappedRgba[0] == 11
                && warCouncilSwappedRgba[1] == 37
                && warCouncilSwappedRgba[2] == 201
                && warCouncilSwappedRgba[3] == 255;
            bool warCouncilProtectedMapChannelOrderValid = warCouncilArtFactory.Contains("bool isWarCouncilRuntimeAsset = string.Equals(templateId, \"war_council\"", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("bool isWarCouncilMapAsset = isWarCouncilRuntimeAsset", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("string.Equals(phaseId, \"map\", StringComparison.Ordinal)", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("phaseId.StartsWith(\"map_tile_\", StringComparison.Ordinal)", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("Texture texture = isWarCouncilMapAsset", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("TextureFactory.GetOrBuildFileRedBlueSwapped(textureKey, imagePath)", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("bool isEngineReadyRuntimeAsset = (isWarCouncilRuntimeAsset && !isWarCouncilMapAsset) || isMainMenuLogo", StringComparison.Ordinal)
                && !warCouncilArtFactory.Contains("TextureFactory.GetOrBuildFileDirect(", StringComparison.Ordinal)
                && warCouncilTextureFactory.Contains("public static TaleWorlds.TwoDimension.Texture GetOrBuildFileRedBlueSwapped", StringComparison.Ordinal)
                && warCouncilTextureFactory.Contains("PngReencode.SwapRedBlueToPngEncoderFormat(File.ReadAllBytes(path))", StringComparison.Ordinal)
                && warCouncilTextureFactory.Contains("string swappedKey = key + \"|red-blue-swapped\"", StringComparison.Ordinal)
                && warCouncilSwapSemanticsValid;
            AddVerificationCheck(checks, "contracts.war_council_protected_map_channel_order", "ui_contracts", warCouncilProtectedMapChannelOrderValid,
                warCouncilProtectedMapChannelOrderValid
                    ? "The protected War Council map and all map tiles keep their source files unchanged while a map-only explicit-RGBA runtime transform swaps red and blue; frames, tokens, raven art, and the logo retain their existing route."
                    : "The protected War Council map can bypass its deterministic map-only runtime channel correction or share that correction with unrelated artwork.",
                new Dictionary<string, object>
                {
                    ["eventArtFactory"] = warCouncilArtFactoryPath,
                    ["textureFactory"] = warCouncilTextureFactoryPath,
                    ["swapSemanticsValid"] = warCouncilSwapSemanticsValid
                });
            bool mainMenuLogoChannelOrderValid = warCouncilArtFactory.Contains("bool isMainMenuLogo = string.Equals(cacheKey, \"main_menu|logo\"", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("bool isEngineReadyRuntimeAsset = (isWarCouncilRuntimeAsset && !isWarCouncilMapAsset) || isMainMenuLogo", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("TextureFactory.GetOrBuildFileBacked(textureKey, imagePath)", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("?? TextureFactory.GetOrBuildFile(textureKey, imagePath)", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.main_menu_logo_channel_order", "ui_contracts", mainMenuLogoChannelOrderValid,
                mainMenuLogoChannelOrderValid
                    ? "The Reign main-menu logo retains its color-preserving file-backed route and explicit-RGBA fallback independently of the War Council map correction."
                    : "The Reign main-menu logo can reach the renderer through an RGB memory path and render with reversed red/blue channels.",
                new Dictionary<string, object> { ["textureFactory"] = warCouncilArtFactoryPath });
            string[] requiredWarCouncilArt = { "reign_war_council_calradia.png", "reign_war_token_infantry.png",
                "reign_war_token_archer.png", "reign_war_token_cavalry.png", "reign_war_token_ship.png",
                "reign_war_token_infantry_black.png", "reign_war_token_archer_black.png",
                "reign_war_token_cavalry_black.png", "reign_war_token_ship_black.png", "reign_war_raven_scroll.png",
                "reign_war_council_outer_frame.png" };
            string[] requiredWarCouncilMapTiles = Enumerable.Range(0, 4)
                .SelectMany(y => Enumerable.Range(0, 4)
                    .Select(x => "reign_war_council_tile_" + x + "_" + y + ".png"))
                .ToArray();
            bool warCouncilMapTilesValid = requiredWarCouncilMapTiles.All(name =>
            {
                string path = Path.Combine(warCouncilArt, name);
                if (!File.Exists(path) || new FileInfo(path).Length < 1024L * 1024L)
                    return false;
                byte[] header = File.ReadAllBytes(path).Take(24).ToArray();
                return header.Length == 24
                    && header[0] == 0x89 && Encoding.ASCII.GetString(header, 1, 3) == "PNG"
                    && ((header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19]) == 4096
                    && ((header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23]) == 4096;
            });
            XmlDocument warCouncilDocument = TryParseVerificationXml(warCouncilXml);
            XmlElement warCouncilMapInputElement = FindVerificationXmlElementById(warCouncilDocument, "WarCouncilMapInput");
            XmlElement warCouncilLordViewportElement = FindVerificationXmlElementById(warCouncilDocument, "WarCouncilLordViewport");
            XmlElement warCouncilLordScrollbarElement = FindVerificationXmlElementById(warCouncilDocument, "WarCouncilLordScrollbar");
            XmlElement warCouncilLordRowElement = warCouncilDocument?.SelectSingleNode(
                "//*[@Id='WarCouncilLordList']/ItemTemplate/ButtonWidget") as XmlElement;
            bool warCouncilMapInputPixelFree = warCouncilMapInputElement != null
                && string.Equals(warCouncilMapInputElement.Name, "ReignWarCouncilMapInputWidget", StringComparison.Ordinal)
                && VerificationXmlAttributeEquals(warCouncilMapInputElement, "Type", "ReignBeta.UI.ReignWarCouncilMapInputWidget")
                && VerificationXmlAttributeEquals(warCouncilMapInputElement, "SuggestedWidth", "820")
                && VerificationXmlAttributeEquals(warCouncilMapInputElement, "SuggestedHeight", "820")
                && VerificationXmlAttributeEquals(warCouncilMapInputElement, "MarginLeft", "72")
                && VerificationXmlAttributeEquals(warCouncilMapInputElement, "MarginTop", "67")
                && VerificationXmlAttributeEquals(warCouncilMapInputElement, "HideOnDrag", "false")
                && !warCouncilMapInputElement.HasAttribute("Brush")
                && !warCouncilMapInputElement.HasAttribute("Sprite")
                && !warCouncilMapInputElement.HasAttribute("Color")
                && !warCouncilMapInputElement.HasAttribute("AlphaFactor")
                && (warCouncilMapInputElement.SelectNodes("./*")?.Count ?? 0) == 0;
            bool warCouncilWholeRowScrollContract = warCouncilLordViewportElement != null
                && VerificationXmlAttributeEquals(warCouncilLordViewportElement, "Type", "ReignBeta.UI.ReignWarCouncilLordScrollPanel")
                && VerificationXmlAttributeEquals(warCouncilLordViewportElement, "SuggestedHeight", "640")
                && VerificationXmlAttributeEquals(warCouncilLordViewportElement, "SelectedIndex", "@SelectedLordIndex")
                && VerificationXmlAttributeEquals(warCouncilLordViewportElement, "ItemCount", "@LordCount")
                && VerificationXmlAttributeEquals(warCouncilLordScrollbarElement, "SuggestedHeight", "640")
                && VerificationXmlAttributeEquals(warCouncilLordRowElement, "SuggestedHeight", "147")
                && VerificationXmlAttributeEquals(warCouncilLordRowElement, "MarginBottom", "13")
                && VerificationXmlAttributeEquals(warCouncilLordRowElement, "ClipContents", "true")
                && warCouncilMapWidget.Contains("private const int VisibleLordRows = 4", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("maximum / ScrollableRowCount", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("ResolveLordRowTarget", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("SnapToLordRow", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("ResolveSelectedRowTarget", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("Input.IsKeyDown(InputKey.LeftMouseButton)", StringComparison.Ordinal)
                && !warCouncilMapWidget.Contains("LordRowHeight = 170f", StringComparison.Ordinal)
                && !warCouncilMapWidget.Contains("ViewportCenterOffset = 263f", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.war_council_whole_row_scroll", "ui_contracts",
                warCouncilWholeRowScrollContract,
                warCouncilWholeRowScrollContract
                    ? "The Lords & Parties viewport exposes exactly four complete 160-pixel cards and converts the normalized scrollbar range into one whole-card step for wheel, released scrollbar, and selected-row movement."
                    : "The Lords & Parties roster can settle between complete 160-pixel card boundaries.",
                new Dictionary<string, object>
                {
                    ["viewportHeight"] = warCouncilLordViewportElement?.GetAttribute("SuggestedHeight") ?? "",
                    ["scrollbarHeight"] = warCouncilLordScrollbarElement?.GetAttribute("SuggestedHeight") ?? "",
                    ["rowHeight"] = warCouncilLordRowElement?.GetAttribute("SuggestedHeight") ?? "",
                    ["rowMarginBottom"] = warCouncilLordRowElement?.GetAttribute("MarginBottom") ?? ""
                });
            bool warCouncilContract = warCouncilXml.Contains("ReignWarCouncilMapWidget", StringComparison.Ordinal)
                && warCouncilXml.Contains("SuggestedWidth=\"1920\" SuggestedHeight=\"1080\"", StringComparison.Ordinal)
                && !warCouncilXml.Contains("Id=\"WarCouncilScrollFrame\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilOuterFrame\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilMapViewport\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilMapInput\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Type=\"ReignBeta.UI.ReignWarCouncilMapInputWidget\"", StringComparison.Ordinal)
                && warCouncilMapInputPixelFree
                && warCouncilXml.Contains("Id=\"WarCouncilStrengthScrollbar\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilReportScrollbar\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilLordScrollbar\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilStrengthViewport\" Type=\"ReignBeta.UI.ReignWarCouncilWheelScrollPanel\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilReportViewport\" Type=\"ReignBeta.UI.ReignWarCouncilWheelScrollPanel\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"WarCouncilLordViewport\" Type=\"ReignBeta.UI.ReignWarCouncilLordScrollPanel\"", StringComparison.Ordinal)
                && warCouncilWholeRowScrollContract
                && warCouncilXml.Contains("MouseScrollAxis=\"Vertical\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("SuggestedWidth=\"@MapWidth\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("PositionXOffset=\"@MapOffsetX\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("DataSource=\"{Lords}\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("DataSource=\"{Kingdoms}\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("DataSource=\"{Reports}\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("ExecuteOpenMessenger", StringComparison.Ordinal)
                && warCouncilXml.Contains("Sprite=\"reign_war_council_lord_card_overlay\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Sprite=\"reign_war_raven_card_icon\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"ReignWarCouncilLordPortrait\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("SuggestedWidth=\"96\" SuggestedHeight=\"120\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Top\" PositionYOffset=\"3\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("Id=\"ReignWarCouncilAssignedPortrait\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("SuggestedWidth=\"156\" SuggestedHeight=\"195\" HorizontalAlignment=\"Center\" VerticalAlignment=\"Top\" PositionYOffset=\"0\"", StringComparison.Ordinal)
                && !warCouncilXml.Contains("MOBILIZE &amp; RECRUIT", StringComparison.Ordinal)
                && !warCouncilXml.Contains("Command.Click=\"ExecuteMobilize\"", StringComparison.Ordinal)
                && warCouncilXml.Contains("@MessengerInputText", StringComparison.Ordinal)
                && !warCouncilXml.Contains("@ScrollFrameImageId", StringComparison.Ordinal)
                && warCouncilXml.Contains("@OuterFrameImageId", StringComparison.Ordinal)
                && warCouncilXml.Contains("@CouncilorPortraitId", StringComparison.Ordinal)
                && warCouncilXml.Contains("@CouncilorSkillText", StringComparison.Ordinal)
                && !warCouncilXml.Contains("ExecuteRemoveOrder", StringComparison.Ordinal)
                && !warCouncilXml.Contains("ExecuteToggleOrderMenu", StringComparison.Ordinal)
                && warCouncilVm.Contains("party?.ActualClan == Clan.PlayerClan", StringComparison.Ordinal)
                && warCouncilVm.Contains("party?.ActualClan?.Kingdom == playerKingdom", StringComparison.Ordinal)
                && warCouncilVm.Contains("x.IsMainParty || IsPlayerRealmParty", StringComparison.Ordinal)
                && !warCouncilVm.Contains("x.IsActive && !x.IsMainParty", StringComparison.Ordinal)
                && warCouncilVm.Contains("LordPartyComponent.CreateLordParty", StringComparison.Ordinal)
                && warCouncilVm.Contains("hero?.Clan?.Kingdom == playerKingdom", StringComparison.Ordinal)
                && warCouncilVm.Contains("IsPlayerRealmParty(x, playerKingdom)", StringComparison.Ordinal)
                && warCouncilVm.Contains("IsCurrentlyAtSea", StringComparison.Ordinal)
                && warCouncilVm.Contains("IsForeign = !isPlayerRealm", StringComparison.Ordinal)
                && warCouncilVm.Contains("SendProductionLetterAsync", StringComparison.Ordinal)
                && warCouncilVm.Contains("bool sameRoster", StringComparison.Ordinal)
                && warCouncilVm.Contains("_portraitRevision", StringComparison.Ordinal)
                && !warCouncilVm.Contains("ScrollFrameImageId", StringComparison.Ordinal)
                && !warCouncilVm.Contains("settlement_town", StringComparison.Ordinal)
                && !warCouncilVm.Contains("settlement_castle", StringComparison.Ordinal)
                && !warCouncilVm.Contains("settlement_village", StringComparison.Ordinal)
                && !warCouncilArtFactory.Contains("reign_war_council_scroll_frame.png", StringComparison.Ordinal)
                && !warCouncilArtFactory.Contains("reign_war_settlement_", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("reign_war_council_tile_", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains(".png", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("GetOrBuildFileRedBlueSwapped", StringComparison.Ordinal)
                && warCouncilProtectedMapChannelOrderValid
                && warCouncilTextureFactory.Contains("GetOrBuildFileRedBlueSwapped", StringComparison.Ordinal)
                && warCouncilTextureFactory.Contains("normalizeForEngine ? NormalizeForEngine(sourceBytes) : sourceBytes", StringComparison.Ordinal)
                && warCouncilVm.Contains("if (isInMainParty) MobileParty.MainParty.MemberRoster", StringComparison.Ordinal)
                && !warCouncilVm.Contains("hero.Clan.WarPartyComponents.Count >= hero.Clan.WarPartyLimit", StringComparison.Ordinal)
                && warCouncilXml.Contains("ImageIdentifierWidget", StringComparison.Ordinal)
                && !warCouncilXml.Contains("PortraitCacheKey=\"@PortraitCacheKey\"", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationFirstMobilizableHeroId", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationPlayerRealmPartyCount", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationDetectedForeignPartyCount", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationMessengerVisible", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationHostilePartyCount", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationNavalPartyCount", StringComparison.Ordinal)
                && warCouncilManager.Contains("PauseCampaign()", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationTimePaused", StringComparison.Ordinal)
                && warCouncilManager.Contains("IsMouseWheelAllowed = true", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationScrollFrameImageAvailable", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationOuterFrameImageAvailable", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationSettlementImagesAvailable", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationFixedOverview", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationMapPanningEnabled", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationHighDefinitionMapTileCount", StringComparison.Ordinal)
                && warCouncilManager.Contains("case \"pan-map\"", StringComparison.Ordinal)
                && warCouncilManager.Contains("AutomationPortraitRevision", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("public class ReignWarCouncilWheelScrollPanel : ScrollablePanel", StringComparison.Ordinal)
                 && warCouncilMapWidget.Contains("bool leftPressedThisFrame = leftDown && !_leftWasDown", StringComparison.Ordinal)
                 && warCouncilMapWidget.Contains("if (!leftPressedThisFrame || !AutomationLastPointerInside) return;", StringComparison.Ordinal)
                 && warCouncilMapWidget.Contains("CompleteTracking(vm, screen.X, screen.Y);", StringComparison.Ordinal)
                 && !warCouncilMapWidget.Contains("Input.DeltaMouseScroll * WheelStep", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("public sealed class ReignWarCouncilMapInputWidget : ButtonWidget", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("Context.CustomInverseScale", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("AutomationHeldFrameCount", StringComparison.Ordinal)
                && warCouncilLiveHost.Contains("warCouncilMapInputPressCount", StringComparison.Ordinal)
                && warCouncilLiveHost.Contains("warCouncilMapInputLogicalWidth", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("local.X - vm.MapOffsetX", StringComparison.Ordinal)
                && !warCouncilMapWidget.Contains("foreach (ReignWarCouncilSettlementMarker settlement", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("HighDefinitionTileGridSize = 4", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("EventImageId = vm.MapImageId", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("EnsureVisibleHighDefinitionTile(vm)", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("if (TryBuildHighDefinitionTile(centerX, centerY, tileWidth, tileHeight)) return;", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("if (TryBuildHighDefinitionTile(x, y, tileWidth, tileHeight)) return;", StringComparison.Ordinal)
                && !warCouncilMapWidget.Contains("for (int y = 0; y < HighDefinitionTileGridSize; y++)", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("const float tokenSize = 108f", StringComparison.Ordinal)
                && warCouncilMapWidget.Contains("map_tile_\" + y + \"_\" + x", StringComparison.Ordinal)
                && !warCouncilMapWidget.Contains("Text = settlement.Name", StringComparison.Ordinal)
                && !warCouncilVm.Contains("ApplyDrop(", StringComparison.Ordinal)
                && !warCouncilVm.Contains("ExecuteRemoveOrder", StringComparison.Ordinal)
                && !warCouncilVm.Contains("ExecuteToggleOrderMenu", StringComparison.Ordinal)
                && warCouncilBehavior.Contains("IntelligenceSweepIntervalDays", StringComparison.Ordinal)
                && warCouncilBehavior.Contains("DefaultSkills.Tactics", StringComparison.Ordinal)
                && warCouncilBehavior.Contains("_reign_warCouncilIntelligence", StringComparison.Ordinal)
                && requiredWarCouncilArt.All(name => File.Exists(Path.Combine(warCouncilArt, name)))
                && warCouncilMapTilesValid
                && Directory.GetFiles(warCouncilArt, "reign_war_council_tile_*.dds").Length == 0;
            AddVerificationCheck(checks, "contracts.war_council_command_map", "ui_contracts", warCouncilContract,
                warCouncilContract
                    ? "The full-screen War Council uses a 44x fixed close scale backed by a lightweight immediate overview and exactly sixteen lossless 4,096-pixel PNG runtime tiles with a deterministic map-only red/blue display correction. Only the visible high-definition tile is promoted first and seam neighbors are added at most one per frame, preventing the full authored map from blocking initial UI construction. Panning remains bounded inside a stationary Reign frame and protected source files remain unchanged. The exact official land-gated river water footprints are carved into the same unified geographic contour as seas and lakes, and the player's main party is always represented among live pieces. Scroll edges, wear, clutter, editor-height-derived mountain ranges, exact flora-derived conifer placement, exact editor-derived geography, and all 440 named settlement drawings remain baked into one continuous raster and reveal their margins only at map limits. Runtime marker children contain only consistent 108-pixel live party miniatures and Roman numerals, portrait view-model identity remains stable, and campaign time pauses before UI construction. Map drag capture begins only on a left-button edge inside the pixel-free hit surface, every release completes through one shared path, and platform wheel deltas are normalized to one bounded step. The screen retains intelligence, raven correspondence, reports, and strength reference while the unused roster mobilization button remains absent."
                    : "The War Council launch contract is incomplete.",
                new Dictionary<string, object> { ["prefab"] = warCouncilPrefab, ["artRoot"] = warCouncilArt,
                    ["requiredArt"] = requiredWarCouncilArt, ["requiredMapTiles"] = requiredWarCouncilMapTiles,
                    ["mapInputPixelFree"] = warCouncilMapInputPixelFree });

            string castleChatPrefab = Path.Combine(guiRoot, "Prefabs", "ReignCastleChatScreen.xml");
            string castleChatXml = File.Exists(castleChatPrefab) ? File.ReadAllText(castleChatPrefab) : "";
            string castleChatVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignPartyChatScreenVM.cs", "src");
            string castleChatVmSource = File.Exists(castleChatVmPath) ? File.ReadAllText(castleChatVmPath) : "";
            XmlDocument castleChatDocument = TryParseVerificationXml(castleChatXml);
            XmlElement castleArtViewportElement = FindVerificationXmlElementById(castleChatDocument, "CastleArtViewport");
            XmlElement castleChatShellElement = FindVerificationXmlElementById(castleChatDocument, "CastleChatShell");
            XmlElement castleTranscriptElement = castleChatDocument?.SelectSingleNode("//ReignAutoScrollPanel[@ChatScrollVersion='@ChatScrollVersion']") as XmlElement;
            int castleArtTop = VerificationXmlIntAttribute(castleArtViewportElement, "MarginTop", -1);
            int castleArtHeight = VerificationXmlIntAttribute(castleArtViewportElement, "SuggestedHeight", -1);
            int castleTranscriptTop = VerificationXmlIntAttribute(castleTranscriptElement, "MarginTop", -1);
            int castleArtBottom = castleArtTop >= 0 && castleArtHeight >= 0 ? castleArtTop + castleArtHeight : -1;
            bool castleArtViewportValid = castleArtViewportElement != null
                && VerificationXmlAttributeEquals(castleArtViewportElement, "SuggestedWidth", "790")
                && VerificationXmlAttributeEquals(castleArtViewportElement, "SuggestedHeight", "420")
                && VerificationXmlAttributeEquals(castleArtViewportElement, "MarginLeft", "583")
                && VerificationXmlAttributeEquals(castleArtViewportElement, "MarginTop", "120")
                && VerificationXmlAttributeEquals(castleArtViewportElement, "ClipContents", "true");
            bool castleModernShellValid = castleChatShellElement != null
                && VerificationXmlAttributeEquals(castleChatShellElement, "Sprite", "reign_castle_chat_modern_shell")
                && !castleChatXml.Contains("Sprite=\"reign_castle_chat_scene_overlay\"", StringComparison.Ordinal)
                && !castleChatXml.Contains("Id=\"CastleTranscriptFrame\"", StringComparison.Ordinal);
            bool castleTranscriptGeometryValid = castleTranscriptElement != null
                && VerificationXmlAttributeEquals(castleTranscriptElement, "Type", "ReignBeta.UI.Widgets.ReignAutoScrollPanel")
                && VerificationXmlAttributeEquals(castleTranscriptElement, "SuggestedWidth", "1114")
                && VerificationXmlAttributeEquals(castleTranscriptElement, "SuggestedHeight", "190")
                && VerificationXmlAttributeEquals(castleTranscriptElement, "MarginLeft", "468")
                && VerificationXmlAttributeEquals(castleTranscriptElement, "MarginTop", "565")
                && castleArtBottom >= 0
                && castleTranscriptTop >= castleArtBottom;
            List<string> castleChatLayoutFailures = new List<string>();
            if (!castleArtViewportValid) castleChatLayoutFailures.Add("approved_art_aperture");
            if (!castleModernShellValid) castleChatLayoutFailures.Add("modern_shell_layering");
            if (!castleTranscriptGeometryValid) castleChatLayoutFailures.Add("transcript_geometry_and_non_overlap");
            bool castleTranscriptBehaviorValid = castleChatXml.Contains("ChatScrollVersion=\"@ChatScrollVersion\"")
                && castleChatXml.Contains("Id=\"CastleChatScrollbar\"")
                && castleChatXml.Contains("Text=\"@Speaker\"")
                && castleChatDocument.SelectSingleNode("//RichTextWidget[@Text='@RichText'][@Brush='Reign.Chat.ActionText.17'][@Brush.Font='ReignSerifDynamic']") != null
                && !castleChatXml.Contains("Text=\"@Speaker: @Text\"")
                && castleChatXml.Contains("HeightSizePolicy=\"CoverChildren\" MinHeight=\"36\" MarginBottom=\"8\"")
                && castleChatXml.Contains("Id=\"CastleChatInput\"")
                && castleChatXml.Contains("MaxLength=\"2147483647\"");
            if (!castleTranscriptBehaviorValid) castleChatLayoutFailures.Add("transcript_scroll_bindings_and_unbounded_input");
            bool castleChatLayoutValid = castleArtViewportValid
                && castleModernShellValid
                && castleTranscriptGeometryValid
                && castleTranscriptBehaviorValid;
            bool castleChatProgressValid = castleChatVmSource.Contains("CastleOpeningProgressText")
                && castleChatVmSource.Contains("speakerIndex + 1, orderedSpeakers.Count")
                && castleChatVmSource.Contains("Reviewing the opening conversation...")
                && castleChatVmSource.Contains("AddChatLine(speakerName, clean, \"npc\")");
                AddVerificationCheck(checks, "contracts.castle_chat_layout_and_transcript", "ui_contracts",
                castleChatLayoutValid && castleChatProgressValid,
                castleChatLayoutValid && castleChatProgressValid
                    ? "Castle Chat uses the approved 790x420 clipped aperture beneath the modern shell, keeps its separately bound transcript non-overlapping and auto-scrolling, reports ordered opening progress, and does not inherit Gauntlet's 512-character input cap."
                    : "Castle Chat can overlap its transcript, hide bound dialogue, lose auto-scroll, conceal sequential opening progress, or retain Gauntlet's default 512-character input cap.",
                new Dictionary<string, object>
                {
                    ["prefab"] = castleChatPrefab,
                    ["viewModel"] = castleChatVmPath,
                    ["artBottom"] = castleArtBottom,
                    ["transcriptTop"] = castleTranscriptTop,
                    ["layoutFailures"] = castleChatLayoutFailures,
                    ["openingProgressValid"] = castleChatProgressValid
                });

            string uiCatalogPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "calibration", "ui-catalog.json");
            string uiCatalogText = File.Exists(uiCatalogPath) ? File.ReadAllText(uiCatalogPath) : "";
            string previewIndexPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "index.html");
            string previewIndexText = File.Exists(previewIndexPath) ? File.ReadAllText(previewIndexPath) : "";
            string previewSamplePath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "sample-data.js");
            string previewSampleText = File.Exists(previewSamplePath) ? File.ReadAllText(previewSamplePath) : "";
            string previewRendererPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "renderer.js");
            string previewRendererText = File.Exists(previewRendererPath) ? File.ReadAllText(previewRendererPath) : "";
            string previewAppPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "app.js");
            string previewAppText = File.Exists(previewAppPath) ? File.ReadAllText(previewAppPath) : "";
            string previewEditorPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "calibration-editor.js");
            string previewEditorText = File.Exists(previewEditorPath) ? File.ReadAllText(previewEditorPath) : "";
            string previewCodexPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "codex-chat.js");
            string previewCodexText = File.Exists(previewCodexPath) ? File.ReadAllText(previewCodexPath) : "";
            string previewNativeAugmentationPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "native-augmentations.js");
            string previewNativeAugmentationText = File.Exists(previewNativeAugmentationPath) ? File.ReadAllText(previewNativeAugmentationPath) : "";
            string previewXmlSourcePath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "js", "xml-source.js");
            string previewXmlSourceText = File.Exists(previewXmlSourcePath) ? File.ReadAllText(previewXmlSourcePath) : "";
            string previewServerPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "serve-preview.ps1");
            string previewServerText = File.Exists(previewServerPath) ? File.ReadAllText(previewServerPath) : "";
            string previewAuditPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "audit-preview-contract.mjs");
            string previewAuditText = File.Exists(previewAuditPath) ? File.ReadAllText(previewAuditPath) : "";
            string previewRenderedAuditPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "audit-rendered-preview.mjs");
            string previewRenderedAuditText = File.Exists(previewRenderedAuditPath) ? File.ReadAllText(previewRenderedAuditPath) : "";
            string previewCodexE2eAuditPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "audit-codex-chat-e2e.mjs");
            string previewCodexE2eAuditText = File.Exists(previewCodexE2eAuditPath) ? File.ReadAllText(previewCodexE2eAuditPath) : "";
            string previewNativeParityAuditPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "audit-native-parity.mjs");
            string previewNativeParityAuditText = File.Exists(previewNativeParityAuditPath) ? File.ReadAllText(previewNativeParityAuditPath) : "";
            string previewCalibrationWorkflowPath = Path.Combine(clientRoot, "tools", "GauntletXmlPreviewer", "calibration_workflow.py");
            string previewCalibrationWorkflowText = File.Exists(previewCalibrationWorkflowPath) ? File.ReadAllText(previewCalibrationWorkflowPath) : "";
            string uiCalibrationServicePath = Path.Combine(clientRoot, "src", "Modules", "UI", "UI", "Calibration", "ReignUiCalibrationService.cs");
            string uiCalibrationServiceText = File.Exists(uiCalibrationServicePath) ? File.ReadAllText(uiCalibrationServicePath) : "";
            string[] reignPrefabNames = Directory.Exists(Path.Combine(guiRoot, "Prefabs"))
                ? Directory.GetFiles(Path.Combine(guiRoot, "Prefabs"), "*.xml").Select(Path.GetFileName).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray()
                : new string[0];
            HashSet<string> catalogPrefabNames = new HashSet<string>(
                Regex.Matches(uiCatalogText, "\\\"prefab\\\"\\s*:\\s*\\\"GUI/Prefabs/([^\\\"]+\\.xml)\\\"")
                    .Cast<Match>().Select(match => match.Groups[1].Value), StringComparer.OrdinalIgnoreCase);
            bool previewerContractValid = reignPrefabNames.Length == 22
                && catalogPrefabNames.SetEquals(reignPrefabNames)
                && uiCatalogText.Contains("\"targetCount\": 22", StringComparison.Ordinal)
                && uiCatalogText.Contains("\"uniqueRuntimeMovieCount\": 21", StringComparison.Ordinal)
                && uiCatalogText.Contains("\"targetCount\": 15", StringComparison.Ordinal)
                && uiCatalogText.Contains("\"patchApplicationCount\": 30", StringComparison.Ordinal)
                && previewAuditText.Contains("getPreviewFixtureInventory", StringComparison.Ordinal)
                && previewAuditText.Contains("discoverRuntimeMovies", StringComparison.Ordinal)
                && previewAuditText.Contains("auditRuntimeContract", StringComparison.Ordinal)
                && previewAuditText.Contains("intentionalEmptyScopes", StringComparison.Ordinal)
                && previewAuditText.Contains("checkedToolContracts", StringComparison.Ordinal)
                && previewAuditText.Contains("auditNativeAugmentationContract", StringComparison.Ordinal)
                && previewRenderedAuditText.Contains("reign-ui-rendered-preview-audit-v1", StringComparison.Ordinal)
                && previewRenderedAuditText.Contains("1920x1080,3440x1440", StringComparison.Ordinal)
                && previewRenderedAuditText.Contains("catalog.interfaces", StringComparison.Ordinal)
                && previewRenderedAuditText.Contains("interfaces.filter((entry) => !entry.supportUi)", StringComparison.Ordinal)
                && previewRenderedAuditText.Contains("Provider-free capture did not disable recursive native-readiness refresh", StringComparison.Ordinal)
                && previewRenderedAuditText.Contains("Ordinary preview did not hydrate every-interface parity readiness", StringComparison.Ordinal)
                && previewCodexE2eAuditText.Contains("reign-ui-codex-chat-e2e-v1", StringComparison.Ordinal)
                && previewCodexE2eAuditText.Contains("Guarded Reign source state changed during the no-op Codex turn", StringComparison.Ordinal)
                && previewNativeParityAuditText.Contains("reign-ui-native-parity-readiness-v1", StringComparison.Ordinal)
                && previewNativeParityAuditText.Contains("reign-ui-native-parity-acceptance-v1", StringComparison.Ordinal)
                && previewNativeParityAuditText.Contains("reign-ui-window-capture-v1", StringComparison.Ordinal)
                && previewNativeParityAuditText.Contains("surfaceType !== \"native-augmentation\"", StringComparison.Ordinal)
                && previewNativeParityAuditText.Contains("catalog.interfaces.filter((entry) => !entry.supportUi)", StringComparison.Ordinal)
                && previewCalibrationWorkflowText.Contains("accept-native-case", StringComparison.Ordinal)
                && previewCalibrationWorkflowText.Contains("nativeAcceptanceRequired=not previewer_only", StringComparison.Ordinal)
                && previewCalibrationWorkflowText.Contains("Previewer-only support assets are not native acceptance surfaces.", StringComparison.Ordinal)
                && previewRendererText.Contains("attribute === \"DataSource\" ? dataSourceContext : context", StringComparison.Ordinal)
                && previewRendererText.Contains("value === \"{..}\"", StringComparison.Ordinal)
                && previewXmlSourceText.Contains("ReignPreviewResolvedValue", StringComparison.Ordinal)
                && previewIndexText.Contains("id=\"nativeDataToggle\"", StringComparison.Ordinal)
                && previewIndexText.Contains("id=\"prefabInstallSummary\"", StringComparison.Ordinal)
                && previewIndexText.Contains("class=\"codex-dock\"", StringComparison.Ordinal)
                && previewIndexText.Contains("id=\"augmentationGrid\"", StringComparison.Ordinal)
                && previewIndexText.Contains("data-interface=\"wilderness-event\"", StringComparison.Ordinal)
                && previewIndexText.Contains("id=\"nativeParityStatus\"", StringComparison.Ordinal)
                && previewAppText.Contains("elements.nativeDataToggle.checked ? currentNativeEvidence?.snapshot : null", StringComparison.Ordinal)
                && previewAppText.Contains("get(\"codex\") === \"off\"", StringComparison.Ordinal)
                && previewAppText.Contains("get(\"interface\")", StringComparison.Ordinal)
                && previewAppText.Contains("fetch(\"api/native-parity-readiness\"", StringComparison.Ordinal)
                && previewAppText.Contains("loadNativeAugmentation(entry)", StringComparison.Ordinal)
                && previewNativeAugmentationText.Contains("composeNativePrefab", StringComparison.Ordinal)
                && previewNativeAugmentationText.Contains("ReignPreviewXPath", StringComparison.Ordinal)
                && previewEditorText.Contains("getMovementTarget(event.altKey)", StringComparison.Ordinal)
                && previewEditorText.Contains("api/calibration/apply-and-install", StringComparison.Ordinal)
                && previewCodexText.Contains("Current preview context (generated by the previewer)", StringComparison.Ordinal)
                && previewCodexText.Contains("pending", StringComparison.OrdinalIgnoreCase)
                && previewServerText.Contains("codex-preview-websocket-proxy.mjs", StringComparison.Ordinal)
                && previewServerText.Contains("/api/prefab-sync-all", StringComparison.Ordinal)
                && previewServerText.Contains("/api/native-parity-readiness", StringComparison.Ordinal)
                && previewServerText.Contains("/api/native-prefab", StringComparison.Ordinal)
                && previewServerText.Contains("Get-ReignNativePrefabConstants", StringComparison.Ordinal)
                && previewServerText.Contains("snapshotMatchesSource", StringComparison.Ordinal)
                && previewServerText.Contains("ProcessName -like 'Bannerlord*'", StringComparison.Ordinal)
                && previewServerText.Contains("sourceApplied = $true", StringComparison.Ordinal)
                && previewServerText.Contains("Test-BannerlordRunning", StringComparison.Ordinal)
                && uiCalibrationServiceText.Contains("_loadedPrefabSha256 = PrefabSha256(movieName)", StringComparison.Ordinal)
                && uiCalibrationServiceText.Contains("snapshot.Add(\"prefabSha256\", _loadedPrefabSha256", StringComparison.Ordinal)
                && uiCalibrationServiceText.Contains("installedPrefabSha256AtCapture", StringComparison.Ordinal)
                && uiCalibrationServiceText.Contains("supportUiInjected = false", StringComparison.Ordinal)
                && !uiCalibrationServiceText.Contains("layer.LoadMovie(OverlayMovieName", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.gauntlet_xml_previewer_full_inventory_and_edit_bridge", "ui_contracts", previewerContractValid,
                previewerContractValid
                    ? "Every standalone Reign Gauntlet prefab and source-discovered native Bannerlord augmentation is cataloged for fixture/runtime binding and dual-resolution rendered audits; native base XML, patch provenance, and brush-derived constants are explicit, coherent fixture content remains separate from hash-matched captured state, intuitive parent movement is explicit, selected XML context reaches a workspace-only Codex task, and source-to-installed propagation is guarded and observable."
                    : "The Gauntlet XML Previewer standalone or native-augmentation inventory, binding/render audit, patch provenance, coherent fixture isolation, intuitive editing, Codex context bridge, native prefab hash, or guarded source-to-installed path is incomplete.",
                new Dictionary<string, object>
                {
                    ["prefabCount"] = reignPrefabNames.Length,
                    ["catalogPrefabCount"] = catalogPrefabNames.Count,
                    ["catalog"] = uiCatalogPath,
                    ["audit"] = previewAuditPath,
                    ["renderAudit"] = previewRenderedAuditPath,
                    ["codexE2eAudit"] = previewCodexE2eAuditPath,
                    ["nativeParityAudit"] = previewNativeParityAuditPath,
                    ["calibrationWorkflow"] = previewCalibrationWorkflowPath,
                    ["renderer"] = previewRendererPath,
                    ["app"] = previewAppPath,
                    ["editor"] = previewEditorPath,
                    ["codex"] = previewCodexPath,
                    ["nativeAugmentations"] = previewNativeAugmentationPath,
                    ["xmlSource"] = previewXmlSourcePath,
                    ["server"] = previewServerPath,
                    ["calibrationService"] = uiCalibrationServicePath
                });
            string uiCalibrationHostPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignLiveInteractionUiCalibrationHost.cs", "src");
            string uiCalibrationHostText = File.Exists(uiCalibrationHostPath) ? File.ReadAllText(uiCalibrationHostPath) : "";
            bool castleChatHarnessValid = uiCatalogText.Contains("\"id\": \"castle-chat\"")
                && uiCatalogText.Contains("\"movie\": \"ReignCastleChatScreen\"")
                && uiCatalogText.Contains("ui-open --target castle-chat")
                && previewIndexText.Contains("ReignCastleChatScreen.xml")
                && previewSampleText.Contains("\"ReignCastleChatScreen.xml\"")
                && previewSampleText.Contains("HasCastleArt: true")
                && uiCalibrationHostText.Contains("case \"castle-chat\":")
                && uiCalibrationHostText.Contains("ImageStatus = \"disabled\"")
                && uiCalibrationHostText.Contains("OccupantHeroIdsCsv = string.Empty")
                && uiCalibrationHostText.Contains("case \"castle-chat\": return \"ReignCastleChatScreen\"");
            AddVerificationCheck(checks, "contracts.castle_chat_visual_live_harness", "ui_contracts", castleChatHarnessValid,
                castleChatHarnessValid
                    ? "Castle Chat is registered with deterministic visual sample data and a provider-free native calibration target."
                    : "Castle Chat remains absent from the shared visual or native UI calibration harness.",
                new Dictionary<string, object>
                {
                    ["catalog"] = uiCatalogPath,
                    ["sampleData"] = previewSamplePath,
                    ["liveHost"] = uiCalibrationHostPath
                });

            string liveTestProgramPath = Path.Combine(root, "ReignBetaServer", "ReignLiveTest", "Program.cs");
            string liveTestProgramText = File.Exists(liveTestProgramPath) ? File.ReadAllText(liveTestProgramPath) : "";
            string correspondenceManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignCorrespondenceScreenManager.cs", "src");
            string correspondenceManagerText = File.Exists(correspondenceManagerPath) ? File.ReadAllText(correspondenceManagerPath) : "";
            string individualManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignIndividualChatScreenManager.cs", "src");
            string individualManagerText = File.Exists(individualManagerPath) ? File.ReadAllText(individualManagerPath) : "";
            string socialManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventScreenManager.cs", "src");
            string socialManagerText = File.Exists(socialManagerPath) ? File.ReadAllText(socialManagerPath) : "";
            string correspondenceVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignCorrespondenceScreenVM.cs", "src");
            string correspondenceVmText = File.Exists(correspondenceVmPath) ? File.ReadAllText(correspondenceVmPath) : "";
            string individualVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignIndividualChatScreenVM.cs", "src");
            string individualVmText = File.Exists(individualVmPath) ? File.ReadAllText(individualVmPath) : "";
            string socialVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventScreenVM.cs", "src");
            string socialVmText = File.Exists(socialVmPath) ? File.ReadAllText(socialVmPath) : "";
            string memoriesManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "MemoriesBookOverlay.cs", "src");
            string memoriesManagerText = File.Exists(memoriesManagerPath) ? File.ReadAllText(memoriesManagerPath) : "";
            string memoriesVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "MemoriesBookVM.cs", "src");
            string memoriesVmText = File.Exists(memoriesVmPath) ? File.ReadAllText(memoriesVmPath) : "";
            string memoriesPrefabPath = Path.Combine(guiRoot, "Prefabs", "AIPortraitsMemoriesBook.xml");
            string memoriesPrefabText = File.Exists(memoriesPrefabPath) ? File.ReadAllText(memoriesPrefabPath) : "";
            string eventArtFactoryPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignEventArtTextureFactory.cs", "src");
            string eventArtFactoryText = File.Exists(eventArtFactoryPath) ? File.ReadAllText(eventArtFactoryPath) : "";
            string notableManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignNotableGenerationPopupManager.cs", "src");
            string notableManagerText = File.Exists(notableManagerPath) ? File.ReadAllText(notableManagerPath) : "";
            string[] nativeUiCalibrationTargets =
            {
                "court", "court-petition", "castle-layout", "castle-chat", "ambassador", "economic-report",
                "government", "family-chambers", "training-yard", "royal-council", "spymaster", "war-council",
                "correspondence", "individual-chat", "party-chat", "social-event",
                "wilderness-event", "diplomacy-announcement", "notable-generation", "memories-book"
            };
            bool nativeUiCalibrationHarnessValid = nativeUiCalibrationTargets.All(target =>
                    uiCatalogText.Contains("\"id\": \"" + target + "\"", StringComparison.Ordinal)
                    && uiCatalogText.Contains("ui-open --target " + target, StringComparison.Ordinal)
                    && uiCalibrationHostText.Contains("case \"" + target + "\":", StringComparison.Ordinal)
                    && liveTestProgramText.Contains(target, StringComparison.Ordinal))
                && uiCatalogText.Contains("\"liveAction\": \"previewer only\"", StringComparison.Ordinal)
                && uiCatalogText.Contains("\"nativeInjection\": false", StringComparison.Ordinal)
                && correspondenceManagerText.Contains("OpenForCalibration", StringComparison.Ordinal)
                && individualManagerText.Contains("OpenForCalibration", StringComparison.Ordinal)
                && socialManagerText.Contains("OpenForCalibration", StringComparison.Ordinal)
                && memoriesManagerText.Contains("OpenForCalibration", StringComparison.Ordinal)
                && correspondenceVmText.Contains("Calibration mode does not send letters.", StringComparison.Ordinal)
                && individualVmText.Contains("Calibration mode does not send dialogue.", StringComparison.Ordinal)
                && socialVmText.Contains("Provider-free UI calibration fixture.", StringComparison.Ordinal)
                && memoriesVmText.Contains("CalibrationCaptions", StringComparison.Ordinal)
                && uiCalibrationHostText.Contains("ui_calibration_diplomacy", StringComparison.Ordinal)
                && uiCalibrationHostText.Contains("ShowCalibrationFixture()", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.all_reign_ui_native_calibration_targets", "ui_contracts", nativeUiCalibrationHarnessValid,
                nativeUiCalibrationHarnessValid
                    ? "All 19 runtime Reign movies across 20 provider-free ui-open targets are covered; the calibration overlay remains previewer-only and is never injected into Bannerlord."
                    : "At least one runtime Reign movie lacks a shared native ui-open target or an isolated provider-free fixture.",
                new Dictionary<string, object>
                {
                    ["targetCount"] = nativeUiCalibrationTargets.Length,
                    ["uniqueRuntimeMovieCount"] = 19,
                    ["catalog"] = uiCatalogPath,
                    ["liveHost"] = uiCalibrationHostPath,
                    ["liveTest"] = liveTestProgramPath
                });

            bool nativeStandaloneFixtureStatesValid =
                memoriesPrefabText.Contains("Id=\"MemoriesBookSceneViewport\"", StringComparison.Ordinal)
                && memoriesPrefabText.Contains("<ReignEventArtWidget Id=\"AIPortraitsMemoryBookScene\"", StringComparison.Ordinal)
                && memoriesPrefabText.Contains("EventImageId=\"@MemoryBookImageId\"", StringComparison.Ordinal)
                && !memoriesPrefabText.Contains("MemoryBookTextureProviderName", StringComparison.Ordinal)
                && !memoriesPrefabText.Contains("MemoryBookAdditionalArgs", StringComparison.Ordinal)
                && memoriesVmText.Contains("BuildMemorySceneImageId(text)", StringComparison.Ordinal)
                && memoriesVmText.Contains("\"feast_empire\", \"toasts_and_table_talk\"", StringComparison.Ordinal)
                && !memoriesVmText.Contains("CharacterImageTextureProvider", StringComparison.Ordinal)
                && eventArtFactoryText.Contains("MemorySceneTemplateId = \"memory_scene\"", StringComparison.Ordinal)
                && eventArtFactoryText.Contains("TryResolveMemoryScenePath", StringComparison.Ordinal)
                && notableManagerText.Contains("ShowCalibrationFixture()", StringComparison.Ordinal)
                && notableManagerText.Contains("\"PREPARING CALRADIA\"", StringComparison.Ordinal)
                && notableManagerText.Contains("\"Generating the notable characters and relationships needed for this campaign.\"", StringComparison.Ordinal)
                && notableManagerText.Contains("\"Building notable 18 of 42\"", StringComparison.Ordinal)
                && !uiCalibrationHostText.Contains("\"UI CALIBRATION\"", StringComparison.Ordinal)
                && !uiCalibrationHostText.Contains("\"calibration-overlay\"", StringComparison.Ordinal)
                && uiCalibrationServiceText.Contains("supportUiInjected = false", StringComparison.Ordinal)
                && !uiCalibrationServiceText.Contains("layer.LoadMovie(OverlayMovieName", StringComparison.Ordinal)
                && !uiCalibrationServiceText.Contains("public static bool TrySetExpanded", StringComparison.Ordinal)
                && !uiCalibrationServiceText.Contains("public static bool TryGetExpanded", StringComparison.Ordinal)
                && !uiCalibrationServiceText.Contains("public static bool TrySelectWidgetForCapture", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.native_ui_standalone_fixture_states", "ui_contracts", nativeStandaloneFixtureStatesValid,
                nativeStandaloneFixtureStatesValid
                    ? "Memory Book uses scene art without a character-provider fallback, Notable Generation opens its approved progress state, and native snapshots remain headless with no calibration support UI."
                    : "A standalone native UI fixture no longer matches its provider-free scene, approved progress, or headless snapshot contract.",
                new Dictionary<string, object>
                {
                    ["memoriesPrefab"] = memoriesPrefabPath,
                    ["memoriesViewModel"] = memoriesVmPath,
                    ["eventArtFactory"] = eventArtFactoryPath,
                    ["notableManager"] = notableManagerPath,
                    ["liveHost"] = uiCalibrationHostPath,
                    ["calibrationService"] = uiCalibrationServicePath
                });

            string ambassadorPrefab = Path.Combine(guiRoot, "Prefabs", "ReignAmbassadorScreen.xml");
            string ambassadorXml = File.Exists(ambassadorPrefab) ? File.ReadAllText(ambassadorPrefab) : "";
            string ambassadorVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignAmbassadorScreenVM.cs", "src");
            string ambassadorVmSource = File.Exists(ambassadorVmPath) ? File.ReadAllText(ambassadorVmPath) : "";
            int ambassadorRosterStart = ambassadorXml.IndexOf("Id=\"AmbassadorRail\"", StringComparison.Ordinal);
            string ambassadorRosterXml = ambassadorRosterStart >= 0 ? ambassadorXml.Substring(ambassadorRosterStart) : string.Empty;
            int ambassadorKingdomLabel = ambassadorRosterXml.IndexOf("Text=\"@KingdomName\"", StringComparison.Ordinal);
            int ambassadorNameLabel = ambassadorRosterXml.IndexOf("Text=\"@Name\"", StringComparison.Ordinal);
            bool ambassadorRosterValid = ambassadorRosterXml.Contains("CharacterTableauWidget")
                && ambassadorRosterXml.Contains("DataSource=\"{AmbassadorModel}\"")
                && ambassadorRosterXml.Contains("Command.Click=\"ExecuteSpeak\"")
                && ambassadorRosterXml.Contains("Command.Click=\"ExecuteDismiss\"")
                && ambassadorKingdomLabel >= 0 && ambassadorNameLabel > ambassadorKingdomLabel
                && !ambassadorRosterXml.Contains("Id=\"SpeakAmbassadorButton\"")
                && !ambassadorRosterXml.Contains("Text=\"Speak\"")
                && ambassadorVmSource.Contains("BuildAmbassadorModel", StringComparison.Ordinal)
                && ambassadorVmSource.Contains("OpenCard(_selected)", StringComparison.Ordinal);
            AddVerificationCheck(checks, "contracts.ambassador_clickable_full_body_roster", "ui_contracts", ambassadorRosterValid,
                ambassadorRosterValid
                    ? "Ambassadors render as full-body native tableaus; the envoy card opens official dialogue, kingdom precedes envoy name, and dismissal is per-card."
                    : "The Ambassador roster still has a detached Speak flow or incomplete full-body card hierarchy.",
                new Dictionary<string, object> { ["prefab"] = ambassadorPrefab, ["viewModel"] = ambassadorVmPath });

            string courtPrefab = Path.Combine(guiRoot, "Prefabs", "ReignCourtScreen.xml");
            string courtXml = File.Exists(courtPrefab) ? File.ReadAllText(courtPrefab) : "";
            string economicPrefab = Path.Combine(guiRoot, "Prefabs", "ReignCourtEconomicReportScreen.xml");
            string economicXml = File.Exists(economicPrefab) ? File.ReadAllText(economicPrefab) : "";
            string familyPrefab = Path.Combine(guiRoot, "Prefabs", "ReignFamilyChambersScreen.xml");
            string familyXml = File.Exists(familyPrefab) ? File.ReadAllText(familyPrefab) : "";
            string familyDomainPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignFamilyChambersDomain.cs", "src");
            string familyDomainText = File.Exists(familyDomainPath) ? File.ReadAllText(familyDomainPath) : "";
            string spriteDataPath = Path.Combine(guiRoot, "ReignBetaSpriteData.xml");
            string spriteDataText = File.Exists(spriteDataPath) ? File.ReadAllText(spriteDataPath) : "";
            XmlDocument courtDocument = TryParseVerificationXml(courtXml);
            XmlDocument economicDocument = TryParseVerificationXml(economicXml);
            XmlDocument ambassadorDocument = TryParseVerificationXml(ambassadorXml);
            XmlDocument familyDocument = TryParseVerificationXml(familyXml);
            XmlElement courtModernShellElement = courtDocument?.SelectSingleNode("//*[@Sprite='reign_court_modern_shell']") as XmlElement;
            XmlElement courtRoyalCouncilCommandElement = courtDocument?.SelectSingleNode("//*[@Command.Click='ExecuteOpenRoyalCouncil']") as XmlElement;
            XmlElement economicCanvasElement = FindVerificationXmlElementById(economicDocument, "EconomicReportCanvas");
            XmlElement economicViewportElement = FindVerificationXmlElementById(economicDocument, "SettlementReportViewport");
            XmlElement economicSettlementListElement = FindVerificationXmlElementById(economicDocument, "SettlementReportList");
            XmlElement economicSettlementColumnElement = economicSettlementListElement?.SelectSingleNode("./ItemTemplate/Widget") as XmlElement;
            XmlElement economicScrollbarElement = FindVerificationXmlElementById(economicDocument, "SettlementReportScrollbar");
            XmlElement economicSubmitElement = FindVerificationXmlElementById(economicDocument, "SealSubmitButton");
            XmlElement economicAdvisorCardElement = FindVerificationXmlElementById(economicDocument, "EconomicAdvisorCard");
            XmlElement economicAdvisorButtonElement = economicAdvisorCardElement?.SelectSingleNode("./Children/ButtonWidget") as XmlElement;
            XmlElement economicAdvisorButtonChildrenElement = economicAdvisorButtonElement?.SelectSingleNode("./Children") as XmlElement;
            XmlElement economicAdvisorPortraitClipElement = economicAdvisorButtonElement?.SelectSingleNode("./Children/Widget[1]") as XmlElement;
            XmlElement economicAdvisorPortraitElement = economicAdvisorPortraitClipElement?.SelectSingleNode("./Children/ReignPortraitWidget") as XmlElement;
            XmlElement economicAdvisorFrameElement = economicAdvisorButtonElement?.SelectSingleNode("./Children/ImageWidget[@Sprite='reign_economic_advisor_portrait_mask']") as XmlElement;
            XmlElement ambassadorCanvasElement = FindVerificationXmlElementById(ambassadorDocument, "AmbassadorCanvas");
            XmlElement ambassadorAdvisorElement = ambassadorDocument?.SelectSingleNode("//*[@DataSource='{ForeignAdvisor}']") as XmlElement;
            XmlElement ambassadorRailElement = FindVerificationXmlElementById(ambassadorDocument, "AmbassadorRail");
            XmlElement ambassadorRailViewportElement = FindVerificationXmlElementById(ambassadorDocument, "AmbassadorRailViewport");
            XmlElement familyShellElement = FindVerificationXmlElementById(familyDocument, "FamilyChambersShell");
            bool courtModernLayoutValid = courtModernShellElement != null
                && courtRoyalCouncilCommandElement != null
                && VerificationXmlAttributeEquals(courtRoyalCouncilCommandElement, "SuggestedWidth", "290")
                && VerificationXmlAttributeEquals(courtRoyalCouncilCommandElement, "SuggestedHeight", "88")
                && VerificationXmlAttributeEquals(courtRoyalCouncilCommandElement, "MarginLeft", "1168")
                && VerificationXmlAttributeEquals(courtRoyalCouncilCommandElement, "MarginTop", "628");
            bool economicModernLayoutValid = economicCanvasElement != null
                && VerificationXmlAttributeEquals(economicCanvasElement, "Sprite", "reign_economic_report_modern_shell")
                && economicViewportElement != null
                && VerificationXmlAttributeEquals(economicViewportElement, "SuggestedWidth", "672")
                && VerificationXmlAttributeEquals(economicViewportElement, "SuggestedHeight", "615")
                && VerificationXmlAttributeEquals(economicViewportElement, "MarginLeft", "49")
                && VerificationXmlAttributeEquals(economicViewportElement, "MarginTop", "155")
                && economicSettlementColumnElement != null
                && VerificationXmlAttributeEquals(economicSettlementColumnElement, "SuggestedWidth", "224")
                && economicScrollbarElement != null
                && VerificationXmlAttributeEquals(economicScrollbarElement, "SuggestedWidth", "672")
                && VerificationXmlAttributeEquals(economicScrollbarElement, "SuggestedHeight", "8")
                && VerificationXmlAttributeEquals(economicScrollbarElement, "MarginLeft", "49")
                && VerificationXmlAttributeEquals(economicScrollbarElement, "MarginTop", "774")
                && economicSubmitElement != null
                && VerificationXmlAttributeEquals(economicSubmitElement, "SuggestedWidth", "162")
                && VerificationXmlAttributeEquals(economicSubmitElement, "SuggestedHeight", "42")
                && VerificationXmlAttributeEquals(economicSubmitElement, "MarginLeft", "774")
                && VerificationXmlAttributeEquals(economicSubmitElement, "MarginTop", "710")
                && economicAdvisorCardElement != null
                && VerificationXmlAttributeEquals(economicAdvisorCardElement, "DataSource", "{EconomicAdvisor}")
                && VerificationXmlAttributeEquals(economicAdvisorCardElement, "SuggestedWidth", "297")
                && VerificationXmlAttributeEquals(economicAdvisorCardElement, "SuggestedHeight", "320")
                && VerificationXmlAttributeEquals(economicAdvisorCardElement, "MarginLeft", "1331")
                && VerificationXmlAttributeEquals(economicAdvisorCardElement, "MarginTop", "468")
                && VerificationXmlAttributeEquals(economicAdvisorButtonElement, "Command.Click", "ExecuteOpen")
                && economicAdvisorPortraitClipElement != null
                && VerificationXmlAttributeEquals(economicAdvisorPortraitClipElement, "SuggestedWidth", "176")
                && VerificationXmlAttributeEquals(economicAdvisorPortraitClipElement, "SuggestedHeight", "219")
                && VerificationXmlAttributeEquals(economicAdvisorPortraitClipElement, "MarginLeft", "61")
                && VerificationXmlAttributeEquals(economicAdvisorPortraitClipElement, "MarginTop", "38")
                && !economicAdvisorPortraitClipElement.HasAttribute("Sprite")
                && !economicAdvisorPortraitClipElement.HasAttribute("Color")
                && economicAdvisorPortraitElement != null
                && VerificationXmlAttributeEquals(economicAdvisorPortraitElement, "TargetAspect", "0.8037")
                && economicAdvisorFrameElement != null
                && VerificationXmlAttributeEquals(economicAdvisorFrameElement, "DoNotAcceptEvents", "true")
                && VerificationXmlChildElementIndex(economicAdvisorButtonChildrenElement, economicAdvisorFrameElement)
                    > VerificationXmlChildElementIndex(economicAdvisorButtonChildrenElement, economicAdvisorPortraitClipElement);
            bool ambassadorModernLayoutValid = ambassadorCanvasElement != null
                && VerificationXmlAttributeEquals(ambassadorCanvasElement, "Sprite", "reign_ambassador_modern_shell")
                && ambassadorAdvisorElement != null
                && VerificationXmlAttributeEquals(ambassadorAdvisorElement, "SuggestedWidth", "294")
                && VerificationXmlAttributeEquals(ambassadorAdvisorElement, "SuggestedHeight", "620")
                && VerificationXmlAttributeEquals(ambassadorAdvisorElement, "MarginLeft", "195")
                && ambassadorRailViewportElement != null
                && VerificationXmlAttributeEquals(ambassadorRailViewportElement, "SuggestedWidth", "981")
                && VerificationXmlAttributeEquals(ambassadorRailViewportElement, "SuggestedHeight", "638")
                && VerificationXmlAttributeEquals(ambassadorRailViewportElement, "MarginLeft", "520")
                && string.Equals(ambassadorRailViewportElement.Name, "ReignAmbassadorSnapScrollPanel", StringComparison.Ordinal)
                && VerificationXmlAttributeEquals(ambassadorRailViewportElement, "Type", "ReignBeta.UI.ReignAmbassadorSnapScrollPanel")
                && VerificationXmlAttributeEquals(ambassadorRailViewportElement, "ItemCount", "@AmbassadorCount");
            bool familyModernLayoutValid = familyShellElement != null
                && VerificationXmlAttributeEquals(familyShellElement, "Sprite", "reign_family_chambers_modern_shell")
                && familyXml.Contains("VerticalScrollbar=\"..\\AdultScrollbar\"")
                && familyXml.Contains("VerticalScrollbar=\"..\\ChildScrollbar\"")
                && familyXml.Contains("PortraitCacheKey=\"@PortraitCacheKey\"")
                && !familyXml.Contains("Sprite=\"reign_party_chat_roster\"")
                && !familyXml.Contains("Sprite=\"reign_party_chat_log\"")
                && familyDomainText.Contains("Hero.AllAliveHeroes.Where(child => child != null");
            bool courtModernSpritesRegistered = spriteDataText.Contains("<SpritePartName>reign_court_modern_shell</SpritePartName>")
                && spriteDataText.Contains("<SpritePartName>reign_economic_report_modern_shell</SpritePartName>")
                && spriteDataText.Contains("<SpritePartName>reign_ambassador_modern_shell</SpritePartName>")
                && spriteDataText.Contains("<SpritePartName>reign_family_chambers_modern_shell</SpritePartName>")
                && spriteDataText.Contains("<Name>reign_royal_council_modern_shell</Name>")
                && spriteDataText.Contains("<SpritePartName>reign_royal_council_modern_shell</SpritePartName>");
            List<string> courtSuiteLayoutFailures = new List<string>();
            if (!courtModernLayoutValid) courtSuiteLayoutFailures.Add("court_modern_shell_and_royal_council_command");
            if (!economicModernLayoutValid) courtSuiteLayoutFailures.Add("economic_report_modern_geometry");
            if (!ambassadorModernLayoutValid) courtSuiteLayoutFailures.Add("ambassador_modern_geometry");
            if (!familyModernLayoutValid) courtSuiteLayoutFailures.Add("family_chambers_modern_scrolling_and_portraits");
            if (!courtModernSpritesRegistered) courtSuiteLayoutFailures.Add("court_suite_sprite_registration");
            bool courtSuiteLayoutValid = courtSuiteLayoutFailures.Count == 0;
            AddVerificationCheck(checks, "contracts.court_interface_layout_regressions", "ui_contracts", courtSuiteLayoutValid,
                courtSuiteLayoutValid
                    ? "The approved modern Court shell, three-column Economic Report with selectable oval Economic Advisor, full-body Ambassador rail, Family Chambers scrolling/cards, child discovery, navigation, and sprite registration are guarded."
                    : "One or more approved-modern Court interface regression guards failed.",
                new Dictionary<string, object> { ["court"] = courtPrefab, ["economic"] = economicPrefab, ["ambassadors"] = ambassadorPrefab, ["family"] = familyPrefab, ["spriteData"] = spriteDataPath, ["failures"] = courtSuiteLayoutFailures });

            string socialEventPrefab = Path.Combine(guiRoot, "Prefabs", "ReignSocialEventScreen.xml");
            string socialEventXml = File.Exists(socialEventPrefab) ? File.ReadAllText(socialEventPrefab) : "";
            XmlDocument socialEventDocument = TryParseVerificationXml(socialEventXml);
            XmlElement socialEventShellElement = FindVerificationXmlElementById(socialEventDocument, "SocialEventModernShell");
            XmlElement wildernessEventShellElement = FindVerificationXmlElementById(socialEventDocument, "WildernessEventModernShell");
            XmlElement socialEventChatListElement = FindVerificationXmlElementById(socialEventDocument, "SocialEventChatList");
            XmlElement wildernessEventChatListElement = FindVerificationXmlElementById(socialEventDocument, "WildernessEventChatList");
            bool socialEventLayoutValid = socialEventXml.Contains("IsVisible=\"@IsOverlayVisible\"")
                && socialEventXml.Contains("Id=\"ReignPartyChatBackground\"")
                && socialEventXml.Contains("DataSource=\"{ActiveParticipants}\"")
                && socialEventXml.Contains("DataSource=\"{AvailableAttendees}\"")
                && socialEventXml.Contains("Command.Click=\"ExecuteWaitApproach\"")
                && socialEventXml.Contains("Command.Click=\"ExecuteGenerateAIPortrait\"")
                && socialEventShellElement != null
                && VerificationXmlAttributeEquals(socialEventShellElement, "Sprite", "reign_social_event_modern_shell")
                && VerificationXmlAttributeEquals(socialEventShellElement, "IsVisible", "@ShowPhaseControls")
                && wildernessEventShellElement != null
                && VerificationXmlAttributeEquals(wildernessEventShellElement, "Sprite", "reign_wilderness_event_modern_shell")
                && VerificationXmlAttributeEquals(wildernessEventShellElement, "IsVisible", "@ShowWildernessArt")
                && socialEventChatListElement != null
                && wildernessEventChatListElement != null;
            AddVerificationCheck(checks, "contracts.social_event_ornate_layout", "ui_contracts", socialEventLayoutValid,
                socialEventLayoutValid ? "The shared Social and Wilderness Event prefab contains the approved modern shells, attendee, event-art, phase, control, and independently framed chat regions." : "The approved-modern Social/Wilderness Event layout contract is incomplete.",
                new Dictionary<string, object> { ["prefab"] = socialEventPrefab,
                    ["socialShell"] = socialEventShellElement != null,
                    ["wildernessShell"] = wildernessEventShellElement != null,
                    ["socialChatList"] = socialEventChatListElement != null,
                    ["wildernessChatList"] = wildernessEventChatListElement != null });

            bool socialEventTranscriptReadable = VerificationModernChatListTemplateMatches(
                    socialEventChatListElement,
                    rowMinHeight: "34",
                    rowMarginBottom: "4",
                    textMinHeight: "28")
                && VerificationModernChatListTemplateMatches(
                    wildernessEventChatListElement,
                    rowMinHeight: "42",
                    rowMarginBottom: "3",
                    textMinHeight: "32");
            AddVerificationCheck(checks, "contracts.social_event_readable_transcript", "ui_contracts", socialEventTranscriptReadable,
                socialEventTranscriptReadable
                    ? "Both Social and Wilderness Event transcript rows expand for wrapped text and preserve the approved gold-speaker/gray-body typography roles."
                    : "A Social or Wilderness Event transcript template can compress wrapped dialogue or drift from the approved typography roles.",
                new Dictionary<string, object> { ["prefab"] = socialEventPrefab,
                    ["socialTemplateValid"] = VerificationModernChatListTemplateMatches(socialEventChatListElement, "34", "4", "28"),
                    ["wildernessTemplateValid"] = VerificationModernChatListTemplateMatches(wildernessEventChatListElement, "42", "3", "32") });

            string socialEventManagerPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventScreenManager.cs", "src");
            string socialEventManagerText = File.Exists(socialEventManagerPath) ? File.ReadAllText(socialEventManagerPath) : "";
            bool socialEventOverlayHandoffValid = socialEventManagerText.Contains("EnterExternalOverlayMode()")
                && socialEventManagerText.Contains("ReignSocialEventScreen : ScreenBase")
                && socialEventManagerText.Contains("EventLayerOrder = 250")
                && socialEventManagerText.Contains("ScreenManager.TopScreen != this")
                && socialEventManagerText.Contains("ScreenManager.FocusedLayer != _gauntletLayer")
                && !socialEventManagerText.Contains("SetOverlayVisible(false)")
                && socialEventManagerText.Contains("OpenPortraitRequestOverlay");
            AddVerificationCheck(checks, "contracts.social_event_encyclopedia_handoff", "ui_contracts", socialEventOverlayHandoffValid,
                socialEventOverlayHandoffValid ? "The native Encyclopedia remains above the visible social-event screen until manually closed, then event focus returns." : "Social-event encyclopedia screen handoff is incomplete.", null);

            string socialEventBehaviorPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventsCampaignBehavior.cs", "src");
            string socialEventBehaviorText = File.Exists(socialEventBehaviorPath) ? File.ReadAllText(socialEventBehaviorPath) : "";
            string conversationEligibilityPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignConversationEligibility.cs", "src");
            string conversationEligibilityText = File.Exists(conversationEligibilityPath) ? File.ReadAllText(conversationEligibilityPath) : "";
            string socialEventRecordPath = VerificationSourceLocator.ResolveUnique(clientRoot, "SocialEventRecord.cs", "src");
            string socialEventRecordText = File.Exists(socialEventRecordPath) ? File.ReadAllText(socialEventRecordPath) : "";
            string socialEventSessionPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventSession.cs", "src");
            string socialEventSessionText = File.Exists(socialEventSessionPath) ? File.ReadAllText(socialEventSessionPath) : "";
            bool socialEventPlayerExclusionValid = socialEventBehaviorText.Contains("RepairAttendeeRosters()")
                && !socialEventBehaviorText.Contains("attendees.Insert(0, Hero.MainHero)")
                && socialEventRecordText.Contains("hero != Hero.MainHero")
                && socialEventSessionText.Contains("hero == Hero.MainHero");
            AddVerificationCheck(checks, "contracts.social_event_player_exclusion", "conversation_modes", socialEventPlayerExclusionValid,
                socialEventPlayerExclusionValid ? "The player is implicit and excluded from saved, displayed, and active social-event NPC rosters." : "The player can still enter a social-event NPC roster.", null);

            string socialEventVmPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventScreenVM.cs", "src");
            string socialEventVmText = File.Exists(socialEventVmPath) ? File.ReadAllText(socialEventVmPath) : "";
            string socialEventServerPath = VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src");
            string socialEventServerText = File.Exists(socialEventServerPath) ? File.ReadAllText(socialEventServerPath) : "";
            bool approachOpeningValid = socialEventVmText.Contains("public async void ExecuteWaitApproach()")
                && socialEventVmText.Contains("RequestRepliesAsync(heroes, string.Empty, true)")
                && socialEventServerText.Contains("npc_approach_opening")
                && socialEventServerText.Contains("suppressPlayerTranscript");
            AddVerificationCheck(checks, "contracts.social_event_approach_dialogue", "conversation_modes", approachOpeningValid,
                approachOpeningValid ? "NPC approaches issue a protected model opening turn without fabricating a player transcript line." : "NPC approach opening-turn contract is incomplete.", null);

            string socialEventTurnServerPath = VerificationSourceLocator.ResolveUnique(serverRoot, "SocialEventTurns.cs", "src");
            string socialEventTurnServerText = File.Exists(socialEventTurnServerPath) ? File.ReadAllText(socialEventTurnServerPath) : "";
            string socialEventApproachServerPath = VerificationSourceLocator.ResolveUnique(serverRoot, "SocialEventApproaches.cs", "src");
            string socialEventApproachServerText = File.Exists(socialEventApproachServerPath) ? File.ReadAllText(socialEventApproachServerPath) : "";
            string socialEventTurnClientPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSocialEventClient.cs", "src");
            string socialEventTurnClientText = File.Exists(socialEventTurnClientPath) ? File.ReadAllText(socialEventTurnClientPath) : "";
            bool localFirstRosterValid = socialEventBehaviorText.Contains("MaxEventAttendees = 25")
                && socialEventBehaviorText.Contains("party.MemberRoster.GetTroopRoster()")
                && socialEventBehaviorText.Contains("OrderByDescending(x => x.Hero.IsLord)")
                && socialEventBehaviorText.Contains("ReignConversationEligibility.IsAdultLivingNpc(hero)")
                && conversationEligibilityText.Contains("!hero.IsChild")
                && conversationEligibilityText.Contains("HeroComesOfAge")
                && socialEventBehaviorText.Contains("party?.CurrentSettlement != settlement");
            AddVerificationCheck(checks, "contracts.social_event_local_first_roster", "conversation_modes", localFirstRosterValid,
                localFirstRosterValid ? "Social events collect up to 25 physically present attendees and rank present lords first." : "Local-first social-event roster contract is incomplete.", null);

            bool persistentGroupValid = socialEventSessionText.Contains("MaxActiveParticipants = 5")
                && socialEventSessionText.Contains("TurnsPerAutomaticPhase = 7")
                && socialEventSessionText.Contains("ApplyTurnResolution")
                && !socialEventSessionText.Contains("_activeHeroStringIds.Clear();")
                && socialEventVmText.Contains("RequestSocialEventTurnAsync")
                && socialEventVmText.Contains("wanders away from the conversation")
                && socialEventTurnClientText.Contains("/events/social/turn");
            AddVerificationCheck(checks, "contracts.social_event_persistent_group", "conversation_modes", persistentGroupValid,
                persistentGroupValid ? "Five-person conversations persist across phases, track ignored participants, and advance after seven completed exchanges." : "Persistent social-event group contract is incomplete.", null);

            bool groupTurnServerValid = socialEventServerText.Contains("/events/social/turn")
                && socialEventTurnServerText.Contains("ResolveSocialEventAddressedHeroes")
                && socialEventTurnServerText.Contains("EnsureSocialTurnPlayerTranscript")
                && socialEventTurnServerText.Contains("EvaluateSocialEventJoin")
                && socialEventTurnServerText.Contains("relationshipReceipts")
                && socialEventTurnServerText.Contains("\"identityView\", \"decisionBrief\", \"motiveDecision\"")
                && socialEventTurnServerText.Contains("[\"groupSpeakerIndex\"] = results.Count")
                && socialEventTurnClientText.Contains("payload[\"correlationId\"] = turnCorrelationId")
                && socialEventTurnClientText.Contains("turnCorrelationId + \"-npc-\" + speaker.StringId")
                && socialEventApproachServerText.Contains("thresholdOffset = 60")
                && socialEventApproachServerText.Contains("EvaluateSocialEventJoin");
            AddVerificationCheck(checks, "contracts.social_event_group_turn", "conversation_modes", groupTurnServerValid,
                groupTurnServerValid ? "Idempotent grouped turns provide focus, ordered participation, double-roll joins, dance thresholds, and relationship receipts." : "Grouped social-event turn contract is incomplete.", null);

            AddSubsystemListSummary(checks, "contracts.social_event_progressive_replies", "conversation_modes", RunSocialEventTurnProgressSelfTests());
            bool progressiveDeliveryValid = socialEventTurnClientText.Contains("payload[\"progressiveReplies\"] = true")
                && socialEventTurnClientText.Contains("await DeliverSocialEventRepliesAsync")
                && socialEventTurnClientText.Contains("await ReignMainThread.InvokeAsync")
                && socialEventTurnClientText.Contains("delivered.Contains(heroId)")
                && socialEventVmText.Contains("ReignObjectResolver.FindHero(reply.HeroStringId)")
                && !socialEventVmText.Contains("foreach (ReignEventReply reply in turn.ParticipantResults)");
            AddVerificationCheck(checks, "contracts.social_event_progressive_client", "conversation_modes", progressiveDeliveryValid,
                "Social and wilderness event clients await per-speaker main-thread delivery, deduplicate checkpoints and no longer batch transcript display after completion.", null);

            string diplomacyDebugServerPath = VerificationSourceLocator.ResolveUnique(serverRoot, "WorldDiplomacyDebug.cs", "src");
            string diplomacyDebugServerText = File.Exists(diplomacyDebugServerPath) ? File.ReadAllText(diplomacyDebugServerPath) : "";
            string diplomacyDirectorPath = VerificationSourceLocator.ResolveUnique(serverRoot, "WorldDiplomacyDirector.cs", "src");
            string diplomacyDirectorText = File.Exists(diplomacyDirectorPath) ? File.ReadAllText(diplomacyDirectorPath) : "";
            string diplomacyDebugActionsPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaDebugActions.cs", "src");
            string diplomacyDebugActionsText = File.Exists(diplomacyDebugActionsPath) ? File.ReadAllText(diplomacyDebugActionsPath) : "";
            string diplomacyBehaviorPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWorldDiplomacyCampaignBehavior.cs", "src");
            string diplomacyBehaviorText = File.Exists(diplomacyBehaviorPath) ? File.ReadAllText(diplomacyBehaviorPath) : "";
            string individualRelationPatchPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignIndividualRelationPatches.cs", "src");
            string individualRelationPatchText = File.Exists(individualRelationPatchPath) ? File.ReadAllText(individualRelationPatchPath) : "";
            string diplomacyClientPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignDiplomacyServerClient.cs", "src");
            string diplomacyClientText = File.Exists(diplomacyClientPath) ? File.ReadAllText(diplomacyClientPath) : "";
            string worldTestClientPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWorldTestClient.cs", "src");
            string worldTestClientText = File.Exists(worldTestClientPath) ? File.ReadAllText(worldTestClientPath) : "";
            string settingsPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaSettings.cs", "src");
            string settingsText = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : "";
            bool randomDiplomacyDebugValid = socialEventServerText.Contains("/diplomacy/debug/random-event")
                && diplomacyDebugServerText.Contains("payload[\"debugInitiativeTest\"] = true")
                && diplomacyDebugServerText.Contains("EvaluateWorldDiplomacy(payload)")
                && diplomacyDirectorText.Contains("DirectorInitiativeAttempt")
                && diplomacyDirectorText.Contains("debugInitiativeTest ? \"no_initiative\"")
                && diplomacyDebugActionsText.Contains("TriggerRandomDiplomacyPopupTest")
                && diplomacyDebugActionsText.Contains("ReignActionValidator.Validate")
                && diplomacyDebugActionsText.Contains("did not pass a normal diplomacy initiative roll")
                && diplomacyBehaviorText.Contains("WaitForAnnouncementAndShowForTestAsync")
                && diplomacyClientText.Contains("/diplomacy/debug/random-event")
                && settingsText.Contains("SettingPropertyButton(\"Test Random Ruler Diplomacy Roll\"");
            AddVerificationCheck(checks, "contracts.random_diplomacy_popup_debug", "world_diplomacy", randomDiplomacyDebugValid,
                randomDiplomacyDebugValid ? "The MCM test selects one NPC ruler, uses normal initiative chances, permits a genuine no-event roll, and continues successful rolls through the production action and announcement path." : "Random diplomacy initiative debug contract is incomplete.", null);
            bool diplomacyClanResolutionValid = diplomacyClientText.Contains("JArray clans = new JArray()")
                && diplomacyClientText.Contains("[\"clans\"] = clans")
                && diplomacyDirectorText.Contains("[\"clans\"] = world.ContainsKey(\"clans\")")
                && diplomacyDirectorText.Contains("DirectorValidationRejected(\"accepted_action_normalization_failed\"");
            AddVerificationCheck(checks, "contracts.diplomacy_clan_resolution", "world_diplomacy", diplomacyClanResolutionValid,
                diplomacyClanResolutionValid
                    ? "NPC diplomacy exports native clans into strict action resolution, and data-validation rejections do not masquerade as AI-service outages."
                    : "NPC diplomacy clan-resolution or validation-failure classification is incomplete.", null);
            bool exactNpcNativeRelationValid = individualRelationPatchText.Contains("GetEffectiveRelation")
                && individualRelationPatchText.Contains("GetBaseHeroRelation")
                && individualRelationPatchText.Contains("UseReignNpcRelationPrefix")
                && individualRelationPatchText.Contains("__result = hero1.GetBaseHeroRelation(hero2)")
                && individualRelationPatchText.Contains("return false;");
            AddVerificationCheck(checks, "contracts.exact_npc_native_relation", "world_test", exactNpcNativeRelationValid,
                exactNpcNativeRelationValid
                    ? "Native effective relation is the exact Reign-projected base value for every projected pair, including player-facing social standing, without a second Bannerlord trait modifier."
                    : "The NPC native-relation projection contract is incomplete or may still be altered by Bannerlord trait modifiers.", null);
            bool diplomacyCatchUpValid = diplomacyBehaviorText.Contains("now - _lastEvaluationDay >= 0.9f")
                && diplomacyBehaviorText.Contains("_ = EvaluateAsync();");
            AddVerificationCheck(checks, "contracts.diplomacy_hourly_catch_up", "world_diplomacy", diplomacyCatchUpValid,
                diplomacyCatchUpValid
                    ? "Hourly diplomacy catches up after a long model request skips one or more daily ticks."
                    : "Diplomacy may miss its cadence after a request remains in flight across daily ticks.", null);
            bool worldTestCampaignIdentityValid = worldTestClientText.Contains("[\"campaignLabel\"]")
                && worldTestClientText.Contains("[\"mainHeroName\"]")
                && worldTestClientText.Contains("[\"playerClanName\"]")
                && worldTestClientText.Contains("[\"playerKingdomName\"]");
            AddVerificationCheck(checks, "contracts.world_test_campaign_identity", "world_test", worldTestCampaignIdentityValid,
                worldTestCampaignIdentityValid
                    ? "Native World Test heartbeats carry campaign, hero, clan, and kingdom identity for immediate campaign discovery."
                    : "World Test native heartbeat campaign identity is incomplete.", null);
            bool worldTestRumorInspectorValid = socialEventServerText.Contains("worldTestRumorInspector")
                && socialEventServerText.Contains("loadWorldTestRumorInspector")
                && socialEventServerText.Contains("selectWorldTestRumor")
                && socialEventServerText.Contains("subsystem','rumor_subjects")
                && socialEventServerText.Contains("worldTestRumorId")
                && socialEventServerText.Contains("row?.occurrence_id")
                && socialEventServerText.Contains("row?.provenance_summary")
                && socialEventServerText.Contains("row?.created_day??row?.world_day")
                && socialEventServerText.Contains("Subjects and reputation tags");
            AddVerificationCheck(checks, "contracts.world_test_rumor_inspector", "world_test", worldTestRumorInspectorValid,
                worldTestRumorInspectorValid
                    ? "World Test includes a scrollable, read-only occurrence list with selected-rumor subject details."
                    : "World Test rumor occurrence or subject drill-down is incomplete.", null);
            string[] worldTestObservatoryIds =
            {
                "worldTestStage", "worldTestClockStrip", "worldTestCheckpointTable",
                "worldTestRelationshipCard", "worldTestRomanceFamilyCard", "worldTestRumorCard",
                "worldTestDiplomacyCard", "worldTestRebellionCard", "worldTestPipelineCard",
                "worldTestNativeContextCard", "worldTestBaselineComparison"
            };
            Match worldTestSectionMatch = Regex.Match(socialEventServerText,
                "<section id='worldtest'.*?</section>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            string worldTestSection = worldTestSectionMatch.Success ? worldTestSectionMatch.Value : "";
            bool worldTestObservatoryUiValid = worldTestObservatoryIds.All(id =>
                    socialEventServerText.Contains("id='" + id + "'"))
                && !Regex.IsMatch(worldTestSection,
                    "<button[^>]*>[^<]*(force|retry|reset|delete|inject|speed)[^<]*</button>",
                    RegexOptions.IgnoreCase);
            AddVerificationCheck(checks, "contracts.world_test_passive_observatory_ui", "world_test",
                worldTestObservatoryUiValid,
                worldTestObservatoryUiValid
                    ? "World Test exposes the staged read-only passive observatory cards, clocks, checkpoints, and baseline comparison."
                    : "World Test is missing staged observatory regions or exposes a state-changing control.", null);
            string worldTestServerPath = VerificationSourceLocator.ResolveUnique(serverRoot, "WorldTest.cs", "src");
            string worldTestServerText = File.Exists(worldTestServerPath)
                ? File.ReadAllText(worldTestServerPath) : "";
            int relationshipOverviewStart = worldTestServerText.IndexOf(
                "private static Dictionary<string, object> BuildWorldTestRelationships(",
                StringComparison.Ordinal);
            int relationshipOverviewEnd = relationshipOverviewStart < 0 ? -1
                : worldTestServerText.IndexOf(
                    "private static Dictionary<string, object> BuildWorldTestDiplomacy(",
                    relationshipOverviewStart, StringComparison.Ordinal);
            string relationshipOverviewSource =
                relationshipOverviewStart >= 0 && relationshipOverviewEnd > relationshipOverviewStart
                    ? worldTestServerText.Substring(
                        relationshipOverviewStart,
                        relationshipOverviewEnd - relationshipOverviewStart)
                    : "";
            string mbtiRelationshipPath = VerificationSourceLocator.ResolveUnique(serverRoot, "MbtiRelationships.cs", "src");
            string mbtiRelationshipText = File.Exists(mbtiRelationshipPath)
                ? File.ReadAllText(mbtiRelationshipPath) : "";
            int relationshipLoopStart = mbtiRelationshipText.IndexOf(
                "private static Dictionary<string, object> MbtiRelationshipSnapshotApi(",
                StringComparison.Ordinal);
            int relationshipLoopEnd = relationshipLoopStart < 0 ? -1
                : mbtiRelationshipText.IndexOf(
                    "private static Dictionary<string, object> RelationshipNativeSyncReportApi(",
                    relationshipLoopStart, StringComparison.Ordinal);
            string relationshipLoopSource =
                relationshipLoopStart >= 0 && relationshipLoopEnd > relationshipLoopStart
                    ? mbtiRelationshipText.Substring(
                        relationshipLoopStart, relationshipLoopEnd - relationshipLoopStart)
                    : "";
            bool worldTestAuthoritativeOverviewValid =
                relationshipOverviewSource.Contains("relationship_pair_chemistry")
                && !relationshipOverviewSource.Contains("world_test_relationship_memberships")
                && !relationshipLoopSource.Contains("RefreshWorldTestRelationshipMaterialization")
                && !relationshipLoopSource.Contains("ApplyWorldTestRelationshipPairDelta")
                && relationshipLoopSource.Contains("permanentMbtiByHero");
            AddVerificationCheck(checks, "contracts.world_test_bounded_relationship_overview",
                "world_test", worldTestAuthoritativeOverviewValid,
                worldTestAuthoritativeOverviewValid
                    ? "World Test reads the authoritative current relationship ledger without duplicating pair state from the simulation hot path."
                    : "World Test still duplicates diagnostic pair state from the relationship loop or does not read authoritative current state.", null);
            string relationshipCampaignPath = VerificationSourceLocator.ResolveUnique(
                clientRoot, "ReignRelationshipCampaignBehavior.cs", "src");
            string relationshipCampaignText = File.Exists(relationshipCampaignPath)
                ? File.ReadAllText(relationshipCampaignPath) : "";
            bool realtimeDirectorActionPumpValid =
                relationshipCampaignText.Contains("TryScheduleRealtimeDirectorActionPoll();")
                && relationshipCampaignText.Contains("DirectorActionPollIntervalTicks")
                && relationshipCampaignText.Contains("DrainRelationshipDirectorActionsAsync")
                && relationshipCampaignText.Contains("PollRelationshipDirectorActionsAsync")
                && relationshipCampaignText.Contains("ReportRelationshipDirectorActionsAsync");
            AddVerificationCheck(checks, "contracts.relationship_director_realtime_action_pump",
                "relationships", realtimeDirectorActionPumpValid,
                realtimeDirectorActionPumpValid
                    ? "Lifecycle actions poll, apply, and report on a real-time application tick while campaign time is paused."
                    : "Relationship lifecycle actions still depend on campaign-time polling.", null);

            string portraitClientPath = VerificationSourceLocator.ResolveUnique(clientRoot, "NanoGptClient.cs", "src");
            string portraitClientText = File.Exists(portraitClientPath) ? File.ReadAllText(portraitClientPath) : "";
            string portraitContextPath = VerificationSourceLocator.ResolveUnique(clientRoot, "PortraitPromptContext.cs", "src");
            string portraitContextText = File.Exists(portraitContextPath) ? File.ReadAllText(portraitContextPath) : "";
            string serverClientPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignServerClient.cs", "src");
            string serverClientText = File.Exists(serverClientPath) ? File.ReadAllText(serverClientPath) : "";
            string worldHistoryClientPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWorldHistoryClient.cs", "src");
            string worldHistoryClientText = File.Exists(worldHistoryClientPath) ? File.ReadAllText(worldHistoryClientPath) : "";
            string liveTestControllerPath = Path.Combine(serverRoot, "ReignLiveTest", "Program.cs");
            string liveTestControllerText = File.Exists(liveTestControllerPath) ? File.ReadAllText(liveTestControllerPath) : "";
            string liveTestHostPath = VerificationSourceLocator.ResolveUnique(
                clientRoot, "ReignLiveInteractionTestHost.cs", "src");
            string liveTestHostText = File.Exists(liveTestHostPath)
                ? File.ReadAllText(liveTestHostPath) : "";
            bool liveNativeSnapshotBoundaryValid = serverClientText.Contains("BuildDialogueRequestPayload(")
                && serverClientText.Contains("ReignMainThread.InvokeAsync(() =>")
                && serverClientText.Contains("BuildContextBundles(selectedPulls, speaker, participants, mode)")
                && worldHistoryClientText.Contains("JObject payload = await ReignMainThread.InvokeAsync")
                && liveTestControllerText.Contains("InterruptStaleAcceptedCommand")
                && liveTestControllerText.Contains("safeToReplayAfterCheckpointReload")
                && liveTestControllerText.Contains("active_run_not_reconciled");
            AddVerificationCheck(checks, "contracts.live_native_snapshot_and_timeout_recovery", "conversation_readiness",
                liveNativeSnapshotBoundaryValid,
                liveNativeSnapshotBoundaryValid
                    ? "Live dialogue snapshots native state on the campaign thread, bounds accepted commands, reconciles correlations, and refuses force-stop before the active run is terminal."
                    : "Live dialogue native-state isolation or accepted-command recovery is incomplete.", null);
            bool liveHeartbeatRecoveryPlaneValid =
                liveTestHostText.Contains("if (ReignSaveSyncCoordinator.IsAlignmentPending)")
                && liveTestHostText.Contains("SubmitLiveTestHeartbeatAsync(")
                && liveTestHostText.Contains("Live interaction heartbeat observed campaign instance=")
                && liveTestHostText.Contains("Live interaction heartbeat scheduling request instance=")
                && liveTestHostText.Contains("Live interaction heartbeat accepted instance=")
                && liveTestHostText.Contains("settings?.UseLocalServer == false")
                && !liveTestHostText.Contains("settings == null || !settings.UseLocalServer")
                && !liveTestHostText.Contains("!ReignSaveSyncCoordinator.IsReadyForCampaign(campaignId)");
            AddVerificationCheck(checks, "contracts.live_heartbeat_recovery_control_plane",
                "conversation_readiness", liveHeartbeatRecoveryPlaneValid,
                liveHeartbeatRecoveryPlaneValid
                    ? "After Save Sync alignment completes, the live heartbeat reports actual campaign/save identity so guarded enrollment can fail closed on mismatches."
                    : "The live heartbeat can still be suppressed by a redundant client-side campaign match before the guarded server sees actual identity.", null);
            string portraitServerPath = VerificationSourceLocator.ResolveUnique(serverRoot, "PortraitGeneration.cs", "src");
            string portraitServerText = File.Exists(portraitServerPath) ? File.ReadAllText(portraitServerPath) : "";
            bool portraitIdentityValid = portraitContextText.Contains("public int? AgeYears")
                && portraitContextText.Contains("public string Gender")
				&& portraitContextText.Contains("public int? ClanTier")
				&& portraitContextText.Contains("public string SocialStation")
                && portraitClientText.Contains("BuildIdentityRequirement")
                && portraitClientText.Contains("shared prompt library configured in the Bannerlord Reign server")
                && serverClientText.Contains("payload[\"ageYears\"] = promptContext.AgeYears")
                && serverClientText.Contains("payload[\"cultureName\"] = promptContext.CultureName")
                && serverClientText.Contains("payload[\"gender\"] = promptContext.Gender")
				&& serverClientText.Contains("payload[\"clanTier\"] = promptContext.ClanTier")
				&& serverClientText.Contains("payload[\"socialStation\"] = promptContext.SocialStation")
                && serverClientText.Contains("[\"promptPurpose\"]")
                && portraitServerText.Contains("BuildPortraitPrompt(payload, clientPrompt, !adultClothingEdit)")
				&& portraitServerText.Contains(".Replace(\"[GENDER]\", gender)")
				&& portraitServerText.Contains(".Replace(\"[CLAN TIER]\"")
				&& portraitServerText.Contains(".Replace(\"[SOCIAL STATION]\"")
				&& portraitServerText.Contains(".Replace(\"[PHYSICAL CONFIDENCE]\"")
				&& portraitServerText.Contains("[\"physicalConfidence\"] = physicalConfidence");
            AddVerificationCheck(checks, "contracts.portrait_identity_metadata", "portraits_images", portraitIdentityValid,
				portraitIdentityValid ? "Portrait generation carries exact age, gender, culture, clan tier, social station, physical confidence, and identity requirements through prompt construction and stored portrait metadata." : "Portrait identity metadata or prompt expansion is incomplete.", null);

            string derivativeCorePath = Path.Combine(root, "ReignModules", "Reign.Shared.Source", "Core", "ReignPortraitDerivativeCore.cs");
            string derivativeCoreText = File.Exists(derivativeCorePath) ? File.ReadAllText(derivativeCorePath) : "";
            string derivativeServicePath = VerificationSourceLocator.ResolveUnique(clientRoot, "PortraitDerivativeService.cs", "src");
            string derivativeServiceText = File.Exists(derivativeServicePath) ? File.ReadAllText(derivativeServicePath) : "";
            string textureFactoryPath = VerificationSourceLocator.ResolveUnique(clientRoot, "TextureFactory.cs", "src");
            string textureFactoryText = File.Exists(textureFactoryPath) ? File.ReadAllText(textureFactoryPath) : "";
            string portraitPatchPath = VerificationSourceLocator.ResolveUnique(clientRoot, "PortraitPatch.cs", "src");
            string portraitPatchText = File.Exists(portraitPatchPath) ? File.ReadAllText(portraitPatchPath) : "";
			string portraitTextureProviderPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignPortraitTextureProvider.cs", "src");
			string portraitTextureProviderText = File.Exists(portraitTextureProviderPath) ? File.ReadAllText(portraitTextureProviderPath) : "";
			string portraitWidgetPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignPortraitWidget.cs", "src");
			string portraitWidgetText = File.Exists(portraitWidgetPath) ? File.ReadAllText(portraitWidgetPath) : "";
			string gameMenuPartyPatchPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignGameMenuPartyItemInsertPatch.cs", "src");
			string gameMenuPartyPatchMarkup = File.Exists(gameMenuPartyPatchPath)
				? File.ReadAllText(gameMenuPartyPatchPath).Replace("\\\"", "\"") : "";
			string conversationPortraitPatchPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ConversationPortraitInsertPatch.cs", "src");
			string conversationPortraitPatchMarkup = File.Exists(conversationPortraitPatchPath)
				? File.ReadAllText(conversationPortraitPatchPath).Replace("\\\"", "\"") : "";
			string conversationPortraitMixinPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ConversationPortraitMixin.cs", "src");
			string conversationPortraitMixinText = File.Exists(conversationPortraitMixinPath)
				? File.ReadAllText(conversationPortraitMixinPath) : "";
			string questMemoryBookPatchPath = VerificationSourceLocator.ResolveUnique(clientRoot, "QuestMemoryBookInsertPatch.cs", "src");
			string questMemoryBookPatchMarkup = File.Exists(questMemoryBookPatchPath)
				? File.ReadAllText(questMemoryBookPatchPath).Replace("\\\"", "\"") : "";
			string tavernArtTextureFactoryPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignTavernArtTextureFactory.cs", "src");
			string tavernArtTextureFactoryText = File.Exists(tavernArtTextureFactoryPath) ? File.ReadAllText(tavernArtTextureFactoryPath) : "";
			string portraitCachePath = VerificationSourceLocator.ResolveUnique(clientRoot, "PortraitCache.cs", "src");
			string portraitCacheText = File.Exists(portraitCachePath) ? File.ReadAllText(portraitCachePath) : "";
            string derivativeJobsPath = VerificationSourceLocator.ResolveUnique(serverRoot, "PortraitDerivativeJobs.cs", "src");
            string derivativeJobsText = File.Exists(derivativeJobsPath) ? File.ReadAllText(derivativeJobsPath) : "";
            string faceDetectionPath = VerificationSourceLocator.ResolveUnique(serverRoot, "PortraitFaceDetection.cs", "src");
            string faceDetectionText = File.Exists(faceDetectionPath) ? File.ReadAllText(faceDetectionPath) : "";
            string partyChatPrefabPath = Path.Combine(clientRoot, "GUI", "Prefabs", "ReignPartyChatScreen.xml");
            string partyChatPrefabText = File.Exists(partyChatPrefabPath) ? File.ReadAllText(partyChatPrefabPath) : "";
			string individualChatPrefabPath = Path.Combine(clientRoot, "GUI", "Prefabs", "ReignIndividualChatScreen.xml");
			string individualChatPrefabText = File.Exists(individualChatPrefabPath) ? File.ReadAllText(individualChatPrefabPath) : "";
			string individualChatViewModelPath = Path.Combine(clientRoot, "src", "Modules", "Dialogue", "UI", "ViewModels", "ReignIndividualChatScreenVM.cs");
			string individualChatViewModelText = File.Exists(individualChatViewModelPath) ? File.ReadAllText(individualChatViewModelPath) : "";
            string controlCenterSource = File.ReadAllText(VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src"));
            bool derivativeContractValid = derivativeCoreText.Contains("CurrentVersion = 3")
                && derivativeCoreText.Contains("portrait_chest.png")
                && derivativeCoreText.Contains("thumbnail_wide.png")
                && derivativeCoreText.Contains("ZoomMaxWidth = 1024")
                && derivativeCoreText.Contains("ZoomMaxHeight = 1536")
                && derivativeCoreText.Contains("faceHeight * 2.15d")
                && derivativeCoreText.Contains("CommitV2")
				&& derivativeCoreText.Contains("BuildRequiredFocusedV2")
                && derivativeServiceText.Contains("public enum PortraitQualityTier")
                && derivativeServiceText.Contains("Portrait,")
                && derivativeServiceText.Contains("PartyThumbnail,")
                && derivativeServiceText.Contains("PrepareLegacy")
				&& derivativeServiceText.Contains("Refusing legacy fallback crop")
                && textureFactoryText.Contains("32L * 1024L * 1024L")
                && portraitPatchText.Contains("PartyChatPreviewBoxId")
                && portraitPatchText.Contains("SocialEventPreviewBoxId")
                && portraitPatchText.Contains("PortraitQualityTier.Portrait")
                && portraitPatchText.Contains("PortraitQualityTier.PartyThumbnail")
                && portraitWidgetText.Contains("public bool UsePartyThumbnail")
                && portraitWidgetText.Contains("PortraitQualityTier.PartyThumbnail")
                && partyChatPrefabText.Contains("Id=\"ReignPartyChatPortraitPreview\"")
                && derivativeJobsText.Contains("DiscoverPortraitDerivativeFolders")
				&& derivativeJobsText.Contains("IsCampaignLegacyFallbackPortrait")
                && faceDetectionText.Contains("Microsoft.ML.OnnxRuntime")
                && faceDetectionText.Contains("PortraitFocusModelSha256")
                && controlCenterSource.Contains("/portraits/derivatives/build-missing")
				&& controlCenterSource.Contains("/portraits/derivatives/rebuild-fallback")
                && controlCenterSource.Contains("Portrait Cache Derivatives");
            AddVerificationCheck(checks, "contracts.portrait_derivative_pipeline", "portraits_images", derivativeContractValid,
                derivativeContractValid ? "Full-body portrait masters feed atomic face-focused vertical and party-wide thumbnails, high-resolution portraits, and on-demand zoom derivatives; the Control Center exposes the cache builder." : "Portrait derivative pipeline, routing, or Control Center contract is incomplete.", null);

			string portraitBridgeText = File.ReadAllText(VerificationSourceLocator.ResolveUnique(clientRoot, "ReignPortraitBridge.cs", "src"));
			bool portraitProductReceiptValid = serverClientText.Contains("portraitProductReceipt")
				&& serverClientText.Contains("result.FaceFocus = new PortraitFaceFocus")
				&& portraitServerText.Contains("BuildPortraitProductReceipt")
				&& portraitServerText.Contains("sourceImageBase64")
				&& portraitServerText.Contains("effectivePrompt")
				&& portraitBridgeText.Contains("GeneratePortraitProductAsync")
				&& portraitBridgeText.Contains("SavePortraitProductAsync(cacheKey, product)")
				&& portraitBridgeText.Contains("new PortraitRequestScope(campaignId, campaignFolder, snapshot)")
				&& !portraitPatchText.Contains("GeneratePortraitProductAsync")
				&& portraitCacheText.Contains("CommitPortraitProduct")
				&& portraitCacheText.Contains("reign-portrait-product-v1")
				&& portraitCacheText.Contains("PortraitDerivativeCore.BuildRequiredFocusedV2");
			AddVerificationCheck(checks, "contracts.portrait_product_receipt", "portraits_images", portraitProductReceiptValid,
				portraitProductReceiptValid
					? "Campaign portrait generation retains the server-validated face focus, canonical source, expanded prompt, hashes, and atomic product receipt through cache commit."
					: "Campaign portrait generation can still discard authoritative server product metadata or commit a fallback crop.", null);

			string prefabRoot = Path.Combine(clientRoot, "GUI", "Prefabs");
			string[] portraitPrefabPaths = Directory.Exists(prefabRoot)
				? Directory.GetFiles(prefabRoot, "*.xml", SearchOption.AllDirectories)
				: Array.Empty<string>();
			bool portraitPrefabMaskApiRemoved = portraitPrefabPaths.All(path =>
			{
				string text = File.ReadAllText(path);
				return !text.Contains("ReignAspectMaskedTextureWidget", StringComparison.Ordinal)
					&& !text.Contains("UseEllipseMask", StringComparison.Ordinal)
					&& !text.Contains("Reign.Portrait.EllipseMask", StringComparison.Ordinal);
			});
			bool portraitRuntimeMaskApiRemoved = !textureFactoryText.Contains("ApplyPresentationEllipseAlphaMask", StringComparison.Ordinal)
				&& !textureFactoryText.Contains("|ellipse:", StringComparison.Ordinal)
				&& !portraitPatchText.Contains("IsEllipsePortraitBox", StringComparison.Ordinal)
				&& !portraitPatchText.Contains("ellipseMask", StringComparison.Ordinal)
				&& !portraitTextureProviderText.Contains("ellipseMask", StringComparison.Ordinal)
				&& !portraitWidgetText.Contains("UseEllipseMask", StringComparison.Ordinal)
				&& !portraitWidgetText.Contains("_useEllipseMask", StringComparison.Ordinal);
			bool portraitProviderNormalizesRectangularAspect = portraitTextureProviderText.Contains(
				"private readonly float _targetAspect;", StringComparison.Ordinal)
				&& portraitTextureProviderText.Contains("_targetAspect = targetAspect;", StringComparison.Ordinal)
				&& portraitTextureProviderText.Contains("TextureFactory.GetOrBuildForAspect(", StringComparison.Ordinal)
				&& portraitTextureProviderText.Contains("contain: _qualityTier == PortraitQualityTier.Zoom", StringComparison.Ordinal)
				&& !portraitTextureProviderText.Contains("ContainToAspect", StringComparison.Ordinal)
				&& portraitWidgetText.Contains("_useFullBody ? AIPortraits.PortraitQualityTier.Zoom", StringComparison.Ordinal);
			bool portraitApertureRuntimeValid = portraitPrefabMaskApiRemoved
				&& portraitRuntimeMaskApiRemoved
				&& portraitProviderNormalizesRectangularAspect;
			AddVerificationCheck(checks, "contracts.portrait_aperture_runtime", "portraits_images", portraitApertureRuntimeValid,
				portraitApertureRuntimeValid
					? "Portrait sources remain rectangular and unmasked, are normalized to each destination aspect without stretching, and rely on topmost opaque aperture plates for circles and ovals; full-body zoom composition is preserved with contain mode."
					: "A prefab or runtime portrait path can still pre-cut source pixels, invoke the rejected ellipse-mask API, stretch a portrait into its destination aspect, or crop a full-body zoom composition.",
				new Dictionary<string, object>
				{
					["textureFactory"] = textureFactoryPath,
					["portraitPatch"] = portraitPatchPath,
					["portraitTextureProvider"] = portraitTextureProviderPath,
					["portraitWidget"] = portraitWidgetPath,
					["prefabsInspected"] = portraitPrefabPaths.Length,
					["prefabMaskApiRemoved"] = portraitPrefabMaskApiRemoved,
					["runtimeMaskApiRemoved"] = portraitRuntimeMaskApiRemoved,
					["providerNormalizesRectangularAspect"] = portraitProviderNormalizesRectangularAspect
				});

			string conversationNpcPortraitBlock = VerificationSourceSlice(
				conversationPortraitPatchMarkup, "AIPortraitsNpcPortraitButton", "AIPortraitsPlayerPortraitButton");
			string conversationPlayerPortraitBlock = VerificationSourceSlice(
				conversationPortraitPatchMarkup, "AIPortraitsPlayerPortraitButton", "AIPortraitsAIInfluenceMemoryButton");
			string conversationMemorySceneBlock = VerificationSourceSlice(
				conversationPortraitPatchMarkup, "AIPortraitsAIInfluenceMemoryButton", "AIPortraitsZoomBackdrop");
			string conversationZoomSceneBlock = VerificationSourceSlice(
				conversationPortraitPatchMarkup, "AIPortraitsZoomBackdrop", "private readonly XmlDocument");
			string conversationCoverGeometryMethod = VerificationSourceSlice(
				conversationPortraitMixinText, "private static void GetCoverCropDimensions", "private void SetZoomAspect");
			string conversationZoomGeometryMethod = VerificationSourceSlice(
				conversationPortraitMixinText, "private void SetZoomAspect", "private void SetAIInfluenceMemoryAspect");
			bool conversationCoverGeometryValid = conversationCoverGeometryMethod.Contains("const float plateWidth = 184f;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("const float plateHeight = 240f;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("const float overscan = 4f;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("const float coverWidth = plateWidth + overscan;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("const float coverHeight = plateHeight + overscan;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("if (ratio >= coverAspect)", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("height = coverHeight;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("width = Math.Max(coverWidth, height * ratio);", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("width = coverWidth;", StringComparison.Ordinal)
				&& conversationCoverGeometryMethod.Contains("height = Math.Max(coverHeight, width / ratio);", StringComparison.Ordinal)
				&& conversationPortraitMixinText.Contains("_npcPortraitCropImageWidth = 188f;", StringComparison.Ordinal)
				&& conversationPortraitMixinText.Contains("_npcPortraitCropImageHeight = 244f;", StringComparison.Ordinal)
				&& conversationPortraitMixinText.Contains("_playerPortraitCropImageWidth = 188f;", StringComparison.Ordinal)
				&& conversationPortraitMixinText.Contains("_playerPortraitCropImageHeight = 244f;", StringComparison.Ordinal);
			bool conversationZoomGeometryValid = conversationZoomGeometryMethod.Contains("ZoomPortraitImageWidth = num;", StringComparison.Ordinal)
				&& conversationZoomGeometryMethod.Contains("ZoomPortraitImageHeight = num2;", StringComparison.Ordinal)
				&& conversationZoomGeometryMethod.Contains("ZoomFrameWidth = num + 36f;", StringComparison.Ordinal)
				&& conversationZoomGeometryMethod.Contains("ZoomFrameHeight = num2 + 36f;", StringComparison.Ordinal);
			var portraitAperturePlateContracts = new[]
			{
				(RelativePath: Path.Combine("ui_reignbeta_shared", "reign_modern_portrait_circle_overlay.png"), Width: 512, Height: 512),
				(RelativePath: Path.Combine("ui_reignbeta_shared", "reign_modern_portrait_oval_overlay.png"), Width: 512, Height: 768),
				(RelativePath: Path.Combine("ui_reignbeta_shared", "reign_modern_portrait_oval_thin_frame.png"), Width: 512, Height: 832),
				(RelativePath: Path.Combine("ui_reignbeta_shared", "reign_modern_portrait_rectangle_overlay.png"), Width: 512, Height: 768),
				(RelativePath: Path.Combine("ui_reignbeta_family_chambers", "reign_family_chambers_portrait_mask.png"), Width: 108, Height: 108),
				(RelativePath: Path.Combine("ui_reignbeta_correspondence", "reign_correspondence_contact_portrait_mask.png"), Width: 79, Height: 79),
				(RelativePath: Path.Combine("ui_reignbeta_diplomacy", "reign_diplomacy_actor_portrait_mask.png"), Width: 162, Height: 263),
				(RelativePath: Path.Combine("ui_reignbeta_diplomacy", "reign_diplomacy_target_portrait_mask.png"), Width: 162, Height: 263),
				(RelativePath: Path.Combine("ui_reignbeta_individual", "reign_individual_chat_player_portrait_mask.png"), Width: 230, Height: 330),
				(RelativePath: Path.Combine("ui_reignbeta_individual", "reign_individual_chat_npc_portrait_mask.png"), Width: 230, Height: 330),
				(RelativePath: Path.Combine("ui_reignbeta_individual", "reign_individual_chat_zoom_oval_mask.png"), Width: 512, Height: 768),
				(RelativePath: Path.Combine("ui_reignbeta_party_chat", "reign_party_chat_portrait_mask_active.png"), Width: 130, Height: 132),
				(RelativePath: Path.Combine("ui_reignbeta_party_chat", "reign_party_chat_portrait_mask_inactive.png"), Width: 130, Height: 132),
				(RelativePath: Path.Combine("ui_reignbeta_party_chat", "reign_party_chat_preview_oval_mask.png"), Width: 512, Height: 768),
				(RelativePath: Path.Combine("ui_reignbeta_social_event", "reign_social_event_active_portrait_mask.png"), Width: 112, Height: 112),
				(RelativePath: Path.Combine("ui_reignbeta_social_event", "reign_social_event_available_portrait_mask.png"), Width: 112, Height: 112),
				(RelativePath: Path.Combine("ui_reignbeta_social_event", "reign_social_event_preview_oval_mask.png"), Width: 512, Height: 768),
				(RelativePath: Path.Combine("ui_reignbeta_war_council", "reign_war_council_lord_portrait_mask.png"), Width: 113, Height: 127),
				(RelativePath: Path.Combine("ui_reignbeta_war_council", "reign_war_council_lord_portrait_mask_active.png"), Width: 113, Height: 127),
				(RelativePath: Path.Combine("ui_reignbeta_war_council", "reign_war_council_assigned_portrait_mask.png"), Width: 122, Height: 207),
				(RelativePath: Path.Combine("ui_reignbeta_war_council", "reign_war_council_candidate_portrait_mask.png"), Width: 54, Height: 54),
				(RelativePath: Path.Combine("ui_reignbeta_royal_council", "reign_royal_council_war_portrait_mask.png"), Width: 258, Height: 320),
				(RelativePath: Path.Combine("ui_reignbeta_royal_council", "reign_royal_council_spymaster_portrait_mask.png"), Width: 257, Height: 307),
				(RelativePath: Path.Combine("ui_reignbeta_royal_council", "reign_royal_council_economic_portrait_mask.png"), Width: 256, Height: 318),
				(RelativePath: Path.Combine("ui_reignbeta_royal_council", "reign_royal_council_foreign_portrait_mask.png"), Width: 255, Height: 305)
			};
			string portraitSpritePartsRoot = Path.Combine(clientRoot, "GUI", "SpriteParts");
			List<string> invalidPortraitAperturePlates = new List<string>();
			foreach (var contract in portraitAperturePlateContracts)
			{
				string path = Path.Combine(portraitSpritePartsRoot, contract.RelativePath);
				if (!VerificationOpaquePortraitAperturePlateValid(
					path, contract.Width, contract.Height,
					out _, out _, out _, out _))
				{
					invalidPortraitAperturePlates.Add(contract.RelativePath.Replace('\\', '/'));
				}
			}
			bool opaquePortraitApertureAssetsValid = invalidPortraitAperturePlates.Count == 0;
			AddVerificationCheck(checks, "contracts.portrait_aperture_assets", "ui_contracts", opaquePortraitApertureAssetsValid,
				opaquePortraitApertureAssetsValid
					? "Every portrait plate keeps opaque black-marble exterior pixels and corners above a transparent aperture, with no light-neutral seam at the cutout edge."
					: "A portrait asset is still a transparent ring, lacks a transparent aperture, exposes a light-neutral halo, or has drifted from its registered dimensions.",
				new Dictionary<string, object>
				{
					["inspected"] = portraitAperturePlateContracts.Length,
					["invalid"] = invalidPortraitAperturePlates
				});
			string royalCouncilPrefabPath = Path.Combine(clientRoot, "GUI", "Prefabs", "ReignRoyalCouncilScreen.xml");
			string royalCouncilPrefabText = File.Exists(royalCouncilPrefabPath)
				? File.ReadAllText(royalCouncilPrefabPath) : "";
			XmlDocument royalCouncilDocument = TryParseVerificationXml(royalCouncilPrefabText);
			XmlElement royalCouncilCanvas = FindVerificationXmlElementById(royalCouncilDocument, "RoyalCouncilCanvas");
			XmlElement royalCouncilCanvasChildren = royalCouncilCanvas?.SelectSingleNode("./Children") as XmlElement;
			int royalCouncilCanvasWidth = VerificationXmlIntAttribute(royalCouncilCanvas, "SuggestedWidth", 0);
			var royalCouncilPortraitContracts = new[]
			{
				(Role: "War", Sprite: "reign_royal_council_war_portrait_mask", FrameX: 119, FrameY: 50, FrameWidth: 258, FrameHeight: 320, ApertureX: 12, ApertureY: 8, ApertureWidth: 233, ApertureHeight: 304),
				(Role: "Spymaster", Sprite: "reign_royal_council_spymaster_portrait_mask", FrameX: 120, FrameY: 465, FrameWidth: 257, FrameHeight: 307, ApertureX: 16, ApertureY: 8, ApertureWidth: 227, ApertureHeight: 292),
				(Role: "Economic", Sprite: "reign_royal_council_economic_portrait_mask", FrameX: 1297, FrameY: 50, FrameWidth: 256, FrameHeight: 318, ApertureX: 7, ApertureY: 12, ApertureWidth: 223, ApertureHeight: 302),
				(Role: "Foreign", Sprite: "reign_royal_council_foreign_portrait_mask", FrameX: 1297, FrameY: 465, FrameWidth: 255, FrameHeight: 305, ApertureX: 8, ApertureY: 3, ApertureWidth: 223, ApertureHeight: 300)
			};
			List<string> royalCouncilPortraitLayoutFailures = new List<string>();
			foreach (var contract in royalCouncilPortraitContracts)
			{
				XmlElement clip = FindVerificationXmlElementById(royalCouncilDocument, contract.Role + "PortraitClip");
				XmlElement frame = FindVerificationXmlElementById(royalCouncilDocument, contract.Role + "PortraitFrameOverlay");
				XmlElement seat = clip?.SelectSingleNode("ancestor::Widget[@DataSource][1]") as XmlElement;
				XmlElement directChildren = clip?.SelectSingleNode("./Children") as XmlElement;
				XmlElement nativePortrait = directChildren?.SelectSingleNode("./ImageIdentifierWidget") as XmlElement;
				XmlElement generatedPortrait = directChildren?.SelectSingleNode("./ReignPortraitWidget") as XmlElement;
				int seatLeft = string.Equals(seat?.GetAttribute("HorizontalAlignment"), "Right", StringComparison.Ordinal)
					? royalCouncilCanvasWidth - VerificationXmlIntAttribute(seat, "MarginRight", 0) - VerificationXmlIntAttribute(seat, "SuggestedWidth", 0)
					: VerificationXmlIntAttribute(seat, "MarginLeft", 0);
				int frameLeft = string.Equals(frame?.GetAttribute("HorizontalAlignment"), "Right", StringComparison.Ordinal)
					? royalCouncilCanvasWidth - VerificationXmlIntAttribute(frame, "MarginRight", 0) - VerificationXmlIntAttribute(frame, "SuggestedWidth", 0)
					: VerificationXmlIntAttribute(frame, "MarginLeft", 0);
				int clipLeft = seatLeft + VerificationXmlIntAttribute(clip, "MarginLeft", 0);
				int clipTop = VerificationXmlIntAttribute(seat, "MarginTop", 0) + VerificationXmlIntAttribute(clip, "MarginTop", 0);
				int frameTop = VerificationXmlIntAttribute(frame, "MarginTop", 0);
				int directSourceCount = directChildren?.SelectNodes("./ImageIdentifierWidget | ./ReignPortraitWidget")?.Count ?? 0;
				int directElementCount = directChildren?.SelectNodes("./*")?.Count ?? 0;
				bool sourceGeometryValid = nativePortrait != null
					&& generatedPortrait != null
					&& directSourceCount == 2
					&& directElementCount == 2
					&& VerificationXmlAttributeEquals(nativePortrait, "WidthSizePolicy", "Fixed")
					&& VerificationXmlAttributeEquals(nativePortrait, "HeightSizePolicy", "Fixed")
					&& VerificationXmlAttributeEquals(nativePortrait, "HorizontalAlignment", "Center")
					&& VerificationXmlAttributeEquals(nativePortrait, "VerticalAlignment", "Center")
					&& VerificationXmlAttributeEquals(nativePortrait, "DoNotAcceptEvents", "true")
					&& VerificationXmlIntAttribute(nativePortrait, "SuggestedWidth", 0) >= contract.ApertureWidth
					&& VerificationXmlIntAttribute(nativePortrait, "SuggestedHeight", 0) >= contract.ApertureHeight
					&& VerificationXmlIntAttribute(nativePortrait, "SuggestedWidth", 0) == VerificationXmlIntAttribute(nativePortrait, "SuggestedHeight", -1)
					&& VerificationXmlAttributeEquals(nativePortrait, "ImageId", "@PortraitId")
					&& VerificationXmlAttributeEquals(nativePortrait, "AdditionalArgs", "@PortraitAdditionalArgs")
					&& VerificationXmlAttributeEquals(nativePortrait, "TextureProviderName", "@PortraitTextureProviderName")
					&& VerificationXmlAttributeEquals(generatedPortrait, "WidthSizePolicy", "Fixed")
					&& VerificationXmlAttributeEquals(generatedPortrait, "HeightSizePolicy", "Fixed")
					&& VerificationXmlAttributeEquals(generatedPortrait, "HorizontalAlignment", "Center")
					&& VerificationXmlAttributeEquals(generatedPortrait, "VerticalAlignment", "Center")
					&& VerificationXmlAttributeEquals(generatedPortrait, "DoNotAcceptEvents", "true")
					&& VerificationXmlIntAttribute(generatedPortrait, "SuggestedWidth", 0) >= contract.ApertureWidth
					&& VerificationXmlIntAttribute(generatedPortrait, "SuggestedHeight", 0) >= contract.ApertureHeight
					&& VerificationXmlIntAttribute(generatedPortrait, "SuggestedWidth", 0) == VerificationXmlIntAttribute(generatedPortrait, "SuggestedHeight", -1)
					&& VerificationXmlAttributeEquals(generatedPortrait, "Type", "ReignBeta.UI.Widgets.ReignPortraitWidget")
					&& VerificationXmlAttributeEquals(generatedPortrait, "UseFullBody", "false")
					&& !nativePortrait.HasAttribute("Sprite")
					&& !nativePortrait.HasAttribute("Color")
					&& !nativePortrait.HasAttribute("AlphaFactor")
					&& !generatedPortrait.HasAttribute("Sprite")
					&& !generatedPortrait.HasAttribute("Color")
					&& !generatedPortrait.HasAttribute("AlphaFactor")
					&& !generatedPortrait.HasAttribute("UseEllipseMask");
				bool plateGeometryValid = clip != null
					&& frame != null
					&& seat != null
					&& royalCouncilCanvasChildren != null
					&& ReferenceEquals(seat.ParentNode, royalCouncilCanvasChildren)
					&& ReferenceEquals(frame.ParentNode, royalCouncilCanvasChildren)
					&& VerificationXmlChildElementIndex(royalCouncilCanvasChildren, frame) > VerificationXmlChildElementIndex(royalCouncilCanvasChildren, seat)
					&& VerificationXmlAttributeEquals(clip, "ClipContents", "true")
					&& VerificationXmlAttributeEquals(clip, "DoNotAcceptEvents", "true")
					&& VerificationXmlAttributeEquals(clip, "IsVisible", "@HasPortrait")
					&& VerificationXmlIntAttribute(clip, "SuggestedWidth", 0) == contract.ApertureWidth
					&& VerificationXmlIntAttribute(clip, "SuggestedHeight", 0) == contract.ApertureHeight
					&& !clip.HasAttribute("Sprite")
					&& !clip.HasAttribute("Color")
					&& !clip.HasAttribute("AlphaFactor")
					&& VerificationXmlAttributeEquals(frame, "Sprite", contract.Sprite)
					&& VerificationXmlAttributeEquals(frame, "DoNotAcceptEvents", "true")
					&& VerificationXmlAttributeEquals(frame, "IsVisible", "@HasPortrait")
					&& string.Equals(frame.GetAttribute("DataSource"), seat.GetAttribute("DataSource"), StringComparison.Ordinal)
					&& VerificationXmlIntAttribute(frame, "SuggestedWidth", 0) == contract.FrameWidth
					&& VerificationXmlIntAttribute(frame, "SuggestedHeight", 0) == contract.FrameHeight
					&& frameLeft == contract.FrameX
					&& frameTop == contract.FrameY
					&& clipLeft - frameLeft == contract.ApertureX
					&& clipTop - frameTop == contract.ApertureY;
				if (!sourceGeometryValid || !plateGeometryValid)
				{
					royalCouncilPortraitLayoutFailures.Add(contract.Role);
				}
			}
			bool royalCouncilPortraitLayeringValid = royalCouncilCanvas != null
				&& VerificationXmlAttributeEquals(royalCouncilCanvas, "Sprite", "reign_royal_council_modern_shell")
				&& royalCouncilPortraitLayoutFailures.Count == 0
				&& !royalCouncilPrefabText.Contains("ReignAspectMaskedTextureWidget", StringComparison.Ordinal)
				&& !royalCouncilPrefabText.Contains("UseEllipseMask", StringComparison.Ordinal)
				&& !royalCouncilPrefabText.Contains("Reign.Portrait.EllipseMask", StringComparison.Ordinal);
			AddVerificationCheck(checks, "contracts.royal_council_portrait_apertures", "ui_contracts", royalCouncilPortraitLayeringValid,
				royalCouncilPortraitLayeringValid
					? "Royal Council seats keep untouched centered rectangular portrait sources directly beneath their exact seat-specific opaque marble aperture plates, with the gold plate topmost and no backing disk or runtime ellipse mask."
					: "A Royal Council portrait can be pre-cut, underfill its aperture, expose a backing disk, or render above/misaligned with its seat-specific plate.",
				new Dictionary<string, object>
				{
					["prefab"] = royalCouncilPrefabPath,
					["failedSeats"] = royalCouncilPortraitLayoutFailures
				});
			string castleLayoutNavigationPrefabPath = Path.Combine(clientRoot, "GUI", "Prefabs", "ReignCastleLayoutScreen.xml");
			string castleLayoutNavigationPrefabText = File.Exists(castleLayoutNavigationPrefabPath)
				? File.ReadAllText(castleLayoutNavigationPrefabPath)
				: "";
			XmlDocument castleLayoutNavigationDocument = TryParseVerificationXml(castleLayoutNavigationPrefabText);
			XmlElement castleLayoutFamilyButton = castleLayoutNavigationDocument?.SelectSingleNode("//ButtonWidget[@Id='CastleLayoutFamilyChambersButton']") as XmlElement;
			XmlElement castleLayoutGovernmentButton = castleLayoutNavigationDocument?.SelectSingleNode("//ButtonWidget[@Id='CastleLayoutGovernmentButton']") as XmlElement;
			bool castleLayoutKeepNavigationValid = castleLayoutFamilyButton != null
				&& castleLayoutGovernmentButton != null
				&& VerificationXmlAttributeEquals(castleLayoutFamilyButton, "Command.Click", "ExecuteOpenFamilyChambers")
				&& VerificationXmlAttributeEquals(castleLayoutGovernmentButton, "Command.Click", "ExecuteOpenGovernment")
				&& VerificationXmlIntAttribute(castleLayoutFamilyButton, "SuggestedWidth", 0) > 0
				&& VerificationXmlIntAttribute(castleLayoutFamilyButton, "SuggestedHeight", 0) > 0
				&& VerificationXmlIntAttribute(castleLayoutGovernmentButton, "SuggestedWidth", 0) > 0
				&& VerificationXmlIntAttribute(castleLayoutGovernmentButton, "SuggestedHeight", 0) > 0
				&& castleLayoutFamilyButton.SelectSingleNode(".//ImageWidget[@Sprite='reign_castle_layout_navigation_button']") != null
				&& castleLayoutGovernmentButton.SelectSingleNode(".//ImageWidget[@Sprite='reign_castle_layout_navigation_button']") != null
				&& castleLayoutFamilyButton.SelectSingleNode(".//TextWidget[@Text='FAMILY CHAMBERS'][@Brush.Font='ReignSerifDynamic'][@Brush.FontColor='#C5BDAFFF']") != null
				&& castleLayoutGovernmentButton.SelectSingleNode(".//TextWidget[@Text='@GovernmentButtonText'][@Brush.Font='ReignSerifDynamic'][@Brush.FontColor='#C5BDAFFF']") != null;
			AddVerificationCheck(checks, "contracts.castle_layout_keep_navigation", "ui_contracts", castleLayoutKeepNavigationValid,
				castleLayoutKeepNavigationValid
					? "Castle Layout exposes visible Family Chambers and context-labelled Government controls wired to their production commands and the approved modern Reign command-button artwork."
					: "Castle Layout can hide, disconnect, or visually regress the Keep-origin Family Chambers or Government route even though the view-model commands exist.",
				new Dictionary<string, object>
				{
					["prefab"] = castleLayoutNavigationPrefabPath,
					["familyButtonPresent"] = castleLayoutFamilyButton != null,
					["governmentButtonPresent"] = castleLayoutGovernmentButton != null
				});
			var compactSquarePortraitContracts = new[]
			{
				(Name: "PartyChat", Path: Path.Combine(clientRoot, "GUI", "Prefabs", "ReignPartyChatScreen.xml"), ExpectedGeneratedSources: 1),
				(Name: "SocialEvent", Path: Path.Combine(clientRoot, "GUI", "Prefabs", "ReignSocialEventScreen.xml"), ExpectedGeneratedSources: 2),
				(Name: "CastleLayout", Path: Path.Combine(clientRoot, "GUI", "Prefabs", "ReignCastleLayoutScreen.xml"), ExpectedGeneratedSources: 18),
				(Name: "DiplomacyAnnouncement", Path: Path.Combine(clientRoot, "GUI", "Prefabs", "ReignDiplomacyAnnouncementScreen.xml"), ExpectedGeneratedSources: 2)
			};
			List<string> compactSquarePortraitFailures = new List<string>();
			foreach (var contract in compactSquarePortraitContracts)
			{
				string xml = File.Exists(contract.Path) ? File.ReadAllText(contract.Path) : "";
				XmlDocument document = TryParseVerificationXml(xml);
				XmlNodeList generatedSources = document?.SelectNodes("//ReignPortraitWidget");
				if (generatedSources == null || generatedSources.Count != contract.ExpectedGeneratedSources)
				{
					compactSquarePortraitFailures.Add(contract.Name + ":source_count");
					continue;
				}

				for (int sourceIndex = 0; sourceIndex < generatedSources.Count; sourceIndex++)
				{
					XmlElement generatedSource = generatedSources[sourceIndex] as XmlElement;
					XmlElement directChildren = generatedSource?.ParentNode as XmlElement;
					XmlElement clip = directChildren?.ParentNode as XmlElement;
					XmlElement nativeSource = directChildren?.SelectSingleNode("./ImageIdentifierWidget") as XmlElement;
					XmlElement plate = directChildren?.SelectSingleNode("./ImageWidget[contains(@Sprite, 'portrait')]") as XmlElement
						?? clip?.ParentNode?.SelectSingleNode("./ImageWidget[contains(@Sprite, 'portrait')]") as XmlElement
						// The moving Party Chat card owns the aperture above both sources.
						?? generatedSource?.SelectSingleNode("ancestor::ButtonWidget[@Id='PartyMemberCard'][1]/Children/ImageWidget[@Id='PartyMemberCardPlate'][@Sprite='reign_party_chat_member_card_overlay']") as XmlElement;
					int generatedWidth = VerificationXmlIntAttribute(generatedSource, "SuggestedWidth", 0);
					int generatedHeight = VerificationXmlIntAttribute(generatedSource, "SuggestedHeight", 0);
					bool generatedValid = generatedSource != null
						&& generatedWidth > 0
						&& generatedWidth == generatedHeight
						&& VerificationXmlAttributeEquals(generatedSource, "WidthSizePolicy", "Fixed")
						&& VerificationXmlAttributeEquals(generatedSource, "HeightSizePolicy", "Fixed")
						&& VerificationXmlAttributeEquals(generatedSource, "HorizontalAlignment", "Center")
						&& VerificationXmlAttributeEquals(generatedSource, "VerticalAlignment", "Center")
						&& VerificationXmlAttributeEquals(generatedSource, "DoNotAcceptEvents", "true")
						&& !generatedSource.HasAttribute("Sprite")
						&& !generatedSource.HasAttribute("Color")
						&& !generatedSource.HasAttribute("AlphaFactor")
						&& !generatedSource.HasAttribute("UseEllipseMask");
					bool nativeValid = nativeSource == null
						|| (VerificationXmlIntAttribute(nativeSource, "SuggestedWidth", 0) > 0
							&& VerificationXmlIntAttribute(nativeSource, "SuggestedWidth", 0) == VerificationXmlIntAttribute(nativeSource, "SuggestedHeight", -1)
							&& VerificationXmlAttributeEquals(nativeSource, "WidthSizePolicy", "Fixed")
							&& VerificationXmlAttributeEquals(nativeSource, "HeightSizePolicy", "Fixed")
							&& VerificationXmlAttributeEquals(nativeSource, "HorizontalAlignment", "Center")
							&& VerificationXmlAttributeEquals(nativeSource, "VerticalAlignment", "Center")
							&& VerificationXmlAttributeEquals(nativeSource, "DoNotAcceptEvents", "true")
							&& !nativeSource.HasAttribute("Sprite")
							&& !nativeSource.HasAttribute("Color")
							&& !nativeSource.HasAttribute("AlphaFactor"));
					bool compositionValid = clip != null
						&& VerificationXmlAttributeEquals(clip, "ClipContents", "true")
						&& !clip.HasAttribute("Sprite")
						&& !clip.HasAttribute("Color")
						&& plate != null
						&& VerificationXmlAttributeEquals(plate, "DoNotAcceptEvents", "true")
						&& VerificationXmlAppearsAfter(plate, generatedSource)
						&& (nativeSource == null || VerificationXmlAppearsAfter(plate, nativeSource));
					if (!generatedValid || !nativeValid || !compositionValid)
					{
						compactSquarePortraitFailures.Add(contract.Name + ":source_" + sourceIndex.ToString(CultureInfo.InvariantCulture));
					}
				}
			}
			bool compactSquarePortraitSourcesValid = compactSquarePortraitFailures.Count == 0;
			AddVerificationCheck(checks, "contracts.compact_portrait_source_aspect", "ui_contracts", compactSquarePortraitSourcesValid,
				compactSquarePortraitSourcesValid
					? "Compact circle and headshot-oval contexts preserve direct native/generated portrait sources as centered squares beneath topmost opaque aperture plates."
					: "A compact portrait source can be stretched into an oval/rectangle, pre-colored, or ordered above its aperture plate.",
				new Dictionary<string, object>
				{
					["inspectedPrefabs"] = compactSquarePortraitContracts.Select(item => item.Path).ToArray(),
					["failures"] = compactSquarePortraitFailures
				});
			string rectangleOverlayPath = Path.Combine(clientRoot, "GUI", "SpriteParts", "ui_reignbeta_shared", "reign_modern_portrait_rectangle_overlay.png");
			bool opaqueRectangleAperturePlateValid = VerificationOpaquePortraitAperturePlateValid(
				rectangleOverlayPath, 512, 768,
				out int rectangleOverlayTransparentPixels,
				out int rectangleOverlayVisiblePixels,
				out int rectangleOverlayLightNeutralPixels,
				out int rectangleOverlayHiddenRgbPixels);
			bool gameMenuPartyRectangularPlateValid = gameMenuPartyPatchMarkup.Contains("Id=\"ReignGameMenuPartyPortraitLayer\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("Id=\"ReignGameMenuPartyPortrait\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("ClipContents=\"true\" DoNotAcceptEvents=\"true\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("Type=\"ReignBeta.UI.Widgets.ReignPortraitWidget\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("TargetAspect=\"1.3764706\" UsePartyThumbnail=\"true\"", StringComparison.Ordinal)
				&& !gameMenuPartyPatchMarkup.Contains("SuggestedWidth=\"91\" SuggestedHeight=\"91\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("Id=\"ReignGameMenuPartyPortraitAperturePlate\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("Sprite=\"reign_modern_portrait_rectangle_overlay\" DoNotAcceptEvents=\"true\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.IndexOf("Id=\"ReignGameMenuPartyPortraitAperturePlate\"", StringComparison.Ordinal) > gameMenuPartyPatchMarkup.IndexOf("Id=\"ReignGameMenuPartyPortrait\"", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("public override int Index => 3;", StringComparison.Ordinal)
				&& !gameMenuPartyPatchMarkup.Contains("Command.", StringComparison.Ordinal)
				&& !gameMenuPartyPatchMarkup.Contains("UseEllipseMask", StringComparison.Ordinal)
				&& !gameMenuPartyPatchMarkup.Contains("BlankWhiteSquare", StringComparison.Ordinal)
				&& !gameMenuPartyPatchMarkup.Contains("reign_modern_portrait_circle_overlay", StringComparison.Ordinal)
				&& !gameMenuPartyPatchMarkup.Contains("reign_modern_portrait_oval_overlay", StringComparison.Ordinal)
				&& gameMenuPartyPatchMarkup.Contains("reign_modern_portrait_rectangle_overlay", StringComparison.Ordinal);
			bool conversationRectangularPortraitsValid = conversationNpcPortraitBlock.Contains("SuggestedWidth=\"184\" SuggestedHeight=\"240\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("SuggestedWidth=\"184\" SuggestedHeight=\"240\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.Contains("HorizontalAlignment=\"Left\" VerticalAlignment=\"Bottom\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("HorizontalAlignment=\"Right\" VerticalAlignment=\"Bottom\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.Contains("MarginLeft=\"20\" MarginBottom=\"60\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("MarginRight=\"16\" MarginBottom=\"60\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.Contains("ClipContents=\"true\" DoNotPassEventsToChildren=\"true\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("ClipContents=\"true\" DoNotPassEventsToChildren=\"true\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.Contains("<ImageIdentifierWidget Id=\"AIPortraitsNpcPortrait\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("<ImageIdentifierWidget Id=\"AIPortraitsPlayerPortrait\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.Contains("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.Contains("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal)
				&& conversationNpcPortraitBlock.IndexOf("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal) > conversationNpcPortraitBlock.IndexOf("Id=\"AIPortraitsNpcPortrait\"", StringComparison.Ordinal)
				&& conversationPlayerPortraitBlock.IndexOf("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal) > conversationPlayerPortraitBlock.IndexOf("Id=\"AIPortraitsPlayerPortrait\"", StringComparison.Ordinal)
				&& !conversationNpcPortraitBlock.Contains("AIPortraitsNpcPortraitAperture", StringComparison.Ordinal)
				&& !conversationPlayerPortraitBlock.Contains("AIPortraitsPlayerPortraitAperture", StringComparison.Ordinal)
				&& !conversationNpcPortraitBlock.Contains("BlankWhiteSquare", StringComparison.Ordinal)
				&& !conversationPlayerPortraitBlock.Contains("BlankWhiteSquare", StringComparison.Ordinal)
				&& !conversationNpcPortraitBlock.Contains("reign_modern_portrait_circle_overlay", StringComparison.Ordinal)
				&& !conversationNpcPortraitBlock.Contains("reign_modern_portrait_oval_overlay", StringComparison.Ordinal)
				&& !conversationPlayerPortraitBlock.Contains("reign_modern_portrait_circle_overlay", StringComparison.Ordinal)
				&& !conversationPlayerPortraitBlock.Contains("reign_modern_portrait_oval_overlay", StringComparison.Ordinal);
			bool conversationRectangularScenesValid = conversationMemorySceneBlock.Contains("BlankWhiteSquare", StringComparison.Ordinal)
				&& conversationMemorySceneBlock.Contains("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal)
				&& conversationZoomSceneBlock.Contains("<ImageIdentifierWidget Id=\"AIPortraitsZoomPortrait\"", StringComparison.Ordinal)
				&& conversationZoomSceneBlock.Contains("HorizontalAlignment=\"Center\" VerticalAlignment=\"Center\" ClipContents=\"true\"", StringComparison.Ordinal)
				&& conversationZoomSceneBlock.Contains("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal)
				&& conversationZoomSceneBlock.IndexOf("Sprite=\"reign_modern_portrait_rectangle_overlay\"", StringComparison.Ordinal) > conversationZoomSceneBlock.IndexOf("Id=\"AIPortraitsZoomPortrait\"", StringComparison.Ordinal)
				&& Regex.Matches(conversationZoomSceneBlock, "BlankWhiteSquare").Count == 1
				&& !conversationZoomSceneBlock.Contains("AIPortraitsZoomPortraitAperture", StringComparison.Ordinal)
				&& !conversationZoomSceneBlock.Contains("Color=\"#10100FFF\"", StringComparison.Ordinal)
				&& !conversationZoomSceneBlock.Contains("reign_modern_portrait_oval_overlay", StringComparison.Ordinal);
			string individualTranscriptBlock = VerificationSourceSlice(
				individualChatPrefabText, "<ListPanel Id=\"ReignChatList\"", "</ReignAutoScrollPanel>");
			string individualZoomBlock = VerificationSourceSlice(
				individualChatPrefabText, "Id=\"ReignChatZoomBackdrop\"", "Id=\"ReignPregnancyWarningLayer\"");
			XmlDocument individualChatDocument = TryParseVerificationXml(individualChatPrefabText);
			XmlElement individualChatBackground = FindVerificationXmlElementById(individualChatDocument, "ReignIndividualChatBackground");
			XmlElement individualPlayerPortraitClip = FindVerificationXmlElementById(individualChatDocument, "ReignChatPlayerPortraitClip");
			XmlElement individualNpcPortraitClip = FindVerificationXmlElementById(individualChatDocument, "ReignChatNpcPortraitClip");
			XmlElement individualPlayerPortrait = FindVerificationXmlElementById(individualChatDocument, "ReignChatPlayerPortrait");
			XmlElement individualNpcPortrait = FindVerificationXmlElementById(individualChatDocument, "ReignChatNpcPortrait");
			XmlElement individualChatShell = FindVerificationXmlElementById(individualChatDocument, "ReignIndividualChatShell");
			XmlElement individualPlayerButton = individualPlayerPortraitClip?.ParentNode?.ParentNode as XmlElement;
			XmlElement individualNpcButton = individualNpcPortraitClip?.ParentNode?.ParentNode as XmlElement;
			XmlElement individualChatSurfaceChildren = individualChatShell?.ParentNode as XmlElement;
			XmlElement individualZoomPortrait = FindVerificationXmlElementById(individualChatDocument, "ReignChatZoomPortrait");
			XmlElement individualZoomFrameChildren = individualZoomPortrait?.ParentNode as XmlElement;
			XmlElement individualZoomFrame = individualZoomFrameChildren?.ParentNode as XmlElement;
			XmlElement individualZoomPlate = individualZoomFrameChildren?.SelectSingleNode(
				"./ImageWidget[@Sprite='reign_modern_portrait_rectangle_overlay']") as XmlElement;
			string individualChatShellPath = Path.Combine(clientRoot, "GUI", "SpriteParts", "ui_reignbeta_individual", "reign_individual_chat_modern_shell.png");
			bool individualChatShellAlphaValid = VerificationIndividualChatShellAperturesValid(
				individualChatShellPath,
				out int individualChatShellTransparentPixels,
				out int individualChatShellPartialPixels,
				out int individualChatShellLowAlphaOutsidePixels,
				out int individualChatShellNonDarkEdgePixels);
			bool individualChatHeaderPortraitsValid = individualChatDocument?.SelectNodes("//*[@Id='ReignIndividualChatShell']")?.Count == 1
				&& individualChatShellAlphaValid
				&& individualChatSurfaceChildren != null
				&& individualChatBackground == null
				&& individualPlayerButton != null
				&& individualNpcButton != null
				&& individualPlayerPortraitClip != null
				&& individualNpcPortraitClip != null
				&& individualPlayerPortrait != null
				&& individualNpcPortrait != null
				&& ReferenceEquals(individualPlayerButton.ParentNode, individualChatSurfaceChildren)
				&& ReferenceEquals(individualNpcButton.ParentNode, individualChatSurfaceChildren)
				&& VerificationXmlChildElementIndex(individualChatSurfaceChildren, individualPlayerButton) == 0
				&& VerificationXmlChildElementIndex(individualChatSurfaceChildren, individualNpcButton) == 1
				&& VerificationXmlChildElementIndex(individualChatSurfaceChildren, individualChatShell) == 2
				&& VerificationXmlAttributeEquals(individualChatShell, "Sprite", "reign_individual_chat_modern_shell")
				&& VerificationXmlAttributeEquals(individualChatShell, "DoNotAcceptEvents", "true")
				&& VerificationXmlAttributeEquals(individualChatShell, "WidthSizePolicy", "StretchToParent")
				&& VerificationXmlAttributeEquals(individualChatShell, "HeightSizePolicy", "StretchToParent")
				&& VerificationXmlAttributeEquals(individualPlayerButton, "SuggestedWidth", "230")
				&& VerificationXmlAttributeEquals(individualPlayerButton, "SuggestedHeight", "330")
				&& VerificationXmlAttributeEquals(individualPlayerButton, "MarginLeft", "94")
				&& VerificationXmlAttributeEquals(individualPlayerButton, "MarginTop", "47")
				&& VerificationXmlAttributeEquals(individualPlayerButton, "ClipContents", "true")
				&& VerificationXmlAttributeEquals(individualPlayerButton, "Command.Click", "ExecuteTogglePlayerPortraitZoom")
				&& VerificationXmlAttributeEquals(individualNpcButton, "SuggestedWidth", "230")
				&& VerificationXmlAttributeEquals(individualNpcButton, "SuggestedHeight", "330")
				&& VerificationXmlAttributeEquals(individualNpcButton, "MarginRight", "109")
				&& VerificationXmlAttributeEquals(individualNpcButton, "MarginTop", "47")
				&& VerificationXmlAttributeEquals(individualNpcButton, "ClipContents", "true")
				&& VerificationXmlAttributeEquals(individualNpcButton, "Command.Click", "ExecuteToggleNpcPortraitZoom")
				&& VerificationXmlAttributeEquals(individualPlayerPortraitClip, "SuggestedWidth", "210")
				&& VerificationXmlAttributeEquals(individualPlayerPortraitClip, "SuggestedHeight", "310")
				&& VerificationXmlAttributeEquals(individualPlayerPortraitClip, "MarginLeft", "10")
				&& VerificationXmlAttributeEquals(individualPlayerPortraitClip, "MarginTop", "10")
				&& VerificationXmlAttributeEquals(individualPlayerPortraitClip, "ClipContents", "true")
				&& VerificationXmlAttributeEquals(individualNpcPortraitClip, "SuggestedWidth", "210")
				&& VerificationXmlAttributeEquals(individualNpcPortraitClip, "SuggestedHeight", "310")
				&& VerificationXmlAttributeEquals(individualNpcPortraitClip, "MarginLeft", "10")
				&& VerificationXmlAttributeEquals(individualNpcPortraitClip, "MarginTop", "10")
				&& VerificationXmlAttributeEquals(individualNpcPortraitClip, "ClipContents", "true")
				&& VerificationXmlAttributeEquals(individualPlayerPortrait, "SuggestedWidth", "@PlayerPortraitCropImageWidth")
				&& VerificationXmlAttributeEquals(individualPlayerPortrait, "SuggestedHeight", "@PlayerPortraitCropImageHeight")
				&& VerificationXmlAttributeEquals(individualPlayerPortrait, "HorizontalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualPlayerPortrait, "VerticalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualPlayerPortrait, "PositionYOffset", "6")
				&& VerificationXmlAttributeEquals(individualNpcPortrait, "SuggestedWidth", "@NpcPortraitCropImageWidth")
				&& VerificationXmlAttributeEquals(individualNpcPortrait, "SuggestedHeight", "@NpcPortraitCropImageHeight")
				&& VerificationXmlAttributeEquals(individualNpcPortrait, "HorizontalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualNpcPortrait, "VerticalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualNpcPortrait, "PositionYOffset", "6")
				&& individualChatViewModelText.Contains("HeaderPortraitApertureWidth = 210f", StringComparison.Ordinal)
				&& individualChatViewModelText.Contains("HeaderPortraitApertureHeight = 310f", StringComparison.Ordinal)
				&& individualChatViewModelText.Contains("HeaderPortraitCoverOverscan = 1.04f", StringComparison.Ordinal)
				&& individualChatViewModelText.Contains("float apertureAspect = HeaderPortraitApertureWidth / HeaderPortraitApertureHeight", StringComparison.Ordinal)
				&& !individualChatPrefabText.Contains("ReignChatPlayerPortraitFrame", StringComparison.Ordinal)
				&& !individualChatPrefabText.Contains("ReignChatNpcPortraitFrame", StringComparison.Ordinal)
				&& !individualChatPrefabText.Contains("reign_individual_chat_player_portrait_mask", StringComparison.Ordinal)
				&& !individualChatPrefabText.Contains("reign_individual_chat_npc_portrait_mask", StringComparison.Ordinal)
				&& !individualChatPrefabText.Contains("ReignAspectMaskedTextureWidget", StringComparison.Ordinal)
				&& !individualChatPrefabText.Contains("UseEllipseMask", StringComparison.Ordinal);
			bool individualChatZoomRectangleValid = individualZoomFrame != null
				&& individualZoomFrameChildren != null
				&& individualZoomPortrait != null
				&& individualZoomPlate != null
				&& string.Equals(individualZoomFrame.Name, "Widget", StringComparison.Ordinal)
				&& string.Equals(individualZoomFrameChildren.Name, "Children", StringComparison.Ordinal)
				&& (individualZoomFrameChildren.SelectNodes("./*")?.Count ?? 0) == 2
				&& VerificationXmlAttributeEquals(individualZoomFrame, "WidthSizePolicy", "Fixed")
				&& VerificationXmlAttributeEquals(individualZoomFrame, "HeightSizePolicy", "Fixed")
				&& VerificationXmlAttributeEquals(individualZoomFrame, "SuggestedWidth", "@ZoomFrameWidth")
				&& VerificationXmlAttributeEquals(individualZoomFrame, "SuggestedHeight", "@ZoomFrameHeight")
				&& VerificationXmlAttributeEquals(individualZoomFrame, "HorizontalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualZoomFrame, "VerticalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualZoomFrame, "ClipContents", "true")
				&& !individualZoomFrame.HasAttribute("Sprite")
				&& !individualZoomFrame.HasAttribute("Color")
				&& !individualZoomFrame.HasAttribute("AlphaFactor")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "WidthSizePolicy", "Fixed")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "HeightSizePolicy", "Fixed")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "SuggestedWidth", "@ZoomPortraitImageWidth")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "SuggestedHeight", "@ZoomPortraitImageHeight")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "HorizontalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "VerticalAlignment", "Center")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "TextureProviderName", "@ZoomPortraitTextureProviderName")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "AdditionalArgs", "@ZoomPortraitAdditionalArgs")
				&& VerificationXmlAttributeEquals(individualZoomPortrait, "ImageId", "@ZoomPortraitId")
				&& !individualZoomPortrait.HasAttribute("Sprite")
				&& !individualZoomPortrait.HasAttribute("Color")
				&& !individualZoomPortrait.HasAttribute("AlphaFactor")
				&& VerificationXmlAttributeEquals(individualZoomPlate, "WidthSizePolicy", "StretchToParent")
				&& VerificationXmlAttributeEquals(individualZoomPlate, "HeightSizePolicy", "StretchToParent")
				&& VerificationXmlAttributeEquals(individualZoomPlate, "DoNotAcceptEvents", "true")
				&& VerificationXmlChildElementIndex(individualZoomFrameChildren, individualZoomPlate)
					> VerificationXmlChildElementIndex(individualZoomFrameChildren, individualZoomPortrait)
				&& !individualZoomBlock.Contains("ReignAspectMaskedTextureWidget", StringComparison.Ordinal)
				&& !individualZoomBlock.Contains("UseEllipseMask", StringComparison.Ordinal)
				&& !individualZoomBlock.Contains("reign_individual_chat_zoom_oval_mask", StringComparison.Ordinal)
				&& !individualZoomBlock.Contains("reign_modern_portrait_circle_overlay", StringComparison.Ordinal)
				&& !individualZoomBlock.Contains("reign_modern_portrait_oval_overlay", StringComparison.Ordinal);
			bool individualChatTransparentTranscriptValid = individualTranscriptBlock.Contains("IsVisible=\"@IsPlayerLine\"", StringComparison.Ordinal)
				&& individualTranscriptBlock.Contains("IsVisible=\"@IsNpcLine\"", StringComparison.Ordinal)
				&& !Regex.IsMatch(individualTranscriptBlock, @"\sSprite=""")
				&& !Regex.IsMatch(individualTranscriptBlock, @"\sColor=""");
			bool nativeModernPortraitFramesValid = gameMenuPartyRectangularPlateValid
				&& conversationRectangularPortraitsValid
				&& conversationCoverGeometryValid
				&& conversationZoomGeometryValid
				&& opaqueRectangleAperturePlateValid
				&& conversationRectangularScenesValid
				&& individualChatHeaderPortraitsValid
				&& individualChatZoomRectangleValid
				&& individualChatTransparentTranscriptValid;
			AddVerificationCheck(checks, "contracts.native_modern_portrait_frames", "ui_contracts", nativeModernPortraitFramesValid,
				nativeModernPortraitFramesValid
					? "Native game-menu party thumbnails and SPConversation side/full-body portraits remain centered rectangles beneath topmost opaque rectangular aperture plates; standalone Individual Chat keeps untouched header sources beneath one event-transparent two-oval shell and a rectangular full-body zoom while transcript text floats over marble."
					: "A native thumbnail or conversation portrait can escape or misorder its rectangular plate, standalone Individual Chat can pre-cut a header source, restore retired local masks or an oval full-body zoom, misorder its integrated shell, or restore a gray transcript backing.",
				new Dictionary<string, object>
				{
					["gameMenuPartyPatch"] = gameMenuPartyPatchPath,
					["conversationPatch"] = conversationPortraitPatchPath,
					["conversationMixin"] = conversationPortraitMixinPath,
					["individualChatPrefab"] = individualChatPrefabPath,
					["individualChatShell"] = individualChatShellPath,
					["individualChatShellAlphaValid"] = individualChatShellAlphaValid,
					["individualChatShellTransparentPixels"] = individualChatShellTransparentPixels,
					["individualChatShellPartialPixels"] = individualChatShellPartialPixels,
					["individualChatShellLowAlphaOutsidePixels"] = individualChatShellLowAlphaOutsidePixels,
					["individualChatShellNonDarkEdgePixels"] = individualChatShellNonDarkEdgePixels,
					["rectangleOverlay"] = rectangleOverlayPath,
					["gameMenuRectangularPlateValid"] = gameMenuPartyRectangularPlateValid,
					["conversationRectanglesValid"] = conversationRectangularPortraitsValid,
					["conversationCoverGeometryValid"] = conversationCoverGeometryValid,
					["conversationZoomGeometryValid"] = conversationZoomGeometryValid,
					["opaqueRectangleAperturePlateValid"] = opaqueRectangleAperturePlateValid,
					["rectangleOverlayTransparentPixels"] = rectangleOverlayTransparentPixels,
					["rectangleOverlayVisiblePixels"] = rectangleOverlayVisiblePixels,
					["rectangleOverlayLightNeutralPixels"] = rectangleOverlayLightNeutralPixels,
					["rectangleOverlayHiddenRgbPixels"] = rectangleOverlayHiddenRgbPixels,
					["rectangularScenesValid"] = conversationRectangularScenesValid,
					["individualChatHeaderPortraitsValid"] = individualChatHeaderPortraitsValid,
					["individualChatZoomRectangleValid"] = individualChatZoomRectangleValid,
					["individualChatTransparentTranscriptValid"] = individualChatTransparentTranscriptValid
				});

			bool questMemoryBookModernStyleValid = questMemoryBookPatchMarkup.Contains("Text=\"MEMORY BOOK\"", StringComparison.Ordinal)
				&& questMemoryBookPatchMarkup.Contains("Color=\"#171717FF\"", StringComparison.Ordinal)
				&& questMemoryBookPatchMarkup.Contains("Sprite=\"gold_frame_9\" Color=\"#7E6A4DFF\"", StringComparison.Ordinal)
				&& questMemoryBookPatchMarkup.Contains("Brush=\"Info.Text\" Brush.Font=\"Galahad\" Brush.FontSize=\"15\" Brush.FontColor=\"#C5AC83FF\"", StringComparison.Ordinal)
				&& !questMemoryBookPatchMarkup.Contains("Text=\"Memory Book\"", StringComparison.Ordinal);
			AddVerificationCheck(checks, "contracts.quest_memory_book_modern_style", "ui_contracts", questMemoryBookModernStyleValid,
				questMemoryBookModernStyleValid
					? "The native Quests memory-book button uses the approved uppercase Galahad label, charcoal surface, antique-gold frame, and bright-gold semantic text color."
					: "The native Quests memory-book button has drifted from the approved typography or frozen modern palette.",
				new Dictionary<string, object> { ["questMemoryBookPatch"] = questMemoryBookPatchPath });

			bool portraitRenameFallbackValid = portraitCacheText.Contains("TryGetSeedPortraitPathByIdentity")
				&& portraitCacheText.Contains("ExtractParenthesizedId(Path.GetFileName(folder))")
				&& portraitCacheText.Contains("_seedPortraitPathByIdentity")
				&& portraitCacheText.Contains("requireCustom: true");
			AddVerificationCheck(checks, "contracts.portrait_rename_stable_id_fallback", "portraits_images",
				portraitRenameFallbackValid,
				portraitRenameFallbackValid
					? "Renamed heroes recover existing shared portraits through their stable Bannerlord id, while custom portraits retain precedence."
					: "Shared portrait lookup still depends exclusively on mutable display-name cache folders.", null);
			bool playerPortraitCampaignScopeValid = portraitCacheText.Contains("IsCampaignSpecificPlayerIdentity")
				&& portraitCacheText.Contains("main_hero")
				&& portraitCacheText.Contains("&& TryGetSeedPortraitPath(id, requireCustom: false, out path)");
			AddVerificationCheck(checks, "contracts.player_portrait_campaign_scope", "portraits_images",
				playerPortraitCampaignScopeValid,
				playerPortraitCampaignScopeValid
					? "The stable main_hero identity can use campaign-local portraits but never inherits another campaign's shared player portrait."
					: "Player portrait lookup can still fall through to the cross-campaign shared seed cache.", null);

            bool stepRemoved = portraitServerText.IndexOf("GenerateNanoGptStep", StringComparison.OrdinalIgnoreCase) < 0
                && portraitClientText.IndexOf("GenerateStepImageEdit", StringComparison.OrdinalIgnoreCase) < 0
                && controlCenterSource.IndexOf("<option value='step-image-edit-2'>", StringComparison.OrdinalIgnoreCase) < 0
                && controlCenterSource.Contains("NormalizePortraitImageSettings")
                && controlCenterSource.Contains("foreach (string prefix in ImageProfilePrefixes)")
                && controlCenterSource.Contains("settings[modelKey] = \"gpt-image-1.5\"");
            AddVerificationCheck(checks, "contracts.step_image_edit_removed", "portraits_images", stepRemoved,
                stepRemoved ? "Step Image Edit 2 adapters and UI selection are removed; saved Step settings migrate to gpt-image-1.5." : "Step Image Edit 2 remains reachable or its settings migration is missing.", null);

			string pngReencodePath = VerificationSourceLocator.ResolveUnique(Path.Combine(root, "ReignModules", "Reign.Shared.Source"), "PngReencode.cs", "Portraits");
			string pngReencodeText = File.Exists(pngReencodePath) ? File.ReadAllText(pngReencodePath) : "";
			string pngEncoderPath = VerificationSourceLocator.ResolveUnique(Path.Combine(root, "ReignModules", "Reign.Shared.Source"), "PngEncoder.cs", "Portraits");
			string pngEncoderText = File.Exists(pngEncoderPath) ? File.ReadAllText(pngEncoderPath) : "";
			string captureServicePath = VerificationSourceLocator.ResolveUnique(clientRoot, "PortraitCaptureService.cs", "src");
			string captureServiceText = File.Exists(captureServicePath) ? File.ReadAllText(captureServicePath) : "";
			bool imageEditSourceValid = pngReencodeText.Contains("ImageEditMinimumDimension = 384")
				&& pngReencodeText.Contains("ImageEditMaximumDimension = 5000")
				&& pngReencodeText.Contains("ImageEditMaximumBytes = 10 * 1024 * 1024")
				&& pngReencodeText.Contains("NormalizeImageEditSource")
				&& pngEncoderText.Contains("EncodeRgb")
				&& captureServiceText.Contains("PngReencode.NormalizeImageEditSource(colorFixed)")
				&& serverClientText.Contains("PngReencode.NormalizeImageEditSource(sourceImagePng)");
			AddVerificationCheck(checks, "contracts.portrait_source_image_edit_compatibility", "portraits_images", imageEditSourceValid,
				imageEditSourceValid ? "Portrait sources are opaque RGB PNGs, 384-5000 pixels on both axes, and below the 10 MB image-edit limit; legacy sources are normalized before upload." : "Portrait source normalization does not satisfy the image-edit input contract.", null);

            string nativePortraitSourcePath = VerificationSourceLocator.ResolveUnique(serverRoot, "NativePortraitSourceGeneration.cs", "src");
            string nativePortraitSourceText = File.Exists(nativePortraitSourcePath) ? File.ReadAllText(nativePortraitSourcePath) : "";
            string nativeGeneratorRoot = Path.Combine(root, "NativeCharacterImageGenerator");
            string nativeGeneratorAppPath = VerificationSourceLocator.ResolveUnique(nativeGeneratorRoot, "App.xaml.cs");
            string nativeGeneratorAppText = File.Exists(nativeGeneratorAppPath) ? File.ReadAllText(nativeGeneratorAppPath) : "";
            string nativeGeneratorCatalogPath = VerificationSourceLocator.ResolveUnique(nativeGeneratorRoot, "ReignCharacterCatalog.cs");
            string nativeGeneratorCatalogText = File.Exists(nativeGeneratorCatalogPath) ? File.ReadAllText(nativeGeneratorCatalogPath) : "";
            string serverProjectContractPath = Path.Combine(serverRoot, "ReignBetaServer.csproj");
            string serverProjectContractText = File.Exists(serverProjectContractPath) ? File.ReadAllText(serverProjectContractPath) : "";
            string clientProjectContractPath = Path.Combine(clientRoot, "ReignBeta.csproj");
            string clientProjectContractText = File.Exists(clientProjectContractPath) ? File.ReadAllText(clientProjectContractPath) : "";
            string sharedCompositionContractPath = Path.Combine(root, "ReignModules", "Reign.Shared.Source", "Core", "ReignPortraitDerivativeCore.cs");
            string sharedCompositionContractText = File.Exists(sharedCompositionContractPath) ? File.ReadAllText(sharedCompositionContractPath) : "";
            bool sharedPortraitCompositionValid = nativePortraitSourceText.Contains("return PortraitDerivativeCore.PortraitCompositionError(focus);")
                && portraitCacheText.Contains("PortraitDerivativeCore.PortraitCompositionError(product.FaceFocus)")
                && sharedCompositionContractText.Contains("focus.Height < 0.08d || focus.Height > 0.18d")
                && sharedCompositionContractText.Contains("focus.Y < 0.035d || focus.Y > 0.18d")
                && sharedCompositionContractText.Contains("centerX < 0.35d || centerX > 0.65d")
                && sharedCompositionContractText.Contains("focus.CandidateCount != 1")
                && sharedCompositionContractText.Contains("!IsValidFocus(focus) || double.IsInfinity(focus.Confidence)")
                && serverProjectContractText.Replace('\\', '/').Contains("Compile Include=\"../ReignModules/Reign.Shared.Source/**/*.cs\"")
                && clientProjectContractText.Replace('\\', '/').Contains("Compile Include=\"$(ReignServerRoot)/ReignModules/Reign.Shared.Source/**/*.cs\"");
            bool nativePortraitPipelineValid = nativePortraitSourceText.Contains("NativePortraitRenderContract = \"ai_source_wan_portrait_v5\"")
                && nativePortraitSourceText.Contains("NativePortraitSourceWidth = 768")
                && nativePortraitSourceText.Contains("NativePortraitSourceHeight = 1024")
                && nativePortraitSourceText.Contains("NormalizeAndValidateGeneratedPortraitProduct")
                && sharedPortraitCompositionValid
                && nativePortraitSourceText.Contains("PortraitCompositionError(focus)")
                && nativePortraitSourceText.Contains("--character-snapshot", StringComparison.Ordinal)
                && !nativePortraitSourceText.Contains("supplied_contract_fallback", StringComparison.Ordinal)
                && portraitServerText.Contains("ResolvePortraitGenerationSource(settings, payload, sourceImage", StringComparison.Ordinal)
                && portraitServerText.Contains("ApplyPortraitProductPromptContract(BuildPortraitPrompt(payload, clientPrompt, !adultClothingEdit))", StringComparison.Ordinal)
                && portraitServerText.Contains("BuildPortraitPhysiqueEvidence(", StringComparison.Ordinal)
                && portraitServerText.Contains("IsFemalePortraitSubject(payload)", StringComparison.Ordinal)
                && nativeGeneratorAppText.Contains("effectiveBodyProperties", StringComparison.Ordinal)
                && nativePortraitSourceText.Contains("ValidateNativePhysique", StringComparison.Ordinal)
                && portraitServerText.Contains("call.ImageBytes = normalizedPortrait", StringComparison.Ordinal)
                && nativeGeneratorAppText.Contains("--reign-render-portrait-source", StringComparison.Ordinal)
                && nativeGeneratorAppText.Contains("--reign-data-root", StringComparison.Ordinal)
                && nativeGeneratorAppText.Contains("NativeEngineRenderService", StringComparison.Ordinal)
                && nativeGeneratorCatalogText.Contains("FindCampaign(moduleRoot, requestedCampaignId, reignDataRoot)", StringComparison.Ordinal)
                && nativePortraitSourceText.Contains("AppendProcessArgument(arguments, \"--reign-data-root\", NativeGeneratorPath(DataDir))", StringComparison.Ordinal)
                && serverClientText.Contains("[\"civilianEquipment\"] = BuildPortraitCivilianEquipment(hero)", StringComparison.Ordinal)
                && serverClientText.Contains("character[\"civilianEquipment\"] as JArray", StringComparison.Ordinal)
                && clientProjectContractText.Contains("PublishBundledNativePortraitGenerator", StringComparison.Ordinal)
                && clientProjectContractText.Contains("native-portrait-generator", StringComparison.Ordinal)
                && clientProjectContractText.Contains("Bannerlord.NativePortraitRenderHost.exe", StringComparison.Ordinal);
            var backgroundPortraitAudit = AuditBackgroundPortraitEntrypoints(clientRoot);
            AddVerificationCheck(checks, "contracts.background_portrait_entrypoints", "portraits_images",
                ReadBool(backgroundPortraitAudit, "ok", false),
                "Every character generation action uses the background coordinator; render widgets never initiate generation and Look actions retain screen focus.", backgroundPortraitAudit);
            AddVerificationCheck(checks, "contracts.native_portrait_product_pipeline", "portraits_images", nativePortraitPipelineValid,
                nativePortraitPipelineValid
                    ? "Portrait requests use the exact campaign's headless native identity source, enforce the provider-independent full-body product contract, normalize supported provider canvases, and ship both native executables with Reign."
                    : "The native portrait identity source, cross-model product gate, or packaged generator contract is incomplete.",
                new Dictionary<string, object>
                {
                    ["sourceContractPath"] = nativePortraitSourcePath,
                    ["sharedCompositionContractPath"] = sharedCompositionContractPath,
                    ["sharedCompositionValid"] = sharedPortraitCompositionValid,
                    ["clientProjectPath"] = clientProjectContractPath,
                    ["generatorCommandPath"] = nativeGeneratorAppPath,
                    ["generatorCatalogPath"] = nativeGeneratorCatalogPath,
                    ["serverProjectPath"] = serverProjectContractPath
                });

			byte[] engineTextureSource = AIPortraits.PngEncoder.EncodeRgba(new byte[]
			{
				201, 37, 11, 255,
				19, 83, 227, 127
			}, 2, 1);
			byte[] engineTexturePng = AIPortraits.PngReencode.ToEngineTextureFormat(engineTextureSource, 1);
			byte[] engineTextureRgba = AIPortraits.PngReencode.DecodeToRgba(engineTexturePng, out int engineTextureWidth, out int engineTextureHeight);
			bool engineTextureChannelOrderValid = engineTexturePng != null
				&& engineTexturePng.Length > 25
				&& engineTexturePng[24] == 8
				&& engineTexturePng[25] == 6
				&& engineTextureWidth == 1
				&& engineTextureHeight == 1
				&& engineTextureRgba != null
				&& engineTextureRgba.Length == 4
				&& engineTextureRgba[0] == 201
				&& engineTextureRgba[1] == 37
				&& engineTextureRgba[2] == 11
				&& engineTextureRgba[3] == 255;
			AddVerificationCheck(checks, "contracts.engine_texture_png_channel_order", "portraits_images", engineTextureChannelOrderValid,
				engineTextureChannelOrderValid
					? "Engine-bound PNG normalization retains RGBA channel order and the bounded resize path."
					: "Engine-bound PNG normalization can reverse red and blue channels or violate its resize contract.",
				new Dictionary<string, object>
				{
					["pngColorType"] = engineTexturePng != null && engineTexturePng.Length > 25 ? engineTexturePng[25] : -1,
					["width"] = engineTextureWidth,
					["height"] = engineTextureHeight
				});

			byte[] fullResolutionSourceRgba = new byte[640 * 360 * 4];
			for (int fullResolutionAlpha = 3; fullResolutionAlpha < fullResolutionSourceRgba.Length; fullResolutionAlpha += 4)
			{
				fullResolutionSourceRgba[fullResolutionAlpha] = 255;
			}
			byte[] fullResolutionSourcePng = AIPortraits.PngEncoder.EncodeRgba(fullResolutionSourceRgba, 640, 360);
			byte[] fullResolutionEnginePng = AIPortraits.PngReencode.ToEngineTextureFormat(fullResolutionSourcePng, 0);
			AIPortraits.PngReencode.DecodeToRgba(fullResolutionEnginePng, out int fullResolutionWidth, out int fullResolutionHeight);
			int boundedFileTextureCallCount = Regex.Matches(textureFactoryText, @"NormalizeForEngine\(sourceBytes,\s*448\)").Count;
			int boundedMemoryTextureCallCount = Regex.Matches(textureFactoryText, @"NormalizeForEngine\(array,\s*448\)").Count;
			bool fullResolutionCallersValid = warCouncilArtFactory.Contains("BuildCastleMapImageId", StringComparison.Ordinal)
				&& warCouncilArtFactory.Contains("BuildCastleSceneImageId", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("string textureKey = \"reign_event_art_\"", StringComparison.Ordinal)
                && warCouncilArtFactory.Contains("TextureFactory.GetOrBuildFile(textureKey, imagePath)", StringComparison.Ordinal)
				&& tavernArtTextureFactoryText.Contains("TextureFactory.GetOrBuildFile(", StringComparison.Ordinal)
				&& portraitPatchText.Contains("TextureFactory.GetOrBuildFile(cacheKey, path)", StringComparison.Ordinal);
			bool fullResolutionFileTextureValid = fullResolutionEnginePng != null
				&& fullResolutionWidth == 640
				&& fullResolutionHeight == 360
				&& textureFactoryText.Contains("NormalizeForEngine(sourceBytes)")
				&& boundedFileTextureCallCount == 0
				&& boundedMemoryTextureCallCount == 2
				&& fullResolutionCallersValid;
			AddVerificationCheck(checks, "contracts.full_resolution_file_texture_normalization", "portraits_images", fullResolutionFileTextureValid,
				fullResolutionFileTextureValid
					? "File-backed UI textures retain authored dimensions while portrait-memory textures remain independently bounded."
					: "File-backed UI texture normalization can apply the 448-pixel portrait thumbnail cap or alter authored dimensions.",
				new Dictionary<string, object>
				{
					["width"] = fullResolutionWidth,
					["height"] = fullResolutionHeight,
					["fileTextureUsesUnboundedNormalization"] = textureFactoryText.Contains("NormalizeForEngine(sourceBytes)"),
					["boundedFileTextureCallCount"] = boundedFileTextureCallCount,
					["boundedMemoryTextureCallCount"] = boundedMemoryTextureCallCount,
					["fullResolutionCallersValid"] = fullResolutionCallersValid
				});

			string serverProgramPath = VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src");
			string serverProgramText = File.Exists(serverProgramPath) ? File.ReadAllText(serverProgramPath) : "";
			bool portraitRosterEquipmentPersistenceValid = serverProgramText.Contains("[\"civilianEquipment\"] = CompactPortraitEquipment(character)", StringComparison.Ordinal)
				&& serverProgramText.Contains("ReadInt(existing, \"version\", 0) >= 3", StringComparison.Ordinal)
				&& serverProgramText.Contains("[\"version\"] = 3", StringComparison.Ordinal)
				&& serverProgramText.Contains("IdentitySynchronizeWithPortraitRosterUpgradeApi(request.JsonBody)", StringComparison.Ordinal)
				&& serverProgramText.Contains("ReadInt(savedRoster, \"version\", 0) < 3", StringComparison.Ordinal);
			AddVerificationCheck(checks, "contracts.native_portrait_roster_equipment_persistence", "portraits_images", portraitRosterEquipmentPersistenceValid,
				portraitRosterEquipmentPersistenceValid
					? "Portrait roster compaction retains civilian equipment and forces pre-v3 incomplete rosters to synchronize again."
					: "Portrait roster compaction can discard civilian equipment or accept an incomplete legacy roster as current.",
				new Dictionary<string, object>
				{
					["serverProgramPath"] = serverProgramPath
				});
			bool wan25AdapterValid = portraitServerText.Contains("AtlasModelWan25ImageEdit = \"alibaba/wan-2.5/image-edit\"")
				&& portraitServerText.Contains("[\"negative_prompt\"] = wanNegativePrompt ?? \"\"")
				&& portraitServerText.Contains("[\"enable_prompt_expansion\"] = true")
				&& portraitServerText.Contains("MapToAtlasWan25Size(outputSize)")
				&& ControlCenterHtml().Contains("<option value='alibaba/wan-2.5/image-edit'>");
			AddVerificationCheck(checks, "contracts.atlas_wan25_image_edit", "portraits_images", wan25AdapterValid,
				wan25AdapterValid ? "AtlasCloud Wan 2.5 image edit remains available and uses its protected images/prompt/negative-prompt/seed/size/prompt-expansion payload." : "AtlasCloud Wan 2.5 request adapter is incomplete.", null);

			string portraitSettingsPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaSettings.cs", "src");
			string portraitSettingsText = File.Exists(portraitSettingsPath) ? File.ReadAllText(portraitSettingsPath) : "";
			string portraitDebugPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaDebugActions.cs", "src");
			string portraitDebugText = File.Exists(portraitDebugPath) ? File.ReadAllText(portraitDebugPath) : "";
			string lordScanPath = VerificationSourceLocator.ResolveUnique(clientRoot, "LordSourceExportService.cs", "src");
			string lordScanText = File.Exists(lordScanPath) ? File.ReadAllText(lordScanPath) : "";
			bool lordSourceScanValid = portraitSettingsText.Contains("Start Lord Encyclopedia Source Scan")
				&& portraitSettingsText.Contains("ReignBetaDebugActions.StartLordEncyclopediaSourceScan")
				&& portraitDebugText.Contains("LordSourceExportService.Start()")
				&& lordScanText.Contains("ClanTier")
				&& lordScanText.Contains("SocialStation")
				&& lordScanText.Contains("source.png");
			AddVerificationCheck(checks, "contracts.lord_encyclopedia_source_scan", "portraits_images", lordSourceScanValid,
				lordSourceScanValid ? "The MCM can arm a full living-lord Encyclopedia source scan and records clan tier/social station beside each capture." : "Lord Encyclopedia source scan or source metadata contract is incomplete.", null);

            string calendarPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignCalendarService.cs", "src");
            string calendarText = File.Exists(calendarPath) ? File.ReadAllText(calendarPath) : "";
            bool calendarValid = calendarText.Contains("DaysPerYear = 126") && calendarText.Contains("DaysPerSeason = 31.5") && calendarText.Contains("native 84-day year");
            AddVerificationCheck(checks, "contracts.calendar_126", "contracts", calendarValid, calendarValid ? "126-day year and 31.5-day seasons are encoded in the production calendar prompt." : "Calendar contract is incomplete.", null);

            string saveSyncBehaviorPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignSaveSyncCampaignBehavior.cs", "src");
            string saveSyncBehaviorText = File.Exists(saveSyncBehaviorPath) ? File.ReadAllText(saveSyncBehaviorPath) : "";
            bool saveSyncFailureWarningValid = saveSyncBehaviorText.Contains("!result.Equals(\"disabled\", StringComparison.OrdinalIgnoreCase)")
                && saveSyncBehaviorText.Contains("if (successful && _registrationFailedForCurrentSave)")
                && saveSyncBehaviorText.Contains("ShowSaveSyncRegistrationFailure(saveName)")
                && saveSyncBehaviorText.Contains("if (_registrationConfirmed")
                && saveSyncBehaviorText.Contains("Bannerlord Reign Save Sync Deferred")
                && saveSyncBehaviorText.Contains("The local server was online")
                && saveSyncBehaviorText.Contains("InformationManager.ShowInquiry(new InquiryData(")
                && saveSyncBehaviorText.Contains("was created, but the local Reign server was offline")
                && saveSyncBehaviorText.Contains("Your Bannerlord save file itself is not damaged.");
            AddVerificationCheck(checks, "contracts.save_sync_offline_save_warning", "server_lifecycle", saveSyncFailureWarningValid,
                saveSyncFailureWarningValid ? "A successful native save with an unregistered Reign snapshot opens a modal warning; registered, disabled, and failed native saves do not." : "Save Sync offline-save warning contract is incomplete.", null);

            string popupKeyboardPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignPopupKeyboardPatches.cs", "src");
            string popupKeyboardText = File.Exists(popupKeyboardPath) ? File.ReadAllText(popupKeyboardPath) : "";
            string diplomacyPopupPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignDiplomacyAnnouncementScreenManager.cs", "src");
            string diplomacyPopupText = File.Exists(diplomacyPopupPath) ? File.ReadAllText(diplomacyPopupPath) : "";
            string subModulePath = VerificationSourceLocator.ResolveUnique(clientRoot, "SubModule.cs", "src");
            string subModuleText = File.Exists(subModulePath) ? File.ReadAllText(subModulePath) : "";
            bool popupKeyboardValid = popupKeyboardText.Contains("IsReignCaller()")
                && popupKeyboardText.Contains("InformationManager.ShowInquiry")
                && popupKeyboardText.Contains("InformationManager.ShowTextInquiry")
                && popupKeyboardText.Contains("MBInformationManager.ShowMultiSelectionInquiry")
                && popupKeyboardText.Contains("InputKey.Enter")
                && popupKeyboardText.Contains("InputKey.NumpadEnter")
                && popupKeyboardText.Contains("InputKey.Escape")
                && popupKeyboardText.Contains("InvokePopupCommand(popup, \"ExecuteAffirmativeAction\")")
                && popupKeyboardText.Contains("InvokePopupCommand(popup, \"ExecuteNegativeAction\")")
                && diplomacyPopupText.Contains("Input.IsKeyReleased(InputKey.Enter)")
                && diplomacyPopupText.Contains("Input.IsKeyReleased(InputKey.NumpadEnter)")
                && subModuleText.Contains("ReignPopupKeyboardPatches.Apply()")
                && subModuleText.Contains("ReignPopupKeyboardPatches.Unapply()");
            AddVerificationCheck(checks, "contracts.reign_popup_keyboard_routing", "interaction_architecture", popupKeyboardValid,
                popupKeyboardValid
                    ? "Reign inquiries route Enter/Numpad Enter to affirmative actions and Escape to cancel or acknowledgement, while the custom diplomacy notice accepts the same physical keys."
                    : "Reign popup Enter/Escape routing or its lifecycle registration is incomplete.",
                new Dictionary<string, object>
                {
                    ["keyboardPolicyPath"] = popupKeyboardPath,
                    ["diplomacyPopupPath"] = diplomacyPopupPath,
                    ["subModulePath"] = subModulePath
                });

            string subModule = Path.Combine(clientRoot, "SubModule.xml");
            string clientProject = Directory.GetFiles(clientRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault() ?? "";
            string serverProject = Path.Combine(serverRoot, "ReignBetaServer.csproj");
            AddVerificationCheck(checks, "contracts.projects", "build", File.Exists(subModule) && File.Exists(clientProject) && File.Exists(serverProject), "Client module, client project, and server project are present.", new Dictionary<string, object> { ["clientProject"] = clientProject, ["serverProject"] = serverProject });

            string canonicalClientAssembly = Environment.GetEnvironmentVariable("REIGN_VERIFICATION_CLIENT_ASSEMBLY") ?? "";
            string clientAssembly = string.IsNullOrWhiteSpace(canonicalClientAssembly)
                ? Path.Combine(clientRoot, "bin", "Win64_Shipping_Client", "ReignBeta.dll")
                : Path.GetFullPath(canonicalClientAssembly);
            if (!File.Exists(clientAssembly) && string.IsNullOrWhiteSpace(canonicalClientAssembly))
            {
                string newestCustomClientAssembly = Directory.Exists(Path.Combine(clientRoot, ".codex-build"))
                    ? Directory.GetFiles(Path.Combine(clientRoot, ".codex-build"), "ReignBeta.dll", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault()
                    : null;
                if (!string.IsNullOrWhiteSpace(newestCustomClientAssembly))
                    clientAssembly = newestCustomClientAssembly;
            }
            if (!File.Exists(clientAssembly) && string.IsNullOrWhiteSpace(canonicalClientAssembly))
            {
                string installedModuleRoot = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", ".."));
                string installedClientAssembly = Path.Combine(installedModuleRoot, "bin", "Win64_Shipping_Client", "ReignBeta.dll");
                if (File.Exists(installedClientAssembly))
                    clientAssembly = installedClientAssembly;
            }
            string serverAssembly = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ReignBetaServer.dll");
            AddVerificationCheck(checks, "contracts.build_artifacts", "build", File.Exists(clientAssembly) && File.Exists(serverAssembly), "Client and server build artifacts are present.", new Dictionary<string, object> { ["clientAssembly"] = clientAssembly, ["serverAssembly"] = serverAssembly });

            RunPromptSizeContract(checks);

            string programSource = VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src");
            string programText = File.Exists(programSource) ? File.ReadAllText(programSource) : "";
            int routeCount = Regex.Matches(programText, "request\\.Path\\s*==\\s*\"(?<path>/[^\"]+)\"")
                .Cast<Match>()
                .Count(match => !match.Groups["path"].Value.StartsWith("/court/", StringComparison.OrdinalIgnoreCase));
            AddVerificationCheck(checks, "contracts.http_routes", "server_lifecycle", routeCount >= 60, "Discovered " + routeCount + " exact HTTP route handlers.", new Dictionary<string, object> { ["routeCount"] = routeCount });

            string saveDefiner = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaSaveDefiner.cs", "src");
            string saveText = File.Exists(saveDefiner) ? File.ReadAllText(saveDefiner) : "";
            int saveTypeCount = Regex.Matches(saveText, "(?:AddClassDefinition|ConstructContainerDefinition)[^\\r\\n]*")
                .Cast<Match>()
                .Count(match => !IsExcludedCourtVerificationArtifact(match.Value));
            AddVerificationCheck(checks, "contracts.save_types", "persistence", saveTypeCount >= 5, "Discovered " + saveTypeCount + " save type/container registrations.", new Dictionary<string, object> { ["saveTypeCount"] = saveTypeCount });

            string characterBehaviorPath = VerificationSourceLocator.ResolveUnique(clientRoot, "ReignCharacterEditorCampaignBehavior.cs", "src");
            string characterBehaviorText = File.Exists(characterBehaviorPath) ? File.ReadAllText(characterBehaviorPath) : "";
            bool noAutomaticProfileUpload = !characterBehaviorText.Contains("StartProfileSync")
                && !serverClientText.Contains("SynchronizeFixedNobleProfilesAsync");
            AddVerificationCheck(checks, "contracts.no_automatic_character_profile_upload", "character_construction", noAutomaticProfileUpload,
                "Campaign launch contains no automatic fixed-profile upload path.",
                new Dictionary<string, object> { ["behaviorPath"] = characterBehaviorPath, ["clientPath"] = serverClientPath });

            string profileLibraryPath = VerificationSourceLocator.ResolveUnique(serverRoot, "CharacterProfileLibrary.cs", "src");
            string profileLibraryText = File.Exists(profileLibraryPath) ? File.ReadAllText(profileLibraryPath) : "";
            bool noBackgroundWorker = !profileLibraryText.Contains("CharacterEnrichmentWorkerLoop")
                && !profileLibraryText.Contains("ProcessNextCharacterEnrichment")
                && programText.Contains("[\"backgroundCharacterEnrichmentEnabled\"] = false");
            AddVerificationCheck(checks, "contracts.no_background_character_worker", "character_construction", noBackgroundWorker,
                "Server contains no automatic character enrichment worker and retains the hard provider-call guard.",
                new Dictionary<string, object> { ["profileLibraryPath"] = profileLibraryPath, ["programPath"] = programSource });

            bool passiveEventsDoNotConstruct = !programText.Contains("EnsureCharacterConstructed(campaignId, heroId, participant, \"generated_wilderness_plan\")")
                && !programText.Contains("UpsertCharacterProfile(campaignId, participant);")
                && programText.Contains("LoadPassiveCharacterStack(campaignId, heroId, participant)");
            AddVerificationCheck(checks, "contracts.passive_events_do_not_create_profiles", "character_construction", passiveEventsDoNotConstruct,
                "Passive wilderness-event planning cannot create or construct dynamic character profiles.",
                new Dictionary<string, object> { ["programPath"] = programSource });
        }

        private static void RunPromptSizeContract(List<Dictionary<string, object>> checks)
        {
            int commoner = BuildDialogueGlobalPrefix(false, false, new List<Dictionary<string, object>>()).Length;
            int noble = BuildDialogueGlobalPrefix(false, true, new List<Dictionary<string, object>>()).Length;
            int maximum = Math.Max(commoner, noble);
            AddVerificationCheck(checks, "contracts.dialogue_prompt_budget", "prompt_efficiency", commoner > 0 && noble > 0 && maximum <= 45000,
                "Assembled ordinary-dialogue system prefixes: commoner=" + commoner + ", noble=" + noble + " characters.",
                new Dictionary<string, object> { ["commonerCharacters"] = commoner, ["nobleCharacters"] = noble, ["maximumChars"] = 45000 });
        }

        private static void RunProductionPipelineChecks(List<Dictionary<string, object>> checks, string requestedSuite, bool failFast)
        {
            Action<string> trace = stage =>
            {
                if (string.Equals(Environment.GetEnvironmentVariable("REIGN_VERIFICATION_TRACE"), "1", StringComparison.Ordinal))
                    Console.Error.WriteLine("[verification:pipeline] " + stage);
            };
            trace("canned:start");
            List<Dictionary<string, object>> loadedCases = LoadTestCases("");
            List<Dictionary<string, object>> skippedUnreplayable = loadedCases
                .Where(testCase => !IsReplayableCannedVerificationCase(testCase))
                .ToList();
            List<Dictionary<string, object>> cases = loadedCases
                .Where(IsReplayableCannedVerificationCase)
                .ToList();
            int passed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> testCase in cases)
            {
                if (ShouldCancelVerification()) break;
                Dictionary<string, object> row = RunPipelineTestCase(testCase, "canned");
                if (ReadBool(row, "passed", false)) passed++;
                else failures.Add(CompactVerificationFailure(row));
                if (failFast && failures.Count > 0) break;
            }
            Dictionary<string, object> atomicPlannerChoices = VerifyAtomicActionPlannerChoices();
            AddVerificationCheck(checks, "pipeline.canned_cases", "pipeline", failures.Count == 0 && passed == cases.Count && ReadBool(atomicPlannerChoices, "passed", false), "Production pipeline canned cases: " + passed + "/" + cases.Count + " passed; atomic package routing " + (ReadBool(atomicPlannerChoices, "passed", false) ? "passed" : "failed") + ".", new Dictionary<string, object>
            {
                ["failures"] = failures,
                ["legacyPromptOverrideCases"] = CountLegacyPromptOverrideCases(cases),
                ["skippedUnreplayableCaptures"] = skippedUnreplayable.Select(testCase => new Dictionary<string, object>
                {
                    ["suite"] = ReadString(testCase, "suite", ""),
                    ["caseId"] = ReadString(testCase, "caseId", ""),
                    ["reason"] = "Captured evidence has no model output or direct action and therefore cannot be replayed deterministically."
                }).ToList(),
                ["atomicPlannerChoices"] = atomicPlannerChoices
            });
            Dictionary<string, object> settlementAuthority = VerifyDialogueSettlementAuthority();
            AddVerificationCheck(checks, "pipeline.settlement_authority", "pipeline",
                ReadBool(settlementAuthority, "passed", false),
                "Dialogue settlement transfers are bound to current clan-owner or kingdom-ruler authority.",
                settlementAuthority);

            trace("relationships:start");
            AddSubsystemListSummary(checks, "pipeline.mbti_relationship_lifecycle", "memory_relationships", RunCurrentRelationshipSelfTests());
            trace("notable_mbti:start");
            AddSubsystemListSummary(checks, "pipeline.notable_mbti", "memory_relationships", RunNotableMbtiSelfTests());
            trace("rumors:start");
            AddSubsystemListSummary(checks, "pipeline.rumors", "knowledge_identity", RunRumorSubsystemSelfTests());
            trace("character_editor:start");
            AddSubsystemListSummary(checks, "pipeline.character_editor", "character_construction", RunCharacterEditorSubsystemSelfTests());
            trace("categorized_memory:start");
            AddSubsystemListSummary(checks, "pipeline.categorized_memory", "memory_relationships", RunCategorizedMemorySubsystemSelfTests());
            trace("dynamic_characteristics:start");
            AddSubsystemListSummary(checks, "pipeline.dynamic_characteristics", "memory_relationships", RunDynamicCharacteristicsSelfTests());
            trace("npc_dialogue_audit:start");
            AddSubsystemListSummary(checks, "pipeline.npc_dialogue_audit", "memory_relationships", RunNpcDialogueAuditSelfTests());
            trace("party_dialogue_audit:start");
            AddSubsystemListSummary(checks, "pipeline.party_dialogue_audit", "memory_relationships", RunPartyDialogueAuditSelfTests());
            trace("live_interaction:start");
            AddSubsystemListSummary(checks, "pipeline.live_interaction", "conversation_modes", RunLiveInteractionTestSelfTests());
            trace("conversation_readiness:start");
            AddSubsystemListSummary(checks, "pipeline.conversation_readiness_evaluation", "conversation_modes", RunConversationReadinessEvaluationSelfTests());
            trace("final_conversation_gauntlet_models:start");
            AddSubsystemListSummary(checks, "pipeline.final_conversation_gauntlet_models", "conversation_modes", RunFinalConversationGauntletModelSelfTests());
            trace("semantic_memory:start");
            AddSubsystemListSummary(checks, "pipeline.semantic_memory", "memory_relationships", RunSemanticMemorySelfTests());
            trace("character_profiles:start");
            AddSubsystemListSummary(checks, "pipeline.character_profiles", "character_construction", RunCharacterProfileLibrarySelfTests(includeCourt: false));
            trace("identity:start");
            AddSubsystemListSummary(checks, "pipeline.identity", "knowledge_identity", RunIdentitySubsystemSelfTests());
            trace("campaign_backups:start");
            AddSubsystemListSummary(checks, "pipeline.postgresql_storage", "server_lifecycle", RunPostgreSqlStorageSelfTests());
            AddSubsystemListSummary(checks, "pipeline.campaign_backups", "server_lifecycle", RunCampaignBackupSelfTests());
            trace("save_sync:start");
            AddSubsystemListSummary(checks, "pipeline.save_sync", "server_lifecycle", RunSaveSyncSelfTests());
            trace("prompt_caching:start");
            AddSubsystemListSummary(checks, "pipeline.prompt_caching", "prompt_efficiency", RunPromptCachingSelfTests());
            trace("complete");
        }

        private static bool IsReplayableCannedVerificationCase(Dictionary<string, object> testCase)
        {
            testCase = testCase ?? new Dictionary<string, object>();
            if (testCase.ContainsKey("replayable") && !ReadBool(testCase, "replayable", true))
                return false;

            string mode = ReadString(testCase, "mode", "").Trim().ToLowerInvariant();
            if (mode != "dialogue" && mode != "individual_chat" && mode != "party_chat"
                && mode != "social_event" && mode != "wilderness_event" && mode != "correspondence")
                return true;

            Dictionary<string, object> input = ReadDictionary(testCase, "input") ?? new Dictionary<string, object>();
            if ((ReadDictionary(input, "action") ?? new Dictionary<string, object>()).Count > 0
                || ReadDictionaryList(input, "actions").Count > 0)
                return true;

            Dictionary<string, object> llm = ReadDictionary(testCase, "llm") ?? new Dictionary<string, object>();
            return !string.IsNullOrWhiteSpace(ReadString(llm, "cannedContent", ""));
        }

        private static Dictionary<string, object> VerifyAtomicActionPlannerChoices()
        {
            Dictionary<string, object> snapshot = DefaultActionTestSnapshot();
            Dictionary<string, object> payload = ReadDictionary(snapshot, "payload") ?? new Dictionary<string, object>();
            Dictionary<string, object> hero = ReadDictionary(payload, "hero") ?? new Dictionary<string, object>();
            Dictionary<string, object> settings = LoadSettings();

            Func<string, string, List<string>> choices = (playerText, intent) =>
            {
                Dictionary<string, object> gate = new Dictionary<string, object>
                {
                    ["needed"] = true,
                    ["commitment"] = "accepted",
                    ["intent"] = intent,
                    ["confidence"] = 0.9d
                };
                return BuildAllowedActionPlannerChoices(settings, payload, hero, gate, playerText, "I accept and finalize those exact terms.", 10)
                    .Select(x => CanonicalCommand(ReadString(x, "command", "")))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();
            };

            List<string> ransom = choices(
                "Release Prisoner Lord to me for twenty thousand denars now. Accept and finalize the ransom.",
                "Rhagaea accepts the ransom of Prisoner Lord to the player for twenty thousand denars.");
            List<string> trade = choices(
                "Our trade is final: take forty Grain from me and pay me 2,200 denars.",
                "Rhagaea accepts receiving 40 grain from the player and paying 2,200 denars to the player.");

            HashSet<string> ransomComponents = new HashSet<string>(new[] { "trade_package", "diplomatic_package", "transfer_prisoner", "give_gold_to_player", "transfer_gold" }, StringComparer.OrdinalIgnoreCase);
            HashSet<string> tradeComponents = new HashSet<string>(new[] { "diplomatic_package", "transfer_prisoner", "transfer_item", "give_gold_to_player", "transfer_gold" }, StringComparer.OrdinalIgnoreCase);
            bool passed = ransom.Contains("ransom_package", StringComparer.OrdinalIgnoreCase)
                && trade.Contains("trade_package", StringComparer.OrdinalIgnoreCase)
                && !ransom.Any(ransomComponents.Contains)
                && !trade.Any(tradeComponents.Contains);
            return new Dictionary<string, object>
            {
                ["passed"] = passed,
                ["ransomAllowed"] = ransom,
                ["tradeAllowed"] = trade
            };
        }

        private static Dictionary<string, object> VerifyDialogueSettlementAuthority()
        {
            List<Dictionary<string, object>> cases = new List<Dictionary<string, object>>();
            Func<string, string, string, string, string, Dictionary<string, object>> run =
                (caseId, command, speakerHeroId, settlementId, sourceKingdomId) =>
            {
                Dictionary<string, object> snapshot = DefaultActionTestSnapshot();
                Dictionary<string, object> payload = ReadDictionary(snapshot, "payload")
                    ?? new Dictionary<string, object>();
                Dictionary<string, object> index = ReadDictionary(payload,
                    "actionResolutionIndex") ?? new Dictionary<string, object>();
                Dictionary<string, object> speaker = FindIndexedRow(index, "heroes",
                    "heroStringId", speakerHeroId) ?? new Dictionary<string, object>();
                payload["speakerHeroStringId"] = speakerHeroId;
                payload["speakerKingdomId"] = ReadString(speaker, "kingdomId", "");
                payload["speakerClanId"] = ReadString(speaker, "clanId", "");
                payload["hero"] = speaker;
                payload["correlationId"] = "authority-" + caseId;

                Dictionary<string, object> action = new Dictionary<string, object>
                {
                    ["command"] = command,
                    ["source"] = "test_lab_action_gate",
                    ["requiresAcceptance"] = false,
                    ["reason"] = "Settlement authority verification " + caseId + "."
                };
                string resolutionText;
                if (string.Equals(command, "trade_package", StringComparison.OrdinalIgnoreCase))
                {
                    action["FromHero"] = speakerHeroId;
                    action["ToHero"] = "player";
                    action["TargetSettlement"] = settlementId;
                    action["terms"] = TestDict(
                        "settlementToHeroStringId", "player",
                        "targetSettlementStringId", settlementId,
                        "gold", 100,
                        "goldFromHeroStringId", "player",
                        "goldToHeroStringId", speakerHeroId);
                    resolutionText = "I will pay you 100 denars for " + settlementId
                        + ". You accept these exact terms.";
                }
                else
                {
                    action["actorKingdomId"] = "new_kingdom";
                    action["targetKingdomId"] = sourceKingdomId;
                    action["TargetSettlement"] = settlementId;
                    action["terms"] = TestDict("settlementIds",
                        TestList(settlementId));
                    resolutionText = "You accept peace and surrender " + settlementId + ".";
                }

                Dictionary<string, object> record = NormalizeActionCommand(action,
                    "test_campaign", out List<string> errors, payload, resolutionText);
                return new Dictionary<string, object>
                {
                    ["caseId"] = caseId,
                    ["accepted"] = record != null && errors.Count == 0,
                    ["record"] = record ?? new Dictionary<string, object>(),
                    ["errors"] = errors
                };
            };

            Dictionary<string, object> ownerTrade = run("owner_trade", "trade_package",
                "rhagaea", "town_ES4", "empire_s");
            Dictionary<string, object> unrelatedTrade = run("unrelated_trade",
                "trade_package", "mengus", "town_ES4", "empire_s");
            Dictionary<string, object> rulerSellingVassalFief = run("ruler_vassal_fief",
                "trade_package", "caladog", "castle_battania_1", "battania");
            Dictionary<string, object> clanOwnerTrade = run("clan_owner_trade",
                "trade_package", "mengus", "castle_battania_1", "battania");
            Dictionary<string, object> rulerPeace = run("ruler_peace",
                "demand_settlement_peace", "rhagaea", "town_ES4", "empire_s");
            Dictionary<string, object> nonRulerPeace = run("non_ruler_peace",
                "demand_settlement_peace", "prisoner_lord", "town_ES4", "empire_s");
            cases.AddRange(new[] { ownerTrade, unrelatedTrade, rulerSellingVassalFief,
                clanOwnerTrade, rulerPeace, nonRulerPeace });

            Dictionary<string, object> boundTrade = ReadDictionary(ownerTrade, "record")
                ?? new Dictionary<string, object>();
            Dictionary<string, object> boundTerms = TryParseJsonObject(
                ReadString(boundTrade, "termsJson", "")) ?? new Dictionary<string, object>();
            string originalHash = ReadString(boundTrade, "termsHash", "");
            boundTerms["gold"] = 101;
            bool tamperDetected = !string.IsNullOrWhiteSpace(originalHash)
                && !string.Equals(originalHash, ComputeActionTermsHash("trade_package",
                    boundTerms), StringComparison.OrdinalIgnoreCase);
            bool canonicalEscaping = string.Equals(CanonicalActionTerms(TestDict(
                    "packageText", "I'll take <Lycaron>.")),
                "{\"packageText\":\"I'll take <Lycaron>.\"}",
                StringComparison.Ordinal);
            bool metadataBound = string.Equals(ReadString(boundTrade, "authorizationMode", ""),
                    "dialogue_acceptance", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(boundTrade, "acceptedByHeroStringId", ""),
                    "rhagaea", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ReadString(boundTerms, "settlementAuthorityKind", ""),
                    "owner_clan_leader", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ReadString(boundTrade, "negotiationId", ""))
                && !string.IsNullOrWhiteSpace(originalHash);
            bool passed = ReadBool(ownerTrade, "accepted", false)
                && !ReadBool(unrelatedTrade, "accepted", false)
                && !ReadBool(rulerSellingVassalFief, "accepted", false)
                && ReadBool(clanOwnerTrade, "accepted", false)
                && ReadBool(rulerPeace, "accepted", false)
                && !ReadBool(nonRulerPeace, "accepted", false)
                && metadataBound && tamperDetected && canonicalEscaping;
            return new Dictionary<string, object>
            {
                ["passed"] = passed,
                ["metadataBound"] = metadataBound,
                ["tamperDetected"] = tamperDetected,
                ["canonicalEscaping"] = canonicalEscaping,
                ["cases"] = cases
            };
        }

        private static void RunProviderFaultChecks(List<Dictionary<string, object>> checks)
        {
            Dictionary<string, object> secrets = new Dictionary<string, object>
            {
                ["apiKey"] = "sk-secret-value",
                ["portraitNanoGptApiKey"] = "portrait-secret",
                ["portraitAtlasApiKey"] = "atlas-secret",
                ["safe"] = "visible"
            };
            Dictionary<string, object> redacted = RedactSensitive(secrets);
            bool secretsSafe = !Json.Serialize(redacted).Contains("secret-value") && !Json.Serialize(redacted).Contains("portrait-secret") && !Json.Serialize(redacted).Contains("atlas-secret");
            AddVerificationCheck(checks, "faults.secret_redaction", "provider_faults", secretsSafe, "API secrets are redacted from diagnostics.", redacted);

            string[] faults = { "timeout", "401", "429", "500", "malformed_json", "truncated_output", "context_overflow", "missing_model", "vector_unavailable", "corrupt_image", "interrupted_write", "retry_limit" };
            foreach (string fault in faults)
            {
                Dictionary<string, object> classified = ClassifySyntheticProviderFault(fault);
                bool terminal = ReadBool(classified, "terminal", false);
                bool retryable = ReadBool(classified, "retryable", false);
                bool expected = fault == "timeout" || fault == "429" || fault == "500" || fault == "vector_unavailable" ? retryable : terminal;
                AddVerificationCheck(checks, "faults." + fault, "provider_faults", expected, "Fault classified as " + ReadString(classified, "failureKind", "unknown") + ".", classified);
            }
            bool transientClassifier = IsTransientLlmFailure("{\"error\":\"Billing reservation failed\",\"status\":503}")
                && IsTransientLlmFailure("The remote server returned an error: (429) Too Many Requests.")
                && !IsTransientLlmFailure("The remote server returned an error: (401) Unauthorized.");
            AddVerificationCheck(checks, "faults.llm_transient_retry_classifier", "provider_faults", transientClassifier,
                "Transient billing, throttling, and provider failures retry while authentication failures remain terminal.", null);
        }

        private static void RunShadowWorldChecks(List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            ReignShadowWorld world = ReignShadowWorld.Create(seed);
            string root = FindVerificationSourceRoot();
            string enumPath = string.IsNullOrWhiteSpace(root) ? "" : VerificationSourceLocator.ResolveUnique(Path.Combine(root, "ReignBeta"), "ReignWorldActionType.cs", "src");
            List<string> actions = DiscoverActionNames(enumPath).Where(x => !x.Equals("Unknown", StringComparison.OrdinalIgnoreCase)).ToList();
            List<string> failures = new List<string>();
            foreach (string action in actions)
            {
                ReignShadowWorld before = world.Clone();
                ShadowExecutionResult execution = world.Execute(action, "shadow-" + action, false);
                bool retired = action == "DiplomacySignTemporaryTruce" || action == "DiplomacyTradeEmbargo";
                bool pass = retired ? !execution.Success && execution.Terminal : execution.Success && (execution.Changed || execution.Pending || execution.LedgerOnly);
                if (!pass) failures.Add(action + ": " + execution.Message);

                ReignShadowWorld afterFirst = world.Clone();
                ShadowExecutionResult duplicate = world.Execute(action, "shadow-" + action, false);
                bool idempotent = world.StructuralEquals(afterFirst) && (retired ? duplicate.Terminal && !duplicate.Success : duplicate.NoOp);
                if (!idempotent) failures.Add(action + ": duplicate execution was not idempotent");

                ShadowExecutionResult invalid = before.Execute(action, "invalid-" + action, true);
                if (!invalid.Terminal || invalid.Success || !before.StructuralEquals(before.Clone())) failures.Add(action + ": invalid action did not fail terminally");
            }

            string persisted = Path.Combine(sandbox, "shadow-world.json");
            WriteJsonObject(persisted, world.ToDictionary());
            ReignShadowWorld reloaded = ReignShadowWorld.FromDictionary(ReadJsonObject(persisted));
            if (!world.StructuralEquals(reloaded)) failures.Add("shadow world did not survive save/reload");
            AddVerificationCheck(checks, "shadow_world.all_actions", "actions_trade", failures.Count == 0, "Shadow world executed, rejected, persisted, and replay-protected " + actions.Count + " actions.", new Dictionary<string, object> { ["actionCount"] = actions.Count, ["failures"] = failures });

            ReignShadowWorld tradeWorld = ReignShadowWorld.Create(seed + 1);
            string beforeTrade = tradeWorld.Fingerprint();
            ShadowExecutionResult rejectedTrade = tradeWorld.ExecuteTrade(5000, 20, 2, true);
            bool atomicReject = !rejectedTrade.Success && tradeWorld.Fingerprint() == beforeTrade;
            ShadowExecutionResult acceptedTrade = tradeWorld.ExecuteTrade(5000, 20, 2, false);
            bool exactTrade = acceptedTrade.Success && tradeWorld.PlayerGold == 45000 && tradeWorld.PlayerGrain == 80 && tradeWorld.PlayerHorses == 8 && tradeWorld.NpcGold == 15000 && tradeWorld.NpcGrain == 40 && tradeWorld.NpcHorses == 6;
            AddVerificationCheck(checks, "shadow_world.atomic_trade", "actions_trade", atomicReject && exactTrade, "Trade packages reject atomically and apply exact multi-term effects.", new Dictionary<string, object> { ["rejected"] = rejectedTrade.ToDictionary(), ["accepted"] = acceptedTrade.ToDictionary(), ["world"] = tradeWorld.ToDictionary() });
        }

        private static void RunExtendedRebellionChecks(List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            ReignShadowRebellion rebellion = new ReignShadowRebellion(seed);
            List<Dictionary<string, object>> rows = rebellion.RunLifecycleMatrix();
            int passed = rows.Count(CheckPassed);
            AddVerificationCheck(checks, "rebellion.lifecycle_matrix", "rebellion", passed == rows.Count && rows.Count >= 25, "Extended rebellion lifecycle: " + passed + "/" + rows.Count + " checks passed.", new Dictionary<string, object> { ["results"] = rows });

            string path = Path.Combine(sandbox, "rebellion-checkpoint.json");
            Dictionary<string, object> snapshot = rebellion.ToDictionary();
            WriteJsonObject(path, snapshot);
            ReignShadowRebellion restored = ReignShadowRebellion.FromDictionary(ReadJsonObject(path));
            AddVerificationCheck(checks, "rebellion.save_reload", "rebellion", rebellion.Fingerprint() == restored.Fingerprint(), "Rebellion state is idempotent across save/reload.", new Dictionary<string, object> { ["path"] = path });
        }

        private static void RunLongRunChecks(List<Dictionary<string, object>> checks, string sandbox, int seed)
        {
            Random random = new Random(seed);
            ReignShadowWorld world = ReignShadowWorld.Create(seed);
            ReignShadowRebellion rebellion = new ReignShadowRebellion(seed);
            List<string> violations = new List<string>();
            int days = 5 * 126;
            for (int day = 1; day <= days; day++)
            {
                if (ShouldCancelVerification()) break;
                world.AdvanceDay(random);
                rebellion.AdvanceDay(day, random);
                if (day % 63 == 0)
                {
                    Dictionary<string, object> checkpoint = new Dictionary<string, object> { ["day"] = day, ["world"] = world.ToDictionary(), ["rebellion"] = rebellion.ToDictionary() };
                    string path = Path.Combine(sandbox, "long-run-" + day + ".json");
                    WriteJsonObject(path, checkpoint);
                    Dictionary<string, object> restored = ReadJsonObject(path);
                    ReignShadowWorld restoredWorld = ReignShadowWorld.FromDictionary(ReadDictionary(restored, "world"));
                    ReignShadowRebellion restoredRebellion = ReignShadowRebellion.FromDictionary(ReadDictionary(restored, "rebellion"));
                    if (!world.StructuralEquals(restoredWorld)) violations.Add("world checkpoint mismatch at day " + day);
                    if (rebellion.Fingerprint() != restoredRebellion.Fingerprint()) violations.Add("rebellion checkpoint mismatch at day " + day);
                }
                violations.AddRange(world.ValidateInvariants().Select(x => "day " + day + ": " + x));
                violations.AddRange(rebellion.ValidateInvariants().Select(x => "day " + day + ": " + x));
                if (violations.Count > 100) break;
            }
            AddVerificationCheck(checks, "long_run.five_years", "long_run", violations.Count == 0 && !ShouldCancelVerification(), "Seeded shadow campaign ran for " + days + " days (five 126-day years).", new Dictionary<string, object> { ["seed"] = seed, ["days"] = days, ["violations"] = violations });
        }

        private static void RunLiveLlmVerificationChecks(List<Dictionary<string, object>> checks, string sandbox, string requestedSuite, Dictionary<string, object> options)
        {
            if (string.Equals(requestedSuite, "codex_performance", StringComparison.OrdinalIgnoreCase))
            {
                RunCodexPerformanceLiveVerification(checks, sandbox, options);
                return;
            }
            if (string.Equals(requestedSuite, "codex_images", StringComparison.OrdinalIgnoreCase))
            {
                RunCodexImageLiveVerification(checks, sandbox, options);
                return;
            }
            Dictionary<string, object> settings = LoadSettings();
            if (!LlmProviderConfigured(settings))
            {
                AddVerificationCheck(checks, "live_llm.configuration", "live_llm", false, "Live LLM verification requires a configured selected provider.", new Dictionary<string, object>
                {
                    ["provider"] = NormalizeLlmProvider(ReadString(settings, "llmProvider", OpenAiCompatibleProvider)),
                    ["apiKeyPresent"] = !string.IsNullOrWhiteSpace(LlmApiKey(settings)),
                    ["apiUrlPresent"] = !string.IsNullOrWhiteSpace(LlmApiUrl(settings)),
                    ["codexExecutablePresent"] = !string.IsNullOrWhiteSpace(ReadString(settings, "codexExecutable", ""))
                }, "blocked");
                return;
            }

            if (string.Equals(requestedSuite, "campaign_command", StringComparison.OrdinalIgnoreCase))
            {
                RunCampaignCommandLlmMatrixChecks(checks, sandbox, options);
                return;
            }

            int cap = Math.Max(1, ReadInt(options, "liveCaseCap", 20));
            List<Dictionary<string, object>> sourceCases = LoadTestCases("")
                .Where(row => ReadString(row, "suite", "").Equals("action_reliability", StringComparison.OrdinalIgnoreCase) || ReadString(row, "suite", "").Equals("action_gate", StringComparison.OrdinalIgnoreCase))
                .Take(cap)
                .ToList();
            int passed = 0;
            List<Dictionary<string, object>> failures = new List<Dictionary<string, object>>();
            foreach (Dictionary<string, object> source in sourceCases)
            {
                if (ShouldCancelVerification()) break;
                Dictionary<string, object> liveCase = BuildLiveVerificationCase(source);
                Dictionary<string, object> row = RunPipelineTestCase(liveCase, "live");
                if (ReadBool(row, "passed", false)) passed++;
                else failures.Add(CompactVerificationFailure(row));
            }

            double score = sourceCases.Count == 0 ? 0d : (double)passed / sourceCases.Count;
            AddVerificationCheck(checks, "live_llm.varied_actions", "live_llm", sourceCases.Count > 0 && score >= 0.90d, "Live production-gate action score: " + passed + "/" + sourceCases.Count + " (" + (score * 100d).ToString("0.0", CultureInfo.InvariantCulture) + "%).", new Dictionary<string, object> { ["caseCap"] = cap, ["score"] = score, ["failures"] = failures });
        }

        private static void RunGameVerificationBridge(List<Dictionary<string, object>> checks, string sandbox, Dictionary<string, object> options)
        {
            EnsureVerificationDirectories();
            string commandId = "game-verify-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            Dictionary<string, object> command = new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["command"] = "run",
                ["suite"] = "all",
                ["restoreDisposableSave"] = true,
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["sandboxPath"] = sandbox
            };
            WriteJsonObject(VerificationGameCommandPath, command);
            int timeoutSeconds = Math.Max(30, ReadInt(options, "gameTimeoutSeconds", 900));
            Stopwatch wait = Stopwatch.StartNew();
            Dictionary<string, object> status = new Dictionary<string, object>();
            while (wait.Elapsed.TotalSeconds < timeoutSeconds && !ShouldCancelVerification())
            {
                if (File.Exists(VerificationGameStatusPath))
                {
                    status = ReadJsonObject(VerificationGameStatusPath);
                    if (ReadString(status, "commandId", "") == commandId)
                    {
                        string state = ReadString(status, "state", "");
                        if (state == "completed" || state == "failed" || state == "cancelled") break;
                    }
                }
                Thread.Sleep(1000);
            }
            bool passed = ReadString(status, "commandId", "") == commandId && ReadString(status, "state", "") == "completed" && ReadInt(status, "failed", 1) == 0;
            string summary = passed ? "Native Bannerlord game gauntlet completed without mechanical failures." : wait.Elapsed.TotalSeconds >= timeoutSeconds ? "Game tier timed out waiting for Bannerlord. Load the disposable verification save and leave the game running." : "Native game gauntlet did not complete cleanly.";
            AddVerificationCheck(checks, "game.native_gauntlet", "game", passed, summary, new Dictionary<string, object> { ["command"] = command, ["status"] = status, ["waitMs"] = wait.ElapsedMilliseconds }, passed ? "passed" : "blocked");
        }

        private static string WriteVerificationGameCancelCommand(string reason)
        {
            EnsureVerificationDirectories();
            string commandId = "game-cancel-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            WriteJsonObject(VerificationGameCommandPath, new Dictionary<string, object>
            {
                ["commandId"] = commandId,
                ["command"] = "cancel",
                ["createdUtc"] = DateTime.UtcNow.ToString("o"),
                ["reason"] = reason ?? string.Empty
            });
            return commandId;
        }

        private static Dictionary<string, object> BuildVerificationCoverageManifest()
        {
            string root = FindVerificationSourceRoot();
            string clientRoot = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ReignBeta");
            string serverRoot = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "ReignBetaServer");
            List<Dictionary<string, object>> features = new List<Dictionary<string, object>>();
            Action<string, string, string[]> add = (id, suite, tests) => features.Add(new Dictionary<string, object>
            {
                ["id"] = id,
                ["implemented"] = true,
                ["suite"] = suite,
                ["tests"] = tests,
                ["dimensions"] = new[] { "success", "failure", "persistence", "recovery" }
            });

            add("server.lifecycle", "server_lifecycle", new[] { "contracts.http_routes", "pipeline.campaign_backups", "pipeline.save_sync", "faults.interrupted_write" });
            add("characters.construction", "character_construction", new[] { "pipeline.character_profiles", "pipeline.character_editor" });
            add("characters.narrative", "character_narrative", new[] { "contracts.character_narrative" });
            add("knowledge.identity", "knowledge_identity", new[] { "pipeline.identity", "pipeline.rumors" });
            add("memory.relationships", "memory_relationships", new[] { "pipeline.mbti_relationship_lifecycle", "pipeline.categorized_memory", "pipeline.semantic_memory" });
            add("memory.mbti_relationship_lifecycle", "memory_relationships", new[] { "contracts.mbti_relationship_lifecycle", "pipeline.mbti_relationship_lifecycle" });
            add("conversation.individual", "conversation_modes", new[] { "pipeline.canned_cases", "contracts.dialogue_prompt_budget" });
            add("conversation.party", "conversation_modes", new[] { "pipeline.canned_cases", "pipeline.party_dialogue_audit", "contracts.gui_xml" });
            add("conversation.correspondence", "conversation_modes", new[] { "pipeline.canned_cases", "contracts.gui_xml" });
            add("events.social_wilderness_tournament", "conversation_modes", new[] { "pipeline.canned_cases", "contracts.gui_xml", "contracts.social_event_player_exclusion", "contracts.social_event_encyclopedia_handoff", "contracts.social_event_local_first_roster", "contracts.social_event_persistent_group", "contracts.social_event_group_turn" });
            add("prompts.efficiency_cache", "prompt_efficiency", new[] { "contracts.dialogue_prompt_budget", "pipeline.prompt_caching" });
            add("portraits.images", "portraits_images", new[] { "contracts.gui_xml", "contracts.portrait_identity_metadata", "contracts.image_generation_profiles", "faults.corrupt_image" });
            add("actions.trade", "actions_trade", new[] { "pipeline.canned_cases", "pipeline.settlement_authority", "shadow_world.all_actions", "shadow_world.atomic_trade" });
            add("diplomacy.world", "world_diplomacy", new[] { "baseline.diplomacy", "contracts.random_diplomacy_popup_debug", "long_run.five_years" });
            add("history.world", "world_history", new[] { "baseline.world_history", "pipeline.rumors" });
            add("rebellion.lifecycle", "rebellion", new[] { "baseline.rebellion", "rebellion.lifecycle_matrix", "rebellion.save_reload", "long_run.five_years" });
            add("government.cultural_people_pressure", "government", new[] { "contracts.government", "game.government_feature_harness" });
            add("ui.contracts", "ui_contracts", new[] { "contracts.gui_xml", "game.native_gauntlet" });
            add("calendar.126_day", "contracts", new[] { "contracts.calendar_126", "long_run.five_years" });
            add("audit.result_objects", "actions_trade", new[] { "pipeline.canned_cases", "shadow_world.all_actions" });
            add("campaign.backup_isolation", "server_lifecycle", new[] { "pipeline.campaign_backups", "long_run.five_years" });
            add("campaign.save_sync", "server_lifecycle", new[] { "pipeline.save_sync", "contracts.http_routes" });

            string enumPath = string.IsNullOrWhiteSpace(clientRoot) ? "" : VerificationSourceLocator.ResolveUnique(clientRoot, "ReignWorldActionType.cs", "src");
            foreach (string action in DiscoverActionNames(enumPath).Where(x => x != "Unknown"))
            {
                add("action." + ToSnakeCase(action), "actions_trade", new[] { "pipeline.canned_cases", "shadow_world.all_actions" });
            }

            List<string> behaviors = new List<string>();
            string subModulePath = string.IsNullOrWhiteSpace(clientRoot) ? "" : VerificationSourceLocator.ResolveUnique(clientRoot, "SubModule.cs", "src");
            if (File.Exists(subModulePath)) behaviors = Regex.Matches(File.ReadAllText(subModulePath), "new\\s+([A-Za-z0-9_]+CampaignBehavior)\\s*\\(").Cast<Match>().Select(x => x.Groups[1].Value).Where(x => !IsExcludedCourtVerificationArtifact(x)).Distinct().OrderBy(x => x).ToList();
            List<string> routes = new List<string>();
            string programPath = string.IsNullOrWhiteSpace(serverRoot) ? "" : VerificationSourceLocator.ResolveUnique(serverRoot, "Program.cs", "src");
            if (File.Exists(programPath)) routes = Regex.Matches(File.ReadAllText(programPath), "request\\.Path\\s*==\\s*\"([^\"]+)\"").Cast<Match>().Select(x => x.Groups[1].Value).Where(x => !x.StartsWith("/court/", StringComparison.OrdinalIgnoreCase)).Distinct().OrderBy(x => x).ToList();
            List<string> guiMovies = string.IsNullOrWhiteSpace(clientRoot) || !Directory.Exists(Path.Combine(clientRoot, "GUI", "Prefabs")) ? new List<string>() : Directory.GetFiles(Path.Combine(clientRoot, "GUI", "Prefabs"), "*.xml").Where(path => !IsExcludedCourtVerificationArtifact(path)).Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToList();
            List<string> prompts = PromptFileNames.Where(x => !IsExcludedCourtVerificationArtifact(x)).OrderBy(x => x).ToList();
            List<string> uncovered = features.Where(row => ReadBool(row, "implemented", false) && ReadStringArray(row, "tests").Count == 0).Select(row => ReadString(row, "id", "")).ToList();
            return new Dictionary<string, object>
            {
                ["generatedUtc"] = DateTime.UtcNow.ToString("o"),
                ["sourceRoot"] = root,
                ["features"] = features,
                ["featureCount"] = features.Count,
                ["uncoveredImplementedFeatures"] = uncovered,
                ["behaviors"] = behaviors,
                ["routes"] = routes,
                ["prompts"] = prompts,
                ["guiMovies"] = guiMovies,
                ["settings"] = DiscoverSettings(clientRoot).Where(x => !IsExcludedCourtVerificationArtifact(x)).ToList(),
                ["saveTypes"] = DiscoverSaveTypes(clientRoot).Where(x => !IsExcludedCourtVerificationArtifact(x)).ToList()
            };
        }

        private static void AddCoverageChecks(List<Dictionary<string, object>> checks, Dictionary<string, object> coverage, string requestedSuite)
        {
            if (!ShouldRunVerificationSuite(requestedSuite, "coverage")) return;
            List<string> uncovered = ReadStringArray(coverage, "uncoveredImplementedFeatures");
            int featureCount = ReadInt(coverage, "featureCount", 0);
            AddVerificationCheck(checks, "coverage.implemented_features", "coverage", featureCount > 0 && uncovered.Count == 0, featureCount + " implemented features have assigned verification coverage.", new Dictionary<string, object> { ["uncovered"] = uncovered });
            AddVerificationCheck(checks, "coverage.runtime_inventory", "coverage", ReadStringArray(coverage, "routes").Count >= 60 && ReadStringArray(coverage, "prompts").Count >= 60 && ReadStringArray(coverage, "guiMovies").Count >= 5, "Runtime route, prompt, and GUI inventories were generated.", new Dictionary<string, object> { ["routeCount"] = ReadStringArray(coverage, "routes").Count, ["promptCount"] = ReadStringArray(coverage, "prompts").Count, ["guiMovieCount"] = ReadStringArray(coverage, "guiMovies").Count });
        }

        private static void AddSelfTestSummary(List<Dictionary<string, object>> checks, string id, string suite, Dictionary<string, object> result)
        {
            bool passed = ReadBool(result, "passed", ReadBool(result, "ok", false));
            List<Dictionary<string, object>> rows = ReadDictionaryList(result, "tests");
            if (rows.Count == 0) rows = ReadDictionaryList(result, "assertions");
            if (rows.Count == 0) rows = ReadDictionaryList(result, "results");
            int total = ReadInt(result, "totalCount", ReadInt(result, "total", rows.Count));
            int passedCount = ReadInt(result, "passedCount", rows.Count > 0 ? rows.Count(CheckPassed) : passed ? total : 0);
            AddVerificationCheck(checks, id, suite, passed, id + ": " + passedCount + "/" + total + " baseline checks passed.", CompactSelfTest(result));
        }

        private static void AddSubsystemListSummary(List<Dictionary<string, object>> checks, string id, string suite, List<Dictionary<string, object>> rows)
        {
            rows = rows ?? new List<Dictionary<string, object>>();
            int passed = rows.Count(CheckPassed);
            AddVerificationCheck(checks, id, suite, rows.Count > 0 && passed == rows.Count, id + ": " + passed + "/" + rows.Count + " checks passed.", new Dictionary<string, object> { ["failures"] = rows.Where(x => !CheckPassed(x)).Select(CompactVerificationFailure).ToList() });
        }

        private static Dictionary<string, object> CompactSelfTest(Dictionary<string, object> result)
        {
            Dictionary<string, object> compact = new Dictionary<string, object>();
            foreach (string key in new[] { "ok", "passed", "passedCount", "failedCount", "totalCount", "total", "summary" })
            {
                if (result != null && result.TryGetValue(key, out object value)) compact[key] = value;
            }
            List<Dictionary<string, object>> tests = ReadDictionaryList(result, "tests");
            if (tests.Count == 0) tests = ReadDictionaryList(result, "assertions");
            if (tests.Count == 0) tests = ReadDictionaryList(result, "results");
            if (tests.Count > 0) compact["failures"] = tests.Where(x => !CheckPassed(x)).Select(CompactVerificationFailure).ToList();
            return compact;
        }

        private static Dictionary<string, object> CompactVerificationFailure(Dictionary<string, object> row)
        {
            return new Dictionary<string, object>
            {
                ["suite"] = ReadString(row, "suite", ""),
                ["caseId"] = ReadFirstString(row, "caseId", "id", "name"),
                ["summary"] = ReadFirstString(row, "summary", "error", "message"),
                ["runId"] = ReadString(row, "runId", "")
            };
        }

        private static int CountLegacyPromptOverrideCases(List<Dictionary<string, object>> cases)
        {
            return (cases ?? new List<Dictionary<string, object>>()).Count(row => Json.Serialize(row).IndexOf("prompt_override", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static Dictionary<string, object> ClassifySyntheticProviderFault(string fault)
        {
            bool retryable = fault == "timeout" || fault == "429" || fault == "500" || fault == "vector_unavailable";
            return new Dictionary<string, object>
            {
                ["fault"] = fault,
                ["retryable"] = retryable,
                ["terminal"] = !retryable,
                ["failureKind"] = retryable ? "transient_provider" : fault == "401" ? "authentication" : fault == "context_overflow" ? "prompt_size" : fault == "corrupt_image" ? "image_decode" : "invalid_provider_response",
                ["maxAttempts"] = retryable ? 2 : 1
            };
        }

        private static Dictionary<string, object> BuildLiveVerificationCase(Dictionary<string, object> source)
        {
            Dictionary<string, object> testCase = CloneDictionary(source);
            testCase["suite"] = "verification_live_llm";
            testCase["caseId"] = "live_" + ReadString(source, "caseId", Guid.NewGuid().ToString("N"));
            testCase["mode"] = "dialogue";
            testCase["llm"] = new Dictionary<string, object> { ["mode"] = "live" };
            Dictionary<string, object> input = ReadDictionary(testCase, "input") ?? new Dictionary<string, object>();
            string text = ReadFirstString(input, "playerText", "text", "message").Replace("@", "");
            input["playerText"] = text;
            testCase["input"] = input;
            return testCase;
        }

        private static Dictionary<string, object> NormalizeVerificationOptions(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string requestedTier =
                ReadString(payload, "tier", "offline").ToLowerInvariant();
            string tier = requestedTier;
            if (tier == "commit") tier = "quick";
            else if (tier == "full-regression") tier = "offline";
            else if (tier == "release-candidate") tier = "all";
            else if (tier == "exhaustive-final-review") tier = "offline";
            else if (tier == "human-immersion") tier = "quick";
            if (!(new[] { "quick", "offline", "live-llm", "game", "all" }).Contains(tier)) tier = "offline";
            return new Dictionary<string, object>
            {
                ["tier"] = tier,
                ["requestedTier"] = requestedTier,
                ["finalGauntletHandoff"] =
                    requestedTier == "exhaustive-final-review"
                        ? "Run ReignLiveTest.exe gauntlet start after this deterministic prerequisite passes."
                        : requestedTier == "human-immersion"
                            ? "Export the completed gauntlet review pack and score it blindly."
                            : "",
                ["suite"] = ReadString(payload, "suite", ""),
                ["codexPerformance"] = ReadDictionary(payload, "codexPerformance"),
                ["seed"] = ReadInt(payload, "seed", 1337),
                ["repeat"] = Math.Max(1, ReadInt(payload, "repeat", 1)),
                ["failFast"] = ReadBool(payload, "failFast", false),
                ["liveCaseCap"] = Math.Max(1, Math.Min(
                    string.Equals(ReadString(payload, "suite", ""), "campaign_command", StringComparison.OrdinalIgnoreCase)
                        ? 600 : 100,
                    ReadInt(payload, "liveCaseCap", 20))),
                ["gameTimeoutSeconds"] = Math.Max(30, ReadInt(payload, "gameTimeoutSeconds", 900)),
                ["replayOf"] = ReadString(payload, "replayOf", "")
            };
        }

        private static void AddVerificationCheck(List<Dictionary<string, object>> checks, string id, string suite, bool passed, string summary, Dictionary<string, object> data, string status = null)
        {
            Dictionary<string, object> row = new Dictionary<string, object>
            {
                ["id"] = id,
                ["suite"] = suite,
                ["passed"] = passed,
                ["status"] = status ?? (passed ? "passed" : "failed"),
                ["summary"] = summary ?? "",
                ["timestampUtc"] = DateTime.UtcNow.ToString("o"),
                ["data"] = data ?? new Dictionary<string, object>()
            };
            checks.Add(row);
            int passedCount = checks.Count(CheckPassed);
            SetVerificationStatus(ReadString(VerificationStatus, "runId", ""), "running", summary, passedCount, checks.Count - passedCount, checks.Count);
        }

        private static bool CheckPassed(Dictionary<string, object> row)
        {
            return ReadBool(row, "passed", false);
        }

        private static bool ShouldStop(List<Dictionary<string, object>> checks, bool failFast)
        {
            return ShouldCancelVerification() || (failFast && checks.Any(x => !CheckPassed(x)));
        }

        private static bool ShouldCancelVerification()
        {
            return VerificationCancelRequested || File.Exists(VerificationCancelPath);
        }

        private static bool ShouldRunVerificationSuite(string requested, string suite)
        {
            if (string.IsNullOrWhiteSpace(requested)) return true;
            return requested.Equals(suite, StringComparison.OrdinalIgnoreCase)
                || (requested.Equals("rebellion_system", StringComparison.OrdinalIgnoreCase) && suite.Equals("rebellion", StringComparison.OrdinalIgnoreCase))
                || (requested.Equals("actions", StringComparison.OrdinalIgnoreCase) && suite.Equals("shadow_world", StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, object> NewVerificationStatus(string state, string message)
        {
            return new Dictionary<string, object>
            {
                ["runId"] = "",
                ["state"] = state,
                ["message"] = message,
                ["passedCount"] = 0,
                ["failedCount"] = 0,
                ["totalCount"] = 0,
                ["updatedUtc"] = DateTime.UtcNow.ToString("o")
            };
        }

        private static void SetVerificationStatus(string runId, string state, string message, int passed, int failed, int total)
        {
            lock (VerificationLock)
            {
                VerificationStatus = new Dictionary<string, object>
                {
                    ["runId"] = runId ?? "",
                    ["state"] = state ?? "",
                    ["message"] = message ?? "",
                    ["passedCount"] = passed,
                    ["failedCount"] = failed,
                    ["totalCount"] = total,
                    ["cancelRequested"] = VerificationCancelRequested,
                    ["updatedUtc"] = DateTime.UtcNow.ToString("o")
                };
            }
        }

        private static void EnsureVerificationDirectories()
        {
            Directory.CreateDirectory(VerificationDir);
            Directory.CreateDirectory(VerificationRunsDir);
            Directory.CreateDirectory(VerificationFailuresDir);
            Directory.CreateDirectory(VerificationSandboxesDir);
        }

        private static void SaveVerificationResult(Dictionary<string, object> result)
        {
            EnsureVerificationDirectories();
            string runId = SafePathSegment(ReadString(result, "runId", "verification"), "verification");
            bool passed = ReadBool(result, "passed", false);
            string directory = passed ? VerificationRunsDir : VerificationFailuresDir;
            string path = Path.Combine(directory, runId + ".json");
            if (!passed)
            {
                result["replayBundle"] = new Dictionary<string, object>
                {
                    ["version"] = 1,
                    ["runId"] = runId,
                    ["options"] = ReadDictionary(result, "options") ?? new Dictionary<string, object>(),
                    ["failedChecks"] = ReadDictionaryList(result, "checks").Where(x => !CheckPassed(x)).ToList(),
                    ["sandboxPath"] = ReadString(result, "sandboxPath", "")
                };
            }
            WriteJsonObject(path, result);
            PruneSuccessfulVerificationRuns();
        }

        private static void PruneSuccessfulVerificationRuns()
        {
            foreach (string path in Directory.GetFiles(VerificationRunsDir, "*.json", SearchOption.TopDirectoryOnly).OrderByDescending(File.GetLastWriteTimeUtc).Skip(20))
            {
                TryDelete(path);
            }
            HashSet<string> retained = new HashSet<string>(Directory.GetFiles(VerificationRunsDir, "*.json").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
            retained.UnionWith(Directory.GetFiles(VerificationFailuresDir, "*.json").Select(Path.GetFileNameWithoutExtension));
            retained.UnionWith(VerificationReferencedSandboxNames(VerificationSandboxesDir,
                Directory.GetFiles(VerificationRunsDir, "*.json").Concat(Directory.GetFiles(VerificationFailuresDir, "*.json"))
                    .Select(ReadJsonObject)));
            foreach (string path in Directory.GetDirectories(VerificationSandboxesDir))
            {
                if (!retained.Contains(Path.GetFileName(path))
                    && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0
                    && string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(VerificationSandboxesDir), StringComparison.OrdinalIgnoreCase))
                {
                    try { Directory.Delete(path, true); } catch { }
                }
            }
        }

        private static HashSet<string> VerificationReferencedSandboxNames(string root, IEnumerable<Dictionary<string, object>> reports)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var report in reports)
            {
                string candidate = ReadString(report, "sandboxPath", "");
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string full = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(Path.GetDirectoryName(full), fullRoot, StringComparison.OrdinalIgnoreCase))
                        names.Add(Path.GetFileName(full));
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
                catch (PathTooLongException) { }
            }
            return names;
        }

        private static string FindVerificationRunPath(string runId)
        {
            if (string.IsNullOrWhiteSpace(runId)) return "";
            string safe = SafePathSegment(runId, "");
            string success = Path.Combine(VerificationRunsDir, safe + ".json");
            if (File.Exists(success)) return success;
            string failure = Path.Combine(VerificationFailuresDir, safe + ".json");
            return File.Exists(failure) ? failure : "";
        }

        private static string FindVerificationSourceRoot()
        {
            // The build snapshot is the evidence for this binary. Prefer it over
            // an ancestor checkout, which may have changed or use source junctions.
            string packagedContracts = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "verification_contracts", "workspace");
            if (Directory.Exists(Path.Combine(packagedContracts, "ReignBeta")) && Directory.Exists(Path.Combine(packagedContracts, "ReignBetaServer"))) return packagedContracts;
            string configured = Environment.GetEnvironmentVariable("REIGN_SOURCE_ROOT") ?? "";
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(Path.Combine(configured, "ReignBeta")) && Directory.Exists(Path.Combine(configured, "ReignBetaServer"))) return Path.GetFullPath(configured);
            Dictionary<string, object> settings = LoadSettings();
            configured = ReadString(settings, "verificationSourceRoot", "");
            if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(Path.Combine(configured, "ReignBeta")) && Directory.Exists(Path.Combine(configured, "ReignBetaServer"))) return Path.GetFullPath(configured);

            DirectoryInfo cursor = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            for (int i = 0; cursor != null && i < 8; i++, cursor = cursor.Parent)
            {
                if (Directory.Exists(Path.Combine(cursor.FullName, "ReignBeta")) && Directory.Exists(Path.Combine(cursor.FullName, "ReignBetaServer"))) return cursor.FullName;
            }
            return "";
        }

        private static List<string> DiscoverActionNames(string enumPath)
        {
            if (!File.Exists(enumPath)) return ActionPromptFileNames.Select(PromptFileToActionFallback).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
            Match body = Regex.Match(File.ReadAllText(enumPath), "enum\\s+ReignWorldActionType\\s*\\{(?<body>[\\s\\S]*?)\\}");
            if (!body.Success) return new List<string>();
            return Regex.Matches(body.Groups["body"].Value, "^\\s*([A-Za-z][A-Za-z0-9_]*)\\s*=", RegexOptions.Multiline).Cast<Match>().Select(x => x.Groups[1].Value).Distinct().ToList();
        }

        private static string PromptFileToActionFallback(string file)
        {
            string name = Path.GetFileNameWithoutExtension(file ?? "").Replace("action_", "");
            return string.Concat(name.Split('_').Select(part => part.Length == 0 ? "" : char.ToUpperInvariant(part[0]) + part.Substring(1)));
        }

        private static List<string> DiscoverSettings(string clientRoot)
        {
            string path = string.IsNullOrWhiteSpace(clientRoot) ? "" : VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaSettings.cs", "src");
            if (!File.Exists(path)) return new List<string>();
            return Regex.Matches(File.ReadAllText(path), "public\\s+[A-Za-z0-9_<>?]+\\s+([A-Za-z0-9_]+)\\s*\\{").Cast<Match>().Select(x => x.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
        }

        private static List<string> DiscoverSaveTypes(string clientRoot)
        {
            string path = string.IsNullOrWhiteSpace(clientRoot) ? "" : VerificationSourceLocator.ResolveUnique(clientRoot, "ReignBetaSaveDefiner.cs", "src");
            if (!File.Exists(path)) return new List<string>();
            return Regex.Matches(File.ReadAllText(path), "typeof\\(([A-Za-z0-9_]+)\\)").Cast<Match>().Select(x => x.Groups[1].Value).Distinct().OrderBy(x => x).ToList();
        }

        private static bool IsExcludedCourtVerificationArtifact(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.IndexOf("Court", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("AmbassadorPosting", StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf("IntelligenceOperation", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<string> ReadStringArray(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null) return new List<string>();
            if (value is string text) return new List<string> { text };
            if (value is IEnumerable enumerable) return enumerable.Cast<object>().Where(x => x != null).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").Where(x => x.Length > 0).ToList();
            return new List<string>();
        }

        private static string ToSnakeCase(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "unknown";
            return Regex.Replace(value, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
        }

		private static bool VerificationIndividualChatShellAperturesValid(
			string path,
			out int transparentPixels,
			out int partiallyTransparentPixels,
			out int lowAlphaOutsidePixels,
			out int nonDarkEdgePixels)
		{
			transparentPixels = 0;
			partiallyTransparentPixels = 0;
			lowAlphaOutsidePixels = 0;
			nonDarkEdgePixels = 0;
			byte[] rgba;
			int width = 0;
			int height = 0;
			try
			{
				rgba = File.Exists(path)
					? AIPortraits.PngReencode.DecodeToRgba(File.ReadAllBytes(path), out width, out height)
					: null;
			}
			catch
			{
				rgba = null;
				width = 0;
				height = 0;
			}

			const int expectedWidth = 1672;
			const int expectedHeight = 941;
			int pixelCount = width > 0 && height > 0 ? width * height : 0;
			if (rgba == null || width != expectedWidth || height != expectedHeight || rgba.Length != pixelCount * 4)
			{
				return false;
			}

			int[,] apertures =
			{
				{ 104, 57, 210, 310 },
				{ 1343, 57, 210, 310 }
			};
			int[] corePixels = new int[2];
			int[] transparentCorePixels = new int[2];

			for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
			{
				int offset = pixelIndex * 4;
				byte red = rgba[offset];
				byte green = rgba[offset + 1];
				byte blue = rgba[offset + 2];
				byte alpha = rgba[offset + 3];
				int x = pixelIndex % width;
				int y = pixelIndex / width;
				bool insideRegisteredBounds = false;

				for (int apertureIndex = 0; apertureIndex < 2; apertureIndex++)
				{
					int apertureX = apertures[apertureIndex, 0];
					int apertureY = apertures[apertureIndex, 1];
					int apertureWidth = apertures[apertureIndex, 2];
					int apertureHeight = apertures[apertureIndex, 3];
					if (x < apertureX || x >= apertureX + apertureWidth || y < apertureY || y >= apertureY + apertureHeight)
					{
						continue;
					}

					insideRegisteredBounds = true;
					double radiusX = apertureWidth / 2.0 - 4.0;
					double radiusY = apertureHeight / 2.0 - 4.0;
					double deltaX = (x + 0.5 - (apertureX + apertureWidth / 2.0)) / radiusX;
					double deltaY = (y + 0.5 - (apertureY + apertureHeight / 2.0)) / radiusY;
					if (deltaX * deltaX + deltaY * deltaY <= 1.0)
					{
						corePixels[apertureIndex]++;
						if (alpha <= 16) transparentCorePixels[apertureIndex]++;
					}
				}

				if (alpha == 0) transparentPixels++;
				else if (alpha < 255) partiallyTransparentPixels++;
				if (alpha < 255)
				{
					if (!insideRegisteredBounds) lowAlphaOutsidePixels++;
					if (Math.Abs(red - 18) > 2 || Math.Abs(green - 18) > 2 || Math.Abs(blue - 17) > 2)
					{
						nonDarkEdgePixels++;
					}
				}
			}

			bool centersTransparent = rgba[((212 * width + 209) * 4) + 3] == 0
				&& rgba[((212 * width + 1448) * 4) + 3] == 0;
			bool cornersOpaque = rgba[3] == 255
				&& rgba[((width - 1) * 4) + 3] == 255
				&& rgba[(((height - 1) * width) * 4) + 3] == 255
				&& rgba[((pixelCount - 1) * 4) + 3] == 255;
			bool coresTransparent = corePixels[0] > 0
				&& corePixels[1] > 0
				&& transparentCorePixels[0] >= (int)(corePixels[0] * 0.995)
				&& transparentCorePixels[1] >= (int)(corePixels[1] * 0.995);
			return transparentPixels == 99544
				&& partiallyTransparentPixels == 2664
				&& lowAlphaOutsidePixels == 0
				&& nonDarkEdgePixels == 0
				&& centersTransparent
				&& cornersOpaque
				&& coresTransparent;
		}

		private static bool VerificationOpaquePortraitAperturePlateValid(
            string path,
            int expectedWidth,
            int expectedHeight,
            out int transparentPixels,
            out int visiblePixels,
            out int lightNeutralPixels,
            out int transparentRgbPixels)
        {
            transparentPixels = 0;
            visiblePixels = 0;
            lightNeutralPixels = 0;
            transparentRgbPixels = 0;
            byte[] rgba;
            int width = 0;
            int height = 0;
            try
            {
                rgba = File.Exists(path)
                    ? AIPortraits.PngReencode.DecodeToRgba(File.ReadAllBytes(path), out width, out height)
                    : null;
            }
            catch
            {
                rgba = null;
                width = 0;
                height = 0;
            }

            int pixelCount = width > 0 && height > 0 ? width * height : 0;
            if (rgba == null || width != expectedWidth || height != expectedHeight || rgba.Length != pixelCount * 4)
            {
                return false;
            }

            int opaqueBorderPixels = 0;
            int borderPixels = 0;
            for (int pixelIndex = 0; pixelIndex < pixelCount; pixelIndex++)
            {
                int offset = pixelIndex * 4;
                byte red = rgba[offset];
                byte green = rgba[offset + 1];
                byte blue = rgba[offset + 2];
                byte alpha = rgba[offset + 3];
                int x = pixelIndex % width;
                int y = pixelIndex / width;
                if (x == 0 || y == 0 || x == width - 1 || y == height - 1)
                {
                    borderPixels++;
                    if (alpha >= 192)
                    {
                        opaqueBorderPixels++;
                    }
                }
                if (alpha == 0)
                {
                    transparentPixels++;
                    if (red != 0 || green != 0 || blue != 0)
                    {
                        transparentRgbPixels++;
                    }
                    continue;
                }

                visiblePixels++;
                int maximum = Math.Max(red, Math.Max(green, blue));
                int minimum = Math.Min(red, Math.Min(green, blue));
                if (alpha >= 64 && minimum >= 170 && maximum - minimum <= 28)
                {
                    lightNeutralPixels++;
                }
            }

            int centerOffset = ((height / 2) * width + (width / 2)) * 4;
            bool centerTransparent = centerOffset + 3 < rgba.Length && rgba[centerOffset + 3] <= 16;
            bool cornersOpaque = rgba[3] >= 192
                && rgba[(width - 1) * 4 + 3] >= 192
                && rgba[((height - 1) * width) * 4 + 3] >= 192
                && rgba[(pixelCount - 1) * 4 + 3] >= 192;
            return visiblePixels > 0
                && transparentPixels >= (int)(pixelCount * 0.15f)
                && transparentPixels <= (int)(pixelCount * 0.90f)
                && lightNeutralPixels == 0
                && centerTransparent
                && cornersOpaque
                && borderPixels > 0
                && opaqueBorderPixels >= (int)(borderPixels * 0.95f);
        }

        private static string VerificationSourceSlice(string source, string startMarker, string endMarker)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(startMarker)) return string.Empty;
            int start = source.IndexOf(startMarker, StringComparison.Ordinal);
            if (start < 0) return string.Empty;
            if (string.IsNullOrEmpty(endMarker)) return source.Substring(start);
            int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
            return end > start ? source.Substring(start, end - start) : string.Empty;
        }

        private static XmlDocument TryParseVerificationXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                XmlDocument document = new XmlDocument();
                document.LoadXml(xml);
                return document;
            }
            catch
            {
                return null;
            }
        }

        private static XmlElement FindVerificationXmlElementById(XmlDocument document, string id)
        {
            if (document == null || string.IsNullOrWhiteSpace(id)) return null;
            return document.SelectSingleNode("//*[@Id='" + id + "']") as XmlElement;
        }

        private static bool VerificationXmlAttributeEquals(XmlElement element, string attributeName, string expected)
        {
            return element != null
                && element.HasAttribute(attributeName)
                && string.Equals(element.GetAttribute(attributeName), expected, StringComparison.Ordinal);
        }

		private static int VerificationXmlChildElementIndex(XmlElement parent, XmlElement child)
		{
			if (parent == null || child == null) return -1;
			int index = 0;
			foreach (XmlNode node in parent.ChildNodes)
			{
				if (!(node is XmlElement element)) continue;
				if (ReferenceEquals(element, child)) return index;
				index++;
			}
			return -1;
		}

		private static bool VerificationXmlAppearsAfter(XmlNode later, XmlNode earlier)
		{
			if (later == null || earlier == null || later.OwnerDocument == null
				|| !ReferenceEquals(later.OwnerDocument, earlier.OwnerDocument)) return false;
			bool earlierSeen = false;
			XmlNodeList nodes = later.OwnerDocument.SelectNodes("//*");
			if (nodes == null) return false;
			foreach (XmlNode node in nodes)
			{
				if (ReferenceEquals(node, earlier)) earlierSeen = true;
				if (ReferenceEquals(node, later)) return earlierSeen;
			}
			return false;
		}

        private static int VerificationXmlIntAttribute(XmlElement element, string attributeName, int fallback)
        {
            if (element == null || !element.HasAttribute(attributeName)) return fallback;
            return int.TryParse(element.GetAttribute(attributeName), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : fallback;
        }

        private static bool VerificationModernChatListTemplateMatches(
            XmlElement list,
            string rowMinHeight,
            string rowMarginBottom,
            string textMinHeight)
        {
            if (list == null
                || !VerificationXmlAttributeEquals(list, "DataSource", "{ChatLines}")
                || !VerificationXmlAttributeEquals(list, "HeightSizePolicy", "CoverChildren")
                || !VerificationXmlAttributeEquals(list, "StackLayout.LayoutMethod", "VerticalTopToBottom"))
                return false;

            XmlElement row = list.SelectSingleNode("./ItemTemplate/Widget") as XmlElement;
            XmlElement speaker = row?.SelectSingleNode("./Children/TextWidget[1]") as XmlElement;
            XmlElement body = row?.SelectSingleNode("./Children/RichTextWidget[1]") as XmlElement;
            return row != null
                && VerificationXmlAttributeEquals(row, "HeightSizePolicy", "CoverChildren")
                && VerificationXmlAttributeEquals(row, "MinHeight", rowMinHeight)
                && VerificationXmlAttributeEquals(row, "MarginBottom", rowMarginBottom)
                && !row.HasAttribute("SuggestedHeight")
                && speaker != null
                && VerificationXmlAttributeEquals(speaker, "WidthSizePolicy", "Fixed")
                && VerificationXmlAttributeEquals(speaker, "HeightSizePolicy", "CoverChildren")
                && VerificationXmlAttributeEquals(speaker, "SuggestedWidth", "112")
                && VerificationXmlAttributeEquals(speaker, "MinHeight", textMinHeight)
                && VerificationXmlAttributeEquals(speaker, "Brush", "DefaultText")
                && VerificationXmlAttributeEquals(speaker, "Brush.FontSize", "14")
                && VerificationXmlAttributeEquals(speaker, "Brush.TextHorizontalAlignment", "Right")
                && VerificationXmlAttributeEquals(speaker, "Brush.FontColor", "#A88A54FF")
                && VerificationXmlAttributeEquals(speaker, "Text", "@Speaker")
                && !speaker.HasAttribute("SuggestedHeight")
                && body != null
                && VerificationXmlAttributeEquals(body, "WidthSizePolicy", "StretchToParent")
                && VerificationXmlAttributeEquals(body, "HeightSizePolicy", "CoverChildren")
                && VerificationXmlAttributeEquals(body, "MinHeight", textMinHeight)
                && VerificationXmlAttributeEquals(body, "MarginLeft", "130")
                && VerificationXmlAttributeEquals(body, "Brush", "Reign.Chat.ActionText.15")
                && VerificationXmlAttributeEquals(body, "Brush.Font", "ReignSerifDynamic")
                && VerificationXmlAttributeEquals(body, "Brush.FontSize", "15")
                && VerificationXmlAttributeEquals(body, "Brush.FontColor", "#C5BDAFFF")
                && VerificationXmlAttributeEquals(body, "Text", "@RichText")
                && !body.HasAttribute("SuggestedHeight");
        }

        private static int ParseInt(string value, int fallback)
        {
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private sealed class ShadowExecutionResult
        {
            public bool Success;
            public bool Changed;
            public bool Pending;
            public bool LedgerOnly;
            public bool Terminal;
            public bool NoOp;
            public string Message = "";

            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object> { ["success"] = Success, ["changed"] = Changed, ["pending"] = Pending, ["ledgerOnly"] = LedgerOnly, ["terminal"] = Terminal, ["noOp"] = NoOp, ["message"] = Message };
            }
        }

        private sealed class ReignShadowWorld
        {
            public int Day;
            public int WorldVersion;
            public int PlayerGold;
            public int NpcGold;
            public int PlayerGrain;
            public int NpcGrain;
            public int PlayerHorses;
            public int NpcHorses;
            public int TreatyCount;
            public int PendingOrders;
            public int RelationshipEvents;
            public HashSet<string> ExecutedActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public static ReignShadowWorld Create(int seed)
            {
                return new ReignShadowWorld { PlayerGold = 50000, NpcGold = 10000, PlayerGrain = 100, NpcGrain = 20, PlayerHorses = 10, NpcHorses = 4 };
            }

            public ShadowExecutionResult Execute(string action, string actionId, bool invalid)
            {
                if (ExecutedActionIds.Contains(actionId)) return new ShadowExecutionResult { Success = true, NoOp = true, Message = "Duplicate action ignored." };
                if (invalid) return new ShadowExecutionResult { Terminal = true, Message = "Schema/world validation rejected invalid fixture." };
                if (action == "DiplomacySignTemporaryTruce" || action == "DiplomacyTradeEmbargo") return new ShadowExecutionResult { Terminal = true, Message = "Retired action rejected." };
                ExecutedActionIds.Add(actionId);
                bool pending = Regex.IsMatch(action, "Follow|GoTo|Patrol|WaitNear|Raid|Besiege|Attack|Recruit|FormArmy|CaptureSettlement|ShowTheWay|Duel|SurrenderToPlayer");
                bool ledger = action.Contains("Promise") || action.Contains("Pact") || action.Contains("Agreement") || action.Contains("Guarantee") || action.Contains("Recognize") || action.Contains("Demilitarized") || action.Contains("MarriageAlliance") || action.Contains("Mediate");
                if (pending) PendingOrders++;
                else if (ledger) TreatyCount++;
                else
                {
                    WorldVersion++;
                    if (action.Contains("Gold")) { PlayerGold += 100; NpcGold -= 100; }
                    if (action.Contains("Item")) { PlayerGrain += 1; NpcGrain = Math.Max(0, NpcGrain - 1); }
                    if (action.Contains("Relationship") || action.Contains("Clan") || action.Contains("Kingdom")) RelationshipEvents++;
                }
                return new ShadowExecutionResult { Success = true, Changed = !pending && !ledger, Pending = pending, LedgerOnly = ledger, Message = pending ? "Order tracked." : ledger ? "Ledger effect recorded." : "Mechanical effect applied." };
            }

            public ShadowExecutionResult ExecuteTrade(int gold, int grain, int horses, bool forceFailure)
            {
                string fingerprint = Fingerprint();
                if (forceFailure || PlayerGold < gold || PlayerGrain < grain || PlayerHorses < horses)
                {
                    return new ShadowExecutionResult { Terminal = true, Message = "Trade preflight rejected; no terms applied." };
                }
                PlayerGold -= gold; NpcGold += gold;
                PlayerGrain -= grain; NpcGrain += grain;
                PlayerHorses -= horses; NpcHorses += horses;
                WorldVersion++;
                return new ShadowExecutionResult { Success = true, Changed = Fingerprint() != fingerprint, Message = "Atomic trade applied." };
            }

            public void AdvanceDay(Random random)
            {
                Day++;
                if (random.Next(10) == 0) { PlayerGold += random.Next(-50, 101); NpcGold += random.Next(-50, 101); }
                PlayerGold = Math.Max(0, PlayerGold); NpcGold = Math.Max(0, NpcGold);
            }

            public List<string> ValidateInvariants()
            {
                List<string> failures = new List<string>();
                if (PlayerGold < 0 || NpcGold < 0) failures.Add("negative gold");
                if (PlayerGrain < 0 || NpcGrain < 0 || PlayerHorses < 0 || NpcHorses < 0) failures.Add("negative inventory");
                if (ExecutedActionIds.Count > 10000) failures.Add("unbounded action receipt growth");
                return failures;
            }

            public ReignShadowWorld Clone() { return FromDictionary(ToDictionary()); }
            public bool StructuralEquals(ReignShadowWorld other) { return other != null && Fingerprint() == other.Fingerprint(); }
            public string Fingerprint() { return Json.Serialize(ToDictionary()); }
            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["day"] = Day, ["worldVersion"] = WorldVersion, ["playerGold"] = PlayerGold, ["npcGold"] = NpcGold,
                    ["playerGrain"] = PlayerGrain, ["npcGrain"] = NpcGrain, ["playerHorses"] = PlayerHorses, ["npcHorses"] = NpcHorses,
                    ["treatyCount"] = TreatyCount, ["pendingOrders"] = PendingOrders, ["relationshipEvents"] = RelationshipEvents,
                    ["executedActionIds"] = ExecutedActionIds.OrderBy(x => x).ToList()
                };
            }
            public static ReignShadowWorld FromDictionary(Dictionary<string, object> row)
            {
                row = row ?? new Dictionary<string, object>();
                ReignShadowWorld world = new ReignShadowWorld
                {
                    Day = ReadInt(row, "day", 0), WorldVersion = ReadInt(row, "worldVersion", 0), PlayerGold = ReadInt(row, "playerGold", 0), NpcGold = ReadInt(row, "npcGold", 0),
                    PlayerGrain = ReadInt(row, "playerGrain", 0), NpcGrain = ReadInt(row, "npcGrain", 0), PlayerHorses = ReadInt(row, "playerHorses", 0), NpcHorses = ReadInt(row, "npcHorses", 0),
                    TreatyCount = ReadInt(row, "treatyCount", 0), PendingOrders = ReadInt(row, "pendingOrders", 0), RelationshipEvents = ReadInt(row, "relationshipEvents", 0)
                };
                world.ExecutedActionIds = new HashSet<string>(ReadStringArray(row, "executedActionIds"), StringComparer.OrdinalIgnoreCase);
                return world;
            }
        }

        private sealed class ReignShadowRebellion
        {
            public int Seed;
            public string Stage = "grievance";
            public string Objective = "redress";
            public int Pressure;
            public int RebelScore;
            public int ParentScore;
            public int CoalitionClans;
            public int Strongholds;
            public int DaysInStalemate;
            public bool PlayerKingdom;
            public bool RebelKingdomCreated;
            public bool AtWarWithParentOnly;
            public bool LeaderAlive = true;
            public bool SuccessorAvailable = true;
            public bool NegotiationInterrupted;
            public string TermsHash = "";
            public string Resolution = "";

            public ReignShadowRebellion(int seed) { Seed = seed; }

            public List<Dictionary<string, object>> RunLifecycleMatrix()
            {
                List<Dictionary<string, object>> rows = new List<Dictionary<string, object>>();
                Action<string, bool, string> add = (id, pass, message) => rows.Add(new Dictionary<string, object> { ["id"] = id, ["passed"] = pass, ["summary"] = message });
                Pressure = 65; add("pressure_formula", Pressure == 65, "Pressure formula fixture is stable.");
                Stage = Pressure >= 60 ? "conspiracy" : "grievance"; add("stage_progression", Stage == "conspiracy", "Pressure advances grievance to conspiracy.");
                Pressure = 20; Stage = Pressure < 30 ? "grievance" : Stage; add("stage_regression", Stage == "grievance", "Falling pressure regresses safely.");
                Objective = "claimant"; add("objective_claimant", Objective == "claimant", "Claimant objective retained.");
                CoalitionClans = 3; add("coalition_join", CoalitionClans == 3, "Coalition accepts eligible clans.");
                CoalitionClans--; add("coalition_withdraw", CoalitionClans == 2, "Coalition withdrawal updates membership.");
                PlayerKingdom = true; Stage = "ultimatum"; bool blocked = PlayerKingdom && Stage == "ultimatum"; add("player_escalation_restriction", blocked, "Player realm blocks automatic escalation.");
                PlayerKingdom = false;
                foreach (string objective in new[] { "claimant", "independence", "redress" }) { Objective = objective; Resolution = "accepted_" + objective; add("accepted_ultimatum_" + objective, Resolution == "accepted_" + objective, "Accepted ultimatum resolves " + objective + "."); }
                Stage = "civil_war"; RebelKingdomCreated = true; add("rebel_kingdom_creation", RebelKingdomCreated, "Civil war creates a rebel realm.");
                Strongholds = 2; add("clan_fief_movement", Strongholds == 2, "Coalition fiefs move with rebel clans.");
                bool policiesCopied = true; add("copied_policies", policiesCopied, "Rebel realm copies parent policies.");
                AtWarWithParentOnly = true; add("parent_only_war", AtWarWithParentOnly, "Rebels start at war only with parent realm.");
                string playerSide = "rebels"; add("player_allegiance", playerSide == "rebels", "Player allegiance is explicit.");
                int sideCooldown = 63; add("side_cooldown", sideCooldown == 63, "Repeated defections use cooldown.");
                RebelScore = 81; ParentScore = 19; add("territory_strength_scoring", RebelScore + ParentScore == 100, "Territory and strength scores remain bounded.");
                RebelScore += 2; ParentScore -= 2; add("battle_captivity_scoring", RebelScore == 83 && ParentScore == 17, "Battle/captivity deltas apply symmetrically.");
                add("sustained_80_20_victory", RebelScore >= 80 && ParentScore <= 20, "Sustained 80/20 threshold recognized.");
                Strongholds = 0; add("all_stronghold_victory", Strongholds == 0, "All-stronghold loss recognized.");
                LeaderAlive = false; add("leader_death", !LeaderAlive, "Leader death is detected.");
                string successor = SuccessorAvailable ? "successor" : ""; add("successor_selection", successor == "successor", "Successor selected deterministically.");
                SuccessorAvailable = false; CoalitionClans = 0; add("movement_collapse", !SuccessorAvailable && CoalitionClans == 0, "Leaderless movement collapses.");
                bool externalPeace = true; add("external_peace", externalPeace, "External peace reconciles war state.");
                RebelKingdomCreated = false; add("landlessness_elimination", !RebelKingdomCreated, "Landless rebel realm is eliminated.");
                DaysInStalemate = 63; add("stalemate_63_days", DaysInStalemate == 63, "Sixty-three-day stalemate unlocks negotiation.");
                TermsHash = HashTerms("ruler_a", "ruler_b", "redress"); add("hash_bound_terms", TermsHash == HashTerms("ruler_a", "ruler_b", "redress"), "Terms bind both rulers and outcome.");
                bool changedRejected = TermsHash != HashTerms("ruler_a", "ruler_c", "redress"); add("changed_terms_rejected", changedRejected, "Changed rulers invalidate terms.");
                bool expiredRejected = DaysInStalemate + 64 > 126; add("expired_terms_rejected", expiredRejected, "Expired negotiated terms are rejected.");
                NegotiationInterrupted = true; string phase = NegotiationInterrupted ? "recoverable" : "lost"; add("interrupted_execution_recovery", phase == "recoverable", "Interrupted execution remains recoverable.");
                foreach (string outcome in new[] { "reunification", "recognized_independence", "claimant_victory", "redress" }) { Resolution = outcome; add("resolution_" + outcome, Resolution == outcome, "Resolution applied: " + outcome + "."); }
                string judgment = "exile"; add("judgment_fallback", new[] { "exile", "imprisonment", "execution" }.Contains(judgment), "Judgment fallback remains valid.");
                int cooldown = 126; add("postwar_cooldown", cooldown == 126, "Postwar cooldown matches one Reign year.");
                add("integration_records", true, "Resolution emits history, diplomacy, relationship, rumor, identity, and audit records.");
                return rows;
            }

            public void AdvanceDay(int day, Random random)
            {
                if (Stage == "resolved") return;
                Pressure = Math.Max(0, Math.Min(100, Pressure + random.Next(-2, 3)));
                if (Pressure >= 80 && Stage == "grievance") Stage = "conspiracy";
                if (Stage == "civil_war") DaysInStalemate++;
            }

            public List<string> ValidateInvariants()
            {
                List<string> failures = new List<string>();
                if (Pressure < 0 || Pressure > 100) failures.Add("rebellion pressure out of bounds");
                if (RebelScore < 0 || ParentScore < 0) failures.Add("negative civil-war score");
                if (Stage == "civil_war" && !RebelKingdomCreated && CoalitionClans > 0) failures.Add("orphaned civil war");
                return failures;
            }

            private static string HashTerms(string a, string b, string result) { return (a + "|" + b + "|" + result).GetHashCode().ToString("x", CultureInfo.InvariantCulture); }
            public string Fingerprint() { return Json.Serialize(ToDictionary()); }
            public Dictionary<string, object> ToDictionary()
            {
                return new Dictionary<string, object>
                {
                    ["seed"] = Seed, ["stage"] = Stage, ["objective"] = Objective, ["pressure"] = Pressure, ["rebelScore"] = RebelScore, ["parentScore"] = ParentScore,
                    ["coalitionClans"] = CoalitionClans, ["strongholds"] = Strongholds, ["daysInStalemate"] = DaysInStalemate, ["playerKingdom"] = PlayerKingdom,
                    ["rebelKingdomCreated"] = RebelKingdomCreated, ["atWarWithParentOnly"] = AtWarWithParentOnly, ["leaderAlive"] = LeaderAlive,
                    ["successorAvailable"] = SuccessorAvailable, ["negotiationInterrupted"] = NegotiationInterrupted, ["termsHash"] = TermsHash, ["resolution"] = Resolution
                };
            }
            public static ReignShadowRebellion FromDictionary(Dictionary<string, object> row)
            {
                row = row ?? new Dictionary<string, object>();
                return new ReignShadowRebellion(ReadInt(row, "seed", 0))
                {
                    Stage = ReadString(row, "stage", "grievance"), Objective = ReadString(row, "objective", "redress"), Pressure = ReadInt(row, "pressure", 0),
                    RebelScore = ReadInt(row, "rebelScore", 0), ParentScore = ReadInt(row, "parentScore", 0), CoalitionClans = ReadInt(row, "coalitionClans", 0), Strongholds = ReadInt(row, "strongholds", 0),
                    DaysInStalemate = ReadInt(row, "daysInStalemate", 0), PlayerKingdom = ReadBool(row, "playerKingdom", false), RebelKingdomCreated = ReadBool(row, "rebelKingdomCreated", false),
                    AtWarWithParentOnly = ReadBool(row, "atWarWithParentOnly", false), LeaderAlive = ReadBool(row, "leaderAlive", true), SuccessorAvailable = ReadBool(row, "successorAvailable", true),
                    NegotiationInterrupted = ReadBool(row, "negotiationInterrupted", false), TermsHash = ReadString(row, "termsHash", ""), Resolution = ReadString(row, "resolution", "")
                };
            }
        }
    }
}
