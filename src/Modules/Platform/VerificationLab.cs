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
    internal static partial class VerificationService
    {
        private static readonly object VerificationLock = new object();
        internal static readonly string VerificationDir = Path.Combine(Program.TestsDir, "verification");
        private static readonly string VerificationRunsDir = Path.Combine(VerificationDir, "runs");
        private static readonly string VerificationFailuresDir = Path.Combine(VerificationDir, "failures");
        // Keep mutable sandboxes beneath the packaged test-data tree, but make the
        // path deliberately short.  The installed Bannerlord module already has a
        // long base path, and fixture campaign/hero IDs can otherwise cross the
        // legacy Win32 MAX_PATH boundary before SQLite creates its database.
        private static readonly string VerificationSandboxesDir = Path.Combine(Program.TestsDir, "v");
        private static readonly string VerificationCancelPath = Path.Combine(VerificationDir, "cancel.request");
        private static readonly string VerificationGameCommandPath = Path.Combine(VerificationDir, "game-command.json");
        private static readonly string VerificationGameStatusPath = Path.Combine(VerificationDir, "game-status.json");
        private static Dictionary<string, object> VerificationStatus = NewVerificationStatus("idle", "No verification run is active.");
        private static volatile bool VerificationCancelRequested;
        internal static Task VerificationTask;

        internal static Dictionary<string, object> RunVerificationCli(string[] args)
        {
            EnsureVerificationDirectories();
            if (Program.HasArg(args, "--cancel"))
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
                ["failFast"] = Program.HasArg(args, "--fail-fast"),
                ["json"] = Program.HasArg(args, "--json"),
                ["liveCaseCap"] = Math.Max(1, ParseInt(ArgValue(args, "--live-case-cap", "20"), 20)),
                ["gameTimeoutSeconds"] = Math.Max(30, ParseInt(ArgValue(args, "--game-timeout-seconds", "900"), 900))
            };
            string codexPlanPath = ArgValue(args, "--codex-performance-plan", "");
            if (!string.IsNullOrWhiteSpace(codexPlanPath)) options["codexPerformance"] = CampaignFileService.ReadJsonObject(codexPlanPath);
            return RunVerification(options);
        }

        internal static Dictionary<string, object> StartVerificationApi(Dictionary<string, object> payload)
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
                    LlmProviderService.ValidateCodexPerformanceRun(options);
                // Admission precedes any status change, so a refusal leaves the previous status.
                // Lock order is VerificationLock -> scheduler gate only: the scheduler never calls
                // out under its gate, and the outcome continuation below takes VerificationLock on
                // a pool thread, so it cannot observe this run before it is published here.
                PriorityWorkAdmission admission = OperationsService.TryEnqueuePriorityBackgroundWork("verification.run", "normal",
                    () => RunVerification(options), null, out Task scheduled);
                if (admission != PriorityWorkAdmission.Accepted)
                {
                    bool busy = admission == PriorityWorkAdmission.Busy;
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["accepted"] = false,
                        ["reason"] = busy ? OperationsService.PriorityWorkNotStartedBusy : OperationsService.PriorityWorkNotStartedStopping,
                        ["error"] = busy
                            ? "The background scheduler is at capacity; verification was not queued."
                            : "The server is stopping; verification was not queued.",
                        ["status"] = CloneDictionary(VerificationStatus)
                    };
                }
                VerificationCancelRequested = false;
                TryDelete(VerificationCancelPath);
                VerificationStatus = NewVerificationStatus("queued", "Verification run queued.");
                VerificationStatus["tier"] = ReadString(options, "tier", "offline");
                VerificationStatus["suite"] = ReadString(options, "suite", "");
                VerificationTask = scheduled;
                scheduled.ContinueWith(PublishVerificationTerminal, CancellationToken.None,
                    TaskContinuationOptions.NotOnRanToCompletion, TaskScheduler.Default);
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["accepted"] = true,
                    ["status"] = CloneDictionary(VerificationStatus)
                };
            }
        }

        // Canceled means the scheduler stopped before RunVerification started; faulted means it
        // threw before publishing a terminal status. The task identity guard ignores a later run.
        // A user cancel of a queued run is unchanged: RunVerification clears the request on start.
        private static void PublishVerificationTerminal(Task task)
        {
            lock (VerificationLock)
            {
                _ = task.Exception;
                string state = ReadString(VerificationStatus, "state", "");
                if (!ReferenceEquals(VerificationTask, task) || (state != "queued" && state != "running")) return;
                Dictionary<string, object> terminal = task.IsCanceled
                    ? NewVerificationStatus("cancelled", "Verification run was not started because the server is stopping.")
                    : NewVerificationStatus("failed", "Verification run failed unexpectedly; see the server log.");
                terminal["runId"] = ReadString(VerificationStatus, "runId", "");
                terminal["tier"] = ReadString(VerificationStatus, "tier", "");
                terminal["suite"] = ReadString(VerificationStatus, "suite", "");
                VerificationStatus = terminal;
            }
        }

        internal static Dictionary<string, object> VerificationStatusApi()
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

        internal static Dictionary<string, object> CancelVerificationApi()
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

        internal static Dictionary<string, object> VerificationResultsApi(Dictionary<string, string> query)
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
                Dictionary<string, object> run = string.IsNullOrWhiteSpace(path) ? new Dictionary<string, object>() : CampaignFileService.ReadJsonObject(path);
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
                    Dictionary<string, object> row = CampaignFileService.ReadJsonObject(path);
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

        internal static Dictionary<string, object> ReplayVerificationApi(Dictionary<string, object> payload)
        {
            payload = payload ?? new Dictionary<string, object>();
            string runId = ReadString(payload, "runId", "");
            string path = FindVerificationRunPath(runId);
            Dictionary<string, object> prior = string.IsNullOrWhiteSpace(path) ? new Dictionary<string, object>() : CampaignFileService.ReadJsonObject(path);
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

        // Drops the campaigns a verification run created and returns those still registered afterwards.
        // Campaigns registered when the run began, campaigns other processes created, and real campaigns
        // this process created outside the run (for example by a live game during a Control Center run)
        // are never dropped. Internal campaigns this process created with no run attribution while the
        // run was active (work that did not flow the run's context) are treated as the run's own.
        internal static List<string> DropVerificationRunCampaigns(
            CampaignCreationScope scope,
            ISet<string> preexistingCampaigns)
        {
            HashSet<string> owned = new HashSet<string>(
                scope.CreatedCampaignIds.Concat(
                    scope.UnattributedCampaignIds.Where(CampaignFileService.IsInternalCampaignId))
                    .Where(id => !string.IsNullOrWhiteSpace(id)
                        && !preexistingCampaigns.Contains(id)),
                StringComparer.OrdinalIgnoreCase);
            if (owned.Count == 0) return new List<string>();
            foreach (Dictionary<string, object> registered
                in ReignPostgreSqlStorage.ListCampaignMetadata())
            {
                string registeredId = ReadString(
                    registered, "campaignId", "");
                if (!owned.Contains(registeredId)) continue;
                try
                {
                    ReignPostgreSqlStorage.DropCampaign(registeredId);
                }
                catch { }
            }
            return ReignPostgreSqlStorage.ListCampaignMetadata()
                .Select(registered => ReadString(registered, "campaignId", ""))
                .Where(owned.Contains)
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToList();
        }

        private static Dictionary<string, object> RunVerification(Dictionary<string, object> rawOptions)
        {
            EnsureVerificationDirectories();
            Dictionary<string, object> options = NormalizeVerificationOptions(rawOptions);
            string tier = ReadString(options, "tier", "offline").ToLowerInvariant();
            string suite = ReadString(options, "suite", "");
            LlmProviderService.CodexPerformanceBudget previousCodexBudget = LlmProviderService.ActiveCodexPerformanceBudget.Value;
            if (suite == "codex_performance" && tier != "quick" && tier != "offline")
            {
                var codexPlan = LlmProviderService.ValidateCodexPerformanceRun(options);
                LlmProviderService.ActiveCodexPerformanceBudget.Value = new LlmProviderService.CodexPerformanceBudget { Maximum = ReadInt(codexPlan, "totalProviderCallCap", 0) };
            }
            int seed = ReadInt(options, "seed", 1337);
            int repeat = Math.Max(1, ReadInt(options, "repeat", 1));
            bool failFast = ReadBool(options, "failFast", false);
            string runId = "verify-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            string sandboxToken = runId.Length > 8 ? runId.Substring(runId.Length - 8) : runId;
            string sandbox = Path.Combine(VerificationSandboxesDir, "r-" + CampaignFileService.SafePathSegment(sandboxToken, "run"));
            string verificationCampaignsRoot = Path.Combine(sandbox, "c");
            string previousCampaignsRoot = Program.CampaignsRootOverride.Value;
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
            Program.CampaignsRootOverride.Value = verificationCampaignsRoot;
            CampaignCreationScope campaignScope =
                ReignPostgreSqlStorage.BeginCampaignCreationScope();
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
                if (LlmProviderService.ActiveCodexPerformanceBudget.Value != previousCodexBudget)
                {
                    if (LlmProviderService.ActiveCodexPerformanceBudget.Value != null) LlmProviderService.ActiveCodexPerformanceBudget.Value.Closed = true;
                    LlmProviderService.ActiveCodexPerformanceBudget.Value = previousCodexBudget;
                }
                ReignPostgreSqlStorage.EndCampaignCreationScope(campaignScope);
                ReignPostgreSqlStorage.ClearAllPools();
                try
                {
                    List<string> leakedCampaigns = DropVerificationRunCampaigns(
                        campaignScope, preexistingPostgreSqlCampaigns);
                    if (leakedCampaigns.Count > 0)
                    {
                        AddVerificationCheck(checks, "verification.campaign_cleanup", "runner", false,
                            "Verification could not remove " + leakedCampaigns.Count + " campaign(s) it created.",
                            new Dictionary<string, object> { ["campaignIds"] = leakedCampaigns });
                    }
                }
                catch { }
                ReignPostgreSqlStorage.ClearAllPools();
                Program.CampaignsRootOverride.Value = previousCampaignsRoot;
                PersistenceService.TryDeleteDirectory(verificationCampaignsRoot);
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
            if (string.Equals(requestedSuite, "categorized_memory", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "pipeline.categorized_memory", "categorized_memory", DialogueService.RunCategorizedMemorySubsystemSelfTests(), caseResultLimit:64);
                return;
            }
            if (string.Equals(requestedSuite, "live_interaction", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.live_interaction", "live_interaction", WorldSimulationService.RunLiveInteractionTestSelfTests(), caseResultLimit:512);
                return;
            }
            if (explicitSuite && ShouldRunVerificationSuite(requestedSuite, "war_economy_options"))
                AddSubsystemListSummary(checks, "contracts.war_economy_options", "war_economy_options", SettingsService.RunWarEconomyOptionsSelfTests());
            if (string.Equals(requestedSuite, "memory_evidence_replay", StringComparison.OrdinalIgnoreCase))
            {
                var replay = DialogueService.RunMemoryEvidenceReplay();
                AddVerificationCheck(checks, "replay.memory_evidence", "memory_evidence_replay", ReadBool(replay, "ok", false),
                    "Provider-free local evidence and writer replay; captured responses and semantic acceptance remain separate.", replay);
                return;
            }
            if (string.Equals(requestedSuite, "memory_accuracy_replay", StringComparison.OrdinalIgnoreCase))
            {
                var replay = DialogueService.RunMemoryAccuracyReplay();
                AddVerificationCheck(checks, "replay.memory_accuracy", "memory_accuracy_replay", ReadBool(replay, "ok", false),
                    "Provider-free source-only comparison; semantic, full-input and native acceptance remain separate.", replay);
                return;
            }
            if (string.Equals(requestedSuite, "personal_gossip", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.personal_gossip", "personal_gossip", DialogueService.RunPersonalGossipSelfTests());
                return;
            }
            if (string.Equals(requestedSuite, "communications", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.communications", "communications", DialogueService.RunCommunicationSelfTests());
                return;
            }
            if (requestedSuite == "relationship_throughput_contracts" || requestedSuite == "relationship_throughput_smoke"
                || requestedSuite == "relationship_throughput")
            {
                if (!Program.HasArg(Environment.GetCommandLineArgs(), "--run-verification"))
                    throw new InvalidOperationException("Relationship throughput contracts require the isolated Verification Lab CLI.");
                AddSubsystemListSummary(checks, "contracts.relationship_throughput", "relationship_throughput", RelationshipsService.RunRelationshipThroughputSelfTests());
                if (requestedSuite != "relationship_throughput_contracts")
                {
                    var replay = RelationshipsService.RunRelationshipThroughputReplay(sandbox, seed, requestedSuite == "relationship_throughput_smoke");
                    AddVerificationCheck(checks, "performance.relationship_throughput", "relationship_throughput", ReadBool(replay, "ok", false),
                        "Identical-input relationship replay and fixed two-second arrivals; the full suite requires 100 days and p95 service below one second.", replay);
                }
                return;
            }
            if (string.Equals(requestedSuite, "codex_performance", StringComparison.OrdinalIgnoreCase))
            {
                if (!Program.HasArg(Environment.GetCommandLineArgs(), "--run-verification"))
                    throw new InvalidOperationException("Injected Codex contracts require the isolated non-listening Verification Lab CLI through reign_run_offline_verification.");
                AddSubsystemListSummary(checks, "contracts.codex_runtime", "codex_performance", LlmProviderService.RunCodexRuntimeSelfTests());
                AddSubsystemListSummary(checks, "contracts.codex_conversation", "codex_performance", DialogueService.RunCodexConversationSelfTests());
                var integration = LlmProviderService.RunCodexPerformanceIntegrationSelfTests();
                AddVerificationCheck(checks, "contracts.codex_integration", "codex_performance", ReadBool(integration, "ok", false), "Codex settings and physical provider-call cap contracts.", integration);
                var reports = LlmProviderService.RunCodexPerformanceReportSelfTests();
                AddVerificationCheck(checks, "contracts.codex_performance_reports", "codex_performance", ReadBool(reports, "ok", false), "Codex comparison report contracts.", reports);
                return;
            }
            if (string.Equals(requestedSuite, "codex_images", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.codex_images", "portraits_images", PortraitsService.RunPortraitProviderAdapterContractTests());
                return;
            }
            if (string.Equals(requestedSuite, "outfit_changes", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.outfit_changes", "portraits_images", PortraitsService.RunOutfitChangeContractTests());
                AddSubsystemListSummary(checks, "contracts.portrait_refresh", "portraits_images", PortraitsService.RunPortraitRefreshContractTests());
                return;
            }
            if (string.Equals(requestedSuite, "chat_view", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.chat_view", "portraits_images", PortraitsService.RunChatViewContractTests());
                return;
            }
            if (string.Equals(requestedSuite, "character_narrative", StringComparison.OrdinalIgnoreCase)
                || string.Equals(requestedSuite, "contracts", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.character_narrative", "characters", CharactersService.RunCharacterNarrativeTests());
            if (string.Equals(requestedSuite, "prompt_efficiency", StringComparison.OrdinalIgnoreCase))
            {
                RunPromptSizeContract(checks);
                AddSubsystemListSummary(checks, "pipeline.prompt_caching", "prompt_efficiency", DialogueService.RunPromptCachingSelfTests());
            }
            if (ShouldRunVerificationSuite(requestedSuite, "memory_precision"))
                AddSubsystemListSummary(checks, "pipeline.memory_precision", "memory_precision", DialogueService.RunMemoryPrecisionSelfTests());
            if (string.Equals(requestedSuite, "conversation_intoxication", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.conversation_intoxication", "conversation_modes", DialogueService.RunConversationIntoxicationSelfTests());
            if (string.Equals(requestedSuite, "relationships", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.mbti_relationship_lifecycle", "memory_relationships", RelationshipsService.RunCurrentRelationshipSelfTests(), caseResultLimit:512);
            if (string.Equals(requestedSuite, "wanderer_population", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.wanderer_population", "characters", CharactersService.RunWandererPopulationTests());
            if (string.Equals(requestedSuite, "encountered_residents", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.encountered_residents", "characters", CharactersService.RunEncounteredResidentTests());
            if (string.Equals(requestedSuite, "tavern_house", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.tavern_house", "characters", CharactersService.RunTavernHouseContractTests());
            if (string.Equals(requestedSuite, "starting_children", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.starting_children", "characters", CharactersService.RunStartingChildrenTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "campaign_command"))
                AddSubsystemListSummary(checks, "contracts.campaign_command", "campaign_command",
                    WorldSimulationService.RunCampaignCommandSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (!includeBaselines && explicitSuite && ShouldRunVerificationSuite(requestedSuite, "social_reputation"))
                AddSubsystemListSummary(checks, "contracts.social_reputation", "social_reputation", ReputationService.RunRumorSubsystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (!includeBaselines && explicitSuite && ShouldRunVerificationSuite(requestedSuite, "spymaster"))
                AddSubsystemListSummary(checks, "contracts.spymaster", "spymaster", SpymasterService.RunSpymasterSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "government"))
                AddSubsystemListSummary(checks, "contracts.government", "government", GovernmentService.RunGovernmentSystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (string.Equals(requestedSuite, "rulers", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.rulers", "rulers", RelationshipsService.RunRulerJudgmentSelfTests());
            if (string.Equals(requestedSuite, "npc_lives", StringComparison.OrdinalIgnoreCase))
                AddSubsystemListSummary(checks, "contracts.npc_lives", "npc_lives", CharactersService.RunNpcLivesSelfTests());
            if (ShouldRunVerificationSuite(requestedSuite, "contracts") ||
                string.Equals(requestedSuite, "ui_contracts", StringComparison.OrdinalIgnoreCase))
            {
                AddSubsystemListSummary(checks, "contracts.structured_actions", "contracts", DialogueService.RunStructuredActionSelfTests());
                AddSubsystemListSummary(checks, "contracts.npc_lives", "contracts", CharactersService.RunNpcLivesSelfTests());
                AddSubsystemListSummary(checks, "contracts.communications", "contracts", DialogueService.RunCommunicationSelfTests());
                AddSubsystemListSummary(checks, "contracts.rulers", "contracts", RelationshipsService.RunRulerJudgmentSelfTests());
                AddSubsystemListSummary(checks, "contracts.character_editor_history", "characters",
                    CharactersService.RunCharacterEditorHistoryProjectionSelfTests(), caseResultLimit: 16);
                RunBuildAndContractChecks(checks, includeExpensiveSubsystems: explicitSuite);
            }
            if (ShouldStop(checks, failFast)) return;

            if ((includeBaselines || explicitSuite) && ShouldRunVerificationSuite(requestedSuite, "baselines"))
            {
                AddSelfTestSummary(checks, "baseline.diplomacy", "world_diplomacy", DiplomacyService.RunWorldDiplomacySelfTests());
                AddSelfTestSummary(checks, "baseline.world_history", "world_history", WorldSimulationService.RunWorldHistorySelfTests());
                AddSelfTestSummary(checks, "baseline.initialization_readiness", "save_load", Program.RunInitializationReadinessSelfTests());
                AddSelfTestSummary(checks, "baseline.rebellion", "rebellion", RebellionService.RunRebellionSelfTests());
                AddSubsystemListSummary(checks, "baseline.world_test", "world_test", WorldSimulationService.RunWorldTestSelfTests());
            }
        }

        private static void RunOfflineVerificationChecks(List<Dictionary<string, object>> checks, string sandbox, string requestedSuite, int seed, bool failFast)
        {
            if (ShouldRunVerificationSuite(requestedSuite, "campaign_command"))
                WorldSimulationService.RunCampaignCommandOfflineSimulationChecks(checks, sandbox, seed);
            if (ShouldStop(checks, failFast)) return;
            if (!string.IsNullOrWhiteSpace(requestedSuite) && ShouldRunVerificationSuite(requestedSuite, "social_reputation"))
                AddSubsystemListSummary(checks, "architecture.social_reputation", "social_reputation", ReputationService.RunRumorSubsystemSelfTests());
            if (ShouldStop(checks, failFast)) return;
            if (ShouldRunVerificationSuite(requestedSuite, "spymaster")) AddSubsystemListSummary(checks, "architecture.spymaster", "spymaster", SpymasterService.RunSpymasterSystemSelfTests());
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
    }
}
