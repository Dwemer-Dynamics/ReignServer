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
            Console.WriteLine("World:  ReignLiveTest.exe world-test status|snapshot|stop|drain|advance|checkpoint|delete-checkpoint [--days N|--target-day D] [--save name] [--prefix run-prefix] [--no-save] [--observe-government-news (advance requires --no-save; manually acknowledge bulletin)] [--initial-baseline] [--timeout seconds] [--catch-up-timeout seconds] [--save-timeout seconds]");
            Console.WriteLine("        --initial-baseline is reserved for the guarded campaign-test arm path; it accepts only an absent pre-existing daily rollup after exact native baseline observation and confirmation. All live queues and failure counters still must be clean.");
            Console.WriteLine("        drain, advance, and checkpoint emit bounded receipts; use their reportPath references for full live-test evidence.");
            Console.WriteLine("Party agency: ReignLiveTest.exe party-agency --profile contracts|status|preflight [--campaign id] [--no-wait]");
            Console.WriteLine("              Mutating and natural-language release cases require the guarded reign_start_party_agency_test MCP tool and an armed campaign-test Current save.");
            Console.WriteLine("              Positive invitations provide purpose, risk, NPC relevance, term or review, free choice, and an explicit request for agreement; conditional interest is not consent.");
            Console.WriteLine("Common: --campaign <id> --run <id> --mode individual_chat|party_chat|social_event|wilderness_event|social_balance --json|--ndjson");
            Console.WriteLine("Targets: [--lords-only|--non-lords-only] [--minimum-age N] [--min-clan-tier N] [--max-clan-tier N] [--max-honor -2..2] [--limit N]");
            Console.WriteLine("Open:   --target <name-or-id> (repeat for groups) [--visible] [--full-effects]");
            Console.WriteLine("UI:     ReignLiveTest.exe ui-open --target action-builder|party-banter|court|court-petition|castle-layout|castle-chat|ambassador|economic-report|clan-accords|government|family-chambers|training-yard|royal-council|spymaster|war-council|correspondence|individual-chat|party-chat|tavern-house|social-event|wilderness-event|diplomacy-announcement|notable-generation|player-approach|portrait-refresh|memories-book|native-game-menu|native-encyclopedia-hero|native-clan-members|native-marriage-offer|native-heir-selection|native-skill-grid-item|native-character-developer|native-crafting-hero|native-crafting|native-education|native-recruit-volunteer|native-game-menu-party|native-conversation|native-quests [--timeout 120]");
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
            Console.WriteLine("        Government scrollbar observations include actual handle identity/ownership, pressed state, range/value and up to 32 held/release samples; they never change widget properties or inject input.");
            Console.WriteLine("        MCP government hearing_compose sets 1-1200 characters of unsent playerMessage on the exact already-open businessId in an armed disposable Current. Click native Speak to submit; it never votes or creates a case.");
            Console.WriteLine("Send:   --run <id> --text <player text>");
            Console.WriteLine("Run:    ReignLiveTest.exe run scenario.json [--timeout 1800]");
            Console.WriteLine("Lifecycle commands use the official visible launcher and coordinated loopback shutdown; they never start a second server.");
        }
    }
}
