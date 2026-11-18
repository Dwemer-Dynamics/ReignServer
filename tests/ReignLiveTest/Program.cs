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
    }
}
