using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

namespace ReignLiveTest
{
    internal static partial class Program
    {
        private static readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        private const int ReadinessEvaluationTimeoutSeconds = 900;
        private static string BaseUrl = "http://127.0.0.1:5101";
        private static bool JsonOutput;
        private static bool NdjsonOutput;

        private static Dictionary<string, object> RebellionControl(string[] args)
        {
            string profile = Value(args, "--profile", "feature").Trim().ToLowerInvariant();
            string[] supported = { "smoke", "feature", "pledges", "reporting", "summons",
                "verdicts", "declaration", "save_prepare", "save_verify", "resolution", "cleanup" };
            if (!supported.Contains(profile, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unsupported rebellion profile '" + profile + "'.");
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            int timeout = Math.Max(90, IntValue(args, "--timeout", 600));
            string disposableSave = Value(args, "--disposable-save", "");
            List<object> steps = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["operation"] = "rebellion_test", ["profile"] = profile,
                    ["fixtureRunId"] = Value(args, "--fixture-run", "rebellion_release_matrix"),
                    ["disposableSaveName"] = disposableSave, ["timeoutSeconds"] = 300
                }
            };
            if (profile == "save_prepare")
            {
                if (string.IsNullOrWhiteSpace(disposableSave) || !disposableSave.StartsWith("Reign_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("save_prepare requires an exact Reign_ disposable save name.");
                steps.Add(new Dictionary<string, object> { ["operation"] = "save_checkpoint",
                    ["saveName"] = disposableSave, ["timeoutSeconds"] = 300 });
            }
            Dictionary<string, object> started = Post("/tests/live/run/start", new Dictionary<string, object>
            {
                ["schemaVersion"] = 2, ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime), ["mode"] = "individual_chat",
                ["presentation"] = Has(args, "--visible") ? "visible" : "headless",
                ["effects"] = "guarded", ["label"] = "Rebellion preparation " + profile,
                ["autoCompleteWhenIdle"] = true, ["steps"] = steps
            });
            if (!IsOk(started) || Has(args, "--no-wait")) return started;
            return WaitForRun(campaignId, String(started, "runId"), "", timeout, true);
        }

        private static int Main(string[] args)
        {
            args = args ?? new string[0];
            if (args.Length == 0 || Has(args, "--help") || Has(args, "-h") || Has(args, "/?"))
            {
                PrintHelp();
                return 0;
            }
            BaseUrl = Value(args, "--server", Environment.GetEnvironmentVariable("REIGN_SERVER_URL") ?? BaseUrl).TrimEnd('/');
            NdjsonOutput = Has(args, "--ndjson");
            JsonOutput = Has(args, "--json") || NdjsonOutput;
            try
            {
                string command = args[0].Trim().ToLowerInvariant();
                Dictionary<string, object> response;
                switch (command)
                {
                    case "catalog": response = TestingCatalogControl(args); break;
                    case "server": response = ServerLifecycle(args); break;
                    case "server-status": response = ServerLifecycle(args, "status"); break;
                    case "server-start": response = ServerLifecycle(args, "start"); break;
                    case "server-stop": response = ServerLifecycle(args, "stop"); break;
                    case "server-restart": response = ServerLifecycle(args, "restart"); break;
                    case "game": response = GameLifecycle(args); break;
                    case "game-status": response = GameLifecycle(args, "status"); break;
                    case "game-start": response = GameLifecycle(args, "start"); break;
                    case "game-save": response = GameLifecycle(args, "save"); break;
                    case "game-stop": response = GameLifecycle(args, "stop"); break;
                    case "game-restart": response = GameLifecycle(args, "restart"); break;
                    case "qualify": response = QualificationControl(args); break;
                    case "gauntlet": response = FinalGauntletControl(args); break;
                    case "skill-exam": response = SkillExamControl(args); break;
                    case "qualify-status": response = QualificationControl(args, "status"); break;
                    case "social-balance": response = SocialBalanceControl(args); break;
                    case "world-test": response = PassiveWorldControl(args); break;
                    case "arrest": response = ArrestControl(args); break;
                    case "party-agency": response = PartyAgencyControl(args); break;
                    case "rebellion": response = RebellionControl(args); break;
                    case "status": response = Get("/tests/live/runtime" + CampaignQuery(args)); break;
                    case "arm": response = Post("/tests/live/arm", new Dictionary<string, object>
                    {
                        ["confirmation"] = "arm", ["minutes"] = IntValue(args, "--minutes", 30), ["requestedBy"] = "ReignLiveTest.exe"
                    }); break;
                    case "targets": response = Targets(args); break;
                    case "open": response = EnqueueConversationCommand(args, "open", true); break;
                    case "ui-open": response = EnqueueConversationCommand(args, "ui_open", true); break;
                    case "ui-action": response = EnqueueConversationCommand(args, "ui_action", false); break;
                    case "ui-status": response = EnqueueConversationCommand(args, "ui_status", false); break;
                    case "ui-snapshot": response = EnqueueConversationCommand(args, "ui_snapshot", false); break;
                    case "ui-back": response = EnqueueConversationCommand(args, "ui_back", false); break;
                    case "ui-close": response = EnqueueConversationCommand(args, "ui_close", false); break;
                    case "send": response = EnqueueConversationCommand(args, "send", false); break;
                    case "control": response = EnqueueConversationCommand(args, Value(args, "--op", ""), false); break;
                    case "close": response = EnqueueConversationCommand(args, "close", false); break;
                    case "run": response = RunScenario(args); break;
                    case "pause": response = ChangeRunState(args, "pause"); break;
                    case "resume": response = ChangeRunState(args, "resume"); break;
                    case "cancel": response = ChangeRunState(args, "cancel"); break;
                    case "report": response = GetRun(args, true); break;
                    default: throw new InvalidOperationException("Unknown command '" + command + "'. Use --help for commands.");
                }
                Print(response);
                return IsOk(response) ? 0 : 2;
            }
            catch (Exception ex)
            {
                Dictionary<string, object> error = new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
                Print(error, true);
                return 2;
            }
        }

        private static Dictionary<string, object> QualificationControl(string[] args, string forcedOperation = "")
        {
            string operation = string.IsNullOrWhiteSpace(forcedOperation)
                ? args.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? "status"
                : forcedOperation;
            if (operation.Trim().Equals("plan", StringComparison.OrdinalIgnoreCase))
                return QualificationPlanCoverage();
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            switch (operation.Trim().ToLowerInvariant())
            {
                case "status":
                case "report":
                    return Get("/tests/live/readiness?campaignId=" + Uri.EscapeDataString(campaignId));
                case "reset":
                case "start":
                    return Post("/tests/live/readiness/reset", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["qualificationId"] = Value(args, "--qualification", ""),
                        ["baselineSaveName"] = Value(args, "--baseline-save", ""),
                        ["notes"] = Value(args, "--notes", "Unattended conversation readiness qualification")
                    });
                case "run":
                    return RunQualification(args, campaignId, runtime);
                case "checkpoint":
                case "rollback":
                    return Post("/tests/live/readiness/marker", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["type"] = operation,
                        ["passed"] = !Has(args, "--failed"),
                        ["saveName"] = Value(args, "--save", ""),
                        ["gameInstanceId"] = RuntimeInstance(runtime),
                        ["evidence"] = Value(args, "--evidence", "")
                    });
                default:
                    throw new InvalidOperationException("Unknown qualification operation '" + operation + "'. Use plan, start, status, run, checkpoint, rollback, or report.");
            }
        }

        private static Dictionary<string, object> SocialBalanceControl(string[] args)
        {
            string operation = args.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? "status";
            string campaignId = Value(args, "--campaign", "");
            string timelineId = Value(args, "--timeline", "main");
            string runId = Value(args, "--run", "");
            string query = "?campaignId=" + Uri.EscapeDataString(campaignId)
                + "&timelineId=" + Uri.EscapeDataString(timelineId)
                + "&runId=" + Uri.EscapeDataString(runId);
            switch (operation.Trim().ToLowerInvariant())
            {
                case "plan":
                case "manifest":
                    return Get("/tests/social-balance/manifest");
                case "prepare":
                    return Post("/tests/social-balance/prepare", new Dictionary<string, object>
                    {
                        ["confirmation"] = "prepare",
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["runId"] = runId,
                        ["savePrefix"] = Value(args, "--save-prefix", ""),
                        ["mainHeroId"] = Value(args, "--main-hero", "")
                    });
                case "status":
                    return Get("/tests/social-balance/status" + query);
                case "export":
                    return Get("/tests/social-balance/export" + query);
                case "record":
                    return Post("/tests/social-balance/case/result", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["timelineId"] = timelineId,
                        ["runId"] = runId,
                        ["caseId"] = Value(args, "--case", ""),
                        ["periodKey"] = Value(args, "--period", "once"),
                        ["status"] = Value(args, "--status", ""),
                        ["dependencyFingerprint"] = Value(args, "--fingerprint", ""),
                        ["evidence"] = new Dictionary<string, object>
                        {
                            ["summary"] = Value(args, "--evidence", ""),
                            ["recordedBy"] = "ReignLiveTest.exe"
                        }
                    });
                default:
                    throw new InvalidOperationException("Unknown social-balance operation '" + operation
                        + "'. Use plan, prepare, status, record, or export.");
            }
        }

        private static Dictionary<string, object> GameLifecycle(string[] args, string forcedOperation = "")
        {
            EnsureLoopbackLifecycleUrl();
            string operation = string.IsNullOrWhiteSpace(forcedOperation)
                ? args.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? "status"
                : forcedOperation;
            switch (operation.Trim().ToLowerInvariant())
            {
                case "status": return GameStatus(args);
                case "start": return StartGame(args);
                case "start-menu": return StartGameMainMenu(args);
                case "save": return SaveGame(args);
                case "stop": return StopGame(args);
                case "restart":
                    Dictionary<string, object> saved = Has(args, "--no-save")
                        ? new Dictionary<string, object> { ["ok"] = true, ["skipped"] = true }
                        : SaveGame(args);
                    if (!IsOk(saved)) return saved;
                    string saveName = FirstNonEmpty(
                        Value(args, "--save", ""),
                        ReadNestedString(saved, "commands", "result", "saveName"),
                        ReadLastSaveName());
                    Dictionary<string, object> stopped = StopGame(args);
                    if (!IsOk(stopped)
                        && (String(stopped, "status").Equals(
                                "stop_timeout", StringComparison.OrdinalIgnoreCase)
                            || String(stopped, "status").Equals(
                                "no_campaign_heartbeat", StringComparison.OrdinalIgnoreCase)))
                    {
                        // The graceful command is reconciled before StopGame
                        // returns. If BLSE nevertheless leaves its process alive
                        // after the heartbeat stops, use the bridge's existing
                        // guarded force path rather than strand an unattended
                        // checkpoint forever.
                        stopped = StopGame(args.Concat(new[] { "--force" }).ToArray());
                    }
                    if (!IsOk(stopped)) return stopped;
                    return StartGame(args, saveName, "restarted");
                default: throw new InvalidOperationException("Unknown game operation '" + operation + "'. Use status, start, start-menu, save, stop, or restart.");
            }
        }

        private static Dictionary<string, object> GameStatus(string[] args)
        {
            Dictionary<string, object> runtime;
            try { runtime = Get("/tests/live/runtime" + CampaignQuery(args)); }
            catch { runtime = new Dictionary<string, object> { ["ok"] = false }; }
            int[] processIds = GameProcessIds();
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = ReadBoolean(runtime, "gameOnline") ? "campaign_ready" : processIds.Length > 0 ? "running_without_campaign" : "stopped",
                ["running"] = processIds.Length > 0,
                ["campaignReady"] = ReadBoolean(runtime, "gameOnline"),
                ["processIds"] = processIds,
                ["launcherProcessIds"] = GameLauncherProcessIds(),
                ["runtime"] = runtime,
                ["launcherPath"] = FindGameLauncher(args),
                ["lastSaveName"] = ReadLastSaveName()
            };
        }

        private static Dictionary<string, object> SaveGame(string[] args)
        {
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            if (!ReadBoolean(runtime, "gameOnline"))
            {
                // A backgrounded Bannerlord campaign reports on a coarse native
                // interval. Beginning a checkpoint during the brief stale window
                // must wait for a fresh heartbeat instead of treating a healthy
                // loaded campaign as absent.
                runtime = QualificationRuntimeWithHeartbeatGrace(campaignId);
            }
            string saveName = Value(args, "--save", NextRotatingSaveName());
            Dictionary<string, object> start = Post("/tests/live/run/start", new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["mode"] = "individual_chat",
                ["presentation"] = "headless",
                ["effects"] = "guarded",
                ["label"] = "Lifecycle save " + saveName,
                ["autoCompleteWhenIdle"] = true,
                ["steps"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["schemaVersion"] = 2,
                        ["operation"] = "save_checkpoint",
                        ["saveName"] = saveName,
                        ["timeoutSeconds"] = Math.Max(60, IntValue(args, "--wait", 180))
                    }
                }
            });
            if (!IsOk(start)) return start;
            Dictionary<string, object> completed = WaitForRun(
                campaignId, String(start, "runId"), "", Math.Max(90, IntValue(args, "--wait", 240)), true);
            string actual = ReadNestedString(completed, "commands", "result", "saveName");
            if (IsOk(completed) && !string.IsNullOrWhiteSpace(actual)) WriteLastSaveName(actual);
            return completed;
        }

        private static Dictionary<string, object> StopGame(string[] args)
        {
            int[] before = GameProcessIds();
            if (before.Length == 0)
                return new Dictionary<string, object> { ["ok"] = true, ["status"] = "already_stopped", ["running"] = false };
            Dictionary<string, object> runtime = Runtime(args);
            if (Has(args, "--force"))
            {
                Dictionary<string, object> activeRun = ReadObject(runtime, "activeRun");
                if (activeRun.Count > 0 && !IsTerminal(String(activeRun, "status")))
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["status"] = "active_run_not_reconciled",
                        ["running"] = true,
                        ["processIds"] = before,
                        ["error"] = "Force-stop was refused because the server still has a non-terminal live run."
                    };

                if (ReadBoolean(runtime, "gameOnline"))
                {
                    Dictionary<string, object> runtimeState = ReadObject(runtime, "runtime");
                    Dictionary<string, object> saveSync = ReadObject(runtimeState, "saveSync");
                    Dictionary<string, object> nativeSave = ReadObject(runtimeState, "nativeSave");
                    Dictionary<string, object> saveFinalization = ReadObject(saveSync, "saveFinalization");
                    long nativeSequence = ReadLong(nativeSave, "sequence", 0);
                    bool nativeSaveComplete = !ReadBoolean(nativeSave, "isSaving")
                        || (string.Equals(String(nativeSave, "stage"), "native_save_completed",
                                StringComparison.OrdinalIgnoreCase)
                            && ReadBoolean(nativeSave, "succeeded")
                            && ReadLong(nativeSave, "completedSequence", -1) >= nativeSequence);
                    string requestedSaveName = String(saveFinalization, "requestedSaveName");
                    bool finalizationComplete = ReadLong(saveFinalization, "inFlight", 0) == 0
                        && (string.IsNullOrWhiteSpace(requestedSaveName)
                            || (ReadBoolean(saveFinalization, "succeeded")
                                && string.Equals(requestedSaveName,
                                    String(saveFinalization, "completedSaveName"),
                                    StringComparison.OrdinalIgnoreCase)));
                    bool saveSyncReady = !ReadBoolean(saveSync, "alignmentPending")
                        && ReadBoolean(saveSync, "ready");
                    if (!nativeSaveComplete || !finalizationComplete || !saveSyncReady)
                        return new Dictionary<string, object>
                        {
                            ["ok"] = false,
                            ["status"] = "force_stop_save_sync_not_ready",
                            ["running"] = true,
                            ["processIds"] = before,
                            ["error"] = "Force-stop was refused because the current native save or Save Sync alignment is not durably complete.",
                            ["nativeSaveComplete"] = nativeSaveComplete,
                            ["saveFinalizationComplete"] = finalizationComplete,
                            ["saveSyncReady"] = saveSyncReady
                        };
                }

                foreach (int processId in before)
                {
                    try
                    {
                        using (Process process = Process.GetProcessById(processId))
                            process.Kill();
                    }
                    catch { }
                }
                DateTime forceDeadline = DateTime.UtcNow.AddSeconds(
                    Math.Max(20, IntValue(args, "--wait", 60)));
                while (DateTime.UtcNow < forceDeadline && GameProcessIds().Length > 0)
                    Thread.Sleep(500);
                int[] remaining = GameProcessIds();
                return new Dictionary<string, object>
                {
                    ["ok"] = remaining.Length == 0,
                    ["status"] = remaining.Length == 0
                        ? "force_stopped_reconciled_instance"
                        : "force_stop_timeout",
                    ["running"] = remaining.Length > 0,
                    ["processIds"] = remaining,
                    ["previousProcessIds"] = before
                };
            }
            if (!ReadBoolean(runtime, "gameOnline"))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["status"] = "no_campaign_heartbeat",
                    ["running"] = true,
                    ["processIds"] = before,
                    ["error"] = "Bannerlord is running without a fresh campaign heartbeat, so a graceful loopback shutdown cannot be confirmed."
                };
            }
            string campaignId = ResolveCampaign(args, runtime);
            Dictionary<string, object> start = Post("/tests/live/run/start", new Dictionary<string, object>
            {
                ["schemaVersion"] = 2,
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["mode"] = "individual_chat",
                ["presentation"] = "headless",
                ["effects"] = "guarded",
                ["label"] = "Lifecycle shutdown",
                ["autoCompleteWhenIdle"] = true,
                ["steps"] = new object[]
                {
                    new Dictionary<string, object>
                    {
                        ["schemaVersion"] = 2,
                        ["operation"] = "shutdown_game",
                        ["delayMilliseconds"] = 3000,
                        ["timeoutSeconds"] = 30
                    }
                }
            });
            if (!IsOk(start)) return start;
            Dictionary<string, object> accepted = WaitForRun(campaignId, String(start, "runId"), "", 60, true);
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(20, IntValue(args, "--wait", 60)));
            while (DateTime.UtcNow < deadline && GameProcessIds().Length > 0) Thread.Sleep(500);
            int[] after = GameProcessIds();
            return new Dictionary<string, object>
            {
                ["ok"] = IsOk(accepted) && after.Length == 0,
                ["status"] = after.Length == 0 ? "stopped" : "stop_timeout",
                ["running"] = after.Length > 0,
                ["processIds"] = after,
                ["commandResult"] = accepted
            };
        }

        private static Dictionary<string, object> StartGame(string[] args, string explicitSave = "", string successStatus = "started")
        {
            int[] existing = GameProcessIds();
            if (existing.Length > 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "already_running",
                    ["running"] = true,
                    ["processIds"] = existing
                };
            string launcher = FindGameLauncher(args);
            if (!File.Exists(launcher)) throw new FileNotFoundException("The BLSE launcher was not found.", launcher);
            string saveName = FirstNonEmpty(explicitSave, Value(args, "--save", ""), ReadLastSaveName());
            if (string.IsNullOrWhiteSpace(saveName))
                throw new InvalidOperationException("A save name is required for unattended BLSE continuation. Use --save once.");
            string moduleArgument = ReadSelectedModuleArgument();
            string previousGameInstanceId = "";
            try { previousGameInstanceId = RuntimeInstance(Get("/tests/live/runtime")); }
            catch { }
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = "/continuesave \"" + saveName.Replace("\"", "") + "\" \"" + moduleArgument + "\"",
                WorkingDirectory = Path.GetDirectoryName(launcher) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            using (Process launched = Process.Start(start)) { }
            int requestedWaitSeconds = Math.Max(60, IntValue(args, "--wait", 240));
            // A large campaign can finish the native load before Save Sync has copied,
            // compared, restored, and reindexed its server snapshot. The game correctly
            // withholds the fresh heartbeat until alignment is safe. Preserve the
            // caller's ordinary load allowance, then add a separate bounded restoration
            // allowance instead of falsely reporting that /continuesave failed.
            int saveSyncGraceSeconds = Math.Max(0, Math.Min(900,
                IntValue(args, "--save-sync-grace", 420)));
            int readinessWaitSeconds = Math.Min(1800,
                requestedWaitSeconds + saveSyncGraceSeconds);
            DateTime deadline = DateTime.UtcNow.AddSeconds(readinessWaitSeconds);
            Dictionary<string, object> runtime = new Dictionary<string, object>();
            while (DateTime.UtcNow < deadline)
            {
                TryHandleKnownLaunchDialog();
                try
                {
                    runtime = Get("/tests/live/runtime");
                    string currentGameInstanceId = RuntimeInstance(runtime);
                    Dictionary<string, object> saveSync = ReadObject(ReadObject(runtime, "runtime"), "saveSync");
                    if (ReadBoolean(runtime, "gameOnline")
                        && !string.IsNullOrWhiteSpace(currentGameInstanceId)
                        && !string.Equals(currentGameInstanceId, previousGameInstanceId, StringComparison.OrdinalIgnoreCase)
                        && ReadBoolean(saveSync, "ready")
                        && !ReadBoolean(saveSync, "alignmentPending"))
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["status"] = successStatus,
                            ["running"] = true,
                            ["saveName"] = saveName,
                            ["launcherPath"] = launcher,
                            ["readinessWaitSeconds"] = readinessWaitSeconds,
                            ["saveSyncGraceSeconds"] = saveSyncGraceSeconds,
                            ["processIds"] = GameProcessIds(),
                            ["runtime"] = runtime
                        };
                }
                catch { }
                Thread.Sleep(1000);
            }
            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["status"] = "campaign_heartbeat_timeout",
                ["running"] = GameProcessIds().Length > 0,
                ["saveName"] = saveName,
                ["launcherPath"] = launcher,
                ["readinessWaitSeconds"] = readinessWaitSeconds,
                ["saveSyncGraceSeconds"] = saveSyncGraceSeconds,
                ["processIds"] = GameProcessIds(),
                ["error"] = "BLSE started, but no loaded, Save-Sync-aligned campaign heartbeat became ready before the bounded load and restoration timeout."
            };
        }

        private static Dictionary<string, object> StartGameMainMenu(string[] args)
        {
            int[] existing = GameProcessIds();
            if (existing.Length > 0)
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["status"] = "game_already_running",
                    ["running"] = true,
                    ["processIds"] = existing,
                    ["error"] = "Main-menu calibration refuses to attach to an already-running Bannerlord process."
                };

            string launcher = FindGameLauncher(args);
            if (!File.Exists(launcher)) throw new FileNotFoundException("The BLSE launcher was not found.", launcher);
            string moduleArgument = ReadSelectedModuleArgument();
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = "/singleplayer \"" + moduleArgument + "\"",
                WorkingDirectory = Path.GetDirectoryName(launcher) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            using (Process launched = Process.Start(start)) { }

            int waitSeconds = Math.Max(60, Math.Min(600, IntValue(args, "--wait", 240)));
            DateTime deadline = DateTime.UtcNow.AddSeconds(waitSeconds);
            DateTime? initialScreenReadySinceUtc = null;
            string initialScreenLogPath = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                TryHandleKnownLaunchDialog();
                int[] processIds = GameProcessIds();
                if (processIds.Length > 0 && TryFindInitialScreenActivation(processIds, out string readyLogPath))
                {
                    initialScreenLogPath = readyLogPath;
                    if (!initialScreenReadySinceUtc.HasValue)
                        initialScreenReadySinceUtc = DateTime.UtcNow;
                    if (DateTime.UtcNow - initialScreenReadySinceUtc.Value >= TimeSpan.FromSeconds(2))
                    {
                        return new Dictionary<string, object>
                        {
                            ["ok"] = true,
                            ["status"] = "main_menu_ready",
                            ["running"] = true,
                            ["campaignReady"] = false,
                            ["launcherPath"] = launcher,
                            ["processIds"] = processIds,
                            ["readinessMarker"] = "GauntletInitialScreen::HandleActivate",
                            ["readinessLogPath"] = initialScreenLogPath,
                            ["stableInitialScreenSeconds"] = 2
                        };
                    }
                }
                else
                {
                    initialScreenReadySinceUtc = null;
                }
                Thread.Sleep(500);
            }

            return new Dictionary<string, object>
            {
                ["ok"] = false,
                ["status"] = "main_menu_window_timeout",
                ["running"] = GameProcessIds().Length > 0,
                ["campaignReady"] = false,
                ["launcherPath"] = launcher,
                ["processIds"] = GameProcessIds(),
                ["readinessMarker"] = "GauntletInitialScreen::HandleActivate",
                ["error"] = "BLSE started, but the native Gauntlet InitialScreen activation marker was not observed before the bounded main-menu readiness timeout."
            };
        }

        private static bool TryFindInitialScreenActivation(int[] processIds, out string logPath)
        {
            logPath = string.Empty;
            string logRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "Mount and Blade II Bannerlord",
                "logs");
            if (!Directory.Exists(logRoot)) return false;

            foreach (int processId in (processIds ?? Array.Empty<int>()).Distinct())
            {
                string candidate = Path.Combine(logRoot, "rgl_log_" + processId + ".txt");
                if (FileTailContains(candidate, "GauntletInitialScreen::HandleActivate"))
                {
                    logPath = candidate;
                    return true;
                }
            }
            return false;
        }

        private static bool FileTailContains(string path, string marker)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(marker) || !File.Exists(path))
                    return false;
                const int maximumTailBytes = 512 * 1024;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    long start = Math.Max(0L, stream.Length - maximumTailBytes);
                    stream.Seek(start, SeekOrigin.Begin);
                    int tailLength = (int)(stream.Length - start);
                    byte[] tail = new byte[tailLength];
                    int read = 0;
                    while (read < tail.Length)
                    {
                        int count = stream.Read(tail, read, tail.Length - read);
                        if (count <= 0) break;
                        read += count;
                    }
                    return Encoding.UTF8.GetString(tail, 0, read).IndexOf(marker, StringComparison.Ordinal) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private delegate bool EnumChildWindowCallback(IntPtr window, IntPtr state);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumChildWindowCallback callback, IntPtr state);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int maxCount);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

        private static void TryHandleKnownLaunchDialog()
        {
            const uint ButtonClick = 0x00F5;
            foreach (Process process in Process.GetProcessesByName("Bannerlord.BLSE.Standalone"))
            {
                try
                {
                    process.Refresh();
                    if (process.MainWindowHandle == IntPtr.Zero)
                        continue;
                    bool safeMode = string.Equals(process.MainWindowTitle, "Safe Mode", StringComparison.OrdinalIgnoreCase);
                    if (!safeMode)
                        continue;
                    IntPtr noButton = IntPtr.Zero;
                    EnumChildWindows(process.MainWindowHandle, delegate(IntPtr child, IntPtr state)
                    {
                        StringBuilder text = new StringBuilder(64);
                        GetWindowText(child, text, text.Capacity);
                        if (string.Equals(text.ToString().Replace("&", ""), "No", StringComparison.OrdinalIgnoreCase))
                        {
                            noButton = child;
                            return false;
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (noButton != IntPtr.Zero)
                        SendMessage(noButton, ButtonClick, IntPtr.Zero, IntPtr.Zero);
                }
                catch { }
                finally { process.Dispose(); }
            }
        }

        private static string ReadSelectedModuleArgument()
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Mount and Blade II Bannerlord",
                "Configs",
                "LauncherData.xml");
            if (!File.Exists(path))
                throw new FileNotFoundException("Bannerlord LauncherData.xml was not found.", path);
            XmlDocument document = new XmlDocument();
            document.Load(path);
            List<string> selected = new List<string>();
            XmlNodeList nodes = document.SelectNodes("//SingleplayerData/ModDatas/UserModData");
            if (nodes == null)
                throw new InvalidOperationException("Bannerlord LauncherData.xml has no single-player module list.");
            foreach (XmlNode node in nodes)
            {
                string id = node.SelectSingleNode("Id")?.InnerText?.Trim() ?? "";
                string isSelected = node.SelectSingleNode("IsSelected")?.InnerText?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(id) && string.Equals(isSelected, "true", StringComparison.OrdinalIgnoreCase))
                    selected.Add(id);
            }
            if (!selected.Contains("ReignBeta", StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("ReignBeta is not enabled in Bannerlord LauncherData.xml.");
            return "_MODULES_*" + string.Join("*", selected) + "*_MODULES_";
        }

        private static int[] GameProcessIds()
        {
            // Launcher windows are not a running game. In particular, the ordinary
            // BLSE launcher may remain open indefinitely while no campaign exists,
            // but its process can also become the in-process Singleplayer client
            // after Continue. Use the native game-window title to distinguish those
            // states so an active launcher-hosted campaign prevents a second launch.
            return new[] { "Bannerlord", "Bannerlord.Native", "Bannerlord.BLSE.Standalone" }
                .SelectMany(ProcessIds)
                .Concat(BannerlordLauncherProcesses(true))
                .Distinct().OrderBy(x => x).ToArray();
        }

        private static int[] GameLauncherProcessIds()
        {
            return new[]
                {
                    "TaleWorlds.MountAndBlade.Launcher"
                }
                .SelectMany(ProcessIds)
                .Concat(BannerlordLauncherProcesses(false))
                .Distinct().OrderBy(x => x).ToArray();
        }

        private static int[] BannerlordLauncherProcesses(bool hostingGame)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("Bannerlord.BLSE.Launcher");
                try
                {
                    return processes.Where(process =>
                    {
                        try
                        {
                            process.Refresh();
                            string title = process.MainWindowTitle ?? "";
                            bool gameWindow = title.StartsWith(
                                "Mount and Blade II Bannerlord",
                                StringComparison.OrdinalIgnoreCase)
                                && title.IndexOf("PID:", StringComparison.OrdinalIgnoreCase) >= 0;
                            return gameWindow == hostingGame;
                        }
                        catch { return !hostingGame; }
                    }).Select(process => process.Id).OrderBy(id => id).ToArray();
                }
                finally { foreach (Process process in processes) process.Dispose(); }
            }
            catch { return new int[0]; }
        }

        private static string FindGameLauncher(string[] args)
        {
            string explicitPath = Value(args, "--game-launcher", "");
            if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
            string standalone = @"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.BLSE.Standalone.exe";
            return File.Exists(standalone)
                ? standalone
                : @"D:\Program Files (x86)\Steam\steamapps\common\Mount & Blade II Bannerlord\bin\Win64_Shipping_Client\Bannerlord.BLSE.Launcher.exe";
        }

        private static string LastSavePath() { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "qualification-last-save.txt"); }
        private static string ReadLastSaveName()
        {
            try { return File.Exists(LastSavePath()) ? File.ReadAllText(LastSavePath()).Trim() : ""; }
            catch { return ""; }
        }
        private static void WriteLastSaveName(string value)
        {
            try { File.WriteAllText(LastSavePath(), value ?? "", Encoding.UTF8); }
            catch { }
        }
        private static string NextRotatingSaveName()
        {
            return ReadLastSaveName().EndsWith("_A", StringComparison.OrdinalIgnoreCase)
                ? "Reign_Conversation_Qualification_B"
                : "Reign_Conversation_Qualification_A";
        }

        private static string ReadNestedString(
            Dictionary<string, object> value, string listKey, string objectKey, string scalarKey)
        {
            foreach (Dictionary<string, object> row in ReadObjects(value, listKey).AsEnumerable().Reverse())
            {
                string found = String(ReadObject(row, objectKey), scalarKey);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            return "";
        }
        private static bool ReadBoolean(Dictionary<string, object> value, string key)
        {
            return value != null && value.TryGetValue(key, out object raw) && raw != null && Convert.ToBoolean(raw);
        }
        private static long ReadLong(Dictionary<string, object> value, string key, long fallback)
        {
            if (value == null || !value.TryGetValue(key, out object raw) || raw == null) return fallback;
            try { return Convert.ToInt64(raw); }
            catch { return fallback; }
        }
        private static string FirstNonEmpty(params string[] values)
        {
            return (values ?? new string[0]).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        }

        private static Dictionary<string, object> ServerLifecycle(string[] args, string forcedOperation = "")
        {
            EnsureLoopbackLifecycleUrl();
            string operation = string.IsNullOrWhiteSpace(forcedOperation)
                ? args.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? "status"
                : forcedOperation;
            operation = operation.Trim().ToLowerInvariant();
            switch (operation)
            {
                case "status": return ServerStatus();
                case "start": return StartServer(args);
                case "stop": return StopServer(args);
                case "restart":
                    Dictionary<string, object> stopped = StopServer(args);
                    if (!IsOk(stopped)) return stopped;
                    return StartServer(args, "restarted");
                default:
                    throw new InvalidOperationException("Unknown server operation '" + operation + "'. Use status, start, stop, or restart.");
            }
        }

        private static Dictionary<string, object> ServerStatus()
        {
            bool running = TryGetServerHealth(out Dictionary<string, object> health);
            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = running ? "running" : "stopped",
                ["running"] = running,
                ["serverUrl"] = BaseUrl,
                ["health"] = health ?? new Dictionary<string, object>(),
                ["serverProcessIds"] = ProcessIds("ReignBetaServer"),
                ["vectorWorkerProcessIds"] = ProcessIds("ReignVectorWorker"),
                ["launcherPath"] = FindServerLauncher(new string[0])
            };
        }

        private static Dictionary<string, object> StartServer(string[] args, string successStatus = "started")
        {
            if (TryGetServerHealth(out Dictionary<string, object> existing))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "already_running",
                    ["running"] = true,
                    ["serverUrl"] = BaseUrl,
                    ["health"] = existing
                };
            }

            int[] existingProcessIds = ProcessIds("ReignBetaServer");
            if (existingProcessIds.Length > 0)
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["status"] = "unhealthy_process_exists",
                    ["running"] = false,
                    ["serverProcessIds"] = existingProcessIds,
                    ["error"] = "A Reign server process exists but its loopback health endpoint is unavailable. It was not safe to start a second server."
                };
            }

            string launcher = FindServerLauncher(args);
            if (!File.Exists(launcher))
                throw new FileNotFoundException("The visible Reign server launcher was not found.", launcher);

            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = launcher,
                WorkingDirectory = Path.GetDirectoryName(launcher) ?? AppDomain.CurrentDomain.BaseDirectory,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
            Process launched = Process.Start(start);
            try { launched?.Dispose(); } catch { }

            int waitSeconds = Math.Max(5, IntValue(args, "--wait", 45));
            if (!WaitForServerState(true, waitSeconds, out Dictionary<string, object> health))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["status"] = "start_timeout",
                    ["running"] = false,
                    ["serverUrl"] = BaseUrl,
                    ["launcherPath"] = launcher,
                    ["serverProcessIds"] = ProcessIds("ReignBetaServer"),
                    ["error"] = "The visible Reign launcher was started, but the server did not become healthy within " + waitSeconds + " seconds."
                };
            }

            return new Dictionary<string, object>
            {
                ["ok"] = true,
                ["status"] = successStatus,
                ["running"] = true,
                ["serverUrl"] = BaseUrl,
                ["launcherPath"] = launcher,
                ["health"] = health
            };
        }

        private static Dictionary<string, object> StopServer(string[] args)
        {
            if (!TryGetServerHealth(out Dictionary<string, object> before))
            {
                int[] processIds = ProcessIds("ReignBetaServer");
                if (processIds.Length > 0)
                {
                    return new Dictionary<string, object>
                    {
                        ["ok"] = false,
                        ["status"] = "unhealthy_process_exists",
                        ["running"] = false,
                        ["serverProcessIds"] = processIds,
                        ["error"] = "A Reign server process exists but cannot receive the coordinated loopback shutdown request. No process was forcibly terminated."
                    };
                }
                return new Dictionary<string, object>
                {
                    ["ok"] = true,
                    ["status"] = "already_stopped",
                    ["running"] = false,
                    ["serverUrl"] = BaseUrl
                };
            }

            Dictionary<string, object> accepted = Post("/api/shutdown", new Dictionary<string, object>());
            int waitSeconds = Math.Max(5, IntValue(args, "--wait", 30));
            if (!WaitForServerState(false, waitSeconds, out Dictionary<string, object> _))
            {
                return new Dictionary<string, object>
                {
                    ["ok"] = false,
                    ["status"] = "stop_timeout",
                    ["running"] = true,
                    ["serverUrl"] = BaseUrl,
                    ["accepted"] = accepted,
                    ["error"] = "The server accepted shutdown but remained reachable after " + waitSeconds + " seconds."
                };
            }

            DateTime processDeadline = DateTime.UtcNow.AddSeconds(Math.Min(10, waitSeconds));
            while (DateTime.UtcNow < processDeadline
                   && (ProcessIds("ReignBetaServer").Length > 0 || ProcessIds("ReignVectorWorker").Length > 0))
                Thread.Sleep(200);

            int[] serverProcesses = ProcessIds("ReignBetaServer");
            int[] vectorProcesses = ProcessIds("ReignVectorWorker");
            bool lifetimeStopped = serverProcesses.Length == 0 && vectorProcesses.Length == 0;
            return new Dictionary<string, object>
            {
                ["ok"] = lifetimeStopped,
                ["status"] = lifetimeStopped ? "stopped" : "lifetime_processes_remain",
                ["running"] = false,
                ["serverUrl"] = BaseUrl,
                ["previousHealth"] = before,
                ["serverProcessIds"] = serverProcesses,
                ["vectorWorkerProcessIds"] = vectorProcesses,
                ["error"] = lifetimeStopped ? "" : "The health endpoint stopped, but one or more Reign lifetime processes remain."
            };
        }

        private static bool WaitForServerState(bool expectedRunning, int timeoutSeconds, out Dictionary<string, object> health)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            health = new Dictionary<string, object>();
            do
            {
                bool running = TryGetServerHealth(out Dictionary<string, object> current);
                if (running == expectedRunning)
                {
                    health = current ?? new Dictionary<string, object>();
                    return true;
                }
                Thread.Sleep(250);
            }
            while (DateTime.UtcNow < deadline);
            return false;
        }

        private static bool TryGetServerHealth(out Dictionary<string, object> health)
        {
            health = new Dictionary<string, object>();
            try
            {
                using (HttpClient probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
                using (HttpResponseMessage response = probe.GetAsync(BaseUrl + "/health").GetAwaiter().GetResult())
                {
                    if (!response.IsSuccessStatusCode) return false;
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    health = Json.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
                    return IsOk(health);
                }
            }
            catch
            {
                health = new Dictionary<string, object>();
                return false;
            }
        }

        private static int[] ProcessIds(string processName)
        {
            try
            {
                Process[] processes = Process.GetProcessesByName(processName);
                try { return processes.Select(x => x.Id).OrderBy(x => x).ToArray(); }
                finally { foreach (Process process in processes) process.Dispose(); }
            }
            catch { return new int[0]; }
        }

        private static string FindServerLauncher(string[] args)
        {
            string explicitPath = Value(args ?? new string[0], "--launcher", "");
            if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
            string appDirectory = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string serverDirectory = Directory.GetParent(appDirectory)?.FullName ?? appDirectory;
            string moduleDirectory = Directory.GetParent(serverDirectory)?.FullName ?? serverDirectory;
            string unifiedLauncher = Path.Combine(moduleDirectory, "Start ReignBeta Server.cmd");
            if (File.Exists(unifiedLauncher)) return unifiedLauncher;
            return Path.Combine(serverDirectory, "Start ReignBeta Server.cmd");
        }

        private static void EnsureLoopbackLifecycleUrl()
        {
            if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out Uri uri)
                || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                     || (IPAddress.TryParse(uri.Host, out IPAddress address) && IPAddress.IsLoopback(address))))
            {
                throw new InvalidOperationException("Server lifecycle control is restricted to a loopback HTTP address.");
            }
        }

        private static Dictionary<string, object> Targets(string[] args)
        {
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            Dictionary<string, object> request =
                new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["gameInstanceId"] = RuntimeInstance(runtime),
                ["mode"] = Mode(args),
                ["search"] = Value(args, "--search", Value(args, "--target", "")),
                ["limit"] = IntValue(args, "--limit", 25)
            };
            if (Has(args, "--lords-only"))
                request["isLord"] = true;
            if (Has(args, "--non-lords-only"))
                request["isLord"] = false;
            if (Has(args, "--minimum-age"))
                request["minimumAge"] =
                    IntValue(args, "--minimum-age", 0);
            if (Has(args, "--min-clan-tier"))
                request["minClanTier"] =
                    IntValue(args, "--min-clan-tier", 0);
            if (Has(args, "--max-clan-tier"))
                request["maxClanTier"] =
                    IntValue(args, "--max-clan-tier", 6);
            if (Has(args, "--max-honor"))
                request["maxHonor"] =
                    IntValue(args, "--max-honor", 2);
            Dictionary<string, object> result = Post(
                "/tests/live/targets/search", request);
            return MaybeWait(args, campaignId, String(result, "runId"), String(ReadObject(result, "command"), "commandId"));
        }

        private static Dictionary<string, object> EnqueueConversationCommand(string[] args, string operation, bool startsRun)
        {
            if (string.IsNullOrWhiteSpace(operation)) throw new InvalidOperationException("control requires --op.");
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            string runId = Value(args, "--run", "");
            string commandMode = Mode(args);
            bool startedNewRun = false;
            if (startsRun && string.IsNullOrWhiteSpace(runId))
            {
                Dictionary<string, object> start = Post("/tests/live/run/start", new Dictionary<string, object>
                {
                    ["campaignId"] = campaignId,
                    ["gameInstanceId"] = RuntimeInstance(runtime),
                    ["mode"] = commandMode,
                    ["presentation"] = Value(args, "--presentation", Has(args, "--visible") ? "visible" : "headless"),
                    ["effects"] = Value(args, "--effects", Has(args, "--full-effects") ? "full" : "guarded"),
                    ["label"] = Value(args, "--label", "Live " + commandMode)
                });
                if (!IsOk(start)) return start;
                runId = String(start, "runId");
                startedNewRun = true;
            }
            if (string.IsNullOrWhiteSpace(runId)) runId = ActiveRunId(runtime);
            if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("No active run exists. Use open first or supply --run.");
            if (!startsRun && !Has(args, "--mode"))
            {
                string activeMode = String(ReadObject(runtime, "activeRun"), "mode");
                if (!string.IsNullOrWhiteSpace(activeMode)) commandMode = NormalizeMode(activeMode);
            }
            int commandTimeoutSeconds = IntValue(args, "--timeout", DefaultCommandTimeoutSeconds(commandMode, operation));

            List<string> targets = Values(args, "--target");
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["runId"] = runId,
                ["commandId"] = Value(args, "--command-id", ""),
                ["mode"] = commandMode,
                ["operation"] = operation,
                ["text"] = Value(args, "--text", PositionalText(args, operation)),
                ["value"] = Value(args, "--value", ""),
                ["targetSearch"] = targets.FirstOrDefault() ?? Value(args, "--search", ""),
                ["targetSearches"] = targets,
                ["templateId"] = Value(args, "--template", ""),
                ["createTestEvent"] = !Has(args, "--existing-event"),
                ["eventId"] = Value(args, "--event", ""),
                ["presentation"] = Value(args, "--presentation", Has(args, "--visible") ? "visible" : "headless"),
                ["effects"] = Value(args, "--effects", Has(args, "--full-effects") ? "full" : "guarded"),
                ["sceneIndex"] = IntValue(args, "--scene", 0),
                ["turnIndex"] = IntValue(args, "--turn", 0),
                ["timeoutSeconds"] = commandTimeoutSeconds
            };
            Dictionary<string, object> response = Post("/tests/live/command/enqueue", payload);
            if (!IsOk(response))
            {
                response["runId"] = runId;
                if (startedNewRun)
                {
                    response["cancelResult"] = Post("/tests/live/run/cancel", new Dictionary<string, object>
                    {
                        ["campaignId"] = campaignId,
                        ["runId"] = runId
                    });
                }
                return response;
            }
            return MaybeWait(args, campaignId, runId, String(ReadObject(response, "command"), "commandId"), commandTimeoutSeconds);
        }

        private static Dictionary<string, object> RunScenario(string[] args)
        {
            string file = args.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) throw new FileNotFoundException("A scenario JSON file is required.", file);
            Dictionary<string, object> scenario = Json.Deserialize<Dictionary<string, object>>(File.ReadAllText(file, Encoding.UTF8)) ?? new Dictionary<string, object>();
            Dictionary<string, object> runtime = Runtime(args);
            scenario["campaignId"] = ResolveCampaign(args, runtime, String(scenario, "campaignId"));
            scenario["gameInstanceId"] = RuntimeInstance(runtime);
            if (!scenario.ContainsKey("schemaVersion")) scenario["schemaVersion"] = 2;
            if (!scenario.ContainsKey("presentation")) scenario["presentation"] = Has(args, "--visible") ? "visible" : "headless";
            if (!scenario.ContainsKey("effects")) scenario["effects"] = Has(args, "--full-effects") ? "full" : "guarded";
            if (!scenario.ContainsKey("autoCompleteWhenIdle")) scenario["autoCompleteWhenIdle"] = true;
            Dictionary<string, object> started = Post("/tests/live/run/start", scenario);
            if (!IsOk(started) || Has(args, "--no-wait")) return started;
            return WaitForRun(String(started, "campaignId"), String(started, "runId"), "", IntValue(args, "--timeout", 1800), true);
        }

        private static Dictionary<string, object> ChangeRunState(string[] args, string operation)
        {
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            string runId = Value(args, "--run", ActiveRunId(runtime));
            if (string.IsNullOrWhiteSpace(runId)) throw new InvalidOperationException("No run id was supplied and no active run exists.");
            return Post("/tests/live/run/" + operation, new Dictionary<string, object> { ["campaignId"] = campaignId, ["runId"] = runId });
        }

        private static Dictionary<string, object> GetRun(string[] args, bool report)
        {
            Dictionary<string, object> runtime = Runtime(args);
            string campaignId = ResolveCampaign(args, runtime);
            string runId = Value(args, "--run", ActiveRunId(runtime));
            string route = report ? "/tests/live/run/report" : "/tests/live/run/status";
            return Get(route + "?campaignId=" + Uri.EscapeDataString(campaignId) + (string.IsNullOrWhiteSpace(runId) ? "" : "&runId=" + Uri.EscapeDataString(runId)));
        }

        private static Dictionary<string, object> MaybeWait(string[] args, string campaignId, string runId, string commandId, int defaultTimeoutSeconds = 300)
        {
            if (Has(args, "--no-wait")) return Get("/tests/live/run/status?campaignId=" + Uri.EscapeDataString(campaignId) + "&runId=" + Uri.EscapeDataString(runId));
            return WaitForRun(campaignId, runId, commandId, IntValue(args, "--timeout", defaultTimeoutSeconds), false);
        }

        private static int DefaultCommandTimeoutSeconds(string mode, string operation)
        {
            string normalizedMode = NormalizeMode(mode);
            string normalizedOperation = (operation ?? "").Trim().ToLowerInvariant();
            if (normalizedOperation == "close" || normalizedOperation == "scene_boundary")
            {
                // Production closure may include an accuracy-preserving scene-summary
                // provider call before the game can report the command result. Keep
                // its liveness boundary distinct from an ordinary dialogue turn so
                // a slow but successful summary is not cancelled and then ignored.
                return 900;
            }
            if (normalizedOperation == "send"
                && (normalizedMode == "party_chat" || normalizedMode == "social_event" || normalizedMode == "wilderness_event"))
            {
                // Group turns are deliberately sequential so later speakers can
                // react to earlier contributions. The controller must wait long
                // enough for several legitimate provider calls and must never
                // encourage a caller to retry an accepted exactly-once command.
                return 900;
            }
            return 300;
        }

        private static Dictionary<string, object> WaitForRun(string campaignId, string runId, string commandId, int timeoutSeconds, bool waitForRun)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, timeoutSeconds));
            Dictionary<string, object> latest = new Dictionary<string, object>();
            string priorFingerprint = "";
            DateTime nextLivenessCheck = DateTime.MinValue;
            while (DateTime.UtcNow < deadline)
            {
                latest = Get("/tests/live/run/status?campaignId=" + Uri.EscapeDataString(campaignId) + "&runId=" + Uri.EscapeDataString(runId));
                string runStatus = String(latest, "status");
                if (NdjsonOutput)
                {
                    List<Dictionary<string, object>> commands = ReadObjects(latest, "commands");
                    string fingerprint = runStatus + "|" + string.Join(";", commands.Select(x => String(x, "commandId") + ":" + String(x, "status")));
                    if (!fingerprint.Equals(priorFingerprint, StringComparison.Ordinal))
                    {
                        priorFingerprint = fingerprint;
                        Console.WriteLine(Json.Serialize(new Dictionary<string, object>
                        {
                            ["event"] = "progress", ["runId"] = runId, ["campaignId"] = campaignId,
                            ["status"] = runStatus, ["commands"] = commands, ["utc"] = DateTimeOffset.UtcNow.ToString("o")
                        }));
                    }
                }
                if (waitForRun && IsTerminal(runStatus)) return latest;
                if (waitForRun && DateTime.UtcNow >= nextLivenessCheck)
                {
                    nextLivenessCheck = DateTime.UtcNow.AddSeconds(5);
                    Dictionary<string, object> stale = InterruptStaleAcceptedCommand(campaignId, runId, latest);
                    if (stale != null) return stale;
                }
                if (!waitForRun)
                {
                    Dictionary<string, object> command = ReadObjects(latest, "commands").FirstOrDefault(x => String(x, "commandId").Equals(commandId, StringComparison.OrdinalIgnoreCase));
                    string status = String(command, "status");
                    if (IsTerminal(status) || status == "needs_input") return latest;
                }
                Thread.Sleep(500);
            }
            latest["ok"] = false;
            latest["errorCode"] = "controller_wait_timeout";
            latest["error"] = "Timed out waiting for the live interaction result.";
            latest["runStillActive"] = !IsTerminal(String(latest, "status"));
            latest["waitedSeconds"] = Math.Max(10, timeoutSeconds);
            return latest;
        }

        private static Dictionary<string, object> InterruptStaleAcceptedCommand(
            string campaignId,
            string runId,
            Dictionary<string, object> latest)
        {
            Dictionary<string, object> command = ReadObjects(latest, "commands")
                .FirstOrDefault(item =>
                {
                    string status = String(item, "status");
                    return status.Equals("accepted", StringComparison.OrdinalIgnoreCase)
                        || status.Equals("running", StringComparison.OrdinalIgnoreCase);
                });
            if (command == null) return null;
            if (!DateTimeOffset.TryParse(String(command, "updatedUtc"), out DateTimeOffset updated)) return null;

            int commandTimeout = Math.Max(30, (int)ReadLong(command, "timeoutSeconds", 300));
            double ageSeconds = (DateTimeOffset.UtcNow - updated).TotalSeconds;
            bool beyondDeclaredTimeout = ageSeconds > commandTimeout + 15;
            if (!beyondDeclaredTimeout && ageSeconds < 90) return null;

            Dictionary<string, object> runtime = Get(
                "/tests/live/runtime?campaignId=" + Uri.EscapeDataString(campaignId));
            bool gameOnline = ReadBoolean(runtime, "gameOnline");
            string operation = String(command, "operation").Trim().ToLowerInvariant();
            bool heartbeatPauseExpected = HeartbeatPauseExpectedOperation(operation);
            bool expectedPauseStillViable = heartbeatPauseExpected
                && (!operation.Equals("world_advance", StringComparison.OrdinalIgnoreCase)
                    || GameProcessIds().Length > 0);
            if (!beyondDeclaredTimeout
                && (gameOnline || expectedPauseStillViable))
                return null;

            string commandId = String(command, "commandId");
            string expectedCorrelation = "live-" + commandId;
            Dictionary<string, object> audit = Get(
                "/audit/query?campaignId=" + Uri.EscapeDataString(campaignId)
                + "&correlationId=" + Uri.EscapeDataString(expectedCorrelation)
                + "&limit=10");
            int auditCount = (int)ReadLong(audit, "count", 0);
            Dictionary<string, object> cancelled = Post("/tests/live/run/cancel", new Dictionary<string, object>
            {
                ["campaignId"] = campaignId,
                ["runId"] = runId
            });
            latest["ok"] = false;
            latest["status"] = "interrupted";
            latest["error"] = auditCount > 0
                ? "The accepted command exceeded its timeout and has correlated server evidence. It was cancelled and will not be retried automatically."
                : "The accepted command exceeded its liveness boundary without correlated server evidence. It was cancelled before campaign recovery.";
            latest["interruption"] = new Dictionary<string, object>
            {
                ["reason"] = gameOnline ? "accepted_command_timeout" : "campaign_heartbeat_stale",
                ["commandId"] = commandId,
                ["expectedCorrelationId"] = expectedCorrelation,
                ["commandAgeSeconds"] = Math.Round(ageSeconds, 1),
                ["commandTimeoutSeconds"] = commandTimeout,
                ["gameOnline"] = gameOnline,
                ["correlatedAuditEntryCount"] = auditCount,
                ["safeToReplayAfterCheckpointReload"] = auditCount == 0,
                ["cancelResult"] = cancelled
            };
            return latest;
        }

        private static bool HeartbeatPauseExpectedOperation(string operation)
        {
            string normalized = (operation ?? "").Trim().ToLowerInvariant();
            // Bannerlord performs checkpoint serialization on the main campaign
            // thread. Large saves can legitimately stop heartbeat polling for
            // longer than the ordinary 90-second crash heuristic, while the
            // command itself remains exactly-once and declares a larger timeout.
            // Shutdown likewise intentionally removes the heartbeat. Long world
            // advances can pause it while a synchronous diplomacy evaluation is
            // legitimately in flight; keep honoring that command only while the
            // Bannerlord process remains present. Do not cancel these operations
            // before their declared timeout plus grace.
            return normalized == "save_checkpoint"
                || normalized == "shutdown_game"
                || normalized == "world_advance";
        }

        private static Dictionary<string, object> Runtime(string[] args)
        {
            Dictionary<string, object> runtime = Get("/tests/live/runtime" + CampaignQuery(args));
            if (!IsOk(runtime)) throw new InvalidOperationException(String(runtime, "error"));
            return runtime;
        }

        private static string ResolveCampaign(string[] args, Dictionary<string, object> runtimeResponse, string fallback = "")
        {
            string explicitId = Value(args, "--campaign", fallback);
            if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId;
            string found = String(ReadObject(runtimeResponse, "runtime"), "campaignId");
            if (string.IsNullOrWhiteSpace(found)) throw new InvalidOperationException("No loaded campaign heartbeat was found. Load a campaign and leave the game running.");
            return found;
        }

        private static string RuntimeInstance(Dictionary<string, object> runtimeResponse) { return String(ReadObject(runtimeResponse, "runtime"), "gameInstanceId"); }
        private static string ActiveRunId(Dictionary<string, object> runtimeResponse) { return String(ReadObject(runtimeResponse, "activeRun"), "runId"); }
        private static string Mode(string[] args) { return NormalizeMode(Value(args, "--mode", "individual_chat")); }
        private static string NormalizeMode(string value) { return (value ?? "individual_chat").Trim().ToLowerInvariant().Replace('-', '_'); }
        private static string CampaignQuery(string[] args) { string c = Value(args, "--campaign", ""); return string.IsNullOrWhiteSpace(c) ? "" : "?campaignId=" + Uri.EscapeDataString(c); }

        private static Dictionary<string, object> Get(string route)
        {
            using (HttpResponseMessage response = Client.GetAsync(BaseUrl + route).GetAwaiter().GetResult())
            {
                string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + body);
                return Json.Deserialize<Dictionary<string, object>>(body) ?? new Dictionary<string, object>();
            }
        }

        private static Dictionary<string, object> Post(string route, Dictionary<string, object> payload)
        {
            return PostWithTimeout(route, payload, Client);
        }

        private static Dictionary<string, object> PostWithTimeout(
            string route,
            Dictionary<string, object> payload,
            int timeoutSeconds)
        {
            using (HttpClient client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds))
            })
                return PostWithTimeout(route, payload, client);
        }

        private static Dictionary<string, object> PostWithTimeout(
            string route,
            Dictionary<string, object> payload,
            HttpClient client)
        {
            string body = Json.Serialize(payload ?? new Dictionary<string, object>());
            using (StringContent content = new StringContent(body, Encoding.UTF8, "application/json"))
            using (HttpResponseMessage response = client.PostAsync(BaseUrl + route, content).GetAwaiter().GetResult())
            {
                string json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("HTTP " + (int)response.StatusCode + ": " + json);
                return Json.Deserialize<Dictionary<string, object>>(json) ?? new Dictionary<string, object>();
            }
        }

        private static void Print(Dictionary<string, object> value, bool error = false)
        {
            string output = Json.Serialize(value ?? new Dictionary<string, object>());
            if (!JsonOutput && value != null)
            {
                string ok = IsOk(value) ? "OK" : "FAILED";
                string run = String(value, "runId");
                string status = String(value, "status");
                string message = String(value, "error");
                output = ok + (string.IsNullOrWhiteSpace(status) ? "" : " - " + status) + (string.IsNullOrWhiteSpace(run) ? "" : " - " + run)
                    + (string.IsNullOrWhiteSpace(message) ? "" : Environment.NewLine + message) + Environment.NewLine + Json.Serialize(value);
            }
            if (error) Console.Error.WriteLine(output); else Console.WriteLine(output);
        }

        private static bool IsOk(Dictionary<string, object> value)
        {
            if (value == null
                || !value.TryGetValue("ok", out object raw)
                || !Convert.ToBoolean(raw))
                return false;
            string status = String(value, "status");
            return !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(status, "interrupted", StringComparison.OrdinalIgnoreCase);
        }
        private static bool IsTerminal(string value) { return new[] { "completed", "failed", "cancelled", "interrupted" }.Contains(value ?? "", StringComparer.OrdinalIgnoreCase); }
        private static string String(Dictionary<string, object> value, string key) { return value != null && value.TryGetValue(key, out object raw) && raw != null ? Convert.ToString(raw) : ""; }
        private static Dictionary<string, object> ReadObject(Dictionary<string, object> value, string key) { return value != null && value.TryGetValue(key, out object raw) ? raw as Dictionary<string, object> ?? new Dictionary<string, object>() : new Dictionary<string, object>(); }
        private static List<Dictionary<string, object>> ReadObjects(Dictionary<string, object> value, string key)
        {
            if (value == null || !value.TryGetValue(key, out object raw)) return new List<Dictionary<string, object>>();
            IEnumerable<object> rows;
            if (raw is ArrayList list) rows = list.Cast<object>();
            else if (raw is object[] array) rows = array;
            else if (raw is IEnumerable sequence && !(raw is string)) rows = sequence.Cast<object>();
            else rows = Enumerable.Empty<object>();
            return rows.OfType<Dictionary<string, object>>().ToList();
        }
        private static bool Has(string[] args, string name) { return args.Any(x => x.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        private static string Value(string[] args, string name, string fallback)
        {
            for (int i = 0; i < args.Length - 1; i++) if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return fallback;
        }
        private static List<string> Values(string[] args, string name)
        {
            List<string> result = new List<string>();
            for (int i = 0; i < args.Length - 1; i++) if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) result.Add(args[i + 1]);
            return result;
        }
        private static int IntValue(string[] args, string name, int fallback) { return int.TryParse(Value(args, name, ""), out int value) ? value : fallback; }
        private static string PositionalText(string[] args, string operation)
        {
            if (operation != "send") return "";
            return args.Skip(1).FirstOrDefault(x => !x.StartsWith("-", StringComparison.Ordinal)) ?? "";
        }

        private static void PrintHelp()
        {
            Console.WriteLine("Bannerlord Reign Live Interaction Test Bridge");
            Console.WriteLine("Usage: ReignLiveTest.exe <command> [options]");
            Console.WriteLine("Commands: catalog, server, game, qualify, gauntlet, skill-exam, social-balance, world-test, party-agency, status, arm, targets, open, ui-open, ui-action, ui-status, ui-snapshot, ui-back, ui-close, send, control, close, run, pause, resume, cancel, report");
            Console.WriteLine("Catalog: ReignLiveTest.exe catalog --json");
            Console.WriteLine("Server: ReignLiveTest.exe server status|start|stop|restart [--wait <seconds>] [--json]");
            Console.WriteLine("Game:   ReignLiveTest.exe game status|start|start-menu|save|stop|restart [--save <name>] [--wait <seconds>] [--save-sync-grace <seconds>] [--json]");
            Console.WriteLine("        start-menu launches the selected Reign module set visibly without loading a campaign and waits for a stable native game window; use guarded game stop --force after main-menu-only calibration.");
            Console.WriteLine("Qualify: ReignLiveTest.exe qualify plan|start|status|run|checkpoint|rollback|report [options]");
            Console.WriteLine("Gauntlet: ReignLiveTest.exe gauntlet plan|roleplay-plan|roleplay|start|status|resume|cancel|report|review-pack [--stage all|a|b] [--stage-a-case STAGEA-*] [--no-wait]");
            Console.WriteLine("Skill exam: ReignLiveTest.exe skill-exam plan|start|status|pause|resume|report [--save ConvTest_SkillExam] [--seed 1042]");
            Console.WriteLine("         qualify run --focus identity_role_authority|identity_role_authority_modes|clan_tier|manipulation|lie_relationship resumes only that focused gate");
            Console.WriteLine("         add --prepare-authority-fixture to create the confirmed disposable sovereign/same-clan/family/governor fixture before an identity-authority gate");
            Console.WriteLine("Social: ReignLiveTest.exe social-balance plan|prepare|status|record|export [options]");
            Console.WriteLine("        prepare requires --campaign --timeline --save-prefix Reign_SocialBalance_* --main-hero");
            Console.WriteLine("World:  ReignLiveTest.exe world-test status|snapshot|stop|drain|advance|checkpoint|delete-checkpoint [--days N|--target-day D] [--save name] [--prefix run-prefix] [--no-save] [--initial-baseline] [--timeout seconds] [--catch-up-timeout seconds] [--save-timeout seconds]");
            Console.WriteLine("        --initial-baseline is reserved for the guarded campaign-test arm path; it accepts only an absent pre-existing daily rollup after exact native baseline observation and confirmation. All live queues and failure counters still must be clean.");
            Console.WriteLine("        drain, advance, and checkpoint emit bounded receipts; use their reportPath references for full live-test evidence.");
            Console.WriteLine("Party agency: ReignLiveTest.exe party-agency --profile contracts|status|preflight [--campaign id] [--no-wait]");
            Console.WriteLine("              Mutating and natural-language release cases require the guarded reign_start_party_agency_test MCP tool and an armed campaign-test Current save.");
            Console.WriteLine("              Positive invitations provide purpose, risk, NPC relevance, term or review, free choice, and an explicit request for agreement; conditional interest is not consent.");
            Console.WriteLine("Common: --campaign <id> --run <id> --mode individual_chat|party_chat|social_event|wilderness_event|social_balance --json|--ndjson");
            Console.WriteLine("Targets: [--lords-only|--non-lords-only] [--minimum-age N] [--min-clan-tier N] [--max-clan-tier N] [--max-honor -2..2] [--limit N]");
            Console.WriteLine("Open:   --target <name-or-id> (repeat for groups) [--visible] [--full-effects]");
            Console.WriteLine("UI:     ReignLiveTest.exe ui-open --target court|court-petition|castle-layout|castle-chat|ambassador|economic-report|clan-accords|government|family-chambers|training-yard|royal-council|spymaster|war-council|correspondence|individual-chat|party-chat|tavern-house|social-event|wilderness-event|diplomacy-announcement|notable-generation|memories-book|native-game-menu|native-encyclopedia-hero|native-clan-members|native-marriage-offer|native-heir-selection|native-skill-grid-item|native-character-developer|native-crafting-hero|native-crafting|native-education|native-recruit-volunteer|native-game-menu-party|native-conversation|native-quests [--timeout 120]");
            Console.WriteLine("        Open readiness: ordinary standalone targets use a bounded 15-second native open/layout deadline; War Council alone uses 120 seconds for cold 16K tile loading and still requires at least two native late-update frames. --timeout only bounds controller observation and does not replace that proof.");
            Console.WriteLine("        Native augmentation targets open the real production Bannerlord screen or popup on the loaded no-save calibration campaign; native-initial-screen uses game start-menu before a campaign exists.");
            Console.WriteLine("        ui-open Royal Council calibration uses a non-persistent four-domain transcript fixture and makes zero provider calls; ordinary player entry remains production-backed.");
            Console.WriteLine("        ui-open Court Petition calibration uses a temporary provider-free petition fixture; its decision controls cannot mutate campaign state.");
            Console.WriteLine("        Guarded ruler_docket_test phases create native needs only on the exact armed Current, open the production petition UI, and leave time/save control to campaign-test.");
            Console.WriteLine("        ReignLiveTest.exe ui-action --target ambassador|economic-report|spymaster|war-council|family-chambers|training-yard|royal-council|individual-chat|native-conversation --text <action> [--value <selector-or-input>]");
            Console.WriteLine("        Individual Chat actions: set-input, send, toggle-player-portrait, toggle-npc-portrait, close-portrait, show-pregnancy-warning. Native Conversation actions: toggle-player-portrait, toggle-npc-portrait, close-portrait.");
            Console.WriteLine("        show-pregnancy-warning is provider-free and calibration-only. Use the visible Proceed/Pull Out buttons for native acceptance, or control --op confirm|deny for controller corroboration; confirmation now awaits a bounded terminal view-model receipt instead of sleeping.");
            Console.WriteLine("        Direct ui-action calls corroborate VM state only; native click/type and visual evidence remain required for binding, hit-test, and geometry acceptance.");
            Console.WriteLine("        War Council: select-lord, open-raven, set-raven-text, send-raven, cancel-raven, mobilize, pan-map, center-settlement, toggle-councilor, select-councilor, use-player-skills");
            Console.WriteLine("        ReignLiveTest.exe ui-snapshot --target <ui> | ui-status | ui-back | ui-close");
            Console.WriteLine("        ui-status observes live Government focus/input counters in governmentInput and the independent courtPetitionOpen flag without opening a fixture or making a decision.");
            Console.WriteLine("        MCP government hearing_compose sets 1-1200 characters of unsent playerMessage on the exact already-open businessId in an armed disposable Current. Click native Speak to submit; it never votes or creates a case.");
            Console.WriteLine("Send:   --run <id> --text <player text>");
            Console.WriteLine("Run:    ReignLiveTest.exe run scenario.json [--timeout 1800]");
            Console.WriteLine("Lifecycle commands use the official visible launcher and coordinated loopback shutdown; they never start a second server.");
        }
    }
}
